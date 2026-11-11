using DevProjex.Application.Dependencies;

namespace DevProjex.Tests.Unit;

public sealed class PackageExportConfigurationBoundaryTests
{
	[Fact]
	public void ReferenceLimitAdmitsTheSixteenthButNotTheSeventeenthCandidate()
	{
		var fixture = ConfigurationFixture.Create();
		fixture.AssertExport();
		var missing = Enumerable.Range(0, 15).Select(index => $"./absent-{index}.json").ToArray();
		fixture.SetReferences(missing.Append("./emit.json"));
		fixture.AssertExport();
		Assert.Equal(16, PackageExportMap.ConfigurationDependencies("tsconfig.json", fixture.Documents["tsconfig.json"], true).Count);
		fixture.SetReferences(missing.Append("./absent-15.json").Append("./emit.json"));
		Assert.Empty(fixture.Exports());
		Assert.DoesNotContain("emit.json", PackageExportMap.ConfigurationDependencies("tsconfig.json", fixture.Documents["tsconfig.json"], true));
	}

	[Fact]
	public void ReferencedConfigurationDoesNotExpandItsOwnReferences()
	{
		var fixture = ConfigurationFixture.Create();
		fixture.AssertExport();
		fixture.Add("bridge.json", """{"references":[{"path":"./emit.json"}]}""");
		fixture.SetReferences(["./bridge.json"]);
		Assert.Empty(fixture.Exports());
		Assert.Empty(PackageExportMap.ConfigurationDependencies("bridge.json", fixture.Documents["bridge.json"], false));
	}

	[Fact]
	public void FourInheritanceLinksAreAcceptedButFiveAreNot()
	{
		var fixture = ConfigurationFixture.Create();
		fixture.AssertExport();
		fixture.SetCandidate(extends: "./parent-1.json");
		for (var index = 1; index <= 4; index++)
			fixture.Add($"parent-{index}.json", index == 4 ? "{}" : JsonSerializer.Serialize(new { extends = $"./parent-{index + 1}.json" }));
		fixture.AssertExport();
		fixture.Add("parent-4.json", """{"extends":"./parent-5.json"}""");
		fixture.Add("parent-5.json", "{}");
		Assert.Empty(fixture.Exports());
	}

	[Theory]
	[InlineData("hidden")]
	[InlineData("missing")]
	[InlineData("cycle")]
	public void AnInheritanceChainMustRemainVisiblePresentAndAcyclic(string failure)
	{
		var fixture = ConfigurationFixture.Create();
		fixture.SetCandidate(extends: "./parents/first.json");
		fixture.Add("parents/first.json", """{"extends":"./second.json"}""");
		fixture.Add("parents/second.json", """{"compilerOptions":{"noEmit":false}}""");
		fixture.AssertExport();
		if (failure == "hidden") fixture.Files.Remove("parents/second.json");
		else if (failure == "missing") fixture.Documents.Remove("parents/second.json");
		else fixture.Add("parents/second.json", """{"extends":"./first.json"}""");
		Assert.Empty(fixture.Exports());
	}

	[Theory]
	[InlineData("rootDir")]
	[InlineData("outDir")]
	public void InheritedDirectoriesCannotReplaceTheCandidatesOwnDirectories(string missing)
	{
		var fixture = ConfigurationFixture.Create();
		fixture.SetCandidate(extends: "./parent.json");
		fixture.Add("parent.json", """{"compilerOptions":{"rootDir":"Source","outDir":"Output"}}""");
		fixture.AssertExport();
		var options = ConfigurationFixture.Options();
		options.Remove(missing);
		fixture.SetCandidate(options, extends: "./parent.json");
		Assert.Empty(fixture.Exports());
	}

	[Theory]
	[InlineData("rootDir", "${configDir}/Source")]
	[InlineData("outDir", "${configDir}/Output")]
	[InlineData("rootDir", "../Source")]
	[InlineData("outDir", "/Output")]
	[InlineData("outDir", "C:/Output")]
	public void CandidateDirectoriesMustBeConcreteLocalPaths(string option, string value)
	{
		var fixture = ConfigurationFixture.Create();
		fixture.AssertExport();
		var options = ConfigurationFixture.Options();
		options[option] = value;
		fixture.SetCandidate(options);
		Assert.Empty(fixture.Exports());
	}

