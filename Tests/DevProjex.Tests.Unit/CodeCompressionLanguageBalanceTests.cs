using DevProjex.Application.Compression;
using DevProjex.Infrastructure.Compression;
using TreeSitter;

namespace DevProjex.Tests.Unit;

public sealed class CodeCompressionLanguageBalanceTests
{
	[Fact]
	public void PythonPreservesSimpleDocstringsAndCompressesTheRemainingBody()
	{
		const string source = """"
			async def load(path):
			    """Load a document from disk."""
			    async_marker = await read(path)
			    return async_marker

			class Service:
			    @trace
			    def parse(self, text):
			        """Parse one document."""
			        decorated_marker = normalize(text)
			        return decorated_marker

			def plain(value):
			    plain_marker = value + 1
			    return plain_marker
			"""";

		var (plan, text) = Compress("service.py", source);
		var normalizedText = text.ReplaceLineEndings("\n");

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("\"\"\"Load a document from disk.\"\"\"\n    ...", normalizedText, StringComparison.Ordinal);
		Assert.Contains("\"\"\"Parse one document.\"\"\"\n        ...", normalizedText, StringComparison.Ordinal);
		Assert.Contains("def plain(value):\n    ...", normalizedText, StringComparison.Ordinal);
		Assert.DoesNotContain("async_marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("decorated_marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("plain_marker", text, StringComparison.Ordinal);
		AssertNoNewParseDefects("python", source, text);
	}

	[Fact]
	public void PythonLeavesDocstringOnlyBodyIntactAndDoesNotTreatFStringAsDocumentation()
	{
		const string source = """"
			def documented_only():
			    """Nothing follows this documentation."""

			def interpolated(name):
			    f"""Hello {name}."""
			    interpolated_marker = name.upper()
			    return interpolated_marker
			"""";

		var (plan, text) = Compress("documentation.py", source);
		var normalizedText = text.ReplaceLineEndings("\n");

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		var nextFunction = source.IndexOf("def interpolated", StringComparison.Ordinal);
		var transformedNextFunction = text.IndexOf("def interpolated", StringComparison.Ordinal);
		Assert.Equal(source[..nextFunction], text[..transformedNextFunction]);
		Assert.Contains("def interpolated(name):\n    ...", normalizedText, StringComparison.Ordinal);
		Assert.DoesNotContain("Hello {name}", text, StringComparison.Ordinal);
		Assert.DoesNotContain("interpolated_marker", text, StringComparison.Ordinal);
		AssertNoNewParseDefects("python", source, text);
	}

	[Fact]
	public void PythonPreservesClassInitializationStateButNotSameNamedModuleFunctions()
	{
		const string source = """
			class ScanSession:
			    def __init__(self, root, cache_size=1024):
			        self.root = Path(root)
			        self.cache = LRUCache(cache_size)
			        self.normalize = lambda value: value.strip()

			    @instrument
			    def __post_init__(self):
			        self.findings = []
			        self.ready = True

			    def scan(self):
			        method_marker = self.root.walk()
			        return method_marker

			def __init__():
			    module_marker = create_global_state()
			    return module_marker
			""";

		var (plan, text) = Compress("session.py", source);
		var normalizedText = text.ReplaceLineEndings("\n");

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("self.root = Path(root)", text, StringComparison.Ordinal);
		Assert.Contains("self.cache = LRUCache(cache_size)", text, StringComparison.Ordinal);
		Assert.Contains("self.normalize = lambda value: value.strip()", text, StringComparison.Ordinal);
		Assert.Contains("self.findings = []", text, StringComparison.Ordinal);
		Assert.Contains("self.ready = True", text, StringComparison.Ordinal);
		Assert.DoesNotContain("method_marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("module_marker", text, StringComparison.Ordinal);
		Assert.Contains("def scan(self):\n        ...", normalizedText, StringComparison.Ordinal);
		Assert.Contains("def __init__():\n    ...", normalizedText, StringComparison.Ordinal);
		AssertNoNewParseDefects("python", source, text);
	}

	public static TheoryData<string, string> JavaScriptFamilyFiles() => new()
	{
		{ "javascript", "options.js" },
		{ "typescript", "options.ts" },
		{ "tsx", "options.tsx" }
	};

	[Theory]
	[MemberData(nameof(JavaScriptFamilyFiles))]
	public void JavaScriptFamilyCompressesObjectFunctionValuesWithoutLosingData(
		string languageId,
		string path)
	{
		const string source = """
			const shared = { extra: 1 };
			export default {
			  name: "CartView",
			  count: 3,
			  nested: { enabled: true },
			  ["computed"]: 42,
			  ...shared,
			  data() { const shorthand_marker = 1; return { shorthand_marker }; },
			  methods: {
			    submit: async function (value) { const function_pair_marker = value + 1; return function_pair_marker; },
			    generate: function* () { const generator_pair_marker = 2; yield generator_pair_marker; },
			    save: (value) => { const arrow_pair_marker = value + 2; return arrow_pair_marker; },
			    total: () => items.reduce(sum, 0),
			    get label() { const getter_marker = this.name; return getter_marker; },
			    set label(value) { const setter_marker = value.trim(); this.name = setter_marker; },
			  },
			};
			class Widget {
			  handler = () => { const class_field_marker = 1; return class_field_marker; };
			}
			""";

		var (plan, text) = Compress(path, source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("name: \"CartView\"", text, StringComparison.Ordinal);
		Assert.Contains("nested: { enabled: true }", text, StringComparison.Ordinal);
		Assert.Contains("[\"computed\"]: 42", text, StringComparison.Ordinal);
		Assert.Contains("...shared", text, StringComparison.Ordinal);
		Assert.Contains("data() { }", text, StringComparison.Ordinal);
		Assert.Contains("submit: async function (value) { }", text, StringComparison.Ordinal);
		Assert.Contains("generate: function* () { }", text, StringComparison.Ordinal);
		Assert.Contains("save: (value) => { }", text, StringComparison.Ordinal);
		Assert.Contains("total: () => items.reduce(sum, 0)", text, StringComparison.Ordinal);
		Assert.Contains("getter_marker", text, StringComparison.Ordinal);
		Assert.Contains("setter_marker", text, StringComparison.Ordinal);
		Assert.Contains("class_field_marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("shorthand_marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("function_pair_marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("generator_pair_marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("arrow_pair_marker", text, StringComparison.Ordinal);
		AssertNoNewParseDefects(languageId, source, text);
	}

	[Theory]
	[MemberData(nameof(JavaScriptFamilyFiles))]
	public void JavaScriptFamilyCompressesCallWrappedFunctionsOnlyUnderStableBindings(
		string languageId,
		string path)
	{
		const string source = """
			const Panel = memo((props) => { const direct_wrapper_marker = props.value; return direct_wrapper_marker; });
			const Nested = memo(forwardRef(function (props, ref) { const nested_wrapper_marker = props.value; return nested_wrapper_marker; }));
			let assigned;
			assigned = debounce(() => { const assigned_wrapper_marker = 1; return assigned_wrapper_marker; }, 300);
			assigned = memo(forwardRef(() => { const nested_assignment_marker = 2; return nested_assignment_marker; }));
			export default memo(forwardRef((props, ref) => { const export_wrapper_marker = props.value; return export_wrapper_marker; }));
			describe("suite", () => { const bare_callback_marker = 1; it("works", () => bare_callback_marker); });
			""";

		var (plan, text) = Compress(path, source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("const Panel = memo((props) => { });", text, StringComparison.Ordinal);
		Assert.Contains("const Nested = memo(forwardRef(function (props, ref) { }));", text, StringComparison.Ordinal);
		Assert.Contains("assigned = debounce(() => { }, 300);", text, StringComparison.Ordinal);
		Assert.Contains("assigned = memo(forwardRef(() => { }));", text, StringComparison.Ordinal);
		Assert.Contains("export default memo(forwardRef((props, ref) => { }));", text, StringComparison.Ordinal);
		Assert.Contains("describe(\"suite\", () => { const bare_callback_marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("direct_wrapper_marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("nested_wrapper_marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("assigned_wrapper_marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("nested_assignment_marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("export_wrapper_marker", text, StringComparison.Ordinal);
		AssertNoNewParseDefects(languageId, source, text);
	}

	[Theory]
	[MemberData(nameof(JavaScriptFamilyFiles))]
	public void JavaScriptFamilyCompressesDirectCallWrapperInDefaultExport(
		string languageId,
		string path)
	{
		const string source = """
			export default memo(function (props) {
			  const direct_export_marker = props.value;
			  return direct_export_marker;
			});
			""";

		var (plan, text) = Compress(path, source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("export default memo(function (props) { });", text, StringComparison.Ordinal);
		Assert.DoesNotContain("direct_export_marker", text, StringComparison.Ordinal);
		AssertNoNewParseDefects(languageId, source, text);
	}

	[Theory]
	[MemberData(nameof(JavaScriptFamilyFiles))]
	public void JavaScriptFamilyDoesNotChangeWrapperCallsWithoutInlineFunctions(
		string languageId,
		string path)
	{
		const string source = """
			function baseline() { const baseline_marker = 1; return baseline_marker; }
			const Panel = (value) => value;
			export default memo(Panel);
			""";

		var (plan, text) = Compress(path, source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("export default memo(Panel);", text, StringComparison.Ordinal);
		Assert.Contains("const Panel = (value) => value;", text, StringComparison.Ordinal);
		Assert.DoesNotContain("baseline_marker", text, StringComparison.Ordinal);
		AssertNoNewParseDefects(languageId, source, text);
	}

	private static (CodeCompressionPlan Plan, string Text) Compress(string path, string source)
	{
		using var compressor = CodeCompressionTestHarness.CreateCompressor();
		using var scope = compressor.CreateScope(Path.GetTempPath());
		var analysis = scope.Analyze(path, path, source, TestContext.Current.CancellationToken);
		return (analysis.Plan, analysis.GetResult(source).Text);
	}

	private static void AssertNoNewParseDefects(string languageId, string source, string transformed)
	{
		using var harness = CodeCompressionTestHarness.For(languageId);
		Assert.True(
			CountParseDefects(harness.Parser, transformed) <= CountParseDefects(harness.Parser, source),
			$"{languageId}: compression introduced a parse defect");
	}

	private static int CountParseDefects(Parser parser, string source)
	{
		using var tree = parser.Parse(source)!;
		var defects = 0;
		var nodes = new Stack<Node>();
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
}
