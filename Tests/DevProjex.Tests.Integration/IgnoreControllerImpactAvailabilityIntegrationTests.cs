using static DevProjex.Tests.Shared.ProjectLoadWorkflow.ProjectLoadWorkflowRefreshHarness;

namespace DevProjex.Tests.Integration;

public sealed class IgnoreControllerImpactAvailabilityIntegrationTests
{
	[Fact]
	public void CleanPolyglotWorkspace_DoesNotShowNoOpGitOrSmartControllers()
	{
		using var project = new TemporaryDirectory();
		CreateSunnyEastLikeWorkspace(project);

		var snapshot = ComputeDefaultSnapshot(project.Path);

		AssertIgnoreOption(snapshot, IgnoreOptionId.UseGitIgnore, expectedVisible: false, expectedChecked: null);
		AssertIgnoreOption(snapshot, IgnoreOptionId.SmartIgnore, expectedVisible: false, expectedChecked: null);
		AssertIgnoreOption(snapshot, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: true);
		AssertIgnoreOption(snapshot, IgnoreOptionId.DotFiles, expectedVisible: true, expectedChecked: true);
		AssertIgnoreOption(snapshot, IgnoreOptionId.ExtensionlessFiles, expectedVisible: true, expectedChecked: true);
	}

	[Fact]
	public void PolyglotWorkspace_WhenSmartAndGitArtifactsAppearAfterRefresh_ShowsControllersCheckedByDefault()
	{
		using var project = new TemporaryDirectory();
		CreateSunnyEastLikeWorkspace(project);
		var services = CreateServices();
		var cleanSnapshot = services.Engine.ComputeFullRefreshSnapshot(
			CreateDefaultContext(project.Path),
			CancellationToken.None);

		project.CreateFile("src/WebApi/bin/Debug/net10.0/WebApi.dll", "binary");

		var refreshed = services.Engine.ComputeFullRefreshSnapshot(
			CreateContextFromSnapshot(project.Path, cleanSnapshot),
			CancellationToken.None);

		AssertIgnoreOption(refreshed, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: true);
		AssertIgnoreOption(refreshed, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: true);
		Assert.DoesNotContain(refreshed.ExtensionOptions, option => option.Name == ".dll");
	}

	[Fact]
	public void GitIgnoreController_IsHiddenWhenOnlyMatchedFileIsAlreadyMaskedByDotFiles()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile(".gitignore", ".env\n");
		project.CreateFile("App.csproj", "<Project />\n");
		project.CreateFile("Program.cs", "Console.WriteLine(\"ok\");\n");
		project.CreateFile(".env", "SECRET=1\n");

		var snapshot = ComputeDefaultSnapshot(project.Path);

		AssertIgnoreOption(snapshot, IgnoreOptionId.UseGitIgnore, expectedVisible: false, expectedChecked: null);
		AssertIgnoreOption(snapshot, IgnoreOptionId.DotFiles, expectedVisible: true, expectedChecked: true);
	}

	[Fact]
	public void GitIgnoreController_IsVisibleWhenFilePatternChangesVisibleContentThroughEmptyFolderPruning()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile(".gitignore", "*.log\n");
		project.CreateFile("App.csproj", "<Project />\n");
		project.CreateFile("Program.cs", "Console.WriteLine(\"ok\");\n");
		project.CreateFile("logs/runtime.log", "git ignored\n");

		var snapshot = ComputeDefaultSnapshot(project.Path);

		AssertIgnoreOption(snapshot, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: true);
		Assert.DoesNotContain(snapshot.ExtensionOptions, option => option.Name == ".log");
	}

	[Fact]
	public void SmartController_IsHiddenWhenKnownStackHasNoExistingSmartArtifact()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile("pyproject.toml", "[project]\nname = \"clean-python\"\n");
		project.CreateFile("src/app.py", "print('ok')\n");

		var snapshot = ComputeDefaultSnapshot(project.Path);

		AssertIgnoreOption(snapshot, IgnoreOptionId.SmartIgnore, expectedVisible: false, expectedChecked: null);
	}

	[Fact]
	public void SmartController_IsVisibleWhenKnownStackHasExistingSmartArtifact()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile("pyproject.toml", "[project]\nname = \"dirty-python\"\n");
		project.CreateFile("src/app.py", "print('ok')\n");
		project.CreateFile("src/__pycache__/app.cpython-310.pyc", "binary");

		var snapshot = ComputeDefaultSnapshot(project.Path);

		AssertIgnoreOption(snapshot, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: true);
		Assert.DoesNotContain(snapshot.ExtensionOptions, option => option.Name == ".pyc");
	}

	private static SelectionRefreshSnapshot ComputeDefaultSnapshot(string projectPath)
	{
		var services = CreateServices();
		return services.Engine.ComputeFullRefreshSnapshot(
			CreateDefaultContext(projectPath),
			CancellationToken.None);
	}

	private static void CreateSunnyEastLikeWorkspace(TemporaryDirectory project)
	{
		project.CreateFile(".gitignore", "bin/\nobj/\nlogs/\n*.user\n");
		project.CreateFile(".dockerignore", "bin\nobj\n");
		project.CreateFile("Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:10.0\n");
		project.CreateFile("FlexErp.sln", "Microsoft Visual Studio Solution File\n");
		project.CreateFile("README.md", "# SunnyEast\n");
		project.CreateFile(".github/workflows/production.yml", "name: production\n");
		project.CreateFile(".run/Client-Server.run.xml", "<component />\n");
		project.CreateFile("src/Application/Application.csproj", "<Project />\n");
		project.CreateFile("src/Application/Features/Orders/CreateOrderCommandHandler.cs", "public sealed class CreateOrderCommandHandler {}\n");
		project.CreateFile("src/WebApi/WebApi.csproj", "<Project />\n");
		project.CreateFile("src/WebApi/Program.cs", "Console.WriteLine(\"ok\");\n");
		project.CreateFile("src/Client/Client/Client.csproj", "<Project />\n");
		project.CreateFile("src/Client/Client/Program.cs", "Console.WriteLine(\"ok\");\n");
	}

	private static void AssertIgnoreOption(
		SelectionRefreshSnapshot snapshot,
		IgnoreOptionId optionId,
		bool expectedVisible,
		bool? expectedChecked)
	{
		var options = snapshot.IgnoreOptions
			.Where(option => option.Id == optionId)
			.ToArray();
		if (!expectedVisible)
		{
			Assert.Empty(options);
			return;
		}

		Assert.Single(options);
		if (expectedChecked.HasValue)
			Assert.Equal(expectedChecked.Value, options[0].IsChecked);
	}
}
