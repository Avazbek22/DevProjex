namespace DevProjex.Tests.Unit;

public sealed class ProjectScopeDiscoveryServiceTests
{
	[Fact]
	public void Discover_SelectedMissingRoot_ReturnsEmptyContextWithoutWorkspaceFallback()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("deleted/package.json", "{}");
		Directory.Delete(Path.Combine(temp.Path, "deleted"), recursive: true);
		temp.CreateFile("sibling/package.json", "{}");

		var discovery = CreateDiscovery();
		var context = discovery.Discover(temp.Path, ["deleted"]);

		Assert.Empty(context.Scopes);
		Assert.False(context.HasAnyGitIgnore);
		Assert.False(context.HasAnyWithoutGitIgnore);
	}

	[Fact]
	public void Discover_MixedWorkspace_UsesOneScopeModelForGitAndMarkerProjects()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("api/.gitignore", "bin/\n");
		temp.CreateFile("api/App.csproj", "<Project />");
		temp.CreateFile("web/package.json", "{}");
		temp.CreateFile("web/src/app.ts", "export {}");

		var discovery = CreateDiscovery();
		var context = discovery.Discover(temp.Path, selectedRootFolders: null);

		Assert.True(context.HasAnyGitIgnore);
		Assert.True(context.HasAnyWithoutGitIgnore);
		Assert.Contains(context.Scopes, scope => scope.HasGitIgnore && scope.RootPath.EndsWith("api"));
		Assert.Contains(context.Scopes, scope => scope.HasProjectMarker && scope.RootPath.EndsWith("web"));
	}

	[Fact]
	public void Discover_PythonProjectWithIdeaGitIgnore_DoesNotTreatIdeaFolderAsGitScope()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("requirements.txt", "pytest\n");
		temp.CreateFile(".idea/.gitignore", "# JetBrains internal file\n");
		temp.CreateFile(".idea/workspace.xml", "<project />");
		temp.CreateFile("__pycache__/main.pyc", "binary");

		var discovery = CreateDiscovery();
		var context = discovery.Discover(temp.Path, selectedRootFolders: null);

		Assert.False(context.HasAnyGitIgnore);
		Assert.True(context.HasAnyWithoutGitIgnore);
		Assert.DoesNotContain(context.Scopes, scope => scope.RootPath.EndsWith(".idea", StringComparison.OrdinalIgnoreCase));
		Assert.Contains(context.Scopes, scope => PathComparer.Default.Equals(scope.RootPath, temp.Path));
	}

	[Fact]
	public void Discover_SelectedProject_KeepsRealNestedGitIgnoreButSkipsIdeaGitIgnore()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("workspace/requirements.txt", "pytest\n");
		temp.CreateFile("workspace/.idea/.gitignore", "# JetBrains internal file\n");
		temp.CreateFile("workspace/module/.gitignore", "*.tmp\n");
		temp.CreateFile("workspace/module/file.tmp", "ignored");

		var discovery = CreateDiscovery();
		var context = discovery.Discover(temp.Path, ["workspace"]);

		Assert.True(context.HasAnyGitIgnore);
		Assert.Contains(context.Scopes, scope => scope.RootPath.EndsWith("module", StringComparison.OrdinalIgnoreCase));
		Assert.DoesNotContain(context.Scopes, scope => scope.RootPath.EndsWith(".idea", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void Discover_NestedProjectProbe_StopsAtConfiguredDepth()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("workspace/level1/level2/package.json", "{}");
		temp.CreateFile("workspace/level1/level2/level3/package.json", "{}");

		var discovery = CreateDiscovery();
		var context = discovery.Discover(temp.Path, ["workspace"]);

		Assert.Contains(context.Scopes, scope => scope.RootPath.EndsWith(Path.Combine("level1", "level2")));
		Assert.DoesNotContain(context.Scopes, scope => scope.RootPath.EndsWith(Path.Combine("level2", "level3")));
	}

	[Fact]
	public void Discover_SmartDescriptorMarkerFile_DetectsCustomProjectScope()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("custom/custom.project", "marker");

		var discovery = new ProjectScopeDiscoveryService(new SmartIgnoreService([
			new DescriptorOnlySmartIgnoreRule(markerFiles: ["custom.project"])
		]));
		var context = discovery.Discover(temp.Path, selectedRootFolders: null);

		Assert.Contains(context.Scopes, scope => scope.RootPath.EndsWith("custom", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void Discover_SmartDescriptorMarkerExtension_DetectsCustomProjectScope()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("custom/app.xproj", "marker");

		var discovery = new ProjectScopeDiscoveryService(new SmartIgnoreService([
			new DescriptorOnlySmartIgnoreRule(markerExtensions: [".xproj"])
		]));
		var context = discovery.Discover(temp.Path, selectedRootFolders: null);

		Assert.Contains(context.Scopes, scope => scope.RootPath.EndsWith("custom", StringComparison.OrdinalIgnoreCase));
	}

	private static ProjectScopeDiscoveryService CreateDiscovery() =>
		new(new SmartIgnoreService([]));

	private sealed class DescriptorOnlySmartIgnoreRule(
		IEnumerable<string>? markerFiles = null,
		IEnumerable<string>? markerExtensions = null)
		: ISmartIgnoreRule, ISmartIgnoreRuleDescriptorProvider
	{
		public SmartIgnoreRuleDescriptor Descriptor { get; } = new(
			(markerFiles ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase),
			(markerExtensions ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase),
			new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			new HashSet<string>(StringComparer.OrdinalIgnoreCase));

		public SmartIgnoreResult Evaluate(string rootPath) => SmartIgnoreResult.Empty;
	}
}
