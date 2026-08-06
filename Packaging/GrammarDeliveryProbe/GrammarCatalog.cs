using System.Runtime.InteropServices;

namespace DevProjex.Packaging.GrammarDeliveryProbe;

/// <summary>
/// One curated grammar. <see cref="LibraryBaseName"/> and <see cref="ExportName"/> are data, not
/// derived: the binding maps ids by lowercasing and swapping '-' for '_', which is why C# is
/// tree-sitter-c-sharp / tree_sitter_c_sharp and cannot be produced from any display name.
/// </summary>
internal sealed record GrammarDescriptor(
	string LanguageId,
	string LibraryBaseName,
	string ExportName,
	string SampleSource,
	string SmokeQuery);

internal static class GrammarCatalog
{
	/// <summary>
	/// The smoke queries only prove that the grammar loaded and produced a usable tree.
	/// Extraction quality is the language packs' concern and is covered by their golden fixtures.
	/// </summary>
	public static IReadOnlyList<GrammarDescriptor> All { get; } =
	[
		new("csharp", "tree-sitter-c-sharp", "tree_sitter_c_sharp",
			"public sealed class Probe { public async Task<int> RunAsync(int value) { return value + 1; } }",
			"(method_declaration name: (identifier) @name)"),
		new("java", "tree-sitter-java", "tree_sitter_java",
			"public final class Probe { public String get(int id) { return null; } }",
			"(method_declaration name: (identifier) @name)"),
		new("python", "tree-sitter-python", "tree_sitter_python",
			"class Probe:\n    async def run(self, data):\n        return 1\n",
			"(function_definition name: (identifier) @name)"),
		new("javascript", "tree-sitter-javascript", "tree_sitter_javascript",
			"export function add(a, b) { return a + b; }",
			"(function_declaration name: (identifier) @name)"),
		new("typescript", "tree-sitter-typescript", "tree_sitter_typescript",
			"export function add(a: number, b?: string): number { return a; }",
			"(function_declaration name: (identifier) @name)"),
		new("tsx", "tree-sitter-tsx", "tree_sitter_tsx",
			"export function App(): JSX.Element { return <div/>; }",
			"(function_declaration name: (identifier) @name)"),
		new("go", "tree-sitter-go", "tree_sitter_go",
			"package main\nfunc Add(a int, b int) int { return a + b }\n",
			"(function_declaration name: (identifier) @name)"),
		new("rust", "tree-sitter-rust", "tree_sitter_rust",
			"impl Probe { pub fn run(&self, value: u32) -> u32 { value + 1 } }",
			"(function_item name: (identifier) @name)"),
		new("c", "tree-sitter-c", "tree_sitter_c",
			"int add(int a, int b) { return a + b; }",
			"(function_definition declarator: (function_declarator declarator: (identifier) @name))"),
		new("cpp", "tree-sitter-cpp", "tree_sitter_cpp",
			"template<class T> T add(T a, T b) { return a + b; }",
			"(function_definition declarator: (function_declarator declarator: (identifier) @name))")
	];

	/// <summary>
	/// Windows ships tree-sitter-c-sharp.dll; Linux and macOS ship libtree-sitter-c-sharp.so/.dylib.
	/// </summary>
	public static string ResolveFileName(string libraryBaseName)
	{
		if (OperatingSystem.IsWindows())
			return $"{libraryBaseName}.dll";
		return OperatingSystem.IsMacOS()
			? $"lib{libraryBaseName}.dylib"
			: $"lib{libraryBaseName}.so";
	}

	public static string ResolveRuntimeIdentifier()
	{
		var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
		var architecture = RuntimeInformation.ProcessArchitecture switch
		{
			Architecture.X64 => "x64",
			Architecture.Arm64 => "arm64",
			Architecture.X86 => "x86",
			Architecture.Arm => "arm",
			var other => other.ToString().ToLowerInvariant()
		};
		return $"{platform}-{architecture}";
	}
}
