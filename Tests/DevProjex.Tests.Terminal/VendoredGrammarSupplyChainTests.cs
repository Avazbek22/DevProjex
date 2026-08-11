using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using TreeSitter;

namespace DevProjex.Tests.Terminal;

public sealed class VendoredGrammarSupplyChainTests
{
	private static readonly IReadOnlyDictionary<string, GrammarContract> ExpectedGrammars =
		new Dictionary<string, GrammarContract>(StringComparer.Ordinal)
		{
			["tree-sitter-kotlin"] = new(
				"tree_sitter_kotlin",
				14,
				"https://github.com/tree-sitter-grammars/tree-sitter-kotlin",
				"v1.1.0",
				"77dd60ea0a9003ce062c9728a513ffe1aaff8c82",
				"https://codeload.github.com/tree-sitter-grammars/tree-sitter-kotlin/zip/refs/tags/v1.1.0",
				"73c45375934dcb1a764a7c1c3b03059ec78d0755e0bf4874ddb06b4df4fbd091",
				["src/parser.c", "src/scanner.c"],
				["src"]),
			["tree-sitter-toml"] = new(
				"tree_sitter_toml",
				13,
				"https://github.com/tree-sitter/tree-sitter-toml",
				null,
				"342d9be207c2dba869b9967124c679b5e6fd0ebe",
				"https://codeload.github.com/tree-sitter/tree-sitter-toml/zip/342d9be207c2dba869b9967124c679b5e6fd0ebe",
				"ba4055ea8bff2c17d4eb5454ce965a52d073f246ce0a7d3d915d57c4746d676a",
				["src/parser.c", "src/scanner.c"],
				["src"]),
			["tree-sitter-xml"] = new(
				"tree_sitter_xml",
				14,
				"https://github.com/tree-sitter-grammars/tree-sitter-xml",
				"v0.7.0",
				"4b64dd3a03ec002258d6268d712fd93716d6ab57",
				"https://codeload.github.com/tree-sitter-grammars/tree-sitter-xml/zip/refs/tags/v0.7.0",
				"991d121cd1217b488aa1f831a1ed07041c126bf1bc4c9069a025c704014c6fb2",
				["xml/src/parser.c", "xml/src/scanner.c"],
				["xml/src"]),
			["tree-sitter-yaml"] = new(
				"tree_sitter_yaml",
				14,
				"https://github.com/tree-sitter-grammars/tree-sitter-yaml",
				"v0.7.2",
				"7708026449bed86239b1cd5bce6e3c34dbca6415",
				"https://codeload.github.com/tree-sitter-grammars/tree-sitter-yaml/zip/refs/tags/v0.7.2",
				"f995e22d02efba52b06d527376f51cfd31ec3029c8f70a8e1132281ae0c1e7fd",
				["src/parser.c", "src/scanner.c"],
				["src"])
		};

	private static readonly IReadOnlyDictionary<string, RidContract> ExpectedRids =
		new Dictionary<string, RidContract>(StringComparer.Ordinal)
		{
			["win-x64"] = new(string.Empty, ".dll", "pe", "x64", 0x8664),
			["win-arm64"] = new(string.Empty, ".dll", "pe", "arm64", 0xAA64),
			["linux-x64"] = new("lib", ".so", "elf", "x64", 62),
			["linux-arm64"] = new("lib", ".so", "elf", "arm64", 183),
			["osx-x64"] = new("lib", ".dylib", "macho", "x64", 7),
			["osx-arm64"] = new("lib", ".dylib", "macho", "arm64", 12)
		};

