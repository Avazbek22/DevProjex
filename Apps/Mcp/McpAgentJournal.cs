using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using DevProjex.Infrastructure.AgentJournal;
using DevProjex.Infrastructure.Secrets;

namespace DevProjex.Mcp;

internal sealed partial class McpAgentJournal : IAsyncDisposable
{
	private const int MaximumQueuedEvents = 1_000;
	private static readonly IReadOnlySet<string> ScalarArguments = new HashSet<string>(StringComparer.Ordinal)
	{
		"path", "mode", "detail", "max_tokens", "direction", "pack_id"
	};
	private static readonly Lazy<IReadOnlyList<ISecretDetector>> ArgumentDetectors = new(
		static () => [new GitleaksSecretDetector(), new PrivateDataDetector()],
		LazyThreadSafetyMode.ExecutionAndPublication);
	private const string RedactedArgumentMarker = "[redacted]";
	private const string HistoryIncompleteNotice = "history-incomplete";
	private static readonly IReadOnlySet<string> SupportedNotices = new HashSet<string>(StringComparer.Ordinal)
	{
		AgentJournalNoticeCodes.OutsideSelection,
		AgentJournalNoticeCodes.StalePack,
		AgentJournalNoticeCodes.SearchPartial,
		AgentJournalNoticeCodes.MatchesOmitted,
		AgentJournalNoticeCodes.BudgetSkipped,
		AgentJournalNoticeCodes.MissingPath,
		AgentJournalNoticeCodes.Unavailable
	};
	private readonly IAgentJournalWriter writer;
	private readonly McpRootRegistry roots;
	private readonly AgentJournalSession session;
	private readonly TimeProvider clock;
	private readonly Channel<JournalOperation> operations = Channel.CreateBounded<JournalOperation>(
		new BoundedChannelOptions(MaximumQueuedEvents)
		{
			SingleReader = true,
			SingleWriter = false,
			AllowSynchronousContinuations = false,
			FullMode = BoundedChannelFullMode.DropOldest
		});
	private readonly CancellationTokenSource shutdown = new();
	private readonly Task pump;
	private readonly AsyncLocal<Invocation?> invocation = new();
	private readonly object totalsSync = new();
	private readonly SemaphoreSlim startGate = new(1, 1);
	private AgentJournalTotals totals = AgentJournalTotals.Empty;
	private long sequence;
	private long lostEvents;
	private int completeSessionRequested;
	private int startState;
	private int startAttempts;
	private int disposed;

	public McpAgentJournal(
		IAgentJournalWriter writer,
		McpRootRegistry roots,
		AgentJournalMode mode,
		AgentJournalToolSet toolSet,
		string serverVersion,
		bool hidePrivateData,
		TimeProvider? timeProvider = null,
		int? pid = null,
		DateTimeOffset? processStartUtc = null)
	{
		this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
		this.roots = roots ?? throw new ArgumentNullException(nameof(roots));
		clock = timeProvider ?? TimeProvider.System;
		var startedUtc = clock.GetUtcNow();
		var processId = pid ?? Environment.ProcessId;
		var processStart = processStartUtc ?? GetCurrentProcessStartUtc();
		session = new AgentJournalSession(
			AgentJournalStore.CreateSessionId(startedUtc, processId),
			startedUtc,
			EndedUtc: null,
			processId,
			processStart,
			ClientName: string.Empty,
			ClientVersion: string.Empty,
			mode,
			roots.ConfiguredRoots.Select((root, index) =>
				new AgentJournalRoot(root, McpRootRegistry.GetProjectName(roots.Roots[index]))).ToArray(),
			toolSet,
			serverVersion,
			hidePrivateData,
			AgentJournalTotals.Empty,
			IsLive: true);
		pump = RunPumpAsync();
	}

	public string SessionId => session.Id;

