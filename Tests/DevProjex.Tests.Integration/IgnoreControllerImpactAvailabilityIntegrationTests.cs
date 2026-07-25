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
	public void GitIgnoreController_OwnsMatchedDotFileBeforeDotFilesRule()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile(".gitignore", ".env\n");
		project.CreateFile("App.csproj", "<Project />\n");
		project.CreateFile("Program.cs", "Console.WriteLine(\"ok\");\n");
		project.CreateFile(".env", "SECRET=1\n");

		var snapshot = ComputeDefaultSnapshot(project.Path);

		AssertIgnoreOption(snapshot, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: true);
		AssertIgnoreOption(snapshot, IgnoreOptionId.DotFiles, expectedVisible: true, expectedChecked: true);
		Assert.Equal(1, snapshot.IgnoreOptionCounts.DotFiles);
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

	[Fact]
	public void SmartController_IsVisibleForTopLevelSignatureArtifactWithoutKnownStackMarker()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile("src/App.cs", "class App {}\n");
		project.CreateFile("obj/project.assets.json", "{}\n");

		var snapshot = ComputeDefaultSnapshot(project.Path);

		AssertIgnoreOption(snapshot, IgnoreOptionId.UseGitIgnore, expectedVisible: false, expectedChecked: null);
		AssertIgnoreOption(snapshot, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: true);
		Assert.True(snapshot.ControllerImpactCounts.SmartIgnore > 0);
		Assert.DoesNotContain(snapshot.ExtensionOptions, option => string.Equals(option.Name, ".json", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void SmartController_ExplicitUncheckedStateStaysVisibleForSignatureArtifact()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile("src/App.cs", "class App {}\n");
		project.CreateFile("obj/project.assets.json", "{}\n");
		var services = CreateServices();
		var baseline = services.Engine.ComputeFullRefreshSnapshot(
			CreateDefaultContext(project.Path),
			TestContext.Current.CancellationToken);

		var snapshot = services.Engine.ComputeFullRefreshSnapshot(
			CreateContextWithForcedIgnoreOptions(
				project.Path,
				baseline,
				new Dictionary<IgnoreOptionId, bool>
				{
					[IgnoreOptionId.SmartIgnore] = false
				}),
			TestContext.Current.CancellationToken);

		AssertIgnoreOption(snapshot, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: false);
		Assert.True(snapshot.ControllerImpactCounts.SmartIgnore > 0);
		Assert.Contains(snapshot.ExtensionOptions, option => string.Equals(option.Name, ".json", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void SmartController_AllIgnoreOptionsOff_StaysVisibleUncheckedForSignatureArtifact()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile("src/App.cs", "class App {}\n");
		project.CreateFile("obj/project.assets.json", "{}\n");
		var services = CreateServices();

		var snapshot = services.Engine.ComputeFullRefreshSnapshot(
			CreateAllIgnoreOptionsOffContext(project.Path),
			TestContext.Current.CancellationToken);

		AssertIgnoreOption(snapshot, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: false);
		Assert.True(snapshot.ControllerImpactCounts.SmartIgnore > 0);
		Assert.Contains(snapshot.ExtensionOptions, option => string.Equals(option.Name, ".json", StringComparison.OrdinalIgnoreCase));
		Assert.Contains(snapshot.RootOptions!, option => option.Name == "obj" && option.IsChecked);
	}

	[Fact]
	public void SmartController_LegacyDependencyStore_IsVisibleAndRemovesOnlyArtifactExtensions()
	{
		using var project = new TemporaryDirectory();
		CreateLegacyPackageStoreWorkspace(project);
		var services = CreateServices();
		var rules = services.IgnoreRulesService.Build(
			project.Path,
			[IgnoreOptionId.SmartIgnore],
			["src"]);
		var localStatePath = Path.Combine(project.Path, "App.sln.DotSettings.user");

		Assert.True(rules.UseSmartIgnore);
		Assert.True(rules.IsSmartIgnoredFile(
			localStatePath,
			Path.GetFileName(localStatePath),
			shouldApplySmartIgnore: true));
		var rootFileScan = new FileSystemScanner().GetRootFileIgnoreSectionSnapshot(
			project.Path,
			rules,
			rules,
			effectiveAllowedExtensions: null,
			TestContext.Current.CancellationToken);
		Assert.DoesNotContain(".user", rootFileScan.Value.VisibleExtensions);

		var snapshot = services.Engine.ComputeFullRefreshSnapshot(
			CreateDefaultContext(project.Path),
			TestContext.Current.CancellationToken);

		AssertIgnoreOption(snapshot, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: true);
		Assert.True(snapshot.ControllerImpactCounts.SmartIgnore > 0);
		Assert.Contains(snapshot.EffectiveExtensionOptions, option => option.Name == ".cs");
		Assert.Contains(snapshot.EffectiveExtensionOptions, option => option.Name == ".config");
		Assert.Contains(snapshot.EffectiveExtensionOptions, option => option.Name == ".dotsettings");
		Assert.DoesNotContain(snapshot.EffectiveExtensionOptions, option => option.Name == ".nupkg");
		Assert.DoesNotContain(snapshot.EffectiveExtensionOptions, option => option.Name == ".dll");
		Assert.DoesNotContain(snapshot.EffectiveExtensionOptions, option => option.Name == ".user");
	}

	[Fact]
	public void SmartController_LegacyDependencyStore_AllOptionsOffKeepsImpactAndArtifactExtensions()
	{
		using var project = new TemporaryDirectory();
		CreateLegacyPackageStoreWorkspace(project);
		var services = CreateServices();

		var snapshot = services.Engine.ComputeFullRefreshSnapshot(
			CreateAllIgnoreOptionsOffContext(project.Path),
			TestContext.Current.CancellationToken);

		AssertIgnoreOption(snapshot, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: false);
		Assert.True(snapshot.ControllerImpactCounts.SmartIgnore > 0);
		Assert.Contains(snapshot.EffectiveExtensionOptions, option => option.Name == ".nupkg");
		Assert.Contains(snapshot.EffectiveExtensionOptions, option => option.Name == ".dll");
		Assert.Contains(snapshot.EffectiveExtensionOptions, option => option.Name == ".user");
	}

	[Fact]
	public void SmartController_ReusedRefreshEngineDetectsArtifactSignatureAddedAfterInitialScan()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile("src/App.cs", "class App {}\n");
		project.CreateFile("packages/README.md", "source packages\n");
		var services = CreateServices();
		var sourceSnapshot = services.Engine.ComputeFullRefreshSnapshot(
			CreateDefaultContext(project.Path),
			TestContext.Current.CancellationToken);

		AssertIgnoreOption(sourceSnapshot, IgnoreOptionId.SmartIgnore, expectedVisible: false, expectedChecked: null);
		Assert.Contains(sourceSnapshot.EffectiveExtensionOptions, option => option.Name == ".md");

		project.CreateFile("packages/repositories.config", "<repositories />\n");
		var artifactSnapshot = services.Engine.ComputeFullRefreshSnapshot(
			CreateContextFromSnapshot(project.Path, sourceSnapshot),
			TestContext.Current.CancellationToken);

		AssertIgnoreOption(artifactSnapshot, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: true);
		Assert.True(artifactSnapshot.ControllerImpactCounts.SmartIgnore > 0);
		Assert.DoesNotContain(artifactSnapshot.EffectiveExtensionOptions, option => option.Name == ".md");
		Assert.DoesNotContain(artifactSnapshot.EffectiveExtensionOptions, option => option.Name == ".config");
	}

	[Fact]
	public void SmartController_ReusedRefreshEngineRevealsSourceAfterArtifactSignatureIsRemoved()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile("src/App.cs", "class App {}\n");
		project.CreateFile("packages/README.md", "source packages\n");
		var markerPath = project.CreateFile("packages/repositories.config", "<repositories />\n");
		var services = CreateServices();
		var artifactSnapshot = services.Engine.ComputeFullRefreshSnapshot(
			CreateDefaultContext(project.Path),
			TestContext.Current.CancellationToken);

		AssertIgnoreOption(artifactSnapshot, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: true);
		Assert.DoesNotContain(artifactSnapshot.EffectiveExtensionOptions, option => option.Name == ".md");

		File.Delete(markerPath);
		var sourceSnapshot = services.Engine.ComputeFullRefreshSnapshot(
			CreateContextFromSnapshot(project.Path, artifactSnapshot),
			TestContext.Current.CancellationToken);

		AssertIgnoreOption(sourceSnapshot, IgnoreOptionId.SmartIgnore, expectedVisible: false, expectedChecked: null);
		Assert.Equal(0, sourceSnapshot.ControllerImpactCounts.SmartIgnore);
		Assert.Contains(sourceSnapshot.EffectiveExtensionOptions, option => option.Name == ".md");
	}

	[Fact]
	public void SmartController_UserSpecificProjectStateAloneHasBidirectionalToggleImpact()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile("App.csproj", "<Project />\n");
		project.CreateFile("src/App.cs", "class App {}\n");
		project.CreateFile("App.csproj.user", "local state\n");
		var services = CreateServices();

		var enabled = services.Engine.ComputeFullRefreshSnapshot(
			CreateDefaultContext(project.Path),
			TestContext.Current.CancellationToken);
		var disabled = services.Engine.ComputeFullRefreshSnapshot(
			CreateContextWithForcedIgnoreOptions(
				project.Path,
				enabled,
				new Dictionary<IgnoreOptionId, bool>
				{
					[IgnoreOptionId.SmartIgnore] = false
				}),
			TestContext.Current.CancellationToken);

		AssertIgnoreOption(enabled, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: true);
		Assert.True(enabled.ControllerImpactCounts.SmartIgnore > 0);
		Assert.DoesNotContain(enabled.EffectiveExtensionOptions, option => option.Name == ".user");
		AssertIgnoreOption(disabled, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: false);
		Assert.True(disabled.ControllerImpactCounts.SmartIgnore > 0);
		Assert.Contains(disabled.EffectiveExtensionOptions, option => option.Name == ".user");
	}

	[Fact]
	public void SmartController_SourcePackagesWithoutArtifactFingerprint_RemainsHidden()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile("packages/domain/Order.cs", "class Order {}\n");
		project.CreateFile("packages/api/Controller.cs", "class Controller {}\n");

		var snapshot = ComputeDefaultSnapshot(project.Path);

		AssertIgnoreOption(snapshot, IgnoreOptionId.SmartIgnore, expectedVisible: false, expectedChecked: null);
		Assert.Equal(0, snapshot.ControllerImpactCounts.SmartIgnore);
		Assert.Contains(snapshot.EffectiveExtensionOptions, option => option.Name == ".cs");
	}

	[Fact]
	public void SmartArtifactRootOwnership_DoesNotStealDotFolderCountsWhenSmartIsDisabled()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile("src/App.cs", "class App {}\n");
		project.CreateFile("obj/project.assets.json", "{}\n");
		project.CreateFile(".idea/workspace.xml", "<project />\n");
		var services = CreateServices();
		var baseline = services.Engine.ComputeFullRefreshSnapshot(
			CreateDefaultContext(project.Path),
			TestContext.Current.CancellationToken);

		var smartOff = services.Engine.ComputeFullRefreshSnapshot(
			CreateContextWithForcedIgnoreOptions(
				project.Path,
				baseline,
				new Dictionary<IgnoreOptionId, bool>
				{
					[IgnoreOptionId.SmartIgnore] = false,
					[IgnoreOptionId.DotFolders] = true
				}),
			TestContext.Current.CancellationToken);

		AssertIgnoreOption(baseline, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: true);
		AssertIgnoreOption(baseline, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: true);
		Assert.Equal(1, baseline.IgnoreOptionCounts.DotFolders);

		AssertIgnoreOption(smartOff, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: false);
		AssertIgnoreOption(smartOff, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: true);
		Assert.Equal(1, smartOff.IgnoreOptionCounts.DotFolders);
		Assert.Contains(smartOff.ExtensionOptions, option => string.Equals(option.Name, ".json", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void RiderProjectsStyleWorkspace_ShowsDotFolders_WhenNestedProjectDotFoldersAffectOutput()
	{
		using var workspace = new TemporaryDirectory();
		CreateRiderProjectsStyleWorkspace(workspace);

		var services = CreateServices();
		var snapshot = services.Engine.ComputeFullRefreshSnapshot(
			CreateDefaultContext(workspace.Path),
			TestContext.Current.CancellationToken);

		var dotFolders = AssertIgnoreOption(snapshot, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: true);

		Assert.Equal(5, snapshot.IgnoreOptionCounts.DotFolders);
		Assert.Contains("(5)", dotFolders.Label);
		Assert.DoesNotContain(snapshot.ExtensionOptions, option => string.Equals(option.Name, ".xml", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void ActiveControllerWorkspace_DotFoldersRemainIndependent_WhenGitAndSmartAreDisabled()
	{
		using var project = new TemporaryDirectory();
		CreateActiveControllerWorkspace(project);

		var services = CreateServices();
		var baseline = services.Engine.ComputeFullRefreshSnapshot(
			CreateDefaultContext(project.Path),
			TestContext.Current.CancellationToken);
		var controllersOffContext = CreateContextWithDisabledIgnoreOptions(
			project.Path,
			baseline,
			IgnoreOptionId.UseGitIgnore,
			IgnoreOptionId.SmartIgnore);

		var snapshot = services.Engine.ComputeFullRefreshSnapshot(
			controllersOffContext,
			TestContext.Current.CancellationToken);

		AssertIgnoreOption(snapshot, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: false);
		var dotFolders = AssertIgnoreOption(snapshot, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: true);

		Assert.Equal(2, snapshot.IgnoreOptionCounts.DotFolders);
		Assert.Contains("(2)", dotFolders.Label);
		Assert.DoesNotContain(snapshot.ExtensionOptions, option => string.Equals(option.Name, ".xml", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void DotFileOnlyGitIgnoreController_ExplicitUncheckedStateStaysVisibleWhenDotFilesTakesOver()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile(".gitignore", ".env\n");
		project.CreateFile("App.csproj", "<Project />\n");
		project.CreateFile("Program.cs", "Console.WriteLine(\"ok\");\n");
		project.CreateFile(".env", "SECRET=1\n");

		var services = CreateServices();
		var allOff = services.Engine.ComputeFullRefreshSnapshot(
			CreateAllIgnoreOptionsOffContext(project.Path),
			TestContext.Current.CancellationToken);
		var dotFilesOn = services.Engine.ComputeFullRefreshSnapshot(
			CreateContextWithForcedIgnoreOptions(
				project.Path,
				allOff,
				new Dictionary<IgnoreOptionId, bool>
				{
					[IgnoreOptionId.UseGitIgnore] = false,
					[IgnoreOptionId.DotFiles] = true
				}),
			TestContext.Current.CancellationToken);

		AssertIgnoreOption(allOff, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: false);
		Assert.True(allOff.ControllerImpactCounts.GitIgnore > 0);
		Assert.True(dotFilesOn.ControllerImpactCounts.GitIgnore > 0);
		AssertIgnoreOption(dotFilesOn, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: false);
		AssertIgnoreOption(dotFilesOn, IgnoreOptionId.DotFiles, expectedVisible: true, expectedChecked: true);
	}

	[Fact]
	public void RiderProjectsStyleWorkspace_HidesDotFolders_WhenGitIgnoreAlreadyMasksAllDotFolders()
	{
		using var workspace = new TemporaryDirectory();
		CreateRiderProjectsStyleWorkspace(workspace, gitIgnoreOwnsDotFolders: true);

		var snapshot = ComputeDefaultSnapshot(workspace.Path);

		AssertIgnoreOption(snapshot, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: true);
		AssertIgnoreOption(snapshot, IgnoreOptionId.DotFolders, expectedVisible: false, expectedChecked: null);
		Assert.Equal(0, snapshot.IgnoreOptionCounts.DotFolders);
		Assert.DoesNotContain(snapshot.ExtensionOptions, option => string.Equals(option.Name, ".xml", StringComparison.OrdinalIgnoreCase));
	}

	private static SelectionRefreshSnapshot ComputeDefaultSnapshot(string projectPath)
	{
		var services = CreateServices();
		return services.Engine.ComputeFullRefreshSnapshot(
			CreateDefaultContext(projectPath),
			TestContext.Current.CancellationToken);
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

	private static void CreateActiveControllerWorkspace(TemporaryDirectory project)
	{
		project.CreateFile(".gitignore", "logs/\n");
		project.CreateFile("App.csproj", "<Project />\n");
		project.CreateFile("Program.cs", "Console.WriteLine(\"ok\");\n");
		project.CreateFile("bin/Debug/net10.0/App.dll", "generated\n");
		project.CreateFile("logs/runtime.log", "ignored\n");
		project.CreateFile(".idea/settings.xml", "<settings />\n");
		project.CreateFile(".run/App.run.xml", "<component />\n");
	}

	private static void CreateLegacyPackageStoreWorkspace(TemporaryDirectory project)
	{
		project.CreateFile("src/App.cs", "class App {}\n");
		project.CreateFile("packages.config", "<packages />\n");
		project.CreateFile("App.sln.DotSettings", "shared settings\n");
		project.CreateFile("App.sln.DotSettings.user", "local settings\n");
		project.CreateFile("packages/Alpha.1.0.0/Alpha.1.0.0.nupkg", "package\n");
		project.CreateFile("packages/Alpha.1.0.0/lib/Alpha.dll", "binary\n");
		project.CreateFile("packages/Beta.2.0.0/Beta.2.0.0.nupkg", "package\n");
		project.CreateFile("packages/Beta.2.0.0/ref/Beta.dll", "binary\n");
	}

	private static void CreateRiderProjectsStyleWorkspace(
		TemporaryDirectory workspace,
		bool gitIgnoreOwnsDotFolders = false)
	{
		CreateNestedProject(
			workspace,
			"DevProjex",
			"DevProjex.csproj",
			"src/MainWindow.cs",
			"class MainWindow {}",
			[".idea", ".github", ".run"],
			"bin/Debug/net10.0/DevProjex.dll",
			"logs/runtime.log",
			gitIgnoreOwnsDotFolders);
		CreateNestedProject(
			workspace,
			"SunnyEast",
			"package.json",
			"src/app.ts",
			"export const app = true;\n",
			[".idea", ".claude"],
			"node_modules/pkg/index.js",
			"dist/bundle.js",
			gitIgnoreOwnsDotFolders);
	}

	private static void CreateNestedProject(
		TemporaryDirectory workspace,
		string rootName,
		string markerPath,
		string sourcePath,
		string sourceContent,
		string[] dotFolders,
		string smartArtifactPath,
		string gitIgnoredPath,
		bool gitIgnoreOwnsDotFolders)
	{
		workspace.CreateFile($"{rootName}/{markerPath}", BuildMarkerContent(markerPath));
		workspace.CreateFile($"{rootName}/{sourcePath}", sourceContent);
		workspace.CreateFile($"{rootName}/{smartArtifactPath}", "generated\n");
		workspace.CreateFile($"{rootName}/{gitIgnoredPath}", "ignored\n");
		workspace.CreateFile($"{rootName}/.gitignore", BuildGitIgnoreContent(gitIgnoredPath, dotFolders, gitIgnoreOwnsDotFolders));

		foreach (var dotFolder in dotFolders)
		{
			// The XML-only payload makes DotFolders impact observable through extension discovery.
			workspace.CreateFile($"{rootName}/{dotFolder}/settings.xml", "<settings />\n");
		}
	}

	private static string BuildMarkerContent(string markerPath) =>
		Path.GetExtension(markerPath).Equals(".json", StringComparison.OrdinalIgnoreCase)
			? "{}\n"
			: "<Project />\n";

	private static string BuildGitIgnoreContent(
		string gitIgnoredPath,
		IEnumerable<string> dotFolders,
		bool gitIgnoreOwnsDotFolders)
	{
		var ignoredRoot = gitIgnoredPath.Split(['/', '\\'], 2)[0];
		var lines = new List<string> { $"{ignoredRoot}/" };
		if (gitIgnoreOwnsDotFolders)
			lines.AddRange(dotFolders.Select(folder => $"{folder}/"));

		return string.Join('\n', lines) + "\n";
	}

	private static SelectionRefreshContext CreateContextWithDisabledIgnoreOptions(
		string projectPath,
		SelectionRefreshSnapshot snapshot,
		params IgnoreOptionId[] disabledOptions)
	{
		var disabled = disabledOptions.ToHashSet();
		var stateCache = snapshot.IgnoreOptionStateCache.ToDictionary(pair => pair.Key, pair => pair.Value);
		foreach (var optionId in disabled)
			stateCache[optionId] = false;

		return CreateContextFromSnapshot(projectPath, snapshot) with
		{
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = snapshot.IgnoreOptions
				.Select(option => option.Id)
				.Where(optionId => !disabled.Contains(optionId))
				.ToHashSet(),
			IgnoreOptionStateCache = stateCache
		};
	}

	private static SelectionRefreshContext CreateAllIgnoreOptionsOffContext(string projectPath) =>
		CreateDefaultContext(projectPath) with
		{
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = new HashSet<IgnoreOptionId>(),
			IgnoreOptionStateCache = Enum.GetValues<IgnoreOptionId>()
				.ToDictionary(optionId => optionId, _ => false),
			IgnoreAllPreference = false,
			IgnoreOptionStateCacheIsComplete = true
		};

	private static SelectionRefreshContext CreateContextWithForcedIgnoreOptions(
		string projectPath,
		SelectionRefreshSnapshot snapshot,
		IReadOnlyDictionary<IgnoreOptionId, bool> forcedStates)
	{
		var stateCache = snapshot.IgnoreOptionStateCache.ToDictionary(pair => pair.Key, pair => pair.Value);
		var selected = snapshot.IgnoreOptions
			.Where(option => option.IsChecked)
			.Select(option => option.Id)
			.ToHashSet();
		foreach (var (optionId, isChecked) in forcedStates)
		{
			stateCache[optionId] = isChecked;
			if (isChecked)
				selected.Add(optionId);
			else
				selected.Remove(optionId);
		}

		return CreateContextFromSnapshot(projectPath, snapshot) with
		{
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = selected,
			IgnoreOptionStateCache = stateCache,
			IgnoreAllPreference = null,
			IgnoreOptionStateCacheIsComplete = true
		};
	}

	private static ResolvedIgnoreOptionState AssertIgnoreOption(
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
			return default;
		}

		Assert.Single(options);
		if (expectedChecked.HasValue)
			Assert.Equal(expectedChecked.Value, options[0].IsChecked);

		return options[0];
	}
}
