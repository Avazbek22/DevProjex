using System.Buffers;

namespace DevProjex.Infrastructure.FileSystem;

public sealed partial class FileSystemScanner : IFileSystemScanner, IFileSystemScannerAdvanced, IFileSystemScannerEffectiveEmptyFolderCounter, IFileSystemScannerEffectiveIgnoreCountsProvider, IFileSystemScannerIgnoreSectionSnapshotProvider, IFileSystemScannerExtensionPolicySnapshotProvider, IFileSystemScannerRootSelectionSnapshotProvider
{
	public bool CanReadRoot(string rootPath)
	{
		try
		{
			_ = Directory.EnumerateFileSystemEntries(rootPath).GetEnumerator().MoveNext();
			return true;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
		catch
		{
			return true;
		}
	}

	public ScanResult<HashSet<string>> GetExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
	{
		var scan = ScanExtensionsCore(
			rootPath,
			rules,
			collectIgnoreOptionCounts: false,
			includeRootDirectoryInCounts: false,
			cancellationToken);
		return new ScanResult<HashSet<string>>(scan.Value.Extensions, scan.RootAccessDenied, scan.HadAccessDenied);
	}

	public ScanResult<ExtensionsScanData> GetExtensionsWithIgnoreOptionCounts(
		string rootPath,
		IgnoreRules rules,
		CancellationToken cancellationToken = default)
	{
		return ScanExtensionsCore(
			rootPath,
			rules,
			collectIgnoreOptionCounts: true,
			includeRootDirectoryInCounts: true,
			cancellationToken);
	}

	public ScanResult<HashSet<string>> GetRootFileExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
	{
		var scan = ScanRootFilesCore(rootPath, rules, collectIgnoreOptionCounts: false, cancellationToken);
		return new ScanResult<HashSet<string>>(scan.Value.Extensions, scan.RootAccessDenied, scan.HadAccessDenied);
	}

	public ScanResult<ExtensionsScanData> GetRootFileExtensionsWithIgnoreOptionCounts(
		string rootPath,
		IgnoreRules rules,
		CancellationToken cancellationToken = default)
	{
		return ScanRootFilesCore(rootPath, rules, collectIgnoreOptionCounts: true, cancellationToken);
	}

	public ScanResult<int> GetEffectiveEmptyFolderCount(
		string rootPath,
		IReadOnlySet<string> allowedExtensions,
		IgnoreRules rules,
		CancellationToken cancellationToken = default)
	{
		return ScanEffectiveEmptyFolderCountCore(rootPath, allowedExtensions, rules, cancellationToken);
	}

	public ScanResult<IgnoreOptionCounts> GetEffectiveIgnoreOptionCounts(
		string rootPath,
		IReadOnlySet<string> allowedExtensions,
		IgnoreRules rules,
		CancellationToken cancellationToken = default)
	{
		return ScanEffectiveIgnoreOptionCountsCore(rootPath, allowedExtensions, rules, cancellationToken);
	}

	public ScanResult<IgnoreOptionCounts> GetEffectiveRootFileIgnoreOptionCounts(
		string rootPath,
		IReadOnlySet<string> allowedExtensions,
		IgnoreRules rules,
		CancellationToken cancellationToken = default)
	{
		return ScanEffectiveRootFileIgnoreOptionCountsCore(rootPath, allowedExtensions, rules, cancellationToken);
	}

	public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
		string rootPath,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IReadOnlySet<string>? effectiveAllowedExtensions,
		CancellationToken cancellationToken = default)
	{
		return GetIgnoreSectionSnapshot(
			rootPath,
			extensionDiscoveryRules,
			effectiveRules,
			CreateExtensionPolicy(effectiveAllowedExtensions),
			cancellationToken);
	}

