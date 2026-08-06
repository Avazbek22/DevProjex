using DevProjex.Application.Context;
using DevProjex.Terminal.Tui;

namespace DevProjex.Tests.Integration;

[Trait("Category", "IgnoreContract")]
public sealed class ModernLocalProfileEvolutionContractIntegrationTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task MissingStoredSelections_RemainDiagnosticIntentWithoutEnteringEffectiveFilters(
		bool useCompleteStateMaps)
	{
		using var workspace = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.CreateFile("project/src/App.cs", "public sealed class App {}\n");
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: ["removed-root"],
			SelectedExtensions: [".removed"],
			SelectedIgnoreOptions: [],
			RootFolderStates: useCompleteStateMaps
				? new Dictionary<string, bool>(PathComparer.Default) { ["removed-root"] = true }
				: null,
			ExtensionStates: useCompleteStateMaps
				? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { [".removed"] = true }
				: null,
			IgnoreOptionStates: useCompleteStateMaps
				? new Dictionary<IgnoreOptionId, bool>()
				: null);
		var store = new ProjectProfileStore(() => appData.Path);
		store.SaveProfile(project, profile);
		var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		Assert.True(store.TryLoadProfile(project, out var storedProfile));
		var desktopSnapshot = BuildDesktopSnapshot(project, storedProfile);

		var resolved = await services.SelectionResolver.ResolveAsync(
			project,
			ProjectProfileReference.Local,
			new ProjectSelectionSpec(),
			TestContext.Current.CancellationToken);
		var plan = await services.ContextFactory.BuildAsync(
			project,
			resolved,
			cancellationToken: TestContext.Current.CancellationToken);
		using var tuiState = await new TerminalWorkspaceController(services, new TerminalTestHost()).OpenAsync(
			project,
			ProjectProfileReference.Local,
			TestContext.Current.CancellationToken);

		// Stale names are user intent worth reporting, but they are not filesystem rows and
		// must never be injected into effective filters merely to preserve a warning.
		Assert.Equal(2, plan.Analysis.Diagnostics.Warnings.Count);
		Assert.Contains(plan.Analysis.Diagnostics.Warnings, static warning => warning.Contains("removed-root", StringComparison.Ordinal));
		Assert.Contains(plan.Analysis.Diagnostics.Warnings, static warning => warning.Contains(".removed", StringComparison.Ordinal));
		Assert.DoesNotContain("removed-root", plan.SelectedRoots, PathComparer.Default);
		Assert.DoesNotContain(".removed", plan.SelectedExtensions, StringComparer.OrdinalIgnoreCase);
		Assert.Contains("removed-root", plan.Selection.Roots ?? [], PathComparer.Default);
		Assert.Contains(".removed", plan.Selection.Extensions ?? [], StringComparer.OrdinalIgnoreCase);

		// When every stored row disappeared, both source shapes use the existing profile
		// fallback rather than opening an unusable empty workspace. The dedicated legacy
		// test below covers coexistence between retained selections and unseen rows.
		Assert.Contains("src", plan.SelectedRoots, PathComparer.Default);
		Assert.Contains(".cs", plan.SelectedExtensions, StringComparer.OrdinalIgnoreCase);
		Assert.Contains("src", CheckedNames(desktopSnapshot.RootOptions ?? [], PathComparer.Default));
		Assert.Contains(
			".cs",
			CheckedNames(desktopSnapshot.EffectiveExtensionOptions, StringComparer.OrdinalIgnoreCase));
		Assert.Equal(
			plan.SelectedRoots.ToHashSet(PathComparer.Default),
			tuiState.Plan.SelectedRoots.ToHashSet(PathComparer.Default));
		Assert.Equal(
			plan.SelectedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase),
			tuiState.Plan.SelectedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task LegacySelectedOnlyLocalProfile_UsesDefaultsForUnseenRowsAcrossDesktopCliAndTui()
	{
		using var workspace = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.CreateFile("project/global.json", "{}\n");
		workspace.CreateFile("project/src/App.cs", "public sealed class App {}\n");
		workspace.CreateFile("project/docs/guide.md", "# Guide\n");
		var store = new ProjectProfileStore(() => appData.Path);
		store.SaveProfile(
			project,
			new ProjectSelectionProfile(
				SelectedRootFolders: ["src"],
				SelectedExtensions: [".cs"],
				SelectedIgnoreOptions: []));
		Assert.True(store.TryLoadProfile(project, out var storedProfile));
		Assert.True(storedProfile.RootFolderStates!["src"]);
		Assert.True(storedProfile.ExtensionStates![".cs"]);
		Assert.Empty(storedProfile.IgnoreOptionStates!);

		var desktopSnapshot = BuildDesktopSnapshot(project, storedProfile);
		var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var resolved = await services.SelectionResolver.ResolveAsync(
			project,
			ProjectProfileReference.Local,
			new ProjectSelectionSpec(),
			TestContext.Current.CancellationToken);
		var cliPlan = await services.ContextFactory.BuildAsync(
			project,
			resolved,
			cancellationToken: TestContext.Current.CancellationToken);
		using var tuiState = await new TerminalWorkspaceController(services, new TerminalTestHost()).OpenAsync(
			project,
			ProjectProfileReference.Local,
			TestContext.Current.CancellationToken);

		Assert.Equal(
			new HashSet<string>(["docs", "src"], PathComparer.Default),
			CheckedNames(desktopSnapshot.RootOptions ?? [], PathComparer.Default));
		Assert.Equal(
			new HashSet<string>([".cs", ".json", ".md"], StringComparer.OrdinalIgnoreCase),
			CheckedNames(desktopSnapshot.EffectiveExtensionOptions, StringComparer.OrdinalIgnoreCase));
		Assert.Equal(
			new HashSet<string>(["docs", "src"], PathComparer.Default),
			cliPlan.SelectedRoots.ToHashSet(PathComparer.Default));
		Assert.Equal(
			new HashSet<string>([".cs", ".json", ".md"], StringComparer.OrdinalIgnoreCase),
			cliPlan.SelectedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase));
		Assert.Equal(
			new HashSet<string>(["docs/guide.md", "global.json", "src/App.cs"], StringComparer.Ordinal),
			RelativeIncludedFiles(cliPlan));
		Assert.Equal(RelativeIncludedFiles(cliPlan), RelativeIncludedFiles(tuiState.Plan));
	}

	[Fact]
	public async Task TuiRepositoryRebuild_PreservesIslandIntentAndDetectsSameTimestampTopology()
	{
		using var workspace = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.CreateFile("project/src/App.cs", "public sealed class App {}\n");
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: ["src"],
			SelectedExtensions: [".cs"],
			SelectedIgnoreOptions:
			[
				IgnoreOptionId.UseGitIgnore,
				IgnoreOptionId.SmartIgnore,
				IgnoreOptionId.DotFolders
			],
			RootFolderStates: new Dictionary<string, bool>(PathComparer.Default) { ["src"] = true },
			ExtensionStates: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { [".cs"] = true },
			IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.UseGitIgnore] = true,
				[IgnoreOptionId.SmartIgnore] = true,
				[IgnoreOptionId.DotFolders] = true
			});
		new ProjectProfileStore(() => appData.Path).SaveProfile(project, profile);
		var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TerminalTestHost());
		using var state = await controller.OpenAsync(
			project,
			ProjectProfileReference.Local,
			TestContext.Current.CancellationToken);
		var originalRootTimestamp = Directory.GetLastWriteTimeUtc(project);

		workspace.CreateFile("project/web/package.json", "{}\n");
		workspace.CreateFile("project/web/node_modules/pkg/generated.js", "generated\n");
		workspace.CreateFile("project/.idea/workspace.xml", "<project />\n");
		workspace.CreateFile("project/.git/config", "administrative metadata\n");
		Directory.SetLastWriteTimeUtc(project, originalRootTimestamp);

		await controller.RebuildRepositoryAsync(
			state,
			state.BuildSelection(),
			TestContext.Current.CancellationToken);

		Assert.Equal(new HashSet<string>(["src", "web"], PathComparer.Default), state.Plan.SelectedRoots.ToHashSet(PathComparer.Default));
		Assert.Contains(ProjectExclusion.SmartIgnore, state.Plan.Selection.Exclusions ?? []);
		Assert.Contains(ProjectExclusion.DotFolders, state.Plan.Selection.Exclusions ?? []);
		Assert.Equal(GitFilteringMode.RespectGitIgnore, state.Plan.Selection.GitMode);
		Assert.Equal(
			new HashSet<string>(["src/App.cs", "web/package.json"], StringComparer.Ordinal),
			RelativeIncludedFiles(state.Plan));

		// Changing Extensions must close only that island. Scan roots remain structural,
		// so directories without a currently selected file type stay in the project scope.
		await controller.SetExtensionsAsync(state, [".cs"], TestContext.Current.CancellationToken);
		workspace.CreateFile("project/docs/Guide.cs", "public sealed class Guide {}\n");
		Directory.SetLastWriteTimeUtc(project, originalRootTimestamp);
		await controller.RebuildRepositoryAsync(
			state,
			state.BuildSelection(),
			TestContext.Current.CancellationToken);

		Assert.Equal(
			new HashSet<string>(["docs", "src", "web"], PathComparer.Default),
			state.Plan.SelectedRoots.ToHashSet(PathComparer.Default));
		Assert.Equal([".cs"], state.Plan.SelectedExtensions);
		Assert.Equal(
			new HashSet<string>(["docs/Guide.cs", "src/App.cs"], StringComparer.Ordinal),
			RelativeIncludedFiles(state.Plan));

		await controller.SetExtensionsAsync(
			state,
			[".cs", ".json"],
			TestContext.Current.CancellationToken);
		Assert.Equal(
			new HashSet<string>(["docs", "src", "web"], PathComparer.Default),
			state.Plan.SelectedRoots.ToHashSet(PathComparer.Default));
		Assert.Equal(
			new HashSet<string>(["docs/Guide.cs", "src/App.cs", "web/package.json"], StringComparer.Ordinal),
			RelativeIncludedFiles(state.Plan));
	}

	[Fact]
	public async Task ExplicitInvocationOverrides_RemainExactWithoutMutatingModernLocalState()
	{
		using var workspace = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.CreateFile("project/src/App.cs", "public sealed class App {}\n");
		workspace.CreateFile("project/docs/guide.md", "# Guide\n");
		workspace.CreateFile("project/feature/NewFeature.ts", "export const ready = true;\n");
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: ["src"],
			SelectedExtensions: [".cs"],
			SelectedIgnoreOptions: [IgnoreOptionId.SmartIgnore],
			RootFolderStates: new Dictionary<string, bool>(PathComparer.Default)
			{
				["src"] = true,
				["docs"] = false
			},
			ExtensionStates: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
			{
				[".cs"] = true,
				[".md"] = false
			},
			IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.SmartIgnore] = true
			});
		var store = new ProjectProfileStore(() => appData.Path);
		store.SaveProfile(project, profile);
		var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);

		var resolved = await services.SelectionResolver.ResolveAsync(
			project,
			ProjectProfileReference.Local,
			new ProjectSelectionSpec(
				Roots: ["docs"],
				Extensions: [".md"],
				GitMode: GitFilteringMode.None,
				Exclusions: []),
			TestContext.Current.CancellationToken);
		var plan = await services.ContextFactory.BuildAsync(
			project,
			resolved,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(["docs"], plan.SelectedRoots);
		Assert.Equal([".md"], plan.SelectedExtensions);
		Assert.Equal(GitFilteringMode.None, plan.Selection.GitMode);
		Assert.Empty(plan.Selection.Exclusions ?? []);
		Assert.Equal(
			new[] { "docs/guide.md" },
			plan.IncludedFiles.Select(path => Path.GetRelativePath(project, path).Replace('\\', '/')));
		Assert.True(store.TryLoadProfile(project, out var persisted));
		Assert.Equal(["src"], persisted.SelectedRootFolders);
		Assert.Equal([".cs"], persisted.SelectedExtensions);
		Assert.Contains(IgnoreOptionId.SmartIgnore, persisted.SelectedIgnoreOptions);

		var controller = new TerminalWorkspaceController(services, new TerminalTestHost());
		using var state = await controller.OpenAsync(
			project,
			ProjectProfileReference.Local,
			TestContext.Current.CancellationToken);
		await controller.SetRootsAsync(state, ["docs"], TestContext.Current.CancellationToken);
		await controller.SetExtensionsAsync(state, [".md"], TestContext.Current.CancellationToken);
		await controller.SetGitModeAsync(state, GitFilteringMode.None, TestContext.Current.CancellationToken);
		await controller.SetExclusionsAsync(state, [], TestContext.Current.CancellationToken);
		Assert.Equal(["docs"], state.Plan.SelectedRoots);
		Assert.Equal([".md"], state.Plan.SelectedExtensions);
		Assert.Equal(
			new[] { "docs/guide.md" },
			state.Plan.IncludedFiles.Select(path => Path.GetRelativePath(project, path).Replace('\\', '/')));
	}

	[Fact]
	public async Task NewRootsExtensionsAndIgnoreEvidence_ResolveIdenticallyForDesktopCliAndTui()
	{
		using var workspace = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.CreateFile("project/src/App.cs", "public sealed class App {}\n");
		workspace.CreateFile("project/docs/guide.md", "# Existing unchecked documentation\n");
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: ["src"],
			SelectedExtensions: [".cs"],
			SelectedIgnoreOptions: [IgnoreOptionId.UseGitIgnore],
			RootFolderStates: new Dictionary<string, bool>(PathComparer.Default)
			{
				["src"] = true,
				["docs"] = false
			},
			ExtensionStates: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
			{
				[".cs"] = true,
				[".md"] = false
			},
			IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.UseGitIgnore] = true
			});

		var store = new ProjectProfileStore(() => appData.Path);
		store.SaveProfile(project, profile);

		// These entries did not exist when the modern profile was saved. Complete state maps
		// preserve known choices while unseen rows use the current product defaults.
		workspace.CreateFile("project/feature/package.json", "{}\n");
		workspace.CreateFile("project/feature/NewFeature.ts", "export const ready = true;\n");
		workspace.CreateFile("project/feature/.private.ts", "export const secret = true;\n");
		workspace.CreateFile("project/feature/node_modules/pkg/generated.ts", "generated\n");
		workspace.CreateFile("project/.git/config", "administrative metadata\n");

		var desktopSnapshot = BuildDesktopSnapshot(project, profile);
		var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var resolved = await services.SelectionResolver.ResolveAsync(
			project,
			ProjectProfileReference.Local,
			new ProjectSelectionSpec(),
			TestContext.Current.CancellationToken);
		var cliPlan = await services.ContextFactory.BuildAsync(
			project,
			resolved,
			cancellationToken: TestContext.Current.CancellationToken);
		var tuiState = await new TerminalWorkspaceController(services, new TerminalTestHost()).OpenAsync(
			project,
			ProjectProfileReference.Local,
			TestContext.Current.CancellationToken);

		var expectedRoots = new HashSet<string>(["feature", "src"], PathComparer.Default);
		var expectedExtensions = new HashSet<string>([".cs", ".json", ".ts"], StringComparer.OrdinalIgnoreCase);
		var expectedFiles = new HashSet<string>(StringComparer.Ordinal)
		{
			"feature/NewFeature.ts",
			"feature/package.json",
			"src/App.cs"
		};

		Assert.Equal(expectedRoots, CheckedNames(desktopSnapshot.RootOptions ?? [], PathComparer.Default));
		Assert.Equal(expectedExtensions, CheckedNames(desktopSnapshot.EffectiveExtensionOptions, StringComparer.OrdinalIgnoreCase));
		Assert.Contains(IgnoreOptionId.SmartIgnore, desktopSnapshot.EffectiveIgnoreOptions);
		Assert.Contains(IgnoreOptionId.DotFiles, desktopSnapshot.EffectiveIgnoreOptions);
		Assert.Contains(IgnoreOptionId.UseGitIgnore, desktopSnapshot.EffectiveIgnoreOptions);
		AssertPlan(expectedRoots, expectedExtensions, expectedFiles, cliPlan);
		AssertPlan(expectedRoots, expectedExtensions, expectedFiles, tuiState.Plan);
	}

	private static SelectionRefreshSnapshot BuildDesktopSnapshot(
		string projectPath,
		ProjectSelectionProfile profile)
	{
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		return services.Engine.ComputeFullRefreshSnapshot(
			new SelectionRefreshContext(
				Path: projectPath,
				PreparedSelectionMode: PreparedSelectionMode.Profile,
				AllRootFoldersChecked: false,
				AllExtensionsChecked: false,
				RootSelectionInitialized: true,
				RootSelectionCache: profile.SelectedRootFolders.ToHashSet(PathComparer.Default),
				ExtensionsSelectionInitialized: true,
				ExtensionsSelectionCache: profile.SelectedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase),
				IgnoreSelectionInitialized: true,
				IgnoreSelectionCache: profile.SelectedIgnoreOptions.ToHashSet(),
				IgnoreOptionStateCache: profile.IgnoreOptionStates ?? new Dictionary<IgnoreOptionId, bool>(),
				IgnoreAllPreference: null,
				CurrentSnapshotState: default,
				RootOptionStateCache: profile.RootFolderStates,
				ExtensionOptionStateCache: profile.ExtensionStates,
				IgnoreOptionStateCacheIsComplete: profile.IgnoreOptionStates is not null,
				CaptureTreeInventory: true),
			TestContext.Current.CancellationToken);
	}

	private static HashSet<string> CheckedNames(
		IEnumerable<DevProjex.Application.Models.SelectionOption> options,
		StringComparer comparer) =>
		options
			.Where(static option => option.IsChecked)
			.Select(static option => option.Name)
			.ToHashSet(comparer);

	private static void AssertPlan(
		IReadOnlySet<string> expectedRoots,
		IReadOnlySet<string> expectedExtensions,
		IReadOnlySet<string> expectedFiles,
		ProjectContextPlan plan)
	{
		Assert.Equal(expectedRoots, plan.SelectedRoots.ToHashSet(PathComparer.Default));
		Assert.Equal(expectedExtensions, plan.SelectedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase));
		Assert.Equal(
			expectedFiles,
			plan.IncludedFiles
				.Select(path => Path.GetRelativePath(plan.SourceRoot, path).Replace('\\', '/'))
				.ToHashSet(StringComparer.Ordinal));
		Assert.Contains(ProjectExclusion.SmartIgnore, plan.Selection.Exclusions ?? []);
		Assert.Contains(ProjectExclusion.DotFiles, plan.Selection.Exclusions ?? []);
		Assert.Equal(GitFilteringMode.RespectGitIgnore, plan.Selection.GitMode);
	}

	private static HashSet<string> RelativeIncludedFiles(ProjectContextPlan plan) =>
		plan.IncludedFiles
			.Select(path => Path.GetRelativePath(plan.SourceRoot, path).Replace('\\', '/'))
			.ToHashSet(StringComparer.Ordinal);
}
