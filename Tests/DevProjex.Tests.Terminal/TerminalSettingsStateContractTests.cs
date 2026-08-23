using DevProjex.Application.Presentation;

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
