using DevProjex.Tests.Shared.ProjectLoadWorkflow;
using static DevProjex.Tests.Shared.ProjectLoadWorkflow.ProjectLoadWorkflowRefreshHarness;

namespace DevProjex.Tests.Integration;

public sealed class IgnoreOptionEndToEndAllTogglesMatrixIntegrationTests
{
	[Fact]
	public void FullRefresh_AllIgnoreOptionsOff_RevealsEveryIgnoreCandidateAcrossSections()
	{
		using var workspace = CreateComprehensiveWorkspace();
		var services = CreateServices();

		var defaults = ComputeConvergedSnapshot(services, workspace.Path, CreateDefaultContext(workspace.Path));
		var allOff = ComputeConvergedSnapshot(services, workspace.Path, CreateAllIgnoreOptionsOffContext(workspace.Path, defaults));

		Assert.DoesNotContain(allOff.IgnoreOptions, option => option.IsChecked);
		AssertAllExpectedIgnoreOptionsVisible(allOff);
		AssertExpectedCounts(allOff);
		AssertTreeState(
			workspace.Path,
			allOff,
			visiblePaths: AllCandidatePaths(),
			hiddenPaths: []);
	}

	[Theory]
	[MemberData(nameof(SingleToggleCases))]
	public void FullRefresh_SingleIgnoreOptionOn_HidesOnlyItsOwnCandidates_EndToEnd(SingleToggleCase toggleCase)
	{
		using var workspace = CreateComprehensiveWorkspace();
		var services = CreateServices();

		var defaults = ComputeConvergedSnapshot(services, workspace.Path, CreateDefaultContext(workspace.Path));
		var allOff = ComputeConvergedSnapshot(services, workspace.Path, CreateAllIgnoreOptionsOffContext(workspace.Path, defaults));
		var singleOn = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateSingleIgnoreOptionOnContext(workspace.Path, allOff, toggleCase.OptionId));

