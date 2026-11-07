namespace DevProjex.Tests.Terminal;

public sealed partial class TerminalSettingsStateContractTests
{
	[Fact]
	public async Task RelationProjectLiftsTheCheckedFocusButKeepsTheWorkspaceFileTypes()
	{
		using var workspace = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		workspace.WriteFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
		workspace.WriteFile("Models/User.cs", "namespace App.Models; public sealed class User { }\n");
		workspace.WriteFile("Services/Store.cs", "using App.Models; namespace App.Services; public sealed class Store { User Value; }\n");
		workspace.WriteFile("Scripts/tool.py", "import os\n");
		var (controller, state) = await OpenAsync(workspace.Path, appData.Path);
		using (state)
		{
			var token = TestContext.Current.CancellationToken;
			await controller.SetExtensionsAsync(state, [".cs"], token);
			var whole = await controller.BuildCurrentPlanAsync(state, token);
			Assert.Null(await controller.BuildRelationProjectPlanAsync(state, whole, GitFilteringMode.RespectGitIgnore, token));

			SelectOnlyTopLevelDirectory(state, "Services");
			var focus = await controller.BuildCurrentPlanAsync(state, token);
			var project = await controller.BuildRelationProjectPlanAsync(state, focus, GitFilteringMode.RespectGitIgnore, token);

			Assert.Equal(["Services/Store.cs"], PlanRelativePaths(focus));
			Assert.NotNull(project);
			Assert.Equal(["Models/User.cs", "Services/Store.cs"], PlanRelativePaths(project));
		}
	}

	[Fact]
	public async Task RelationProjectLiftsAMomentaryGitScopeToThePersistentMode()
	{
		using var repository = new GitModeRepository();
		using var appData = new TemporaryDirectory();
		repository.WriteScopeMatrix();
		var (controller, state) = await OpenAsync(repository.Root, appData.Path);
		using (state)
		{
			var token = TestContext.Current.CancellationToken;
			await controller.SetPathFilteringAsync(state, GitFilteringMode.None, [], token);
			await controller.SetExtensionsAsync(state, [".cs"], token);
			await controller.SetPathFilteringAsync(state, GitFilteringMode.Changes, [], token);
			var changes = await controller.BuildCurrentPlanAsync(state, token);
			var project = await controller.BuildRelationProjectPlanAsync(state, changes, GitFilteringMode.RespectGitIgnore, token);

			Assert.Equal(GitModeRepository.ExpectedScopeFiles("changes"), PlanRelativePaths(changes));
			Assert.NotNull(project);
			Assert.Equal(GitModeRepository.ExpectedScopeFiles("gitignore"), PlanRelativePaths(project));
			Assert.DoesNotContain("Ignored.cs", PlanRelativePaths(project));
			Assert.Throws<ArgumentOutOfRangeException>(() => controller
				.BuildRelationProjectPlanAsync(state, changes, GitFilteringMode.Changes, token)
				.GetAwaiter()
				.GetResult());
		}
	}

	private static IReadOnlyList<string> PlanRelativePaths(ProjectContextPlan plan) =>
		plan.IncludedFiles
			.Select(path => PathUtility.GetPortableRelativePath(plan.SourceRoot, path))
			.Order(StringComparer.Ordinal)
			.ToArray();
}
