using System.Diagnostics;
using DevProjex.Application.Presentation;
using DevProjex.Application.Services;
using DevProjex.Infrastructure.ProjectProfiles;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalSettingsStateContractTests
{
	[Fact]
	public void ChangingOnlyTheDiffRangeRequiresAFullStructuralRefresh()
	{
		var baseline = ProjectSelectionSpec.Standard with
		{
			GitMode = GitFilteringMode.Diff,
			GitDiffRange = "main..feature-a"
		};

		Assert.True(TerminalWorkspaceController.RequiresStructuralRefresh(
			baseline,
			baseline with { GitDiffRange = "main..feature-b" }));
		Assert.False(TerminalWorkspaceController.RequiresStructuralRefresh(baseline, baseline));
	}

	[Theory]
	[InlineData(GitFilteringMode.None)]
	[InlineData(GitFilteringMode.Staged)]
	[InlineData(GitFilteringMode.Changes)]
	public void SwitchingAwayFromDiffClearsItsRange(GitFilteringMode mode)
	{
		var selection = TerminalWorkspaceController.BuildPathFilteringSelection(
			ProjectSelectionSpec.Standard with
			{
				GitMode = GitFilteringMode.Diff,
				GitDiffRange = "main..feature"
			},
			mode,
			ProjectSelectionSpec.StandardExclusions);

		Assert.Equal(mode, selection.GitMode);
		Assert.Null(selection.GitDiffRange);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("main")]
	public void DiffModeRequiresAValidRange(string? range)
	{
		var exception = Assert.Throws<ArgumentException>(() =>
			TerminalWorkspaceController.BuildPathFilteringSelection(
				ProjectSelectionSpec.Standard with { GitDiffRange = range },
				GitFilteringMode.Diff,
				ProjectSelectionSpec.StandardExclusions));

		Assert.Equal("diffRange", exception.ParamName);
	}

	[Theory]
	[InlineData(IgnoreOptionId.HideSecrets)]
	[InlineData(IgnoreOptionId.HidePrivateData)]
	[InlineData(IgnoreOptionId.CompressCode)]
	[InlineData(IgnoreOptionId.StripComments)]
	[InlineData(IgnoreOptionId.StripBlankLines)]
	public async Task EveryContentTransformationChangesSelectionWithoutReplacingTheTree(
		IgnoreOptionId optionId)
	{
		using var workspace = CreateWorkspace();
		var (controller, state) = await OpenAsync(workspace);
		using (state)
		{
			var tree = state.Plan.EffectiveTree;
			var revision = state.Revision;

			controller.SetContentTransformation(
				state,
				optionId,
				enabled: true,
				TestContext.Current.CancellationToken);

			Assert.True(TerminalParameterRowsBuilder.IsContentTransformationEnabled(
				state.Plan.Selection,
				optionId));
			Assert.Same(tree, state.Plan.EffectiveTree);
			Assert.Equal(revision + 1, state.Revision);

			controller.SetContentTransformation(
				state,
				optionId,
				enabled: false,
				TestContext.Current.CancellationToken);

			Assert.False(TerminalParameterRowsBuilder.IsContentTransformationEnabled(
				state.Plan.Selection,
				optionId));
			Assert.Same(tree, state.Plan.EffectiveTree);
			Assert.Equal(revision + 2, state.Revision);
		}
	}

	[Fact]
	public async Task SettingsPlanContentRefreshPreservesTheTreeAndKnownExtensionStates()
	{
		using var workspace = CreateWorkspace();
		var (controller, state) = await OpenAsync(workspace);
		using (state)
		{
			var baseline = state.Plan;
			var knownStates = state.ExtensionOptionStates.ToDictionary(
				static pair => pair.Key,
				static pair => pair.Value,
				StringComparer.OrdinalIgnoreCase);
			var knownPathStates = state.PathOptionStates.ToDictionary(
				static pair => pair.Key,
				static pair => pair.Value,
				PathComparer.Default);

			var result = await controller.BuildSettingsPlanAsync(
				baseline,
				state.BuildSelection() with { HideSecrets = true },
				knownStates,
				state.BuildSelectedItemRelativePaths(),
				state.PathOptionStates,
				TestContext.Current.CancellationToken);

			Assert.Same(baseline.EffectiveTree, result.Plan.EffectiveTree);
			Assert.True(result.Plan.Selection.HideSecrets);
			Assert.Equal(knownStates.Count, result.ExtensionOptionStates.Count);
			Assert.All(knownStates, pair =>
				Assert.Equal(pair.Value, result.ExtensionOptionStates[pair.Key]));
			Assert.All(knownPathStates, pair =>
				Assert.Equal(pair.Value, result.PathOptionStates[pair.Key]));

			controller.ApplySettingsPlan(state, result);
			Assert.All(knownStates, pair =>
				Assert.Equal(pair.Value, state.ExtensionOptionStates[pair.Key]));
			Assert.All(knownPathStates, pair =>
				Assert.Equal(pair.Value, state.PathOptionStates[pair.Key]));
		}
	}

	[Fact]
	public async Task ApplyingLatestSettingsPlanDisablesUnusedRedactionSession()
	{
		using var workspace = CreateWorkspace();
		workspace.WriteFile(
			"src/Secrets.cs",
			"internal static class Secrets { private const string Token = \"ghp_1234567890abcdefghijklmnopqrstuvwxyz\"; }");
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(
			services,
			new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);
		controller.SetContentTransformation(
			state,
			IgnoreOptionId.HideSecrets,
			enabled: true,
			TestContext.Current.CancellationToken);
		using var preview = await controller.BuildPreviewDocumentAsync(
			state,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Markdown,
			TestContext.Current.CancellationToken);
		Assert.NotNull(services.SecretRedactionSession.GetSnapshot(
			state.Plan.SourceRoot,
			state.Plan.IncludedFiles));

		var result = await controller.BuildSettingsPlanAsync(
			state.Plan,
			state.BuildSelection() with { HideSecrets = false },
			state.ExtensionOptionStates,
			state.BuildSelectedItemRelativePaths(),
			state.PathOptionStates,
			TestContext.Current.CancellationToken);
		controller.ApplySettingsPlan(state, result);

		Assert.Null(services.SecretRedactionSession.GetSnapshot(
			state.Plan.SourceRoot,
			state.Plan.IncludedFiles));
	}

	[Fact]
	public async Task PathFilteringReplacesTheModeAndRuleSetExactly()
	{
		using var workspace = CreateWorkspace();
		var (controller, state) = await OpenAsync(workspace);
		using (state)
		{
			await controller.SetPathFilteringAsync(
				state,
				GitFilteringMode.None,
				[],
				TestContext.Current.CancellationToken);
			Assert.Equal(GitFilteringMode.None, state.Plan.GitReadiness.Mode);
			Assert.Empty(state.Plan.Selection.Exclusions ?? []);

			var expected = ProjectPresentationCatalog.Exclusions
				.Select(static descriptor => descriptor.RequireId())
				.ToArray();
			await controller.SetPathFilteringAsync(
				state,
				GitFilteringMode.RespectGitIgnore,
				expected,
				TestContext.Current.CancellationToken);

			Assert.Equal(GitFilteringMode.RespectGitIgnore, state.Plan.GitReadiness.Mode);
			Assert.Equal(expected.Order(), (state.Plan.Selection.Exclusions ?? []).Order());
		}
	}

	[Fact]
	public async Task ExtensionSelectionUsesTheExactRequestedSetInBothDirections()
	{
		using var workspace = CreateWorkspace();
		var (controller, state) = await OpenAsync(workspace);
		using (state)
		{
			await controller.SetExtensionsAsync(
				state,
				[],
				TestContext.Current.CancellationToken);
			Assert.Empty(state.Plan.SelectedExtensions);

			var available = state.Plan.AvailableExtensions.ToArray();
			await controller.SetExtensionsAsync(
				state,
				available,
				TestContext.Current.CancellationToken);

			Assert.Equal(
				available.Order(StringComparer.OrdinalIgnoreCase),
				state.Plan.SelectedExtensions.Order(StringComparer.OrdinalIgnoreCase));
		}
	}

	[Fact]
	public async Task GitIgnoreAvailabilityEvolutionMatchesDesktopSelectionPolicy()
	{
		using var workspace = CreateWorkspace();
		workspace.WriteFile(".gitignore", "*.generated\n");
		workspace.WriteFile("src/hidden.generated", "generated");
		var (controller, state) = await OpenAsync(workspace);
		using (state)
		{
			Assert.DoesNotContain(".generated", state.Plan.AvailableExtensions);
			await controller.SetExtensionsAsync(
				state,
				[".json"],
				TestContext.Current.CancellationToken);
			var knownBefore = state.ExtensionOptionStates.ToDictionary(
				static pair => pair.Key,
				static pair => pair.Value,
				StringComparer.OrdinalIgnoreCase);

			await controller.SetPathFilteringAsync(
				state,
				GitFilteringMode.None,
				state.Plan.Selection.Exclusions ?? [],
				TestContext.Current.CancellationToken);

			var desktop = new FilterOptionSelectionService().BuildExtensionOptions(
				state.Plan.AvailableExtensions,
				new HashSet<string>([".json"], StringComparer.OrdinalIgnoreCase),
				knownBefore);
			var desktopSelected = desktop
				.Where(static option => option.IsChecked)
				.Select(static option => option.Name)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			Assert.Contains(".generated", state.Plan.AvailableExtensions);
			Assert.Contains(".generated", state.Plan.SelectedExtensions);
			Assert.DoesNotContain(".cs", state.Plan.SelectedExtensions);
			Assert.True(desktopSelected.SetEquals(state.Plan.SelectedExtensions));

			await controller.SetExtensionsAsync(
				state,
				state.Plan.SelectedExtensions
					.Where(static extension => extension != ".generated")
					.ToArray(),
				TestContext.Current.CancellationToken);
			Assert.DoesNotContain(".generated", state.Plan.SelectedExtensions);
			Assert.False(state.ExtensionOptionStates[".generated"]);
			await controller.SetPathFilteringAsync(
				state,
				GitFilteringMode.RespectGitIgnore,
				state.Plan.Selection.Exclusions ?? [],
				TestContext.Current.CancellationToken);
			Assert.DoesNotContain(".generated", state.Plan.AvailableExtensions);
			Assert.False(state.ExtensionOptionStates[".generated"]);

			await controller.SetPathFilteringAsync(
				state,
				GitFilteringMode.None,
				state.Plan.Selection.Exclusions ?? [],
				TestContext.Current.CancellationToken);
			Assert.Contains(".generated", state.Plan.AvailableExtensions);
			Assert.DoesNotContain(".generated", state.Plan.SelectedExtensions);
		}
	}

	[Fact]
	public async Task StagedModeRestrictsTheEffectiveTreeToTheCurrentGitScope()
	{
		using var workspace = CreateWorkspace();
		RunGit(workspace.Path, "init", "--quiet");
		RunGit(workspace.Path, "config", "user.email", "terminal-tests@devprojex.local");
		RunGit(workspace.Path, "config", "user.name", "DevProjex Terminal Tests");
		RunGit(workspace.Path, "add", "--all");
		RunGit(workspace.Path, "commit", "--quiet", "-m", "Initial project");
		var (controller, state) = await OpenAsync(workspace);
		using (state)
		{
			await controller.SetGitModeAsync(
				state,
				GitFilteringMode.Staged,
				TestContext.Current.CancellationToken);

			Assert.Empty(state.Plan.EffectiveTree.Children);

			workspace.WriteFile(".scope/Staged.cs", "internal sealed class Staged { }");
			RunGit(workspace.Path, "add", "--", ".scope/Staged.cs");
			await controller.RefreshProjectAsync(state, TestContext.Current.CancellationToken);
			Assert.Empty(state.Plan.EffectiveTree.Children);
			Assert.Equal(1, state.Plan.IgnoreOptionCounts.DotFolders);

			await controller.SetExclusionsAsync(
				state,
				[],
				TestContext.Current.CancellationToken);
			var paths = TerminalWorkspaceState.BuildSelectableRelativePaths(
				state.Plan.EffectiveTree,
				state.Plan.SourceRoot);
			Assert.Equal([".scope/Staged.cs"], paths);
			Assert.DoesNotContain("global.json", paths);
			Assert.DoesNotContain("src/App.cs", paths);
			Assert.DoesNotContain("src/settings.json", paths);

			await controller.SetExclusionsAsync(
				state,
				ProjectSelectionSpec.StandardExclusions,
				TestContext.Current.CancellationToken);
			Assert.Empty(state.Plan.IncludedFiles);
			Assert.True(
				state.Plan.IgnoreOptionCounts.DotFolders == 1,
				$"Expected one scoped dot-folder blocker; roots=" +
				$"[{string.Join(',', state.Plan.SelectedRoots)}], available=" +
				$"[{string.Join(',', state.Plan.AvailableRoots)}], extensions=" +
				$"[{string.Join(',', state.Plan.AvailableExtensions)}].");
		}
	}

	[Fact]
	public async Task SelectionProjectionPreservesAnExplicitlyEmptyTree()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("only.cs", "internal sealed class Only { }");
		using var appData = new TemporaryDirectory();
		var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(
			services,
			new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);

		Assert.Single(state.Plan.IncludedFiles);
		state.SelectNone();
		await controller.ReprojectSelectionAsync(
			state,
			TestContext.Current.CancellationToken);

		Assert.Empty(state.Plan.IncludedFiles);
		Assert.Empty(state.Plan.IncludedFolders);
		Assert.True(state.IsEffectiveRootUnchecked);

		state.SelectAll();
		await controller.ReprojectSelectionAsync(
			state,
			TestContext.Current.CancellationToken);
		Assert.Single(state.Plan.IncludedFiles);

		var onlyFileIndex = state.VisibleRows
			.Select((row, index) => (row, index))
			.Single(item => item.row.Node.DisplayName == "only.cs")
			.index;
		state.ToggleSelection(onlyFileIndex);
		await controller.ReprojectSelectionAsync(
			state,
			TestContext.Current.CancellationToken);
		Assert.Empty(state.Plan.IncludedFiles);
		Assert.True(state.IsEffectiveRootUnchecked);
	}

	[Fact]
	public async Task GitScopeEvolutionSelectsNewPathsAndPreservesKnownUncheckedPaths()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("kept.cs", "internal sealed class Kept { }");
		workspace.WriteFile("hidden.cs", "internal sealed class Hidden { }");
		RunGit(workspace.Path, "init", "--quiet");
		RunGit(workspace.Path, "config", "user.email", "terminal-tests@devprojex.local");
		RunGit(workspace.Path, "config", "user.name", "DevProjex Terminal Tests");
		RunGit(workspace.Path, "add", "--all");
		RunGit(workspace.Path, "commit", "--quiet", "-m", "Initial project");
		using var appData = new TemporaryDirectory();
		var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(
			services,
			new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);

		var hiddenIndex = state.VisibleRows
			.Select((row, index) => (row, index))
			.Single(item => item.row.Node.DisplayName == "hidden.cs")
			.index;
		state.ToggleSelection(hiddenIndex);
		await controller.ReprojectSelectionAsync(
			state,
			TestContext.Current.CancellationToken);
		Assert.False(state.PathOptionStates["hidden.cs"]);
		await controller.SetGitModeAsync(
			state,
			GitFilteringMode.Staged,
			TestContext.Current.CancellationToken);
		Assert.Empty(state.Plan.IncludedFiles);
		Assert.False(state.PathOptionStates["hidden.cs"]);
		Assert.True(state.PathOptionStates["kept.cs"]);

		workspace.WriteFile("hidden.cs", "internal sealed class Hidden { private int Changed; }");
		workspace.WriteFile("new.cs", "internal sealed class New { }");
		RunGit(workspace.Path, "add", "--", "hidden.cs", "new.cs");
		await controller.RefreshProjectAsync(
			state,
			TestContext.Current.CancellationToken);

		Assert.DoesNotContain(
			state.Plan.IncludedFiles,
			path => Path.GetFileName(path) == "hidden.cs");
		Assert.Contains(
			state.Plan.IncludedFiles,
			path => Path.GetFileName(path) == "new.cs");
		Assert.False(state.PathOptionStates["hidden.cs"]);
		Assert.True(state.PathOptionStates["new.cs"]);
	}

	[Fact]
	public async Task LocalProfileDisabledRootRemainsDisabledAcrossStructuralSettingsRefresh()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/App.cs", "internal sealed class App { }");
		workspace.WriteFile("docs/readme.md", "# Documentation");
		using var appData = new TemporaryDirectory();
		new ProjectProfileStore(() => appData.Path).SaveProfile(
			workspace.Path,
			new ProjectSelectionProfile(
				SelectedRootFolders: ["src"],
				SelectedExtensions: [".cs", ".md"],
				SelectedIgnoreOptions: ProjectSelectionAdapter.ToIgnoreOptions(
					ProjectSelectionSpec.Standard),
				RootFolderStates: new Dictionary<string, bool>(PathComparer.Default)
				{
					["src"] = true,
					["docs"] = false
				},
				ExtensionStates: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
				{
					[".cs"] = true,
					[".md"] = true
				}));
		var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(
			services,
			new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Local,
			TestContext.Current.CancellationToken);

		Assert.Equal(
			ProjectSelectionApplicationMode.Preserve,
			state.Plan.Selection.ApplicationIntent?.Roots);
		Assert.Equal(["src"], state.Plan.SelectedRoots);
		Assert.DoesNotContain(
			state.Plan.IncludedFiles,
			path => Path.GetFileName(path) == "readme.md");

		await controller.SetExclusionsAsync(
			state,
			(state.Plan.Selection.Exclusions ?? [])
				.Where(static exclusion => exclusion != ProjectExclusion.EmptyFiles)
				.ToArray(),
			TestContext.Current.CancellationToken);

		Assert.Equal(["src"], state.Plan.SelectedRoots);
		Assert.DoesNotContain(
			state.Plan.IncludedFiles,
			path => Path.GetFileName(path) == "readme.md");
	}

	[Fact]
	public async Task ExclusionAvailabilityEvolutionChecksNewExtensionsAndRemembersExplicitState()
	{
		using var workspace = CreateWorkspace();
		workspace.WriteFile("src/hidden.generated", string.Empty);
		var (controller, state) = await OpenAsync(workspace);
		using (state)
		{
			Assert.DoesNotContain(".generated", state.Plan.AvailableExtensions);
			await controller.SetExtensionsAsync(
				state,
				[".json"],
				TestContext.Current.CancellationToken);
			var withoutEmptyFiles = (state.Plan.Selection.Exclusions ?? [])
				.Where(static exclusion => exclusion != ProjectExclusion.EmptyFiles)
				.ToArray();

			await controller.SetExclusionsAsync(
				state,
				withoutEmptyFiles,
				TestContext.Current.CancellationToken);

			Assert.Contains(".generated", state.Plan.AvailableExtensions);
			Assert.Contains(".generated", state.Plan.SelectedExtensions);
			Assert.DoesNotContain(".cs", state.Plan.SelectedExtensions);

			await controller.SetExtensionsAsync(
				state,
				state.Plan.SelectedExtensions
					.Where(static extension => extension != ".generated")
					.ToArray(),
				TestContext.Current.CancellationToken);
			Assert.DoesNotContain(".generated", state.Plan.SelectedExtensions);
			Assert.False(state.ExtensionOptionStates[".generated"]);
			await controller.SetExclusionsAsync(
				state,
				[.. withoutEmptyFiles, ProjectExclusion.EmptyFiles],
				TestContext.Current.CancellationToken);
			Assert.DoesNotContain(".generated", state.Plan.AvailableExtensions);
			Assert.False(state.ExtensionOptionStates[".generated"]);

			await controller.SetExclusionsAsync(
				state,
				withoutEmptyFiles,
				TestContext.Current.CancellationToken);
			Assert.Contains(".generated", state.Plan.AvailableExtensions);
			Assert.DoesNotContain(".generated", state.Plan.SelectedExtensions);
		}
	}

	[Fact]
	public void WorkspaceActionModelHasNoLegacyRootSelectionRoute()
	{
		Assert.DoesNotContain(
			Enum.GetNames<TerminalWorkspaceActionKind>(),
			static name => name.Contains("Root", StringComparison.OrdinalIgnoreCase));
	}

	private static TemporaryDirectory CreateWorkspace()
	{
		var workspace = new TemporaryDirectory();
		workspace.WriteFile("global.json", "{}");
		workspace.WriteFile("src/App.cs", "internal sealed class App { }");
		workspace.WriteFile("src/settings.json", "{}");
		return workspace;
	}

	private static async Task<(TerminalWorkspaceController Controller, TerminalWorkspaceState State)>
		OpenAsync(TemporaryDirectory workspace)
	{
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(
			services,
			new TestTerminalEnvironment());
		var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);
		return (controller, state);
	}

	private static void RunGit(string workingDirectory, params string[] arguments)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = OperatingSystem.IsWindows() ? "git.exe" : "git",
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);

		var result = TerminalTestProcess.Run(startInfo);
		Assert.Equal(0, result.ExitCode);
	}
}
