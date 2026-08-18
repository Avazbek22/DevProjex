using System.Diagnostics;
using System.Reflection;
using DevProjex.Application.Secrets;

const int FindingCount = 4_096;

var resolver = typeof(SecretRedactionSession).Assembly
	.GetType("DevProjex.Application.Secrets.SecretRedactionScope", throwOnError: true)!
	.GetMethod("ResolveSegmentedFindings", BindingFlags.Static | BindingFlags.NonPublic)!;

Console.WriteLine("Resolver benchmark (Release, 4,096 findings)");
Console.WriteLine("Workload\tLegacy ms / MiB\tSegmented ms / MiB\tSegments");
RunResolverWorkload("nested marks", CreateNestedMarks(FindingCount));
RunResolverWorkload("partial chain", CreatePartialChain(FindingCount));
RunResolverWorkload("exact stacks", CreateExactStacks(FindingCount));

Console.WriteLine();
Console.WriteLine("Production cache and decisions (2,048 exact Secret/PrivateData stacks)");
RunProductionPipelineBenchmark(2_048);
return;

void RunResolverWorkload(string name, IReadOnlyList<DetectedSecret> findings)
{
	LegacyResolve(findings);
	resolver.Invoke(null, [findings]);
	var legacy = Measure(() => LegacyResolve(findings));
	var segmented = Measure(() => resolver.Invoke(null, [findings])!);
	var segments = (System.Collections.ICollection)segmented.Result.GetType()
		.GetProperty("Segments")!
		.GetValue(segmented.Result)!;
	Console.WriteLine(
		$"{name}\t{legacy.Elapsed.TotalMilliseconds:F2} / {ToMebibytes(legacy.AllocatedBytes):F2}" +
		$"\t{segmented.Elapsed.TotalMilliseconds:F2} / {ToMebibytes(segmented.AllocatedBytes):F2}" +
		$"\t{segments.Count}");
}

void RunProductionPipelineBenchmark(int count)
{
	var root = Path.Combine(Path.GetTempPath(), $"DevProjex-SegmentedBenchmark-{Guid.NewGuid():N}");
	Directory.CreateDirectory(root);
	try
	{
		var content = string.Concat(Enumerable.Range(0, count).Select(static index => $"V{index:D7}|"));
		var path = Path.Combine(root, "data.txt");
		File.WriteAllText(path, content);
		var secretFindings = CreateExactFindings(content, count, "secret", RedactionFindingCategory.Secrets);
		var privateFindings = CreateExactFindings(content, count, "private", RedactionFindingCategory.PrivateData);

		using (var jitSession = CreateSession(secretFindings, privateFindings))
			RunOutput(jitSession, root, path, content);
		using var session = CreateSession(secretFindings, privateFindings);
		var cold = Measure(() => RunOutput(session, root, path, content));
		var warm = Measure(() => RunOutput(session, root, path, content));
		var occurrenceIds = warm.Result.Spans
			.Select(static span => span.OccurrenceId)
			.Distinct(StringComparer.Ordinal)
			.ToArray();
		var decisions = Measure(() =>
		{
			for (var iteration = 0; iteration < 100; iteration++)
			{
				session.SetKeepAsIs(occurrenceIds, keep: true);
				session.SetKeepAsIs(occurrenceIds, keep: false);
			}
			return occurrenceIds.Length;
		});

		Console.WriteLine($"cold cache\t{cold.Elapsed.TotalMilliseconds:F2} ms\t{ToMebibytes(cold.AllocatedBytes):F2} MiB");
		Console.WriteLine($"warm cache\t{warm.Elapsed.TotalMilliseconds:F2} ms\t{ToMebibytes(warm.AllocatedBytes):F2} MiB");
		Console.WriteLine(
			$"100 keep/unkeep batches\t{decisions.Elapsed.TotalMilliseconds:F2} ms" +
			$"\t{ToMebibytes(decisions.AllocatedBytes):F2} MiB");
	}
	finally
	{
		Directory.Delete(root, recursive: true);
	}
}

