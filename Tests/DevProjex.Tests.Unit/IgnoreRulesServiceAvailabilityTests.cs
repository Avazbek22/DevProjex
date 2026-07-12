namespace DevProjex.Tests.Unit;

public sealed class IgnoreRulesServiceAvailabilityTests
{
	[Fact]
	public void GetIgnoreOptionsAvailability_SingleProjectWithGitIgnore_HidesSmartOption()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "bin/");
		temp.CreateFile("App.csproj", "<Project />");

		var service = new IgnoreRulesService(new SmartIgnoreService([]));
		var availability = service.GetIgnoreOptionsAvailability(temp.Path, []);

		Assert.True(availability.IncludeGitIgnore);
		Assert.False(availability.IncludeSmartIgnore);
		Assert.True(availability.SmartIgnoreFollowsGitIgnore);
	}

	[Fact]
	public void GetIgnoreOptionsAvailability_SingleProjectWithoutGitIgnore_ShowsOnlySmartOption()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("package.json", "{}");

		var service = new IgnoreRulesService(new SmartIgnoreService([]));
		var availability = service.GetIgnoreOptionsAvailability(temp.Path, []);

		Assert.False(availability.IncludeGitIgnore);
		Assert.True(availability.IncludeSmartIgnore);
	}

	[Theory]
	[InlineData("requirements.txt")]
	[InlineData("setup.py")]
	[InlineData("Pipfile")]
	[InlineData("poetry.lock")]
	[InlineData("environment.yml")]
	public void GetIgnoreOptionsAvailability_PythonMarkerKnownOnlyBySmartRule_ShowsSmartOption(string markerFile)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(markerFile, string.Empty);

		var service = new IgnoreRulesService(new SmartIgnoreService([
			new PythonArtifactsIgnoreRule()
		]));
		var availability = service.GetIgnoreOptionsAvailability(temp.Path, []);

		Assert.False(availability.IncludeGitIgnore);
		Assert.True(availability.IncludeSmartIgnore);
	}

	[Fact]
	public void Build_RootProjectMarkerWithExplicitRootSelection_KeepsRootSmartScope()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("pyproject.toml", "[project]");
		temp.CreateFile("src/app.py", "print('ok')");
		temp.CreateFile("src/__pycache__/app.pyc", "binary");

		var service = new IgnoreRulesService(new SmartIgnoreService([
			new PythonArtifactsIgnoreRule()
		]));

		var rules = service.Build(
			temp.Path,
			[IgnoreOptionId.SmartIgnore],
			selectedRootFolders: ["src"]);

		Assert.True(rules.UseSmartIgnore);
		Assert.Contains(temp.Path, rules.SmartIgnoreScopeRoots, PathComparer.Default);
		Assert.True(rules.IsSmartIgnoredDirectory(
			Path.Combine(temp.Path, "src", "__pycache__"),
			"__pycache__"));
	}

	[Fact]
	public void GetIgnoreOptionsAvailability_SingleGitIgnoreProjectWithExplicitRootSelection_HidesSmartOption()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "*.log");
		temp.CreateFile("pyproject.toml", "[project]");
		temp.CreateFile("src/app.py", "print('ok')");

		var service = new IgnoreRulesService(new SmartIgnoreService([
			new PythonArtifactsIgnoreRule()
		]));
		var availability = service.GetIgnoreOptionsAvailability(temp.Path, ["src"]);

		Assert.True(availability.IncludeGitIgnore);
		Assert.False(availability.IncludeSmartIgnore);
		Assert.True(availability.SmartIgnoreFollowsGitIgnore);
	}

	[Fact]
	public void Build_SingleGitIgnoreProjectWithExplicitRootSelection_UseGitIgnoreControlsSmartArtifacts()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "*.log");
		temp.CreateFile("pyproject.toml", "[project]");
		temp.CreateFile("src/app.py", "print('ok')");
		temp.CreateFile("src/__pycache__/app.pyc", "binary");

		var service = new IgnoreRulesService(new SmartIgnoreService([
			new PythonArtifactsIgnoreRule()
		]));

		var rules = service.Build(
			temp.Path,
			[IgnoreOptionId.UseGitIgnore],
			selectedRootFolders: ["src"]);

		Assert.True(rules.UseGitIgnore);
		Assert.True(rules.UseSmartIgnore);
		Assert.Contains(temp.Path, rules.SmartIgnoreScopeRoots, PathComparer.Default);
		Assert.True(rules.IsSmartIgnoredDirectory(
			Path.Combine(temp.Path, "src", "__pycache__"),
			"__pycache__"));
	}

	[Fact]
	public void GetIgnoreOptionsAvailability_MixedWorkspace_ShowsBothGitAndSmartOptions()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("proj-git/.gitignore", "bin/");
		temp.CreateFile("proj-git/App.csproj", "<Project />");
		temp.CreateFile("proj-no-git/package.json", "{}");

		var service = new IgnoreRulesService(new SmartIgnoreService([]));
		var availability = service.GetIgnoreOptionsAvailability(temp.Path, []);

		Assert.True(availability.IncludeGitIgnore);
		Assert.True(availability.IncludeSmartIgnore);
		Assert.False(availability.SmartIgnoreFollowsGitIgnore);
	}

	[Fact]
	public void GetIgnoreOptionsAvailability_DeepMonorepoGitAndSmartScopes_ShowBothPrimaryOptions()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("pnpm-workspace.yaml", "packages:\n  - apps/**\n");
		temp.CreateFile("apps/domain/team/api/.gitignore", "generated/\n");
		temp.CreateFile("apps/domain/team/worker/pyproject.toml", "[project]\nname = \"worker\"\n");

		var service = new IgnoreRulesService(new SmartIgnoreService([
			new PythonArtifactsIgnoreRule()
		]));
		var availability = service.GetIgnoreOptionsAvailability(temp.Path, []);
		var options = CreateIgnoreOptionsService().GetOptions(availability);

		Assert.True(availability.IncludeGitIgnore);
		Assert.True(availability.IncludeSmartIgnore);
		Assert.Equal(6, options.Count);
		Assert.Equal(IgnoreOptionId.SmartIgnore, options[0].Id);
		Assert.Equal(IgnoreOptionId.UseGitIgnore, options[1].Id);
	}

	[Fact]
	public void GetIgnoreOptionsAvailability_NestedProjectInSelectedFolder_ShowsSmartOption()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("Visual Studio 2019/America/America.sln", "");
		temp.CreateFile("Visual Studio 2019/America/America/America.csproj", "<Project />");

		var service = new IgnoreRulesService(new SmartIgnoreService([]));
		var availability = service.GetIgnoreOptionsAvailability(temp.Path, ["Visual Studio 2019"]);

		Assert.False(availability.IncludeGitIgnore);
		Assert.True(availability.IncludeSmartIgnore);
	}

	[Fact]
	public void Build_SelectedNestedProjectFolder_ProducesDotNetSmartIgnoreFolders()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("Visual Studio 2019/America/America.sln", "");
		temp.CreateFile("Visual Studio 2019/America/America/America.csproj", "<Project />");

		var smartService = new SmartIgnoreService([
			new DotNetArtifactsIgnoreRule()
		]);
		var service = new IgnoreRulesService(smartService);
		var rules = service.Build(
			temp.Path,
			[IgnoreOptionId.SmartIgnore],
			selectedRootFolders: ["Visual Studio 2019"]);

		Assert.True(rules.UseSmartIgnore);
		Assert.Contains("bin", rules.SmartIgnoredFolders);
		Assert.Contains("obj", rules.SmartIgnoredFolders);

		var nestedProjectPath = Path.Combine(temp.Path, "Visual Studio 2019", "America", "America");
		Assert.True(rules.ShouldApplySmartIgnore(nestedProjectPath));
		Assert.True(rules.SmartIgnoreScopeRoots.Any());
	}

	[Fact]
	public void GetIgnoreOptionsAvailability_ParentFolderDepthTwoProject_ShowsSmartOption()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("Documents/Visual Studio 2019/America/America.sln", "");
		temp.CreateFile("Documents/Visual Studio 2019/America/America/America.csproj", "<Project />");

		var service = new IgnoreRulesService(new SmartIgnoreService([]));
		var availability = service.GetIgnoreOptionsAvailability(temp.Path, ["Documents"]);

		Assert.False(availability.IncludeGitIgnore);
		Assert.True(availability.IncludeSmartIgnore);
	}

	[Fact]
	public void GetIgnoreOptionsAvailability_ParentFolderWithNestedGitIgnoreProject_ShowsGitOption()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("Documents/Visual Studio 2019/America/.gitignore", "bin/\nobj/\n");
		temp.CreateFile("Documents/Visual Studio 2019/America/America/America.csproj", "<Project />");

		var service = new IgnoreRulesService(new SmartIgnoreService([]));
		var availability = service.GetIgnoreOptionsAvailability(temp.Path, ["Documents"]);

		Assert.True(availability.IncludeGitIgnore);
		Assert.False(availability.IncludeSmartIgnore);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	public void GetIgnoreOptionsAvailability_NestedDotNetProject_AvailabilityStableAcrossOpenedRootLevels(int rootMode)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("Documents/Visual Studio 2019/America/America.sln", "");
		temp.CreateFile("Documents/Visual Studio 2019/America/America/America.csproj", "<Project />");

		var (openedRootPath, selectedRootFolders) = ResolveRootMode(temp.Path, rootMode);
		var service = new IgnoreRulesService(new SmartIgnoreService([]));
		var availability = service.GetIgnoreOptionsAvailability(openedRootPath, selectedRootFolders);

		Assert.False(availability.IncludeGitIgnore);
		Assert.True(availability.IncludeSmartIgnore);
	}

	[Theory]
	[InlineData(0, false)]
	[InlineData(1, true)]
	[InlineData(2, true)]
	public void GetIgnoreOptionsAvailability_NestedGitIgnoreProject_ReflectsDiscoveredScopesByOpenedRootLevel(
		int rootMode,
		bool expectedIncludeSmartIgnore)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("Documents/Visual Studio 2019/America/.gitignore", "bin/\nobj/\n");
		temp.CreateFile("Documents/Visual Studio 2019/America/America/America.csproj", "<Project />");

		var (openedRootPath, selectedRootFolders) = ResolveRootMode(temp.Path, rootMode);
		var service = new IgnoreRulesService(new SmartIgnoreService([]));
		var availability = service.GetIgnoreOptionsAvailability(openedRootPath, selectedRootFolders);

		Assert.True(availability.IncludeGitIgnore);
		Assert.Equal(expectedIncludeSmartIgnore, availability.IncludeSmartIgnore);
	}

	[Fact]
	public void Build_ParentFolderWithNestedGitIgnoreProject_KeepsSmartToggleExplicitWhenSmartOptionAvailable()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("Documents/Visual Studio 2019/America/.gitignore", "bin/\nobj/\n");
		temp.CreateFile("Documents/Visual Studio 2019/America/America/America.csproj", "<Project />");

		var smartService = new SmartIgnoreService([
			new DotNetArtifactsIgnoreRule()
		]);
		var service = new IgnoreRulesService(smartService);
		var rules = service.Build(
			temp.Path,
			[IgnoreOptionId.UseGitIgnore],
			selectedRootFolders: ["Documents"]);

		Assert.True(rules.UseGitIgnore);
		Assert.False(rules.UseSmartIgnore);
		Assert.NotEmpty(rules.ScopedGitIgnoreMatchers);
		Assert.Empty(rules.SmartIgnoredFolders);
		Assert.Empty(rules.SmartIgnoredFiles);
	}

	[Fact]
	public void GetIgnoreOptionsAvailability_DoesNotThrow_WhenSmartIgnoreRuleFails()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("Documents/Visual Studio 2019/America/America.sln", "");

		var smartService = new SmartIgnoreService([
			new ThrowingSmartIgnoreRule()
		]);
		var service = new IgnoreRulesService(smartService);

		var availability = service.GetIgnoreOptionsAvailability(temp.Path, ["Documents"]);
		Assert.False(availability.IncludeGitIgnore);
		Assert.True(availability.IncludeSmartIgnore);
	}

	private static (string OpenedRootPath, IReadOnlyCollection<string> SelectedRootFolders) ResolveRootMode(
		string tempPath,
		int rootMode)
	{
		return rootMode switch
		{
			0 => (
				OpenedRootPath: tempPath,
				SelectedRootFolders: ["Documents"]),
			1 => (
				OpenedRootPath: Path.Combine(tempPath, "Documents"),
				SelectedRootFolders: ["Visual Studio 2019"]),
			2 => (
				OpenedRootPath: Path.Combine(tempPath, "Documents", "Visual Studio 2019"),
				SelectedRootFolders: new[] { "America" }),
			_ => throw new ArgumentOutOfRangeException(nameof(rootMode), rootMode, "Unsupported root mode.")
		};
	}

	private sealed class ThrowingSmartIgnoreRule : ISmartIgnoreRule
	{
		public SmartIgnoreResult Evaluate(string rootPath)
		{
			throw new UnauthorizedAccessException("Access denied.");
		}
	}

	private static IgnoreOptionsService CreateIgnoreOptionsService()
	{
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Settings.Ignore.SmartIgnore"] = "Smart Ignore",
				["Settings.Ignore.UseGitIgnore"] = "Use .gitignore",
				["Settings.Ignore.HiddenFolders"] = "Hidden folders",
				["Settings.Ignore.HiddenFiles"] = "Hidden files",
				["Settings.Ignore.DotFolders"] = "Dot folders",
				["Settings.Ignore.DotFiles"] = "Dot files"
			}
		});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		return new IgnoreOptionsService(localization);
	}
}
