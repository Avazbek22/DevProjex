namespace DevProjex.Tests.Integration;

public sealed class IgnoreOptionRootExtensionGoldenMatrixIntegrationTests
{
	private static readonly IgnoreOptionId[] AllIgnoreOptions =
	[
		IgnoreOptionId.UseGitIgnore,
		IgnoreOptionId.SmartIgnore,
		IgnoreOptionId.HiddenFolders,
		IgnoreOptionId.HiddenFiles,
		IgnoreOptionId.DotFolders,
		IgnoreOptionId.DotFiles,
		IgnoreOptionId.EmptyFolders,
		IgnoreOptionId.EmptyFiles,
		IgnoreOptionId.ExtensionlessFiles
	];

	[Theory]
	[MemberData(nameof(GoldenExtensionCases))]
	public void ProjectWorkspaceScan_ExtensionDiscoveryMatchesIndependentOracle(GoldenExtensionCase testCase)
	{
		using var temp = new TemporaryDirectory();
		var workspace = SeedGoldenWorkspace(temp);
		var effectiveRules = CreateRules(temp.Path, testCase.IgnoreState.EnabledOptions);
		var extensionDiscoveryRules = BuildExtensionDiscoveryRules(effectiveRules);
		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var selectedRoots = testCase.RootMode.SelectedRootFolders;

		var snapshot = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			selectedRoots,
			extensionDiscoveryRules,
			effectiveRules,
			testCase.ExtensionMode.AllowedExtensions,
			includeDirectoryToggleProbeRoots: true,
			cancellationToken: TestContext.Current.CancellationToken,
			includeControllerImpactProbeRoots: true);

		var actualExtensions = CollectRealExtensions(snapshot.Value.Extensions);
		var expectedExtensions = ComputeExpectedVisibleExtensions(
			workspace,
			selectedRoots,
			extensionDiscoveryRules);

		AssertExtensionSetEquals(
			expectedExtensions,
			actualExtensions,
			$"{testCase}; Counts={snapshot.Value.EffectiveIgnoreOptionCounts}; Controller={snapshot.Value.ControllerImpactCounts}");

