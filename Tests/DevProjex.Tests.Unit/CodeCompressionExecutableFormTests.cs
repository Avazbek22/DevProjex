using DevProjex.Application.Compression;

namespace DevProjex.Tests.Unit;

public sealed class CodeCompressionExecutableFormTests
{
	public static TheoryData<string, string, string, string> ExecutableForms() =>
		new()
		{
			{
				"c", "conditional.c",
				"#if ENABLED\nstatic int sum(int left, int right) { int executable_c_conditional = left + right; return executable_c_conditional; }\n#endif\n",
				"executable_c_conditional"
			},
			{
				"c", "callback.c",
				"typedef int (*callback)(int);\nstatic int invoke(callback action, int value) { int executable_c_callback = action(value); return executable_c_callback; }\n",
				"executable_c_callback"
			},
			{
				"cpp", "operators.cpp",
				"struct Value { int raw; Value(int value) { int executable_cpp_ctor = value; raw = executable_cpp_ctor; } ~Value() { int executable_cpp_dtor = raw; raw = executable_cpp_dtor; } Value operator+(const Value& other) const { int executable_cpp_operator = raw + other.raw; return Value(executable_cpp_operator); } };",
				"executable_cpp_operator"
			},
			{
				"cpp", "generic-lambda.cpp",
				"auto map_value(int value) { auto action = [value]<typename T>(T offset) { int executable_cpp_generic_lambda = value + offset; return executable_cpp_generic_lambda; }; return action(1); }",
				"executable_cpp_generic_lambda"
			},
			{
				"csharp", "Accessors.cs",
				"public sealed class Accessors { private int _value; public int Value { get { var executable_csharp_getter = _value; return executable_csharp_getter; } set { var executable_csharp_setter = value; _value = executable_csharp_setter; } } }",
				"executable_csharp_getter"
			},
			{
				"csharp", "Operators.cs",
				"public readonly record struct Number(int Value) { public static Number operator +(Number left, Number right) { var executable_csharp_operator = left.Value + right.Value; return new(executable_csharp_operator); } public int Map() { int Local(int value) { var executable_csharp_local = value + 1; return executable_csharp_local; } return Local(Value); } }",
				"executable_csharp_local"
			},
			{
				"go", "callbacks.go",
				"package sample\ntype Store[T any] struct { value T }\nfunc (store Store[T]) Apply(transform func(T) T) T { callback := func(value T) T { executableGoCallback := transform(value); return executableGoCallback }; executableGoMethod := callback(store.value); return executableGoMethod }\n",
				"executableGoCallback"
			},
			{
				"go", "defer.go",
				"package sample\nfunc run(action func()) { defer func() { executableGoDeferred := action; executableGoDeferred() }(); executableGoRun := action; executableGoRun() }\n",
				"executableGoDeferred"
			},
			{
				"java", "CompactRecord.java",
				"record Range(int start, int end) { Range { int executableJavaCompact = end - start; if (executableJavaCompact < 0) throw new IllegalArgumentException(); } }",
				"executableJavaCompact"
			},
			{
				"java", "Callbacks.java",
				"final class Callbacks { synchronized int run(int value) { java.util.function.IntUnaryOperator action = item -> { int executableJavaLambda = item + 1; return executableJavaLambda; }; int executableJavaMethod = action.applyAsInt(value); return executableJavaMethod; } }",
				"executableJavaLambda"
			},
			{
				"javascript", "generator-expression.mjs",
				"export const create = function* (values) { const executableJsGeneratorExpression = values.length; yield executableJsGeneratorExpression; };",
				"executableJsGeneratorExpression"
			},
			{
				"javascript", "object-methods.mjs",
				"export const service = { get value() { const executableJsGetter = 42; return executableJsGetter; }, async run(value) { const executableJsMethod = await value; return executableJsMethod; } };",
				"executableJsGetter"
			},
			{
				"python", "nested.py",
				"async def outer(values):\n    async def inner(value):\n        executable_python_inner = value + 1\n        return executable_python_inner\n    executable_python_outer = [await inner(value) for value in values]\n    return executable_python_outer\n",
				"executable_python_inner"
			},
			{
				"python", "generator.py",
				"def stream(values):\n    for value in values:\n        executable_python_generator = value + 1\n        yield executable_python_generator\n",
				"executable_python_generator"
			},
			{
				"rust", "async.rs",
				"pub async fn load(value: i32) -> i32 { let executable_rust_async = value + 1; executable_rust_async }",
				"executable_rust_async"
			},
			{
				"rust", "closures.rs",
				"pub trait Map { fn map(&self, value: i32) -> i32 { let action = |item| { let executable_rust_closure = item + 1; executable_rust_closure }; let executable_rust_default = action(value); executable_rust_default } }",
				"executable_rust_closure"
			},
			{
				"tsx", "ClassWidget.tsx",
				"export class Widget extends React.Component<{ value: number }> { render() { const executableTsxRender = this.props.value + 1; return <section>{executableTsxRender}</section>; } }",
				"executableTsxRender"
			},
			{
				"tsx", "Callbacks.tsx",
				"export function List({ items }: { items: readonly number[] }) { return <section>{items.map((item) => { const executableTsxCallback = item + 1; return <span>{executableTsxCallback}</span>; })}</section>; }",
				"executableTsxCallback"
			},
			{
				"typescript", "generator-expression.ts",
				"export const create = function* (values: readonly number[]) { const executableTypeScriptGenerator = values.length; yield executableTypeScriptGenerator; };",
				"executableTypeScriptGenerator"
			},
			{
				"typescript", "accessors.ts",
				"export class Store { get value(): number { const executableTypeScriptGetter = 42; return executableTypeScriptGetter; } async map(value: Promise<number>): Promise<number> { const executableTypeScriptMethod = await value; return executableTypeScriptMethod; } }",
				"executableTypeScriptGetter"
			}
		};

	[Theory]
	[MemberData(nameof(ExecutableForms))]
	public void SupportedExecutableFormDoesNotLeakItsImplementation(
		string languageId,
		string path,
		string source,
		string implementationMarker)
	{
		using var harness = CodeCompressionTestHarness.For(languageId);
		using var compressor = CodeCompressionTestHarness.CreateCompressor(harness.Pack);
		using var scope = compressor.CreateScope(Path.GetTempPath());

		var analysis = scope.Analyze(
			path,
			path,
			source,
			TestContext.Current.CancellationToken);
		var result = analysis.GetResult(source);

		Assert.Equal(CodeCompressionOutcome.Compressed, analysis.Plan.Outcome);
		Assert.DoesNotContain(implementationMarker, result.Text, StringComparison.Ordinal);
		Assert.True(result.Text.Length < source.Length);
	}
}
