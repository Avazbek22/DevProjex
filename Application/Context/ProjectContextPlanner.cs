using System.Security.Cryptography;
using DevProjex.Application.Selection;

namespace DevProjex.Application.Context;

public sealed class ProjectContextPlanner(ProjectAnalysisService analysisService)
{
	private const string InvalidSelectedPathCode = ProjectSelectionPath.InvalidPathCode;
	private const string MissingSelectedPathCode = "DPX-SELECTION-PATH-MISSING";
	private const string TrackedIndexUnavailableCode = "DPX-GIT-TRACKED-INDEX-UNAVAILABLE";
	private const string TrackedIndexPartialCode = "DPX-GIT-TRACKED-INDEX-PARTIAL";

	public async Task<ProjectContextPlan> BuildAsync(
		ProjectContextRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentNullException.ThrowIfNull(request.Selection);

		var sourceRoot = NormalizeSourceRoot(request.ProjectPath);
		var selection = EnsureResolved(request.Selection);
		var selectedIgnoreOptions = ProjectSelectionAdapter.ToIgnoreOptions(selection);
		var loaded = analysisService.Load(
			new ProjectAnalysisRequest(
				sourceRoot,
				selection.Roots,
				selection.Extensions,
				selectedIgnoreOptions),
			cancellationToken);

		var diagnostics = new List<ContextDiagnostic>();
		var selectedFullPaths = ResolveSelectedPaths(
			loaded.Tree.Root,
			sourceRoot,
			selection.SelectedPaths,
			diagnostics,
			out var explicitSelectionHadMatch);
		var selectsNoEffectivePaths =
			selection.SelectedPaths is { Count: > 0 } &&
			!explicitSelectionHadMatch;
		var includedNodes = selectsNoEffectivePaths
			? []
			: ProjectTreeSelectionProjection.BuildIncludedNodes(
				loaded.Tree.Root,
				selectedFullPaths);
		var includedPathSet = includedNodes
			.Select(static node => node.FullPath)
			.ToHashSet(PathComparer.Default);
		var projectedTree = selectsNoEffectivePaths
			? loaded.Tree.Root with { Children = [] }
			: BuildProjectedTree(loaded.Tree.Root, includedPathSet) ??
			  loaded.Tree.Root with { Children = [] };
		var includedFiles = selectsNoEffectivePaths
			? []
			: ProjectTreeSelectionProjection.BuildOrderedSelectedFilePaths(
				loaded.Tree.Root,
				selectedFullPaths,
				ensureExists: false);
		var effectiveFileSizes = BuildEffectiveFileSizes(
			loaded.Tree.Root,
			loaded.TreeInventory,
			cancellationToken);
		var includedBytes = CalculateIncludedBytes(
			includedFiles,
			effectiveFileSizes,
			cancellationToken);
		var includedFolders = includedNodes
			.Where(static node => node.IsDirectory)
			.Select(static node => node.FullPath)
			.OrderBy(static path => path, PathComparer.Default)
			.ToArray();

		var projectedLoaded = loaded with
		{
			Tree = new BuildTreeResult(
				projectedTree,
				loaded.Tree.RootAccessDenied,
				loaded.Tree.HadAccessDenied,
				includedFiles)
		};
		var analysis = await analysisService
			.BuildReportFromTreeAsync(projectedLoaded, cancellationToken)
			.ConfigureAwait(false);
		AddAnalysisDiagnostics(analysis, diagnostics);

		var unavailableTrackedIndexCount = loaded.UnavailableGitTrackedIndexCount;
		var trackedIndexCount = Math.Max(
			0,
			loaded.DiscoveredGitTrackedIndexCount - unavailableTrackedIndexCount);
		var trackedReady = selection.GitMode != GitFilteringMode.TrackedFilesOnly ||
		                   trackedIndexCount > 0;
		if (!trackedReady)
		{
			diagnostics.Add(new ContextDiagnostic(
				TrackedIndexUnavailableCode,
				ContextDiagnosticSeverity.Error,
				"Tracked Git files mode was requested, but no readable Git index is available.",
				sourceRoot));
		}
		else if (selection.GitMode == GitFilteringMode.TrackedFilesOnly &&
		         unavailableTrackedIndexCount > 0)
		{
			diagnostics.Add(new ContextDiagnostic(
				TrackedIndexPartialCode,
				ContextDiagnosticSeverity.Warning,
				"Some nested Git indexes could not be read; those repository scopes were excluded.",
				sourceRoot));
		}

		var effectiveSelection = selection with
		{
			Roots = loaded.SelectedRootFolders.ToArray(),
			Extensions = loaded.SelectedExtensions.ToArray(),
			SelectedPaths = NormalizeRelativeSelectionForOutput(
				sourceRoot,
				selectedFullPaths)
		};

		return new ProjectContextPlan(
			SourceRoot: sourceRoot,
			Selection: effectiveSelection,
			AvailableRoots: loaded.AvailableRootFolders.OrderBy(static value => value, PathComparer.Default).ToArray(),
			SelectedRoots: loaded.SelectedRootFolders.OrderBy(static value => value, PathComparer.Default).ToArray(),
			AvailableExtensions: loaded.AvailableExtensions.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
			SelectedExtensions: loaded.SelectedExtensions.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
			EffectiveTree: loaded.Tree.Root,
			ProjectedTree: projectedTree,
			SelectedFullPaths: selectedFullPaths,
			IncludedFiles: includedFiles,
			IncludedFolders: includedFolders,
			Analysis: analysis,
			Diagnostics: diagnostics,
			GitReadiness: new ProjectContextGitReadiness(
				selection.GitMode!.Value,
				trackedIndexCount,
				trackedReady,
				unavailableTrackedIndexCount),
			Fingerprint: BuildFingerprint(sourceRoot, effectiveSelection, includedNodes),
			IncludedBytes: includedBytes,
			EffectiveFileSizes: effectiveFileSizes);
	}

