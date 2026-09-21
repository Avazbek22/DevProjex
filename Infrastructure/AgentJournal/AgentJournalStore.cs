using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.LiveContext;
using DevProjex.Infrastructure.Persistence;
using DevProjex.Infrastructure.Secrets;

namespace DevProjex.Infrastructure.AgentJournal;

public sealed partial class AgentJournalStore : IAgentJournalWriter, IAgentJournalReader, IDisposable
{
	public const int MaximumDeliveredPaths = 200;
	public const int MaximumArgumentValueCharacters = 4096;
	private const int MaximumLineCharacters = 2 * 1024 * 1024;
	private const int TailBoundaryProbeBytes = 64;
	private static readonly TimeSpan ChangePollInterval = TimeSpan.FromMilliseconds(250);
	private static readonly Lazy<IReadOnlyList<ISecretDetector>> ArgumentDetectors = new(
		static () => [new GitleaksSecretDetector(), new PrivateDataDetector()],
		LazyThreadSafetyMode.ExecutionAndPublication);
	private static readonly IReadOnlySet<string> AllowedArguments = new HashSet<string>(StringComparer.Ordinal)
	{
		"path", "paths", "query_present", "query_length", "mode", "symbols", "symbol_length",
		"symbol_class", "limit", "detail", "max_tokens", "direction", "depth", "pack_id", "lost_events"
	};
	private static readonly IReadOnlySet<string> AllowedNotices = new HashSet<string>(StringComparer.Ordinal)
	{
		AgentJournalNoticeCodes.OutsideSelection,
		AgentJournalNoticeCodes.StalePack,
		AgentJournalNoticeCodes.SearchPartial,
		AgentJournalNoticeCodes.MatchesOmitted,
		AgentJournalNoticeCodes.BudgetSkipped,
		AgentJournalNoticeCodes.MissingPath,
		AgentJournalNoticeCodes.Unavailable,
		"history-recovered",
		"history-incomplete"
	};
	private readonly Func<string> stateRoot;
	private readonly TimeProvider clock;
	private readonly Func<IReadOnlyList<LiveSessionRecord>> activeSessions;
	private readonly ConcurrentDictionary<string, SemaphoreSlim> fileLocks = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, AgentJournalSession> sessionHeaders = new(StringComparer.Ordinal);
	private int disposed;

	internal Action<long>? TailBytesReadObserver { get; set; }

	public AgentJournalStore(
		Func<string>? stateRootProvider = null,
		TimeProvider? timeProvider = null,
		Func<IReadOnlyList<LiveSessionRecord>>? activeSessionProvider = null,
		AgentJournalRetentionPolicy? retention = null)
	{
		stateRoot = stateRootProvider ?? UserDataPathResolver.GetStateRoot;
		clock = timeProvider ?? TimeProvider.System;
		Retention = retention ?? AgentJournalRetentionPolicy.Default;
		activeSessions = activeSessionProvider ??
			(() => new LiveSessionRegistry(stateRoot, clock).ReadActive());
		Sweep();
	}

	public AgentJournalRetentionPolicy Retention { get; }

	public string DirectoryPath => Path.Combine(stateRoot(), "agent-journal");

