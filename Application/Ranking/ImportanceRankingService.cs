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
			.Select(static (candidate, id) => candidate with { Id = id })
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
		var factsByPath = dependency.FileByPath.Count == dependency.Files.Count
			? dependency.FileByPath
			: dependency.Files.ToDictionary(static file => file.Path, StringComparer.Ordinal);
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

		var states = candidates.Select(static candidate => new CandidateState(candidate)).ToArray();
		foreach (var state in states)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var candidate = state.Candidate;
			factsByPath.TryGetValue(candidate.RelativePath, out var facts);
			if (graph.NodeByPath.TryGetValue(candidate.RelativePath, out var node))
			{
				state.Dependents = graph.Dependents[node];
				state.Dependencies = graph.Outgoing[node].Length;
				state.GraphRaw = QuantizePageRank(pageRank[node]);
			}
			state.Role = ClassifyRole(
				candidate.RelativePath,
				facts,
				state.Dependents,
				state.Dependencies);
			state.IsCoordinator = IsCoordinatorCandidate(state.Role, state.Dependents, state.Dependencies);
		}

		NormalizeStateValues(states, RankingValueKind.Graph, cancellationToken);
		foreach (var state in states)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var candidate = state.Candidate;
			state.GitRaw = history.Files.TryGetValue(candidate.FullPath, out var activity)
				? activity.CommitCount + Recency(activity, history.WindowSize)
				: null;
			state.RoleRaw = RoleValue(state.Role, state.IsCoordinator);
		}
		NormalizeStateValues(states, RankingValueKind.Git, cancellationToken);
		NormalizeRoleValues(states, cancellationToken);
		var coverage = CalculateExtractedFactsCoverage(dependency.Coverage, candidates.Length);
		var candidateRelativePaths = candidates
			.Select(static candidate => candidate.RelativePath)
			.ToHashSet(StringComparer.Ordinal);
		var internalReferenceCandidates = dependency.Edges.Count(static edge =>
			edge.Status != ResolutionStatus.External);
		var resolvedInternalReferences = dependency.Edges.Count(edge =>
			edge.Status == ResolutionStatus.Resolved &&
			ResolvedTargets(edge).Any(candidateRelativePaths.Contains));
		var resolvedInternalReferenceCoverage = internalReferenceCandidates == 0
			? 0
			: Math.Clamp((double)resolvedInternalReferences / internalReferenceCandidates, 0, 1);
		var hasMissingSignals = coverage < 1 || states.Any(static state => state.GitRaw is null);
		var gitConfidence = history.IsShallow && !history.IsComplete
			? Math.Clamp((double)history.CommitCount / history.WindowSize, 0, 1)
			: 1;

		var scored = new List<ScoredCandidate>(candidates.Length);
		var scoreProgressStep = Math.Max(1, candidates.Length / 25);
		for (var index = 0; index < states.Length; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var state = states[index];
			var score = CalculateScoreBreakdown(
				state.GraphNormalized,
				state.GitNormalized,
				state.RoleNormalized ?? 0.5,
				coverage,
				MissingSignalPolicy,
				gitConfidence);
			scored.Add(new ScoredCandidate(state, score));
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
			.ThenBy(static candidate => candidate.State.Candidate.RelativePath, StringComparer.Ordinal)
			.Select((candidate, index) =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				var state = candidate.State;
				var path = state.Candidate.RelativePath;
				var hasHistory = history.Files.TryGetValue(state.Candidate.FullPath, out var activity);
				var historyReason = hasHistory
					? (ProjectGitHistoryUnavailableReason?)null
					: history.UnavailableFiles.TryGetValue(state.Candidate.FullPath, out var unavailableReason)
						? unavailableReason
						: history.UnavailableReason;
				return new ImportanceRankingEntry(
					state.Candidate.FullPath,
					path,
					index + 1,
					candidate.Score.Score,
					state.Dependents,
					state.Dependencies,
					hasHistory ? activity!.CommitCount : null,
					hasHistory ? activity!.MostRecentCommitPosition : null,
					state.Role,
					state.GraphRaw is not null,
					hasHistory,
					historyReason)
				{
					Confidence = candidate.Score.Confidence,
					MainContribution = candidate.Score.MainContribution,
					ConfidenceLimited = MissingSignalPolicy == ImportanceMissingSignalPolicy.ConfidenceLimited &&
						candidate.Score.Confidence < 1,
					IsCoordinator = state.IsCoordinator
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
			UniqueResolvedFilePairs = graph.EdgeCount,
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

	private static IEnumerable<string> ResolvedTargets(DependencyEdge edge) =>
		edge.DeclarationFiles.Count > 0
			? edge.DeclarationFiles
			: edge.Target is null ? [] : [edge.Target];

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
		var compactRanks = TryCreateCompactRanks(values.Values, cancellationToken);
		var present = values
			.Where(static pair => pair.Value is not null)
			.Select(static pair => (pair.Key, Value: pair.Value!.Value));
		var result = values.Keys.ToDictionary(static path => path, static _ => (double?)null, StringComparer.Ordinal);
		if (compactRanks is not null)
		{
			foreach (var pair in present)
			{
				cancellationToken.ThrowIfCancellationRequested();
				result[pair.Key] = compactRanks[pair.Value];
			}
			return result;
		}
		var ordered = present
			.OrderBy(static pair => pair.Value)
			.ThenBy(static pair => pair.Key, StringComparer.Ordinal)
			.ToArray();
		if (ordered.Length == 0)
			return result;
		var groupCount = 1;
		for (var index = 1; index < ordered.Length; index++)
			if (!ordered[index].Value.Equals(ordered[index - 1].Value))
				groupCount++;
		if (groupCount == 1)
		{
			foreach (var pair in ordered)
			{
				cancellationToken.ThrowIfCancellationRequested();
				result[pair.Key] = 0.5;
			}
			return result;
		}
		var group = 0;
		for (var index = 0; index < ordered.Length; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (index > 0 && !ordered[index].Value.Equals(ordered[index - 1].Value))
				group++;
			result[ordered[index].Key] = (double)group / (groupCount - 1);
		}
		return result;
	}

	private static void NormalizeStateValues(
		IReadOnlyList<CandidateState> states,
		RankingValueKind kind,
		CancellationToken cancellationToken)
	{
		var compactRanks = TryCreateCompactRanks(states.Select(state => RawValue(state, kind)), cancellationToken);
		if (compactRanks is not null)
		{
			foreach (var state in states)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var value = RawValue(state, kind);
				if (value is not null)
					SetNormalizedValue(state, kind, compactRanks[value.Value]);
			}
			return;
		}
		var present = states.Select(state => (State: state, Value: RawValue(state, kind)))
			.Where(static item => item.Value is not null)
			.OrderBy(static item => item.Value!.Value)
			.ThenBy(static item => item.State.Candidate.Id)
			.ToArray();
		if (present.Length == 0)
			return;
		var groupCount = 1;
		for (var index = 1; index < present.Length; index++)
			if (!present[index].Value!.Value.Equals(present[index - 1].Value!.Value))
				groupCount++;
		var group = 0;
		for (var index = 0; index < present.Length; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (index > 0 && !present[index].Value!.Value.Equals(present[index - 1].Value!.Value))
				group++;
			SetNormalizedValue(present[index].State, kind, groupCount == 1
				? 0.5
				: (double)group / (groupCount - 1));
		}
	}

	private static Dictionary<double, double>? TryCreateCompactRanks(
		IEnumerable<double?> values,
		CancellationToken cancellationToken)
	{
		const int maximumCompactRankCount = 256;
		var distinct = new HashSet<double>();
		foreach (var value in values)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (value is not null && distinct.Add(value.Value) && distinct.Count > maximumCompactRankCount)
				return null;
		}
		var ordered = distinct.Order().ToArray();
		var result = new Dictionary<double, double>(ordered.Length);
		for (var index = 0; index < ordered.Length; index++)
			result[ordered[index]] = ordered.Length == 1 ? 0.5 : (double)index / (ordered.Length - 1);
		return result;
	}

	private static void NormalizeRoleValues(
		IReadOnlyList<CandidateState> states,
		CancellationToken cancellationToken)
	{
		var values = states.Select(static state => state.RoleRaw).Distinct().Order().ToArray();
		foreach (var state in states)
		{
			cancellationToken.ThrowIfCancellationRequested();
			state.RoleNormalized = values.Length == 1
				? 0.5
				: (double)Array.BinarySearch(values, state.RoleRaw) / (values.Length - 1);
		}
	}

	private static double? RawValue(CandidateState state, RankingValueKind kind) => kind switch
	{
		RankingValueKind.Graph => state.GraphRaw,
		RankingValueKind.Git => state.GitRaw,
		_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
	};

	private static void SetNormalizedValue(CandidateState state, RankingValueKind kind, double value)
	{
		if (kind == RankingValueKind.Graph)
			state.GraphNormalized = value;
		else if (kind == RankingValueKind.Git)
			state.GitNormalized = value;
		else
			throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
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
		DependencyEngineDiagnostics.RecordGraphBuild();
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
		var outgoingWeights = new Dictionary<int, double>?[paths.Length];
		foreach (var edge in snapshot.Edges)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (edge.Status != ResolutionStatus.Resolved ||
				!nodeByPath.TryGetValue(edge.Source, out var sourceNode))
			{
				continue;
			}
			if (edge.DeclarationFiles.Count == 0 && edge.Target is { } singleTarget)
			{
				if (nodeByPath.TryGetValue(singleTarget, out var targetNode) && sourceNode != targetNode)
				{
					var directWeights = outgoingWeights[sourceNode] ??= [];
					if (!directWeights.TryGetValue(targetNode, out var existing) || existing < 1d)
						directWeights[targetNode] = 1d;
				}
				continue;
			}
			var targets = (edge.DeclarationFiles.Count > 0
					? edge.DeclarationFiles
					: edge.Target is null ? [] : [edge.Target])
				.Distinct(StringComparer.Ordinal)
				.Where(nodeByPath.ContainsKey)
				.Select(path => nodeByPath[path])
				.Where(targetNode => sourceNode != targetNode)
				.Order()
				.ToArray();
			if (targets.Length == 0)
				continue;
			var partWeight = 1d / targets.Length;
			var sourceWeights = outgoingWeights[sourceNode] ??= [];
			foreach (var targetNode in targets)
			{
				if (!sourceWeights.TryGetValue(targetNode, out var existing) || partWeight > existing)
					sourceWeights[targetNode] = partWeight;
			}
		}

		var outgoing = new int[paths.Length][];
		var weights = new double[paths.Length][];
		var weightSums = new double[paths.Length];
		var dependents = new int[paths.Length];
		var filesWithEdges = new bool[paths.Length];
		var edgeCount = 0;
		for (var source = 0; source < paths.Length; source++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var sourceWeights = outgoingWeights[source];
			outgoing[source] = sourceWeights is null ? [] : sourceWeights.Keys.Order().ToArray();
			weights[source] = sourceWeights is null
				? []
				: outgoing[source].Select(target => sourceWeights[target]).ToArray();
			weightSums[source] = weights[source].Sum();
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
			weights,
			weightSums,
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
				var weights = graph.OutgoingWeights[source];
				var totalWeight = graph.OutgoingWeightSums[source];
				var share = damping * rank[source] / totalWeight;
				for (var targetIndex = 0; targetIndex < targets.Length; targetIndex++)
					next[targets[targetIndex]] += share * weights[targetIndex];
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

	/// <summary>File metadata a language adapter writes when a file declares the entry point.</summary>
	private const string EntryPointMarker = "$csharp-entry-point";

	private static bool HasExplicitEntryPointEvidence(FileFacts? facts) =>
		facts is not null &&
		(facts.Aliases.ContainsKey(EntryPointMarker) ||
		 facts.Declarations.Any(static declaration =>
			 declaration.Identity.SymbolKind is SymbolKind.Function or SymbolKind.Module &&
			 IsEntryPointName(declaration.Identity.QualifiedName)));

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
	internal sealed record Candidate(string FullPath, string RelativePath)
	{
		public int Id { get; init; }
	}

	private sealed class CandidateState(Candidate candidate)
	{
		public Candidate Candidate { get; } = candidate;
		public int Dependents { get; set; }
		public int Dependencies { get; set; }
		public ImportanceFileRole Role { get; set; }
		public bool IsCoordinator { get; set; }
		public double? GraphRaw { get; set; }
		public double? GraphNormalized { get; set; }
		public double? GitRaw { get; set; }
		public double? GitNormalized { get; set; }
		public double RoleRaw { get; set; }
		public double? RoleNormalized { get; set; }
	}

	private enum RankingValueKind
	{
		Graph,
		Git
	}

	private sealed record ScoredCandidate(CandidateState State, ImportanceScoreBreakdown Score);
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
