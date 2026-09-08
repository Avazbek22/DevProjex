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
	public const double PageRankAbsoluteQuantum = 1e-12;
	public const ImportanceMissingSignalPolicy MissingSignalPolicy =
		ImportanceMissingSignalPolicy.ConfidenceLimited;
	private const int MaximumTopEntries = 10;
	private const int PageRankIterations = 30;
	private static readonly HashSet<string> TestFrameworks = new(StringComparer.OrdinalIgnoreCase)
	{
		"xunit", "nunit", "microsoft.visualstudio.testtools.unittesting",
		"vitest", "jest", "mocha", "pytest", "unittest"
	};

	public Task<ImportanceRankingReport> RankAsync(
		string sourceRoot,
		IReadOnlyList<string> candidateFiles,
		CancellationToken cancellationToken = default) =>
		RankCoreAsync(sourceRoot, candidateFiles, focus: null, progress: null, cancellationToken);

	public Task<ImportanceRankingReport> RankAsync(
		string sourceRoot,
		IReadOnlyList<string> candidateFiles,
		IProgress<ImportanceRankingProgress>? progress,
		CancellationToken cancellationToken = default) =>
		RankCoreAsync(sourceRoot, candidateFiles, focus: null, progress, cancellationToken);

	public Task<ImportanceRankingReport> RankAsync(
		string sourceRoot,
		IReadOnlyList<string> candidateFiles,
		FocusRankingRequest focus,
		IProgress<ImportanceRankingProgress>? progress = null,
		CancellationToken cancellationToken = default) =>
		RankCoreAsync(sourceRoot, candidateFiles, focus, progress, cancellationToken);

	private async Task<ImportanceRankingReport> RankCoreAsync(
		string sourceRoot,
		IReadOnlyList<string> candidateFiles,
		FocusRankingRequest? focus,
		IProgress<ImportanceRankingProgress>? progress,
		CancellationToken cancellationToken)
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
		Report(progress, ImportanceRankingStage.IndexingFacts, 0, candidates.Length);
		var factsProgress = new BoundedFactsProgress(progress, candidates.Length);
		var (dependency, sourceVersions) = await ReadStableDependencySnapshotAsync(
			root,
			candidatePaths,
			factsProgress,
			cancellationToken).ConfigureAwait(false);
		Report(progress, ImportanceRankingStage.IndexingFacts, candidates.Length, candidates.Length);

		Report(progress, ImportanceRankingStage.ReadingHistory, 0, 1);
		var history = await gitHistoryReader.ReadAsync(root, candidatePaths, cancellationToken)
			.ConfigureAwait(false);
		Report(progress, ImportanceRankingStage.ReadingHistory, 1, 1);

		const int computationUnits = 100;
		Report(progress, ImportanceRankingStage.ComputingPriorities, 0, computationUnits);
		var factsByPath = dependency.Files.ToDictionary(static file => file.Path, StringComparer.Ordinal);
		var graph = BuildGraph(candidates, dependency, cancellationToken);
		Report(progress, ImportanceRankingStage.ComputingPriorities, 10, computationUnits);
		var pageRank = CalculatePageRank(
			graph,
			cancellationToken,
			iteration => Report(
				progress,
				ImportanceRankingStage.ComputingPriorities,
				10 + (iteration + 1) * 50 / PageRankIterations,
				computationUnits));

		var graphRaw = new Dictionary<string, double?>(candidates.Length, StringComparer.Ordinal);
		var dependents = new Dictionary<string, int>(candidates.Length, StringComparer.Ordinal);
		var dependencies = new Dictionary<string, int>(candidates.Length, StringComparer.Ordinal);
		var roles = new Dictionary<string, ImportanceFileRole>(candidates.Length, StringComparer.Ordinal);
		var coordinators = new Dictionary<string, bool>(candidates.Length, StringComparer.Ordinal);
		foreach (var candidate in candidates)
		{
			cancellationToken.ThrowIfCancellationRequested();
			factsByPath.TryGetValue(candidate.RelativePath, out var facts);
			if (graph.NodeByPath.TryGetValue(candidate.RelativePath, out var node))
			{
				dependents[candidate.RelativePath] = graph.Dependents[node];
				dependencies[candidate.RelativePath] = graph.Outgoing[node].Length;
				graphRaw[candidate.RelativePath] = QuantizePageRank(pageRank[node]);
			}
			else
			{
				dependents[candidate.RelativePath] = 0;
				dependencies[candidate.RelativePath] = 0;
				graphRaw[candidate.RelativePath] = null;
			}
			roles[candidate.RelativePath] = ClassifyRole(
				candidate.RelativePath,
				facts,
				dependents[candidate.RelativePath],
				dependencies[candidate.RelativePath]);
			coordinators[candidate.RelativePath] = IsCoordinatorCandidate(
				roles[candidate.RelativePath],
				dependents[candidate.RelativePath],
				dependencies[candidate.RelativePath]);
		}

		var graphNormalized = RankNormalize(graphRaw, cancellationToken);
		var gitRaw = new Dictionary<string, double?>(candidates.Length, StringComparer.Ordinal);
		var roleRaw = new Dictionary<string, double?>(candidates.Length, StringComparer.Ordinal);
		foreach (var candidate in candidates)
		{
			cancellationToken.ThrowIfCancellationRequested();
			gitRaw[candidate.RelativePath] = history.Files.TryGetValue(candidate.FullPath, out var activity)
				? activity.CommitCount + Recency(activity, history.WindowSize)
				: null;
			roleRaw[candidate.RelativePath] = RoleValue(
				roles[candidate.RelativePath],
				coordinators[candidate.RelativePath]);
		}
		var gitNormalized = RankNormalize(gitRaw, cancellationToken);
		var roleNormalized = RankNormalize(roleRaw, cancellationToken);
		var coverage = CalculateExtractedFactsCoverage(dependency.Coverage, candidates.Length);
		var internalReferenceCandidates = dependency.Edges.Count(static edge =>
			edge.Status != ResolutionStatus.External);
		var resolvedInternalReferences = graph.EdgeCount;
		var resolvedInternalReferenceCoverage = internalReferenceCandidates == 0
			? 0
			: Math.Clamp((double)resolvedInternalReferences / internalReferenceCandidates, 0, 1);
		var hasMissingSignals = coverage < 1 || gitRaw.Values.Any(static value => value is null);
		var gitConfidence = history.IsShallow && !history.IsComplete
			? Math.Clamp((double)history.CommitCount / history.WindowSize, 0, 1)
			: 1;

		var scored = new List<ScoredCandidate>(candidates.Length);
		var scoreProgressStep = Math.Max(1, candidates.Length / 25);
		for (var index = 0; index < candidates.Length; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var candidate = candidates[index];
			var score = CalculateScoreBreakdown(
				graphNormalized[candidate.RelativePath],
				gitNormalized[candidate.RelativePath],
				roleNormalized[candidate.RelativePath] ?? 0.5,
				coverage,
				MissingSignalPolicy,
				gitConfidence);
			scored.Add(new ScoredCandidate(candidate, score));
			if ((index + 1) % scoreProgressStep == 0 || index + 1 == candidates.Length)
			{
				Report(
					progress,
					ImportanceRankingStage.ComputingPriorities,
					70 + (index + 1) * 29 / candidates.Length,
					computationUnits);
			}
		}

		var ordered = scored
			.OrderByDescending(static candidate => candidate.Score.Score)
			.ThenBy(static candidate => candidate.Candidate.RelativePath, StringComparer.Ordinal)
			.Select((candidate, index) =>
			{
				cancellationToken.ThrowIfCancellationRequested();
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
					candidate.Score.Score,
					dependents[path],
					dependencies[path],
					hasHistory ? activity!.CommitCount : null,
					hasHistory ? activity!.MostRecentCommitPosition : null,
					roles[path],
					graphRaw[path] is not null,
					hasHistory,
					historyReason)
				{
					Confidence = candidate.Score.Confidence,
					MainContribution = candidate.Score.MainContribution,
					ConfidenceLimited = MissingSignalPolicy == ImportanceMissingSignalPolicy.ConfidenceLimited &&
						candidate.Score.Confidence < 1,
					IsCoordinator = coordinators[path]
				};
			})
			.ToArray();
		Report(progress, ImportanceRankingStage.ComputingPriorities, computationUnits, computationUnits);

		var report = new ImportanceRankingReport(
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
			MissingSignalPolicy == ImportanceMissingSignalPolicy.Redistribute && hasMissingSignals,
			GraphVariant)
		{
			ResolvedInternalReferences = resolvedInternalReferences,
			InternalReferenceCandidates = internalReferenceCandidates,
			ResolvedInternalReferenceCoverage = resolvedInternalReferenceCoverage,
			FilesWithResolvedEdges = graph.FilesWithEdges,
			GitHistoryIsShallow = history.IsShallow,
			GitHistoryIsComplete = history.IsComplete,
			HasMissingSignals = hasMissingSignals,
			MissingSignalPolicy = MissingSignalPolicy,
			SourceVersions = sourceVersions,
			DependencyMetrics = dependency.Metrics
		};
		return focus is null
			? report
			: FocusRankingEngine.Apply(report, focus, graph, dependency, cancellationToken);
	}

	internal static double CalculateExtractedFactsCoverage(
		DependencyFactsCoverage coverage,
		int candidateCount) =>
		candidateCount <= 0
			? 0
			: Math.Clamp((double)coverage.Supported / candidateCount, 0, 1);

	private async Task<(DependencyIndexSnapshot Snapshot, IReadOnlyDictionary<string, RankingSourceVersion> Versions)>
		ReadStableDependencySnapshotAsync(
			string root,
			IReadOnlyList<string> candidatePaths,
			IProgress<DependencyIndexProgress>? progress,
			CancellationToken cancellationToken)
	{
		for (var attempt = 0; attempt < 2; attempt++)
		{
			var before = await CaptureVersionsAsync(candidatePaths, cancellationToken)
				.ConfigureAwait(false);
			var contentIdentities = new DependencyManifestContentIdentities(
				before.ToDictionary(
					static pair => pair.Key,
					static pair => pair.Value.ContentHash ?? string.Empty,
					PathComparer.Default));
			var snapshot = await dependencyFactsEngine
				.IndexAsync(
					root,
					candidatePaths,
					progress,
					cancellationToken,
					contentIdentities)
				.ConfigureAwait(false);
			var after = await CaptureVersionsAsync(candidatePaths, cancellationToken)
				.ConfigureAwait(false);
			if (VersionsEqual(before, after))
				return (snapshot, after);
		}

		throw new IOException("Selected source files changed while dependency facts were being indexed.");
	}

	private static async Task<IReadOnlyDictionary<string, RankingSourceVersion>> CaptureVersionsAsync(
		IReadOnlyList<string> paths,
		CancellationToken cancellationToken)
	{
		var versions = new Dictionary<string, RankingSourceVersion>(paths.Count, PathComparer.Default);
		foreach (var path in paths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			versions[Path.GetFullPath(path)] = await RankingSourceVersion
				.CaptureAsync(path, cancellationToken)
				.ConfigureAwait(false);
		}
		return versions;
	}

	private static bool VersionsEqual(
		IReadOnlyDictionary<string, RankingSourceVersion> left,
		IReadOnlyDictionary<string, RankingSourceVersion> right) =>
		left.Count == right.Count && left.All(pair =>
			right.TryGetValue(pair.Key, out var version) && version == pair.Value);

	internal static IReadOnlyDictionary<string, double?> RankNormalize(
		IReadOnlyDictionary<string, double?> values,
		CancellationToken cancellationToken = default)
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
			{
				cancellationToken.ThrowIfCancellationRequested();
				result[pair.Key] = 0.5;
			}
			return result;
		}
		for (var index = 0; index < present.Length; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var normalized = (double)index / (present.Length - 1);
			foreach (var pair in present[index])
				result[pair.Key] = normalized;
		}
		return result;
	}

	internal static double CalculateScore(double? graph, double? git, double role, double graphCoverage)
		=> CalculateScoreBreakdown(
			graph,
			git,
			role,
			graphCoverage,
			MissingSignalPolicy,
			gitConfidence: 1).Score;

	internal static ImportanceScoreBreakdown CalculateScoreBreakdown(
		double? graph,
		double? git,
		double role,
		double graphCoverage,
		ImportanceMissingSignalPolicy policy,
		double gitConfidence = 1)
	{
		var coverage = Math.Clamp(graphCoverage, 0, 1);
		var effectiveGraphWeight = GraphWeight * coverage;
		var effectiveGitWeight = GitWeight * Math.Clamp(gitConfidence, 0, 1);
		var graphContribution = graph.GetValueOrDefault() * (graph is null ? 0 : effectiveGraphWeight);
		var gitContribution = git.GetValueOrDefault() * (git is null ? 0 : effectiveGitWeight);
		var roleContribution = role * RoleWeight;
		var availableWeight = (graph is null ? 0 : effectiveGraphWeight) +
		                      (git is null ? 0 : effectiveGitWeight) + RoleWeight;
		var knownContribution = graphContribution + gitContribution + roleContribution;
		var score = policy switch
		{
			ImportanceMissingSignalPolicy.Redistribute => availableWeight <= 0
				? 0
				: knownContribution / availableWeight,
			ImportanceMissingSignalPolicy.NeutralFill =>
				knownContribution + (1 - availableWeight) * 0.5,
			ImportanceMissingSignalPolicy.ConfidenceLimited => knownContribution,
			_ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
		};
		var mainContribution = graphContribution >= gitContribution && graphContribution >= roleContribution
			? ImportanceRankingSignal.Graph
			: gitContribution >= roleContribution
				? ImportanceRankingSignal.Git
				: ImportanceRankingSignal.Role;
		return new ImportanceScoreBreakdown(
			score,
			Math.Clamp(availableWeight, 0, 1),
			graphContribution,
			gitContribution,
			roleContribution,
			mainContribution);
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
		if (HasExplicitEntryPointEvidence(facts))
			return ImportanceFileRole.EntryPoint;
		return ImportanceFileRole.Source;
	}

	internal static bool IsCoordinatorCandidate(
		ImportanceFileRole role,
		int dependents,
		int dependencies) =>
		role == ImportanceFileRole.Source && dependents == 0 && dependencies >= 3;

	internal static IReadOnlyDictionary<string, double> CalculatePageRank(
		IReadOnlyList<(string FullPath, string RelativePath)> candidates,
		DependencyIndexSnapshot snapshot,
		CancellationToken cancellationToken = default,
		Action<int>? iterationCompleted = null)
	{
		var candidateRecords = candidates
			.Select(static candidate => new Candidate(candidate.FullPath, candidate.RelativePath))
			.OrderBy(static candidate => candidate.RelativePath, StringComparer.Ordinal)
			.ToArray();
		var graph = BuildGraph(candidateRecords, snapshot, cancellationToken);
		var ranks = CalculatePageRank(graph, cancellationToken, iterationCompleted);
		var result = new Dictionary<string, double>(graph.Paths.Length, StringComparer.Ordinal);
		for (var node = 0; node < graph.Paths.Length; node++)
			result[graph.Paths[node]] = QuantizePageRank(ranks[node]);
		return result;
	}

	internal static RankingGraph BuildGraph(
		IReadOnlyList<Candidate> candidates,
		DependencyIndexSnapshot snapshot,
		CancellationToken cancellationToken)
	{
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
		var nodeByPath = new Dictionary<string, int>(paths.Length, StringComparer.Ordinal);
		for (var node = 0; node < paths.Length; node++)
			nodeByPath.Add(paths[node], node);
		var outgoingSets = new HashSet<int>[paths.Length];
		for (var node = 0; node < outgoingSets.Length; node++)
			outgoingSets[node] = [];
		foreach (var edge in snapshot.Edges)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (edge.Status != ResolutionStatus.Resolved ||
				edge.Target is not { } target ||
				!nodeByPath.TryGetValue(edge.Source, out var sourceNode) ||
				!nodeByPath.TryGetValue(target, out var targetNode) ||
				sourceNode == targetNode)
			{
				continue;
			}
			outgoingSets[sourceNode].Add(targetNode);
		}

		var outgoing = new int[paths.Length][];
		var dependents = new int[paths.Length];
		var filesWithEdges = new bool[paths.Length];
		var edgeCount = 0;
		for (var source = 0; source < paths.Length; source++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			outgoing[source] = outgoingSets[source].Order().ToArray();
			edgeCount += outgoing[source].Length;
			if (outgoing[source].Length > 0)
				filesWithEdges[source] = true;
			foreach (var target in outgoing[source])
			{
				dependents[target]++;
				filesWithEdges[target] = true;
			}
		}
		return new RankingGraph(
			paths,
			nodeByPath,
			outgoing,
			dependents,
			edgeCount,
			filesWithEdges.Count(static value => value));
	}

	private static double[] CalculatePageRank(
		RankingGraph graph,
		CancellationToken cancellationToken,
		Action<int>? iterationCompleted = null)
	{
		const double damping = 0.85;
		var count = graph.Paths.Length;
		if (count == 0)
			return [];
		var rank = new double[count];
		var next = new double[count];
		Array.Fill(rank, 1d / count);
		for (var iteration = 0; iteration < PageRankIterations; iteration++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Array.Fill(next, (1 - damping) / count);
			var dangling = 0d;
			for (var source = 0; source < count; source++)
			{
				if ((source & 255) == 0)
					cancellationToken.ThrowIfCancellationRequested();
				if (graph.Outgoing[source].Length == 0)
					dangling += rank[source];
			}
			var danglingShare = damping * dangling / count;
			for (var node = 0; node < count; node++)
				next[node] += danglingShare;
			for (var source = 0; source < count; source++)
			{
				if ((source & 255) == 0)
					cancellationToken.ThrowIfCancellationRequested();
				var targets = graph.Outgoing[source];
				if (targets.Length == 0)
					continue;
				var share = damping * rank[source] / targets.Length;
				foreach (var target in targets)
					next[target] += share;
			}
			(rank, next) = (next, rank);
			iterationCompleted?.Invoke(iteration);
		}
		return rank;
	}

	private static bool HasTestFrameworkEvidence(FileFacts? facts)
	{
		if (facts is null)
			return false;
		return facts.Imports.Any(import => TestFrameworks.Contains(NormalizeFramework(import.Specifier))) ||
		       facts.ContextNamespaces.Any(context => TestFrameworks.Contains(NormalizeFramework(context)));
	}

	private static bool HasExplicitEntryPointEvidence(FileFacts? facts) =>
		facts?.Declarations.Any(static declaration =>
			declaration.Identity.SymbolKind is SymbolKind.Function or SymbolKind.Module &&
			IsEntryPointName(declaration.Identity.QualifiedName)) == true;

	private static bool IsEntryPointName(string qualifiedName)
	{
		var separator = qualifiedName.LastIndexOfAny(['.', ':', '/', '\\']);
		var name = separator >= 0 ? qualifiedName[(separator + 1)..] : qualifiedName;
		return name.Equals("Main", StringComparison.Ordinal) ||
		       name.Equals("__main__", StringComparison.Ordinal);
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

	private static double RoleValue(ImportanceFileRole role, bool isCoordinator) => role switch
	{
		ImportanceFileRole.Manifest => 1,
		ImportanceFileRole.EntryPoint => 0.9,
		ImportanceFileRole.Source when isCoordinator => 0.65,
		ImportanceFileRole.Source => 0.5,
		ImportanceFileRole.TestSource => 0,
		_ => throw new ArgumentOutOfRangeException(nameof(role), role, null)
	};

	internal static double QuantizePageRank(double value) =>
		Math.Round(value / PageRankAbsoluteQuantum, MidpointRounding.ToEven) * PageRankAbsoluteQuantum;

	private static double Recency(ProjectGitFileActivity activity, int window) =>
		activity.MostRecentCommitPosition <= 0 || window <= 1
			? 0
			: 1d - (double)(activity.MostRecentCommitPosition - 1) / (window - 1);

	private static string PortableRelative(string root, string path) =>
		Path.GetRelativePath(root, path).Replace('\\', '/');

	private static ImportanceRankingReport EmptyReport() =>
		new(AlgorithmId, [], [], 0, 0, 0, 0, ProjectGitHistoryReaderWindow, 0,
			ProjectGitHistoryUnavailableReason.NotRepository, false, GraphVariant);

	private static void Report(
		IProgress<ImportanceRankingProgress>? progress,
		ImportanceRankingStage stage,
		int completed,
		int total) =>
		progress?.Report(new ImportanceRankingProgress(stage, Math.Clamp(completed, 0, total), total));

	private const int ProjectGitHistoryReaderWindow = 200;
	internal sealed record Candidate(string FullPath, string RelativePath);
	private sealed record ScoredCandidate(Candidate Candidate, ImportanceScoreBreakdown Score);
	internal readonly record struct ImportanceScoreBreakdown(
		double Score,
		double Confidence,
		double GraphContribution,
		double GitContribution,
		double RoleContribution,
		ImportanceRankingSignal MainContribution);

	private sealed class BoundedFactsProgress(
		IProgress<ImportanceRankingProgress>? progress,
		int candidateCount) : IProgress<DependencyIndexProgress>
	{
		private readonly int _step = Math.Max(1, candidateCount / 100);
		private int _lastReported;

		public void Report(DependencyIndexProgress value)
		{
			var completed = Math.Clamp(value.CompletedFiles, 0, value.TotalFiles);
			if (completed != value.TotalFiles && completed - _lastReported < _step)
				return;
			_lastReported = completed;
			ImportanceRankingService.Report(
				progress,
				ImportanceRankingStage.IndexingFacts,
				completed,
				Math.Max(1, value.TotalFiles));
		}
	}
}