	public static string CreateSessionId(DateTimeOffset startedUtc, int pid)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(pid, 1);
		return $"{startedUtc.UtcDateTime:yyyyMMdd-HHmmss}-{pid}";
	}

	public async ValueTask StartSession(
		AgentJournalSession session,
		CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
		ValidateSession(session);
		var normalizedSession = NormalizeSession(session);
		sessionHeaders[session.Id] = normalizedSession;
		Sweep();
		Directory.CreateDirectory(DirectoryPath);
		var path = ResolveSessionPath(session.Id);
		var gate = fileLocks.GetOrAdd(session.Id, static _ => new SemaphoreSlim(1, 1));
		await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await WriteLineAsync(
				path,
				new AgentJournalLine("session", Session: normalizedSession),
				FileMode.CreateNew,
				cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			gate.Release();
		}
	}

	public ValueTask RecordCall(
		string sessionId,
		AgentJournalCall call,
		CancellationToken cancellationToken = default) =>
		AppendAsync(
			sessionId,
			new AgentJournalLine("call", Call: NormalizeCall(call)),
			cancellationToken);

	public ValueTask EndSession(
		string sessionId,
		DateTimeOffset endedUtc,
		AgentJournalTotals totals,
		CancellationToken cancellationToken = default) =>
		AppendAsync(
			sessionId,
			new AgentJournalLine(
				"end",
				End: new AgentJournalEnd(endedUtc.ToUniversalTime(), NormalizeTotals(totals))),
			cancellationToken);

	public async ValueTask<IReadOnlyList<AgentJournalSession>> ListSessionsAsync(
		string? projectRoot = null,
		int limit = 200,
		CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
		ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
		Sweep();
		var requestedRoot = TryNormalizeRoot(projectRoot);
		if (projectRoot is not null && requestedRoot is null)
			return [];
		var live = ActiveSessionKeys();
		var sessions = new List<AgentJournalSession>();
		foreach (var path in EnumerateSessionFiles())
		{
			cancellationToken.ThrowIfCancellationRequested();
			var content = await ReadContentAsync(path, cancellationToken).ConfigureAwait(false);
			if (content.Session is null ||
				(requestedRoot is not null && !ContainsRoot(content.Session.Roots, requestedRoot)))
			{
				continue;
			}
			sessions.Add(MaterializeSession(content, live));
		}
		return sessions
			.OrderByDescending(static session => session.StartedUtc)
			.ThenByDescending(static session => session.Id, StringComparer.Ordinal)
			.Take(limit)
			.ToArray();
	}

	public async ValueTask<IReadOnlyList<AgentJournalCall>> ReadCallsAsync(
		string sessionId,
		CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
		Sweep();
		var content = await ReadContentAsync(ResolveSessionPath(sessionId), cancellationToken).ConfigureAwait(false);
		return content.Calls;
	}

	public async ValueTask<AgentJournalReceipt?> ReadReceiptAsync(
		string sessionId,
		CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
		Sweep();
		var content = await ReadContentAsync(ResolveSessionPath(sessionId), cancellationToken).ConfigureAwait(false);
		if (content.Session is null)
			return null;
		var session = MaterializeSession(content, ActiveSessionKeys());
		var multipleRoots = session.Roots.Count > 1;
		var delivered = content.Calls
			.SelectMany(call => call.DeliveredPaths
				.Distinct(ProjectTreePathIdentity.CanonicalComparer)
				.Select(path => new
				{
					call.RootIndex,
					Path = multipleRoots
						? $"root {(call.RootIndex ?? -1) + 1}: {path}"
						: path
				}))
			.GroupBy(static item => (item.RootIndex, item.Path))
			.Select(static group => new AgentJournalDeliveredPath(group.Key.Path, group.LongCount()))
			.OrderBy(static item => item.Path, ProjectTreePathIdentity.CanonicalComparer)
			.ToArray();
		return new AgentJournalReceipt(session, session.Totals, delivered, content.Calls);
	}

	public async IAsyncEnumerable<AgentJournalChange> WatchChangesAsync(
		string sessionId,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
		var path = ResolveSessionPath(sessionId);
		long lastSequence = 0;
		long offset = 0;
		var observedFile = false;
		var missingAfterObservation = false;
		byte[] observedBoundary = [];
		var ended = false;
		while (!ended)
		{
			if (!File.Exists(path))
			{
				missingAfterObservation |= observedFile;
				await Task.Delay(ChangePollInterval, clock, cancellationToken).ConfigureAwait(false);
				continue;
			}
			TailReadResult tail;
			try
			{
				var file = new FileInfo(path);
				var boundaryChanged = observedFile && file.Length >= offset &&
					!await TailBoundaryMatchesAsync(path, offset, observedBoundary, cancellationToken)
						.ConfigureAwait(false);
				var reset = observedFile &&
					(missingAfterObservation || file.Length < offset || boundaryChanged);
				if (reset)
				{
					offset = 0;
					lastSequence = 0;
				}
				observedFile = true;
				missingAfterObservation = false;
				tail = await ReadTailAsync(path, offset, cancellationToken).ConfigureAwait(false);
				offset = tail.Offset;
				observedBoundary = await ReadTailBoundaryAsync(path, offset, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				missingAfterObservation = true;
				await Task.Delay(ChangePollInterval, clock, cancellationToken).ConfigureAwait(false);
				continue;
			}
			foreach (var call in tail.Calls.Where(call => call.Sequence > lastSequence))
			{
				lastSequence = call.Sequence;
				yield return new AgentJournalChange(sessionId, AgentJournalChangeKind.CallAppended, call.Sequence);
			}
			if (tail.Ended)
			{
				ended = true;
				yield return new AgentJournalChange(sessionId, AgentJournalChangeKind.SessionEnded);
				continue;
			}
			await Task.Delay(ChangePollInterval, clock, cancellationToken).ConfigureAwait(false);
		}
	}

	private async ValueTask<bool> TailBoundaryMatchesAsync(
		string path,
		long offset,
		byte[] expected,
		CancellationToken cancellationToken)
	{
		var actual = await ReadTailBoundaryAsync(path, offset, cancellationToken).ConfigureAwait(false);
		return actual.AsSpan().SequenceEqual(expected);
	}

	private async ValueTask<byte[]> ReadTailBoundaryAsync(
		string path,
		long offset,
		CancellationToken cancellationToken)
	{
		var length = checked((int)Math.Min(offset, TailBoundaryProbeBytes));
		if (length == 0)
			return [];
		await using var stream = new FileStream(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.ReadWrite | FileShare.Delete,
			bufferSize: TailBoundaryProbeBytes,
			FileOptions.Asynchronous | FileOptions.RandomAccess);
		stream.Position = offset - length;
		var boundary = new byte[length];
		var totalRead = 0;
		while (totalRead < boundary.Length)
		{
			var read = await stream.ReadAsync(boundary.AsMemory(totalRead), cancellationToken).ConfigureAwait(false);
			if (read == 0)
				return boundary[..totalRead];
			totalRead += read;
			TailBytesReadObserver?.Invoke(read);
		}
		return boundary;
	}

	private async ValueTask<TailReadResult> ReadTailAsync(
		string path,
		long offset,
		CancellationToken cancellationToken)
	{
		await using var stream = new FileStream(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.ReadWrite | FileShare.Delete,
			bufferSize: 4096,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		if (stream.Length <= offset)
			return new TailReadResult(offset, [], Ended: false);
		stream.Position = offset;
		var calls = new List<AgentJournalCall>();
		var buffer = new byte[8192];
		using var pending = new MemoryStream();
		var lineTooLong = false;
		var completeOffset = offset;
		var absoluteOffset = offset;
		var ended = false;
		while (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) is var read && read > 0)
		{
			TailBytesReadObserver?.Invoke(read);
			for (var index = 0; index < read; index++)
			{
				var value = buffer[index];
				absoluteOffset++;
				if (value != (byte)'\n')
				{
					if (!lineTooLong)
					{
						pending.WriteByte(value);
						lineTooLong = pending.Length > MaximumLineCharacters;
					}
					continue;
				}
				completeOffset = absoluteOffset;
				if (!lineTooLong && pending.Length > 0 && TryDeserializeLine(pending, out var record))
				{
					if (record.Call is not null)
						calls.Add(record.Call);
					ended |= record.Type == "end" && record.End is not null;
				}
				pending.SetLength(0);
				lineTooLong = false;
			}
		}
		return new TailReadResult(completeOffset, calls, ended);
	}

	private static bool TryDeserializeLine(MemoryStream line, out AgentJournalLine record)
	{
		var length = checked((int)line.Length);
		var span = line.GetBuffer().AsSpan(0, length);
		if (!span.IsEmpty && span[^1] == (byte)'\r')
			span = span[..^1];
		try
		{
			record = JsonSerializer.Deserialize(
				span,
				AgentJournalJsonSerializerContext.Default.AgentJournalLine)!;
			return record is not null;
		}
		catch (JsonException)
		{
			record = null!;
			return false;
		}
	}

	public async ValueTask<int> ClearAsync(
		string? projectRoot = null,
		CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
		var requestedRoot = TryNormalizeRoot(projectRoot);
		if (projectRoot is not null && requestedRoot is null)
			return 0;
		var live = ActiveSessionKeys();
		var removed = 0;
		foreach (var path in EnumerateSessionFiles())
		{
			cancellationToken.ThrowIfCancellationRequested();
			var content = await ReadContentAsync(path, cancellationToken).ConfigureAwait(false);
			if (content.Session is not null && IsActive(content.Session, live))
				continue;
			if (requestedRoot is not null)
			{
				if (content.Session is null || !ContainsRoot(content.Session.Roots, requestedRoot))
					continue;
			}
			try
			{
				File.Delete(path);
				removed++;
			}
			catch (FileNotFoundException)
			{
			}
		}
		return removed;
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref disposed, 1) != 0)
			return;
		foreach (var gate in fileLocks.Values)
			gate.Dispose();
		fileLocks.Clear();
		sessionHeaders.Clear();
	}

	private async ValueTask AppendAsync(
		string sessionId,
		AgentJournalLine line,
		CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
		var path = ResolveSessionPath(sessionId);
		var gate = fileLocks.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
		await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var recoveredTail = false;
			if (!File.Exists(path))
			{
				if (!sessionHeaders.TryGetValue(sessionId, out var header))
					throw new FileNotFoundException("The journal session does not exist.", path);
				Directory.CreateDirectory(DirectoryPath);
				await WriteLineAsync(
					path,
					new AgentJournalLine("session", Session: header),
					FileMode.CreateNew,
					cancellationToken).ConfigureAwait(false);
				if (line.Call is { } recoveredCall)
				{
					line = line with
					{
						Call = recoveredCall with
						{
							Notices = recoveredCall.Notices.Append("history-recovered").ToArray()
						}
					};
				}
			}
			else
			{
				recoveredTail = await RepairIncompleteTailAsync(path, cancellationToken).ConfigureAwait(false);
				if (new FileInfo(path).Length == 0)
				{
					if (!sessionHeaders.TryGetValue(sessionId, out var header))
						throw new InvalidDataException("The journal session header is incomplete.");
					await WriteLineAsync(
						path,
						new AgentJournalLine("session", Session: header),
						FileMode.Create,
						cancellationToken).ConfigureAwait(false);
				}
			}
			if (recoveredTail && line.Call is { } recoveredTailCall)
			{
				line = line with
				{
					Call = recoveredTailCall with
					{
						Notices = recoveredTailCall.Notices.Append("history-recovered").ToArray()
					}
				};
			}
			await WriteLineAsync(path, line, FileMode.Append, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			gate.Release();
		}
	}

	private static async ValueTask<bool> RepairIncompleteTailAsync(
		string path,
		CancellationToken cancellationToken)
	{
		await using var stream = new FileStream(
			path,
			FileMode.Open,
			FileAccess.ReadWrite,
			FileShare.Read,
			bufferSize: 4096,
			FileOptions.Asynchronous | FileOptions.RandomAccess);
		if (stream.Length == 0)
			return true;

		stream.Position = stream.Length - 1;
		if (stream.ReadByte() == (byte)'\n')
			return false;

		var buffer = new byte[4096];
		var cursor = stream.Length;
		long completeLength = 0;
		while (cursor > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var count = checked((int)Math.Min(buffer.Length, cursor));
			var start = cursor - count;
			stream.Position = start;
			await stream.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
			var newline = buffer.AsSpan(0, count).LastIndexOf((byte)'\n');
			if (newline >= 0)
			{
				completeLength = start + newline + 1;
				break;
			}
			cursor = start;
		}

		stream.SetLength(completeLength);
		await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
		return true;
	}

	private static async ValueTask WriteLineAsync(
		string path,
		AgentJournalLine line,
		FileMode mode,
		CancellationToken cancellationToken)
	{
		var bytes = JsonSerializer.SerializeToUtf8Bytes(
			line,
			AgentJournalJsonSerializerContext.Default.AgentJournalLine);
		if (bytes.Length > MaximumLineCharacters)
			throw new InvalidDataException("The journal record exceeds the supported line size.");
		var framed = new byte[bytes.Length + 1];
		bytes.CopyTo(framed, 0);
		framed[^1] = (byte)'\n';
		await using var stream = new FileStream(
			path,
			mode,
			FileAccess.Write,
			FileShare.Read,
			bufferSize: 4096,
			FileOptions.Asynchronous | FileOptions.WriteThrough);
		await stream.WriteAsync(framed, cancellationToken).ConfigureAwait(false);
		await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask<JournalContent> ReadContentAsync(
		string path,
		CancellationToken cancellationToken)
	{
		if (!File.Exists(path))
			return JournalContent.Empty;
		var content = new JournalContent();
		await foreach (var line in ReadCompleteLinesAsync(path, cancellationToken).ConfigureAwait(false))
		{
			if (line.Length is 0 or > MaximumLineCharacters)
				continue;
			AgentJournalLine? record;
			try
			{
				record = JsonSerializer.Deserialize(
					line,
					AgentJournalJsonSerializerContext.Default.AgentJournalLine);
			}
			catch (JsonException)
			{
				continue;
			}
			switch (record?.Type)
			{
				case "session" when content.Session is null && record.Session is not null:
					content.Session = record.Session;
					break;
				case "call" when record.Call is not null:
					content.Calls.Add(record.Call);
					break;
				case "end" when record.End is not null:
					content.End = record.End;
					break;
			}
		}
		content.Calls.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));
		return content;
	}

	private static async IAsyncEnumerable<string> ReadCompleteLinesAsync(
		string path,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var stream = new FileStream(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.ReadWrite | FileShare.Delete,
			bufferSize: 4096,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		if (stream.Length == 0)
			yield break;
		stream.Seek(-1, SeekOrigin.End);
		var hasCompleteTail = stream.ReadByte() == (byte)'\n';
		stream.Position = 0;
		using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		string? pending = null;
		while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
		{
			if (pending is not null)
				yield return pending;
			pending = line;
		}
		if (hasCompleteTail && pending is not null)
			yield return pending;
	}

	private AgentJournalSession MaterializeSession(
		JournalContent content,
		IReadOnlySet<(int Pid, long StartTicks)> live)
	{
		var header = content.Session!;
		var totals = content.End?.Totals ?? SumTotals(content.Calls);
		return header with
		{
			EndedUtc = content.End?.EndedUtc,
			Totals = totals,
			IsLive = content.End is null && live.Contains((header.Pid, header.ProcessStartUtc.UtcTicks))
		};
	}

	private IReadOnlySet<(int Pid, long StartTicks)> ActiveSessionKeys()
	{
		try
		{
			return activeSessions()
				.Select(static record => (record.Pid, record.ProcessStartUtc.UtcTicks))
				.ToHashSet();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Trace.TraceWarning("Journal live-session state could not be read: {0}", exception.GetType().Name);
			return new HashSet<(int, long)>();
		}
	}

	private void Sweep()
	{
		if (Retention.MaximumAge <= TimeSpan.Zero || Retention.MaximumSessions <= 0)
			throw new InvalidOperationException("Journal retention must keep a positive age and session count.");
		var cutoff = clock.GetUtcNow() - Retention.MaximumAge;
		var live = ActiveSessionKeys();
		var files = EnumerateSessionFiles()
			.Select(path => new FileInfo(path))
			.OrderByDescending(static file => file.LastWriteTimeUtc)
			.ThenByDescending(static file => file.Name, StringComparer.Ordinal)
			.ToArray();
		for (var index = 0; index < files.Length; index++)
		{
			if (index < Retention.MaximumSessions && files[index].LastWriteTimeUtc >= cutoff.UtcDateTime)
				continue;
			if (!TryReadSessionHeader(files[index].FullName, out var session) ||
				session is not null && IsActive(session, live))
			{
				continue;
			}
			try
			{
				files[index].Delete();
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
			}
		}
	}

	private static bool TryReadSessionHeader(string path, out AgentJournalSession? session)
	{
		session = null;
		try
		{
			using var stream = new FileStream(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete,
				bufferSize: 4096,
				FileOptions.SequentialScan);
			using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
			var line = reader.ReadLine();
			if (line is null || line.Length > MaximumLineCharacters)
				return true;
			try
			{
				var record = JsonSerializer.Deserialize(
					line,
					AgentJournalJsonSerializerContext.Default.AgentJournalLine);
				if (record?.Type == "session")
					session = record.Session;
			}
			catch (JsonException)
			{
			}
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	private string[] EnumerateSessionFiles()
	{
		try
		{
			return Directory.Exists(DirectoryPath)
				? Directory.GetFiles(DirectoryPath, "*.jsonl", SearchOption.TopDirectoryOnly)
				: [];
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}

	private string ResolveSessionPath(string sessionId)
	{
		if (!SessionIdPattern().IsMatch(sessionId))
			throw new ArgumentException("The journal session ID is invalid.", nameof(sessionId));
		return Path.Combine(DirectoryPath, sessionId + ".jsonl");
	}

	private static AgentJournalSession NormalizeSession(AgentJournalSession session) => session with
	{
		StartedUtc = session.StartedUtc.ToUniversalTime(),
		EndedUtc = null,
		ProcessStartUtc = session.ProcessStartUtc.ToUniversalTime(),
		ClientName = BoundSingleLine(session.ClientName, 256),
		ClientVersion = BoundSingleLine(session.ClientVersion, 128),
		Roots = session.Roots
			.Take(256)
			.Select(static root => new AgentJournalRoot(
				Path.GetFullPath(root.ConfiguredPath),
				BoundSingleLine(root.Name, 256)))
			.ToArray(),
		ServerVersion = BoundSingleLine(session.ServerVersion, 128),
		Totals = AgentJournalTotals.Empty,
		IsLive = false
	};

	private static AgentJournalCall NormalizeCall(AgentJournalCall call)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(call.Sequence, 1);
		var arguments = call.Arguments
			.Where(static pair => AllowedArguments.Contains(pair.Key))
			.ToDictionary(
				static pair => pair.Key,
				static pair => SanitizeArgumentValue(BoundSingleLine(pair.Value, MaximumArgumentValueCharacters)),
				StringComparer.Ordinal);
		var paths = call.DeliveredPaths
			.Select(TryNormalizeRelativePath)
			.Where(static path => path is not null)
			.Cast<string>()
			.Distinct(ProjectTreePathIdentity.CanonicalComparer)
			.ToArray();
		var storedPaths = paths.Take(MaximumDeliveredPaths).ToArray();
		return call with
		{
			Utc = call.Utc.ToUniversalTime(),
			Tool = BoundSingleLine(call.Tool, 128),
			Arguments = arguments,
			DurationMs = Math.Max(0, call.DurationMs),
			ResultCharacters = Math.Max(0, call.ResultCharacters),
			EstimatedTokens = Math.Max(0, call.EstimatedTokens),
			FilesDelivered = Math.Max(0, call.FilesDelivered),
			DeliveredPaths = storedPaths,
			AdditionalDeliveredPaths = Math.Max(0, call.AdditionalDeliveredPaths + paths.Length - storedPaths.Length),
			SecretsMasked = Math.Max(0, call.SecretsMasked),
			PrivateDataMasked = Math.Max(0, call.PrivateDataMasked),
			Notices = call.Notices.Where(static notice => AllowedNotices.Contains(notice)).Distinct(StringComparer.Ordinal).ToArray(),
			ErrorCode = call.ErrorCode is null ? null : BoundSingleLine(call.ErrorCode, 128)
		};
	}

	private static void ValidateSession(AgentJournalSession session)
	{
		ArgumentNullException.ThrowIfNull(session);
		if (!SessionIdPattern().IsMatch(session.Id))
			throw new ArgumentException("The journal session ID is invalid.", nameof(session));
		ArgumentOutOfRangeException.ThrowIfLessThan(session.Pid, 1);
		if (session.Roots.Count is 0 or > 256)
			throw new ArgumentException("A journal session must have between one and 256 roots.", nameof(session));
		if (session.Roots.Any(static root => !Path.IsPathFullyQualified(root.ConfiguredPath)))
			throw new ArgumentException("Journal roots must be fully qualified paths.", nameof(session));
	}

	private static AgentJournalTotals NormalizeTotals(AgentJournalTotals totals) => new(
		Math.Max(0, totals.Calls),
		Math.Max(0, totals.ResultCharacters),
		Math.Max(0, totals.EstimatedTokens),
		Math.Max(0, totals.FilesDelivered),
		Math.Max(0, totals.SecretsMasked),
		Math.Max(0, totals.PrivateDataMasked),
		Math.Max(0, totals.Errors));

	private static string SanitizeArgumentValue(string value)
	{
		if (string.IsNullOrEmpty(value))
			return value;
		try
		{
			foreach (var detector in ArgumentDetectors.Value)
				if (detector.Detect("agent-journal-value.txt", value).Count > 0)
					return "[redacted]";
			return value;
		}
		catch (SecretDetectionException)
		{
			return "[redacted]";
		}
	}

	private static AgentJournalTotals SumTotals(IEnumerable<AgentJournalCall> calls)
	{
		long count = 0;
		long characters = 0;
		long tokens = 0;
		long files = 0;
		long secrets = 0;
		long privateData = 0;
		long errors = 0;
		foreach (var call in calls)
		{
			if (call.Notices.Contains("history-incomplete", StringComparer.Ordinal))
				continue;
			count++;
			characters = SaturatingAdd(characters, call.ResultCharacters);
			tokens = SaturatingAdd(tokens, call.EstimatedTokens);
			files = SaturatingAdd(files, call.FilesDelivered);
			secrets = SaturatingAdd(secrets, call.SecretsMasked);
			privateData = SaturatingAdd(privateData, call.PrivateDataMasked);
			if (call.ErrorCode is not null)
				errors = SaturatingAdd(errors, 1);
		}
		return new AgentJournalTotals(count, characters, tokens, files, secrets, privateData, errors);
	}

	private static long SaturatingAdd(long left, long right) =>
		left > long.MaxValue - right ? long.MaxValue : left + right;

	private static bool ContainsRoot(IReadOnlyList<AgentJournalRoot> roots, string requestedRoot) =>
		roots.Any(root => PathComparer.Default.Equals(TryNormalizeRoot(root.ConfiguredPath), requestedRoot));

	private static bool IsActive(
		AgentJournalSession session,
		IReadOnlySet<(int Pid, long StartTicks)> live) =>
		live.Contains((session.Pid, session.ProcessStartUtc.UtcTicks));

	private static string? TryNormalizeRoot(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return null;
		try
		{
			return PathUtility.Normalize(path);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
		{
			return null;
		}
	}

	private static string? TryNormalizeRelativePath(string? path)
	{
		if (string.IsNullOrWhiteSpace(path) || Path.IsPathFullyQualified(path))
			return null;
		var normalized = path.Replace('\\', '/').TrimStart('/');
		var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
		return segments.Length == 0 || segments.Any(static segment => segment is "." or "..")
			? null
			: string.Join('/', segments);
	}

	private static string BoundSingleLine(string value, int maximumCharacters)
	{
		value ??= string.Empty;
		var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
		return singleLine.Length <= maximumCharacters ? singleLine : singleLine[..maximumCharacters];
	}

	[GeneratedRegex("^[0-9]{8}-[0-9]{6}-[1-9][0-9]*$", RegexOptions.CultureInvariant)]
	private static partial Regex SessionIdPattern();

	private sealed class JournalContent
	{
		public static JournalContent Empty { get; } = new();
		public AgentJournalSession? Session { get; set; }
		public List<AgentJournalCall> Calls { get; } = [];
		public AgentJournalEnd? End { get; set; }
	}

	private sealed record TailReadResult(
		long Offset,
		IReadOnlyList<AgentJournalCall> Calls,
		bool Ended);
}
