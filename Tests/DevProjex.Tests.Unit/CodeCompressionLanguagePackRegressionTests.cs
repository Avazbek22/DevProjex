using DevProjex.Application.Compression;

namespace DevProjex.Tests.Unit;

public sealed class CodeCompressionLanguagePackRegressionTests
{
	public static IEnumerable<object[]> RichLanguageCases() =>
		Cases.Select(static testCase => new object[] { testCase });

	public static IEnumerable<object[]> BrokenLanguageCases() =>
		Cases.Select(static testCase => new object[] { testCase with
		{
			Source = testCase.Source + testCase.BrokenSuffix,
			Preserved = [.. testCase.Preserved, testCase.BrokenMarker]
		} });

	[Theory]
	[MemberData(nameof(RichLanguageCases))]
	public void ProductionLanguagePack_CompressesAdversarialConstructsWithoutLosingStructure(
		LanguageRegressionCase testCase)
	{
		var (plan, text) = Compress(testCase.Path, testCase.Source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.True(plan.SavedCharacters > 0);
		Assert.All(testCase.Preserved, expected =>
			Assert.Contains(expected, text, StringComparison.Ordinal));
		Assert.All(testCase.Removed, implementation =>
			Assert.DoesNotContain(implementation, text, StringComparison.Ordinal));
	}

	[Theory]
	[MemberData(nameof(BrokenLanguageCases))]
	public void ProductionLanguagePack_PreservesPreExistingBrokenSyntaxWithoutAddingDamage(
		LanguageRegressionCase testCase)
	{
		var (plan, text) = Compress(testCase.Path, testCase.Source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.All(testCase.Preserved, expected =>
			Assert.Contains(expected, text, StringComparison.Ordinal));
		Assert.All(testCase.Removed, implementation =>
			Assert.DoesNotContain(implementation, text, StringComparison.Ordinal));
	}

	[Theory]
	[MemberData(nameof(RichLanguageCases))]
	public void ProductionLanguagePack_UsesUtf16OffsetsForUnicodeBeforeCapturedBodies(
		LanguageRegressionCase testCase)
	{
		var commentPrefix = Path.GetExtension(testCase.Path).Equals(".py", StringComparison.OrdinalIgnoreCase)
			? "#"
			: "//";
		var unicodePrefix = $"{commentPrefix} Кириллица перед телом: 😀\n";
		var source = unicodePrefix + testCase.Source;

		var (plan, text) = Compress(testCase.Path, source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.StartsWith(unicodePrefix, text, StringComparison.Ordinal);
		Assert.All(testCase.Removed, implementation =>
			Assert.DoesNotContain(implementation, text, StringComparison.Ordinal));
	}

	private static (CodeCompressionPlan Plan, string Text) Compress(string relativePath, string source)
	{
		using var compressor = CodeCompressionTestHarness.CreateCompressor();
		using var scope = compressor.CreateScope(Path.GetTempPath());
		var plan = scope.Analyze(
			relativePath,
			relativePath,
			source,
			TestContext.Current.CancellationToken).Plan;
		return (plan, plan.Apply(source).Text);
	}

	public sealed record LanguageRegressionCase(
		string Path,
		string Source,
		string[] Preserved,
		string[] Removed,
		string BrokenSuffix,
		string BrokenMarker);

	private static readonly LanguageRegressionCase[] Cases =
	[
		new(
			"operations.c",
			"""
			#define APPLY(operation, left, right) operation(left, right)
			typedef int (*operation_fn)(int, int);

			static int apply(operation_fn operation, int left, int right)
			{
			    int c_implementation_marker = operation(left, right);
			    c_implementation_marker += APPLY(operation, left, right);
			    return c_implementation_marker;
			}
			""",
			["#define APPLY", "typedef int (*operation_fn)", "static int apply("],
			["c_implementation_marker"],
			"\nint broken_c(\n",
			"int broken_c("),
		new(
			"box.cpp",
			"""
			#define BOX_VERSION 4
			namespace sample {
			template <typename T>
			class Box {
			public:
			    explicit Box(T value) : value_(value)
			    {
			        auto cpp_constructor_marker = value_;
			        value_ = cpp_constructor_marker;
			    }

			    template <typename U>
			    U convert(U fallback) const
			    {
			        auto cpp_template_marker = static_cast<U>(value_);
			        return cpp_template_marker == U{} ? fallback : cpp_template_marker;
			    }
			private:
			    T value_;
			};
			}
			""",
			["#define BOX_VERSION", "namespace sample", "template <typename T>", "explicit Box(T value)", "U convert(U fallback) const", "T value_;"],
			["cpp_constructor_marker", "cpp_template_marker"],
			"\ntemplate <typename T> void broken_cpp(\n",
			"void broken_cpp("),
		new(
			"Pipeline.cs",
			"""
			[System.Obsolete]
			public sealed class Pipeline<T> where T : class
			{
			    private readonly System.Func<int, int> _map = value =>
			    {
			        var csharp_lambda_marker = value * 2;
			        return csharp_lambda_marker + 1;
			    };

			    public async System.Threading.Tasks.Task<T> RunAsync(T value)
			    {
			        var csharp_method_marker = await System.Threading.Tasks.Task.FromResult(value);
			        return csharp_method_marker;
			    }
			}
			""",
			["[System.Obsolete]", "class Pipeline<T>", "where T : class", "RunAsync(T value)", "_map = value =>"],
			["csharp_lambda_marker", "csharp_method_marker"],
			"\npublic sealed class BrokenCSharp<\n",
			"class BrokenCSharp<"),
		new(
			"Envelope.java",
			"""
			@Deprecated
			final class Envelope<T extends Comparable<T>> {
			    private final T value;

			    Envelope(T value) {
			        T java_constructor_marker = value;
			        this.value = java_constructor_marker;
			    }

			    <R> R map(java.util.function.Function<T, R> mapper) {
			        R java_generic_marker = mapper.apply(value);
			        return java_generic_marker;
			    }
			}
			""",
			["@Deprecated", "class Envelope<T extends Comparable<T>>", "Envelope(T value)", "<R> R map("],
			["java_constructor_marker", "java_generic_marker"],
			"\nclass BrokenJava<\n",
			"class BrokenJava<"),
		new(
			"pipeline.js",
			"""
			export async function loadPipeline(source) {
			    const javascript_async_marker = await source.read();
			    const transform = value => {
			        const javascript_arrow_marker = value * 2;
			        return javascript_arrow_marker;
			    };
			    return transform(javascript_async_marker);
			}

			export const pipeline = {
			    async run(source) {
			        const javascript_method_marker = await loadPipeline(source);
			        return javascript_method_marker;
			    }
			};
			""",
			["export async function loadPipeline", "async run(source)"],
			["javascript_async_marker", "javascript_arrow_marker", "javascript_method_marker"],
			"\nexport const brokenJavaScript = (\n",
			"brokenJavaScript"),
		new(
			"pipeline.ts",
			"""
			function sealed<T extends new (...args: any[]) => object>(target: T): T { return target; }

			@sealed
			export class Pipeline<T extends { id: string }> {
			    run(value: T): string;
			    run(value: T, suffix: string): string;
			    run(value: T, suffix = ""): string {
			        const typescript_overload_marker = `${value.id}${suffix}`;
			        return typescript_overload_marker;
			    }

			    map = <R>(value: R): R => {
			        const typescript_arrow_marker = value;
			        return typescript_arrow_marker;
			    };
			}
			""",
			["@sealed", "class Pipeline<T extends", "run(value: T): string;", "run(value: T, suffix", "map = <R>"],
			["typescript_overload_marker", "typescript_arrow_marker"],
			"\nexport interface BrokenTypeScript<\n",
			"interface BrokenTypeScript<"),
		new(
			"Widget.tsx",
			"""
			type WidgetProps<T> = { values: T[]; render: (value: T) => JSX.Element };

			export function Widget<T>({ values, render }: WidgetProps<T>) {
			    const tsx_component_marker = values.map(value => {
			        const tsx_callback_marker = render(value);
			        return <li onClick={() => console.log(value)}>{tsx_callback_marker}</li>;
			    });
			    return <ul>{tsx_component_marker}</ul>;
			}
			""",
			["type WidgetProps<T>", "export function Widget<T>"],
			["tsx_component_marker", "tsx_callback_marker", "console.log(value)"],
			"\nexport type BrokenTsx = { value:\n",
			"type BrokenTsx"),
		new(
			"repository.go",
			"""
			package sample

			type Repository[T any] struct { values []T }

			func (repository *Repository[T]) Add(value T) {
				go_receiver_marker := append(repository.values, value)
				repository.values = go_receiver_marker
			}

			func Map[T any, R any](values []T, mapper func(T) R) []R {
				go_generic_marker := make([]R, 0, len(values))
				for _, value := range values { go_generic_marker = append(go_generic_marker, mapper(value)) }
				return go_generic_marker
			}
			""",
			["type Repository[T any]", "func (repository *Repository[T]) Add", "func Map[T any, R any]"],
			["go_receiver_marker", "go_generic_marker"],
			"\nfunc brokenGo[T any](\n",
			"func brokenGo"),
		new(
			"repository.rs",
			"""
			pub struct Repository<T> { values: Vec<T> }

			pub trait Store<T> {
			    fn normalize(&self, value: T) -> T {
			        let rust_trait_marker = value;
			        rust_trait_marker
			    }
			}

			impl<T: Clone> Repository<T> {
			    pub fn map<R, F: Fn(&T) -> R>(&self, mapper: F) -> Vec<R> {
			        let rust_impl_marker = self.values.iter().map(|value| {
			            let rust_closure_marker = mapper(value);
			            rust_closure_marker
			        });
			        rust_impl_marker.collect()
			    }
			}
			""",
			["struct Repository<T>", "trait Store<T>", "fn normalize", "impl<T: Clone>", "pub fn map<R"],
			["rust_trait_marker", "rust_impl_marker", "rust_closure_marker"],
			"\npub fn broken_rust<T>(\n",
			"fn broken_rust"),
		new(
			"pipeline.py",
			""""
			from typing import TypeVar

			T = TypeVar("T")

			def traced(function):
			    return function

			@traced
			async def run_pipeline(value: T) -> T:
			    """Run one value through the pipeline."""
			    python_async_marker = value
			    async def nested(item: T) -> T:
			        python_nested_marker = item
			        return python_nested_marker
			    return await nested(python_async_marker)
			"""",
			["@traced", "async def run_pipeline", "Run one value through the pipeline."],
			["python_async_marker", "python_nested_marker"],
			"\ndef broken_python(:\n",
			"def broken_python("),
	];
}
