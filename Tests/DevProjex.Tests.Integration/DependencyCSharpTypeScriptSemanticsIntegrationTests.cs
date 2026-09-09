using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyCSharpTypeScriptSemanticsIntegrationTests
{
	[Fact]
	public async Task New010_DynamicImportAttributesPreserveEveryImportFact()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var data = fixture.CreateFile("data.ts", "export default 1;");
		var ordinary = fixture.CreateFile("ordinary.ts", "export default 2;");
		var source = fixture.CreateFile(
			"main.ts",
			"import('./data.js', { with: { type: 'json' } });\nimport './ordinary.js';");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, data, ordinary, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var facts = result.Files.Single(file => file.Path == "main.ts");
		Assert.Equal(DependencyFileStatus.Supported, facts.Status);
		Assert.Contains(facts.Imports, import =>
			import.Specifier == "./data.js" && import.ImportKind == ModuleImportKind.DynamicImport);
		Assert.Contains(facts.Imports, import => import.Specifier == "./ordinary.js");
		Assert.Contains(result.Edges, edge => edge.Source == "main.ts" && edge.Target == "data.ts");
		Assert.Contains(result.Edges, edge => edge.Source == "main.ts" && edge.Target == "ordinary.ts");
	}

	private static DependencyFactsEngine CreateEngine() => new(
		new TreeSitterDependencyFactExtractor(),
		new FileDependencyConfigurationProvider());
}
