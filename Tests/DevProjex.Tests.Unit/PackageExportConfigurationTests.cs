using DevProjex.Application.Dependencies;

namespace DevProjex.Tests.Unit;

public sealed class PackageExportConfigurationTests
{
	private static Dictionary<string, string> Documents() => new(StringComparer.Ordinal)
	{
		["package.json"] = """{"exports":{"./bun":{"types":"./dist/types/adapter/bun/index.d.ts","import":"./dist/adapter/bun/index.js"}}}""",
		["tsconfig.json"] = """{"files":[],"references":[{"path":"./tsconfig.build.json"}]}""",
		["tsconfig.build.json"] = """{"extends":"./tsconfig.base.json","compilerOptions":{"rootDir":"./src/","outDir":"./dist/types/"},"include":["src/**/*.ts","src/**/*.mts"],"exclude":["src/**/*.test.ts","src/**/*.test.tsx"]}""",
		["tsconfig.base.json"] = """{/* inherited options do not override an explicit output */ "compilerOptions":{"outDir":"${configDir}/dist"},}"""
	};

	private static HashSet<string> Files(Dictionary<string, string> documents) => documents.Keys.Concat(
		["src/adapter/bun/index.ts", "src/adapter/bun/index.test.ts", "src/other.mts"]).ToHashSet(StringComparer.Ordinal);

	private static void AssertBun(Dictionary<string, string> documents, HashSet<string> files) =>
		Assert.Equal("src/adapter/bun/index.ts", Assert.Single(PackageExportMap.Find(documents, files)).Target);

	[Fact]
	public void ReferencedBuildWithSafeInheritanceAndOwnMembershipProvesTheTypeExport()
	{
		var documents = Documents();
		var files = Files(documents);
		AssertBun(documents, files);
		Assert.Equal(PackageExportMap.Find(documents, files), PackageExportMap.Find(documents.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value), files));
		files.Remove("tsconfig.build.json");
		Assert.Empty(PackageExportMap.Find(documents, files));
	}

	[Theory]
	[InlineData("outFile", "\"out.js\"")]
	[InlineData("declarationDir", "\"types\"")]
	[InlineData("noEmit", "true")]
	[InlineData("noEmit", "null")]
	public void UnsafeInheritedEmissionOptionsVetoTheCandidate(string option, string value)
	{
		var documents = Documents(); var files = Files(documents); AssertBun(documents, files);
		documents["tsconfig.base.json"] = "{\"compilerOptions\":{\"" + option + "\":" + value + "}}";
		Assert.Empty(PackageExportMap.Find(documents, files));
	}

	[Theory]
	[InlineData("\"package/config\"")]
	[InlineData("[\"./tsconfig.base.json\"]")]
	[InlineData("\"../outside.json\"")]
	[InlineData("\"./missing.json\"")]
	public void UnreadableOrUnsupportedInheritanceCannotProveAnExport(string parent)
	{
		var documents = Documents(); var files = Files(documents); AssertBun(documents, files);
		documents["tsconfig.build.json"] = documents["tsconfig.build.json"].Replace("\"./tsconfig.base.json\"", parent, StringComparison.Ordinal);
		Assert.Empty(PackageExportMap.Find(documents, files));
	}

	[Theory]
	[InlineData("src/**", "src/**/*.test.ts", true)]
	[InlineData("src/adapter", "src/**/*.test.ts", true)]
	[InlineData(".", "src/**/*.test.ts", true)]
	[InlineData("src/**/index.?s", "src/**/*.test.ts", true)]
	[InlineData("src\\\\**\\\\*.ts", "src/**/*.test.ts", true)]
	[InlineData("other/**", "src/**/*.test.ts", false)]
	[InlineData("src/**", "src/adapter/**", false)]
	[InlineData("src/[ab]*/**", "", false)]
	public void OwnPatternsDetermineMembership(string include, string exclude, bool expected)
	{
		var documents = Documents(); var files = Files(documents); AssertBun(documents, files);
		documents["tsconfig.build.json"] = JsonSerializer.Serialize(new
		{
			extends = "./tsconfig.base.json",
			compilerOptions = new { rootDir = "src", outDir = "dist/types" },
			include = new[] { include },
			exclude = exclude.Length == 0 ? Array.Empty<string>() : new[] { exclude }
		});
		Assert.Equal(expected, PackageExportMap.Find(documents, files).Count > 0);
	}

	[Fact]
	public void CyclesConflictingOutputsAndExistingOutputsAreNotGuessed()
	{
		var documents = Documents(); var files = Files(documents); AssertBun(documents, files);
		documents["tsconfig.base.json"] = """{"extends":"./tsconfig.build.json"}""";
		Assert.Empty(PackageExportMap.Find(documents, files));
		documents = Documents(); files = Files(documents);
		documents["tsconfig.build.json"] = documents["tsconfig.build.json"].Replace("\"src/**/*.ts\"", "\"src/**/*.ts\",\"src/**/*.tsx\"", StringComparison.Ordinal);
		files.Add("src/adapter/bun/index.tsx");
		Assert.Empty(PackageExportMap.Find(documents, files));
		files.Remove("src/adapter/bun/index.tsx");
		files.Add("dist/types/adapter/bun/index.d.ts");
		Assert.DoesNotContain(PackageExportMap.Find(documents, files), item => item.Target == "src/adapter/bun/index.ts");
	}
}
