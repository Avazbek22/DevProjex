using DevProjex.Application.Dependencies;
using DevProjex.Terminal.Rendering;

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

	[Fact]
	public async Task RelatedInTheCheckedFocusAnswersFromTheWorkspaceProject()
	{
		using var workspace = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		NarrowedRelationFixtures.WriteGlobalUsingLibrary(workspace, ".");
		var (controller, state) = await OpenAsync(workspace.Path, appData.Path);
		using (state)
		{
			var token = TestContext.Current.CancellationToken;
			var whole = await controller.BuildCurrentPlanAsync(state, token);
			var (wholeRelated, wholeOutside) = await controller.FindRelatedAsync(
				state, whole, NarrowedRelationFixtures.LibrarySeed, DependencyDirection.Both, 1,
				GitFilteringMode.RespectGitIgnore, token);
			var wholeText = await RenderRelatedAsync(appData.Path, wholeRelated, wholeOutside, token);

			SelectOnlyTopLevelDirectory(state, NarrowedRelationFixtures.LibraryFolder);
			var focus = await controller.BuildCurrentPlanAsync(state, token);
			var (related, outside) = await controller.FindRelatedAsync(
				state, focus, NarrowedRelationFixtures.LibrarySeed, DependencyDirection.Both, 1,
				GitFilteringMode.RespectGitIgnore, token);
			var text = await RenderRelatedAsync(appData.Path, related, outside, token);

			Assert.Null(wholeOutside);
			Assert.NotNull(outside);
			Assert.Equal(
				NarrowedRelationFixtures.LibraryDependenciesInside,
				NarrowedRelationFixtures.SectionPaths(text, "Dependencies"));
			Assert.Equal(["Events/LogEvent.cs"],
				NarrowedRelationFixtures.SectionPaths(text, "Dependencies outside the selection"));
			Assert.Equal(["Configuration.cs"],
				NarrowedRelationFixtures.SectionPaths(text, "Dependents outside the selection"));
			Assert.Contains("[Resolution] resolved=3 · ambiguous=0 · unresolved=0 · external=0 · outside-selection=2",
				text, StringComparison.Ordinal);
			Assert.Contains("[Resolution] resolved=5 · ambiguous=0 · unresolved=0 · external=0\n",
				wholeText.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);

			// A deeper walk visits the selected files the project relates, never the outside ones.
			var (_, deeper) = await controller.FindRelatedAsync(
				state, focus, NarrowedRelationFixtures.LibrarySeed, DependencyDirection.Dependencies, 2,
				GitFilteringMode.RespectGitIgnore, token);
			Assert.NotNull(deeper);
			Assert.Equal(
				[NarrowedRelationFixtures.LibrarySeed, .. NarrowedRelationFixtures.LibraryDependenciesInside],
				deeper.Selection.Seeds.Select(static seed => seed.Seed));
		}
	}

	private static async Task<string> RenderRelatedAsync(
		string appDataPath,
		DependencyRelatedResult related,
		DependencyOutsideSelectionResult? outside,
		CancellationToken cancellationToken)
	{
		using var writer = new StringWriter();
		await RelatedOutputRenderer.WriteAsync(
			writer,
			related,
			outside,
			DependencyDirection.Both,
			AnalysisOutputFormat.Text,
			new TerminalServiceFactory(() => appDataPath).Create(AppLanguage.En).Localization,
			cancellationToken);
		return writer.ToString();
	}

	private static IReadOnlyList<string> PlanRelativePaths(ProjectContextPlan plan) =>
		plan.IncludedFiles
			.Select(path => PathUtility.GetPortableRelativePath(plan.SourceRoot, path))
			.Order(StringComparer.Ordinal)
			.ToArray();
}
