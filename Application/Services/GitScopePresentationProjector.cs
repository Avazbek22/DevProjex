using DevProjex.Application.Selection;

namespace DevProjex.Application.Services;

public sealed record GitScopePresentationProjection(
	IReadOnlyList<string> AvailableExtensions,
	IgnoreOptionCounts IgnoreOptionCounts,
	IgnoreControllerImpactCounts ControllerImpactCounts)
{
	public static readonly GitScopePresentationProjection Empty = new(
		[],
		IgnoreOptionCounts.Empty,
		IgnoreControllerImpactCounts.Empty);
}

public static class GitScopePresentationProjector
{
	public static GitScopePresentationProjection Build(
		string projectRoot,
		ProjectTreeInventorySnapshot? inventory,
		IReadOnlySet<string> scopedPaths,
		IReadOnlySet<string> selectedRootFolders,
		IReadOnlySet<string> availableRootFolders,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		IgnoreRules effectiveRules,
		CancellationToken cancellationToken = default,
		bool rootSelectionIsExplicit = false,
		bool includeIgnoreImpactCounts = true) =>
		BuildCore(
			projectRoot,
			inventory,
			scopedPaths,
			scopedPaths.Contains,
			getComparisonIdentity: null,
			selectedRootFolders,
			availableRootFolders,
			effectiveExtensionPolicy,
			effectiveRules,
			cancellationToken,
			rootSelectionIsExplicit,
			includeIgnoreImpactCounts);

	public static GitScopePresentationProjection Build(
		string projectRoot,
		ProjectTreeInventorySnapshot? inventory,
		GitScopePathResult scope,
		IReadOnlySet<string> selectedRootFolders,
		IReadOnlySet<string> availableRootFolders,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		IgnoreRules effectiveRules,
		CancellationToken cancellationToken = default,
		bool rootSelectionIsExplicit = false,
		bool includeIgnoreImpactCounts = true) =>
		BuildCore(
			projectRoot,
			inventory,
			scope.IncludedPaths,
			scope.ContainsPath,
			scope.GetComparisonIdentity,
			selectedRootFolders,
			availableRootFolders,
			effectiveExtensionPolicy,
			effectiveRules,
			cancellationToken,
			rootSelectionIsExplicit,
			includeIgnoreImpactCounts);

	private static GitScopePresentationProjection BuildCore(
		string projectRoot,
		ProjectTreeInventorySnapshot? inventory,
		IReadOnlySet<string> scopedPaths,
		Func<string, bool> containsScopedPath,
		Func<string, string?>? getComparisonIdentity,
		IReadOnlySet<string> selectedRootFolders,
		IReadOnlySet<string> availableRootFolders,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		IgnoreRules effectiveRules,
		CancellationToken cancellationToken,
		bool rootSelectionIsExplicit,
		bool includeIgnoreImpactCounts)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		ArgumentNullException.ThrowIfNull(scopedPaths);
		ArgumentNullException.ThrowIfNull(selectedRootFolders);
		ArgumentNullException.ThrowIfNull(availableRootFolders);
		ArgumentNullException.ThrowIfNull(effectiveRules);
		if (inventory is null || inventory.Entries.Count == 0 || scopedPaths.Count == 0)
			return GitScopePresentationProjection.Empty;

		var files = CollectScopedFiles(
			inventory,
			containsScopedPath,
			scopedPaths.Count,
			selectedRootFolders,
			cancellationToken,
			rootSelectionIsExplicit);

		var availableExtensions = CollectAvailableExtensions(
			projectRoot,
			inventory,
			files,
			effectiveRules,
			cancellationToken);
		if (!includeIgnoreImpactCounts)
		{
			return new GitScopePresentationProjection(
				availableExtensions,
				IgnoreOptionCounts.Empty,
				IgnoreControllerImpactCounts.Empty);
		}
		var baseline = EvaluateVisibility(
			projectRoot,
			inventory,
			files,
			effectiveExtensionPolicy,
			effectiveRules,
			cancellationToken);
		var relevantDirectories = BuildRelevantDirectories(
			inventory,
			files,
			effectiveExtensionPolicy,
			cancellationToken);
		var baselineDirectoryVisibility = EvaluateDirectoryVisibility(
			projectRoot,
			inventory,
			relevantDirectories,
			effectiveRules,
			cancellationToken);