	[Theory]
	[InlineData("own", "noEmit", "true")]
	[InlineData("own", "noEmit", "\"false\"")]
	[InlineData("own", "noEmit", "0")]
	[InlineData("own", "outFile", "null")]
	[InlineData("own", "declarationDir", "null")]
	[InlineData("parent", "noEmit", "\"false\"")]
	[InlineData("parent", "noEmit", "0")]
	[InlineData("parent", "outFile", "null")]
	[InlineData("parent", "declarationDir", "null")]
	public void EmissionGuardsApplyToOwnAndInheritedOptions(string owner, string option, string value)
	{
		var fixture = ConfigurationFixture.Create();
		var options = ConfigurationFixture.Options();
		options["noEmit"] = false;
		fixture.SetCandidate(options, extends: "./parent.json");
		fixture.Add("parent.json", """{"compilerOptions":{"noEmit":false}}""");
		fixture.AssertExport();
		using var parsed = JsonDocument.Parse(value);
		if (owner == "own")
		{
			options[option] = parsed.RootElement.Clone();
			fixture.SetCandidate(options, extends: "./parent.json");
		}
		else fixture.Add("parent.json", JsonSerializer.Serialize(new { compilerOptions = new Dictionary<string, object?> { [option] = parsed.RootElement.Clone() } }));
		Assert.Empty(fixture.Exports());
	}

	[Fact]
	public void InheritedMembershipDoesNotSubstituteForOwnMembership()
	{
		var fixture = ConfigurationFixture.Create();
		fixture.SetCandidate(extends: "./parent.json");
		fixture.Add("parent.json", """{"include":["Source"]}""");
		fixture.AssertExport();
		fixture.SetCandidate(extends: "./parent.json", membership: new Dictionary<string, object?>());
		Assert.Empty(fixture.Exports());
		fixture.SetCandidate(extends: "./parent.json", membership: new Dictionary<string, object?> { ["files"] = Array.Empty<string>() });
		Assert.Empty(fixture.Exports());
	}

	[Fact]
	public void ExplicitFilesAndIncludeAreAUnionWhileExcludeWinsOverBoth()
	{
		var fixture = ConfigurationFixture.Create();
		fixture.AssertExport();
		fixture.Add("Source/extra.mts", "export const extra = 1;");
		fixture.Add("Source/omitted.ts", "export const omitted = 1;");
		fixture.Documents["package.json"] = """{"exports":{"./entry":"./Output/index.d.cts","./extra":"./Output/extra.d.mts","./omitted":"./Output/omitted.d.ts"}}""";
		fixture.SetCandidate(membership: new Dictionary<string, object?>
		{
			["files"] = new[] { "Source/index.cts", "Source/omitted.ts" },
			["include"] = new[] { "Source/extra.mts", "Source/omitted.ts" },
			["exclude"] = new[] { "Source/omitted.ts" }
		});
		Assert.Equal(new[] { "Source/index.cts", "Source/extra.mts" }, fixture.Exports().Select(item => item.Target));
	}

	[Theory]
	[InlineData("files", "\"Source/index.cts\"")]
	[InlineData("include", "[1]")]
	[InlineData("exclude", "[null]")]
	[InlineData("include", "[\"Source/a**b.cts\"]")]
	[InlineData("include", "[\"Source/{a,b}/**\"]")]
	public void MalformedOrAmbiguousMembershipCannotProveAnExport(string field, string json)
	{
		var fixture = ConfigurationFixture.Create();
		fixture.AssertExport();
		using var value = JsonDocument.Parse(json);
		fixture.SetCandidate(membership: new Dictionary<string, object?>
		{
			["include"] = new[] { "Source/**/*" }, [field] = value.RootElement.Clone()
		});
		Assert.Empty(fixture.Exports());
	}

	[Fact]
	public void DifferentSourcesCompetingForTheSameOutputAreRejected()
	{
		var fixture = ConfigurationFixture.Create();
		fixture.AssertExport();
		fixture.Add("Other/index.cts", "export const other = 1;");
		fixture.Add("other.json", """{"compilerOptions":{"rootDir":"Other","outDir":"Output"},"files":["Other/index.cts"]}""");
		fixture.SetReferences(["./emit.json", "./other.json"]);
		Assert.Empty(fixture.Exports());
	}