	public async ValueTask StartAsync(
		string? clientName,
		string? clientVersion,
		CancellationToken cancellationToken)
	{
		if (Volatile.Read(ref startState) == 2)
			return;
		await startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (Volatile.Read(ref startState) == 2 || startAttempts >= 3)
				return;
			startAttempts++;
			Volatile.Write(ref startState, 1);
			var header = session with
			{
				ClientName = clientName ?? string.Empty,
				ClientVersion = clientVersion ?? string.Empty
			};
			await writer.StartSession(header, cancellationToken).ConfigureAwait(false);
			Volatile.Write(ref startState, 2);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			Volatile.Write(ref startState, 0);
			throw;
		}
		catch (Exception exception)
		{
			Volatile.Write(ref startState, 0);
			Trace.TraceWarning("MCP journal session could not be started: {0}", exception.GetType().Name);
		}
		finally
		{
			startGate.Release();
		}
	}

	public IDisposable BeginCall(string tool, CallToolRequestParams request)
	{
		var previous = invocation.Value;
		var current = new Invocation(tool, CaptureArguments(request), Stopwatch.GetTimestamp());
		invocation.Value = current;
		return new InvocationScope(this, previous, current);
	}

	public void RecordPlan(ProjectContextPlan plan, int? revision)
	{
		var current = invocation.Value;
		if (current is null)
			return;
		current.RootIndex = ResolveRootIndex(plan.SourceRoot);
		current.Revision = revision;
	}

	public void RecordStoredContext(string sourceRoot, int? revision)
	{
		var current = invocation.Value;
		if (current is null)
			return;
		current.RootIndex = ResolveRootIndex(sourceRoot);
		current.Revision = revision;
	}

	public void RecordProtection(long secretsMasked, long privateDataMasked)
	{
		var current = invocation.Value;
		if (current is null)
			return;
		current.SecretsMasked += Math.Max(0, secretsMasked);
		current.PrivateDataMasked += Math.Max(0, privateDataMasked);
	}

	public void RecordProtection(int replacementCount, SecretRedactionSnapshot? snapshot)
	{
		var current = invocation.Value;
		if (current is null || snapshot is null || replacementCount <= 0)
			return;
		var secrets = Math.Max(0, snapshot.SecretRedactedCount);
		var privateData = Math.Max(0, snapshot.PrivateDataRedactedCount);
		if (privateData == 0)
			current.SecretsMasked += replacementCount;
		else if (secrets == 0)
			current.PrivateDataMasked += replacementCount;
		else if (replacementCount == secrets + privateData)
		{
			current.SecretsMasked += secrets;
			current.PrivateDataMasked += privateData;
		}
	}

	public void RecordDeliveredPaths(string sourceRoot, IEnumerable<string> paths)
	{
		var current = invocation.Value;
		if (current is null)
			return;
		foreach (var path in paths)
		{
			var relative = Path.IsPathFullyQualified(path)
				? Path.GetRelativePath(sourceRoot, path)
				: path;
			if (TryNormalizeRelativePath(relative) is { } normalized)
				current.DeliveredPaths.Add(normalized);
		}
		current.FilesDelivered = Math.Max(current.FilesDelivered, current.DeliveredPaths.Count);
	}

	public void RecordFileCount(int count)
	{
		var current = invocation.Value;
		if (current is not null)
			current.FilesDelivered = Math.Max(current.FilesDelivered, Math.Max(0, count));
	}

	public void RecordProtection(SecretRedactionSnapshot? snapshot)
	{
		var current = invocation.Value;
		if (current is null || snapshot is null)
			return;
		current.SecretsMasked = Math.Max(current.SecretsMasked, snapshot.SecretRedactedCount);
		current.PrivateDataMasked = Math.Max(current.PrivateDataMasked, snapshot.PrivateDataRedactedCount);
	}

	public void RecordProtection(
		IReadOnlyList<EffectiveRedactionFinding> findings,
		int startLine,
		int endLine)
	{
		var current = invocation.Value;
		if (current is null || findings.Count == 0 || endLine < startLine)
			return;
		var returned = findings.Where(finding => finding.LineNumber >= startLine && finding.LineNumber <= endLine);
		current.SecretsMasked += returned.LongCount(static finding =>
			finding.Category == RedactionFindingCategory.Secrets);
		current.PrivateDataMasked += returned.LongCount(static finding =>
			finding.Category == RedactionFindingCategory.PrivateData);
	}

	public void RecordProtection(
		IReadOnlyList<EffectiveRedactionFinding> findings,
		IReadOnlySet<int> returnedLines)
	{
		var current = invocation.Value;
		if (current is null || findings.Count == 0 || returnedLines.Count == 0)
			return;
		var returned = findings.Where(finding => returnedLines.Contains(finding.LineNumber));
		current.SecretsMasked += returned.LongCount(static finding =>
			finding.Category == RedactionFindingCategory.Secrets);
		current.PrivateDataMasked += returned.LongCount(static finding =>
			finding.Category == RedactionFindingCategory.PrivateData);
	}

	public void RecordNotice(string notice)
	{
		var current = invocation.Value;
		if (current is not null && SupportedNotices.Contains(notice))
			current.Notices.Add(notice);
	}

	public void Complete(CallToolResult result)
	{
		var current = invocation.Value;
		if (current is null || Volatile.Read(ref disposed) != 0)
			return;
		if (Volatile.Read(ref startState) != 2)
		{
			Interlocked.Increment(ref lostEvents);
			return;
		}
		var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(static block => block.Text));
		var trustedText = ExtractTrustedText(text);
		var characters = text.Length;
		var paths = current.DeliveredPaths
			.Order(ProjectTreePathIdentity.CanonicalComparer)
			.ToArray();
		var storedPaths = paths.Take(AgentJournalStore.MaximumDeliveredPaths).ToArray();
		var call = new AgentJournalCall(
			Interlocked.Increment(ref sequence),
			clock.GetUtcNow(),
			current.Tool,
			current.RootIndex,
			current.Arguments,
			current.Revision,
			(long)Stopwatch.GetElapsedTime(current.StartTimestamp).TotalMilliseconds,
			characters,
			CodeCompressionSnapshot.EstimateTokens(characters),
			Math.Max(current.FilesDelivered, paths.Length),
			storedPaths,
			Math.Max(0, paths.Length - storedPaths.Length),
			current.SecretsMasked,
			current.PrivateDataMasked,
			current.Notices.ToArray(),
			result.IsError == true ? CaptureErrorCode(trustedText) : null);
		operations.Writer.TryWrite(new JournalOperation(call));
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref disposed, 1) != 0)
			return;
		if (Volatile.Read(ref startState) == 2)
			Volatile.Write(ref completeSessionRequested, 1);
		operations.Writer.TryComplete();
		try
		{
			await pump.ConfigureAwait(false);
		}
		finally
		{
			shutdown.Cancel();
			shutdown.Dispose();
			startGate.Dispose();
		}
	}

	private async Task RunPumpAsync()
	{
		long lastProcessedSequence = 0;
		try
		{
			await foreach (var operation in operations.Reader.ReadAllAsync(shutdown.Token).ConfigureAwait(false))
			{
				try
				{
					var call = operation.Call;
					var skipped = Math.Max(0, call.Sequence - lastProcessedSequence - 1);
					if (skipped > 0)
						Interlocked.Add(ref lostEvents, skipped);
					lastProcessedSequence = Math.Max(lastProcessedSequence, call.Sequence);
					await WriteCallAsync(call, shutdown.Token).ConfigureAwait(false);
				}
				catch (Exception exception) when (exception is not OperationCanceledException)
				{
					Trace.TraceWarning("MCP journal record could not be written: {0}", exception.GetType().Name);
				}
			}
		}
		catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
		{
		}
		if (Volatile.Read(ref completeSessionRequested) != 0)
		{
			var submitted = Volatile.Read(ref sequence);
			if (submitted > lastProcessedSequence)
				Interlocked.Add(ref lostEvents, submitted - lastProcessedSequence);
			await CompleteSessionAsync(shutdown.Token).ConfigureAwait(false);
		}
	}

	private async ValueTask WriteCallAsync(AgentJournalCall call, CancellationToken cancellationToken)
	{
		var pendingLoss = Interlocked.Exchange(ref lostEvents, 0);
		var persistedCall = pendingLoss > 0
			? AttachIncompleteHistory(call, pendingLoss)
			: call;
		if (await TryWriteWithRetryAsync(
				ct => writer.RecordCall(session.Id, persistedCall, ct),
				cancellationToken).ConfigureAwait(false))
		{
			AddTotals(call);
			return;
		}
		Interlocked.Add(ref lostEvents, pendingLoss + 1);
	}

	private async ValueTask CompleteSessionAsync(CancellationToken cancellationToken)
	{
		var lost = Volatile.Read(ref lostEvents);
		if (lost > 0)
		{
			var marker = CreateIncompleteHistoryMarker(lost);
			await TryWriteWithRetryAsync(
				ct => writer.RecordCall(session.Id, marker, ct),
				cancellationToken).ConfigureAwait(false);
		}
		AgentJournalTotals completed;
		lock (totalsSync)
			completed = totals;
		await TryWriteWithRetryAsync(
			ct => writer.EndSession(session.Id, clock.GetUtcNow(), completed, ct),
			cancellationToken).ConfigureAwait(false);
	}

	private static AgentJournalCall AttachIncompleteHistory(AgentJournalCall call, long lost)
	{
		var arguments = call.Arguments.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
		arguments["lost_events"] = lost.ToString(CultureInfo.InvariantCulture);
		return call with
		{
			Arguments = arguments,
			Notices = call.Notices.Append(HistoryIncompleteNotice).Distinct(StringComparer.Ordinal).ToArray()
		};
	}

	private void AddTotals(AgentJournalCall call)
	{
		lock (totalsSync)
		{
			totals = new AgentJournalTotals(
				totals.Calls + 1,
				SaturatingAdd(totals.ResultCharacters, call.ResultCharacters),
				SaturatingAdd(totals.EstimatedTokens, call.EstimatedTokens),
				SaturatingAdd(totals.FilesDelivered, call.FilesDelivered),
				SaturatingAdd(totals.SecretsMasked, call.SecretsMasked),
				SaturatingAdd(totals.PrivateDataMasked, call.PrivateDataMasked),
				totals.Errors + (call.ErrorCode is null ? 0 : 1));
		}
	}

	private AgentJournalCall CreateIncompleteHistoryMarker(long lost) => new(
		Interlocked.Increment(ref sequence),
		clock.GetUtcNow(),
		"journal",
		RootIndex: null,
		new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["lost_events"] = lost.ToString(CultureInfo.InvariantCulture)
		},
		Revision: null,
		DurationMs: 0,
		ResultCharacters: 0,
		EstimatedTokens: 0,
		FilesDelivered: 0,
		DeliveredPaths: [],
		AdditionalDeliveredPaths: 0,
		SecretsMasked: 0,
		PrivateDataMasked: 0,
		Notices: [HistoryIncompleteNotice],
		ErrorCode: null);

	private static async ValueTask<bool> TryWriteWithRetryAsync(
		Func<CancellationToken, ValueTask> operation,
		CancellationToken cancellationToken)
	{
		const int maximumAttempts = 3;
		for (var attempt = 1; attempt <= maximumAttempts; attempt++)
		{
			try
			{
				await operation(cancellationToken).ConfigureAwait(false);
				return true;
			}
			catch (Exception exception) when (
				exception is IOException or UnauthorizedAccessException &&
				attempt < maximumAttempts)
			{
				await Task.Yield();
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				Trace.TraceWarning("MCP journal record could not be written: {0}", exception.GetType().Name);
				return false;
			}
		}
		return false;
	}

	private int? ResolveRootIndex(string sourceRoot)
	{
		for (var index = 0; index < roots.Roots.Count; index++)
			if (PathComparer.Default.Equals(roots.Roots[index], sourceRoot))
				return index;
		return null;
	}

	private IReadOnlyDictionary<string, string> CaptureArguments(CallToolRequestParams request)
	{
		var source = request.Arguments;
		if (source is null || source.Count == 0)
			return new Dictionary<string, string>(StringComparer.Ordinal);
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var name in ScalarArguments)
		{
			if (!source.TryGetValue(name, out var value) || value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
				continue;
			var captured = value.ToString();
			if (name == "path" && Path.IsPathFullyQualified(captured))
			{
				captured = TryMakeProjectRelative(captured) ?? string.Empty;
				if (captured.Length == 0)
					continue;
			}
			result[name] = SanitizeArgumentValue(captured);
		}
		if (source.TryGetValue("pattern", out var pattern) && pattern.ValueKind == JsonValueKind.String)
		{
			var value = pattern.GetString() ?? string.Empty;
			result["query_present"] = bool.TrueString.ToLowerInvariant();
			result["query_length"] = value.Length.ToString(CultureInfo.InvariantCulture);
		}
		if (source.TryGetValue("symbol", out var symbol) && symbol.ValueKind == JsonValueKind.String)
		{
			var value = symbol.GetString() ?? string.Empty;
			result["symbol_length"] = value.Length.ToString(CultureInfo.InvariantCulture);
			result["symbol_class"] = ClassifySymbol(value);
		}
		if (source.TryGetValue("paths", out var paths))
			result["paths"] = CountValues(paths).ToString(CultureInfo.InvariantCulture);
		if (source.TryGetValue("requests", out var requests) && requests.ValueKind == JsonValueKind.Array)
		{
			result["paths"] = requests.GetArrayLength().ToString(CultureInfo.InvariantCulture);
			var symbols = requests.EnumerateArray().Count(static item =>
				item.ValueKind == JsonValueKind.Object &&
				item.TryGetProperty("symbol", out var symbol) &&
				symbol.ValueKind == JsonValueKind.String);
			if (symbols > 0)
				result["symbols"] = symbols.ToString(CultureInfo.InvariantCulture);
		}
		CopyAlias(source, result, "max_results", "limit");
		CopyAlias(source, result, "max_depth", "depth");
		return result;
	}

	private static string SanitizeArgumentValue(string value)
	{
		if (string.IsNullOrEmpty(value))
			return value;
		try
		{
			foreach (var detector in ArgumentDetectors.Value)
				if (detector.Detect("agent-journal-value.txt", value).Count > 0)
					return RedactedArgumentMarker;
			return value;
		}
		catch (SecretDetectionException)
		{
			return RedactedArgumentMarker;
		}
	}

	private static string ClassifySymbol(string value)
	{
		if (value.Length == 0)
			return "empty";
		if (value.All(static character => char.IsLetterOrDigit(character) || character == '_'))
			return "simple";
		if (value.All(static character =>
				char.IsLetterOrDigit(character) || character is '_' or '.' or ':' or '+' or '`'))
		{
			return "qualified";
		}
		return "expression";
	}

	private string? TryMakeProjectRelative(string path)
	{
		foreach (var root in roots.Roots)
		{
			var relative = Path.GetRelativePath(root, path);
			if (!Path.IsPathFullyQualified(relative) &&
				relative != ".." &&
				!relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
				!relative.StartsWith("../", StringComparison.Ordinal))
			{
				return TryNormalizeRelativePath(relative);
			}
		}
		return null;
	}

	private static void CopyAlias(
		IDictionary<string, JsonElement> source,
		IDictionary<string, string> destination,
		string sourceName,
		string destinationName)
	{
		if (source.TryGetValue(sourceName, out var value) && value.ValueKind == JsonValueKind.Number)
			destination[destinationName] = value.ToString();
	}

	private static int CountValues(JsonElement value) => value.ValueKind switch
	{
		JsonValueKind.Array => value.GetArrayLength(),
		JsonValueKind.String => 1,
		_ => 0
	};

	private static string ExtractTrustedText(string text)
	{
		const string openingPrefix = "<untrusted-data-";
		var cursor = 0;
		var trusted = new StringBuilder(text.Length);
		while (text.IndexOf(openingPrefix, cursor, StringComparison.Ordinal) is var opening && opening >= 0)
		{
			trusted.Append(text, cursor, opening - cursor);
			var openingEnd = text.IndexOf('>', opening + openingPrefix.Length);
			if (openingEnd < 0)
				return trusted.ToString();
			var tag = text[opening..(openingEnd + 1)];
			var closingTag = "</" + tag[1..];
			var closing = text.IndexOf(closingTag, openingEnd + 1, StringComparison.Ordinal);
			if (closing < 0)
				return trusted.ToString();
			cursor = closing + closingTag.Length;
		}
		trusted.Append(text, cursor, text.Length - cursor);
		return trusted.ToString();
	}

	private static string? CaptureErrorCode(string text)
	{
		var match = ErrorCodePattern().Match(text);
		return match.Success ? match.Value : "DPX-MCP-OPERATION-FAILED";
	}

	private static string? TryNormalizeRelativePath(string path)
	{
		var normalized = path.Replace('\\', '/').Trim('/');
		var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
		return segments.Length == 0 || segments.Any(static segment => segment is "." or "..")
			? null
			: string.Join('/', segments);
	}

	private static DateTimeOffset GetCurrentProcessStartUtc()
	{
		using var process = Process.GetCurrentProcess();
		return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
	}

	private static long SaturatingAdd(long left, long right) =>
		left > long.MaxValue - right ? long.MaxValue : left + right;

	[GeneratedRegex("DPX-MCP-[A-Z0-9-]+", RegexOptions.CultureInvariant)]
	private static partial Regex ErrorCodePattern();

	private sealed class Invocation(
		string tool,
		IReadOnlyDictionary<string, string> arguments,
		long startTimestamp)
	{
		public string Tool { get; } = tool;
		public IReadOnlyDictionary<string, string> Arguments { get; } = arguments;
		public long StartTimestamp { get; } = startTimestamp;
		public int? RootIndex { get; set; }
		public int? Revision { get; set; }
		public HashSet<string> DeliveredPaths { get; } = new(ProjectTreePathIdentity.CanonicalComparer);
		public int FilesDelivered { get; set; }
		public long SecretsMasked { get; set; }
		public long PrivateDataMasked { get; set; }
		public HashSet<string> Notices { get; } = new(StringComparer.Ordinal);
	}

	private sealed class InvocationScope(
		McpAgentJournal owner,
		Invocation? previous,
		Invocation current) : IDisposable
	{
		private int disposed;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref disposed, 1) != 0)
				return;
			if (ReferenceEquals(owner.invocation.Value, current))
				owner.invocation.Value = previous;
		}
	}

	private sealed record JournalOperation(AgentJournalCall Call);
}
