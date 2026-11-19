using DevProjex.Application.Dependencies;

namespace DevProjex.Tests.Unit;

public sealed class PackageExportMapTests
{
	[Fact]
	public void GeneratedExplicitExportsRespectVisibilityAndDoNotDependOnInputOrder()
	{
		const int seed = 49027;
		var random = new Random(seed);
		for (var sample = 0; sample < 120; sample++)
		{
			var declarations = Enumerable.Range(0, 20).Select(index =>
				(Key: "./entry" + index, Target: "pkg/src/名前🙂" + index + ".ts", Visible: random.Next(2) == 0)).ToArray();
			var exports = declarations.ToDictionary(item => item.Key, item => "./" + item.Target[4..]);
			var json = JsonSerializer.Serialize(new { exports });
			var documents = new Dictionary<string, string> { ["pkg/package.json"] = json, ["unrelated.json"] = json };
			var visible = declarations.Where(item => item.Visible).Select(item => item.Target).Append("pkg/package.json")
				.Append("pkg/ordinary.ts").ToHashSet(StringComparer.Ordinal);
			var expected = declarations.Where(item => item.Visible).OrderBy(item => item.Key, StringComparer.Ordinal)
				.Select(item => (Manifest: "pkg/package.json", item.Key, item.Target)).ToArray();
			var actual = PackageExportMap.Find(documents, visible);
			Assert.True(expected.SequenceEqual(actual.Select(item => (item.Manifest, item.Key, item.Target))), $"seed={seed}; sample={sample}");
			Assert.Equal(actual, PackageExportMap.Find(documents.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value),
				visible.Reverse().ToHashSet(StringComparer.Ordinal)));
			Assert.Equal(actual, PackageExportMap.Find(new Dictionary<string, string>
			{
				["pkg/package.json"] = JsonSerializer.Serialize(new { exports = exports.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value) })
			}, visible));
			Assert.Equal(json, documents["pkg/package.json"]);
			Assert.Equal(declarations.Count(item => item.Visible) + 2, visible.Count);
		}
	}

	[Theory]
	[InlineData("../escape.ts")]
	[InlineData("/absolute.ts")]
	[InlineData("file:///outside.ts")]
	[InlineData("./src/**/entry.ts")]
	public void TargetsThatCannotProveOneVisibleSourceAreIgnored(string target)
	{
		var documents = new Dictionary<string, string> { ["pkg/package.json"] = JsonSerializer.Serialize(new { exports = target }) };
		Assert.Empty(PackageExportMap.Find(documents, new HashSet<string>
		{
			"pkg/package.json", "pkg/src/entry.ts", "pkg/src/deep/entry.ts", "escape.ts", "absolute.ts"
		}));
	}

	[Theory]
	[InlineData(".ts", ".js")]
	[InlineData(".mts", ".mjs")]
	[InlineData(".cts", ".cjs")]
	public void CompiledExportsRequireAVisibleConfigurationAndNoExistingOutput(string sourceExtension, string outputExtension)
	{
		var documents = new Dictionary<string, string>
		{
			["nested/package.json"] = JsonSerializer.Serialize(new { exports = "./dist/entry" + outputExtension }),
			["nested/tsconfig.json"] = """{"compilerOptions":{"rootDir":"./src/","outDir":"./dist/"}}"""
		};
		var files = new HashSet<string> { "nested/package.json", "nested/src/entry" + sourceExtension };
		Assert.Empty(PackageExportMap.Find(documents, files));
		files.Add("nested/tsconfig.json");
		Assert.Equal("nested/src/entry" + sourceExtension, Assert.Single(PackageExportMap.Find(documents, files)).Target);
		files.Add("nested/dist/entry" + outputExtension);
		Assert.All(PackageExportMap.Find(documents, files), item => Assert.Equal("nested/dist/entry" + outputExtension, item.Target));
		files.Remove("nested/package.json");
		Assert.Empty(PackageExportMap.Find(documents, files));
	}

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
	public void RootStringAndConditionalRootExportsUseTheDotKey()
	{
		foreach (var exports in new[] { "\"./index.ts\"", "{\"types\":\"./index.ts\",\"default\":\"./index.ts\"}", "[\"./missing.ts\",\"./index.ts\"]" })
		{
			var documents = new Dictionary<string, string> { ["package.json"] = "{\"exports\":" + exports + "}" };
			var result = Assert.Single(PackageExportMap.Find(documents, new HashSet<string> { "package.json", "index.ts" }));
			Assert.Equal(".", result.Key);
			Assert.Equal("index.ts", result.Target);
		}
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
	[InlineData("\"exclude\":[\"src/**\"],")]
	[InlineData("\"include\":[\"other/**\"],")]
	[InlineData("\"extends\":\"./base.json\",")]
	public void UnprovenCompilationMembershipDoesNotMapCompiledOutput(string configuration)
	{
		var documents = new Dictionary<string, string>
		{
			["package.json"] = """{"exports":"./dist/index.js"}""",
			["tsconfig.json"] = "{" + configuration + "\"compilerOptions\":{\"rootDir\":\"src\",\"outDir\":\"dist\"}}"
		};
		Assert.Empty(PackageExportMap.Find(documents, new HashSet<string> { "package.json", "tsconfig.json", "src/index.ts" }));
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
