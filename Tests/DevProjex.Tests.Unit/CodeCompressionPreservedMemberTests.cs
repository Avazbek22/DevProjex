using DevProjex.Application.Compression;
using DevProjex.Infrastructure.Compression;

namespace DevProjex.Tests.Unit;

public sealed class CodeCompressionPreservedMemberTests
{
	public static IEnumerable<object[]> PreservedMemberCases() =>
		Cases.Select(static testCase => new object[] { testCase });

	[Theory]
	[MemberData(nameof(PreservedMemberCases))]
	public void DeclarativeMembersRemainByteForByteWhileAdjacentImplementationsAreCompressed(
		PreservedMemberCase testCase)
	{
		using var harness = CodeCompressionTestHarness.For(testCase.LanguageId);
		using var compressor = CodeCompressionTestHarness.CreateCompressor(harness.Pack);
		using var scope = compressor.CreateScope(Path.GetTempPath());

		var analysis = scope.Analyze(
			testCase.Path,
			testCase.Path,
			testCase.Source,
			TestContext.Current.CancellationToken);
		var result = analysis.GetResult(testCase.Source);

		Assert.Equal(CodeCompressionOutcome.Compressed, analysis.Plan.Outcome);
		Assert.All(testCase.Preserved, fragment =>
			Assert.Contains(fragment, result.Text, StringComparison.Ordinal));
		Assert.All(testCase.Removed, marker =>
			Assert.DoesNotContain(marker, result.Text, StringComparison.Ordinal));
		Assert.True(result.Text.Length < testCase.Source.Length);
		Assert.True(
			CountParseDefects(harness, result.Text) <= CountParseDefects(harness, testCase.Source),
			$"{testCase.LanguageId}: compression introduced a parse defect");
	}

	[Theory]
	[MemberData(nameof(PreservedMemberCases))]
	public void PreserveQueryCompilesAndCapturesARealDeclarativeMember(PreservedMemberCase testCase)
	{
		using var harness = CodeCompressionTestHarness.For(testCase.LanguageId);
		Assert.NotNull(harness.Preserves);
		using var tree = harness.Parser.Parse(testCase.Source)!;
		using var cursor = harness.Preserves!.Execute(tree.RootNode);

		var captures = cursor.Captures
			.Where(static capture => capture.Name.Equals("preserve", StringComparison.Ordinal))
			.Select(static capture => capture.Node.Text)
			.ToArray();

		Assert.NotEmpty(captures);
		Assert.Contains(captures, capture =>
			testCase.Preserved.Any(fragment => capture.Contains(fragment, StringComparison.Ordinal)));
	}

