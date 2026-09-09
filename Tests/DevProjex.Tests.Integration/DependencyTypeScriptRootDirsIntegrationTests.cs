using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyTypeScriptRootDirsIntegrationTests
{
	[Fact]
	public async Task RootDirsResolveTheSameVirtualRelativePath()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\",\"rootDirs\":[\"src\",\"generated\"]}}");
		var source = fixture.CreateFile("src/view.ts", "import { value } from './types'; export { value };\n");
		var target = fixture.CreateFile("generated/types.ts", "export const value = 1;\n");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, source, target],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "src/view.ts" && candidate.Reference == "./types");
		Assert.Equal(ResolutionStatus.Resolved, edge.Status);
		Assert.Equal("generated/types.ts", edge.Target);
		Assert.Equal(["generated/types.ts"], edge.Candidates);
	}

	[Fact]
	public async Task RootDirsReportEveryManifestCandidateAsAmbiguous()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\",\"rootDirs\":[\"src\",\"generated\"]}}");
		var source = fixture.CreateFile("src/view.ts", "import { value } from './types'; export { value };\n");
		var sourceTarget = fixture.CreateFile("src/types.ts", "export const value = 1;\n");
		var generatedTarget = fixture.CreateFile("generated/types.ts", "export const value = 2;\n");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, source, sourceTarget, generatedTarget],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "src/view.ts" && candidate.Reference == "./types");
		Assert.Equal(ResolutionStatus.Ambiguous, edge.Status);
		Assert.Null(edge.Target);
		Assert.Equal(["generated/types.ts", "src/types.ts"], edge.Candidates);
	}

	[Fact]
	public async Task RootDirsDoNotExposeTargetsOutsideTheManifest()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\",\"rootDirs\":[\"src\",\"generated\"]}}");
		var source = fixture.CreateFile("src/view.ts", "import { value } from './types'; export { value };\n");
		var omitted = fixture.CreateFile("generated/types.ts", "export const value = 1;\n");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "src/view.ts" && candidate.Reference == "./types");
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Null(edge.Target);
		Assert.Empty(edge.Candidates);
		Assert.DoesNotContain(omitted, string.Join("\n", edge.Reasons), StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task RootDirsChangeReresolvesWithoutReparsingSources()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\",\"rootDirs\":[\"src\",\"first\"]}}");
		var source = fixture.CreateFile("src/view.ts", "import { value } from './types'; export { value };\n");
		var firstTarget = fixture.CreateFile("first/types.ts", "export const value = 1;\n");
		var secondTarget = fixture.CreateFile("second/types.ts", "export const value = 2;\n");
		var manifest = new[] { config, source, firstTarget, secondTarget };
		using var engine = CreateEngine();

		var first = await engine.IndexAsync(
			fixture.Path,
			manifest,
			cancellationToken: TestContext.Current.CancellationToken);
		var parseCount = engine.ParseCount;
		Assert.Contains(first.Edges, edge => edge.Source == "src/view.ts" && edge.Target == "first/types.ts");

		File.WriteAllText(
			config,
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\",\"rootDirs\":[\"src\",\"second\"]}}");
		File.SetLastWriteTimeUtc(config, DateTime.UtcNow.AddSeconds(2));
		var second = await engine.IndexAsync(
			fixture.Path,
			manifest,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(parseCount, engine.ParseCount);
		Assert.False(second.Metrics.ResolutionCacheHit);
		Assert.Contains(second.Edges, edge => edge.Source == "src/view.ts" && edge.Target == "second/types.ts");
		Assert.DoesNotContain(second.Edges, edge => edge.Source == "src/view.ts" && edge.Target == "first/types.ts");
	}

	[Fact]
	public async Task WindowsSeparatorsAndFileSystemCaseResolveRootDirs()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("Windows path identity is required for this assertion.");
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\",\"rootDirs\":[\".\\\\SRC\",\".\\\\GENERATED\"]}}");
		var source = fixture.CreateFile("src/view.ts", "import { value } from './types'; export { value };\n");
		var target = fixture.CreateFile("generated/types.ts", "export const value = 1;\n");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, source, target],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "src/view.ts" && candidate.Reference == "./types");
		Assert.Equal(ResolutionStatus.Resolved, edge.Status);
		Assert.Equal("generated/types.ts", edge.Target);
	}

	[Fact]
	public async Task UnsafeRootDirsAreIgnoredWithoutInvalidatingTheScope()
	{
		using var fixture = new TemporaryDirectory();
		using var outside = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\",\"rootDirs\":[\"src\",\"../outside\"]}}");
		var source = fixture.CreateFile("src/view.ts", "import { value } from './types'; export { value };\n");
		var target = fixture.CreateFile("src/types.ts", "export const value = 1;\n");

		var configuration = await new FileDependencyConfigurationProvider().ReadAsync(
			fixture.Path,
			[config, source, target],
			TestContext.Current.CancellationToken);

		var scope = Assert.Single(configuration.Scopes, candidate => candidate.HasConfiguration);
		Assert.Equal(DependencyConfigurationState.Valid, scope.ConfigurationState);
		Assert.Equal([Path.Combine(fixture.Path, "src")], scope.TypeScriptRootDirectories);
		var diagnostic = Assert.Single(configuration.ConfigurationDiagnostics);
		Assert.Equal(DependencyConfigurationState.UnsupportedSemantics, diagnostic.State);
		Assert.Equal(FileDependencyConfigurationProvider.TypeScriptRootDirectoriesOutsideRootReason, diagnostic.Reason);
	}

	[Fact]
	public async Task RootDirsUseInheritedOriginModuleSuffixesAndDirectoryIndexes()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"app/tsconfig.json",
			"{\"extends\":\"../configs/base.json\"}");
		fixture.CreateFile(
			"configs/base.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\",\"rootDirs\":[\"../app/src\",\"../generated\"],\"moduleSuffixes\":[\".native\",\"\"]}}");
		var source = fixture.CreateFile("app/src/view.ts", "import { value } from './types'; export { value };\n");
		var target = fixture.CreateFile("generated/types/index.native.ts", "export const value = 1;\n");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, source, target],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "app/src/view.ts" && candidate.Reference == "./types");
		Assert.Equal(ResolutionStatus.Resolved, edge.Status);
		Assert.Equal("generated/types/index.native.ts", edge.Target);
	}

	[Fact]
	public async Task RootDirectorySymlinkOutsideTheProjectIsIgnored()
	{
		using var fixture = new TemporaryDirectory();
		using var outside = new TemporaryDirectory();
		var link = Path.Combine(fixture.Path, "linked");
		try
		{
			Directory.CreateSymbolicLink(link, outside.Path);
		}
		catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or NotSupportedException)
		{
			Assert.Skip("Directory symbolic links are unavailable on this host.");
		}
		var config = fixture.CreateFile(
			"tsconfig.json",
			"{\"compilerOptions\":{\"rootDirs\":[\"src\",\"linked\"]}}");
		var source = fixture.CreateFile("src/view.ts", "export const value = 1;\n");

		var configuration = await new FileDependencyConfigurationProvider().ReadAsync(
			fixture.Path,
			[config, source],
			TestContext.Current.CancellationToken);

		var scope = Assert.Single(configuration.Scopes, candidate => candidate.HasConfiguration);
		Assert.Equal(DependencyConfigurationState.Valid, scope.ConfigurationState);
		Assert.Equal([Path.Combine(fixture.Path, "src")], scope.TypeScriptRootDirectories);
		Assert.Equal(
			FileDependencyConfigurationProvider.TypeScriptRootDirectoriesOutsideRootReason,
			Assert.Single(configuration.ConfigurationDiagnostics).Reason);
	}

	[Fact]
	public async Task NonStringRootDirectoryMakesConfigurationUnsupported()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"tsconfig.json",
			"{\"compilerOptions\":{\"rootDirs\":[\"src\",42]}}");

		var configuration = await new FileDependencyConfigurationProvider().ReadAsync(
			fixture.Path,
			[config],
			TestContext.Current.CancellationToken);

		var diagnostic = Assert.Single(configuration.ConfigurationDiagnostics);
		Assert.Equal(DependencyConfigurationState.UnsupportedSemantics, diagnostic.State);
		Assert.Equal("tsconfig compilerOptions.rootDirs must be an array of strings", diagnostic.Reason);
	}

	private static DependencyFactsEngine CreateEngine() => new(
		new TreeSitterDependencyFactExtractor(),
		new FileDependencyConfigurationProvider());
}