		var hiddenFolders = CountDirectoryToggleImpact(
			projectRoot,
			inventory,
			relevantDirectories,
			baselineDirectoryVisibility,
			effectiveRules with { IgnoreHiddenFolders = !effectiveRules.IgnoreHiddenFolders },
			static entry => entry.IsHidden,
			cancellationToken);
		var dotFolders = CountDirectoryToggleImpact(
			projectRoot,
			inventory,
			relevantDirectories,
			baselineDirectoryVisibility,
			effectiveRules with { IgnoreDotFolders = !effectiveRules.IgnoreDotFolders },
			static entry => IgnoreRuleSemantics.IsDotName(entry.Name),
			cancellationToken,
			VisibilityVariant.DotFolders);
		var hiddenFiles = CountFileToggleImpact(
			projectRoot,
			inventory,
			files,
			effectiveExtensionPolicy,
			effectiveRules with { IgnoreHiddenFiles = !effectiveRules.IgnoreHiddenFiles },
			baseline,
			static entry => entry.IsHidden,
			cancellationToken);
		var dotFiles = CountFileToggleImpact(
			projectRoot,
			inventory,
			files,
			effectiveExtensionPolicy,
			effectiveRules with { IgnoreDotFiles = !effectiveRules.IgnoreDotFiles },
			baseline,
			static entry => IgnoreRuleSemantics.IsDotName(entry.Name),
			cancellationToken);
		var emptyFiles = CountFileToggleImpact(
			projectRoot,
			inventory,
			files,
			effectiveExtensionPolicy,
			effectiveRules with { IgnoreEmptyFiles = !effectiveRules.IgnoreEmptyFiles },
			baseline,
			static entry => entry.Length == 0,
			cancellationToken);
		var extensionlessFiles = CountFileToggleImpact(
			projectRoot,
			inventory,
			files,
			effectiveExtensionPolicy,
			effectiveRules with { IgnoreExtensionlessFiles = !effectiveRules.IgnoreExtensionlessFiles },
			baseline,
			static entry => IgnoreRuleSemantics.IsExtensionlessFileName(entry.Name),
			cancellationToken);

		var smartRules = ToggleSmartIgnore(effectiveRules);
		var smartVisibility = EvaluateVisibility(
			projectRoot,
			inventory,
			files,
			effectiveExtensionPolicy,
			smartRules,
			cancellationToken);
		var smartImpact = CountChangedFiles(baseline, smartVisibility, cancellationToken);

		var counts = PreserveActiveOwnerEvidence(
			projectRoot,
			inventory,
			files,
			scopedPaths,
			getComparisonIdentity,
			selectedRootFolders,
			effectiveRules,
			new IgnoreOptionCounts(
				HiddenFolders: hiddenFolders,
				HiddenFiles: hiddenFiles,
				DotFolders: dotFolders,
				DotFiles: dotFiles,
				EmptyFolders: 0,
				ExtensionlessFiles: extensionlessFiles,
				EmptyFiles: emptyFiles),
			cancellationToken,
			rootSelectionIsExplicit,
			out var activeSmartEvidence);