	[Fact]
	public void OverlappingCandidatesForTheSameSourceRemainUnambiguous()
	{
		var fixture = ConfigurationFixture.Create();
		fixture.AssertExport();
		fixture.Add("duplicate.json", fixture.Documents["emit.json"]);
		fixture.SetReferences(["./emit.json", "./duplicate.json", "./emit.json"]);
		fixture.AssertExport();
		fixture.Add("Other/index.cts", "export const other = 1;");
		fixture.Add("duplicate.json", """{"compilerOptions":{"rootDir":"Other","outDir":"Output"},"include":["Other"]}""");
		Assert.Empty(fixture.Exports());
	}

	[Fact]
	public void FolderReferencesResolveRelativeToTheirOwnConfigurationWithJsonComments()
	{
		var fixture = ConfigurationFixture.Create();
		fixture.AssertExport();
		fixture.Documents["package.json"] = """{"exports":{".":"./build/lib/item.d.mts"}}""";
		fixture.Documents["tsconfig.json"] = """{/* root */"references":[{"path":"./build"},],}""";
		fixture.Add("build/source/item.mts", "export const item = 1;");
		fixture.Add("build/tsconfig.json", """{/* candidate */"extends":"./base.json","compilerOptions":{"rootDir":"source","outDir":"lib",},"include":["source"],}""");
		fixture.Add("build/base.json", """{/* inherited */"compilerOptions":{"noEmit":false},}""");
		Assert.Equal("build/source/item.mts", Assert.Single(fixture.Exports()).Target);
		Assert.Equal(new[] { "build/tsconfig.json" }, PackageExportMap.ConfigurationDependencies("tsconfig.json", fixture.Documents["tsconfig.json"], true));
		fixture.SetReferences([".\\build\\tsconfig.json"]);
		Assert.Equal("build/source/item.mts", Assert.Single(fixture.Exports()).Target);
		fixture.Files.Remove("build/base.json");
		Assert.Empty(fixture.Exports());
	}