	public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
		string rootPath,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		CancellationToken cancellationToken = default)
	{
		return ScanIgnoreSectionSnapshotCore(
			rootPath,
			extensionDiscoveryRules,
			effectiveRules,
			effectiveExtensionPolicy,
			includeRootDirectoryInRawCounts: true,
			cancellationToken);
	}

	public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
		string rootPath,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IReadOnlySet<string>? effectiveAllowedExtensions,
		CancellationToken cancellationToken = default)
	{
		return GetRootFileIgnoreSectionSnapshot(
			rootPath,
			extensionDiscoveryRules,
			effectiveRules,
			CreateExtensionPolicy(effectiveAllowedExtensions),
			cancellationToken);
	}

	public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
		string rootPath,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		CancellationToken cancellationToken = default)
	{
		return ScanRootFileIgnoreSectionSnapshotCore(
			rootPath,
			extensionDiscoveryRules,
			effectiveRules,
			effectiveExtensionPolicy,
			cancellationToken);
	}

	public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshotForRootSelection(
		string rootPath,
		IReadOnlyCollection<string> selectedRootFolders,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		bool includeDirectoryToggleProbeRoots = false,
		CancellationToken cancellationToken = default,
		bool includeControllerImpactProbeRoots = false)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var scanPlan = BuildRootSelectionScanPlan(
			rootPath,
			selectedRootFolders,
			effectiveRules,
			includeDirectoryToggleProbeRoots,
			includeControllerImpactProbeRoots,
			cancellationToken);

		var aggregatedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var rawCounts = IgnoreOptionCounts.Empty;
		var effectiveCounts = IgnoreOptionCounts.Empty;
		var controllerImpactCounts = IgnoreControllerImpactCounts.Empty;
		var rootAccessDenied = scanPlan.RootAccessDenied ? 1 : 0;
		var hadAccessDenied = scanPlan.HadAccessDenied ? 1 : 0;
		var mergeLock = new object();

		var rootFileSnapshot = ScanRootFileIgnoreSectionSnapshotCore(
			rootPath,
			extensionDiscoveryRules,
			effectiveRules,
			effectiveExtensionPolicy,
			cancellationToken);
		aggregatedExtensions.UnionWith(rootFileSnapshot.Value.Extensions);
		rawCounts = rawCounts.Add(rootFileSnapshot.Value.RawIgnoreOptionCounts);
		effectiveCounts = effectiveCounts.Add(rootFileSnapshot.Value.EffectiveIgnoreOptionCounts);
		controllerImpactCounts = controllerImpactCounts.Add(rootFileSnapshot.Value.ControllerImpactCounts);
		if (rootFileSnapshot.RootAccessDenied)
			Interlocked.Exchange(ref rootAccessDenied, 1);
		if (rootFileSnapshot.HadAccessDenied)
			Interlocked.Exchange(ref hadAccessDenied, 1);

		if (scanPlan.SelectedRootPaths.Count > 0)
		{
			var parallelOptions = ScanParallelismPolicy.CreateOptions(cancellationToken);
			Parallel.ForEach(
				scanPlan.SelectedRootPaths,
				parallelOptions,
				() => new IgnoreSectionSnapshotLocalState(),
				(folderPath, _, localState) =>
				{
					parallelOptions.CancellationToken.ThrowIfCancellationRequested();

					var snapshot = ScanIgnoreSectionSnapshotCore(
						folderPath,
						extensionDiscoveryRules,
						effectiveRules,
						effectiveExtensionPolicy,
						includeRootDirectoryInRawCounts: true,
						parallelOptions.CancellationToken);

					localState.Extensions.UnionWith(snapshot.Value.Extensions);
					localState.RawCounts.Add(snapshot.Value.RawIgnoreOptionCounts);
					localState.EffectiveCounts = localState.EffectiveCounts.Add(snapshot.Value.EffectiveIgnoreOptionCounts);
					localState.ControllerImpactCounts =
						localState.ControllerImpactCounts.Add(snapshot.Value.ControllerImpactCounts);

					if (snapshot.RootAccessDenied)
						Interlocked.Exchange(ref rootAccessDenied, 1);
					if (snapshot.HadAccessDenied)
						Interlocked.Exchange(ref hadAccessDenied, 1);

					return localState;
				},
				localState =>
				{
					if (localState.Extensions.Count == 0 &&
					    localState.RawCounts.IsEmpty &&
					    localState.EffectiveCounts == IgnoreOptionCounts.Empty &&
					    localState.ControllerImpactCounts == IgnoreControllerImpactCounts.Empty)
					{
						return;
					}

					lock (mergeLock)
					{
						if (localState.Extensions.Count > 0)
							aggregatedExtensions.UnionWith(localState.Extensions);
						rawCounts = rawCounts.Add(localState.RawCounts.ToImmutable());
						effectiveCounts = effectiveCounts.Add(localState.EffectiveCounts);
						controllerImpactCounts = controllerImpactCounts.Add(localState.ControllerImpactCounts);
					}
				});
		}

		if (includeDirectoryToggleProbeRoots && scanPlan.DirectoryToggleCandidates.Count > 0)
		{
			var rootCandidateCounts = CountRootDirectoryToggleCandidates(
				scanPlan.DirectoryToggleCandidates,
				effectiveRules,
				cancellationToken);
			effectiveCounts = effectiveCounts.Add(rootCandidateCounts.Value);
			if (rootCandidateCounts.RootAccessDenied)
				Interlocked.Exchange(ref rootAccessDenied, 1);
			if (rootCandidateCounts.HadAccessDenied)
				Interlocked.Exchange(ref hadAccessDenied, 1);
		}

		if (scanPlan.ControllerImpactCandidates.Count > 0)
		{
			var rootControllerImpactCounts = CountRootDirectoryControllerImpactCandidates(
				scanPlan.ControllerImpactCandidates,
				effectiveRules,
				effectiveExtensionPolicy,
				cancellationToken);
			controllerImpactCounts = controllerImpactCounts.Add(rootControllerImpactCounts.Value);
			if (rootControllerImpactCounts.RootAccessDenied)
				Interlocked.Exchange(ref rootAccessDenied, 1);
			if (rootControllerImpactCounts.HadAccessDenied)
				Interlocked.Exchange(ref hadAccessDenied, 1);
		}

		return new ScanResult<IgnoreSectionScanData>(
			new IgnoreSectionScanData(aggregatedExtensions, rawCounts, effectiveCounts, controllerImpactCounts),
			rootAccessDenied == 1,
			hadAccessDenied == 1);
	}

	public ScanResult<List<string>> GetRootFolderNames(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var names = new List<string>();
		var useGitIgnore = rules.UseGitIgnore;
		var gitIgnoreContext = rules.CreateGitIgnoreScanContext(rootPath);

		if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
			return new ScanResult<List<string>>(names, false, false);

		try
		{
			foreach (var dir in FileSystemEntryEnumerator.EnumerateDirectories(rootPath))
			{
				cancellationToken.ThrowIfCancellationRequested();

				var dirName = dir.Name;
				var directoryGitIgnore = useGitIgnore
					? gitIgnoreContext.Evaluate(dir.FullPath, dir.RelativePath, isDirectory: true, dirName)
					: IgnoreRules.GitIgnoreEvaluation.NotIgnored;
				if (ShouldSkipDirectoryByName(dirName, dir.FullPath, dir.IsHidden, rules, directoryGitIgnore))
					continue;

				names.Add(dirName);
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (UnauthorizedAccessException)
		{
			return new ScanResult<List<string>>(names, true, true);
		}
		catch
		{
			return new ScanResult<List<string>>(names, false, false);
		}

		names.Sort(StringComparer.OrdinalIgnoreCase);
		return new ScanResult<List<string>>(names, false, false);
	}

	/// <summary>
	/// Optimized version that avoids DirectoryInfo allocation when possible.
	/// Only creates DirectoryInfo when checking Hidden attribute.
	/// </summary>
	private static bool ShouldSkipDirectoryByName(
		string name,
		string fullPath,
		IgnoreRules rules,
		in IgnoreRules.GitIgnoreEvaluation gitIgnoreEvaluation)
	{
		return ShouldSkipDirectoryByName(
			name,
			fullPath,
			HasHiddenAttribute(fullPath),
			rules,
			gitIgnoreEvaluation);
	}

	/// <summary>
	/// Entry-based scans already know whether a directory is hidden.
	/// Reusing that metadata avoids an extra stat call for every candidate folder.
	/// </summary>
	private static bool ShouldSkipDirectoryByName(
		string name,
		string fullPath,
		bool isHidden,
		IgnoreRules rules,
		in IgnoreRules.GitIgnoreEvaluation gitIgnoreEvaluation)
	{
		if (gitIgnoreEvaluation.IsIgnored)
		{
			if (!gitIgnoreEvaluation.ShouldTraverseIgnoredDirectory)
				return true;
		}

		if (rules.IsSmartIgnoredDirectory(fullPath, name))
			return true;

		var isDot = IgnoreRuleSemantics.IsDotName(name);
		if (IgnoreRuleSemantics.ShouldIgnoreDotDirectory(rules.IgnoreDotFolders, isDot))
			return true;

		if (IgnoreRuleSemantics.ShouldIgnoreHiddenDirectory(
			    rules.IgnoreHiddenFolders,
			    isHidden,
			    isDot,
			    rules.IgnoreDotFolders))
			return true;

		return false;
	}

	/// <summary>
	/// Optimized version that avoids FileInfo allocation when possible.
	/// Only checks attributes when necessary.
	/// </summary>
	private static bool ShouldSkipFileByName(
		string name,
		string fullPath,
		IgnoreRules rules,
		bool shouldApplySmartIgnore,
		in IgnoreRules.GitIgnoreEvaluation gitIgnoreEvaluation)
	{
		return ShouldSkipFileByName(
			name,
			fullPath,
			HasHiddenAttribute(fullPath),
			GetFileLength(fullPath),
			rules,
			shouldApplySmartIgnore,
			gitIgnoreEvaluation);
	}

	/// <summary>
	/// File scans reuse the metadata captured during enumeration so hidden/empty checks
	/// stay purely in-memory instead of bouncing back to the filesystem for every file.
	/// </summary>
	private static bool ShouldSkipFileByName(
		string name,
		string fullPath,
		bool isHidden,
		long length,
		IgnoreRules rules,
		bool shouldApplySmartIgnore,
		in IgnoreRules.GitIgnoreEvaluation gitIgnoreEvaluation)
	{
		if (gitIgnoreEvaluation.IsIgnored)
			return true;

		if (rules.IsSmartIgnoredFile(fullPath, name, shouldApplySmartIgnore))
			return true;

		var isDot = IgnoreRuleSemantics.IsDotName(name);
		if (IgnoreRuleSemantics.ShouldIgnoreDotFile(rules.IgnoreDotFiles, isDot))
			return true;

		if (rules.IgnoreExtensionlessFiles && IsExtensionlessFileName(name))
			return true;

		if (rules.IgnoreEmptyFiles && length == 0)
			return true;

		if (IgnoreRuleSemantics.ShouldIgnoreHiddenFile(
			    rules.IgnoreHiddenFiles,
			    isHidden,
			    isDot,
			    rules.IgnoreDotFiles))
			return true;

		return false;
	}

	private static DirectoryScanFacts AnalyzeDirectory(
		string fullPath,
		string relativePath,
		string name,
		IgnoreRules rules,
		IgnoreRules.GitIgnoreScanContext gitIgnoreContext,
		IgnoreRules.GitIgnoreScanContext gitIgnoreCandidateContext)
	{
		return AnalyzeDirectory(
			fullPath,
			relativePath,
			name,
			HasHiddenAttribute(fullPath),
			rules,
			gitIgnoreContext,
			gitIgnoreCandidateContext);
	}

	private static DirectoryScanFacts AnalyzeDirectory(
		string fullPath,
		string relativePath,
		string name,
		bool isHidden,
		IgnoreRules rules,
		IgnoreRules.GitIgnoreScanContext gitIgnoreContext,
		IgnoreRules.GitIgnoreScanContext gitIgnoreCandidateContext)
	{
		var gitIgnoreEvaluation = rules.UseGitIgnore
			? gitIgnoreContext.Evaluate(fullPath, relativePath, isDirectory: true, name)
			: IgnoreRules.GitIgnoreEvaluation.NotIgnored;
		var gitIgnoreCandidateEvaluation =
			gitIgnoreCandidateContext.Evaluate(fullPath, relativePath, isDirectory: true, name);

		return new DirectoryScanFacts(
			Name: name,
			FullPath: fullPath,
			RelativePath: relativePath,
			IsHidden: isHidden,
			IsDot: IgnoreRuleSemantics.IsDotName(name),
			IsSmartIgnored: rules.IsSmartIgnoredDirectory(fullPath, name),
			IsSmartIgnoredCandidate: rules.IsSmartIgnoredDirectoryCandidate(fullPath, name),
			GitIgnoreEvaluation: gitIgnoreEvaluation,
			GitIgnoreCandidateEvaluation: gitIgnoreCandidateEvaluation);
	}

	private static FileScanFacts AnalyzeFile(
		string fullPath,
		string relativePath,
		string name,
		bool shouldApplySmartIgnoreForFiles,
		bool shouldApplySmartIgnoreCandidateForFiles,
		IgnoreRules rules,
		IgnoreRules.GitIgnoreScanContext gitIgnoreContext,
		IgnoreRules.GitIgnoreScanContext gitIgnoreCandidateContext)
	{
		return AnalyzeFile(
			fullPath,
			relativePath,
			name,
			HasHiddenAttribute(fullPath),
			GetFileLength(fullPath),
			shouldApplySmartIgnoreForFiles,
			shouldApplySmartIgnoreCandidateForFiles,
			rules,
			gitIgnoreContext,
			gitIgnoreCandidateContext);
	}

	private static FileScanFacts AnalyzeFile(
		string fullPath,
		string relativePath,
		string name,
		bool isHidden,
		long length,
		bool shouldApplySmartIgnoreForFiles,
		bool shouldApplySmartIgnoreCandidateForFiles,
		IgnoreRules rules,
		IgnoreRules.GitIgnoreScanContext gitIgnoreContext,
		IgnoreRules.GitIgnoreScanContext gitIgnoreCandidateContext)
	{
		var isExtensionless = IsExtensionlessFileName(name);
		var gitIgnored = rules.UseGitIgnore &&
		                 gitIgnoreContext.Evaluate(fullPath, relativePath, isDirectory: false, name).IsIgnored;
		var gitIgnoredCandidate = gitIgnoreCandidateContext
			.Evaluate(fullPath, relativePath, isDirectory: false, name)
			.IsIgnored;

		return new FileScanFacts(
			Name: name,
			RelativePath: relativePath,
			Extension: Path.GetExtension(name),
			IsHidden: isHidden,
			IsDot: IgnoreRuleSemantics.IsDotName(name),
			IsEmpty: length == 0,
			IsExtensionless: isExtensionless,
			IsSmartIgnored: rules.IsSmartIgnoredFile(fullPath, name, shouldApplySmartIgnoreForFiles),
			IsSmartIgnoredCandidate: rules.IsSmartIgnoredFileCandidate(
				fullPath,
				name,
				shouldApplySmartIgnoreCandidateForFiles),
			IsGitIgnored: gitIgnored,
			IsGitIgnoredCandidate: gitIgnoredCandidate);
	}

	private static DirectoryToggleRuleState EvaluateDirectoryRuleState(
		in DirectoryScanFacts facts,
		bool ignoreHiddenFolders,
		bool ignoreDotFolders)
	{
		if (facts.GitIgnoreEvaluation.IsIgnored && !facts.GitIgnoreEvaluation.ShouldTraverseIgnoredDirectory)
			return new DirectoryToggleRuleState(CanTraverseChildren: false, IsSelfIgnoredButTraversed: false);

		if (facts.IsSmartIgnored)
			return new DirectoryToggleRuleState(CanTraverseChildren: false, IsSelfIgnoredButTraversed: false);

		if (IgnoreRuleSemantics.ShouldIgnoreDotDirectory(ignoreDotFolders, facts.IsDot))
			return new DirectoryToggleRuleState(CanTraverseChildren: false, IsSelfIgnoredButTraversed: false);

		if (IgnoreRuleSemantics.ShouldIgnoreHiddenDirectory(
			    ignoreHiddenFolders,
			    facts.IsHidden,
			    facts.IsDot,
			    ignoreDotFolders))
			return new DirectoryToggleRuleState(CanTraverseChildren: false, IsSelfIgnoredButTraversed: false);

		return new DirectoryToggleRuleState(
			CanTraverseChildren: true,
			IsSelfIgnoredButTraversed: facts.GitIgnoreEvaluation.IsIgnored &&
			                           facts.GitIgnoreEvaluation.ShouldTraverseIgnoredDirectory);
	}

	private static IgnoreControllerImpactCounts CountDirectDirectoryControllerImpact(
		in DirectoryScanFacts facts,
		IgnoreRules rules,
		IExtensionInclusionPolicy? extensionPolicy,
		CancellationToken cancellationToken,
		bool requireVisibleContentWhenEmptyFoldersIgnored = true)
	{
		if (!PassesNonControllerDirectoryRules(facts, rules))
			return IgnoreControllerImpactCounts.Empty;

		// Selected-scope directories need a content probe because EmptyFolders can already
		// remove an empty folder. Root-list candidates are different: hiding the top-level
		// checkbox itself is user-visible even before extension/content filters are applied.
		if (requireVisibleContentWhenEmptyFoldersIgnored &&
		    rules.IgnoreEmptyFolders &&
		    !HasVisibleContentForControllerImpactCandidate(
			    facts.FullPath,
			    facts.RelativePath,
			    rules,
			    extensionPolicy,
			    cancellationToken))
		{
			return IgnoreControllerImpactCounts.Empty;
		}

		var gitDirectImpact =
			facts.GitIgnoreCandidateEvaluation.IsIgnored &&
			!facts.GitIgnoreCandidateEvaluation.ShouldTraverseIgnoredDirectory;
		var smartDirectImpact = facts.IsSmartIgnoredCandidate;

		return new IgnoreControllerImpactCounts(
			GitIgnore: gitDirectImpact || (rules.SmartIgnoreFollowsGitIgnore && smartDirectImpact) ? 1 : 0,
			SmartIgnore: smartDirectImpact ? 1 : 0);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool PassesNonControllerDirectoryRules(
		in DirectoryScanFacts facts,
		IgnoreRules rules)
	{
		if (IgnoreRuleSemantics.ShouldIgnoreDotDirectory(rules.IgnoreDotFolders, facts.IsDot))
			return false;

		if (IgnoreRuleSemantics.ShouldIgnoreHiddenDirectory(
			    rules.IgnoreHiddenFolders,
			    facts.IsHidden,
			    facts.IsDot,
			    rules.IgnoreDotFolders))
		{
			return false;
		}

		return true;
	}

	private static bool HasVisibleContentForControllerImpactCandidate(
		string rootPath,
		string rootRelativePath,
		IgnoreRules rules,
		IExtensionInclusionPolicy? extensionPolicy,
		CancellationToken cancellationToken)
	{
		var pending = new Stack<(string Path, string RelativePath)>();
		pending.Push((rootPath, rootRelativePath));

		while (pending.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var (currentPath, currentRelativePath) = pending.Pop();

			try
			{
				foreach (var file in FileSystemEntryEnumerator.EnumerateFiles(currentPath, currentRelativePath))
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (IsFileVisibleWithoutControllerRules(file, rules, extensionPolicy))
						return true;
				}

				foreach (var directory in FileSystemEntryEnumerator.EnumerateDirectories(currentPath, currentRelativePath))
				{
					cancellationToken.ThrowIfCancellationRequested();
					var childFacts = new DirectoryScanFacts(
						Name: directory.Name,
						FullPath: directory.FullPath,
						RelativePath: directory.RelativePath,
						IsHidden: directory.IsHidden,
						IsDot: IgnoreRuleSemantics.IsDotName(directory.Name),
						IsSmartIgnored: false,
						IsSmartIgnoredCandidate: false,
						GitIgnoreEvaluation: IgnoreRules.GitIgnoreEvaluation.NotIgnored,
						GitIgnoreCandidateEvaluation: IgnoreRules.GitIgnoreEvaluation.NotIgnored);
					if (!PassesNonControllerDirectoryRules(childFacts, rules))
						continue;

					pending.Push((directory.FullPath, directory.RelativePath));
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (UnauthorizedAccessException)
			{
				// An unreadable directory is still visible to the user as an access-denied node.
				return true;
			}
			catch
			{
				// Best-effort impact probing must not destabilize project loading.
			}
		}

		return false;
	}

	private static bool IsFileVisibleWithoutControllerRules(
		FileSystemFileEntry file,
		IgnoreRules rules,
		IExtensionInclusionPolicy? extensionPolicy)
	{
		var facts = new FileScanFacts(
			Name: file.Name,
			RelativePath: file.RelativePath,
			Extension: Path.GetExtension(file.Name),
			IsHidden: file.IsHidden,
			IsDot: IgnoreRuleSemantics.IsDotName(file.Name),
			IsEmpty: file.Length == 0,
			IsExtensionless: IsExtensionlessFileName(file.Name),
			IsSmartIgnored: false,
			IsSmartIgnoredCandidate: false,
			IsGitIgnored: false,
			IsGitIgnoredCandidate: false);

		return PassesControllerImpactExtensionFilter(facts, extensionPolicy) &&
		       PassesFileIgnoreRules(
			       facts,
			       rules.IgnoreHiddenFiles,
			       rules.IgnoreDotFiles,
			       rules.IgnoreEmptyFiles,
			       rules.IgnoreExtensionlessFiles);
	}

	private static bool IsDirectoryLocallyVisible(
		DirectoryToggleRuleState ruleState,
		bool ignoreEmptyFolders,
		bool hasVisibleContent)
	{
		if (!ruleState.CanTraverseChildren)
			return false;

		if (!hasVisibleContent)
		{
			if (ruleState.IsSelfIgnoredButTraversed)
				return false;
			if (ignoreEmptyFolders)
				return false;
		}

		return true;
	}

	private static EffectiveFileVisibilityProfile EvaluateFileVisibilityProfile(
		in FileScanFacts facts,
		IExtensionInclusionPolicy? extensionPolicy,
		IgnoreRules rules,
		bool allowWhenExtensionsAreDiscovered)
	{
		var controllerBaselineVisible =
			PassesControllerImpactExtensionFilter(facts, extensionPolicy) &&
			PassesFileIgnoreRules(
				facts,
				rules.IgnoreHiddenFiles,
				rules.IgnoreDotFiles,
				rules.IgnoreEmptyFiles,
				rules.IgnoreExtensionlessFiles);
		var gitIgnoreVisible = controllerBaselineVisible &&
		                       !facts.IsGitIgnoredCandidate &&
		                       !(rules.SmartIgnoreFollowsGitIgnore && facts.IsSmartIgnoredCandidate);
		var smartIgnoreVisible = controllerBaselineVisible && !facts.IsSmartIgnoredCandidate;

		if (facts.IsGitIgnored ||
		    facts.IsSmartIgnored ||
		    !PassesExtensionFilter(facts, extensionPolicy, allowWhenExtensionsAreDiscovered))
		{
			return new EffectiveFileVisibilityProfile(
				BaseVisible: false,
				HiddenFilesVisible: false,
				DotFilesVisible: false,
				EmptyFilesVisible: false,
				ExtensionlessFilesVisible: false,
				ControllerBaselineVisible: controllerBaselineVisible,
				GitIgnoreVisible: gitIgnoreVisible,
				SmartIgnoreVisible: smartIgnoreVisible);
		}

		return new EffectiveFileVisibilityProfile(
			BaseVisible: PassesFileIgnoreRules(
				facts,
				rules.IgnoreHiddenFiles,
				rules.IgnoreDotFiles,
				rules.IgnoreEmptyFiles,
				rules.IgnoreExtensionlessFiles),
			HiddenFilesVisible: PassesFileIgnoreRules(
				facts,
				!rules.IgnoreHiddenFiles,
				rules.IgnoreDotFiles,
				rules.IgnoreEmptyFiles,
				rules.IgnoreExtensionlessFiles),
			DotFilesVisible: PassesFileIgnoreRules(
				facts,
				rules.IgnoreHiddenFiles,
				!rules.IgnoreDotFiles,
				rules.IgnoreEmptyFiles,
				rules.IgnoreExtensionlessFiles),
			EmptyFilesVisible: PassesFileIgnoreRules(
				facts,
				rules.IgnoreHiddenFiles,
				rules.IgnoreDotFiles,
				!rules.IgnoreEmptyFiles,
				rules.IgnoreExtensionlessFiles),
			ExtensionlessFilesVisible: PassesFileIgnoreRules(
				facts,
				rules.IgnoreHiddenFiles,
				rules.IgnoreDotFiles,
				rules.IgnoreEmptyFiles,
				!rules.IgnoreExtensionlessFiles),
			ControllerBaselineVisible: controllerBaselineVisible,
			GitIgnoreVisible: gitIgnoreVisible,
			SmartIgnoreVisible: smartIgnoreVisible);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool PassesControllerImpactExtensionFilter(
		in FileScanFacts facts,
		IExtensionInclusionPolicy? extensionPolicy)
	{
		if (facts.IsExtensionless)
			return true;

		if (string.IsNullOrWhiteSpace(facts.Extension))
			return false;

		return extensionPolicy is null || extensionPolicy.AllowsExtension(facts.Extension.AsSpan());
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool PassesExtensionFilter(
		in FileScanFacts facts,
		IExtensionInclusionPolicy? extensionPolicy,
		bool allowWhenExtensionsAreDiscovered)
	{
		if (facts.IsExtensionless)
			return true;

		if (!allowWhenExtensionsAreDiscovered)
			return false;

		if (extensionPolicy is null)
			return allowWhenExtensionsAreDiscovered &&
			       !string.IsNullOrWhiteSpace(facts.Extension);

		return !string.IsNullOrWhiteSpace(facts.Extension) &&
		       extensionPolicy.AllowsExtension(facts.Extension.AsSpan());
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool PassesFileIgnoreRules(
		in FileScanFacts facts,
		bool ignoreHiddenFiles,
		bool ignoreDotFiles,
		bool ignoreEmptyFiles,
		bool ignoreExtensionlessFiles)
	{
		if (IgnoreRuleSemantics.ShouldIgnoreHiddenFile(
			    ignoreHiddenFiles,
			    facts.IsHidden,
			    facts.IsDot,
			    ignoreDotFiles))
			return false;
		if (IgnoreRuleSemantics.ShouldIgnoreDotFile(ignoreDotFiles, facts.IsDot))
			return false;
		if (ignoreEmptyFiles && facts.IsEmpty)
			return false;
		if (ignoreExtensionlessFiles && facts.IsExtensionless)
			return false;

		return true;
	}

	private static IExtensionInclusionPolicy? CreateExtensionPolicy(
		IReadOnlySet<string>? allowedExtensions)
	{
		if (allowedExtensions is null)
			return null;

		return new ExtensionSetInclusionPolicy(allowedExtensions);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool PassesExtensionDiscoveryRules(in FileScanFacts facts, IgnoreRules rules)
	{
		return !facts.IsGitIgnored &&
		       !facts.IsSmartIgnored &&
		       PassesFileIgnoreRules(
			       facts,
			       rules.IgnoreHiddenFiles,
			       rules.IgnoreDotFiles,
			       rules.IgnoreEmptyFiles,
			       rules.IgnoreExtensionlessFiles);
	}

	private static IgnoreRules BuildEffectiveCountDiscoveryRules(IgnoreRules rules)
	{
		// Effective counters measure what each basic file rule changes from the current tree.
		// Keep Git/Smart exclusions, but leave file-level toggles open so the rule that hides
		// a file can still be counted as the active cause.
		return rules with
		{
			IgnoreHiddenFiles = false,
			IgnoreDotFiles = false,
			IgnoreEmptyFiles = false,
			IgnoreExtensionlessFiles = false
		};
	}

	private ScanResult<ExtensionsScanData> ScanExtensionsCore(
		string rootPath,
		IgnoreRules rules,
		bool collectIgnoreOptionCounts,
		bool includeRootDirectoryInCounts,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var uniqueExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var useGitIgnore = rules.UseGitIgnore;
		var gitIgnoreContext = rules.CreateGitIgnoreScanContext(rootPath);
		if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
		{
			return new ScanResult<ExtensionsScanData>(
				new ExtensionsScanData(uniqueExtensions, IgnoreOptionCounts.Empty),
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		var normalizedRootPath = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var rootName = Path.GetFileName(normalizedRootPath);
		var rootGitIgnore = useGitIgnore
			? gitIgnoreContext.Evaluate(rootPath, string.Empty, isDirectory: true, rootName)
			: IgnoreRules.GitIgnoreEvaluation.NotIgnored;

		// Selected root folders must obey the same directory-level rules as the tree itself.
		// Otherwise a stale root selection (for example a dot-folder discovered before the
		// dynamic DotFolders toggle appeared) would still leak its subtree into counts.
		if (ShouldSkipDirectoryByName(rootName, rootPath, HasHiddenAttribute(rootPath), rules, rootGitIgnore))
		{
			return new ScanResult<ExtensionsScanData>(
				new ExtensionsScanData(uniqueExtensions, IgnoreOptionCounts.Empty),
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		// Directory discovery is single-threaded; keep it allocation-light and parent-indexed.
		var directories = new List<DirectoryScanNode>(capacity: 256);
		var rootAccessDenied = 0;
		var hadAccessDenied = 0;
		var directoryCounts = default(MutableIgnoreOptionCounts);

		// First pass: collect all traversable directories and parent links.
		var pending = new Stack<(string Path, string RelativePath, int ParentIndex, bool IsRootDirectory)>();
		pending.Push((rootPath, RelativePath: string.Empty, ParentIndex: -1, IsRootDirectory: true));

		while (pending.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var (dir, relativePath, parentIndex, isRootDirectory) = pending.Pop();
			var currentDirectoryIndex = directories.Count;
			directories.Add(new DirectoryScanNode(dir, relativePath, parentIndex, isAccessDenied: false));

			if (collectIgnoreOptionCounts && isRootDirectory && includeRootDirectoryInCounts)
			{
				AccumulateDirectoryIgnoreOptionCounts(
					new FileSystemDirectoryEntry(Path.GetFileName(dir), dir, relativePath, HasHiddenAttribute(dir)),
					ref directoryCounts);
			}

			try
			{
				foreach (var sd in FileSystemEntryEnumerator.EnumerateDirectories(dir, relativePath))
				{
					cancellationToken.ThrowIfCancellationRequested();

					if (collectIgnoreOptionCounts)
						AccumulateDirectoryIgnoreOptionCounts(sd, ref directoryCounts);

					var directoryGitIgnore = useGitIgnore
						? gitIgnoreContext.Evaluate(sd.FullPath, sd.RelativePath, isDirectory: true, sd.Name)
						: IgnoreRules.GitIgnoreEvaluation.NotIgnored;
					if (ShouldSkipDirectoryByName(sd.Name, sd.FullPath, sd.IsHidden, rules, directoryGitIgnore))
						continue;

					pending.Push((sd.FullPath, sd.RelativePath, currentDirectoryIndex, IsRootDirectory: false));
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (UnauthorizedAccessException)
			{
				var accessDeniedNode = directories[currentDirectoryIndex];
				accessDeniedNode.IsAccessDenied = true;
				directories[currentDirectoryIndex] = accessDeniedNode;
				Interlocked.Exchange(ref hadAccessDenied, 1);
				if (isRootDirectory) Interlocked.Exchange(ref rootAccessDenied, 1);
				continue;
			}
			catch
			{
				continue;
			}
		}

		var mergeLock = new object();
		var fileCounts = default(MutableIgnoreOptionCounts);
		var hasVisibleFilesByDirectory = ArrayPool<bool>.Shared.Rent(directories.Count);
		var isAccessDeniedByDirectory = ArrayPool<bool>.Shared.Rent(directories.Count);
		Array.Clear(hasVisibleFilesByDirectory, 0, directories.Count);
		Array.Clear(isAccessDeniedByDirectory, 0, directories.Count);

		try
		{
			for (var i = 0; i < directories.Count; i++)
				isAccessDeniedByDirectory[i] = directories[i].IsAccessDenied;

			var parallelOptions = ScanParallelismPolicy.CreateOptions(cancellationToken);
			Parallel.For(
				0,
				directories.Count,
				parallelOptions,
				() => new LocalExtensionScanState(),
				(index, _, localState) =>
				{
					parallelOptions.CancellationToken.ThrowIfCancellationRequested();
					var dir = directories[index].Path;
					var relativePath = directories[index].RelativePath;
					var shouldApplySmartIgnoreForFiles = rules.ShouldApplySmartIgnore(dir, isDirectory: true);

					var hasVisibleFiles = false;
					try
					{
						foreach (var file in FileSystemEntryEnumerator.EnumerateFiles(dir, relativePath))
						{
							parallelOptions.CancellationToken.ThrowIfCancellationRequested();

							if (collectIgnoreOptionCounts)
								AccumulateFileIgnoreOptionCounts(file, ref localState.Counts);

							var fileGitIgnore = useGitIgnore
								? gitIgnoreContext.Evaluate(file.FullPath, file.RelativePath, isDirectory: false, file.Name)
								: IgnoreRules.GitIgnoreEvaluation.NotIgnored;
							if (ShouldSkipFileByName(
								    file.Name,
								    file.FullPath,
								    file.IsHidden,
								    file.Length,
								    rules,
								    shouldApplySmartIgnoreForFiles,
								    fileGitIgnore))
								continue;

							hasVisibleFiles = true;
							var ext = Path.GetExtension(file.Name);
							if (IsExtensionlessFileName(file.Name))
								localState.Extensions.Add(file.Name);
							else if (!string.IsNullOrWhiteSpace(ext))
								localState.Extensions.Add(ext);
						}
					}
					catch (OperationCanceledException)
					{
						throw;
					}
					catch (UnauthorizedAccessException)
					{
						Interlocked.Exchange(ref hadAccessDenied, 1);
						isAccessDeniedByDirectory[index] = true;
						return localState;
					}
					catch
					{
						return localState;
					}

					hasVisibleFilesByDirectory[index] = hasVisibleFiles;
					return localState;
				},
				localState =>
				{
					if (localState.Extensions.Count == 0 && !collectIgnoreOptionCounts)
						return;

					lock (mergeLock)
					{
						if (localState.Extensions.Count > 0)
							uniqueExtensions.UnionWith(localState.Extensions);
						if (collectIgnoreOptionCounts)
							fileCounts.Add(localState.Counts);
					}
				});

			var emptyFolderCount = 0;
			if (collectIgnoreOptionCounts && directories.Count > 0)
			{
				// Bottom-up fold simulates IgnoreEmptyFolders pruning without a second filesystem pass.
				var nonPrunedChildCounts = ArrayPool<int>.Shared.Rent(directories.Count);
				Array.Clear(nonPrunedChildCounts, 0, directories.Count);
				try
				{
					for (var index = directories.Count - 1; index >= 0; index--)
					{
						var hasVisibleFiles = hasVisibleFilesByDirectory[index];
						var hasVisibleChildren = nonPrunedChildCounts[index] > 0;
						var isAccessDenied = isAccessDeniedByDirectory[index];
						var parentIndex = directories[index].ParentIndex;

						var shouldRemain = isAccessDenied || hasVisibleFiles || hasVisibleChildren;
						if (!shouldRemain)
						{
							if (parentIndex >= 0 || includeRootDirectoryInCounts)
								emptyFolderCount++;
							continue;
						}

						if (parentIndex >= 0)
							nonPrunedChildCounts[parentIndex]++;
					}
				}
				finally
				{
					ArrayPool<int>.Shared.Return(nonPrunedChildCounts);
				}
			}

			var counts = collectIgnoreOptionCounts
				? directoryCounts.ToImmutable().Add(fileCounts.ToImmutable()) with { EmptyFolders = emptyFolderCount }
				: IgnoreOptionCounts.Empty;

			return new ScanResult<ExtensionsScanData>(
				new ExtensionsScanData(uniqueExtensions, counts),
				rootAccessDenied == 1,
				hadAccessDenied == 1);
		}
		finally
		{
			ArrayPool<bool>.Shared.Return(hasVisibleFilesByDirectory);
			ArrayPool<bool>.Shared.Return(isAccessDeniedByDirectory);
		}
	}

	private ScanResult<ExtensionsScanData> ScanRootFilesCore(
		string rootPath,
		IgnoreRules rules,
		bool collectIgnoreOptionCounts,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var useGitIgnore = rules.UseGitIgnore;
		var gitIgnoreContext = rules.CreateGitIgnoreScanContext(rootPath);
		if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
		{
			return new ScanResult<ExtensionsScanData>(
				new ExtensionsScanData(exts, IgnoreOptionCounts.Empty),
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		var counts = default(MutableIgnoreOptionCounts);
		var shouldApplySmartIgnoreForFiles = rules.ShouldApplySmartIgnore(rootPath, isDirectory: true);
		try
		{
			foreach (var file in FileSystemEntryEnumerator.EnumerateFiles(rootPath))
			{
				cancellationToken.ThrowIfCancellationRequested();

				if (collectIgnoreOptionCounts)
					AccumulateFileIgnoreOptionCounts(file, ref counts);

				var fileGitIgnore = useGitIgnore
					? gitIgnoreContext.Evaluate(file.FullPath, file.RelativePath, isDirectory: false, file.Name)
					: IgnoreRules.GitIgnoreEvaluation.NotIgnored;
				if (ShouldSkipFileByName(
					    file.Name,
					    file.FullPath,
					    file.IsHidden,
					    file.Length,
					    rules,
					    shouldApplySmartIgnoreForFiles,
					    fileGitIgnore))
					continue;

				var ext = Path.GetExtension(file.Name);
				if (IsExtensionlessFileName(file.Name))
					exts.Add(file.Name);
				else if (!string.IsNullOrWhiteSpace(ext))
					exts.Add(ext);
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (UnauthorizedAccessException)
		{
			return new ScanResult<ExtensionsScanData>(
				new ExtensionsScanData(exts, IgnoreOptionCounts.Empty),
				RootAccessDenied: true,
				HadAccessDenied: true);
		}
		catch
		{
			return new ScanResult<ExtensionsScanData>(
				new ExtensionsScanData(exts, IgnoreOptionCounts.Empty),
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		return new ScanResult<ExtensionsScanData>(
			new ExtensionsScanData(exts, collectIgnoreOptionCounts ? counts.ToImmutable() : IgnoreOptionCounts.Empty),
			RootAccessDenied: false,
			HadAccessDenied: false);
	}

	private ScanResult<IgnoreOptionCounts> ScanEffectiveRootFileIgnoreOptionCountsCore(
		string rootPath,
		IReadOnlySet<string> allowedExtensions,
		IgnoreRules rules,
		CancellationToken cancellationToken)
	{
		var effectiveCountDiscoveryRules = BuildEffectiveCountDiscoveryRules(rules);
		var scan = ScanRootFileIgnoreSectionSnapshotCore(
			rootPath,
			effectiveCountDiscoveryRules,
			rules,
			CreateExtensionPolicy(allowedExtensions),
			cancellationToken);
		return new ScanResult<IgnoreOptionCounts>(
			scan.Value.EffectiveIgnoreOptionCounts,
			scan.RootAccessDenied,
			scan.HadAccessDenied);
	}

	private ScanResult<IgnoreOptionCounts> ScanEffectiveIgnoreOptionCountsCore(
		string rootPath,
		IReadOnlySet<string> allowedExtensions,
		IgnoreRules rules,
		CancellationToken cancellationToken)
	{
		var effectiveCountDiscoveryRules = BuildEffectiveCountDiscoveryRules(rules);
		var scan = ScanIgnoreSectionSnapshotCore(
			rootPath,
			effectiveCountDiscoveryRules,
			rules,
			CreateExtensionPolicy(allowedExtensions),
			includeRootDirectoryInRawCounts: true,
			cancellationToken);
		return new ScanResult<IgnoreOptionCounts>(
			scan.Value.EffectiveIgnoreOptionCounts,
			scan.RootAccessDenied,
			scan.HadAccessDenied);
	}

	private ScanResult<IgnoreSectionScanData> ScanRootFileIgnoreSectionSnapshotCore(
		string rootPath,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var rawCounts = default(MutableIgnoreOptionCounts);
		var effectiveCounts = default(MutableIgnoreOptionCounts);
		var controllerImpactCounts = IgnoreControllerImpactCounts.Empty;

		if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
		{
			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(
					extensions,
					IgnoreOptionCounts.Empty,
					IgnoreOptionCounts.Empty,
					IgnoreControllerImpactCounts.Empty),
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		// The combined ignore-section path intentionally reuses one file-fact record.
		// This keeps the snapshot cheap enough for live refreshes, so the caller must
		// keep structural ignore semantics aligned between discovery and effective rules.
		var effectiveGitIgnoreContext = effectiveRules.CreateGitIgnoreScanContext(rootPath);
		var effectiveGitIgnoreCandidateContext = effectiveRules.CreateGitIgnoreCandidateScanContext(rootPath);
		var shouldApplySmartIgnoreForFiles = effectiveRules.ShouldApplySmartIgnore(rootPath, isDirectory: true);
		var shouldApplySmartIgnoreCandidateForFiles =
			effectiveRules.ShouldApplySmartIgnoreCandidate(rootPath, isDirectory: true);

		try
		{
			foreach (var file in FileSystemEntryEnumerator.EnumerateFiles(rootPath))
			{
				cancellationToken.ThrowIfCancellationRequested();

				AccumulateFileIgnoreOptionCounts(file, ref rawCounts);

				var facts = AnalyzeFile(
					file.FullPath,
					file.RelativePath,
					file.Name,
					file.IsHidden,
					file.Length,
					shouldApplySmartIgnoreForFiles,
					shouldApplySmartIgnoreCandidateForFiles,
					effectiveRules,
					effectiveGitIgnoreContext,
					effectiveGitIgnoreCandidateContext);
				var passesDiscovery = PassesExtensionDiscoveryRules(facts, extensionDiscoveryRules);
				if (passesDiscovery)
					AddExtensionEntry(
						file.Name,
						facts.Extension,
						facts.IsExtensionless,
						extensions,
						skipExtensionlessEntry: effectiveRules.IgnoreExtensionlessFiles);

				var visibility = EvaluateFileVisibilityProfile(
					facts,
					effectiveExtensionPolicy,
					effectiveRules,
					passesDiscovery);
				AccumulateDirectFileDelta(facts.IsHidden, visibility.BaseVisible, visibility.HiddenFilesVisible, ref effectiveCounts.HiddenFiles);
				AccumulateDirectFileDelta(facts.IsDot, visibility.BaseVisible, visibility.DotFilesVisible, ref effectiveCounts.DotFiles);
				AccumulateDirectFileDelta(facts.IsEmpty, visibility.BaseVisible, visibility.EmptyFilesVisible, ref effectiveCounts.EmptyFiles);
				AccumulateDirectFileDelta(
					facts.IsExtensionless,
					visibility.BaseVisible,
					visibility.ExtensionlessFilesVisible,
					ref effectiveCounts.ExtensionlessFiles);
				controllerImpactCounts = controllerImpactCounts.Add(
					CountFileControllerImpact(visibility));
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (UnauthorizedAccessException)
		{
			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(
					extensions,
					IgnoreOptionCounts.Empty,
					IgnoreOptionCounts.Empty,
					IgnoreControllerImpactCounts.Empty),
				RootAccessDenied: true,
				HadAccessDenied: true);
		}
		catch
		{
			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(
					extensions,
					IgnoreOptionCounts.Empty,
					IgnoreOptionCounts.Empty,
					IgnoreControllerImpactCounts.Empty),
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		return new ScanResult<IgnoreSectionScanData>(
			new IgnoreSectionScanData(
				extensions,
				rawCounts.ToImmutable(),
				effectiveCounts.ToImmutable(),
				controllerImpactCounts),
			RootAccessDenied: false,
			HadAccessDenied: false);
	}

	private ScanResult<IgnoreSectionScanData> ScanIgnoreSectionSnapshotCore(
		string rootPath,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		bool includeRootDirectoryInRawCounts,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var discovery = DiscoverEffectiveIgnoreScanNodes(
			rootPath,
			extensionDiscoveryRules,
			effectiveRules,
			effectiveExtensionPolicy,
			cancellationToken);
		var directories = discovery.Value;
		if (directories.Count == 0)
		{
			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase),
					IgnoreOptionCounts.Empty,
					IgnoreOptionCounts.Empty,
					IgnoreControllerImpactCounts.Empty),
				discovery.RootAccessDenied,
				discovery.HadAccessDenied);
		}

		var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var rawCounts = default(MutableIgnoreOptionCounts);
		var fileMetrics = ArrayPool<EffectiveIgnoreNodeFileMetrics>.Shared.Rent(directories.Count);
		var visibilityStates = ArrayPool<EffectiveIgnoreNodeVisibilityState>.Shared.Rent(directories.Count);
		Array.Clear(fileMetrics, 0, directories.Count);
		Array.Clear(visibilityStates, 0, directories.Count);
		var hadAccessDenied = discovery.HadAccessDenied ? 1 : 0;
		var mergeLock = new object();
		var effectiveGitIgnoreContext = effectiveRules.CreateGitIgnoreScanContext(rootPath);
		var effectiveGitIgnoreCandidateContext = effectiveRules.CreateGitIgnoreCandidateScanContext(rootPath);

		try
		{
			for (var index = 0; index < directories.Count; index++)
				visibilityStates[index].IsAccessDenied = directories[index].IsAccessDenied;

			for (var index = 0; index < directories.Count; index++)
			{
				var node = directories[index];
				var visibilityState = visibilityStates[index];
				// Discovery visibility tracks whether this subtree is reachable for extension/raw inventory.
				// It intentionally ignores empty-folder pruning and only follows the structural discovery path.
				visibilityState.ExtensionDiscoveryFinalVisible = node.ParentIndex < 0
					? node.ExtensionDiscoveryRuleState.CanTraverseChildren
					: visibilityStates[node.ParentIndex].ExtensionDiscoveryFinalVisible &&
					  node.ExtensionDiscoveryRuleState.CanTraverseChildren;
				visibilityStates[index] = visibilityState;
			}

			EffectiveIgnoreNodeFileMetrics ScanDirectoryFiles(
				int index,
				CancellationToken token,
				HashSet<string> localExtensions,
				ref MutableIgnoreOptionCounts localRawCounts)
			{
				token.ThrowIfCancellationRequested();

				var node = directories[index];
				if (!node.CanAnyVariantTraverseChildren)
					return default;

				var shouldApplySmartIgnoreForFiles = effectiveRules.ShouldApplySmartIgnore(node.Path, isDirectory: true);
				var shouldApplySmartIgnoreCandidateForFiles =
					effectiveRules.ShouldApplySmartIgnoreCandidate(node.Path, isDirectory: true);
				var extensionDiscoveryVisible = visibilityStates[index].ExtensionDiscoveryFinalVisible;
				var localMetrics = default(EffectiveIgnoreNodeFileMetrics);

				try
				{
					foreach (var file in FileSystemEntryEnumerator.EnumerateFiles(node.Path, node.RelativePath))
					{
						token.ThrowIfCancellationRequested();

						var facts = AnalyzeFile(
							file.FullPath,
							file.RelativePath,
							file.Name,
							file.IsHidden,
							file.Length,
							shouldApplySmartIgnoreForFiles,
							shouldApplySmartIgnoreCandidateForFiles,
							effectiveRules,
							effectiveGitIgnoreContext,
							effectiveGitIgnoreCandidateContext);
						if (extensionDiscoveryVisible)
							AccumulateFileIgnoreOptionCounts(file, ref localRawCounts);

						// When "all extensions" is selected we still must respect the discovery result.
						// Otherwise a subtree hidden from extension availability could leak back into the
						// effective counts simply because it contains a syntactically valid extension.
						var passesDiscovery = extensionDiscoveryVisible &&
						                      PassesExtensionDiscoveryRules(facts, extensionDiscoveryRules);
						if (passesDiscovery)
						{
							AddExtensionEntry(
								file.Name,
								facts.Extension,
								facts.IsExtensionless,
								localExtensions,
								skipExtensionlessEntry: effectiveRules.IgnoreExtensionlessFiles);
							localMetrics.ExtensionDiscoveryVisibleFiles++;
						}

						var visibility = EvaluateFileVisibilityProfile(
							facts,
							effectiveExtensionPolicy,
							effectiveRules,
							passesDiscovery);

						if (visibility.BaseVisible)
							localMetrics.BaseVisibleFiles++;
						if (visibility.HiddenFilesVisible)
							localMetrics.HiddenFilesVisibleFiles++;
						if (visibility.DotFilesVisible)
							localMetrics.DotFilesVisibleFiles++;
						if (visibility.EmptyFilesVisible)
							localMetrics.EmptyFilesVisibleFiles++;
						if (visibility.ExtensionlessFilesVisible)
							localMetrics.ExtensionlessFilesVisibleFiles++;
						if (visibility.ControllerBaselineVisible)
							localMetrics.ControllerBaselineVisibleFiles++;
						if (visibility.GitIgnoreVisible)
							localMetrics.GitIgnoreVisibleFiles++;
						if (visibility.SmartIgnoreVisible)
							localMetrics.SmartIgnoreVisibleFiles++;

						AccumulateToggleTransition(
							facts.IsHidden,
							visibility.BaseVisible,
							visibility.HiddenFilesVisible,
							ref localMetrics.HiddenFilesAppearWhenToggled,
							ref localMetrics.HiddenFilesDisappearWhenToggled);
						AccumulateToggleTransition(
							facts.IsDot,
							visibility.BaseVisible,
							visibility.DotFilesVisible,
							ref localMetrics.DotFilesAppearWhenToggled,
							ref localMetrics.DotFilesDisappearWhenToggled);
						AccumulateToggleTransition(
							facts.IsEmpty,
							visibility.BaseVisible,
							visibility.EmptyFilesVisible,
							ref localMetrics.EmptyFilesAppearWhenToggled,
							ref localMetrics.EmptyFilesDisappearWhenToggled);
						AccumulateToggleTransition(
							facts.IsExtensionless,
							visibility.BaseVisible,
							visibility.ExtensionlessFilesVisible,
							ref localMetrics.ExtensionlessFilesAppearWhenToggled,
							ref localMetrics.ExtensionlessFilesDisappearWhenToggled);
					}
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (UnauthorizedAccessException)
				{
					Interlocked.Exchange(ref hadAccessDenied, 1);
					var visibilityState = visibilityStates[index];
					visibilityState.IsAccessDenied = true;
					visibilityStates[index] = visibilityState;
					return default;
				}
				catch
				{
					return default;
				}

				return localMetrics;
			}

			var parallelOptions = ScanParallelismPolicy.CreateOptions(cancellationToken);
			Parallel.For(
				0,
				directories.Count,
				parallelOptions,
				() => new IgnoreSectionSnapshotLocalState(),
				(index, _, localState) =>
				{
					fileMetrics[index] = ScanDirectoryFiles(
						index,
						parallelOptions.CancellationToken,
						localState.Extensions,
						ref localState.RawCounts);
					return localState;
				},
				localState =>
				{
					if (localState.Extensions.Count == 0 &&
					    localState.RawCounts.IsEmpty)
					{
						return;
					}

					lock (mergeLock)
					{
						rawCounts.Add(localState.RawCounts);
						if (localState.Extensions.Count > 0)
							extensions.UnionWith(localState.Extensions);
					}
				});

			for (var index = 0; index < directories.Count; index++)
			{
				var node = directories[index];
				var parentDiscoveryVisible = node.ParentIndex >= 0 &&
				                            visibilityStates[node.ParentIndex].ExtensionDiscoveryFinalVisible;
				var shouldCountNode = node.ParentIndex < 0
					? includeRootDirectoryInRawCounts && visibilityStates[index].ExtensionDiscoveryFinalVisible
					: parentDiscoveryVisible;
				if (!shouldCountNode)
					continue;

				if (node.IsDot)
					rawCounts.DotFolders++;
				if (node.IsHidden)
					rawCounts.HiddenFolders++;
			}

			for (var index = directories.Count - 1; index >= 0; index--)
			{
				var node = directories[index];
				var visibilityState = visibilityStates[index];
				if (!visibilityState.ExtensionDiscoveryFinalVisible)
					continue;

				// Raw empty-folder inventory must match the historical extension scan contract:
				// count only folders that are part of the discovery-visible subtree and become
				// logically empty after discovery rules, without mixing in effective toggle state.
				var hasVisibleContent = visibilityState.IsAccessDenied ||
				                        fileMetrics[index].ExtensionDiscoveryVisibleFiles > 0 ||
				                        visibilityState.RawDiscoveryVisibleChildren > 0;
				if (!hasVisibleContent)
				{
					if (node.ParentIndex >= 0 || includeRootDirectoryInRawCounts)
						rawCounts.EmptyFolders++;
					continue;
				}

				if (node.ParentIndex >= 0)
				{
					var parentVisibilityState = visibilityStates[node.ParentIndex];
					parentVisibilityState.RawDiscoveryVisibleChildren++;
					visibilityStates[node.ParentIndex] = parentVisibilityState;
				}
			}

			var finalizedCounts = FinalizeEffectiveIgnoreCounts(directories, fileMetrics, visibilityStates, effectiveRules);
			return new ScanResult<IgnoreSectionScanData>(
				new IgnoreSectionScanData(
					extensions,
					rawCounts.ToImmutable(),
					finalizedCounts.IgnoreOptionCounts,
					finalizedCounts.ControllerImpactCounts),
				discovery.RootAccessDenied,
				hadAccessDenied == 1);
		}
		finally
		{
			ArrayPool<EffectiveIgnoreNodeFileMetrics>.Shared.Return(fileMetrics);
			ArrayPool<EffectiveIgnoreNodeVisibilityState>.Shared.Return(visibilityStates);
		}
	}

	private ScanResult<List<EffectiveIgnoreScanNode>> DiscoverEffectiveIgnoreScanNodes(
		string rootPath,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
			return new ScanResult<List<EffectiveIgnoreScanNode>>([], RootAccessDenied: false, HadAccessDenied: false);

		var rootName = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
		var effectiveGitIgnoreContext = effectiveRules.CreateGitIgnoreScanContext(rootPath);
		var effectiveGitIgnoreCandidateContext = effectiveRules.CreateGitIgnoreCandidateScanContext(rootPath);
		var rootFacts = AnalyzeDirectory(
			rootPath,
			string.Empty,
			rootName,
			HasHiddenAttribute(rootPath),
			effectiveRules,
			effectiveGitIgnoreContext,
			effectiveGitIgnoreCandidateContext);
		var rootDirectControllerImpactCounts = CountDirectDirectoryControllerImpact(
			rootFacts,
			effectiveRules,
			effectiveExtensionPolicy,
			cancellationToken);
		var rootExtensionDiscoveryRuleState = EvaluateDirectoryRuleState(
			rootFacts,
			extensionDiscoveryRules.IgnoreHiddenFolders,
			extensionDiscoveryRules.IgnoreDotFolders);
		var rootBaseRuleState = EvaluateDirectoryRuleState(rootFacts, effectiveRules.IgnoreHiddenFolders, effectiveRules.IgnoreDotFolders);
		var rootHiddenFoldersRuleState = EvaluateDirectoryRuleState(rootFacts, !effectiveRules.IgnoreHiddenFolders, effectiveRules.IgnoreDotFolders);
		var rootDotFoldersRuleState = EvaluateDirectoryRuleState(
			rootFacts,
			ShouldApplyHiddenFoldersForDotFoldersVariant(rootFacts, effectiveRules),
			!effectiveRules.IgnoreDotFolders);

		if (!CanAnyVariantTraverseChildren(rootExtensionDiscoveryRuleState, rootBaseRuleState, rootHiddenFoldersRuleState, rootDotFoldersRuleState) &&
		    rootDirectControllerImpactCounts == IgnoreControllerImpactCounts.Empty)
			return new ScanResult<List<EffectiveIgnoreScanNode>>([], RootAccessDenied: false, HadAccessDenied: false);

		var directories = new List<EffectiveIgnoreScanNode>(capacity: 256);
		var rootAccessDenied = 0;
		var hadAccessDenied = 0;
		var pending =
			new Stack<(
				DirectoryScanFacts Facts,
				int ParentIndex,
				bool IsRootDirectory,
				IgnoreControllerImpactCounts DirectControllerImpactCounts,
				DirectoryToggleRuleState ExtensionDiscoveryRuleState,
				DirectoryToggleRuleState BaseRuleState,
				DirectoryToggleRuleState HiddenFoldersRuleState,
				DirectoryToggleRuleState DotFoldersRuleState)>();
		pending.Push((
			rootFacts,
			ParentIndex: -1,
			IsRootDirectory: true,
			rootDirectControllerImpactCounts,
			rootExtensionDiscoveryRuleState,
			rootBaseRuleState,
			rootHiddenFoldersRuleState,
			rootDotFoldersRuleState));

		while (pending.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var (facts, parentIndex, isRootDirectory, directControllerImpactCounts, extensionDiscoveryRuleState, baseRuleState, hiddenFoldersRuleState, dotFoldersRuleState) = pending.Pop();
			var currentDirectoryIndex = directories.Count;
			directories.Add(new EffectiveIgnoreScanNode(
				facts.FullPath,
				facts.RelativePath,
				facts.Name,
				parentIndex,
				isAccessDenied: false,
				facts.IsHidden,
				facts.IsDot,
				directControllerImpactCounts,
				extensionDiscoveryRuleState,
				baseRuleState,
				hiddenFoldersRuleState,
				dotFoldersRuleState));

			if (!CanAnyVariantTraverseChildren(extensionDiscoveryRuleState, baseRuleState, hiddenFoldersRuleState, dotFoldersRuleState))
				continue;

			try
			{
				foreach (var childDirectory in FileSystemEntryEnumerator.EnumerateDirectories(facts.FullPath, facts.RelativePath))
				{
					cancellationToken.ThrowIfCancellationRequested();

					var childFacts = AnalyzeDirectory(
						childDirectory.FullPath,
						childDirectory.RelativePath,
						childDirectory.Name,
						childDirectory.IsHidden,
						effectiveRules,
						effectiveGitIgnoreContext,
						effectiveGitIgnoreCandidateContext);
					var childDirectControllerImpactCounts = CountDirectDirectoryControllerImpact(
						childFacts,
						effectiveRules,
						effectiveExtensionPolicy,
						cancellationToken);
					var childExtensionDiscoveryRuleState = EvaluateDirectoryRuleState(
						childFacts,
						extensionDiscoveryRules.IgnoreHiddenFolders,
						extensionDiscoveryRules.IgnoreDotFolders);
					var childBaseRuleState = EvaluateDirectoryRuleState(
						childFacts,
						effectiveRules.IgnoreHiddenFolders,
						effectiveRules.IgnoreDotFolders);
					var childHiddenFoldersRuleState = EvaluateDirectoryRuleState(
						childFacts,
						!effectiveRules.IgnoreHiddenFolders,
						effectiveRules.IgnoreDotFolders);
					var childDotFoldersRuleState = EvaluateDirectoryRuleState(
						childFacts,
						ShouldApplyHiddenFoldersForDotFoldersVariant(childFacts, effectiveRules),
						!effectiveRules.IgnoreDotFolders);

					if (!CanAnyVariantTraverseChildren(
						    childExtensionDiscoveryRuleState,
						    childBaseRuleState,
						    childHiddenFoldersRuleState,
						    childDotFoldersRuleState) &&
					    childDirectControllerImpactCounts == IgnoreControllerImpactCounts.Empty)
					{
						continue;
					}

					pending.Push((
						childFacts,
						currentDirectoryIndex,
						IsRootDirectory: false,
						childDirectControllerImpactCounts,
						childExtensionDiscoveryRuleState,
						childBaseRuleState,
						childHiddenFoldersRuleState,
						childDotFoldersRuleState));
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (UnauthorizedAccessException)
			{
				var deniedNode = directories[currentDirectoryIndex];
				deniedNode.IsAccessDenied = true;
				directories[currentDirectoryIndex] = deniedNode;
				Interlocked.Exchange(ref hadAccessDenied, 1);
				if (isRootDirectory)
					Interlocked.Exchange(ref rootAccessDenied, 1);
			}
			catch
			{
				// Keep best-effort behavior for partial filesystem reads.
			}
		}

		return new ScanResult<List<EffectiveIgnoreScanNode>>(directories, rootAccessDenied == 1, hadAccessDenied == 1);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool ShouldApplyHiddenFoldersForDotFoldersVariant(
		DirectoryScanFacts facts,
		IgnoreRules effectiveRules)
	{
		return IgnoreRuleSemantics.ShouldIgnoreHiddenDirectory(
			effectiveRules.IgnoreHiddenFolders,
			facts.IsHidden,
			facts.IsDot,
			ignoreDotFolders: !effectiveRules.IgnoreDotFolders);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool CanAnyVariantTraverseChildren(
		DirectoryToggleRuleState extensionDiscoveryRuleState,
		DirectoryToggleRuleState baseRuleState,
		DirectoryToggleRuleState hiddenFoldersRuleState,
		DirectoryToggleRuleState dotFoldersRuleState)
	{
		return extensionDiscoveryRuleState.CanTraverseChildren ||
		       baseRuleState.CanTraverseChildren ||
		       hiddenFoldersRuleState.CanTraverseChildren ||
		       dotFoldersRuleState.CanTraverseChildren;
	}

	private static EffectiveIgnoreFinalizeResult FinalizeEffectiveIgnoreCounts(
		IReadOnlyList<EffectiveIgnoreScanNode> directories,
		EffectiveIgnoreNodeFileMetrics[] fileMetrics,
		EffectiveIgnoreNodeVisibilityState[] visibilityStates,
		IgnoreRules effectiveRules)
	{
		for (var index = directories.Count - 1; index >= 0; index--)
		{
			var node = directories[index];
			var visibilityState = visibilityStates[index];
			var metrics = fileMetrics[index];

			var baseHasVisibleContent = visibilityState.IsAccessDenied ||
			                            metrics.BaseVisibleFiles > 0 ||
			                            visibilityState.BaseVisibleChildren > 0;
			var hiddenFoldersHasVisibleContent = visibilityState.IsAccessDenied ||
			                                     metrics.BaseVisibleFiles > 0 ||
			                                     visibilityState.HiddenFoldersVisibleChildren > 0;
			var dotFoldersHasVisibleContent = visibilityState.IsAccessDenied ||
			                                  metrics.BaseVisibleFiles > 0 ||
			                                  visibilityState.DotFoldersVisibleChildren > 0;
			var hiddenFilesHasVisibleContent = visibilityState.IsAccessDenied ||
			                                   metrics.HiddenFilesVisibleFiles > 0 ||
			                                   visibilityState.HiddenFilesVisibleChildren > 0;
			var dotFilesHasVisibleContent = visibilityState.IsAccessDenied ||
			                                metrics.DotFilesVisibleFiles > 0 ||
			                                visibilityState.DotFilesVisibleChildren > 0;
			var emptyFilesHasVisibleContent = visibilityState.IsAccessDenied ||
			                                  metrics.EmptyFilesVisibleFiles > 0 ||
			                                  visibilityState.EmptyFilesVisibleChildren > 0;
			var extensionlessFilesHasVisibleContent = visibilityState.IsAccessDenied ||
			                                          metrics.ExtensionlessFilesVisibleFiles > 0 ||
			                                          visibilityState.ExtensionlessFilesVisibleChildren > 0;
			var emptyFoldersHasVisibleContent = visibilityState.IsAccessDenied ||
			                                    metrics.BaseVisibleFiles > 0 ||
			                                    visibilityState.EmptyFoldersVisibleChildren > 0;
			var controllerBaselineHasVisibleContent = visibilityState.IsAccessDenied ||
			                                          metrics.ControllerBaselineVisibleFiles > 0 ||
			                                          visibilityState.ControllerBaselineVisibleChildren > 0;
			var gitIgnoreHasVisibleContent = visibilityState.IsAccessDenied ||
			                                 metrics.GitIgnoreVisibleFiles > 0 ||
			                                 visibilityState.GitIgnoreVisibleChildren > 0;
			var smartIgnoreHasVisibleContent = visibilityState.IsAccessDenied ||
			                                   metrics.SmartIgnoreVisibleFiles > 0 ||
			                                   visibilityState.SmartIgnoreVisibleChildren > 0;

			visibilityState.BaseLocalVisible = IsDirectoryLocallyVisible(
				node.BaseRuleState,
				effectiveRules.IgnoreEmptyFolders,
				baseHasVisibleContent);
			visibilityState.HiddenFoldersLocalVisible = IsDirectoryLocallyVisible(
				node.HiddenFoldersRuleState,
				effectiveRules.IgnoreEmptyFolders,
				hiddenFoldersHasVisibleContent);
			visibilityState.DotFoldersLocalVisible = IsDirectoryLocallyVisible(
				node.DotFoldersRuleState,
				effectiveRules.IgnoreEmptyFolders,
				dotFoldersHasVisibleContent);
			visibilityState.HiddenFilesLocalVisible = IsDirectoryLocallyVisible(
				node.BaseRuleState,
				effectiveRules.IgnoreEmptyFolders,
				hiddenFilesHasVisibleContent);
			visibilityState.DotFilesLocalVisible = IsDirectoryLocallyVisible(
				node.BaseRuleState,
				effectiveRules.IgnoreEmptyFolders,
				dotFilesHasVisibleContent);
			visibilityState.EmptyFilesLocalVisible = IsDirectoryLocallyVisible(
				node.BaseRuleState,
				effectiveRules.IgnoreEmptyFolders,
				emptyFilesHasVisibleContent);
			visibilityState.ExtensionlessFilesLocalVisible = IsDirectoryLocallyVisible(
				node.BaseRuleState,
				effectiveRules.IgnoreEmptyFolders,
				extensionlessFilesHasVisibleContent);
			visibilityState.EmptyFoldersLocalVisible = IsDirectoryLocallyVisible(
				node.BaseRuleState,
				!effectiveRules.IgnoreEmptyFolders,
				emptyFoldersHasVisibleContent);
			visibilityState.ControllerBaselineLocalVisible = IsDirectoryLocallyVisible(
				node.BaseRuleState,
				effectiveRules.IgnoreEmptyFolders,
				controllerBaselineHasVisibleContent);
			visibilityState.GitIgnoreLocalVisible = IsDirectoryLocallyVisible(
				node.BaseRuleState,
				effectiveRules.IgnoreEmptyFolders,
				gitIgnoreHasVisibleContent);
			visibilityState.SmartIgnoreLocalVisible = IsDirectoryLocallyVisible(
				node.BaseRuleState,
				effectiveRules.IgnoreEmptyFolders,
				smartIgnoreHasVisibleContent);
			visibilityStates[index] = visibilityState;

			if (node.ParentIndex < 0)
				continue;

			var parentVisibilityState = visibilityStates[node.ParentIndex];
			if (visibilityState.BaseLocalVisible)
				parentVisibilityState.BaseVisibleChildren++;
			if (visibilityState.HiddenFoldersLocalVisible)
				parentVisibilityState.HiddenFoldersVisibleChildren++;
			if (visibilityState.DotFoldersLocalVisible)
				parentVisibilityState.DotFoldersVisibleChildren++;
			if (visibilityState.HiddenFilesLocalVisible)
				parentVisibilityState.HiddenFilesVisibleChildren++;
			if (visibilityState.DotFilesLocalVisible)
				parentVisibilityState.DotFilesVisibleChildren++;
			if (visibilityState.EmptyFilesLocalVisible)
				parentVisibilityState.EmptyFilesVisibleChildren++;
			if (visibilityState.ExtensionlessFilesLocalVisible)
				parentVisibilityState.ExtensionlessFilesVisibleChildren++;
			if (visibilityState.EmptyFoldersLocalVisible)
				parentVisibilityState.EmptyFoldersVisibleChildren++;
			if (visibilityState.ControllerBaselineLocalVisible)
				parentVisibilityState.ControllerBaselineVisibleChildren++;
			if (visibilityState.GitIgnoreLocalVisible)
				parentVisibilityState.GitIgnoreVisibleChildren++;
			if (visibilityState.SmartIgnoreLocalVisible)
				parentVisibilityState.SmartIgnoreVisibleChildren++;
			visibilityStates[node.ParentIndex] = parentVisibilityState;
		}

		var effectiveCounts = default(MutableIgnoreOptionCounts);
		var controllerImpactCounts = IgnoreControllerImpactCounts.Empty;
		for (var index = 0; index < directories.Count; index++)
		{
			var node = directories[index];
			var visibilityState = visibilityStates[index];
			var metrics = fileMetrics[index];

			if (node.ParentIndex < 0)
			{
				visibilityState.BaseFinalVisible = visibilityState.BaseLocalVisible;
				visibilityState.HiddenFoldersFinalVisible = visibilityState.HiddenFoldersLocalVisible;
				visibilityState.DotFoldersFinalVisible = visibilityState.DotFoldersLocalVisible;
				visibilityState.HiddenFilesFinalVisible = visibilityState.HiddenFilesLocalVisible;
				visibilityState.DotFilesFinalVisible = visibilityState.DotFilesLocalVisible;
				visibilityState.EmptyFilesFinalVisible = visibilityState.EmptyFilesLocalVisible;
				visibilityState.ExtensionlessFilesFinalVisible = visibilityState.ExtensionlessFilesLocalVisible;
				visibilityState.EmptyFoldersFinalVisible = visibilityState.EmptyFoldersLocalVisible;
				visibilityState.ControllerBaselineFinalVisible = visibilityState.ControllerBaselineLocalVisible;
				visibilityState.GitIgnoreFinalVisible = visibilityState.GitIgnoreLocalVisible;
				visibilityState.SmartIgnoreFinalVisible = visibilityState.SmartIgnoreLocalVisible;
			}
			else
			{
				var parentVisibilityState = visibilityStates[node.ParentIndex];
				visibilityState.BaseFinalVisible = parentVisibilityState.BaseFinalVisible &&
				                                  visibilityState.BaseLocalVisible;
				visibilityState.HiddenFoldersFinalVisible = parentVisibilityState.HiddenFoldersFinalVisible &&
				                                           visibilityState.HiddenFoldersLocalVisible;
				visibilityState.DotFoldersFinalVisible = parentVisibilityState.DotFoldersFinalVisible &&
				                                        visibilityState.DotFoldersLocalVisible;
				visibilityState.HiddenFilesFinalVisible = parentVisibilityState.HiddenFilesFinalVisible &&
				                                         visibilityState.HiddenFilesLocalVisible;
				visibilityState.DotFilesFinalVisible = parentVisibilityState.DotFilesFinalVisible &&
				                                      visibilityState.DotFilesLocalVisible;
				visibilityState.EmptyFilesFinalVisible = parentVisibilityState.EmptyFilesFinalVisible &&
				                                        visibilityState.EmptyFilesLocalVisible;
				visibilityState.ExtensionlessFilesFinalVisible = parentVisibilityState.ExtensionlessFilesFinalVisible &&
				                                                 visibilityState.ExtensionlessFilesLocalVisible;
				visibilityState.EmptyFoldersFinalVisible = parentVisibilityState.EmptyFoldersFinalVisible &&
				                                          visibilityState.EmptyFoldersLocalVisible;
				visibilityState.ControllerBaselineFinalVisible = parentVisibilityState.ControllerBaselineFinalVisible &&
				                                                visibilityState.ControllerBaselineLocalVisible;
				visibilityState.GitIgnoreFinalVisible = parentVisibilityState.GitIgnoreFinalVisible &&
				                                       visibilityState.GitIgnoreLocalVisible;
				visibilityState.SmartIgnoreFinalVisible = parentVisibilityState.SmartIgnoreFinalVisible &&
				                                         visibilityState.SmartIgnoreLocalVisible;
			}

			visibilityStates[index] = visibilityState;

			if (node.IsHidden &&
			    HasDirectoryToggleImpact(
				    node,
				    visibilityStates,
				    node.HiddenFoldersRuleState,
				    visibilityState.BaseFinalVisible,
				    visibilityState.HiddenFoldersFinalVisible))
			{
				effectiveCounts.HiddenFolders++;
			}

			if (node.IsDot &&
			    HasDirectoryToggleImpact(
				    node,
				    visibilityStates,
				    node.DotFoldersRuleState,
				    visibilityState.BaseFinalVisible,
				    visibilityState.DotFoldersFinalVisible))
			{
				effectiveCounts.DotFolders++;
			}

			if (visibilityState.BaseFinalVisible)
			{
				effectiveCounts.HiddenFiles += metrics.HiddenFilesDisappearWhenToggled;
				effectiveCounts.DotFiles += metrics.DotFilesDisappearWhenToggled;
				effectiveCounts.EmptyFiles += metrics.EmptyFilesDisappearWhenToggled;
				effectiveCounts.ExtensionlessFiles += metrics.ExtensionlessFilesDisappearWhenToggled;
			}

			if (visibilityState.HiddenFilesFinalVisible)
				effectiveCounts.HiddenFiles += metrics.HiddenFilesAppearWhenToggled;
			if (visibilityState.DotFilesFinalVisible)
				effectiveCounts.DotFiles += metrics.DotFilesAppearWhenToggled;
			if (visibilityState.EmptyFilesFinalVisible)
				effectiveCounts.EmptyFiles += metrics.EmptyFilesAppearWhenToggled;
			if (visibilityState.ExtensionlessFilesFinalVisible)
				effectiveCounts.ExtensionlessFiles += metrics.ExtensionlessFilesAppearWhenToggled;

			if (visibilityState.BaseFinalVisible != visibilityState.EmptyFoldersFinalVisible)
				effectiveCounts.EmptyFolders++;

			controllerImpactCounts = controllerImpactCounts.Add(node.DirectControllerImpactCounts);
			if (visibilityState.ControllerBaselineFinalVisible)
			{
				controllerImpactCounts = controllerImpactCounts.Add(new IgnoreControllerImpactCounts(
					GitIgnore: Math.Abs(metrics.ControllerBaselineVisibleFiles - metrics.GitIgnoreVisibleFiles),
					SmartIgnore: Math.Abs(metrics.ControllerBaselineVisibleFiles - metrics.SmartIgnoreVisibleFiles)));
			}

			if (visibilityState.ControllerBaselineFinalVisible != visibilityState.GitIgnoreFinalVisible)
				controllerImpactCounts = controllerImpactCounts.Add(new IgnoreControllerImpactCounts(GitIgnore: 1));
			if (visibilityState.ControllerBaselineFinalVisible != visibilityState.SmartIgnoreFinalVisible)
				controllerImpactCounts = controllerImpactCounts.Add(new IgnoreControllerImpactCounts(SmartIgnore: 1));
		}

		return new EffectiveIgnoreFinalizeResult(
			effectiveCounts.ToImmutable(),
			controllerImpactCounts);
	}

	private readonly record struct EffectiveIgnoreFinalizeResult(
		IgnoreOptionCounts IgnoreOptionCounts,
		IgnoreControllerImpactCounts ControllerImpactCounts);

	private static bool HasDirectoryToggleImpact(
		in EffectiveIgnoreScanNode node,
		EffectiveIgnoreNodeVisibilityState[] visibilityStates,
		in DirectoryToggleRuleState toggledRuleState,
		bool baseFinalVisible,
		bool toggledFinalVisible)
	{
		var parentBaseVisible = node.ParentIndex < 0 ||
		                        visibilityStates[node.ParentIndex].BaseFinalVisible;
		if (!parentBaseVisible)
			return false;

		// A directory-level toggle affects the tree as soon as it changes whether a
		// reachable directory node can be entered. Do not require visible child files:
		// those files may use extensions that are discoverable only after the directory
		// toggle is disabled, and empty-folder filtering must not hide the toggle itself.
		if (node.BaseRuleState.CanTraverseChildren != toggledRuleState.CanTraverseChildren)
			return true;

		return baseFinalVisible != toggledFinalVisible;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void AccumulateDirectFileDelta(
		bool matchesTargetRule,
		bool baseVisible,
		bool toggledVisible,
		ref int count)
	{
		if (matchesTargetRule && baseVisible != toggledVisible)
			count++;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void AccumulateToggleTransition(
		bool matchesTargetRule,
		bool baseVisible,
		bool toggledVisible,
		ref int appearsWhenToggled,
		ref int disappearsWhenToggled)
	{
		if (!matchesTargetRule || baseVisible == toggledVisible)
			return;

		if (toggledVisible)
			appearsWhenToggled++;
		else
			disappearsWhenToggled++;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static IgnoreControllerImpactCounts CountFileControllerImpact(
		in EffectiveFileVisibilityProfile visibility)
	{
		return new IgnoreControllerImpactCounts(
			GitIgnore: visibility.ControllerBaselineVisible != visibility.GitIgnoreVisible ? 1 : 0,
			SmartIgnore: visibility.ControllerBaselineVisible != visibility.SmartIgnoreVisible ? 1 : 0);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void AddExtensionEntry(
		string fileName,
		string extension,
		bool isExtensionless,
		ICollection<string> extensions,
		bool skipExtensionlessEntry = false)
	{
		if (isExtensionless)
		{
			if (skipExtensionlessEntry)
				return;

			extensions.Add(fileName);
			return;
		}

		if (!string.IsNullOrWhiteSpace(extension))
			extensions.Add(extension);
	}

	private ScanResult<int> ScanEffectiveEmptyFolderCountCore(
		string rootPath,
		IReadOnlySet<string> allowedExtensions,
		IgnoreRules rules,
		CancellationToken cancellationToken)
	{
		var scan = ScanEffectiveIgnoreOptionCountsCore(rootPath, allowedExtensions, rules, cancellationToken);
		return new ScanResult<int>(scan.Value.EmptyFolders, scan.RootAccessDenied, scan.HadAccessDenied);
	}

	private static RootSelectionScanPlan BuildRootSelectionScanPlan(
		string rootPath,
		IReadOnlyCollection<string> selectedRootFolders,
		IgnoreRules effectiveRules,
		bool includeDirectoryToggleProbeRoots,
		bool includeControllerImpactProbeRoots,
		CancellationToken cancellationToken)
	{
		var selectedRootPaths = new List<string>(selectedRootFolders.Count);
		var directoryToggleCandidates = new List<FileSystemDirectoryEntry>();
		var controllerImpactCandidates = new List<DirectoryScanFacts>();
		if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
		{
			return new RootSelectionScanPlan(
				selectedRootPaths,
				directoryToggleCandidates,
				controllerImpactCandidates,
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		var selectedNames = new HashSet<string>(PathComparer.Default);
		foreach (var selectedRootFolder in selectedRootFolders)
		{
			if (!IsSafeRelativeRootFolderName(selectedRootFolder))
				continue;

			selectedNames.Add(selectedRootFolder);
		}

		var effectiveGitIgnoreContext = effectiveRules.CreateGitIgnoreScanContext(rootPath);
		var effectiveGitIgnoreCandidateContext = effectiveRules.CreateGitIgnoreCandidateScanContext(rootPath);

		try
		{
			foreach (var directory in FileSystemEntryEnumerator.EnumerateDirectories(rootPath))
			{
				cancellationToken.ThrowIfCancellationRequested();

				if (selectedNames.Contains(directory.Name))
				{
					selectedRootPaths.Add(directory.FullPath);
					continue;
				}

				if (includeDirectoryToggleProbeRoots &&
				    IsPotentialDirectoryToggleCandidate(directory, effectiveRules))
				{
					directoryToggleCandidates.Add(directory);
				}

				if (includeControllerImpactProbeRoots)
				{
					var facts = AnalyzeDirectory(
						directory.FullPath,
						directory.RelativePath,
						directory.Name,
						directory.IsHidden,
						effectiveRules,
						effectiveGitIgnoreContext,
						effectiveGitIgnoreCandidateContext);
					if (IsPotentialControllerImpactCandidate(facts, effectiveRules))
						controllerImpactCandidates.Add(facts);
				}
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (UnauthorizedAccessException)
		{
			AddSelectedRootPathsByName(rootPath, selectedNames, selectedRootPaths);
			return new RootSelectionScanPlan(
				selectedRootPaths,
				directoryToggleCandidates,
				controllerImpactCandidates,
				RootAccessDenied: true,
				HadAccessDenied: true);
		}
		catch
		{
			AddSelectedRootPathsByName(rootPath, selectedNames, selectedRootPaths);
		}

		return new RootSelectionScanPlan(
			selectedRootPaths,
			directoryToggleCandidates,
			controllerImpactCandidates,
			RootAccessDenied: false,
			HadAccessDenied: false);
	}

	private static void AddSelectedRootPathsByName(
		string rootPath,
		IReadOnlyCollection<string> selectedNames,
		List<string> selectedRootPaths)
	{
		if (selectedNames.Count == 0)
			return;

		string normalizedRootPath;
		try
		{
			normalizedRootPath = PathUtility.Normalize(rootPath);
		}
		catch
		{
			return;
		}

		foreach (var selectedName in selectedNames)
		{
			try
			{
				var fullPath = PathUtility.Normalize(Path.Combine(rootPath, selectedName));
				if (PathComparer.Default.Equals(fullPath, normalizedRootPath) ||
				    !PathUtility.IsPathInside(fullPath, normalizedRootPath) ||
				    !Directory.Exists(fullPath) ||
				    IsReparsePointDirectory(fullPath))
				{
					continue;
				}

				selectedRootPaths.Add(fullPath);
			}
			catch
			{
				// Invalid stale root-folder selections are ignored by design.
			}
		}
	}

	private static bool IsSafeRelativeRootFolderName(string? selectedRootFolder)
	{
		return !string.IsNullOrWhiteSpace(selectedRootFolder) &&
		       !Path.IsPathRooted(selectedRootFolder) &&
		       selectedRootFolder.IndexOf(Path.DirectorySeparatorChar) < 0 &&
		       selectedRootFolder.IndexOf(Path.AltDirectorySeparatorChar) < 0;
	}

	private static bool IsPotentialDirectoryToggleCandidate(
		FileSystemDirectoryEntry directory,
		IgnoreRules effectiveRules)
	{
		if (!effectiveRules.IgnoreDotFolders && !effectiveRules.IgnoreHiddenFolders)
			return false;

		var isDotFolder = IgnoreRuleSemantics.IsDotName(directory.Name);
		var isHiddenByCurrentHiddenFolderRule = IgnoreRuleSemantics.ShouldIgnoreHiddenDirectory(
			effectiveRules.IgnoreHiddenFolders,
			directory.IsHidden,
			isDotFolder,
			effectiveRules.IgnoreDotFolders);
		if (isDotFolder &&
		    (effectiveRules.IgnoreDotFolders || !isHiddenByCurrentHiddenFolderRule))
		{
			return true;
		}

		return isHiddenByCurrentHiddenFolderRule;
	}

	private static bool IsPotentialControllerImpactCandidate(
		in DirectoryScanFacts facts,
		IgnoreRules effectiveRules)
	{
		if (!PassesNonControllerDirectoryRules(facts, effectiveRules))
			return false;

		if (facts.GitIgnoreCandidateEvaluation.IsIgnored &&
		    !facts.GitIgnoreCandidateEvaluation.ShouldTraverseIgnoredDirectory)
		{
			return true;
		}

		return facts.IsSmartIgnoredCandidate;
	}

	private static ScanResult<IgnoreOptionCounts> CountRootDirectoryToggleCandidates(
		IReadOnlyList<FileSystemDirectoryEntry> candidateDirectories,
		IgnoreRules effectiveRules,
		CancellationToken cancellationToken)
	{
		if (candidateDirectories.Count == 0 ||
		    (!effectiveRules.IgnoreDotFolders && !effectiveRules.IgnoreHiddenFolders))
		{
			return new ScanResult<IgnoreOptionCounts>(
				IgnoreOptionCounts.Empty,
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		var hiddenFolders = 0;
		var dotFolders = 0;
		var parallelOptions = ScanParallelismPolicy.CreateOptions(cancellationToken);

		Parallel.ForEach(
			candidateDirectories,
			parallelOptions,
			() => new RootDirectoryToggleCandidateAccumulator(),
			(directory, _, localCounts) =>
			{
				parallelOptions.CancellationToken.ThrowIfCancellationRequested();
				if (IsSuppressedByNonDirectoryToggleRule(directory.FullPath, directory.Name, effectiveRules))
					return localCounts;

				var isDotFolder = IgnoreRuleSemantics.IsDotName(directory.Name);
				var isHiddenByCurrentHiddenFolderRule =
					IgnoreRuleSemantics.ShouldIgnoreHiddenDirectory(
						effectiveRules.IgnoreHiddenFolders,
						directory.IsHidden,
						isDotFolder,
						effectiveRules.IgnoreDotFolders);
				var shouldCountDotFolder =
					isDotFolder &&
					(effectiveRules.IgnoreDotFolders || !isHiddenByCurrentHiddenFolderRule);

				if (shouldCountDotFolder)
				{
					// A root-level dot directory is direct evidence for the DotFolders
					// toggle. Do not let EmptyFolders mask this evidence, otherwise a
					// .cache/.idea root can disappear for one refresh and reappear on the
					// next one after the DotFolders option was not persisted as visible.
					localCounts.DotFolders++;
				}

				if (isHiddenByCurrentHiddenFolderRule)
				{
					// Same contract as DotFolders: native hidden root folders are direct
					// directory-toggle evidence and must not depend on content-level rules.
					localCounts.HiddenFolders++;
				}

				return localCounts;
			},
			localCounts =>
			{
				if (localCounts.IsEmpty)
					return;

				Interlocked.Add(ref hiddenFolders, localCounts.HiddenFolders);
				Interlocked.Add(ref dotFolders, localCounts.DotFolders);
			});

		return new ScanResult<IgnoreOptionCounts>(
			new IgnoreOptionCounts(HiddenFolders: hiddenFolders, DotFolders: dotFolders),
			RootAccessDenied: false,
			HadAccessDenied: false);
	}

	private static ScanResult<IgnoreControllerImpactCounts> CountRootDirectoryControllerImpactCandidates(
		IReadOnlyList<DirectoryScanFacts> candidateDirectories,
		IgnoreRules effectiveRules,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		CancellationToken cancellationToken)
	{
		if (candidateDirectories.Count == 0)
		{
			return new ScanResult<IgnoreControllerImpactCounts>(
				IgnoreControllerImpactCounts.Empty,
				RootAccessDenied: false,
				HadAccessDenied: false);
		}

		var counts = IgnoreControllerImpactCounts.Empty;
		var mergeLock = new object();
		var parallelOptions = ScanParallelismPolicy.CreateOptions(cancellationToken);

		Parallel.ForEach(
			candidateDirectories,
			parallelOptions,
			() => IgnoreControllerImpactCounts.Empty,
			(facts, _, localCounts) =>
			{
				parallelOptions.CancellationToken.ThrowIfCancellationRequested();
				return localCounts.Add(CountDirectDirectoryControllerImpact(
					facts,
					effectiveRules,
					effectiveExtensionPolicy,
					parallelOptions.CancellationToken,
					requireVisibleContentWhenEmptyFoldersIgnored: false));
			},
			localCounts =>
			{
				if (localCounts == IgnoreControllerImpactCounts.Empty)
					return;

				lock (mergeLock)
					counts = counts.Add(localCounts);
			});

		return new ScanResult<IgnoreControllerImpactCounts>(
			counts,
			RootAccessDenied: false,
			HadAccessDenied: false);
	}

	private static bool IsSuppressedByNonDirectoryToggleRule(
		string directoryPath,
		string name,
		IgnoreRules rules)
	{
		if (rules.IsSmartIgnoredDirectory(directoryPath, name))
			return true;

		if (!rules.UseGitIgnore)
			return false;

		var gitIgnoreContext = rules.CreateGitIgnoreScanContext(directoryPath);
		var gitIgnore = gitIgnoreContext.Evaluate(directoryPath, string.Empty, isDirectory: true, name);
		return gitIgnore.IsIgnored && !gitIgnore.ShouldTraverseIgnoredDirectory;
	}

	private static ScanResult<bool> HasVisibleContentForDirectoryToggleCandidate(
		string rootPath,
		IgnoreRules rules,
		CancellationToken cancellationToken)
	{
		var rootName = Path.GetFileName(rootPath);
		var gitIgnoreContext = rules.CreateGitIgnoreScanContext(rootPath);
		if (IsDirectorySuppressedByRules(
			    rootPath,
			    relativePath: string.Empty,
			    rootName,
			    HasHiddenAttribute(rootPath),
			    rules,
			    gitIgnoreContext))
		{
			return new ScanResult<bool>(false, RootAccessDenied: false, HadAccessDenied: false);
		}

		var hadAccessDenied = false;
		var pending = new Stack<(string Path, string RelativePath)>();
		pending.Push((rootPath, RelativePath: string.Empty));

		while (pending.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var (currentPath, relativePath) = pending.Pop();

			try
			{
				foreach (var file in FileSystemEntryEnumerator.EnumerateFiles(currentPath, relativePath))
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (IsFileVisibleForDirectoryToggleCandidate(file, rules, gitIgnoreContext))
						return new ScanResult<bool>(true, RootAccessDenied: false, HadAccessDenied: hadAccessDenied);
				}

				foreach (var directory in FileSystemEntryEnumerator.EnumerateDirectories(currentPath, relativePath))
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (IsDirectorySuppressedByRules(
						    directory.FullPath,
						    directory.RelativePath,
						    directory.Name,
						    directory.IsHidden,
						    rules,
						    gitIgnoreContext))
					{
						continue;
					}

					pending.Push((directory.FullPath, directory.RelativePath));
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (UnauthorizedAccessException)
			{
				hadAccessDenied = true;
				return new ScanResult<bool>(true, RootAccessDenied: currentPath == rootPath, HadAccessDenied: true);
			}
			catch
			{
				// Best-effort: unreadable children should not destabilize ignore availability.
			}
		}

		return new ScanResult<bool>(false, RootAccessDenied: false, HadAccessDenied: hadAccessDenied);
	}

	private static bool IsDirectorySuppressedByRules(
		string directoryPath,
		string relativePath,
		string name,
		bool isHidden,
		IgnoreRules rules,
		IgnoreRules.GitIgnoreScanContext gitIgnoreContext)
	{
		if (rules.IsSmartIgnoredDirectory(directoryPath, name))
			return true;

		if (rules.UseGitIgnore)
		{
			var gitIgnore = gitIgnoreContext.Evaluate(directoryPath, relativePath, isDirectory: true, name);
			if (gitIgnore.IsIgnored && !gitIgnore.ShouldTraverseIgnoredDirectory)
				return true;
		}

		var isDot = IgnoreRuleSemantics.IsDotName(name);
		if (IgnoreRuleSemantics.ShouldIgnoreDotDirectory(rules.IgnoreDotFolders, isDot))
			return true;

		if (IgnoreRuleSemantics.ShouldIgnoreHiddenDirectory(
			    rules.IgnoreHiddenFolders,
			    isHidden,
			    isDot,
			    rules.IgnoreDotFolders))
		{
			return true;
		}

		return false;
	}

	private static bool IsFileVisibleForDirectoryToggleCandidate(
		FileSystemFileEntry file,
		IgnoreRules rules,
		IgnoreRules.GitIgnoreScanContext gitIgnoreContext)
	{
		if (rules.UseGitIgnore &&
		    gitIgnoreContext.Evaluate(file.FullPath, file.RelativePath, isDirectory: false, file.Name).IsIgnored)
		{
			return false;
		}

		if (rules.IsSmartIgnoredFile(
			    file.FullPath,
			    file.Name,
			    rules.ShouldApplySmartIgnore(file.FullPath, isDirectory: false)))
		{
			return false;
		}

		var isDot = IgnoreRuleSemantics.IsDotName(file.Name);
		if (IgnoreRuleSemantics.ShouldIgnoreDotFile(rules.IgnoreDotFiles, isDot))
			return false;

		if (IgnoreRuleSemantics.ShouldIgnoreHiddenFile(
			    rules.IgnoreHiddenFiles,
			    file.IsHidden,
			    isDot,
			    rules.IgnoreDotFiles))
		{
			return false;
		}

		if (rules.IgnoreEmptyFiles && file.Length == 0)
			return false;

		if (rules.IgnoreExtensionlessFiles && IsExtensionlessFileName(file.Name))
			return false;

		return true;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void AccumulateDirectoryIgnoreOptionCounts(
		FileSystemDirectoryEntry entry,
		ref MutableIgnoreOptionCounts counts)
	{
		if (IgnoreRuleSemantics.IsDotName(entry.Name))
			counts.DotFolders++;
		if (entry.IsHidden)
			counts.HiddenFolders++;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void AccumulateFileIgnoreOptionCounts(
		FileSystemFileEntry entry,
		ref MutableIgnoreOptionCounts counts)
	{
		if (IsExtensionlessFileName(entry.Name))
			counts.ExtensionlessFiles++;
		if (entry.Length == 0)
			counts.EmptyFiles++;
		if (IgnoreRuleSemantics.IsDotName(entry.Name))
			counts.DotFiles++;
		if (entry.IsHidden)
			counts.HiddenFiles++;
	}

	private static bool HasHiddenAttribute(string fullPath)
	{
		try
		{
			return File.GetAttributes(fullPath).HasFlag(FileAttributes.Hidden);
		}
		catch
		{
			return false;
		}
	}

	private static bool IsReparsePointDirectory(string path)
	{
		try
		{
			return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
		}
		catch
		{
			return true;
		}
	}

	private static long GetFileLength(string fullPath)
	{
		try
		{
			return new FileInfo(fullPath).Length;
		}
		catch
		{
			return 0;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool IsExtensionlessFileName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return false;

		// Use Span to find extension without allocation
		var span = name.AsSpan();
		var dotIndex = span.LastIndexOf('.');

		// No dot found or dot is first char (like .gitignore)
		if (dotIndex <= 0)
			return dotIndex != 0;

		// Dot is at the end (like "file.")
		return dotIndex == span.Length - 1;
	}

}