	[Fact]
	public void VendoredBinariesMatchThePinnedSupplyChainManifest()
	{
		var rootPath = PublishedApplicationLocator.FindRepositoryRoot();
		var vendoredRoot = Path.Combine(rootPath, "Infrastructure", "Grammars", "vendored");
		using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(
			vendoredRoot,
			"vendored-grammars.lock.json")));
		var root = manifest.RootElement;
		Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
		var grammars = root.GetProperty("grammars")
			.EnumerateArray()
			.ToDictionary(
				static grammar => grammar.GetProperty("name").GetString()!,
				StringComparer.Ordinal);

		Assert.Equal(
			ExpectedGrammars.Keys.Order(StringComparer.Ordinal),
			grammars.Keys.Order(StringComparer.Ordinal));

		foreach (var (name, expectedGrammar) in ExpectedGrammars)
		{
			var grammar = grammars[name];
			Assert.Equal(expectedGrammar.Export, grammar.GetProperty("export").GetString());
			var abiVersion = grammar.GetProperty("abiVersion").GetInt32();
			Assert.Equal(expectedGrammar.AbiVersion, abiVersion);
			Assert.InRange(abiVersion, 13, 15);

			var source = grammar.GetProperty("source");
			Assert.Equal(expectedGrammar.Repository, source.GetProperty("repository").GetString());
			if (expectedGrammar.Tag is null)
				Assert.Equal(JsonValueKind.Null, source.GetProperty("tag").ValueKind);
			else
				Assert.Equal(expectedGrammar.Tag, source.GetProperty("tag").GetString());
			Assert.Equal(expectedGrammar.Commit, source.GetProperty("commit").GetString());
			Assert.Equal(expectedGrammar.ArchiveUrl, source.GetProperty("archiveUrl").GetString());
			Assert.Equal(expectedGrammar.ArchiveSha256, source.GetProperty("archiveSha256").GetString());

			var build = grammar.GetProperty("build");
			Assert.Equal("cc", build.GetProperty("compiler").GetString());
			Assert.Equal(
				expectedGrammar.SourceFiles,
				build.GetProperty("sourceFiles")
					.EnumerateArray()
					.Select(static value => value.GetString()!)
					.ToArray());
			var includeDirectories = build.TryGetProperty("includeDirectories", out var includes)
				? includes.EnumerateArray().Select(static value => value.GetString()!).ToArray()
				: ["src"];
			Assert.Equal(expectedGrammar.IncludeDirectories, includeDirectories);

			var toolchain = grammar.GetProperty("toolchain");
			Assert.Equal("zig", toolchain.GetProperty("name").GetString());
			Assert.Equal("0.16.0", toolchain.GetProperty("version").GetString());
			Assert.Equal(
				"https://ziglang.org/download/0.16.0/zig-x86_64-windows-0.16.0.zip",
				toolchain.GetProperty("archiveUrl").GetString());
			Assert.Equal(
				"68659eb5f1e4eb1437a722f1dd889c5a322c9954607f5edcf337bc3684a75a7e",
				toolchain.GetProperty("archiveSha256").GetString());
			var flags = toolchain.GetProperty("compilerFlags")
				.EnumerateArray()
				.Select(static value => value.GetString()!)
				.ToArray();
			Assert.Equal(
				["-shared", "-O2", "-DNDEBUG", "-fno-ident", "-fvisibility=hidden", "-g0", "-s"],
				flags);
			if (name.Equals("tree-sitter-toml", StringComparison.Ordinal))
			{
				var patch = Assert.Single(grammar.GetProperty("sourcePatches").EnumerateArray());
				Assert.Equal("src/parser.c", patch.GetProperty("path").GetString());
				Assert.Contains(
					"visibility(\"default\")",
					patch.GetProperty("newText").GetString(),
					StringComparison.Ordinal);
			}
			else if (name.Equals("tree-sitter-yaml", StringComparison.Ordinal))
			{
				var patch = Assert.Single(grammar.GetProperty("sourcePatches").EnumerateArray());
				Assert.Equal("src/scanner.c", patch.GetProperty("path").GetString());
				Assert.Contains(
					"size + 2 * sizeof(int16_t) <= TREE_SITTER_SERIALIZATION_BUFFER_SIZE",
					patch.GetProperty("newText").GetString(),
					StringComparison.Ordinal);
			}
			else
			{
				Assert.False(grammar.TryGetProperty("sourcePatches", out _));
			}

			var binaries = grammar.GetProperty("binaries")
				.EnumerateArray()
				.ToDictionary(
					static binary => binary.GetProperty("rid").GetString()!,
					StringComparer.Ordinal);
			Assert.Equal(
				ExpectedRids.Keys.Order(StringComparer.Ordinal),
				binaries.Keys.Order(StringComparer.Ordinal));

			foreach (var (rid, expectedRid) in ExpectedRids)
			{
				var binary = binaries[rid];
				var expectedPath = $"{rid}/{expectedRid.Prefix}{name}{expectedRid.Extension}";
				Assert.Equal(expectedPath, binary.GetProperty("path").GetString());
				Assert.Equal(expectedRid.Format, binary.GetProperty("format").GetString());
				Assert.Equal(expectedRid.ArchitectureName, binary.GetProperty("architecture").GetString());

				var fullPath = Path.Combine(
					vendoredRoot,
					expectedPath.Replace('/', Path.DirectorySeparatorChar));
				var bytes = File.ReadAllBytes(fullPath);
				Assert.Equal(binary.GetProperty("size").GetInt64(), bytes.LongLength);
				Assert.Equal(
					binary.GetProperty("sha256").GetString(),
					Convert.ToHexStringLower(SHA256.HashData(bytes)));
				AssertBinaryShape(bytes, expectedRid.Format, expectedRid.ArchitectureId);
				Assert.True(
					bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(expectedGrammar.Export)) >= 0,
					$"{name}/{rid}: export name is absent from the native symbol table.");
				Assert.True(
					bytes.AsSpan().IndexOf(".debug_"u8) < 0,
					$"{name}/{rid}: stripped grammar still contains debug sections.");
			}
		}
	}

	[Fact]
	public void EveryPackageGrammarExistsForEveryShippingRid()
	{
		var rootPath = PublishedApplicationLocator.FindRepositoryRoot();
		var project = XDocument.Load(Path.Combine(rootPath, "Infrastructure", "Infrastructure.csproj"));
		var packageGrammars = project.Descendants("DevProjexGrammar")
			.Select(static element => element.Attribute("Include")?.Value)
			.Where(static value => !string.IsNullOrWhiteSpace(value))
			.Cast<string>()
			.ToArray();
		var packageVersion = XDocument.Load(Path.Combine(rootPath, "Directory.Packages.props"))
			.Descendants("TreeSitterDotNetVersion")
			.Single()
			.Value;
		var packageRoot = ResolveNuGetPackageRoot(packageVersion);
		var missing = new List<string>();

		foreach (var grammarName in packageGrammars)
		{
			foreach (var (rid, expectedRid) in ExpectedRids)
			{
				var fileName = $"{expectedRid.Prefix}{grammarName}{expectedRid.Extension}";
				var path = Path.Combine(packageRoot, "runtimes", rid, "native", fileName);
				if (!File.Exists(path))
					missing.Add($"{grammarName}/{rid}: {path}");
			}
		}

		Assert.True(
			missing.Count == 0,
			"A package grammar is not available on every shipping RID. Move partial grammars " +
			$"to the vendored pipeline:{Environment.NewLine}{string.Join(Environment.NewLine, missing)}");
	}

	[Fact]
	public void VendoredTomlProducesTheSameAstAsThePinnedPackageBinaryOnWindows()
	{
		if (!OperatingSystem.IsWindows())
			return;

		var rid = RuntimeInformation.ProcessArchitecture switch
		{
			Architecture.X64 => "win-x64",
			Architecture.Arm64 => "win-arm64",
			var architecture => throw new PlatformNotSupportedException(
				$"No shipped TOML parity binary for Windows {architecture}.")
		};
		var rootPath = PublishedApplicationLocator.FindRepositoryRoot();
		var version = XDocument.Load(Path.Combine(rootPath, "Directory.Packages.props"))
			.Descendants("TreeSitterDotNetVersion")
			.Single()
			.Value;
		var packagePath = Path.Combine(
			ResolveNuGetPackageRoot(version),
			"runtimes",
			rid,
			"native",
			"tree-sitter-toml.dll");
		var vendoredPath = Path.Combine(
			rootPath,
			"Infrastructure",
			"Grammars",
			"vendored",
			rid,
			"tree-sitter-toml.dll");
		const string source = """"
			title = "AST parity"
			ports = [8000, 8001]

			[database]
			enabled = true
			multiline = """
			retained
			value
			"""
			"""";

		using var packageLanguage = new Language(packagePath, "tree_sitter_toml");
		using var vendoredLanguage = new Language(vendoredPath, "tree_sitter_toml");
		using var packageParser = new Parser(packageLanguage);
		using var vendoredParser = new Parser(vendoredLanguage);
		using var packageTree = packageParser.Parse(source);
		using var vendoredTree = vendoredParser.Parse(source);

		Assert.NotNull(packageTree);
		Assert.NotNull(vendoredTree);
		Assert.False(packageTree.RootNode.HasError);
		Assert.False(vendoredTree.RootNode.HasError);
		Assert.Equal(packageTree.RootNode.ToString(), vendoredTree.RootNode.ToString());
	}

	[Fact]
	public void EveryVendoredGrammarLoadsAndParsesItsDeliveryFixtureOnWindows()
	{
		if (!OperatingSystem.IsWindows())
			return;

		var rid = RuntimeInformation.ProcessArchitecture switch
		{
			Architecture.X64 => "win-x64",
			Architecture.Arm64 => "win-arm64",
			var architecture => throw new PlatformNotSupportedException(
				$"No shipped vendored grammar binary for Windows {architecture}.")
		};
		var rootPath = PublishedApplicationLocator.FindRepositoryRoot();
		var fixtures = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["tree-sitter-kotlin"] = "fun main() { println(\"delivery\") }",
			["tree-sitter-toml"] = "title = \"delivery\"\n[probe]\nenabled = true\n",
			["tree-sitter-xml"] = "<?xml version=\"1.0\"?><root><!-- delivery --><![CDATA[kept]]></root>",
			["tree-sitter-yaml"] = "---\nprobe: &probe\n  enabled: true # delivery\ncopy: *probe\n"
		};

		foreach (var (name, expected) in ExpectedGrammars)
		{
			var fileName = $"{name}.dll";
			var path = Path.Combine(
				rootPath,
				"Infrastructure",
				"Grammars",
				"vendored",
				rid,
				fileName);
			using var language = new Language(path, expected.Export);
			using var parser = new Parser(language);
			using var tree = parser.Parse(fixtures[name]);

			Assert.NotNull(tree);
			Assert.False(tree.RootNode.HasError, $"{name} rejected its delivery fixture.");
		}
	}

	[Fact]
	public void VendoredGrammarsUseTheSameDeliveryAndExclusionContractAsPackageGrammars()
	{
		var rootPath = PublishedApplicationLocator.FindRepositoryRoot();
		var project = XDocument.Load(Path.Combine(rootPath, "Infrastructure", "Infrastructure.csproj"));
		var vendoredNames = project.Descendants("DevProjexVendoredGrammar")
			.Select(static element => element.Attribute("Include")?.Value)
			.Where(static value => value is not null)
			.Cast<string>()
			.Order(StringComparer.Ordinal)
			.ToArray();
		Assert.Equal(ExpectedGrammars.Keys.Order(StringComparer.Ordinal), vendoredNames);

		var curated = Assert.Single(
			project.Descendants("CuratedGrammarFile"),
			static element => (element.Attribute("Include")?.Value ?? string.Empty)
				.Contains("@(DevProjexVendoredGrammar", StringComparison.Ordinal));
		Assert.Contains("_VendoredGrammarSourceDir", curated.Attribute("Include")?.Value, StringComparison.Ordinal);
		Assert.Contains(
			"DevProjexExcludeGrammars",
			curated.Parent?.Attribute("Condition")?.Value ?? string.Empty,
			StringComparison.Ordinal);

		var attributes = File.ReadAllText(Path.Combine(rootPath, ".gitattributes"));
		Assert.Contains("Infrastructure/Grammars/vendored/**/*.dll binary", attributes, StringComparison.Ordinal);
		Assert.Contains("Infrastructure/Grammars/vendored/**/*.so binary", attributes, StringComparison.Ordinal);
		Assert.Contains("Infrastructure/Grammars/vendored/**/*.dylib binary", attributes, StringComparison.Ordinal);

		var compatibilityWrapper = File.ReadAllText(Path.Combine(rootPath, "tools", "grammars", "build-kotlin.ps1"));
		Assert.Contains("build-vendored-grammars.ps1", compatibilityWrapper, StringComparison.Ordinal);
		var workflow = File.ReadAllText(Path.Combine(rootPath, ".github", "workflows", "grammar-delivery.yml"));
		Assert.Contains(
			"./tools/grammars/build-vendored-grammars.ps1 -VerifyOnly",
			workflow,
			StringComparison.Ordinal);
		foreach (var rid in ExpectedRids.Keys)
			Assert.Contains($"rid: {rid}", workflow, StringComparison.Ordinal);
	}

	private static string ResolveNuGetPackageRoot(string version)
	{
		var configuredRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
		var packagesRoot = string.IsNullOrWhiteSpace(configuredRoot)
			? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages")
			: configuredRoot;
		return Path.Combine(packagesRoot, "treesitter.dotnet", version);
	}

	private static void AssertBinaryShape(ReadOnlySpan<byte> bytes, string format, ushort architecture)
	{
		switch (format)
		{
			case "pe":
				Assert.True(bytes.StartsWith("MZ"u8));
				var peOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(0x3C, 4));
				Assert.True(bytes.Slice(peOffset, 4).SequenceEqual("PE\0\0"u8));
				Assert.Equal(architecture, BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(peOffset + 4, 2)));
				break;
			case "elf":
				Assert.True(bytes[..4].SequenceEqual(new byte[] { 0x7F, 0x45, 0x4C, 0x46 }));
				Assert.Equal(2, bytes[4]);
				Assert.Equal(architecture, BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(18, 2)));
				break;
			case "macho":
				Assert.True(bytes[..4].SequenceEqual(new byte[] { 0xCF, 0xFA, 0xED, 0xFE }));
				var cpuType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4));
				Assert.Equal(0x01000000u | architecture, cpuType);
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown native binary format.");
		}
	}

	private sealed record GrammarContract(
		string Export,
		int AbiVersion,
		string Repository,
		string? Tag,
		string Commit,
		string ArchiveUrl,
		string ArchiveSha256,
		IReadOnlyList<string> SourceFiles,
		IReadOnlyList<string> IncludeDirectories);

	private sealed record RidContract(
		string Prefix,
		string Extension,
		string Format,
		string ArchitectureName,
		ushort ArchitectureId);
}