	[Theory]
	[InlineData("files", "Source\\index.cts", true)]
	[InlineData("include", "Source/**/index.c?s", true)]
	[InlineData("include", "source/**/*", false)]
	[InlineData("files", "source/index.cts", false)]
	public void MembershipNormalizesSeparatorsAndRetainsOrdinalPathIdentity(string field, string path, bool expected)
	{
		var fixture = ConfigurationFixture.Create();
		fixture.AssertExport();
		fixture.SetCandidate(membership: new Dictionary<string, object?> { [field] = new[] { path } });
		Assert.Equal(expected, fixture.Exports().Count > 0);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void NativeAbsoluteReferencesRequireAnExplicitRootAndResolveFilesOrFolders(bool folder)
	{
		var fixture = ConfigurationFixture.Create();
		fixture.AssertExport();
		fixture.Documents["package.json"] = """{"exports":{".":"./config/output/item.d.ts"}}""";
		fixture.Add("config/source/item.ts", "export const item = 1;");
		fixture.Add("config/tsconfig.json", """{"compilerOptions":{"rootDir":"source","outDir":"output"},"files":["source/item.ts"]}""");
		fixture.SetReferences(["./config"]);
		Assert.Equal("config/source/item.ts", Assert.Single(fixture.Exports()).Target);
		var projectRoot = NativeProjectRoot();
		var absolute = folder ? Path.Combine(projectRoot, "config") : Path.Combine(projectRoot, "config", "tsconfig.json");
		fixture.SetReferences([absolute]);
		Assert.Equal("config/source/item.ts", Assert.Single(fixture.Exports(projectRoot)).Target);
		Assert.Equal(new[] { "config/tsconfig.json" }, PackageExportMap.ConfigurationDependencies(
			"tsconfig.json", fixture.Documents["tsconfig.json"], true, projectRoot, includeInheritance: false));
		Assert.Empty(fixture.Exports());
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void AbsoluteReferencesCannotReachOutsideOrSiblingPrefixDirectories(bool siblingPrefix)
	{
		var fixture = ConfigurationFixture.Create();
		fixture.AssertExport();
		var projectRoot = NativeProjectRoot();
		fixture.SetReferences([Path.Combine(projectRoot, "emit.json")]);
		fixture.AssertExport(projectRoot);
		var outside = siblingPrefix ? projectRoot + "-sibling" : Path.GetFullPath(Path.Combine(projectRoot, "..", "outside"));
		fixture.SetReferences([Path.Combine(outside, "emit.json")]);
		Assert.Empty(fixture.Exports(projectRoot));
		Assert.Empty(PackageExportMap.ConfigurationDependencies("tsconfig.json", fixture.Documents["tsconfig.json"], true, projectRoot));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void AbsoluteReferencesStillRequireAVisibleReadableDocument(bool hidden)
	{
		var fixture = ConfigurationFixture.Create();
		fixture.AssertExport();
		var projectRoot = NativeProjectRoot();
		fixture.SetReferences([Path.Combine(projectRoot, "emit.json")]);
		fixture.AssertExport(projectRoot);
		if (hidden) fixture.Files.Remove("emit.json");
		else fixture.Documents.Remove("emit.json");
		Assert.Empty(fixture.Exports(projectRoot));
	}

	[Fact]
	public void AbsoluteInheritanceRemainsUnsupportedEvenInsideTheRoot()
	{
		var fixture = ConfigurationFixture.Create();
		fixture.SetCandidate(extends: "./parent.json");
		fixture.Add("parent.json", "{}");
		fixture.AssertExport();
		var projectRoot = NativeProjectRoot();
		fixture.SetCandidate(extends: Path.Combine(projectRoot, "parent.json"));
		Assert.Empty(fixture.Exports(projectRoot));
		Assert.Empty(PackageExportMap.ConfigurationDependencies("emit.json", fixture.Documents["emit.json"], false, projectRoot));
	}

	[Fact]
	public void AbsoluteConfigurationIdentityUsesThePlatformsUnambiguousCaseRules()
	{
		var fixture = ConfigurationFixture.Create();
		fixture.AssertExport();
		var projectRoot = NativeProjectRoot();
		fixture.SetReferences([Path.Combine(projectRoot, "EMIT.JSON")]);
		Assert.Equal(OperatingSystem.IsWindows(), fixture.Exports(projectRoot).Count > 0);
		fixture.Add("EMIT.JSON", "{}");
		fixture.SetReferences([Path.Combine(projectRoot, "Emit.Json")]);
		Assert.Empty(fixture.Exports(projectRoot));
		fixture.SetReferences([Path.Combine(projectRoot, "emit.json")]);
		fixture.AssertExport(projectRoot);
	}

	[Theory]
	[InlineData("Z:/configuration/emit.json")]
	[InlineData("Z:\\configuration\\emit.json")]
	public void ForeignDrivePathsNeverBecomeRelativeReferences(string path)
	{
		var fixture = ConfigurationFixture.Create();
		fixture.AssertExport();
		fixture.SetReferences([path]);
		Assert.Empty(fixture.Exports(NativeProjectRoot()));
	}

	private static string NativeProjectRoot() => Path.Combine(Path.GetTempPath(), "PackageExportConfigurationBoundary", "project");

	private sealed class ConfigurationFixture
	{
		public Dictionary<string, string> Documents { get; } = new(StringComparer.Ordinal);
		public HashSet<string> Files { get; } = new(StringComparer.Ordinal);

		public static Dictionary<string, object?> Options() => new() { ["rootDir"] = "Source", ["outDir"] = "Output" };

		public static ConfigurationFixture Create()
		{
			var fixture = new ConfigurationFixture();
			fixture.Add("package.json", """{"exports":{".":"./Output/index.d.cts"}}""");
			fixture.Add("Source/index.cts", "export const entry = 1;");
			fixture.SetReferences(["./emit.json"]);
			fixture.SetCandidate();
			return fixture;
		}

		public void Add(string path, string text)
		{
			Documents[path] = text;
			Files.Add(path);
		}

		public void SetReferences(IEnumerable<string> paths) => Add("tsconfig.json", JsonSerializer.Serialize(new
		{
			references = paths.Select(path => new { path }).ToArray()
		}));

		public void SetCandidate(Dictionary<string, object?>? options = null, string? extends = null,
			Dictionary<string, object?>? membership = null)
		{
			var document = new Dictionary<string, object?> { ["compilerOptions"] = options ?? Options() };
			if (extends is not null) document["extends"] = extends;
			foreach (var pair in membership ?? new Dictionary<string, object?> { ["include"] = new[] { "Source/**/*" } })
				document[pair.Key] = pair.Value;
			Add("emit.json", JsonSerializer.Serialize(document));
		}

		public IReadOnlyList<PackageExport> Exports(string? projectRoot = null) => PackageExportMap.Find(Documents, Files, projectRoot);

		public void AssertExport(string? projectRoot = null) => Assert.Equal("Source/index.cts", Assert.Single(Exports(projectRoot)).Target);
	}
}