	public async Task<ProjectContextPlan> ReprojectSelectionAsync(
		ProjectContextPlan baseline,
		IReadOnlyCollection<string>? selectedPaths,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(baseline);
		var diagnostics = baseline.Diagnostics
			.Where(static diagnostic =>
				diagnostic.Code is not MissingSelectedPathCode and not InvalidSelectedPathCode)
			.ToList();
		var selectedFullPaths = ResolveSelectedPaths(
			baseline.EffectiveTree,
			baseline.SourceRoot,
			selectedPaths,
			diagnostics,
			out var explicitSelectionHadMatch);
		var selectsNoEffectivePaths =
			selectedPaths is { Count: > 0 } &&
			!explicitSelectionHadMatch;
		var includedNodes = selectsNoEffectivePaths
			? []
			: ProjectTreeSelectionProjection.BuildIncludedNodes(
				baseline.EffectiveTree,
				selectedFullPaths);
		var includedPathSet = includedNodes
			.Select(static node => node.FullPath)
			.ToHashSet(PathComparer.Default);
		var projectedTree = selectsNoEffectivePaths
			? baseline.EffectiveTree with { Children = [] }
			: BuildProjectedTree(baseline.EffectiveTree, includedPathSet) ??
			  baseline.EffectiveTree with { Children = [] };
		var includedFiles = selectsNoEffectivePaths
			? []
			: ProjectTreeSelectionProjection.BuildOrderedSelectedFilePaths(
				baseline.EffectiveTree,
				selectedFullPaths,
				ensureExists: false);
		var includedBytes = CalculateIncludedBytes(
			includedFiles,
			baseline.EffectiveFileSizes,
			cancellationToken);
		var includedFolders = includedNodes
			.Where(static node => node.IsDirectory)
			.Select(static node => node.FullPath)
			.OrderBy(static path => path, PathComparer.Default)
			.ToArray();
		var selection = baseline.Selection with
		{
			SelectedPaths = NormalizeRelativeSelectionForOutput(
				baseline.SourceRoot,
				selectedFullPaths)
		};
		var reportInput = new LoadedProjectAnalysisRequest(
			RootPath: baseline.SourceRoot,
			Tree: new BuildTreeResult(
				projectedTree,
				baseline.Analysis.Diagnostics.RootAccessDenied,
				baseline.Analysis.Diagnostics.HadAccessDenied,
				includedFiles),
			AvailableRootFolders: baseline.AvailableRoots,
			AvailableExtensions: baseline.AvailableExtensions,
			SelectedRootFolders: baseline.SelectedRoots,
			SelectedExtensions: baseline.SelectedExtensions,
			SelectedIgnoreOptions: ProjectSelectionAdapter.ToIgnoreOptions(selection),
			RootAccessDenied: baseline.Analysis.Diagnostics.RootAccessDenied,
			HadAccessDenied: baseline.Analysis.Diagnostics.HadAccessDenied,
			KnownLoadingElapsed: TimeSpan.Zero,
			DiscoveredGitTrackedIndexCount:
				baseline.GitReadiness.LoadedTrackedIndexCount +
				baseline.GitReadiness.UnavailableTrackedIndexCount,
			UnavailableGitTrackedIndexCount:
				baseline.GitReadiness.UnavailableTrackedIndexCount);
		var analysis = await analysisService
			.BuildReportFromTreeAsync(reportInput, cancellationToken)
			.ConfigureAwait(false);

		return baseline with
		{
			Selection = selection,
			ProjectedTree = projectedTree,
			SelectedFullPaths = selectedFullPaths,
			IncludedFiles = includedFiles,
			IncludedFolders = includedFolders,
			Analysis = analysis,
			Diagnostics = diagnostics,
			Fingerprint = BuildFingerprint(
				baseline.SourceRoot,
				selection,
				includedNodes),
			IncludedBytes = includedBytes
		};
	}

