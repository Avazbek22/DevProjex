using DevProjex.Application.Compression;
using DevProjex.Infrastructure.Compression;

namespace DevProjex.Tests.Unit;

public sealed class TreeSitterCodeCompressorTests
{
	private static (CodeCompressionPlan Plan, string Text) Compress(string relativePath, string source)
	{
		var compressor = CodeCompressionTestHarness.CreateCompressor();
		using var scope = compressor.CreateScope(Path.GetTempPath());
		var plan = scope.Plan(relativePath, relativePath, source, TestContext.Current.CancellationToken);
		return (plan, plan.Apply(source).Text);
	}

	[Fact]
	public void CSharp_RemovesBodiesAndKeepsEveryDeclaration()
	{
		var (plan, text) = Compress("Widget.cs", CodeCompressionFixtures.CSharp);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.True(plan.SavedCharacters > 0);

		foreach (var declaration in new[]
		         {
			         "namespace Sample.Services", "class Widget", "IStore _store", "Key",
			         "Widget(IStore store)", "Count", "Names", "SumAsync", "Describe",
			         "enum Mode", "Fast", "Slow", "class Nested", "Work"
		         })
		{
			Assert.Contains(declaration, text, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void CSharp_DropsImplementationButNotStructure()
	{
		var (_, text) = Compress("Widget.cs", CodeCompressionFixtures.CSharp);

		Assert.DoesNotContain("foreach (var value in values)", text, StringComparison.Ordinal);
		Assert.DoesNotContain("implementation comment", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Console.WriteLine", text, StringComparison.Ordinal);
		// The nested type's own body is a container: its members must survive even though the
		// method inside it is emptied.
		Assert.Contains("public void Work()", text, StringComparison.Ordinal);
	}

	[Fact]
	public void CSharp_LocalFunctionInsideARemovedBody_DoesNotRejectTheFile()
	{
		// The local function legitimately disappears with the body. If the gate compared raw
		// declaration sets this file would be refused, and the refusal would be silent.
		var (plan, text) = Compress("Widget.cs", CodeCompressionFixtures.CSharp);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.DoesNotContain("static int Double(int value)", text, StringComparison.Ordinal);
	}

	[Fact]
	public void CSharp_FieldInitializerWithACollectionExpression_Survives()
	{
		// The shipped grammar does not understand C# 12 collection expressions and parses this with
		// a defect. Refusing every such file would cost a quarter of a modern codebase, so the gate
		// tolerates pre-existing defects and only refuses NEW ones.
		var (plan, text) = Compress("Widget.cs", CodeCompressionFixtures.CSharp);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("Names { get; } = [];", text, StringComparison.Ordinal);
	}

	[Fact]
	public void Python_KeepsTheDocstringAndRemovesTheRest()
	{
		var (plan, text) = Compress("model.py", CodeCompressionFixtures.PythonSource);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("Multi-line docstring.", text, StringComparison.Ordinal);
		Assert.Contains("\"\"\"Doc.\"\"\"", text, StringComparison.Ordinal);
		Assert.DoesNotContain("self._cache = {}", text, StringComparison.Ordinal);
		Assert.DoesNotContain("return a + b", text, StringComparison.Ordinal);
	}

	[Fact]
	public void Python_LeadingCommentIsNotADocstringAndIsRemoved()
	{
		// The difference between a comment on the first line of a body and a docstring is not
		// obvious, and someone will eventually read this behaviour as a bug. It is not: only a
		// string literal is documentation in Python.
		var (_, text) = Compress("model.py", CodeCompressionFixtures.PythonSource);

		Assert.DoesNotContain("implementation comment, not a docstring", text, StringComparison.Ordinal);
		Assert.Contains("def run(self, data):", text, StringComparison.Ordinal);
	}

	[Fact]
	public void Python_BodyThatIsOnlyADocstring_StaysValidAndKeepsIt()
	{
		var (plan, text) = Compress("model.py", CodeCompressionFixtures.PythonSource);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("def only_doc(self):", text, StringComparison.Ordinal);
		Assert.Contains("Nothing but documentation.", text, StringComparison.Ordinal);
	}

	[Fact]
	public void Python_NestedClassSuiteIsNeverCollapsed()
	{
		// class_definition and function_definition both use "block" as their body node, so a bare
		// (block) query would delete class suites wholesale.
		var (_, text) = Compress("model.py", CodeCompressionFixtures.PythonSource);

		Assert.Contains("class Inner:", text, StringComparison.Ordinal);
		Assert.Contains("def work(self):", text, StringComparison.Ordinal);
	}

	[Fact]
	public void UnsupportedExtension_IsLeftFullWithAReason()
	{
		var (plan, text) = Compress("notes.md", "# hello\n");

		Assert.Equal(CodeCompressionOutcome.UnchangedUnsupportedLanguage, plan.Outcome);
		Assert.Equal("# hello\n", text);
	}

	[Fact]
	public void FileOverTheParseLimit_IsLeftFullWithAReason()
	{
		// Parsing cannot be aborted once started, so the size cap is the only defence and its
		// refusal must be explainable rather than mysterious.
		var huge = new string('a', TreeSitterCodeCompressor.MaximumParsableCharacters + 1);

		var (plan, text) = Compress("huge.cs", huge);

		Assert.Equal(CodeCompressionOutcome.UnchangedTooLarge, plan.Outcome);
		Assert.Equal(huge.Length, text.Length);
	}

	[Fact]
	public void AMalformedLanguagePackRefusesOneFileRatherThanThrowing()
	{
		// A query capturing overlapping spans is a pack defect. Plan is contracted never to throw
		// for a refusal, so it must cost one uncompressed file, not the whole export.
		var pack = CodeCompressionTestHarness.PackWithOverlappingBodyQuery("csharp");
		var compressor = CodeCompressionTestHarness.CreateCompressor(pack);
		using var scope = compressor.CreateScope(Path.GetTempPath());

		var plan = scope.Plan("Widget.cs", "Widget.cs", CodeCompressionFixtures.CSharp, TestContext.Current.CancellationToken);

		Assert.NotEqual(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Equal(CodeCompressionFixtures.CSharp, plan.Apply(CodeCompressionFixtures.CSharp).Text);
	}

	[Fact]
	public void CompressionIsDeterministic()
	{
		var first = Compress("Widget.cs", CodeCompressionFixtures.CSharp);
		var second = Compress("Widget.cs", CodeCompressionFixtures.CSharp);

		Assert.Equal(first.Text, second.Text);
		Assert.Equal(first.Plan.Edits.Count, second.Plan.Edits.Count);
	}

	[Fact]
	public void CompressedOutputIsNeverLargerThanTheSource()
	{
		foreach (var (path, source) in new[]
		         {
			         ("Widget.cs", CodeCompressionFixtures.CSharp),
			         ("model.py", CodeCompressionFixtures.PythonSource)
		         })
		{
			var (plan, text) = Compress(path, source);
			Assert.True(text.Length <= source.Length, $"{path} grew from {source.Length} to {text.Length}");
			Assert.Equal(plan.TransformedLength, text.Length);
		}
	}

	[Fact]
	public void OffsetsOutsideRemovedBodies_StillPointAtTheSameCharacters()
	{
		var compressor = CodeCompressionTestHarness.CreateCompressor();
		using var scope = compressor.CreateScope(Path.GetTempPath());
		var source = CodeCompressionFixtures.CSharp;
		var plan = scope.Plan("Widget.cs", "Widget.cs", source, TestContext.Current.CancellationToken);
		var applied = plan.Apply(source);

		var checkedOffsets = 0;
		for (var offset = 0; offset < source.Length; offset++)
		{
			if (plan.Edits.Any(edit => offset >= edit.SourceStart && offset < edit.SourceEnd))
				continue;
			Assert.True(applied.Map.TryToTransformed(offset, out var transformed));
			Assert.Equal(source[offset], applied.Text[transformed]);
			checkedOffsets++;
		}

		Assert.True(checkedOffsets > 0);
	}
}
