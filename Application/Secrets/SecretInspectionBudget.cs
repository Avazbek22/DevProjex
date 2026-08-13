using System.Diagnostics;

namespace DevProjex.Application.Secrets;

public static class SecretInspectionLimits
{
	// The reviewed DevProjex corpus stays below 100 findings per file. 4,096 leaves two orders
	// of magnitude of headroom while bounding per-file match metadata.
	public const int MaximumFindingsPerFile = 4_096;
	// A 16x per-file allowance supports large exports without allowing aggregate metadata growth
	// to become proportional to every candidate in an adversarial repository.
	public const int MaximumFindingsPerOutput = 65_536;
	// Persistent marks are user-authored and normally number in single digits. This bound also
	// caps profile parsing, matcher construction, and retained HMAC digests.
	public const int MaximumPersistentMarksPerProject = 4_096;
	// Distinct lengths multiply boundary probes; 256 permits heterogeneous credentials while
	// preventing a corrupted profile from turning one content scan into thousands of hash passes.
	public const int MaximumDistinctPersistentMarkLengths = 256;
	// Keys are display metadata, not matching input. Longer values add no useful identity signal.
	public const int MaximumPersistentMarkKeyLength = 256;
	// Fifty million cheap boundary-length probes bound CPU on the 16 MiB scan ceiling while
	// remaining far above measured project workloads.
	public const long MaximumPersistentMatcherWorkUnits = 50_000_000;
	// Provider regexes also have individual timeouts; this outer deadline caps their cumulative
	// work plus structured and manual matching for one untrusted file.
	public static readonly TimeSpan MaximumDetectorTimePerFile = TimeSpan.FromSeconds(5);
	// Regex patterns and the pinned TOML catalog are trusted application data. A separate ceiling
	// prevents a cold candidate set from inheriting the untrusted file deadline without allowing
	// initialization failures to occupy a worker indefinitely.
	public static readonly TimeSpan MaximumRuleInitializationTimePerFile = TimeSpan.FromSeconds(30);
}

/// <summary>Allocation-free mutable budget owned by one file inspection.</summary>
public sealed class SecretFileInspectionBudget
{
	private readonly long _startedTimestamp;
	private readonly TimeSpan _maximumDuration;
	private readonly TimeSpan _maximumRuleInitializationDuration;
	private readonly CancellationToken _lifetimeToken;
	private int _findingCount;
	private long _matcherWorkUnits;
	private long _excludedInitializationTimestampTicks;
	private long _ruleInitializationTimestampTicks;

	public SecretFileInspectionBudget()
		: this(SecretInspectionLimits.MaximumDetectorTimePerFile, CancellationToken.None)
	{
	}

