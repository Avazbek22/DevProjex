using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyConfigurationInheritanceIntegrationTests
{
	[Fact]
	public async Task InheritedPathsResolveRelativeToTheConfigThatDeclaredThem()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"extends\":\"./configs/base.json\"}");
		fixture.CreateFile("configs/base.json", "{\"compilerOptions\":{\"paths\":{\"@model\":[\"../src/model.ts\"]}}}");
		var source = fixture.CreateFile("src/main.ts", "import { model } from '@model';");
		var target = fixture.CreateFile("src/model.ts", "export const model = 1;");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, source, target],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge =>
			edge.Source == "src/main.ts" && edge.Target == "src/model.ts" && edge.Status == ResolutionStatus.Resolved);
	}

	[Fact]
	public async Task InheritedBaseUrlAndPathsRetainTheirDeclaringConfigOrigin()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"extends\":\"./configs/base.json\"}");
		fixture.CreateFile(
			"configs/base.json",
			"{\"compilerOptions\":{\"baseUrl\":\"../sources\",\"paths\":{\"@model\":[\"model.ts\"]}}}");
		var source = fixture.CreateFile("main.ts", "import { model } from '@model';");
		var target = fixture.CreateFile("sources/model.ts", "export const model = 1;");
		var configuration = await new FileDependencyConfigurationProvider().ReadAsync(
			fixture.Path,
			[config, source, target],
			TestContext.Current.CancellationToken);
		Assert.Equal([Path.Combine("sources", "model.ts")],
			Assert.Single(configuration.Scopes, scope => scope.HasConfiguration).TypeScriptPaths["@model"]);
		Assert.True(Assert.Single(configuration.Scopes, scope => scope.HasConfiguration).LegacyTypeScriptConfiguration);
	}

	[Fact]
	public async Task InheritedNodeNextRejectsExtensionlessEsmImport()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"extends\":\"./base.json\"}");
		fixture.CreateFile("base.json", "{\"compilerOptions\":{\"moduleResolution\":\"NodeNext\"}}");
		var package = fixture.CreateFile("package.json", "{\"type\":\"module\"}");
		var source = fixture.CreateFile("main.ts", "import value from './dep';");
		var target = fixture.CreateFile("dep.ts", "export default 1;");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, package, source, target],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, edge => edge.Source == "main.ts" && edge.Reference == "./dep");
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Contains(edge.Reasons, reason => reason.Contains("extension required", StringComparison.Ordinal));
	}

	[Fact]
	public async Task ChildCompilerOptionsOverrideInheritedKeys()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"tsconfig.json",
			"{\"extends\":\"./base.json\",\"compilerOptions\":{\"moduleResolution\":\"bundler\",\"paths\":{\"alias\":[\"new.ts\"]}}}");
		fixture.CreateFile(
			"base.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"NodeNext\",\"paths\":{\"alias\":[\"old.ts\"]}}}");
		var package = fixture.CreateFile("package.json", "{\"type\":\"module\"}");
		var source = fixture.CreateFile("main.ts", "import value from './dep'; import { chosen } from 'alias';");
		var dependency = fixture.CreateFile("dep.ts", "export default 1;");
		var oldTarget = fixture.CreateFile("old.ts", "export const chosen = 'old';");
		var newTarget = fixture.CreateFile("new.ts", "export const chosen = 'new';");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, package, source, dependency, oldTarget, newTarget],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge => edge.Source == "main.ts" && edge.Target == "dep.ts");
		Assert.Contains(result.Edges, edge => edge.Source == "main.ts" && edge.Target == "new.ts");
		Assert.DoesNotContain(result.Edges, edge => edge.Source == "main.ts" && edge.Target == "old.ts");
	}

	[Theory]
	[InlineData("{\"extends\":\"@tsconfig/node20/tsconfig.json\"}", "tsconfig package extends is not supported")]
	[InlineData("{\"extends\":\"base.json\"}", "tsconfig package extends is not supported")]
	[InlineData("{\"extends\":[\"./base.json\"]}", "tsconfig extends must be one relative path string")]
	[InlineData("{\"extends\":\"../outside.json\"}", "tsconfig extends must stay inside the project root")]
	public async Task UnsupportedExtendsFormsUseConstantDiagnostics(string content, string reason)
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", content);

		var result = await new FileDependencyConfigurationProvider().ReadAsync(
			fixture.Path,
			[config],
			TestContext.Current.CancellationToken);

		var diagnostic = Assert.Single(result.ConfigurationDiagnostics);
		Assert.Equal(DependencyConfigurationState.UnsupportedSemantics, diagnostic.State);
		Assert.Equal(reason, diagnostic.Reason);
	}

	[Fact]
	public async Task ExtendsCycleAndDepthLimitUseConstantDiagnostics()
	{
		using var cycle = new TemporaryDirectory();
		var cycleConfig = cycle.CreateFile("tsconfig.json", "{\"extends\":\"./base.json\"}");
		cycle.CreateFile("base.json", "{\"extends\":\"./tsconfig.json\"}");
		var cycleResult = await new FileDependencyConfigurationProvider().ReadAsync(
			cycle.Path,
			[cycleConfig],
			TestContext.Current.CancellationToken);

		Assert.Equal(
			"tsconfig extends cycle is not supported",
			Assert.Single(cycleResult.ConfigurationDiagnostics).Reason);

		using var deep = new TemporaryDirectory();
		var deepConfig = deep.CreateFile("tsconfig.json", "{\"extends\":\"./base-1.json\"}");
		for (var index = 1; index <= FileDependencyConfigurationProvider.MaximumTypeScriptExtendsDepth; index++)
		{
			deep.CreateFile(
				$"base-{index}.json",
				$"{{\"extends\":\"./base-{index + 1}.json\"}}");
		}
		var deepResult = await new FileDependencyConfigurationProvider().ReadAsync(
			deep.Path,
			[deepConfig],
			TestContext.Current.CancellationToken);

		Assert.Equal(
			"tsconfig extends exceeds the maximum depth",
			Assert.Single(deepResult.ConfigurationDiagnostics).Reason);
	}

	[Fact]
	public async Task EightExtendsEdgesAreSupported()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"extends\":\"./base-1.json\"}");
		for (var index = 1; index < FileDependencyConfigurationProvider.MaximumTypeScriptExtendsDepth; index++)
			fixture.CreateFile($"base-{index}.json", $"{{\"extends\":\"./base-{index + 1}.json\"}}");
		fixture.CreateFile(
			$"base-{FileDependencyConfigurationProvider.MaximumTypeScriptExtendsDepth}.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"NodeNext\"}}");

		var result = await new FileDependencyConfigurationProvider().ReadAsync(
			fixture.Path,
			[config],
			TestContext.Current.CancellationToken);

		Assert.Empty(result.ConfigurationDiagnostics);
		Assert.Equal("nodenext", Assert.Single(result.Scopes, scope => scope.HasConfiguration).ModuleResolution);
	}

	[Fact]
	public async Task MissingExtendedConfigIsObservedAndItsAppearanceChangesFingerprint()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"extends\":\"./base.json\"}");
		var provider = new FileDependencyConfigurationProvider();

		var missing = await provider.ReadAsync(
			fixture.Path,
			[config],
			TestContext.Current.CancellationToken);
		var basePath = Path.Combine(fixture.Path, "base.json");
		Assert.Contains(basePath, missing.AbsentControlFiles, PathComparer.Default);
		Assert.Equal(DependencyConfigurationState.Missing, Assert.Single(missing.ConfigurationDiagnostics).State);
		Assert.Equal("extended tsconfig is unavailable", Assert.Single(missing.ConfigurationDiagnostics).Reason);

		File.WriteAllText(basePath, "{\"compilerOptions\":{\"moduleResolution\":\"NodeNext\"}}");
		var present = await provider.ReadAsync(
			fixture.Path,
			[config],
			TestContext.Current.CancellationToken);

		Assert.Empty(present.AbsentControlFiles);
		Assert.Empty(present.ConfigurationDiagnostics);
		Assert.NotEqual(missing.Fingerprint, present.Fingerprint);
		Assert.Equal("nodenext", Assert.Single(present.Scopes, scope => scope.HasConfiguration).ModuleResolution);
	}

	[Fact]
	public async Task ExtendedConfigChangeInvalidatesTheDependencySnapshot()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"extends\":\"./base.json\"}");
		var baseConfig = fixture.CreateFile("base.json", "{\"compilerOptions\":{\"paths\":{\"alias\":[\"first.ts\"]}}}");
		var source = fixture.CreateFile("main.ts", "import { value } from 'alias';");
		var firstTarget = fixture.CreateFile("first.ts", "export const value = 1;");
		var secondTarget = fixture.CreateFile("other.ts", "export const value = 2;");
		var manifest = new[] { config, source, firstTarget, secondTarget };
		using var engine = CreateEngine();

		var first = await engine.IndexAsync(
			fixture.Path,
			manifest,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(first.Edges, edge => edge.Source == "main.ts" && edge.Target == "first.ts");

		File.WriteAllText(baseConfig, "{\"compilerOptions\":{\"paths\":{\"alias\":[\"other.ts\"]}}}");
		File.SetLastWriteTimeUtc(baseConfig, DateTime.UtcNow.AddSeconds(2));
		var second = await engine.IndexAsync(
			fixture.Path,
			manifest,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(second.Edges, edge => edge.Source == "main.ts" && edge.Target == "other.ts");
		Assert.DoesNotContain(second.Edges, edge => edge.Source == "main.ts" && edge.Target == "first.ts");
		Assert.False(second.Metrics.ResolutionCacheHit);
	}

	[Fact]
	public async Task ConfigWithoutExtendsKeepsBundlerDefaults()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{}");

		var result = await new FileDependencyConfigurationProvider().ReadAsync(
			fixture.Path,
			[config],
			TestContext.Current.CancellationToken);

		var scope = Assert.Single(result.Scopes, scope => scope.HasConfiguration);
		Assert.Equal("bundler", scope.ModuleResolution);
		Assert.False(scope.LegacyTypeScriptConfiguration);
		Assert.False(scope.AllowJavaScript);
		Assert.Empty(scope.TypeScriptPaths);
		Assert.Empty(result.ConfigurationDiagnostics);
	}

	[Theory]
	[InlineData("node10", "node10")]
	[InlineData("NODE", "node")]
	[InlineData("Classic", "classic")]
	[InlineData("node16", "node16")]
	[InlineData("NodeNext", "nodenext")]
	[InlineData("BUNDLER", "bundler")]
	public async Task ModuleResolutionUsesTheSupportedCaseInsensitiveVocabulary(string configured, string expected)
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"tsconfig.json",
			$"{{\"compilerOptions\":{{\"moduleResolution\":\"{configured}\"}}}}");

		var result = await new FileDependencyConfigurationProvider().ReadAsync(
			fixture.Path,
			[config],
			TestContext.Current.CancellationToken);

		Assert.Empty(result.ConfigurationDiagnostics);
		Assert.Equal(expected, Assert.Single(result.Scopes, scope => scope.HasConfiguration).ModuleResolution);
	}

	[Fact]
	public async Task UnknownModuleResolutionUsesAConstantDiagnostic()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"future-mode\"}}");

		var result = await new FileDependencyConfigurationProvider().ReadAsync(
			fixture.Path,
			[config],
			TestContext.Current.CancellationToken);

		var diagnostic = Assert.Single(result.ConfigurationDiagnostics);
		Assert.Equal(DependencyConfigurationState.UnsupportedSemantics, diagnostic.State);
		Assert.Equal(FileDependencyConfigurationProvider.TypeScriptModuleResolutionReason, diagnostic.Reason);
	}

	[Fact]
	public async Task ProjectReferencesIgnoreFalseAndUnevaluatedConditions()
	{
		using var fixture = new TemporaryDirectory();
		var rootProject = fixture.CreateFile(
			"Root.csproj",
			"""
			<Project>
			  <ItemGroup>
			    <ProjectReference Include="Enabled.csproj" Condition="true" />
			    <ProjectReference Include="False.csproj" Condition="false" />
			    <ProjectReference Include="Quoted.csproj" Condition="'false'" />
			    <ProjectReference Include="Unknown.csproj" Condition="'$(Configuration)' == 'Debug'" />
			    <ProjectReference Include="OutputDisabled.csproj" ReferenceOutputAssembly="false" />
			  </ItemGroup>
			</Project>
			""");
		var projects = new[]
		{
			rootProject,
			fixture.CreateFile("Enabled.csproj", "<Project />"),
			fixture.CreateFile("False.csproj", "<Project />"),
			fixture.CreateFile("Quoted.csproj", "<Project />"),
			fixture.CreateFile("Unknown.csproj", "<Project />"),
			fixture.CreateFile("OutputDisabled.csproj", "<Project />")
		};

		var result = await new FileDependencyConfigurationProvider().ReadAsync(
			fixture.Path,
			projects,
			TestContext.Current.CancellationToken);

		var rootScope = Assert.Single(result.Scopes, scope => scope.ScopeId == "csharp:Root.csproj");
		Assert.Equal(["csharp:Enabled.csproj"], rootScope.ProjectReferences);
		Assert.Equal(DependencyConfigurationState.UnsupportedSemantics, rootScope.ConfigurationState);
		Assert.Equal(
			FileDependencyConfigurationProvider.ProjectReferenceConditionReason,
			rootScope.ConfigurationDiagnostic);
	}

	[Fact]
	public async Task ExternalAndNetworkProjectReferencesAreRejectedBeforeMetadataAccess()
	{
		using var fixture = new TemporaryDirectory();
		using var outside = new TemporaryDirectory();
		var safeMissing = Path.Combine(fixture.Path, "Missing.csproj");
		var project = fixture.CreateFile(
			"Root.csproj",
			$"""
			<Project><ItemGroup>
			  <ProjectReference Include="Missing.csproj" />
			  <ProjectReference Include="{Path.Combine(outside.Path, "Outside.csproj")}" />
			  <ProjectReference Include="\\\\server\\share\\Remote.csproj" />
			</ItemGroup></Project>
			""");
		var metadata = new RecordingPathMetadata();
		var provider = new FileDependencyConfigurationProvider(
			new BoundedDependencyControlFileReader(),
			metadata);

		var result = await provider.ReadAsync(
			fixture.Path,
			[project],
			TestContext.Current.CancellationToken);

		Assert.Equal([safeMissing], metadata.FileExistenceChecks, PathComparer.Default);
		Assert.All(metadata.ContainmentChecks, path => Assert.True(IsWithin(fixture.Path, path)));
		Assert.Contains(safeMissing, result.AbsentControlFiles, PathComparer.Default);
	}

	[Fact]
	public async Task ProjectReferenceSymlinkOutsideTheRootIsNotProbedAsAControlFile()
	{
		using var fixture = new TemporaryDirectory();
		using var outside = new TemporaryDirectory();
		var outsideProject = outside.CreateFile("Outside.csproj", "<Project />");
		var link = Path.Combine(fixture.Path, "Linked.csproj");
		try
		{
			File.CreateSymbolicLink(link, outsideProject);
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
			Assert.Skip($"File symbolic links are unavailable: {exception.GetType().Name}.");
			return;
		}
		var project = fixture.CreateFile(
			"Root.csproj",
			"<Project><ItemGroup><ProjectReference Include=\"Linked.csproj\" /></ItemGroup></Project>");

		var result = await new FileDependencyConfigurationProvider().ReadAsync(
			fixture.Path,
			[project],
			TestContext.Current.CancellationToken);

		Assert.DoesNotContain(link, result.AbsentControlFiles, PathComparer.Default);
	}

	private static bool IsWithin(string root, string path)
	{
		var relative = Path.GetRelativePath(root, path);
		return relative != ".." &&
		       !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
		       !Path.IsPathRooted(relative);
	}

	private sealed class RecordingPathMetadata : IDependencyPathMetadata
	{
		public List<string> FileExistenceChecks { get; } = [];
		public List<string> ContainmentChecks { get; } = [];

		public bool FileExists(string path)
		{
			FileExistenceChecks.Add(path);
			return false;
		}

		public bool TryResolveContainedPath(string root, string path, out string resolvedPath)
		{
			ContainmentChecks.Add(path);
			resolvedPath = path;
			return true;
		}
	}

	private static DependencyFactsEngine CreateEngine() =>
		new(new TreeSitterDependencyFactExtractor(), new FileDependencyConfigurationProvider());
}
