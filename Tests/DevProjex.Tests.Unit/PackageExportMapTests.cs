using DevProjex.Application.Dependencies;

namespace DevProjex.Tests.Unit;

public sealed class PackageExportMapTests
{
	[Theory]
	[InlineData("package.json")]
	[InlineData("jsr.json")]
	[InlineData("deno.json")]
	public void ExportConditionsWildcardsAndFallbacksNameTheirSource(string manifest)
	{
		var documents = new Dictionary<string, string>
		{
			[manifest] = """{"exports":{"./bun":{"import":"./src/bun.ts","require":[null,"./src/bun.ts"]},"./*":"./src/*.ts"}}"""
		};
		var exports = PackageExportMap.Find(documents, new HashSet<string> { manifest, "src/bun.ts", "src/other.ts" });
		Assert.Contains(exports, item => item.Manifest == manifest && item.Key == "./bun" && item.Target == "src/bun.ts");
		Assert.Contains(exports, item => item.Manifest == manifest && item.Key == "./other" && item.Target == "src/other.ts");
		Assert.Equal(exports.Count, exports.Distinct().Count());
	}

	[Fact]
	public void HiddenManifestAndOrdinarySourceHaveNoExports()
	{
		var documents = new Dictionary<string, string> { ["package.json"] = """{"exports":"./src/index.ts"}""" };
		Assert.Empty(PackageExportMap.Find(documents, new HashSet<string> { "src/index.ts" }));
		Assert.Empty(PackageExportMap.Find(documents, new HashSet<string> { "package.json", "src/other.ts" }));
	}

	[Theory]
	[InlineData(false, true)]
	[InlineData(true, false)]
	public void CompiledTargetRequiresOneSource(bool ambiguous, bool expected)
	{
		var documents = new Dictionary<string, string>
		{
			["package.json"] = """{"exports":{".":"./dist/index.js"}}""",
			["tsconfig.json"] = """{"compilerOptions":{"rootDir":"src","outDir":"dist"}}"""
		};
		var files = new HashSet<string> { "package.json", "tsconfig.json", "src/index.ts" };
		if (ambiguous) files.Add("src/index.tsx");
		Assert.Equal(expected, PackageExportMap.Find(documents, files).Count > 0);
	}

	[Theory]
	[InlineData("null")]
	[InlineData("{}")]
	[InlineData("{broken")]
	public void InvalidOrEmptyDocumentsProveNoExports(string content)
	{
		var documents = new Dictionary<string, string> { ["jsr.json"] = content };
		Assert.Empty(PackageExportMap.Find(documents, new HashSet<string> { "jsr.json", "index.ts" }));
	}
}
