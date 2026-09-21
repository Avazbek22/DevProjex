using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using DevProjex.Infrastructure.AgentJournal;

namespace DevProjex.Mcp;

internal sealed partial class McpAgentJournal : IAsyncDisposable
{
	private static readonly IReadOnlySet<string> ScalarArguments = new HashSet<string>(StringComparer.Ordinal)
	{
		"path", "pattern", "mode", "symbol", "detail", "max_tokens", "direction", "pack_id"
	};
	private readonly IAgentJournalWriter writer;
	private readonly McpRootRegistry roots;
	private readonly AgentJournalSession session;
	private readonly TimeProvider clock;
	private readonly Channel<Func<CancellationToken, ValueTask>> operations = Channel.CreateUnbounded<Func<CancellationToken, ValueTask>>(
		new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
	private readonly CancellationTokenSource shutdown = new();
	private readonly Task pump;
	private readonly AsyncLocal<Invocation?> invocation = new();
	private readonly object totalsSync = new();
	private AgentJournalTotals totals = AgentJournalTotals.Empty;
	private long sequence;
	private int startState;
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
		if (Interlocked.CompareExchange(ref startState, 1, 0) != 0)
			return;
		var header = session with
		{
			ClientName = clientName ?? string.Empty,
			ClientVersion = clientVersion ?? string.Empty
		};
		try
		{
			await writer.StartSession(header, cancellationToken).ConfigureAwait(false);
			Volatile.Write(ref startState, 2);
		}
		catch (Exception exception)
		{
			Volatile.Write(ref startState, -1);
			Trace.TraceWarning("MCP journal session could not be started: {0}", exception.GetType().Name);
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

	public void Complete(CallToolResult result)
	{
		var current = invocation.Value;
		if (current is null || Volatile.Read(ref startState) != 2 || Volatile.Read(ref disposed) != 0)
			return;
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
			CaptureNotices(trustedText),
			result.IsError == true ? CaptureErrorCode(trustedText) : null);
		AddTotals(call);
		operations.Writer.TryWrite(token => writer.RecordCall(session.Id, call, token));
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref disposed, 1) != 0)
			return;
		if (Volatile.Read(ref startState) == 2)
		{
			AgentJournalTotals completed;
			lock (totalsSync)
				completed = totals;
			operations.Writer.TryWrite(token => writer.EndSession(session.Id, clock.GetUtcNow(), completed, token));
		}
		operations.Writer.TryComplete();
		try
		{
			await pump.ConfigureAwait(false);
		}
		finally
		{
			shutdown.Cancel();
			shutdown.Dispose();
		}
	}

	private async Task RunPumpAsync()
	{
		try
		{
			await foreach (var operation in operations.Reader.ReadAllAsync(shutdown.Token).ConfigureAwait(false))
			{
				try
				{
					await operation(shutdown.Token).ConfigureAwait(false);
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
			result[name] = captured;
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

	private static IReadOnlyList<string> CaptureNotices(string text)
	{
		var notices = new List<string>();
		Add(AgentJournalNoticeCodes.OutsideSelection, text.Contains("outside the current window selection", StringComparison.Ordinal));
		Add(AgentJournalNoticeCodes.StalePack, text.Contains("built at revision", StringComparison.Ordinal));
		Add(AgentJournalNoticeCodes.SearchPartial, text.Contains("Results are partial", StringComparison.Ordinal));
		Add(AgentJournalNoticeCodes.MatchesOmitted, text.Contains("additional observed matches not shown", StringComparison.Ordinal));
		Add(AgentJournalNoticeCodes.BudgetSkipped, text.Contains("budget", StringComparison.OrdinalIgnoreCase) && text.Contains("skipped", StringComparison.OrdinalIgnoreCase));
		Add(AgentJournalNoticeCodes.MissingPath, text.Contains("DPX-SELECTION-PATH-MISSING", StringComparison.Ordinal));
		Add(AgentJournalNoticeCodes.Unavailable, text.Contains("[Batch unavailable]", StringComparison.Ordinal));
		return notices;

		void Add(string code, bool condition)
		{
			if (condition)
				notices.Add(code);
		}
	}

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
}
