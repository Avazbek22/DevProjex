using DevProjex.Application.Context;
using DevProjex.Terminal.Execution;
using DevProjex.Terminal.Tui;

namespace DevProjex.Tests.Unit;

public sealed class ProjectContextPlannerMarkedSecretsTests
{
    [Fact]
    public async Task LocalProfileMarksSurvivePlanningAndTuiSelectionRebuild()
    {
        using var workspace = new TemporaryDirectory();
        using var appData = new TemporaryDirectory();
        var project = workspace.CreateFolder("project");
        workspace.CreateFile("project/src/App.cs", "public sealed class App {}\n");
        var mark = new MarkedSecretProfileEntry("v2:marked", "secret", 6);
        var profile = new ProjectSelectionProfile(
            SelectedRootFolders: [],
            SelectedExtensions: [".cs"],
            SelectedIgnoreOptions: [IgnoreOptionId.HideSecrets],
            MarkedSecrets: [mark]);
        using var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
        var selection = services.SelectionResolver.ResolveLocalSnapshot(
            profile,
            new ProjectSelectionSpec());

        var plan = await services.ContextPlanner.BuildStructureAsync(
            new ProjectContextRequest(project, selection),
            TestContext.Current.CancellationToken);
        using var state = new TerminalWorkspaceState(plan);

        Assert.Equal(mark, Assert.Single(ProjectSelectionMarkedSecretsResolver.Resolve(selection)));
        Assert.Equal(mark, Assert.Single(ProjectSelectionMarkedSecretsResolver.Resolve(plan.Selection)));
        Assert.Equal(mark, Assert.Single(ProjectSelectionMarkedSecretsResolver.Resolve(state.BuildSelection())));
    }
}