static SecretTextRedactionResult RunOutput(
	SecretRedactionSession session,
	string root,
	string path,
	string content)
{
	var scope = session.BeginOutput(
		root,
		[path],
		features: SecretRedactionFeatures.Secrets | SecretRedactionFeatures.PrivateData);
	var result = scope.Redact(path, content);
	scope.Complete();
	return result;
}

static SecretRedactionSession CreateSession(
	IReadOnlyList<DetectedSecret> secretFindings,
	IReadOnlyList<DetectedSecret> privateFindings) =>
	SecretRedactionSession.CreateWithPrivateData(
		new FixedDetector("catalog:smart-secrets-v4", secretFindings),
		new FixedDetector("private-data-v1", privateFindings));

static DetectedSecret[] CreateExactFindings(
	string content,
	int count,
	string rule,
	RedactionFindingCategory category)
{
	var findings = new DetectedSecret[count];
	for (var index = 0; index < count; index++)
	{
		var start = index * 9;
		findings[index] = new DetectedSecret(
			rule,
			start,
			8,
			content.Substring(start, 8),
			-100,
			Category: category);
	}
	return findings;
}

static DetectedSecret[] CreateNestedMarks(int count)
{
	var findings = new DetectedSecret[count];
	for (var index = 0; index < count; index++)
	{
		findings[index] = new DetectedSecret(
			"manual-secret",
			index,
			(count * 2) - (index * 2),
			string.Empty,
			int.MinValue,
			SecretFindingSource.SessionMark,
			Category: RedactionFindingCategory.Secrets);
	}
	return findings;
}

static DetectedSecret[] CreatePartialChain(int count)
{
	var findings = new DetectedSecret[count];
	for (var index = 0; index < count; index++)
	{
		findings[index] = new DetectedSecret(
			"manual-secret",
			index * 2,
			5,
			string.Empty,
			int.MinValue,
			SecretFindingSource.PersistentMark,
			Category: RedactionFindingCategory.Secrets);
	}
	return findings;
}

static DetectedSecret[] CreateExactStacks(int count)
{
	var groupCount = count / 3;
	var findings = new DetectedSecret[count];
	for (var index = 0; index < groupCount; index++)
	{
		var start = index * 10;
		findings[index * 3] = Create("generic-api-key", start, RedactionFindingCategory.Secrets, -50);
		findings[index * 3 + 1] = Create("specific", start, RedactionFindingCategory.Secrets, -100);
		findings[index * 3 + 2] = Create("private", start, RedactionFindingCategory.PrivateData, -100);
	}
	if (findings.Length > groupCount * 3)
		findings[^1] = Create("standalone", groupCount * 10, RedactionFindingCategory.Secrets, -100);
	return findings;

	static DetectedSecret Create(string rule, int start, RedactionFindingCategory category, int order) =>
		new(rule, start, 8, string.Empty, order, Category: category);
}

static IReadOnlyList<LegacyGroup> LegacyResolve(IReadOnlyList<DetectedSecret> findings)
{
	var candidates = findings
		.GroupBy(static finding => (finding.Start, finding.Length))
		.Select(static group => new LegacyGroup(
			group.Key.Start,
			group.Key.Length,
			group.GroupBy(static finding => finding.Category)
				.Select(static category => category.OrderBy(GetPriority).First())
				.OrderBy(GetPriority)
				.ToArray()))
		.OrderBy(static group => GetPriority(group.Candidates[0]))
		.ThenBy(static group => group.Start)
		.ToArray();
	var accepted = new SortedSet<LegacyInterval>(LegacyIntervalComparer.Instance);
	foreach (var candidate in candidates)
	{
		var overlaps = FindLegacyOverlaps(accepted, candidate.Start, candidate.End);
		if (overlaps.Count == 0)
		{
			accepted.Add(new LegacyInterval(candidate.Start, candidate.End, candidate));
			continue;
		}
		var blocked = overlaps.Select(static overlap => overlap.Group!.Candidates[0].Category).ToHashSet();
		var survivors = candidate.Candidates.Where(match => !blocked.Contains(match.Category)).ToArray();
		if (survivors.Length == 0)
			continue;
		var cursor = candidate.Start;
		foreach (var overlap in overlaps)
		{
			if (overlap.Start > cursor)
			{
				var residual = new LegacyGroup(cursor, overlap.Start - cursor, survivors);
				accepted.Add(new LegacyInterval(residual.Start, residual.End, residual));
			}
			cursor = Math.Max(cursor, overlap.End);
		}
		if (cursor < candidate.End)
		{
			var residual = new LegacyGroup(cursor, candidate.End - cursor, survivors);
			accepted.Add(new LegacyInterval(residual.Start, residual.End, residual));
		}
	}
	return accepted.Select(static interval => interval.Group!).ToArray();
}

