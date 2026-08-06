using static DevProjex.Tests.Shared.ProjectLoadWorkflow.ProjectLoadWorkflowRefreshHarness;

namespace DevProjex.Tests.Integration;

public sealed class ProjectLoadWorkflowCrossSectionStateMatrixIntegrationTests
{
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ComputeFullRefreshSnapshot_CrossSectionStateMatrix_ConvergesAndPreservesSelectionContracts(
        string extensionScenarioName,
        string ignoreScenarioName)
    {
        var extensionScenario = Enum.Parse<WorkflowExtensionScenario>(extensionScenarioName);
        var ignoreScenario = Enum.Parse<WorkflowIgnoreScenario>(ignoreScenarioName);

        var rootPath = ProjectLoadWorkflowSharedWorkspace.RootPath;

        var services = CreateServices();
        var baselineSnapshot = services.Engine.ComputeFullRefreshSnapshot(
            CreateDefaultContext(rootPath) with { CaptureTreeInventory = true },
            CancellationToken.None);
        var scenario = CreateScenario(baselineSnapshot, extensionScenario, ignoreScenario);

        var scenarioContext = CreateScenarioContext(rootPath, scenario) with { CaptureTreeInventory = true };
        var firstSnapshot = services.Engine.ComputeFullRefreshSnapshot(
            scenarioContext,
            CancellationToken.None);

        SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
            rootPath,
            services.IgnoreRulesService,
            firstSnapshot);
        AssertVisibleAdvancedIgnoreOptionsCarryPositiveCounts(firstSnapshot);
        AssertScenarioSelectionContract(firstSnapshot, scenario);

        var secondSnapshot = services.Engine.ComputeFullRefreshSnapshot(
            BuildConvergedContext(rootPath, firstSnapshot, scenarioContext) with { CaptureTreeInventory = true },
            CancellationToken.None);

        SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
            rootPath,
            services.IgnoreRulesService,
            secondSnapshot);
        AssertEquivalentSnapshots(firstSnapshot, secondSnapshot);
        AssertVisibleAdvancedIgnoreOptionsCarryPositiveCounts(secondSnapshot);
        AssertScenarioSelectionContract(secondSnapshot, scenario);

        var firstMetrics = await ComputeMetricsFromSnapshotAsync(rootPath, firstSnapshot);
        var secondMetrics = await ComputeMetricsFromSnapshotAsync(rootPath, secondSnapshot);
        Assert.Equal(firstMetrics.TreeMetrics, secondMetrics.TreeMetrics);
        Assert.Equal(firstMetrics.ContentMetrics, secondMetrics.ContentMetrics);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var extensionScenario in Enum.GetValues<WorkflowExtensionScenario>())
        {
            foreach (var ignoreScenario in Enum.GetValues<WorkflowIgnoreScenario>())
                yield return [extensionScenario.ToString(), ignoreScenario.ToString()];
        }
    }
}
