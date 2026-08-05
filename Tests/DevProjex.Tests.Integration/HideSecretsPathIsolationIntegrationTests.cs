using DevProjex.Application.Context;
using DevProjex.Application.Diagnostics;
using DevProjex.Terminal.Tui;

namespace DevProjex.Tests.Integration;

public sealed class HideSecretsPathIsolationIntegrationTests
{
	[Fact]
	public async Task TuiToggle_PreservesSmartIgnoreAndTreeWithoutFilesystemRefresh()
	{
		using var workspace = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.CreateFile("project/pyproject.toml", "[project]\nname = \"isolation\"\n");
		workspace.CreateFile("project/src/app.py", "print('ok')\n");
		workspace.CreateFile("project/src/__pycache__/app.pyc", "binary");
		var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TerminalTestHost());
		using var state = await controller.OpenAsync(
			project,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);
		Assert.Contains(ProjectExclusion.SmartIgnore, state.Plan.Selection.Exclusions ?? []);

		var effectiveTree = state.Plan.EffectiveTree;
		var projectedTree = state.Plan.ProjectedTree;
		var includedFiles = state.Plan.IncludedFiles;
		var includedFolders = state.Plan.IncludedFolders;
		var previewDocument = state.PreviewDocument;
		using var measurement = IgnorePipelineDiagnostics.BeginMeasurement();
		var enabled = (state.Plan.Selection.Exclusions ?? [])
			.Append(ProjectExclusion.HideSecrets)
			.Distinct()
			.ToArray();

		await controller.SetExclusionsAsync(
			state,
			enabled,
			TestContext.Current.CancellationToken);

		Assert.Contains(ProjectExclusion.SmartIgnore, state.Plan.Selection.Exclusions ?? []);
		Assert.Contains(ProjectExclusion.HideSecrets, state.Plan.Selection.Exclusions ?? []);
		Assert.Same(effectiveTree, state.Plan.EffectiveTree);
		Assert.Same(projectedTree, state.Plan.ProjectedTree);
		Assert.Same(includedFiles, state.Plan.IncludedFiles);
		Assert.Same(includedFolders, state.Plan.IncludedFolders);
		Assert.Same(previewDocument, state.PreviewDocument);

		await controller.SetExclusionsAsync(
			state,
			enabled.Where(static exclusion => exclusion != ProjectExclusion.HideSecrets).ToArray(),
			TestContext.Current.CancellationToken);

		Assert.Contains(ProjectExclusion.SmartIgnore, state.Plan.Selection.Exclusions ?? []);
		Assert.DoesNotContain(ProjectExclusion.HideSecrets, state.Plan.Selection.Exclusions ?? []);
		Assert.Same(effectiveTree, state.Plan.EffectiveTree);
		Assert.Same(projectedTree, state.Plan.ProjectedTree);
		Assert.Same(includedFiles, state.Plan.IncludedFiles);
		Assert.Same(includedFolders, state.Plan.IncludedFolders);
		Assert.Same(previewDocument, state.PreviewDocument);

		var diagnostics = measurement.Capture();
		Assert.Equal(0, diagnostics.WorkspaceScans);
		Assert.Equal(0, diagnostics.DirectoryEnumerations);
		Assert.Equal(0, diagnostics.FileEnumerations);
		Assert.Equal(0, diagnostics.IgnoreRulesBuilds);
		Assert.Equal(0, diagnostics.FullSelectionRefreshes);
		Assert.Equal(0, diagnostics.LiveSelectionRefreshes);
	}
}