		var repeated = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			selectedRoots,
			extensionDiscoveryRules,
			effectiveRules,
			testCase.ExtensionMode.AllowedExtensions,
			includeDirectoryToggleProbeRoots: true,
			cancellationToken: TestContext.Current.CancellationToken,
			includeControllerImpactProbeRoots: true);
		AssertExtensionSetEquals(
			expectedExtensions,
			CollectRealExtensions(repeated.Value.Extensions),
			$"Repeated scan drifted. {testCase}");
		Assert.Equal(snapshot.Value.EffectiveIgnoreOptionCounts, repeated.Value.EffectiveIgnoreOptionCounts);
		Assert.Equal(snapshot.Value.ControllerImpactCounts, repeated.Value.ControllerImpactCounts);
	}

	[Theory]
	[MemberData(nameof(FileLevelToggleInvarianceCases))]
	public void ProjectWorkspaceScan_FileLevelIgnoreTogglesDoNotChangeExtensionDiscovery(
		RootMode rootMode,
		ExtensionMode extensionMode,
		IgnoreOptionId fileLevelOption)
	{
		using var temp = new TemporaryDirectory();
		_ = SeedGoldenWorkspace(temp);
		var allOn = CreateRules(temp.Path, AllIgnoreOptions);
		var forcedOff = CreateRules(temp.Path, AllIgnoreOptions.Where(option => option != fileLevelOption));
		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());

		var allOnSnapshot = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			rootMode.SelectedRootFolders,
			BuildExtensionDiscoveryRules(allOn),
			allOn,
			extensionMode.AllowedExtensions,
			includeDirectoryToggleProbeRoots: true,
			cancellationToken: TestContext.Current.CancellationToken,
			includeControllerImpactProbeRoots: true);
		var forcedOffSnapshot = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			rootMode.SelectedRootFolders,
			BuildExtensionDiscoveryRules(forcedOff),
			forcedOff,
			extensionMode.AllowedExtensions,
			includeDirectoryToggleProbeRoots: true,
			cancellationToken: TestContext.Current.CancellationToken,
			includeControllerImpactProbeRoots: true);

		AssertExtensionSetEquals(
			CollectRealExtensions(allOnSnapshot.Value.Extensions),
			CollectRealExtensions(forcedOffSnapshot.Value.Extensions),
			$"{fileLevelOption} changed extension availability for {rootMode.Name}/{extensionMode.Name}.");
	}

	[Fact]
	public void ProjectWorkspaceScan_AllRulesOnPublishesExpectedGoldenCounts()
	{
		using var temp = new TemporaryDirectory();
		_ = SeedGoldenWorkspace(temp);
		var rules = CreateRules(temp.Path, AllIgnoreOptions);
		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var snapshot = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			RootModes().Single(mode => mode.Name == "all roots").SelectedRootFolders,
			BuildExtensionDiscoveryRules(rules),
			rules,
			effectiveAllowedExtensions: null,
			includeDirectoryToggleProbeRoots: true,
			cancellationToken: TestContext.Current.CancellationToken,
			includeControllerImpactProbeRoots: true);

		var expectedHiddenFolders = OperatingSystem.IsWindows() ? 1 : 0;
		var expectedHiddenFiles = OperatingSystem.IsWindows() ? 2 : 0;

		Assert.Equal(new IgnoreOptionCounts(
				HiddenFolders: expectedHiddenFolders,
				HiddenFiles: expectedHiddenFiles,
				DotFolders: 2,
				DotFiles: 2,
				EmptyFolders: 0,
				ExtensionlessFiles: 2,
				EmptyFiles: 2),
			snapshot.Value.EffectiveIgnoreOptionCounts);
		Assert.Equal(new IgnoreControllerImpactCounts(GitIgnore: 3, SmartIgnore: 3),
			snapshot.Value.ControllerImpactCounts);
	}

	[Fact]
	public void ProjectWorkspaceScan_AllowsDiscoveryRulesToDifferOnlyByFileLevelOptions()
	{
		using var temp = new TemporaryDirectory();
		_ = SeedGoldenWorkspace(temp);
		var effectiveRules = CreateRules(temp.Path, AllIgnoreOptions);
		var scanner = new FileSystemScanner();
		var validRequest = new ProjectWorkspaceScanRequest(
			temp.Path,
			RootModes().Single(mode => mode.Name == "all roots").SelectedRootFolders,
			BuildExtensionDiscoveryRules(effectiveRules),
			effectiveRules,
			EffectiveExtensionPolicy: null,
			CaptureTreeInventory: false,
			IncludeDirectoryToggleProbeRoots: true,
			IncludeControllerImpactProbeRoots: true);

		var valid = scanner.ScanProjectWorkspace(validRequest, TestContext.Current.CancellationToken);
		Assert.NotEmpty(valid.Value.IgnoreSection.Extensions);

		var emptyFoldersDriftRequest = validRequest with
		{
			ExtensionDiscoveryRules = validRequest.ExtensionDiscoveryRules with
			{
				IgnoreEmptyFolders = !effectiveRules.IgnoreEmptyFolders
			}
		};
		var emptyFoldersDrift = scanner.ScanProjectWorkspace(emptyFoldersDriftRequest, TestContext.Current.CancellationToken);
		AssertExtensionSetEquals(
			CollectRealExtensions(valid.Value.IgnoreSection.Extensions),
			CollectRealExtensions(emptyFoldersDrift.Value.IgnoreSection.Extensions),
			"EmptyFolders drift must not change extension discovery.");

		var invalidDiscoveryRules = BuildExtensionDiscoveryRules(effectiveRules) with
		{
			IgnoreDotFolders = false
		};
		var invalidRequest = validRequest with
		{
			ExtensionDiscoveryRules = invalidDiscoveryRules
		};

		var exception = Assert.Throws<ArgumentException>(() =>
			scanner.ScanProjectWorkspace(invalidRequest, TestContext.Current.CancellationToken));
		Assert.Contains("file-level", exception.Message, StringComparison.OrdinalIgnoreCase);
	}

	public static IEnumerable<object[]> GoldenExtensionCases()
	{
		foreach (var rootMode in RootModes())
		foreach (var extensionMode in ExtensionModes())
		foreach (var ignoreState in IgnoreStates())
			yield return [new GoldenExtensionCase(rootMode, extensionMode, ignoreState)];
	}

	public static IEnumerable<object[]> FileLevelToggleInvarianceCases()
	{
		var fileLevelOptions = new[]
		{
			IgnoreOptionId.HiddenFiles,
			IgnoreOptionId.DotFiles,
			IgnoreOptionId.EmptyFiles,
			IgnoreOptionId.ExtensionlessFiles
		};

		foreach (var rootMode in RootModes())
		foreach (var extensionMode in ExtensionModes())
		foreach (var option in fileLevelOptions)
			yield return [rootMode, extensionMode, option];
	}

	private static IEnumerable<RootMode> RootModes()
	{
		yield return new RootMode("root files only", []);
		yield return new RootMode("normal only", ["normal"]);
		yield return new RootMode("dot root only", [".dot-root"]);
		yield return new RootMode("controller roots only", ["git-owned", "node_modules"]);
		yield return new RootMode("mixed only", ["mixed"]);
		yield return new RootMode("all roots", ["normal", ".dot-root", "git-owned", "node_modules", "hidden-root", "mixed"]);
	}

	private static IEnumerable<ExtensionMode> ExtensionModes()
	{
		yield return new ExtensionMode("all extensions", null);
		yield return new ExtensionMode(".cs only", CreateExtensionSet(".cs"));
		yield return new ExtensionMode(".dotpayload only", CreateExtensionSet(".dotpayload"));
		yield return new ExtensionMode(".gitpayload only", CreateExtensionSet(".gitpayload"));
		yield return new ExtensionMode("no extensions", CreateExtensionSet());
	}

	private static HashSet<string> CreateExtensionSet(params string[] extensions)
	{
		return new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
	}

	private static IEnumerable<IgnoreState> IgnoreStates()
	{
		yield return new IgnoreState("all off", []);
		yield return new IgnoreState("all on", AllIgnoreOptions);

		foreach (var option in AllIgnoreOptions)
			yield return new IgnoreState($"only {option} on", [option]);

		foreach (var option in AllIgnoreOptions)
			yield return new IgnoreState($"all except {option}", AllIgnoreOptions.Where(candidate => candidate != option));
	}

	private static GoldenWorkspace SeedGoldenWorkspace(TemporaryDirectory temp)
	{
		var files = new List<GoldenFile>();

		AddFile("root.cs", "root");
		AddFile(".rootenv", "root env");
		AddFile("empty.rootempty", string.Empty);
		AddFile("ROOTREADME", "extensionless root");
		AddFile("root-hidden.secret", "hidden root file", hiddenOnWindows: true);

		AddFile("normal/visible.cs", "class Visible {}");
		AddFile("normal/nested/data.json", "{}");
		AddFile("normal/.env", "NORMAL_ENV=test");
		AddFile("normal/empty.empty", string.Empty);
		AddFile("normal/README", "extensionless normal");
		AddFile("normal/hidden.hiddenfile", "hidden normal file", hiddenOnWindows: true);
		AddFile("normal/Thumbs.db", "smart ignored file");
		AddFile("normal/git-file.gitfile", "git ignored file");

		AddFile(".dot-root/inside.dotpayload", "dot root");
		AddFile(".dot-root/nested/inside.dotdeep", "dot root nested");

		AddFile("git-owned/ignored.gitpayload", "git ignored root");
		AddFile("node_modules/pkg/index.js", "smart ignored root");

		AddFile("hidden-root/inside.hiddenpayload", "hidden folder payload");

		AddFile("mixed/visible.mixed", "mixed visible");
		AddFile("mixed/.dot-child/file.childdot", "mixed dot child");
		AddFile("mixed/git-owned-nested/file.nestedgit", "mixed git child");
		AddFile("mixed/node_modules/file.nestedsmart", "mixed smart child");

		if (OperatingSystem.IsWindows())
			MarkHidden(Path.Combine(temp.Path, "hidden-root"));

		return new GoldenWorkspace(files);

		void AddFile(string relativePath, string content, bool hiddenOnWindows = false)
		{
			var fullPath = temp.CreateFile(relativePath, content);
			if (hiddenOnWindows && OperatingSystem.IsWindows())
				MarkHidden(fullPath);

			files.Add(GoldenFile.Create(relativePath, isHidden: hiddenOnWindows && OperatingSystem.IsWindows()));
		}
	}

	private static HashSet<string> ComputeExpectedVisibleExtensions(
		GoldenWorkspace workspace,
		IReadOnlyCollection<string> selectedRootFolders,
		IgnoreRules discoveryRules)
	{
		var selectedRoots = new HashSet<string>(selectedRootFolders, PathComparer.Default);
		var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var file in workspace.Files)
		{
			if (!IsInsideSelectedScanScope(file, selectedRoots))
				continue;

			if (!DirectoryChainIsReachable(file, discoveryRules))
				continue;

			if (!FilePassesDiscovery(file, discoveryRules))
				continue;

			if (!string.IsNullOrWhiteSpace(file.Extension))
				expected.Add(file.Extension);
		}

		return expected;
	}

	private static bool IsInsideSelectedScanScope(GoldenFile file, IReadOnlySet<string> selectedRoots)
	{
		return file.RootFolder is null || selectedRoots.Contains(file.RootFolder);
	}

	private static bool DirectoryChainIsReachable(GoldenFile file, IgnoreRules rules)
	{
		var current = string.Empty;
		foreach (var segment in file.DirectorySegments)
		{
			current = current.Length == 0 ? segment : $"{current}/{segment}";
			if (!CanTraverseDirectory(current, segment, rules))
				return false;
		}

		return true;
	}

	private static bool CanTraverseDirectory(string relativePath, string name, IgnoreRules rules)
	{
		if (rules.UseGitIgnore && IsGitIgnoredDirectory(relativePath))
			return false;

		if (rules.UseSmartIgnore && IsSmartIgnoredDirectory(name))
			return false;

		var isDot = IsDotName(name);
		if (rules.IgnoreDotFolders && isDot)
			return false;

		return !ShouldIgnoreHiddenEntry(
			rules.IgnoreHiddenFolders,
			IsHiddenDirectory(relativePath),
			isDot,
			rules.IgnoreDotFolders);
	}

	private static bool FilePassesDiscovery(GoldenFile file, IgnoreRules rules)
	{
		if (rules.UseGitIgnore && file.RelativePath == "normal/git-file.gitfile")
			return false;

		if (rules.UseSmartIgnore && file.Name == "Thumbs.db")
			return false;

		var isDot = IsDotName(file.Name);
		if (rules.IgnoreDotFiles && isDot)
			return false;

		if (rules.IgnoreExtensionlessFiles && file.IsExtensionless)
			return false;

		if (rules.IgnoreEmptyFiles && file.IsEmpty)
			return false;

		return !ShouldIgnoreHiddenEntry(
			rules.IgnoreHiddenFiles,
			file.IsHidden,
			isDot,
			rules.IgnoreDotFiles);
	}

	private static IgnoreRules CreateRules(string rootPath, IEnumerable<IgnoreOptionId> enabledOptions)
	{
		var enabled = enabledOptions.ToHashSet();
		var smartFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node_modules" };
		var smartFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Thumbs.db" };

		return new IgnoreRules(
			IgnoreHiddenFolders: enabled.Contains(IgnoreOptionId.HiddenFolders),
			IgnoreHiddenFiles: enabled.Contains(IgnoreOptionId.HiddenFiles),
			IgnoreDotFolders: enabled.Contains(IgnoreOptionId.DotFolders),
			IgnoreDotFiles: enabled.Contains(IgnoreOptionId.DotFiles),
			SmartIgnoredFolders: smartFolders,
			SmartIgnoredFiles: smartFiles)
		{
			UseGitIgnore = enabled.Contains(IgnoreOptionId.UseGitIgnore),
			UseSmartIgnore = enabled.Contains(IgnoreOptionId.SmartIgnore),
			IgnoreEmptyFolders = enabled.Contains(IgnoreOptionId.EmptyFolders),
			IgnoreEmptyFiles = enabled.Contains(IgnoreOptionId.EmptyFiles),
			IgnoreExtensionlessFiles = enabled.Contains(IgnoreOptionId.ExtensionlessFiles),
			GitIgnoreMatcher = GitIgnoreMatcher.Build(rootPath,
			[
				"git-owned/",
				"mixed/git-owned-nested/",
				"normal/git-file.gitfile"
			]),
			GitIgnoreCandidateMatcher = GitIgnoreMatcher.Build(rootPath,
			[
				"git-owned/",
				"mixed/git-owned-nested/",
				"normal/git-file.gitfile"
			]),
			SmartIgnoreCandidateFolders = smartFolders,
			SmartIgnoreCandidateFiles = smartFiles
		};
	}

	private static IgnoreRules BuildExtensionDiscoveryRules(IgnoreRules effectiveRules)
	{
		// Extension availability must be independent from file-level ignore toggles. If a
		// hidden/dot/empty/extensionless file owns the only file for an extension, the user
		// still needs to see that extension option and then decide whether to include it.
		return effectiveRules with
		{
			IgnoreHiddenFiles = false,
			IgnoreDotFiles = false,
			IgnoreEmptyFiles = false,
			IgnoreExtensionlessFiles = false
		};
	}

	private static HashSet<string> CollectRealExtensions(IReadOnlyCollection<string> entries)
	{
		return entries
			.Where(static entry => entry.StartsWith('.'))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
	}

	private static void AssertExtensionSetEquals(
		IReadOnlySet<string> expected,
		IReadOnlySet<string> actual,
		string message)
	{
		var missing = expected.Except(actual, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
		var extra = actual.Except(expected, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
		Assert.True(
			missing.Length == 0 && extra.Length == 0,
			$"{message}{Environment.NewLine}Expected({expected.Count})=[{string.Join(", ", expected.Order(StringComparer.OrdinalIgnoreCase))}]" +
			$"{Environment.NewLine}Actual({actual.Count})=[{string.Join(", ", actual.Order(StringComparer.OrdinalIgnoreCase))}]" +
			$"{Environment.NewLine}Missing=[{string.Join(", ", missing)}]; Extra=[{string.Join(", ", extra)}]");
	}

	private static bool IsGitIgnoredDirectory(string relativePath)
	{
		return relativePath is "git-owned" or "mixed/git-owned-nested";
	}

	private static bool IsSmartIgnoredDirectory(string name)
	{
		return string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsHiddenDirectory(string relativePath)
	{
		return OperatingSystem.IsWindows() && relativePath == "hidden-root";
	}

	private static bool IsDotName(string name)
	{
		return name.Length > 0 && name[0] == '.';
	}

	private static bool IsExtensionlessFileName(string fileName)
	{
		if (string.IsNullOrWhiteSpace(fileName))
			return false;

		var dotIndex = fileName.AsSpan().LastIndexOf('.');
		if (dotIndex <= 0)
			return dotIndex != 0;

		return dotIndex == fileName.Length - 1;
	}

	private static bool ShouldIgnoreHiddenEntry(
		bool ignoreHidden,
		bool isHidden,
		bool isDot,
		bool ignoreDotEntry)
	{
		if (!ignoreHidden || !isHidden)
			return false;

		if (!isDot)
			return true;

		if (ignoreDotEntry)
			return false;

		return OperatingSystem.IsWindows();
	}

	private static void MarkHidden(string path)
	{
		File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
	}

	public sealed record GoldenExtensionCase(
		RootMode RootMode,
		ExtensionMode ExtensionMode,
		IgnoreState IgnoreState)
	{
		public override string ToString() => $"{RootMode.Name} / {ExtensionMode.Name} / {IgnoreState.Name}";
	}

	public sealed record RootMode(string Name, string[] SelectedRootFolders)
	{
		public override string ToString() => Name;
	}

	public sealed record ExtensionMode(string Name, IReadOnlySet<string>? AllowedExtensions)
	{
		public override string ToString() => Name;
	}

	public sealed record IgnoreState(string Name, IEnumerable<IgnoreOptionId> EnabledOptions)
	{
		public override string ToString() => Name;
	}

	private sealed record GoldenWorkspace(IReadOnlyList<GoldenFile> Files);

	private sealed record GoldenFile(
		string RelativePath,
		string Name,
		string? RootFolder,
		string[] DirectorySegments,
		string Extension,
		bool IsHidden,
		bool IsEmpty,
		bool IsExtensionless)
	{
		public static GoldenFile Create(string relativePath, bool isHidden)
		{
			var normalized = relativePath.Replace('\\', '/');
			var slashIndex = normalized.IndexOf('/');
			var rootFolder = slashIndex < 0 ? null : normalized[..slashIndex];
			var name = slashIndex < 0
				? normalized
				: normalized[(normalized.LastIndexOf('/') + 1)..];
			var directorySegments = slashIndex < 0
				? []
				: normalized[..normalized.LastIndexOf('/')].Split('/', StringSplitOptions.RemoveEmptyEntries);

			return new GoldenFile(
				normalized,
				name,
				rootFolder,
				directorySegments,
				Path.GetExtension(name),
				isHidden,
				IsEmptyPath(normalized),
				IsExtensionlessFileName(name));
		}

		private static bool IsEmptyPath(string relativePath)
		{
			return relativePath is "empty.rootempty" or "normal/empty.empty";
		}
	}
}
