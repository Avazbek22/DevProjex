using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace DevProjex.Tests.Terminal;

public sealed class VendoredGrammarSupplyChainTests
{
	private static readonly IReadOnlyDictionary<string, (string Path, string Format, ushort Architecture)> ExpectedBinaries =
		new Dictionary<string, (string Path, string Format, ushort Architecture)>(StringComparer.Ordinal)
		{
			["win-x64"] = ("win-x64/tree-sitter-kotlin.dll", "pe", 0x8664),
			["win-arm64"] = ("win-arm64/tree-sitter-kotlin.dll", "pe", 0xAA64),
			["linux-x64"] = ("linux-x64/libtree-sitter-kotlin.so", "elf", 62),
			["linux-arm64"] = ("linux-arm64/libtree-sitter-kotlin.so", "elf", 183),
			["osx-x64"] = ("osx-x64/libtree-sitter-kotlin.dylib", "macho", 7),
			["osx-arm64"] = ("osx-arm64/libtree-sitter-kotlin.dylib", "macho", 12)
		};

	[Fact]
	public void KotlinVendoredBinariesMatchThePinnedSupplyChainManifest()
	{
		var rootPath = PublishedApplicationLocator.FindRepositoryRoot();
		var vendoredRoot = Path.Combine(rootPath, "Infrastructure", "Grammars", "vendored");
		using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(
			vendoredRoot,
			"vendored-grammars.lock.json")));
		var root = manifest.RootElement;
		Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
		var grammar = Assert.Single(root.GetProperty("grammars").EnumerateArray());

		Assert.Equal("tree-sitter-kotlin", grammar.GetProperty("name").GetString());
		Assert.Equal("tree_sitter_kotlin", grammar.GetProperty("export").GetString());
		Assert.Equal(14, grammar.GetProperty("abiVersion").GetInt32());
		var source = grammar.GetProperty("source");
		Assert.Equal(
			"https://github.com/tree-sitter-grammars/tree-sitter-kotlin",
			source.GetProperty("repository").GetString());
		Assert.Equal("v1.1.0", source.GetProperty("tag").GetString());
		Assert.Equal("77dd60ea0a9003ce062c9728a513ffe1aaff8c82", source.GetProperty("commit").GetString());
		Assert.Equal(64, source.GetProperty("archiveSha256").GetString()!.Length);

		var toolchain = grammar.GetProperty("toolchain");
		Assert.Equal("zig", toolchain.GetProperty("name").GetString());
		Assert.Equal("0.16.0", toolchain.GetProperty("version").GetString());
		var flags = toolchain.GetProperty("compilerFlags")
			.EnumerateArray()
			.Select(static value => value.GetString())
			.ToArray();
		Assert.Contains("-O2", flags);
		Assert.Contains("-DNDEBUG", flags);
		Assert.Contains("-g0", flags);
		Assert.Contains("-s", flags);

		var binaries = grammar.GetProperty("binaries").EnumerateArray().ToArray();
		Assert.Equal(ExpectedBinaries.Count, binaries.Length);
		foreach (var binary in binaries)
		{
			var rid = binary.GetProperty("rid").GetString()!;
			var expected = ExpectedBinaries[rid];
			Assert.Equal(expected.Path, binary.GetProperty("path").GetString());
			Assert.Equal(expected.Format, binary.GetProperty("format").GetString());

			var fullPath = Path.Combine(vendoredRoot, expected.Path.Replace('/', Path.DirectorySeparatorChar));
			var bytes = File.ReadAllBytes(fullPath);
			Assert.Equal(binary.GetProperty("size").GetInt64(), bytes.LongLength);
			Assert.Equal(
				binary.GetProperty("sha256").GetString(),
				Convert.ToHexStringLower(SHA256.HashData(bytes)));
			AssertBinaryShape(bytes, expected.Format, expected.Architecture);
			Assert.True(
				bytes.AsSpan().IndexOf("tree_sitter_kotlin"u8) >= 0,
				$"{rid}: export name is absent from the native symbol table.");
			Assert.True(
				bytes.AsSpan().IndexOf(".debug_"u8) < 0,
				$"{rid}: stripped grammar still contains debug sections.");
		}
	}

	[Fact]
	public void VendoredGrammarUsesTheSameDeliveryAndExclusionContractAsPackageGrammars()
	{
		var rootPath = PublishedApplicationLocator.FindRepositoryRoot();
		var project = XDocument.Load(Path.Combine(rootPath, "Infrastructure", "Infrastructure.csproj"));
		var vendored = Assert.Single(project.Descendants("DevProjexVendoredGrammar"));
		Assert.Equal("tree-sitter-kotlin", vendored.Attribute("Include")?.Value);

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
}