		AssertSingleCheckedIgnoreOption(singleOn, toggleCase.OptionId);
		AssertOptionImpact(singleOn, toggleCase);
		AssertTreeState(
			workspace.Path,
			singleOn,
			visiblePaths: AllCandidatePaths()
				.Where(path => !toggleCase.HiddenPaths.Any(hiddenPath => SamePath(path, hiddenPath)))
				.ToArray(),
			hiddenPaths: toggleCase.HiddenPaths);
	}

	[Fact]
	public void FullRefresh_ExtensionFilterOff_KeepsGitIgnoreVisibleWhenIgnoredFolderStillChangesTree()
	{
		using var workspace = CreateComprehensiveWorkspace();
		var services = CreateServices();

		var defaults = ComputeConvergedSnapshot(services, workspace.Path, CreateDefaultContext(workspace.Path));
		var allOff = ComputeConvergedSnapshot(services, workspace.Path, CreateAllIgnoreOptionsOffContext(workspace.Path, defaults));
		var logExtensionOff = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateExtensionStateContext(workspace.Path, allOff, ".log", isChecked: false));

		AssertExtensionOption(logExtensionOff, ".log", expectedChecked: false);
		AssertIgnoreOption(logExtensionOff, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: false);
		AssertTreeState(
			workspace.Path,
			logExtensionOff,
			visiblePaths:
			[
				"api/src/Program.cs",
				"api/logs",
				"web/src/app.ts",
				"general/visible.txt"
			],
			hiddenPaths: ["api/logs/runtime.log"]);
	}

	[Fact]
	public void FullRefresh_ExtensionFilterOffAndEmptyFoldersOn_KeepsExplicitlyUncheckedGitIgnoreReversible()
	{
		using var workspace = CreateComprehensiveWorkspace();
		var services = CreateServices();

		var defaults = ComputeConvergedSnapshot(services, workspace.Path, CreateDefaultContext(workspace.Path));
		var allOff = ComputeConvergedSnapshot(services, workspace.Path, CreateAllIgnoreOptionsOffContext(workspace.Path, defaults));
		var logExtensionOff = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateExtensionStateContext(workspace.Path, allOff, ".log", isChecked: false));
		var logExtensionOffAndEmptyFoldersOn = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateSingleIgnoreOptionOnContext(workspace.Path, logExtensionOff, IgnoreOptionId.EmptyFolders));

		AssertExtensionOption(logExtensionOffAndEmptyFoldersOn, ".log", expectedChecked: false);
		AssertIgnoreOption(logExtensionOffAndEmptyFoldersOn, IgnoreOptionId.EmptyFolders, expectedVisible: true, expectedChecked: true);
		AssertIgnoreOption(logExtensionOffAndEmptyFoldersOn, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: false);
		AssertTreeState(
			workspace.Path,
			logExtensionOffAndEmptyFoldersOn,
			visiblePaths:
			[
				"api/src/Program.cs",
				"web/src/app.ts",
				"general/visible.txt"
			],
			hiddenPaths:
			[
				"api/logs",
				"api/logs/runtime.log",
				"general/empty-root"
			]);
	}

	[Fact]
	public void FullRefresh_RootFilterOff_DoesNotLeakIgnoreCandidatesFromUncheckedRoot()
	{
		using var workspace = CreateComprehensiveWorkspace();
		var services = CreateServices();

		var defaults = ComputeConvergedSnapshot(services, workspace.Path, CreateDefaultContext(workspace.Path));
		var generalRootOff = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateRootStateContext(workspace.Path, defaults, "general", isChecked: false));

		AssertRootOption(generalRootOff, "general", expectedChecked: false);
		AssertNoUncheckedRootCountLeak(generalRootOff);
		AssertTreeState(
			workspace.Path,
			generalRootOff,
			visiblePaths:
			[
				"api/src/Program.cs",
				"web/src/app.ts"
			],
			hiddenPaths:
			[
				"general/visible.txt",
				"general/.config/settings.json",
				"general/.env",
				"general/empty.txt",
				"general/README"
			]);
	}

	[Fact]
	public void FullRefresh_EveryIgnorePowerSetState_MatchesIndependentGoldenPathsInOnePass()
	{
		using var workspace = CreateComprehensiveWorkspace();
		var services = CreateServices();
		var defaults = ComputeConvergedSnapshot(services, workspace.Path, CreateDefaultContext(workspace.Path));
		var allOff = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateAllIgnoreOptionsOffContext(workspace.Path, defaults));
		var cases = SingleToggleCases()
			.Select(static row => Assert.IsType<SingleToggleCase>(row[0]))
			.ToArray();
		var allPaths = AllCandidatePaths();
		var stateCount = 1 << cases.Length;

		for (var mask = 0; mask < stateCount; mask++)
		{
			var selected = new HashSet<IgnoreOptionId>();
			var hiddenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			for (var bit = 0; bit < cases.Length; bit++)
			{
				if ((mask & (1 << bit)) == 0)
					continue;

				selected.Add(cases[bit].OptionId);
				hiddenPaths.UnionWith(cases[bit].HiddenPaths);
			}

			var snapshot = ComputeConvergedSnapshot(
				services,
				workspace.Path,
				CreateIgnoreStateContext(workspace.Path, allOff, selected));
			var visiblePaths = allPaths.Where(path => !hiddenPaths.Contains(path)).ToArray();

			AssertTreeState(workspace.Path, snapshot, visiblePaths, hiddenPaths);
			SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
				workspace.Path,
				services.IgnoreRulesService,
				snapshot);
		}
	}

	public static IEnumerable<object[]> SingleToggleCases()
	{
		yield return [new SingleToggleCase(
			IgnoreOptionId.UseGitIgnore,
			["api/logs/runtime.log"],
			ExpectedCount: null)];
		yield return [new SingleToggleCase(
			IgnoreOptionId.SmartIgnore,
			[
				"api/bin/Debug/app.dll",
				"web/node_modules/pkg/index.js"
			],
			ExpectedCount: null)];
		yield return [new SingleToggleCase(
			IgnoreOptionId.DotFolders,
			["general/.config/settings.json"],
			ExpectedCount: 1)];
		yield return [new SingleToggleCase(
			IgnoreOptionId.DotFiles,
			[
				"api/.gitignore",
				"general/.env"
			],
			ExpectedCount: 2)];
		yield return [new SingleToggleCase(
			IgnoreOptionId.EmptyFolders,
			["general/empty-root"],
			ExpectedCount: 1)];
		yield return [new SingleToggleCase(
			IgnoreOptionId.EmptyFiles,
			["general/empty.txt"],
			ExpectedCount: 1)];
		yield return [new SingleToggleCase(
			IgnoreOptionId.ExtensionlessFiles,
			["general/README"],
			ExpectedCount: 1)];

		if (!OperatingSystem.IsWindows())
			yield break;

		yield return [new SingleToggleCase(
			IgnoreOptionId.HiddenFolders,
			["general/hidden-root/inside.txt"],
			ExpectedCount: 1)];
		yield return [new SingleToggleCase(
			IgnoreOptionId.HiddenFiles,
			["general/hidden-file.secret"],
			ExpectedCount: 1)];
	}

	private static TemporaryDirectory CreateComprehensiveWorkspace()
	{
		var workspace = new TemporaryDirectory();
		workspace.CreateFile("api/.gitignore", "logs/\n");
		workspace.CreateFile("api/App.csproj", "<Project />\n");
		workspace.CreateFile("api/src/Program.cs", "Console.WriteLine(\"ok\");\n");
		workspace.CreateFile("api/bin/Debug/app.dll", "binary\n");
		workspace.CreateFile("api/logs/runtime.log", "ignored\n");
		workspace.CreateFile("web/package.json", "{}\n");
		workspace.CreateFile("web/src/app.ts", "export const ok = true;\n");
		workspace.CreateFile("web/node_modules/pkg/index.js", "module.exports = {};\n");
		workspace.CreateFile("general/visible.txt", "visible\n");
		workspace.CreateFile("general/.config/settings.json", "{}\n");
		workspace.CreateFile("general/.env", "APP_ENV=test\n");
		workspace.CreateDirectory("general/empty-root");
		workspace.CreateFile("general/empty.txt", string.Empty);
		workspace.CreateFile("general/README", "extensionless\n");

		if (OperatingSystem.IsWindows())
		{
			var hiddenRoot = workspace.CreateDirectory("general/hidden-root");
			workspace.CreateFile("general/hidden-root/inside.txt", "hidden folder content\n");
			MarkHidden(hiddenRoot);

			var hiddenFile = workspace.CreateFile("general/hidden-file.secret", "hidden file content\n");
			MarkHidden(hiddenFile);
		}

		return workspace;
	}

	private static SelectionRefreshSnapshot ComputeConvergedSnapshot(
		WorkflowServices services,
		string rootPath,
		SelectionRefreshContext context)
	{
		var snapshot = services.Engine.ComputeFullRefreshSnapshot(
			context,
			TestContext.Current.CancellationToken);
		var repeated = services.Engine.ComputeFullRefreshSnapshot(
			CreateContextFromSnapshot(rootPath, snapshot),
			TestContext.Current.CancellationToken);

		// A single user action must publish the final state. Repeated external refreshes here
		// would hide the exact "checkbox appears only after F5" regression this matrix guards.
		AssertEquivalentSnapshots(snapshot, repeated);
		return snapshot;
	}

	private static SelectionRefreshContext CreateAllIgnoreOptionsOffContext(
		string rootPath,
		SelectionRefreshSnapshot snapshot)
	{
		var stateCache = new Dictionary<IgnoreOptionId, bool>(snapshot.IgnoreOptionStateCache);
		foreach (var option in snapshot.IgnoreOptions)
			stateCache[option.Id] = false;
		foreach (var optionId in stateCache.Keys.ToArray())
			stateCache[optionId] = false;

		return CreateContextFromSnapshot(rootPath, snapshot) with
		{
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = new HashSet<IgnoreOptionId>(),
			IgnoreOptionStateCache = stateCache,
			IgnoreAllPreference = false
		};
	}

	private static SelectionRefreshContext CreateSingleIgnoreOptionOnContext(
		string rootPath,
		SelectionRefreshSnapshot snapshot,
		IgnoreOptionId optionId)
	{
		var stateCache = new Dictionary<IgnoreOptionId, bool>(snapshot.IgnoreOptionStateCache);
		foreach (var existingOptionId in stateCache.Keys.ToArray())
			stateCache[existingOptionId] = false;
		foreach (var option in snapshot.IgnoreOptions)
			stateCache[option.Id] = option.Id == optionId;
		stateCache[optionId] = true;

		return CreateContextFromSnapshot(rootPath, snapshot) with
		{
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = new HashSet<IgnoreOptionId> { optionId },
			IgnoreOptionStateCache = stateCache,
			IgnoreAllPreference = null
		};
	}

	private static SelectionRefreshContext CreateIgnoreStateContext(
		string rootPath,
		SelectionRefreshSnapshot snapshot,
		IReadOnlySet<IgnoreOptionId> selectedOptions)
	{
		var stateCache = snapshot.IgnoreOptionStateCache.ToDictionary(
			static pair => pair.Key,
			pair => selectedOptions.Contains(pair.Key));
		foreach (var option in snapshot.IgnoreOptions)
			stateCache[option.Id] = selectedOptions.Contains(option.Id);

		return CreateContextFromSnapshot(rootPath, snapshot) with
		{
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = new HashSet<IgnoreOptionId>(selectedOptions),
			IgnoreOptionStateCache = stateCache,
			IgnoreAllPreference = null,
			IgnoreOptionStateCacheIsComplete = true,
			CaptureTreeInventory = true
		};
	}

	private static SelectionRefreshContext CreateExtensionStateContext(
		string rootPath,
		SelectionRefreshSnapshot snapshot,
		string extension,
		bool isChecked)
	{
		var extensionStates = BuildExtensionOptionStateCache(snapshot);
		extensionStates[extension] = isChecked;
		var selectedExtensions = snapshot.ExtensionOptions
			.Where(option => !string.Equals(option.Name, extension, StringComparison.OrdinalIgnoreCase) && option.IsChecked)
			.Select(option => option.Name)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (isChecked)
			selectedExtensions.Add(extension);

		return CreateContextFromSnapshot(rootPath, snapshot) with
		{
			AllExtensionsChecked = false,
			ExtensionsSelectionInitialized = true,
			ExtensionsSelectionCache = selectedExtensions,
			ExtensionOptionStateCache = extensionStates
		};
	}

	private static SelectionRefreshContext CreateRootStateContext(
		string rootPath,
		SelectionRefreshSnapshot snapshot,
		string rootName,
		bool isChecked)
	{
		var rootStates = BuildRootOptionStateCache(snapshot);
		rootStates[rootName] = isChecked;
		var selectedRoots = snapshot.RootOptions!
			.Where(option => !string.Equals(option.Name, rootName, StringComparison.Ordinal) && option.IsChecked)
			.Select(option => option.Name)
			.ToHashSet(PathComparer.Default);
		if (isChecked)
			selectedRoots.Add(rootName);

		return CreateContextFromSnapshot(rootPath, snapshot) with
		{
			AllRootFoldersChecked = false,
			RootSelectionInitialized = true,
			RootSelectionCache = selectedRoots,
			RootOptionStateCache = rootStates
		};
	}

	private static TreeBuildResult BuildTreeFromSnapshot(string rootPath, SelectionRefreshSnapshot snapshot)
	{
		var rules = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService()
			.Build(rootPath, CollectCheckedIgnoreOptionIds(snapshot), CollectCheckedRootNames(snapshot));

		return new TreeBuilder().Build(rootPath, new TreeFilterOptions(
			AllowedExtensions: CollectCheckedExtensionNames(snapshot),
			AllowedRootFolders: CollectCheckedRootNames(snapshot),
			IgnoreRules: rules));
	}

	private static void AssertAllExpectedIgnoreOptionsVisible(SelectionRefreshSnapshot snapshot)
	{
		AssertIgnoreOption(snapshot, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: false);
		AssertIgnoreOption(snapshot, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: false);
		AssertIgnoreOption(snapshot, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: false);
		AssertIgnoreOption(snapshot, IgnoreOptionId.DotFiles, expectedVisible: true, expectedChecked: false);
		AssertIgnoreOption(snapshot, IgnoreOptionId.EmptyFolders, expectedVisible: true, expectedChecked: false);
		AssertIgnoreOption(snapshot, IgnoreOptionId.EmptyFiles, expectedVisible: true, expectedChecked: false);
		AssertIgnoreOption(snapshot, IgnoreOptionId.ExtensionlessFiles, expectedVisible: true, expectedChecked: false);

		AssertIgnoreOption(
			snapshot,
			IgnoreOptionId.HiddenFolders,
			expectedVisible: OperatingSystem.IsWindows(),
			expectedChecked: OperatingSystem.IsWindows() ? false : null);
		AssertIgnoreOption(
			snapshot,
			IgnoreOptionId.HiddenFiles,
			expectedVisible: OperatingSystem.IsWindows(),
			expectedChecked: OperatingSystem.IsWindows() ? false : null);
	}

	private static void AssertExpectedCounts(SelectionRefreshSnapshot snapshot)
	{
		Assert.True(snapshot.ControllerImpactCounts.GitIgnore > 0);
		Assert.True(snapshot.ControllerImpactCounts.SmartIgnore > 0);
		Assert.Equal(1, snapshot.IgnoreOptionCounts.DotFolders);
		Assert.Equal(2, snapshot.IgnoreOptionCounts.DotFiles);
		Assert.Equal(1, snapshot.IgnoreOptionCounts.EmptyFolders);
		Assert.Equal(1, snapshot.IgnoreOptionCounts.EmptyFiles);
		Assert.Equal(1, snapshot.IgnoreOptionCounts.ExtensionlessFiles);
		Assert.Equal(OperatingSystem.IsWindows() ? 1 : 0, snapshot.IgnoreOptionCounts.HiddenFolders);
		Assert.Equal(OperatingSystem.IsWindows() ? 1 : 0, snapshot.IgnoreOptionCounts.HiddenFiles);
	}

	private static void AssertNoUncheckedRootCountLeak(SelectionRefreshSnapshot snapshot)
	{
		var leakedCounts = new Dictionary<string, int>
		{
			[nameof(IgnoreOptionCounts.DotFolders)] = snapshot.IgnoreOptionCounts.DotFolders,
			[nameof(IgnoreOptionCounts.EmptyFiles)] = snapshot.IgnoreOptionCounts.EmptyFiles,
			[nameof(IgnoreOptionCounts.ExtensionlessFiles)] = snapshot.IgnoreOptionCounts.ExtensionlessFiles
		};

		var leaks = leakedCounts
			.Where(pair => pair.Value != 0)
			.Select(pair => $"{pair.Key}={pair.Value}")
			.ToArray();

		Assert.True(leaks.Length == 0, $"Unchecked root leaked ignore counts: {string.Join(", ", leaks)}.");
	}

	private static void AssertSingleCheckedIgnoreOption(
		SelectionRefreshSnapshot snapshot,
		IgnoreOptionId optionId)
	{
		var checkedOptions = snapshot.IgnoreOptions
			.Where(option => option.IsChecked)
			.Select(option => option.Id)
			.ToArray();

		Assert.Equal([optionId], checkedOptions);
		AssertIgnoreOption(snapshot, optionId, expectedVisible: true, expectedChecked: true);
	}

	private static void AssertOptionImpact(
		SelectionRefreshSnapshot snapshot,
		SingleToggleCase toggleCase)
	{
		if (toggleCase.OptionId == IgnoreOptionId.UseGitIgnore)
		{
			Assert.True(snapshot.ControllerImpactCounts.GitIgnore > 0);
			return;
		}

		if (toggleCase.OptionId == IgnoreOptionId.SmartIgnore)
		{
			Assert.True(snapshot.ControllerImpactCounts.SmartIgnore > 0);
			return;
		}

		Assert.NotNull(toggleCase.ExpectedCount);
		Assert.Equal(toggleCase.ExpectedCount.Value, GetIgnoreCount(snapshot.IgnoreOptionCounts, toggleCase.OptionId));
	}

	private static int GetIgnoreCount(IgnoreOptionCounts counts, IgnoreOptionId optionId)
	{
		return optionId switch
		{
			IgnoreOptionId.HiddenFolders => counts.HiddenFolders,
			IgnoreOptionId.HiddenFiles => counts.HiddenFiles,
			IgnoreOptionId.DotFolders => counts.DotFolders,
			IgnoreOptionId.DotFiles => counts.DotFiles,
			IgnoreOptionId.EmptyFolders => counts.EmptyFolders,
			IgnoreOptionId.EmptyFiles => counts.EmptyFiles,
			IgnoreOptionId.ExtensionlessFiles => counts.ExtensionlessFiles,
			_ => throw new ArgumentOutOfRangeException(nameof(optionId), optionId, null)
		};
	}

	private static void AssertTreeState(
		string rootPath,
		SelectionRefreshSnapshot snapshot,
		IReadOnlyCollection<string> visiblePaths,
		IReadOnlyCollection<string> hiddenPaths)
	{
		var tree = BuildTreeFromSnapshot(rootPath, snapshot);
		foreach (var visiblePath in visiblePaths)
			Assert.True(ContainsPath(tree.Root, visiblePath), $"Expected path '{visiblePath}' to be visible.");
		foreach (var hiddenPath in hiddenPaths)
			Assert.False(ContainsPath(tree.Root, hiddenPath), $"Expected path '{hiddenPath}' to be hidden.");
	}

	private static bool ContainsPath(FileSystemNode root, string relativePath)
	{
		var current = root;
		foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
		{
			var next = current.Children.FirstOrDefault(child => string.Equals(child.Name, segment, StringComparison.Ordinal));
			if (next is null)
				return false;

			current = next;
		}

		return true;
	}

	private static ResolvedIgnoreOptionState AssertIgnoreOption(
		SelectionRefreshSnapshot snapshot,
		IgnoreOptionId optionId,
		bool expectedVisible,
		bool? expectedChecked)
	{
		var options = snapshot.IgnoreOptions.Where(option => option.Id == optionId).ToArray();
		if (!expectedVisible)
		{
			Assert.Empty(options);
			return default;
		}

		Assert.Single(options);
		if (expectedChecked.HasValue)
			Assert.Equal(expectedChecked.Value, options[0].IsChecked);

		return options[0];
	}

	private static void AssertExtensionOption(
		SelectionRefreshSnapshot snapshot,
		string extension,
		bool expectedChecked)
	{
		var option = Assert.Single(
			snapshot.ExtensionOptions,
			option => string.Equals(option.Name, extension, StringComparison.OrdinalIgnoreCase));
		Assert.Equal(expectedChecked, option.IsChecked);
	}

	private static void AssertRootOption(
		SelectionRefreshSnapshot snapshot,
		string rootName,
		bool expectedChecked)
	{
		var option = Assert.Single(
			snapshot.RootOptions!,
			option => string.Equals(option.Name, rootName, StringComparison.Ordinal));
		Assert.Equal(expectedChecked, option.IsChecked);
	}

	private static string[] AllCandidatePaths()
	{
		var paths = new List<string>
		{
			"api/.gitignore",
			"api/src/Program.cs",
			"api/bin/Debug/app.dll",
			"api/logs/runtime.log",
			"web/src/app.ts",
			"web/node_modules/pkg/index.js",
			"general/visible.txt",
			"general/.config/settings.json",
			"general/.env",
			"general/empty-root",
			"general/empty.txt",
			"general/README"
		};

		if (OperatingSystem.IsWindows())
		{
			paths.Add("general/hidden-root/inside.txt");
			paths.Add("general/hidden-file.secret");
		}

		return [.. paths];
	}

	private static bool SamePath(string left, string right) =>
		string.Equals(left.Replace('\\', '/'), right.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

	private static void MarkHidden(string path)
	{
		var attributes = File.GetAttributes(path);
		File.SetAttributes(path, attributes | FileAttributes.Hidden);
	}

	public sealed record SingleToggleCase(
		IgnoreOptionId OptionId,
		string[] HiddenPaths,
		int? ExpectedCount);
}