static IReadOnlyList<LegacyInterval> FindLegacyOverlaps(
	SortedSet<LegacyInterval> accepted,
	int start,
	int end)
{
	if (accepted.Count == 0)
		return [];
	var overlaps = new List<LegacyInterval>();
	var predecessors = accepted.GetViewBetween(
		LegacyInterval.Minimum,
		new LegacyInterval(start, int.MaxValue, null));
	var predecessor = predecessors.Max;
	if (predecessor is not null && predecessor.End > start)
		overlaps.Add(predecessor);
	foreach (var interval in accepted.GetViewBetween(
		         new LegacyInterval(start, int.MinValue, null),
		         LegacyInterval.Maximum))
	{
		if (interval.Start >= end)
			break;
		if (overlaps.Count == 0 || !ReferenceEquals(overlaps[^1], interval))
			overlaps.Add(interval);
	}
	return overlaps;
}

static (int Mark, int Category, int Generic, int Order, string Rule) GetPriority(DetectedSecret finding) =>
	(
		(finding.Source & (SecretFindingSource.SessionMark | SecretFindingSource.PersistentMark)) != 0 ? 0 : 1,
		(int)finding.Category,
		finding.RuleId == "generic-api-key" ? 1 : 0,
		finding.RuleOrder,
		finding.RuleId);

static Measurement<T> Measure<T>(Func<T> operation)
{
	GC.Collect();
	GC.WaitForPendingFinalizers();
	GC.Collect();
	var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
	var stopwatch = Stopwatch.StartNew();
	var result = operation();
	stopwatch.Stop();
	var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
	GC.KeepAlive(result);
	return new Measurement<T>(result, stopwatch.Elapsed, allocated);
}

static double ToMebibytes(long bytes) => bytes / (1024d * 1024d);

internal sealed record Measurement<T>(T Result, TimeSpan Elapsed, long AllocatedBytes);
internal sealed record LegacyGroup(int Start, int Length, IReadOnlyList<DetectedSecret> Candidates)
{
	public int End => Start + Length;
}

internal sealed record LegacyInterval(int Start, int End, LegacyGroup? Group)
{
	public static LegacyInterval Minimum { get; } = new(int.MinValue, int.MinValue, null);
	public static LegacyInterval Maximum { get; } = new(int.MaxValue, int.MaxValue, null);
}

internal sealed class LegacyIntervalComparer : IComparer<LegacyInterval>
{
	public static LegacyIntervalComparer Instance { get; } = new();

	public int Compare(LegacyInterval? left, LegacyInterval? right)
	{
		if (ReferenceEquals(left, right))
			return 0;
		if (left is null)
			return -1;
		if (right is null)
			return 1;
		var start = left.Start.CompareTo(right.Start);
		return start != 0 ? start : left.End.CompareTo(right.End);
	}
}

internal sealed class FixedDetector(
	string rulesIdentity,
	IReadOnlyList<DetectedSecret> findings) : ISecretDetector
{
	public string RulesIdentity => rulesIdentity;

	public IReadOnlyList<DetectedSecret> Detect(
		string repositoryRelativePath,
		string content,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return findings;
	}
}
