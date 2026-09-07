using DevProjex.Application.Dependencies;

namespace DevProjex.Application.Ranking;

public sealed class ImportanceRankingService(
	DependencyFactsEngine dependencyFactsEngine,
	IProjectGitHistoryReader gitHistoryReader) : IImportanceRankingService
{
	public const string AlgorithmId = "importance-v1";
	public const string GraphVariant = "pagerank";
	public const double GraphWeight = 0.85;
	public const double GitWeight = 0.10;
	public const double RoleWeight = 0.05;
	private const int MaximumTopEntries = 10;
	private static readonly HashSet<string> TestFrameworks = new(StringComparer.OrdinalIgnoreCase)
	{
		"xunit", "nunit", "microsoft.visualstudio.testtools.unittesting",
		"vitest", "jest", "mocha", "pytest", "unittest"
	};

	public async Task<ImportanceRankingReport> RankAsync(
		string sourceRoot,
		IReadOnlyList<string> candidateFiles,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
		ArgumentNullException.ThrowIfNull(candidateFiles);
		var root = Path.GetFullPath(sourceRoot);
		var candidates = candidateFiles
			.Select(Path.GetFullPath)
			.Distinct(PathComparer.Default)
			.Select(path => new Candidate(path, PortableRelative(root, path)))
			.OrderBy(static candidate => candidate.RelativePath, StringComparer.Ordinal)
			.ToArray();
		if (candidates.Length == 0)
			return EmptyReport();

		var candidatePaths = candidates.Select(static candidate => candidate.FullPath).ToArray();
		var dependencyTask = ReadStableDependencySnapshotAsync(root, candidatePaths, cancellationToken);
		var historyTask = gitHistoryReader.ReadAsync(
			root,
			candidatePaths,
			cancellationToken);
		await Task.WhenAll(dependencyTask, historyTask).ConfigureAwait(false);
		var (dependency, sourceVersions) = await dependencyTask.ConfigureAwait(false);
		var history = await historyTask.ConfigureAwait(false);

		var factsByPath = dependency.Files.ToDictionary(static file => file.Path, StringComparer.Ordinal);
		var graphRaw = new Dictionary<string, double?>(StringComparer.Ordinal);
		var dependents = new Dictionary<string, int>(StringComparer.Ordinal);
		var dependencies = new Dictionary<string, int>(StringComparer.Ordinal);
		var roles = new Dictionary<string, ImportanceFileRole>(StringComparer.Ordinal);
		var pageRank = CalculatePageRank(candidates, dependency);
		foreach (var candidate in candidates)
		{
			factsByPath.TryGetValue(candidate.RelativePath, out var facts);
			var supported = facts?.Status == DependencyFileStatus.Supported;
			var incoming = supported
				? UniqueResolvedSources(dependency.EdgesByTarget.GetValueOrDefault(candidate.RelativePath))
				: 0;
			var outgoing = supported
				? UniqueResolvedTargets(dependency.EdgesBySource.GetValueOrDefault(candidate.RelativePath))
				: 0;
			dependents[candidate.RelativePath] = incoming;
			dependencies[candidate.RelativePath] = outgoing;
			graphRaw[candidate.RelativePath] = supported
				? pageRank.GetValueOrDefault(candidate.RelativePath)
				: null;
			roles[candidate.RelativePath] = ClassifyRole(candidate.RelativePath, facts, incoming, outgoing);
		}

		var graphNormalized = RankNormalize(graphRaw);
		var gitRaw = candidates.ToDictionary(
			static candidate => candidate.RelativePath,
			candidate => history.Files.TryGetValue(candidate.FullPath, out var activity)
				? (double?)(activity.CommitCount + Recency(activity, history.WindowSize))
				: null,
			StringComparer.Ordinal);
		var gitNormalized = RankNormalize(gitRaw);
		var roleNormalized = RankNormalize(candidates.ToDictionary(
			static candidate => candidate.RelativePath,
			candidate => (double?)RoleValue(roles[candidate.RelativePath]),
			StringComparer.Ordinal));
		var coverage = candidates.Length == 0
			? 0
			: Math.Clamp(
				(double)Math.Max(0, dependency.Coverage.Supported - dependency.Coverage.ExtractionFailed) /
				candidates.Length,
				0,
				1);
		var redistributed = coverage < 1 || gitRaw.Values.Any(static value => value is null);

		var scored = new List<ScoredCandidate>(candidates.Length);
		foreach (var candidate in candidates)
		{
			var score = CalculateScore(
				graphNormalized[candidate.RelativePath],
				gitNormalized[candidate.RelativePath],
				roleNormalized[candidate.RelativePath] ?? 0.5,
				coverage);
			scored.Add(new ScoredCandidate(candidate, score));
		}

		var ordered = scored
			.OrderByDescending(static candidate => candidate.Score)
			.ThenBy(static candidate => candidate.Candidate.RelativePath, StringComparer.Ordinal)
			.Select((candidate, index) =>
			{
				var path = candidate.Candidate.RelativePath;
				var hasHistory = history.Files.TryGetValue(candidate.Candidate.FullPath, out var activity);
				var historyReason = hasHistory
					? (ProjectGitHistoryUnavailableReason?)null
					: history.UnavailableFiles.TryGetValue(candidate.Candidate.FullPath, out var unavailableReason)
						? unavailableReason
						: history.UnavailableReason;
				return new ImportanceRankingEntry(
					candidate.Candidate.FullPath,
					path,
					index + 1,
					candidate.Score,
					dependents[path],
					dependencies[path],
					hasHistory ? activity!.CommitCount : null,
					hasHistory ? activity!.MostRecentCommitPosition : null,
					roles[path],
					graphRaw[path] is not null,
					hasHistory,
					historyReason);
			})
			.ToArray();

		return new ImportanceRankingReport(
			AlgorithmId,
			ordered,
			ordered.Take(MaximumTopEntries).ToArray(),
			candidates.Length,
			dependency.Coverage.Supported,
			dependency.Coverage.ExtractionFailed,
			coverage,
			history.WindowSize,
			history.CommitCount,
			history.UnavailableReason,
			redistributed,
			GraphVariant)
		{
			SourceVersions = sourceVersions
		};
	}

	private async Task<(DependencyIndexSnapshot Snapshot, IReadOnlyDictionary<string, RankingSourceVersion> Versions)>
		ReadStableDependencySnapshotAsync(
			string root,
			IReadOnlyList<string> candidatePaths,
			CancellationToken cancellationToken)
	{
		for (var attempt = 0; attempt < 2; attempt++)
		{
			var before = CaptureVersions(candidatePaths);
			var snapshot = await dependencyFactsEngine
				.IndexAsync(root, candidatePaths, progress: null, cancellationToken)
				.ConfigureAwait(false);
			var after = CaptureVersions(candidatePaths);
			if (VersionsEqual(before, after))
				return (snapshot, after);
		}

		throw new IOException("Selected source files changed while dependency facts were being indexed.");
	}

	private static IReadOnlyDictionary<string, RankingSourceVersion> CaptureVersions(
		IReadOnlyList<string> paths) =>
		paths.ToDictionary(
			Path.GetFullPath,
			RankingSourceVersion.Capture,
			PathComparer.Default);

	private static bool VersionsEqual(
		IReadOnlyDictionary<string, RankingSourceVersion> left,
		IReadOnlyDictionary<string, RankingSourceVersion> right) =>
		left.Count == right.Count && left.All(pair =>
			right.TryGetValue(pair.Key, out var version) && version == pair.Value);

	internal static IReadOnlyDictionary<string, double?> RankNormalize(
		IReadOnlyDictionary<string, double?> values)
	{
		var present = values
			.Where(static pair => pair.Value is not null)
			.GroupBy(static pair => pair.Value!.Value)
			.OrderBy(static group => group.Key)
			.ToArray();
		var result = values.Keys.ToDictionary(static path => path, static _ => (double?)null, StringComparer.Ordinal);
		if (present.Length == 0)
			return result;
		if (present.Length == 1)
		{
			foreach (var pair in present[0])
				result[pair.Key] = 0.5;
			return result;
		}
		for (var index = 0; index < present.Length; index++)
		{
			var normalized = (double)index / (present.Length - 1);
			foreach (var pair in present[index])
				result[pair.Key] = normalized;
		}
		return result;
	}

	internal static double CalculateScore(double? graph, double? git, double role, double graphCoverage)
	{
		var coverage = Math.Clamp(graphCoverage, 0, 1);
		var effectiveGraphWeight = GraphWeight * coverage;
		var graphDeficit = GraphWeight - effectiveGraphWeight;
		var nonGraphWeight = GitWeight + RoleWeight;
		var effectiveGitWeight = GitWeight + graphDeficit * GitWeight / nonGraphWeight;
		var effectiveRoleWeight = RoleWeight + graphDeficit * RoleWeight / nonGraphWeight;
		var signals = new List<(double Value, double Weight)>(3);
		if (graph is { } graphValue)
			signals.Add((graphValue, effectiveGraphWeight));
		if (git is { } gitValue)
			signals.Add((gitValue, effectiveGitWeight));
		signals.Add((role, effectiveRoleWeight));
		var availableWeight = signals.Sum(static signal => signal.Weight);
		return availableWeight <= 0
			? 0
			: signals.Sum(static signal => signal.Value * signal.Weight) / availableWeight;
	}

	internal static ImportanceFileRole ClassifyRole(
		string path,
		FileFacts? facts,
		int dependents,
		int dependencies)
	{
		if (IsManifest(path))
			return ImportanceFileRole.Manifest;
		if (HasTestFrameworkEvidence(facts) || HasTestPathEvidence(path) && facts?.Status == DependencyFileStatus.Supported)
			return ImportanceFileRole.TestSource;
		if (dependents == 0 && dependencies >= 3)
			return ImportanceFileRole.EntryPoint;
		return ImportanceFileRole.Source;
	}

	internal static IReadOnlyDictionary<string, double> CalculatePageRank(
		IReadOnlyList<(string FullPath, string RelativePath)> candidates,
		DependencyIndexSnapshot snapshot) =>
		CalculatePageRank(candidates.Select(static candidate => new Candidate(candidate.FullPath, candidate.RelativePath)).ToArray(), snapshot);

	private static IReadOnlyDictionary<string, double> CalculatePageRank(
		IReadOnlyList<Candidate> candidates,
		DependencyIndexSnapshot snapshot)
	{
		const double damping = 0.85;
		const int iterations = 30;
		var supportedPaths = snapshot.Files
			.Where(static file => file.Status == DependencyFileStatus.Supported)
			.Select(static file => file.Path)
			.ToHashSet(StringComparer.Ordinal);
		var paths = candidates
			.Select(static candidate => candidate.RelativePath)
			.Where(supportedPaths.Contains)
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)
			.ToArray();
		var pathSet = paths.ToHashSet(StringComparer.Ordinal);
		var outgoingSets = paths.ToDictionary(static path => path, static _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
		foreach (var edge in snapshot.Edges)
		{
			if (edge.Status == ResolutionStatus.Resolved &&
			    edge.Target is { } target &&
			    edge.Source != target &&
			    pathSet.Contains(edge.Source) &&
			    pathSet.Contains(target))
			{
				outgoingSets[edge.Source].Add(target);
			}
		}
		var outgoing = outgoingSets.ToDictionary(
			static pair => pair.Key,
			static pair => pair.Value.Order(StringComparer.Ordinal).ToArray(),
			StringComparer.Ordinal);
		var count = paths.Length;
		if (count == 0)
			return new Dictionary<string, double>(StringComparer.Ordinal);
		var rank = paths.ToDictionary(static path => path, _ => 1d / count, StringComparer.Ordinal);
		for (var iteration = 0; iteration < iterations; iteration++)
		{
			var next = paths.ToDictionary(static path => path, _ => (1 - damping) / count, StringComparer.Ordinal);
			var dangling = paths.Where(path => outgoing[path].Length == 0).Sum(path => rank[path]);
			foreach (var path in paths)
				next[path] += damping * dangling / count;
			foreach (var source in paths)
			{
				var targets = outgoing[source];
				if (targets.Length == 0)
					continue;
				var share = damping * rank[source] / targets.Length;
				foreach (var target in targets)
					next[target] += share;
			}
			rank = next;
		}
		return rank;
	}

	private static int UniqueResolvedSources(IReadOnlyList<DependencyEdge>? edges) =>
		edges?.Where(static edge => edge.Status == ResolutionStatus.Resolved && edge.Target is not null && edge.Source != edge.Target)
			.Select(static edge => edge.Source).Distinct(StringComparer.Ordinal).Count() ?? 0;

	private static int UniqueResolvedTargets(IReadOnlyList<DependencyEdge>? edges) =>
		edges?.Where(static edge => edge.Status == ResolutionStatus.Resolved && edge.Target is not null && edge.Source != edge.Target)
			.Select(static edge => edge.Target!).Distinct(StringComparer.Ordinal).Count() ?? 0;

	private static bool HasTestFrameworkEvidence(FileFacts? facts)
	{
		if (facts is null)
			return false;
		return facts.Imports.Any(import => TestFrameworks.Contains(NormalizeFramework(import.Specifier))) ||
		       facts.ContextNamespaces.Any(context => TestFrameworks.Contains(NormalizeFramework(context)));
	}

	private static string NormalizeFramework(string value)
	{
		var normalized = value.Trim().TrimStart('@').Replace("::", ".", StringComparison.Ordinal).ToLowerInvariant();
		if (normalized.StartsWith("microsoft.visualstudio.testtools.unittesting", StringComparison.Ordinal))
			return "microsoft.visualstudio.testtools.unittesting";
		var separator = normalized.IndexOfAny('/', '.');
		return separator > 0 ? normalized[..separator] : normalized;
	}

	private static bool HasTestPathEvidence(string path)
	{
		var normalized = '/' + path.Replace('\\', '/').ToLowerInvariant() + '/';
		var name = Path.GetFileNameWithoutExtension(path);
		return normalized.Contains("/test/", StringComparison.Ordinal) ||
		       normalized.Contains("/tests/", StringComparison.Ordinal) ||
		       name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase) ||
		       name.EndsWith(".test", StringComparison.OrdinalIgnoreCase) ||
		       name.EndsWith(".spec", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsManifest(string path)
	{
		var name = Path.GetFileName(path);
		var extension = Path.GetExtension(path);
		return extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
		       extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
		       name.Equals("package.json", StringComparison.OrdinalIgnoreCase) ||
		       name.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) ||
		       name.StartsWith("tsconfig", StringComparison.OrdinalIgnoreCase) &&
		       name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
	}

	private static double RoleValue(ImportanceFileRole role) => role switch
	{
		ImportanceFileRole.Manifest => 1,
		ImportanceFileRole.EntryPoint => 0.75,
		ImportanceFileRole.Source => 0.5,
		ImportanceFileRole.TestSource => 0,
		_ => throw new ArgumentOutOfRangeException(nameof(role), role, null)
	};

	private static double Recency(ProjectGitFileActivity activity, int window) =>
		activity.MostRecentCommitPosition <= 0 || window <= 1
			? 0
			: 1d - (double)(activity.MostRecentCommitPosition - 1) / (window - 1);

	private static string PortableRelative(string root, string path) =>
		Path.GetRelativePath(root, path).Replace('\\', '/');

	private static ImportanceRankingReport EmptyReport() =>
		new(AlgorithmId, [], [], 0, 0, 0, 0, ProjectGitHistoryReaderWindow, 0,
			ProjectGitHistoryUnavailableReason.NotRepository, false, GraphVariant);

	private const int ProjectGitHistoryReaderWindow = 200;
	private sealed record Candidate(string FullPath, string RelativePath);
	private sealed record ScoredCandidate(Candidate Candidate, double Score);
}