	[Fact]
	public void PreservedObjectPropertyInsideFunctionDoesNotPreventOuterFunctionCompression()
	{
		const string source = """
			export function build() {
			    const settings = {
			        normalize: value => {
			            const nested_property_marker = value + 1;
			            return nested_property_marker;
			        }
			    };
			    const outer_function_marker = settings.normalize(41);
			    return outer_function_marker;
			}
			""";

		var (plan, text) = Compress("settings.js", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.DoesNotContain("nested_property_marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("outer_function_marker", text, StringComparison.Ordinal);
	}

	[Fact]
	public void PythonNonPropertyDecoratorDoesNotProtectFunctionBody()
	{
		const string source = """
			class Service:
			    @route
			    def handle(self, value):
			        ordinary_decorator_marker = value + 1
			        return ordinary_decorator_marker
			""";

		var (plan, text) = Compress("service.py", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.DoesNotContain("ordinary_decorator_marker", text, StringComparison.Ordinal);
	}

	[Fact]
	public void PreserveQueryMatchLimitExceededRejectsTheWholeFile()
	{
		const string source = """
			public sealed class Settings
			{
			    private int _first;
			    private int _second;
			    private int _third;
			    public int Calculate(int value)
			    {
			        var implementation_marker = value + _first + _second + _third;
			        return implementation_marker;
			    }
			}
			""";
		using var harness = CodeCompressionTestHarness.For("csharp");
		using var compressor = new TreeSitterCodeCompressor(
			CodeCompressionTestHarness.CreateLocator(),
			[harness.Pack],
			queryMatchLimit: 1);
		using var scope = compressor.CreateScope(Path.GetTempPath());

		var analysis = scope.Analyze(
			"Settings.cs",
			"Settings.cs",
			source,
			TestContext.Current.CancellationToken);

		Assert.Equal(CodeCompressionOutcome.UnchangedGateRejected, analysis.Plan.Outcome);
		Assert.Empty(analysis.Plan.Edits);
		Assert.Equal(source, analysis.GetResult(source).Text);
	}

	[Fact]
	public void CSharpExpressionBodiesRemainByteForByteWhileAdjacentBlockBodyIsCompressed()
	{
		const string source = """
			public sealed class Resource
			{
			    private readonly System.Func<int, int> _normalize = value => value + 1;
			    public bool IsSupported(string path) => path.Length > 0;
			    public void Dispose() => System.Console.WriteLine("implementation");
			    public void Reset()
			    {
			        System.Console.WriteLine("block implementation");
			    }
			}
			""";

		var (plan, text) = Compress("Resource.cs", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("private readonly System.Func<int, int> _normalize = value => value + 1;", text, StringComparison.Ordinal);
		Assert.Contains("public bool IsSupported(string path) => path.Length > 0;", text, StringComparison.Ordinal);
		Assert.Contains("public void Dispose() => System.Console.WriteLine(\"implementation\");", text, StringComparison.Ordinal);
		Assert.Contains("public void Reset()", text, StringComparison.Ordinal);
		Assert.DoesNotContain("block implementation", text, StringComparison.Ordinal);
		Assert.Equal("{ }", Assert.Single(plan.Edits).Replacement);
	}

	[Fact]
	public void CSharpCustomEventAccessorsRemainDeclaredButTheirBlocksAreCompressed()
	{
		const string source = """
			public sealed class Publisher
			{
			    private event System.Action? _changed;
			    public event System.Action Changed
			    {
			        add
			        {
			            var custom_add_marker = value;
			            _changed += custom_add_marker;
			        }
			        remove
			        {
			            var custom_remove_marker = value;
			            _changed -= custom_remove_marker;
			        }
			    }
			}
			""";

		var (plan, text) = Compress("Publisher.cs", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("public event System.Action Changed", text, StringComparison.Ordinal);
		Assert.Contains("add", text, StringComparison.Ordinal);
		Assert.Contains("remove", text, StringComparison.Ordinal);
		Assert.DoesNotContain("custom_add_marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("custom_remove_marker", text, StringComparison.Ordinal);
		Assert.Equal(2, plan.Edits.Count);
		Assert.All(plan.Edits, edit => Assert.Equal("{ }", edit.Replacement));
	}

	public static IEnumerable<object[]> JavaScriptBindingCases() =>
		JavaScriptCases.Select(static testCase => new object[] { testCase });

	[Theory]
	[MemberData(nameof(JavaScriptBindingCases))]
	public void JavaScriptFamilyCompressesNamedBlockArrowsButPreservesExpressionsAndFreeCallbacks(
		JavaScriptBindingCase testCase)
	{
		var (plan, text) = Compress(testCase.Path, testCase.Source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains(testCase.NamedSignature, text, StringComparison.Ordinal);
		Assert.DoesNotContain(testCase.NamedImplementationMarker, text, StringComparison.Ordinal);
		Assert.Contains(testCase.ExpressionArrow, text, StringComparison.Ordinal);
		Assert.Contains(testCase.FreeCallback, text, StringComparison.Ordinal);
		Assert.Contains(testCase.ClassField, text, StringComparison.Ordinal);
		AssertNoNewParseDefects(testCase.LanguageId, testCase.Source, text);
	}

	[Fact]
	public void JavaScriptAssignmentAndDefaultExportFunctionsKeepTheirBindingAndCompressTheirBlocks()
	{
		const string source = """
			module.exports = function (value) {
			    const assigned_function_marker = value + 1;
			    return assigned_function_marker;
			};

			export default () => {
			    const default_export_marker = 42;
			    return default_export_marker;
			};
			""";

		var (plan, text) = Compress("module.mjs", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("module.exports = function (value) { }", text, StringComparison.Ordinal);
		Assert.Contains("export default () => { }", text, StringComparison.Ordinal);
		Assert.DoesNotContain("assigned_function_marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("default_export_marker", text, StringComparison.Ordinal);
		AssertNoNewParseDefects("javascript", source, text);
	}

	public static IEnumerable<object[]> FreeClosureCases() =>
		FreeClosures.Select(static testCase => new object[] { testCase });

	[Theory]
	[MemberData(nameof(FreeClosureCases))]
	public void FreeClosuresRemainIntactWhileAdjacentNamedFunctionsAreCompressed(
		FreeClosureCase testCase)
	{
		var (plan, text) = Compress(testCase.Path, testCase.Source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains(testCase.FreeClosure, text, StringComparison.Ordinal);
		Assert.Contains(testCase.NamedSignature, text, StringComparison.Ordinal);
		Assert.DoesNotContain(testCase.NamedImplementationMarker, text, StringComparison.Ordinal);
		AssertNoNewParseDefects(testCase.LanguageId, testCase.Source, text);
	}

	private static (CodeCompressionPlan Plan, string Text) Compress(string path, string source)
	{
		using var compressor = CodeCompressionTestHarness.CreateCompressor();
		using var scope = compressor.CreateScope(Path.GetTempPath());
		var analysis = scope.Analyze(path, path, source, TestContext.Current.CancellationToken);
		return (analysis.Plan, analysis.GetResult(source).Text);
	}

	private static int CountParseDefects(CodeCompressionTestHarness harness, string source)
	{
		using var tree = harness.Parser.Parse(source)!;
		var defects = 0;
		var nodes = new Stack<TreeSitter.Node>();
		nodes.Push(tree.RootNode);
		while (nodes.TryPop(out var node))
		{
			if (node.IsError || node.IsMissing || node.IsNamed && node.StartIndex == node.EndIndex)
				defects++;
			foreach (var child in node.Children)
				nodes.Push(child);
		}
		return defects;
	}

	private static void AssertNoNewParseDefects(string languageId, string source, string transformed)
	{
		using var harness = CodeCompressionTestHarness.For(languageId);
		Assert.True(
			CountParseDefects(harness, transformed) <= CountParseDefects(harness, source),
			$"{languageId}: compression introduced a parse defect");
	}

	public sealed record PreservedMemberCase(
		string LanguageId,
		string Path,
		string Source,
		string[] Preserved,
		string[] Removed);

	public sealed record JavaScriptBindingCase(
		string LanguageId,
		string Path,
		string Source,
		string NamedSignature,
		string NamedImplementationMarker,
		string ExpressionArrow,
		string FreeCallback,
		string ClassField);

	public sealed record FreeClosureCase(
		string LanguageId,
		string Path,
		string Source,
		string FreeClosure,
		string NamedSignature,
		string NamedImplementationMarker);

	private static readonly JavaScriptBindingCase[] JavaScriptCases =
	[
		new(
			"javascript",
			"bindings.js",
			"const named = (value) => { const named_js_marker = value + 1; return named_js_marker; };\nconst expression = (value) => value + 1;\nregister(() => { const free_js_marker = 1; return free_js_marker; });\nclass Widget { onClick = () => this.close(); }",
			"const named = (value) => { }",
			"named_js_marker",
			"const expression = (value) => value + 1;",
			"register(() => { const free_js_marker = 1; return free_js_marker; });",
			"onClick = () => this.close();"),
		new(
			"typescript",
			"bindings.ts",
			"const named = (value: number): number => { const named_ts_marker = value + 1; return named_ts_marker; };\nconst expression = (value: number): number => value + 1;\nregister(() => { const free_ts_marker = 1; return free_ts_marker; });\nclass Widget { onClick = () => this.close(); }",
			"const named = (value: number): number => { }",
			"named_ts_marker",
			"const expression = (value: number): number => value + 1;",
			"register(() => { const free_ts_marker = 1; return free_ts_marker; });",
			"onClick = () => this.close();"),
		new(
			"tsx",
			"bindings.tsx",
			"const Named = (value: number) => { const named_tsx_marker = value + 1; return <span>{named_tsx_marker}</span>; };\nconst Expression = (value: number) => (<span>{value + 1}</span>);\nregister(() => { const free_tsx_marker = 1; return <span>{free_tsx_marker}</span>; });\nclass Widget extends React.Component { onClick = () => this.close(); }",
			"const Named = (value: number) => { }",
			"named_tsx_marker",
			"const Expression = (value: number) => (<span>{value + 1}</span>);",
			"register(() => { const free_tsx_marker = 1; return <span>{free_tsx_marker}</span>; });",
			"onClick = () => this.close();")
	];

	private static readonly FreeClosureCase[] FreeClosures =
	[
		new(
			"cpp",
			"closures.cpp",
			"auto callback = [](int value) { int free_cpp_marker = value + 1; return free_cpp_marker; };\nint calculate(int value) { int named_cpp_marker = value + 2; return named_cpp_marker; }",
			"[](int value) { int free_cpp_marker = value + 1; return free_cpp_marker; }",
			"int calculate(int value)",
			"named_cpp_marker"),
		new(
			"java",
			"Closures.java",
			"final class Closures { static { Runnable callback = () -> { int free_java_marker = 1; System.out.println(free_java_marker); }; callback.run(); } int calculate(int value) { int named_java_marker = value + 2; return named_java_marker; } }",
			"() -> { int free_java_marker = 1; System.out.println(free_java_marker); }",
			"int calculate(int value)",
			"named_java_marker"),
		new(
			"rust",
			"closures.rs",
			"const CALLBACK: fn(i32) -> i32 = |value| { let free_rust_marker = value + 1; free_rust_marker };\npub fn calculate(value: i32) -> i32 { let named_rust_marker = value + 2; named_rust_marker }",
			"|value| { let free_rust_marker = value + 1; free_rust_marker }",
			"pub fn calculate(value: i32) -> i32",
			"named_rust_marker"),
		new(
			"go",
			"closures.go",
			"package sample\nvar callback = func(value int) int { freeGoMarker := value + 1; return freeGoMarker }\nfunc calculate(value int) int { namedGoMarker := value + 2; return namedGoMarker }",
			"func(value int) int { freeGoMarker := value + 1; return freeGoMarker }",
			"func calculate(value int) int",
			"namedGoMarker")
	];

	private static readonly PreservedMemberCase[] Cases =
	[
		new(
			"c",
			"settings.c",
			"typedef struct Settings { int retries; const char *endpoint; } Settings;\nstatic int calculate(int value) { int removed_c_method = value + 1; return removed_c_method; }",
			["int retries;", "const char *endpoint;"],
			["removed_c_method"]),
		new(
			"cpp",
			"settings.cpp",
			"struct Settings { std::function<int(int)> normalize = [](int value) { int preserved_cpp_field = value + 1; return preserved_cpp_field; }; int calculate(int value) { int removed_cpp_method = value + 2; return removed_cpp_method; } };",
			["std::function<int(int)> normalize = [](int value) { int preserved_cpp_field = value + 1; return preserved_cpp_field; };"],
			["removed_cpp_method"]),
		new(
			"csharp",
			"Settings.cs",
			"public sealed class Settings { private readonly System.Func<int, int> _normalize = value => { var preserved_csharp_field = value + 1; return preserved_csharp_field; }; public int Value { get { var preserved_csharp_getter = _normalize(1); return preserved_csharp_getter; } set { var preserved_csharp_setter = value; } } public int Calculate(int value) { var removed_csharp_method = value + 2; return removed_csharp_method; } }",
			["private readonly System.Func<int, int> _normalize = value => { var preserved_csharp_field = value + 1; return preserved_csharp_field; };", "public int Value { get { var preserved_csharp_getter = _normalize(1); return preserved_csharp_getter; } set { var preserved_csharp_setter = value; } }"],
			["removed_csharp_method"]),
		new(
			"go",
			"settings.go",
			"package sample\ntype Settings struct { Retries int `json:\"retries\"` }\nfunc calculate(value int) int { removedGoMethod := value + 1; return removedGoMethod }",
			["Retries int `json:\"retries\"`"],
			["removedGoMethod"]),
		new(
			"java",
			"Settings.java",
			"final class Settings { private final java.util.function.IntUnaryOperator normalize = value -> { int preservedJavaField = value + 1; return preservedJavaField; }; int calculate(int value) { int removedJavaMethod = value + 2; return removedJavaMethod; } }",
			["private final java.util.function.IntUnaryOperator normalize = value -> { int preservedJavaField = value + 1; return preservedJavaField; };"],
			["removedJavaMethod"]),
		new(
			"javascript",
			"settings.js",
			"class Settings { normalize = value => { const preservedJsField = value + 1; return preservedJsField; }; get value() { const preservedJsGetter = 42; return preservedJsGetter; } set value(next) { const preservedJsSetter = next; } calculate(value) { const removedJsMethod = value + 2; return removedJsMethod; } }",
			["normalize = value => { const preservedJsField = value + 1; return preservedJsField; };", "get value() { const preservedJsGetter = 42; return preservedJsGetter; }", "set value(next) { const preservedJsSetter = next; }"],
			["removedJsMethod"]),
		new(
			"python",
			"settings.py",
			"class Settings:\n    @property\n    def value(self):\n        preserved_python_getter = self._value + 1\n        return preserved_python_getter\n\n    @value.setter\n    def value(self, next_value):\n        preserved_python_setter = next_value\n        self._value = preserved_python_setter\n\n    def calculate(self, value):\n        removed_python_method = value + 2\n        return removed_python_method\n",
			["@property\n    def value(self):\n        preserved_python_getter = self._value + 1", "@value.setter\n    def value(self, next_value):\n        preserved_python_setter = next_value"],
			["removed_python_method"]),
		new(
			"rust",
			"settings.rs",
			"pub struct Settings { pub retries: usize } impl Settings { pub fn calculate(&self, value: usize) -> usize { let removed_rust_method = value + self.retries; removed_rust_method } }",
			["pub retries: usize"],
			["removed_rust_method"]),
		new(
			"tsx",
			"Settings.tsx",
			"class Settings extends React.Component { normalize = (value: number) => { const preservedTsxField = value + 1; return preservedTsxField; }; get value(): number { const preservedTsxGetter = 42; return preservedTsxGetter; } render() { const removedTsxMethod = this.value + 2; return <span>{removedTsxMethod}</span>; } }",
			["normalize = (value: number) => { const preservedTsxField = value + 1; return preservedTsxField; };", "get value(): number { const preservedTsxGetter = 42; return preservedTsxGetter; }"],
			["removedTsxMethod"]),
		new(
			"typescript",
			"Settings.ts",
			"class Settings { private normalize = (value: number): number => { const preservedTsField = value + 1; return preservedTsField; }; get value(): number { const preservedTsGetter = 42; return preservedTsGetter; } calculate(value: number): number { const removedTsMethod = value + 2; return removedTsMethod; } }",
			["private normalize = (value: number): number => { const preservedTsField = value + 1; return preservedTsField; };", "get value(): number { const preservedTsGetter = 42; return preservedTsGetter; }"],
			["removedTsMethod"])
	];
}