	internal SecretFileInspectionBudget(
		TimeSpan maximumDuration,
		CancellationToken lifetimeToken = default,
		TimeSpan? maximumRuleInitializationDuration = null)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(maximumDuration, TimeSpan.Zero);
		if (maximumRuleInitializationDuration is { } initializationDuration)
			ArgumentOutOfRangeException.ThrowIfLessThan(initializationDuration, TimeSpan.Zero);
		_startedTimestamp = Stopwatch.GetTimestamp();
		_maximumDuration = maximumDuration;
		_maximumRuleInitializationDuration = maximumRuleInitializationDuration ??
		                                     SecretInspectionLimits.MaximumRuleInitializationTimePerFile;
		_lifetimeToken = lifetimeToken;
	}

	public void Checkpoint(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		_lifetimeToken.ThrowIfCancellationRequested();
		var activeTimestampTicks = Math.Max(
			0,
			Stopwatch.GetTimestamp() - _startedTimestamp -
			Volatile.Read(ref _excludedInitializationTimestampTicks));
		if (Stopwatch.GetElapsedTime(0, activeTimestampTicks) > _maximumDuration)
			throw SecretInspectionBudgetExceededException.DetectorDeadline();
	}

	/// <summary>
	/// Accounts trusted lazy rule initialization separately from untrusted content matching.
	/// Detector adapters must wrap only configuration or expression construction, never matching.
	/// </summary>
	public T RunRuleInitialization<T>(Func<T> initialize)
	{
		ArgumentNullException.ThrowIfNull(initialize);
		var started = Stopwatch.GetTimestamp();
		var completed = false;
		try
		{
			var result = initialize();
			completed = true;
			return result;
		}
		finally
		{
			var elapsed = Math.Max(0, Stopwatch.GetTimestamp() - started);
			Interlocked.Add(ref _excludedInitializationTimestampTicks, elapsed);
			var total = Interlocked.Add(ref _ruleInitializationTimestampTicks, elapsed);
			if (completed && Stopwatch.GetElapsedTime(0, total) > _maximumRuleInitializationDuration)
				throw SecretInspectionBudgetExceededException.RuleInitializationDeadline();
		}
	}

	/// <inheritdoc cref="RunRuleInitialization{T}(Func{T})" />
	public void RunRuleInitialization(Action initialize) =>
		RunRuleInitialization(() =>
		{
			initialize();
			return true;
		});

	public void RegisterFinding(CancellationToken cancellationToken)
	{
		Checkpoint(cancellationToken);
		if (!TryAddWithinLimit(ref _findingCount, 1, SecretInspectionLimits.MaximumFindingsPerFile))
			throw SecretInspectionBudgetExceededException.FindingsPerFile();
	}

	public void RegisterFindings(int count, CancellationToken cancellationToken)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(count);
		Checkpoint(cancellationToken);
		if (count == 0)
			return;
		if (!TryAddWithinLimit(
			    ref _findingCount,
			    count,
			    SecretInspectionLimits.MaximumFindingsPerFile))
			throw SecretInspectionBudgetExceededException.FindingsPerFile();
	}

	public void RegisterMatcherWork(long workUnits, CancellationToken cancellationToken)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(workUnits);
		Checkpoint(cancellationToken);
		if (!TryAddWithinLimit(
			    ref _matcherWorkUnits,
			    workUnits,
			    SecretInspectionLimits.MaximumPersistentMatcherWorkUnits))
		{
			throw SecretInspectionBudgetExceededException.MatcherWork();
		}
	}

	private static bool TryAddWithinLimit(ref int value, int increment, int maximum)
	{
		while (true)
		{
			var current = Volatile.Read(ref value);
			if (increment > maximum - current)
				return false;
			if (Interlocked.CompareExchange(ref value, current + increment, current) == current)
				return true;
		}
	}

	private static bool TryAddWithinLimit(ref long value, long increment, long maximum)
	{
		while (true)
		{
			var current = Volatile.Read(ref value);
			if (increment > maximum - current)
				return false;
			if (Interlocked.CompareExchange(ref value, current + increment, current) == current)
				return true;
		}
	}
}

internal sealed class SecretOutputInspectionBudget
{
	private int _findingCount;

	public void RegisterFindings(int count)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(count);
		if (count > SecretInspectionLimits.MaximumFindingsPerFile)
			throw SecretInspectionBudgetExceededException.FindingsPerFile();
		while (true)
		{
			var current = Volatile.Read(ref _findingCount);
			if (count > SecretInspectionLimits.MaximumFindingsPerOutput - current)
				throw SecretInspectionBudgetExceededException.FindingsPerOutput();
			if (Interlocked.CompareExchange(ref _findingCount, current + count, current) == current)
				return;
		}
	}
}

public sealed class SecretInspectionBudgetExceededException(string limitName) :
	SecretDetectionException($"Secret inspection exceeded the '{limitName}' safety limit.")
{
	public string LimitName { get; } = limitName;

	internal static SecretInspectionBudgetExceededException FindingsPerFile() =>
		new(nameof(SecretInspectionLimits.MaximumFindingsPerFile));

	internal static SecretInspectionBudgetExceededException FindingsPerOutput() =>
		new(nameof(SecretInspectionLimits.MaximumFindingsPerOutput));

	internal static SecretInspectionBudgetExceededException DetectorDeadline() =>
		new(nameof(SecretInspectionLimits.MaximumDetectorTimePerFile));

	internal static SecretInspectionBudgetExceededException RuleInitializationDeadline() =>
		new(nameof(SecretInspectionLimits.MaximumRuleInitializationTimePerFile));

	internal static SecretInspectionBudgetExceededException MatcherWork() =>
		new(nameof(SecretInspectionLimits.MaximumPersistentMatcherWorkUnits));

	internal static SecretInspectionBudgetExceededException PersistentMarks() =>
		new(nameof(SecretInspectionLimits.MaximumPersistentMarksPerProject));

	internal static SecretInspectionBudgetExceededException DistinctPersistentMarkLengths() =>
		new(nameof(SecretInspectionLimits.MaximumDistinctPersistentMarkLengths));
}
