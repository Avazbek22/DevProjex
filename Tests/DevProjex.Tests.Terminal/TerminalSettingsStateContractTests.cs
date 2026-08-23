using DevProjex.Application.Presentation;
using DevProjex.Application.Services;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalSettingsStateContractTests
{
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

			var result = await controller.BuildSettingsPlanAsync(
				baseline,
				state.BuildSelection() with { HideSecrets = true },
				knownStates,
				TestContext.Current.CancellationToken);

			Assert.Same(baseline.EffectiveTree, result.Plan.EffectiveTree);
			Assert.True(result.Plan.Selection.HideSecrets);
			Assert.Equal(knownStates.Count, result.ExtensionOptionStates.Count);
			Assert.All(knownStates, pair =>
				Assert.Equal(pair.Value, result.ExtensionOptionStates[pair.Key]));
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
			await controller.SetPathFilteringAsync(
				state,
				GitFilteringMode.RespectGitIgnore,
				state.Plan.Selection.Exclusions ?? [],
				TestContext.Current.CancellationToken);
			Assert.DoesNotContain(".generated", state.Plan.AvailableExtensions);

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
			await controller.SetExclusionsAsync(
				state,
				[.. withoutEmptyFiles, ProjectExclusion.EmptyFiles],
				TestContext.Current.CancellationToken);
			Assert.DoesNotContain(".generated", state.Plan.AvailableExtensions);

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
}