	private static IReadOnlyDictionary<string, long> BuildEffectiveFileSizes(
		TreeNodeDescriptor root,
		ProjectTreeInventorySnapshot? inventory,
		CancellationToken cancellationToken)
	{
		var sizes = new Dictionary<string, long>(PathComparer.Default);
		var stack = new Stack<TreeNodeDescriptor>();
		stack.Push(root);
		while (stack.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var node = stack.Pop();
			if (!node.IsDirectory)
			{
				sizes[node.FullPath] = -1;
				continue;
			}

			for (var index = node.Children.Count - 1; index >= 0; index--)
				stack.Push(node.Children[index]);
		}

		if (inventory is not null)
		{
			foreach (var entry in inventory.Entries)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!entry.IsDirectory && sizes.ContainsKey(entry.FullPath))
					sizes[entry.FullPath] = Math.Max(0, entry.Length);
			}
		}

		foreach (var path in sizes.Keys.ToArray())
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (sizes[path] >= 0)
				continue;
			try
			{
				sizes[path] = Math.Max(0, new FileInfo(path).Length);
			}
			catch
			{
				sizes[path] = 0;
			}
		}

		return sizes;
	}

	private static long CalculateIncludedBytes(
		IReadOnlyList<string> includedFiles,
		IReadOnlyDictionary<string, long>? effectiveFileSizes,
		CancellationToken cancellationToken)
	{
		if (effectiveFileSizes is null || effectiveFileSizes.Count == 0)
			return 0;

		long total = 0;
		foreach (var path in includedFiles)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!effectiveFileSizes.TryGetValue(path, out var length) || length <= 0)
				continue;
			total = total > long.MaxValue - length
				? long.MaxValue
				: total + length;
		}
		return total;
	}

	private static string NormalizeSourceRoot(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new ProjectContextValidationException("DPX-PROJECT-PATH-REQUIRED", "Project path is required.");

		var normalized = PathUtility.Normalize(path);
		if (!Directory.Exists(normalized))
		{
			throw new ProjectContextValidationException(
				"DPX-PROJECT-NOT-FOUND",
				"Project directory was not found.");
		}

		return normalized;
	}

	private static ProjectSelectionSpec EnsureResolved(ProjectSelectionSpec selection)
	{
		if (selection.GitMode is null || selection.Exclusions is null)
		{
			throw new ProjectContextValidationException(
				"DPX-CLI-PROFILE-UNRESOLVED",
				"Selection profile was not fully resolved.");
		}

		return selection;
	}

	private static IReadOnlySet<string> ResolveSelectedPaths(
		TreeNodeDescriptor root,
		string sourceRoot,
		IReadOnlyCollection<string>? selectedPaths,
		List<ContextDiagnostic> diagnostics,
		out bool explicitSelectionHadMatch)
	{
		explicitSelectionHadMatch = false;
		if (selectedPaths is null || selectedPaths.Count == 0)
			return new HashSet<string>(PathComparer.Default);

		var effectivePathMap = BuildEffectivePathMap(root, sourceRoot);
		var resolved = new HashSet<string>(PathComparer.Default);
		foreach (var input in selectedPaths)
		{
			var relativePath = ProjectSelectionPath.NormalizeRelative(input);
			if (relativePath.Length == 0)
			{
				explicitSelectionHadMatch = true;
				resolved.Add(root.FullPath);
				continue;
			}

			if (effectivePathMap.TryGetValue(relativePath, out var fullPath))
			{
				explicitSelectionHadMatch = true;
				resolved.Add(fullPath);
				continue;
			}

			diagnostics.Add(new ContextDiagnostic(
				MissingSelectedPathCode,
				ContextDiagnosticSeverity.Warning,
				"Selected path is not present in the effective project tree.",
				relativePath));
		}

		return NormalizeSelectionFrontier(root, resolved);
	}

	private static IReadOnlySet<string> NormalizeSelectionFrontier(
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths)
	{
		if (selectedPaths.Contains(root.FullPath))
			return new HashSet<string>(PathComparer.Default);

		if (selectedPaths.Count < 2)
			return selectedPaths;

		var frontier = new HashSet<string>(selectedPaths, PathComparer.Default);
		foreach (var selectedPath in selectedPaths)
		{
			var parent = Directory.GetParent(selectedPath);
			while (parent is not null)
			{
				if (frontier.Contains(parent.FullName))
				{
					frontier.Remove(selectedPath);
					break;
				}

				parent = parent.Parent;
			}
		}

		return frontier;
	}

	private static Dictionary<string, string> BuildEffectivePathMap(
		TreeNodeDescriptor root,
		string sourceRoot)
	{
		var paths = new Dictionary<string, string>(PathComparer.Default);
		var stack = new Stack<TreeNodeDescriptor>();
		stack.Push(root);
		while (stack.Count > 0)
		{
			var node = stack.Pop();
			var relativePath = Path.GetRelativePath(sourceRoot, node.FullPath);
			var key = relativePath == "." ? string.Empty : NormalizePathSeparators(relativePath);
			paths[key] = node.FullPath;

			for (var index = node.Children.Count - 1; index >= 0; index--)
				stack.Push(node.Children[index]);
		}

		return paths;
	}

	private static string NormalizePathSeparators(string path) =>
		path.Replace('\\', '/');

	private static TreeNodeDescriptor? BuildProjectedTree(
		TreeNodeDescriptor node,
		IReadOnlySet<string> includedPaths)
	{
		if (!includedPaths.Contains(node.FullPath))
			return null;

		if (!node.IsDirectory || node.Children.Count == 0)
			return node;

		var children = new List<TreeNodeDescriptor>();
		foreach (var child in node.Children)
		{
			var projected = BuildProjectedTree(child, includedPaths);
			if (projected is not null)
				children.Add(projected);
		}

		return node with { Children = children };
	}

	private static void AddAnalysisDiagnostics(
		ProjectAnalysisReport analysis,
		List<ContextDiagnostic> diagnostics)
	{
		if (analysis.Diagnostics.RootAccessDenied)
		{
			diagnostics.Add(new ContextDiagnostic(
				"DPX-PROJECT-ROOT-ACCESS-DENIED",
				ContextDiagnosticSeverity.Error,
				"The project root cannot be read.",
				analysis.RootPath));
		}
		else if (analysis.Diagnostics.HadAccessDenied)
		{
			diagnostics.Add(new ContextDiagnostic(
				"DPX-PROJECT-PARTIAL-ACCESS",
				ContextDiagnosticSeverity.Warning,
				"Some project paths could not be read."));
		}

		foreach (var warning in analysis.Diagnostics.Warnings)
		{
			diagnostics.Add(new ContextDiagnostic(
				"DPX-PROJECT-SELECTION-WARNING",
				ContextDiagnosticSeverity.Warning,
				warning));
		}
	}

	private static IReadOnlyList<string> NormalizeRelativeSelectionForOutput(
		string sourceRoot,
		IReadOnlySet<string> selectedFullPaths)
	{
		if (selectedFullPaths.Count == 0)
			return [];

		return selectedFullPaths
			.Select(path => Path.GetRelativePath(sourceRoot, path))
			.Select(static path => path == "." ? "." : NormalizePathSeparators(path))
			.OrderBy(static path => path, StringComparer.Ordinal)
			.ToArray();
	}

	private static string BuildFingerprint(
		string sourceRoot,
		ProjectSelectionSpec selection,
		IReadOnlyList<TreeNodeDescriptor> includedNodes)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		Append(selection.GitMode!.Value.ToString());
		foreach (var exclusion in selection.Exclusions!.OrderBy(static value => value))
			Append(exclusion.ToString());
		foreach (var root in selection.Roots ?? [])
			Append("r:" + root);
		foreach (var extension in selection.Extensions ?? [])
			Append("e:" + extension);
		foreach (var node in includedNodes.OrderBy(static node => node.FullPath, PathComparer.Default))
		{
			var relativePath = Path.GetRelativePath(sourceRoot, node.FullPath);
			Append((node.IsDirectory ? "d:" : "f:") + NormalizePathSeparators(relativePath));
		}

		return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

		void Append(string value)
		{
			var bytes = Encoding.UTF8.GetBytes(value);
			hash.AppendData(bytes);
			hash.AppendData([0]);
		}
	}
}