		return new GitScopePresentationProjection(
			availableExtensions,
			counts,
			new IgnoreControllerImpactCounts(
				SmartIgnore: Math.Max(smartImpact, activeSmartEvidence)));
	}

	private static List<ScopedFile> CollectScopedFiles(
		ProjectTreeInventorySnapshot inventory,
		Func<string, bool> containsScopedPath,
		int scopedPathCount,
		IReadOnlySet<string> selectedRootFolders,
		CancellationToken cancellationToken,
		bool rootSelectionIsExplicit)
	{
		var files = new List<ScopedFile>(Math.Min(scopedPathCount, inventory.Entries.Count));
		for (var index = 1; index < inventory.Entries.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ref readonly var entry = ref inventory.GetEntryRef(index);
			if (entry.IsDirectory || !containsScopedPath(entry.FullPath))
				continue;
			if (!IsInsideSelectedRoot(
				    inventory,
				    index,
				    selectedRootFolders,
				    rootSelectionIsExplicit))
				continue;

			files.Add(new ScopedFile(index, CollectAncestorIndexes(inventory, entry.ParentIndex)));
		}

		return files;
	}

	private static bool IsInsideSelectedRoot(
		ProjectTreeInventorySnapshot inventory,
		int fileIndex,
		IReadOnlySet<string> selectedRootFolders,
		bool rootSelectionIsExplicit)
	{
		var currentIndex = inventory.GetEntryRef(fileIndex).ParentIndex;
		if (currentIndex <= 0)
			return true;

		while (inventory.GetEntryRef(currentIndex).ParentIndex > 0)
			currentIndex = inventory.GetEntryRef(currentIndex).ParentIndex;
		var rootName = inventory.GetEntryRef(currentIndex).Name;
		return !rootSelectionIsExplicit || selectedRootFolders.Contains(rootName);
	}

	private static int[] CollectAncestorIndexes(ProjectTreeInventorySnapshot inventory, int parentIndex)
	{
		var count = 0;
		for (var current = parentIndex; current > 0; current = inventory.GetEntryRef(current).ParentIndex)
			count++;
		if (count == 0)
			return [];

		var ancestors = new int[count];
		var target = count - 1;
		for (var current = parentIndex; current > 0; current = inventory.GetEntryRef(current).ParentIndex)
			ancestors[target--] = current;
		return ancestors;
	}

	private static IReadOnlyList<string> CollectAvailableExtensions(
		string projectRoot,
		ProjectTreeInventorySnapshot inventory,
		IReadOnlyList<ScopedFile> files,
		IgnoreRules effectiveRules,
		CancellationToken cancellationToken)
	{
		var visible = EvaluateVisibility(
			projectRoot,
			inventory,
			files,
			effectiveExtensionPolicy: null,
			effectiveRules,
			cancellationToken,
			applyExtensionFilter: false);
		var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		for (var index = 0; index < files.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!visible[index])
				continue;
			ref readonly var entry = ref inventory.GetEntryRef(files[index].EntryIndex);
			if (IgnoreRuleSemantics.IsExtensionlessFileName(entry.Name))
				continue;
			var extension = Path.GetExtension(entry.Name.AsSpan());
			if (!extension.IsEmpty)
				ExtensionOptionProjection.AddCanonicalExtension(extensions, extension);
		}

		var ordered = extensions.ToArray();
		CancellationAwareSort.Sort(ordered, StringComparer.OrdinalIgnoreCase, cancellationToken);
		return ordered;
	}

	private static bool[] EvaluateVisibility(
		string projectRoot,
		ProjectTreeInventorySnapshot inventory,
		IReadOnlyList<ScopedFile> files,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		IgnoreRules rules,
		CancellationToken cancellationToken,
		bool applyExtensionFilter = true,
		VisibilityVariant variant = VisibilityVariant.Default)
	{
		var visibility = new bool[files.Count];
		var gitIgnore = rules.CreateGitIgnoreScanContext(
			projectRoot,
			inventory.DiscoveredGitIgnoreMatchers,
			inventory.DiscoveredGitTrackedPathIndexes);
		for (var index = 0; index < files.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var scopedFile = files[index];
			var ancestorsVisible = true;
			foreach (var ancestorIndex in scopedFile.AncestorIndexes)
			{
				ref readonly var directory = ref inventory.GetEntryRef(ancestorIndex);
				var directoryRules = variant == VisibilityVariant.DotFolders &&
				                     IgnoreRuleSemantics.IsDotName(directory.Name)
					? rules with { IgnoreHiddenFolders = false }
					: rules;
				var gitEvaluation = rules.IsGitIgnoreTraversalEnabled
					? gitIgnore.Evaluate(
						directory.FullPath,
						directory.RelativePath,
						isDirectory: true,
						directory.Name)
					: IgnoreRules.GitIgnoreEvaluation.NotIgnored;
				if (IgnoreDecisionEngine.EvaluateDirectory(
						directory.FullPath,
						directory.Name,
						directory.IsHidden,
						directoryRules,
						gitEvaluation).IsIgnored)
				{
					ancestorsVisible = false;
					break;
				}
			}
			if (!ancestorsVisible)
				continue;

			ref readonly var file = ref inventory.GetEntryRef(scopedFile.EntryIndex);
			if (applyExtensionFilter && !AllowsExtension(file.Name, effectiveExtensionPolicy))
				continue;
			var fileGitEvaluation = rules.IsGitIgnoreTraversalEnabled
				? gitIgnore.Evaluate(file.FullPath, file.RelativePath, isDirectory: false, file.Name)
				: IgnoreRules.GitIgnoreEvaluation.NotIgnored;
			var smartIgnoreScopePath = file.ParentIndex >= 0
				? inventory.GetEntryRef(file.ParentIndex).FullPath
				: projectRoot;
			visibility[index] = !IgnoreDecisionEngine.EvaluateFile(
				file.FullPath,
				file.Name,
				file.IsHidden,
				file.Length,
				rules,
				rules.ShouldApplySmartIgnore(smartIgnoreScopePath, isDirectory: true),
				fileGitEvaluation).IsIgnored;
		}

		return visibility;
	}

	private static bool AllowsExtension(
		string fileName,
		IExtensionInclusionPolicy? effectiveExtensionPolicy)
	{
		if (IgnoreRuleSemantics.IsExtensionlessFileName(fileName))
			return true;
		var extension = Path.GetExtension(fileName.AsSpan());
		return !extension.IsEmpty &&
		       (effectiveExtensionPolicy?.AllowsExtension(extension) ?? true);
	}

	private static int CountFileToggleImpact(
		string projectRoot,
		ProjectTreeInventorySnapshot inventory,
		IReadOnlyList<ScopedFile> files,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		IgnoreRules toggledRules,
		IReadOnlyList<bool> baseline,
		Func<ProjectTreeInventoryEntry, bool> matches,
		CancellationToken cancellationToken)
	{
		var toggled = EvaluateVisibility(
			projectRoot,
			inventory,
			files,
			effectiveExtensionPolicy,
			toggledRules,
			cancellationToken);
		var count = 0;
		for (var index = 0; index < files.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (baseline[index] == toggled[index])
				continue;
			if (matches(inventory.GetEntry(files[index].EntryIndex)))
				count++;
		}

		return count;
	}

	private static int CountDirectoryToggleImpact(
		string projectRoot,
		ProjectTreeInventorySnapshot inventory,
		IReadOnlyList<bool> relevantDirectories,
		IReadOnlyList<bool> baseline,
		IgnoreRules toggledRules,
		Func<ProjectTreeInventoryEntry, bool> matches,
		CancellationToken cancellationToken,
		VisibilityVariant variant = VisibilityVariant.Default)
	{
		var toggled = EvaluateDirectoryVisibility(
			projectRoot,
			inventory,
			relevantDirectories,
			toggledRules,
			cancellationToken,
			variant: variant);
		var affected = new HashSet<string>(StringComparer.Ordinal);
		for (var index = 1; index < inventory.Entries.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (baseline[index] == toggled[index])
				continue;
			ref readonly var directory = ref inventory.GetEntryRef(index);
			var parentIsVisible = directory.ParentIndex <= 0 || baseline[directory.ParentIndex];
			if (parentIsVisible && matches(directory))
				affected.Add(directory.FullPath);
		}

		return affected.Count;
	}

	private static bool[] BuildRelevantDirectories(
		ProjectTreeInventorySnapshot inventory,
		IReadOnlyList<ScopedFile> files,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		CancellationToken cancellationToken)
	{
		var relevant = new bool[inventory.Entries.Count];
		foreach (var file in files)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ref readonly var entry = ref inventory.GetEntryRef(file.EntryIndex);
			if (!AllowsExtension(entry.Name, effectiveExtensionPolicy))
				continue;
			foreach (var ancestorIndex in file.AncestorIndexes)
				relevant[ancestorIndex] = true;
		}
		return relevant;
	}

	private static bool[] EvaluateDirectoryVisibility(
		string projectRoot,
		ProjectTreeInventorySnapshot inventory,
		IReadOnlyList<bool> relevantDirectories,
		IgnoreRules rules,
		CancellationToken cancellationToken,
		VisibilityVariant variant = VisibilityVariant.Default)
	{
		var visibility = new bool[inventory.Entries.Count];
		visibility[0] = true;
		var gitIgnore = rules.CreateGitIgnoreScanContext(
			projectRoot,
			inventory.DiscoveredGitIgnoreMatchers,
			inventory.DiscoveredGitTrackedPathIndexes);
		for (var index = 1; index < inventory.Entries.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!relevantDirectories[index])
				continue;
			ref readonly var directory = ref inventory.GetEntryRef(index);
			if (directory.ParentIndex > 0 && !visibility[directory.ParentIndex])
				continue;

			var directoryRules = variant == VisibilityVariant.DotFolders &&
			                     IgnoreRuleSemantics.IsDotName(directory.Name)
				? rules with { IgnoreHiddenFolders = false }
				: rules;
			var gitEvaluation = rules.IsGitIgnoreTraversalEnabled
				? gitIgnore.Evaluate(
					directory.FullPath,
					directory.RelativePath,
					isDirectory: true,
					directory.Name)
				: IgnoreRules.GitIgnoreEvaluation.NotIgnored;
			visibility[index] = !IgnoreDecisionEngine.EvaluateDirectory(
				directory.FullPath,
				directory.Name,
				directory.IsHidden,
				directoryRules,
				gitEvaluation).IsIgnored;
		}

		return visibility;
	}

	private static IgnoreRules ToggleSmartIgnore(IgnoreRules rules)
	{
		if (rules.UseSmartIgnore)
			return rules with { UseSmartIgnore = false };

		return rules with
		{
			UseSmartIgnore = true,
			SmartIgnoredFolders = rules.SmartIgnoreCandidateFolders ?? rules.SmartIgnoredFolders,
			SmartIgnoredFiles = rules.SmartIgnoreCandidateFiles ?? rules.SmartIgnoredFiles,
			SmartIgnoreScopeRoots = rules.SmartIgnoreCandidateScopeRoots,
			ScopedSmartIgnoreMatchers = rules.ScopedSmartIgnoreCandidateMatchers,
			SmartArtifactIgnoreMatcher = rules.SmartArtifactIgnoreCandidateMatcher
		};
	}

	private static int CountChangedFiles(
		IReadOnlyList<bool> baseline,
		IReadOnlyList<bool> toggled,
		CancellationToken cancellationToken)
	{
		var count = 0;
		for (var index = 0; index < baseline.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (baseline[index] != toggled[index])
				count++;
		}
		return count;
	}

	private static IgnoreOptionCounts PreserveActiveOwnerEvidence(
		string projectRoot,
		ProjectTreeInventorySnapshot inventory,
		IReadOnlyList<ScopedFile> files,
		IReadOnlySet<string> scopedPaths,
		Func<string, string?>? getComparisonIdentity,
		IReadOnlySet<string> selectedRootFolders,
		IgnoreRules rules,
		IgnoreOptionCounts counts,
		CancellationToken cancellationToken,
		bool rootSelectionIsExplicit,
		out int smartIgnoreEvidence)
	{
		var ownerPaths = new Dictionary<IgnoreDecisionOwner, HashSet<string>>();
		var inventoriedFiles = new HashSet<string>(StringComparer.Ordinal);
		var inventoriedPathIdentities = getComparisonIdentity is null
			? null
			: new HashSet<string>(StringComparer.Ordinal);
		var gitIgnore = rules.CreateGitIgnoreScanContext(
			projectRoot,
			inventory.DiscoveredGitIgnoreMatchers,
			inventory.DiscoveredGitTrackedPathIndexes);
		foreach (var scopedFile in files)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ref readonly var file = ref inventory.GetEntryRef(scopedFile.EntryIndex);
			inventoriedFiles.Add(file.FullPath);
			if (getComparisonIdentity?.Invoke(file.FullPath) is { } identity)
				inventoriedPathIdentities!.Add(identity);

			var blocked = false;
			foreach (var ancestorIndex in scopedFile.AncestorIndexes)
			{
				ref readonly var directory = ref inventory.GetEntryRef(ancestorIndex);
				var gitEvaluation = rules.IsGitIgnoreTraversalEnabled
					? gitIgnore.Evaluate(
						directory.FullPath,
						directory.RelativePath,
						isDirectory: true,
						directory.Name)
					: IgnoreRules.GitIgnoreEvaluation.NotIgnored;
				var decision = IgnoreDecisionEngine.EvaluateDirectory(
					directory.FullPath,
					directory.Name,
					directory.IsHidden,
					rules,
					gitEvaluation);
				if (!decision.IsIgnored)
					continue;

				AddOwner(ownerPaths, decision.Owner, directory.FullPath);
				blocked = true;
				break;
			}
			if (blocked)
				continue;

			var fileGitEvaluation = rules.IsGitIgnoreTraversalEnabled
				? gitIgnore.Evaluate(file.FullPath, file.RelativePath, isDirectory: false, file.Name)
				: IgnoreRules.GitIgnoreEvaluation.NotIgnored;
			var fileDecision = IgnoreDecisionEngine.EvaluateFile(
				file.FullPath,
				file.Name,
				file.IsHidden,
				file.Length,
				rules,
				rules.ShouldApplySmartIgnore(
					inventory.GetEntryRef(file.ParentIndex).FullPath,
					isDirectory: true),
				fileGitEvaluation);
			if (fileDecision.IsIgnored)
				AddOwner(ownerPaths, fileDecision.Owner, file.FullPath);
		}

		foreach (var scopedPath in scopedPaths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var matchesInventoriedIdentity =
				getComparisonIdentity?.Invoke(scopedPath) is { } identity &&
				inventoriedPathIdentities!.Contains(identity);
			if (inventoriedFiles.Contains(scopedPath) ||
			    matchesInventoriedIdentity ||
			    !IsInsideSelectedRoot(
				    projectRoot,
				    scopedPath,
				    selectedRootFolders,
				    rootSelectionIsExplicit))
				continue;

			AddFileSystemOwnerEvidence(
				projectRoot,
				scopedPath,
				rules,
				ownerPaths);
		}

		smartIgnoreEvidence = GetOwnerCount(ownerPaths, IgnoreDecisionOwner.SmartIgnore);
		return counts with
		{
			HiddenFolders = Math.Max(
				counts.HiddenFolders,
				GetOwnerCount(ownerPaths, IgnoreDecisionOwner.HiddenFolders)),
			HiddenFiles = Math.Max(
				counts.HiddenFiles,
				GetOwnerCount(ownerPaths, IgnoreDecisionOwner.HiddenFiles)),
			DotFolders = Math.Max(
				counts.DotFolders,
				GetOwnerCount(ownerPaths, IgnoreDecisionOwner.DotFolders)),
			DotFiles = Math.Max(
				counts.DotFiles,
				GetOwnerCount(ownerPaths, IgnoreDecisionOwner.DotFiles)),
			EmptyFiles = Math.Max(
				counts.EmptyFiles,
				GetOwnerCount(ownerPaths, IgnoreDecisionOwner.EmptyFiles)),
			ExtensionlessFiles = Math.Max(
				counts.ExtensionlessFiles,
				GetOwnerCount(ownerPaths, IgnoreDecisionOwner.ExtensionlessFiles))
		};
	}

	private static void AddFileSystemOwnerEvidence(
		string projectRoot,
		string fullPath,
		IgnoreRules rules,
		Dictionary<IgnoreDecisionOwner, HashSet<string>> ownerPaths)
	{
		if (!File.Exists(fullPath))
			return;
		var fileName = Path.GetFileName(fullPath);

		var relativePath = Path.GetRelativePath(projectRoot, fullPath);
		var parentRelativePath = Path.GetDirectoryName(relativePath);
		var currentDirectory = projectRoot;
		if (!string.IsNullOrEmpty(parentRelativePath))
		{
			foreach (var segment in parentRelativePath.Split(
				         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
				         StringSplitOptions.RemoveEmptyEntries))
			{
				currentDirectory = Path.Combine(currentDirectory, segment);
				var decision = IgnoreDecisionEngine.EvaluateDirectory(
					currentDirectory,
					segment,
					TryGetHidden(currentDirectory),
					rules,
					IgnoreRules.GitIgnoreEvaluation.NotIgnored);
				if (!decision.IsIgnored)
					continue;

				AddOwner(ownerPaths, decision.Owner, currentDirectory);
				return;
			}
		}

		var fileDecision = IgnoreDecisionEngine.EvaluateFile(
			fullPath,
			fileName,
			TryGetHidden(fullPath),
			TryGetLength(fullPath),
			rules,
			rules.ShouldApplySmartIgnore(
				Path.GetDirectoryName(fullPath) ?? projectRoot,
				isDirectory: true),
			IgnoreRules.GitIgnoreEvaluation.NotIgnored);
		if (fileDecision.IsIgnored)
			AddOwner(ownerPaths, fileDecision.Owner, fullPath);
	}

	private static void AddOwner(
		Dictionary<IgnoreDecisionOwner, HashSet<string>> ownerPaths,
		IgnoreDecisionOwner owner,
		string path)
	{
		if (owner == IgnoreDecisionOwner.None)
			return;
		if (!ownerPaths.TryGetValue(owner, out var paths))
		{
			paths = new HashSet<string>(StringComparer.Ordinal);
			ownerPaths.Add(owner, paths);
		}
		paths.Add(path);
	}

	private static int GetOwnerCount(
		IReadOnlyDictionary<IgnoreDecisionOwner, HashSet<string>> ownerPaths,
		IgnoreDecisionOwner owner) =>
		ownerPaths.TryGetValue(owner, out var paths) ? paths.Count : 0;

	private static bool TryGetHidden(string path)
	{
		try
		{
			return File.GetAttributes(path).HasFlag(FileAttributes.Hidden);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static bool IsInsideSelectedRoot(
		string rootPath,
		string path,
		IReadOnlySet<string> selectedRootFolders,
		bool rootSelectionIsExplicit)
	{
		try
		{
			var relativePath = Path.GetRelativePath(rootPath, path);
			if (Path.IsPathRooted(relativePath) ||
			    relativePath == ".." ||
			    relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
			    relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
			{
				return false;
			}

			var separatorIndex = relativePath.IndexOfAny(
				[Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
			if (separatorIndex < 0)
				return true;
			var rootName = relativePath[..separatorIndex];
			return !rootSelectionIsExplicit || selectedRootFolders.Contains(rootName);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
		{
			return false;
		}
	}

	private static long TryGetLength(string path)
	{
		try
		{
			return new FileInfo(path).Length;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return -1;
		}
	}

	private sealed record ScopedFile(int EntryIndex, int[] AncestorIndexes);

	private enum VisibilityVariant
	{
		Default,
		DotFolders
	}
}
