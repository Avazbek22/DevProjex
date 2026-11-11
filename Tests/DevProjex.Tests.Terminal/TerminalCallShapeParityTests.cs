using DevProjex.Application.Dependencies;
using DevProjex.Mcp;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalCallShapeParityTests
{
	[Theory]
	[InlineData("Run\\(")]
	[InlineData("Ru.*")]
	[InlineData("Run\\(\\)[\\s\\S]*needle")]
	public async Task WorkspaceSymbolRegularExpressionKeepsRegexMatchesAndReportsItsInterpretation(string pattern)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/App.cs", "public sealed class App\n{\n public string Run() => \"needle value\";\n public string Call() => Run();\n}\n");
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("data")).Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(project, ProjectProfileReference.Standard, TestContext.Current.CancellationToken);
		var plan = await controller.BuildCurrentPlanAsync(state, TestContext.Current.CancellationToken);
		var ordinary = await controller.SearchProjectAsync(plan, pattern, SearchMode.Regex, 50, TestContext.Current.CancellationToken);
		var fallback = await controller.SearchProjectAsync(plan, pattern, SearchMode.Symbols, 50, TestContext.Current.CancellationToken);
		Assert.True(fallback.SymbolsReadAsText);
		Assert.Equal(SearchMode.Regex, fallback.Mode);
		Assert.Equal(ordinary.Matches, fallback.Matches);
		Assert.Equal(ordinary.Boundary, fallback.Boundary);
		Assert.Equal(ordinary.Body, fallback.Body);
		Assert.Equal(TerminalProjectSearchOutput.Format(ordinary, services.Localization),
			TerminalProjectSearchOutput.Format(fallback, services.Localization)
				.Replace(McpSearchSymbols.RegularExpressionFallbackNotice + "\n", string.Empty, StringComparison.Ordinal));
		var exception = await Assert.ThrowsAsync<SearchCommandException>(() => controller.SearchProjectAsync(plan,
			" | ", SearchMode.Symbols, 50, TestContext.Current.CancellationToken));
		Assert.Equal("DPX-TUI-GREP-PATTERN", exception.Code);
	}

	[Theory]
	[InlineData("Parts")]
	[InlineData("./Parts/")]
	public async Task WorkspaceFolderSelectionKeepsDirectOrdinalSeedsWithinTheEffectiveFiles(string folder)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
		workspace.WriteFile("project/Parts/Z.cs", "public sealed class Z { public Target Value { get; } }\n");
		workspace.WriteFile("project/Parts/a.cs", "public sealed class A { public Target Value { get; } }\n");
		workspace.WriteFile("project/Parts/deep/NotASeed.cs", "public sealed class NotASeed {}\n");
		workspace.WriteFile("project/Parts/ignored.md", "not a selected file\n");
		workspace.WriteFile("project/Target.cs", "public sealed class Target {}\n");
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("data")).Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(project, ProjectProfileReference.Standard, TestContext.Current.CancellationToken);
		await controller.SetExtensionsAsync(state, [".cs", ".csproj"], TestContext.Current.CancellationToken);
		var plan = await controller.BuildCurrentPlanAsync(state, TestContext.Current.CancellationToken);
		var selection = RelatedQueryRunner.ResolveSeedSelection(plan, folder);
		Assert.True(selection.ExpandedFolder);
		Assert.Equal(["Parts/Z.cs", "Parts/a.cs"], selection.Files);
		var (related, _) = await controller.FindRelatedAsync(TerminalWorkspaceController.CaptureRelationSnapshot(state), plan,
			selection.Files, DependencyDirection.Dependencies, 1, GitFilteringMode.None, TestContext.Current.CancellationToken);
		Assert.Equal(selection.Files, related.Seeds.Select(static item => item.Seed));
		Assert.All(related.Seeds, seed => Assert.Contains(seed.Dependencies, dependency => dependency.Path == "Target.cs"));
	}

	[Fact]
	public async Task FolderSelectionKeepsSixteenSeedsAndPreservesOldEmptyAndOversizedErrors()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.CreateDirectory("project/Empty");
		workspace.WriteFile("project/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
		for (var index = 0; index < 17; index++)
		{
			workspace.WriteFile($"project/Over/File{index:D2}.cs", $"public sealed class Over{index:D2} {{ }}\n");
			if (index < 16) workspace.WriteFile($"project/Within/File{index:D2}.cs", $"public sealed class Within{index:D2} {{ }}\n");
		}
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("data")).Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(project, ProjectProfileReference.Standard, TestContext.Current.CancellationToken);
		var plan = await controller.BuildCurrentPlanAsync(state, TestContext.Current.CancellationToken);
		Assert.Equal(16, RelatedQueryRunner.ResolveSeeds(plan, "Within").Count);
		foreach (var folder in new[] { "Empty", "Over" })
		{
			var original = Assert.Throws<ProjectContextValidationException>(() => RelatedQueryRunner.ResolveSeed(plan, folder));
			var current = Assert.Throws<ProjectContextValidationException>(() => RelatedQueryRunner.ResolveSeeds(plan, folder));
			Assert.Equal(original.Code, current.Code);
			Assert.Equal(original.Message, current.Message);
		}
	}
}
