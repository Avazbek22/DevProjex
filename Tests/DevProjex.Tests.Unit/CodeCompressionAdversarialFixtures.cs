namespace DevProjex.Tests.Unit;

internal sealed record CodeCompressionAdversarialFixture(
	string LanguageId,
	string Path,
	string Source,
	IReadOnlyList<string> RetainedFragments,
	IReadOnlyList<string> RemovedFragments,
	string MalformedSource);

/// <summary>
/// Production-shaped language fixtures. Each case combines syntax that tends to change tree-sitter
/// nesting with explicit implementation markers, so a silent whole-file rejection is observable.
/// </summary>
internal static class CodeCompressionAdversarialFixtures
{
	private static readonly IReadOnlyDictionary<string, CodeCompressionAdversarialFixture> Fixtures =
		new[]
		{
			new CodeCompressionAdversarialFixture(
				"c",
				"pipeline.c",
				"""
				#define APPLY(transform, value) transform(value)
				typedef int (*transform_fn)(int);

				struct pipeline {
				    transform_fn transform;
				};

				static int apply_pipeline(transform_fn transform, int value)
				{
				    int adversarial_c_marker = APPLY(transform, value);
				    return adversarial_c_marker + 1;
				}
				""",
				["typedef int (*transform_fn)(int);", "apply_pipeline(transform_fn transform, int value)"],
				["adversarial_c_marker"],
				"int broken_c(int value) { int malformed_c_marker = value + 1;"),
			new CodeCompressionAdversarialFixture(
				"cpp",
				"box.hpp",
				"""
				namespace sample {
				template <typename T>
				class Box final {
				public:
				    explicit Box(T value) : value_(value)
				    {
				        auto adversarial_cpp_ctor_marker = value_;
				        value_ = adversarial_cpp_ctor_marker;
				    }

				    template <typename F>
				    auto map(F&& transform) const
				    {
				        auto adversarial_cpp_map_marker = transform(value_);
				        return [result = adversarial_cpp_map_marker](int delta) {
				            int adversarial_cpp_lambda_marker = result + delta;
				            return adversarial_cpp_lambda_marker;
				        };
				    }

				private:
				    T value_;
				};
				}
				""",
				["template <typename T>", "explicit Box(T value)", "auto map(F&& transform) const", "T value_;"],
				["adversarial_cpp_ctor_marker", "adversarial_cpp_map_marker", "adversarial_cpp_lambda_marker"],
				"template <typename T> T broken_cpp(T value) { auto malformed_cpp_marker = value;"),
			new CodeCompressionAdversarialFixture(
				"csharp",
				"Coordinator.cs",
				"""
				[System.AttributeUsage(System.AttributeTargets.Method)]
				public sealed class TraceAttribute : System.Attribute { }

				public sealed record Envelope<T>(T Value) where T : notnull;

				public sealed class Coordinator<T> where T : class
				{
				    private readonly System.Func<T, int> _measure = Measure;

				    [Trace]
				    public async System.Threading.Tasks.Task<int> RunAsync<TValue>(TValue value)
				        where TValue : struct
				    {
				        var adversarial_csharp_marker = await System.Threading.Tasks.Task.FromResult(value.GetHashCode());
				        return adversarial_csharp_marker;
				    }

				    private static int Measure(T value)
				    {
				        var adversarial_csharp_measure_marker = value.GetHashCode();
				        return adversarial_csharp_measure_marker;
				    }
				}
				""",
				["record Envelope<T>", "_measure = Measure;", "RunAsync<TValue>", "where TValue : struct", "Measure(T value)"],
				["adversarial_csharp_marker", "adversarial_csharp_measure_marker"],
				"public sealed class Broken { public int Run() { var malformed_csharp_marker = 1;"),
			new CodeCompressionAdversarialFixture(
				"go",
				"store.go",
				"""
				package sample

				type Store[T any] struct {
					value T
				}

				func (store *Store[T]) Map(transform func(T) T) T {
					adversarialGoMarker := transform(store.value)
					callback := func(value T) T {
						adversarialGoLiteralMarker := transform(value)
						return adversarialGoLiteralMarker
					}
					return callback(adversarialGoMarker)
				}
				""",
				["type Store[T any] struct", "func (store *Store[T]) Map(transform func(T) T) T"],
				["adversarialGoMarker", "adversarialGoLiteralMarker"],
				"package sample\nfunc brokenGo(value int) int { malformedGoMarker := value + 1"),
			new CodeCompressionAdversarialFixture(
				"java",
				"Repository.java",
				"""
				import java.util.function.Function;

				@interface Traced { }

				record Envelope<T>(T value) { }

				final class Repository<T extends Comparable<T>> {
				    private final T value;

				    Repository(T value) {
				        T adversarialJavaCtorMarker = value;
				        this.value = adversarialJavaCtorMarker;
				    }

				    @Traced
				    <R> R map(Function<T, R> transform) {
				        R adversarialJavaMapMarker = transform.apply(value);
				        Function<R, R> identity = item -> {
				            R adversarialJavaLambdaMarker = item;
				            return adversarialJavaLambdaMarker;
				        };
				        return identity.apply(adversarialJavaMapMarker);
				    }
				}
				""",
				["record Envelope<T>", "Repository(T value)", "<R> R map(Function<T, R> transform)", "@Traced"],
				["adversarialJavaCtorMarker", "adversarialJavaMapMarker", "adversarialJavaLambdaMarker"],
				"class Broken { int brokenJava(int value) { int malformedJavaMarker = value + 1;"),
			new CodeCompressionAdversarialFixture(
				"javascript",
				"service.mjs",
				"""
				export async function* stream(values) {
				    const adversarialJsGeneratorMarker = values.length;
				    yield adversarialJsGeneratorMarker;
				}

				export class Service {
				    #normalize(value) {
				        const adversarialJsPrivateMarker = value.trim();
				        return adversarialJsPrivateMarker;
				    }

				    async run(values) {
				        const transform = (value) => {
				            const adversarialJsArrowMarker = this.#normalize(value);
				            return adversarialJsArrowMarker;
				        };
				        return Promise.all(values.map(transform));
				    }
				}
				""",
				["async function* stream(values)", "class Service", "#normalize(value)", "async run(values)"],
				["adversarialJsGeneratorMarker", "adversarialJsPrivateMarker", "adversarialJsArrowMarker"],
				"export function brokenJs(value) { const malformedJsMarker = value + 1;"),
			new CodeCompressionAdversarialFixture(
				"php",
				"Repository.phtml",
				"""
				<section data-stack="php">Repository</section>
				<?php
				#[RepositoryAttribute]
				final class Repository
				{
				    private const LIMIT = 64;
				    private array $values = [];

				    public function __construct(public readonly string $name)
				    {
				        $this->values = [$name];
				    }

				    public function map(callable $transform): array
				    {
				        $adversarialPhpMapMarker = array_map($transform, $this->values);
				        return $adversarialPhpMapMarker;
				    }
				}
				?>
				""",
				["<section data-stack=\"php\">", "#[RepositoryAttribute]", "private const LIMIT", "private array $values", "public readonly string $name", "$this->values = [$name]"],
				["$adversarialPhpMapMarker"],
				"<?php function broken_php(int $value): int { $malformedPhpMarker = $value + 1;"),
			new CodeCompressionAdversarialFixture(
				"python",
				"service.py",
				""""
				from __future__ import annotations

				def traced(function):
				    """Decorator contract."""
				    adversarial_python_decorator_marker = function
				    return adversarial_python_decorator_marker

				class Service:
				    @classmethod
				    async def create(cls, values: list[int]) -> Service:
				        """Build a service asynchronously."""
				        adversarial_python_create_marker = sum(values)
				        return cls()

				    @traced
				    def stream(self, values: list[int]):
				        """Yield normalized values."""
				        for value in values:
				            adversarial_python_stream_marker = value + 1
				            yield adversarial_python_stream_marker
				"""",
				["def traced(function):", "class Service:", "@classmethod", "async def create", "@traced", "def stream"],
				["adversarial_python_decorator_marker", "adversarial_python_create_marker", "adversarial_python_stream_marker"],
				"def broken_python(value):\n"),
			new CodeCompressionAdversarialFixture(
				"ruby",
				"repository.rb",
				"""
				module Persistence
				  class Repository
				    LIMIT = 64
				    attr_reader :root

				    def initialize(root)
				      @root = root
				      @normalizer = ->(value) { value.to_s.strip }
				    end

				    def fetch(key)
				      adversarial_ruby_fetch_marker = File.join(@root, key)
				      File.read(adversarial_ruby_fetch_marker)
				    end

				    def self.open(root)
				      adversarial_ruby_singleton_marker = new(root)
				      adversarial_ruby_singleton_marker
				    end
				  end
				end
				""",
				["module Persistence", "class Repository", "LIMIT = 64", "attr_reader :root", "@root = root", "@normalizer = ->"],
				["adversarial_ruby_fetch_marker", "adversarial_ruby_singleton_marker"],
				"class Broken\n  def broken(value)\n    malformed_ruby_marker = value + 1\n"),
			new CodeCompressionAdversarialFixture(
				"rust",
				"repository.rs",
				"""
				pub trait Transform<T> {
				    fn transform(&self, value: T) -> T {
				        let adversarial_rust_trait_marker = value;
				        adversarial_rust_trait_marker
				    }
				}

				pub struct Repository<T> {
				    value: T,
				}

				impl<T: Clone> Repository<T> {
				    pub fn map<F>(&self, transform: F) -> T
				    where
				        F: Fn(T) -> T,
				    {
				        let callback = |value: T| {
				            let adversarial_rust_closure_marker = transform(value);
				            adversarial_rust_closure_marker
				        };
				        let adversarial_rust_map_marker = self.value.clone();
				        callback(adversarial_rust_map_marker)
				    }
				}
				""",
				["trait Transform<T>", "struct Repository<T>", "impl<T: Clone>", "pub fn map<F>", "F: Fn(T) -> T"],
				["adversarial_rust_trait_marker", "adversarial_rust_closure_marker", "adversarial_rust_map_marker"],
				"pub fn broken_rust(value: i32) -> i32 { let malformed_rust_marker = value + 1;"),
			new CodeCompressionAdversarialFixture(
				"scala",
				"Repository.scala",
				"""
				package sample

				final case class Envelope[T](value: T)

				trait Transform[-T, +R] {
				  def apply(value: T): R
				}

				object Repository {
				  val normalize: String => String = value => value.trim
				  var created: Int = 0

				  def create[T](value: T)(using transform: Transform[T, String]): Envelope[String] = {
				    val adversarial_scala_create_marker = transform(value)
				    created += 1
				    Envelope(adversarial_scala_create_marker)
				  }
				}

				extension [T](values: List[T]) {
				  def indexed: List[(T, Int)] = {
				    val adversarial_scala_extension_marker = values.zipWithIndex
				    adversarial_scala_extension_marker
				  }
				}
				""",
				[
					"case class Envelope[T](value: T)",
					"trait Transform[-T, +R]",
					"val normalize: String => String = value => value.trim",
					"var created: Int = 0",
					"def create[T](value: T)(using transform: Transform[T, String])",
					"extension [T](values: List[T])",
					"def indexed: List[(T, Int)]"
				],
				["adversarial_scala_create_marker", "adversarial_scala_extension_marker"],
				"object Broken { def broken(value: Int): Int = { val malformed_scala_marker = value + 1"),
			new CodeCompressionAdversarialFixture(
				"tsx",
				"Widget.tsx",
				"""
				type WidgetProps<T> = {
				    items: readonly T[];
				    renderItem: (item: T) => React.ReactNode;
				};

				export function Widget<T>({ items, renderItem }: WidgetProps<T>) {
				    const adversarialTsxComponentMarker = items.length;
				    const onClick = (item: T) => {
				        const adversarialTsxArrowMarker = String(item);
				        console.log(adversarialTsxArrowMarker);
				    };
				    return (
				        <section data-count={adversarialTsxComponentMarker}>
				            {items.map((item) => <button onClick={() => onClick(item)}>{renderItem(item)}</button>)}
				        </section>
				    );
				}
				""",
				["type WidgetProps<T>", "function Widget<T>", "WidgetProps<T>"],
				["adversarialTsxComponentMarker", "adversarialTsxArrowMarker"],
				"export function BrokenTsx() { const malformedTsxMarker = 1; return <section>"),
			new CodeCompressionAdversarialFixture(
				"typescript",
				"repository.ts",
				"""
				function traced(target: object, key: string, descriptor: PropertyDescriptor): void { }

				export class Repository<T extends { id: string }> {
				    map(value: T): string;
				    map(value: readonly T[]): readonly string[];
				    @traced
				    map(value: T | readonly T[]): string | readonly string[] {
				        const adversarialTypeScriptMapMarker = Array.isArray(value) ? value : [value];
				        const project = (item: T) => {
				            const adversarialTypeScriptArrowMarker = item.id;
				            return adversarialTypeScriptArrowMarker;
				        };
				        return adversarialTypeScriptMapMarker.map(project);
				    }
				}
				""",
				["class Repository<T extends { id: string }>", "map(value: T): string;", "map(value: readonly T[]): readonly string[];", "@traced"],
				["adversarialTypeScriptMapMarker", "adversarialTypeScriptArrowMarker"],
				"export function brokenTypeScript(value: number): number { const malformedTypeScriptMarker = value + 1;"),
		}.ToDictionary(static fixture => fixture.LanguageId, StringComparer.Ordinal);

	public static IReadOnlyList<string> LanguageIds { get; } =
		Fixtures.Keys.Order(StringComparer.Ordinal).ToArray();

	public static CodeCompressionAdversarialFixture For(string languageId) =>
		Fixtures[languageId];
}
