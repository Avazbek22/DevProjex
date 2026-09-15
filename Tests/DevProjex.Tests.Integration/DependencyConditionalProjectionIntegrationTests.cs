using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyConditionalProjectionIntegrationTests
{
	public static TheoryData<string, string, string> BrokenConditionalLists => new()
	{
		{ "method", "public void Run(\n#if FEATURE\nInside first,\n#else\nInside second,\n#endif\nTarget last) { Target value; }", "Run" },
		{ "constructor", "public Holder(\n#if FEATURE\nInside first,\n#else\nInside second,\n#endif\nTarget last) { Target value; }", "Holder" },
		{ "local function", "public void Run() { void Local(\n#if FEATURE\nInside first,\n#else\nInside second,\n#endif\nTarget last) { Target value; } }", "Local" },
		{ "arguments", "public void Run() { Call(\n#if FEATURE\nInside.First,\n#else\nInside.Second,\n#endif\n0); Target value; }", "Run" },
		{ "initializer", "public void Run() { var values = new[] {\n#if FEATURE\nInside.First,\n#else\nInside.Second,\n#endif\n0 }; Target value; }", "Run" },
		{ "members", "public\n#if FEATURE\nstatic\n#else\nreadonly\n#endif\nTarget Value; public void Run() { Target value; }", "Run" },
		{ "attributes", "[Marker(\n#if FEATURE\ntypeof(Inside),\n#else\ntypeof(Inside),\n#endif\n0)] public void Run() { Target value; }", "Run" },
		{ "nested", "public void Run(\n#if OUTER\n#if INNER\nInside first,\n#else\nInside second,\n#endif\n#else\nInside third,\n#endif\nTarget last) { Target value; }", "Run" },
		{ "elif", "public void Run(\n#if FIRST\nInside first,\n#elif SECOND\nInside second,\n#else\nInside third,\n#endif\nTarget last) { Target value; }", "Run" }
	};

	[Theory]
	[MemberData(nameof(BrokenConditionalLists))]
	public void ProjectionRestoresUnconditionalNavigationAndReportsTheEntireConditionalList(
		string scenario, string member, string expectedName)
	{
		var source = "namespace Sample;\npublic class Holder\n{\n" + member + "\n}\n";
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, source);
		Assert.Equal(DependencyFileStatus.Supported, facts.Status);
		var qualifiedName = expectedName == "Local" ? "Sample.Holder.Run.Local" : "Sample.Holder." + expectedName;
		Assert.Contains(facts.NavigationDeclarations, declaration => declaration.Name == qualifiedName);
		Assert.Contains(facts.References, reference => reference.Name == "Target");
		Assert.DoesNotContain(facts.References, reference => reference.Name.StartsWith("Inside", StringComparison.Ordinal));
		var omittedStartLine = LineAt(source, source.IndexOf("#if", StringComparison.Ordinal));
		var omittedEndLine = LineAt(source, source.LastIndexOf("#endif", StringComparison.Ordinal));
		Assert.DoesNotContain(facts.References, reference => reference.Site.Line >= omittedStartLine && reference.Site.Line <= omittedEndLine);
		Assert.DoesNotContain(facts.Declarations.SelectMany(declaration => declaration.DeclarationSites), site => site.Line >= omittedStartLine && site.Line <= omittedEndLine);
		Assert.DoesNotContain(facts.NavigationDeclarations, declaration => declaration.StartLine >= omittedStartLine && declaration.EndLine <= omittedEndLine);
		var partial = Assert.IsType<DependencyPartialParseDiagnostic>(facts.PartialParse);
		Assert.Contains(partial.Ranges, range =>
			range.StartLine == omittedStartLine && range.EndLine == omittedEndLine);
		Assert.Equal(1, partial.DroppedConstructs);
		Assert.True(extractor.ParseCount <= 2, scenario);
		Assert.Equal(facts.NavigationDeclarations,
			extractor.ExtractNavigation("Holder.cs", source, "snapshot", TestContext.Current.CancellationToken));
	}

	[Theory]
	[InlineData("\n")]
	[InlineData("\r\n")]
	public void ProjectionPreservesUnicodeOffsetsAndLineEndings(string newline)
	{
		var source = ("// Кириллица 😀\nnamespace Sample;\nclass Holder\n{\npublic void Run(\n#if FEATURE\n隐藏 first,\n#else\nInside second,\n#endif\nTarget last)\n{\nTarget value;\n}\n}\n").Replace("\n", newline, StringComparison.Ordinal);
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, source);
		var run = Assert.Single(facts.NavigationDeclarations, declaration => declaration.Name == "Sample.Holder.Run");
		Assert.Equal(5, run.StartLine);
		Assert.Equal(14, run.EndLine);
		Assert.Equal(source.IndexOf("public void Run", StringComparison.Ordinal), run.StartIndex);
		Assert.Equal(source.IndexOf("}" + newline + "}", StringComparison.Ordinal) + 1, run.EndIndex);
		Assert.DoesNotContain(facts.References, reference => reference.Name == "隐藏" || reference.Name == "Inside");
	}

	[Fact]
	public void RemainingSyntaxDamageStillDropsItsEntireConstruction()
	{
		var source = "class Holder { public void Broken(\n#if FEATURE\nInside first,\n#else\nInside second,\n#endif\nTarget last) { Missing value } }\nclass Before { Target value; }";
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, source);
		Assert.DoesNotContain(facts.NavigationDeclarations, declaration => declaration.Name == "Holder.Broken");
		Assert.DoesNotContain(facts.References, reference => reference.Name == "Missing" || reference.Name == "Inside");
		Assert.Contains(facts.NavigationDeclarations, declaration => declaration.Name == "Before");
		Assert.NotNull(facts.PartialParse);
	}

	[Theory]
	[InlineData("class Holder { public object Run() { return new Box\n#if FEATURE\n<Target>\n#else\n<Inside>\n#endif\n(); } }\nclass Box {}", 0)]
	[InlineData("class Holder { public object Run() { return new Box<\n#if FEATURE\nTarget,\n#else\nInside,\n#endif\nTail>(); } }\nclass Box<T> {}", 1)]
	[InlineData("class Holder : Box\n#if FEATURE\n<Target>\n#elif OTHER\n/* comment */ <Inside>\n#else\n<Tail>\n#endif\n{}\nclass Box {}", 0)]
	[InlineData("class Holder : Box<\n#if FEATURE\nTarget,\n#else\nInside,\n#endif\nTail> {}\nclass Box<T> {}", 1)]
	[InlineData("class Holder { public object Run() { return new Box\n#if FEATURE\n/* comment\n*/ <Target>\n#else\n// comment\n<Inside>\n#endif\n(); } }\nclass Box {}", 0)]
	public void ErasingConditionalGenericArgumentsCannotInventADifferentTypeReference(string source, int incorrectArity)
	{
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, source);
		Assert.DoesNotContain(facts.References, reference => reference.Name == "Box" && reference.GenericArity == incorrectArity);
		Assert.NotNull(facts.PartialParse);
	}

	[Fact]
	public void ErasingConditionalQualificationCannotInventAReferenceToTheOutsideName()
	{
		var source = "class Holder { public object Run() { return new Box\n#if FEATURE\n.Target\n#else\n.Inside\n#endif\n(); } }\nclass Box {}";
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, source);
		Assert.DoesNotContain(facts.References, reference => reference.Name == "Box");
	}

	[Fact]
	public void AGenericTypeWhollyInsideAnOmittedParameterDoesNotBlockIndependentNavigation()
	{
		var source = "class Holder { public void Run(\n#if FEATURE\nReadOnlySpan<Target> first,\n#else\nInside second,\n#endif\nTarget last) { Target value; } }";
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, source);
		Assert.Contains(facts.NavigationDeclarations, declaration => declaration.Name == "Holder.Run");
		Assert.Contains(facts.References, reference => reference.Name == "Target");
		Assert.DoesNotContain(facts.References, reference => reference.Name == "ReadOnlySpan" || reference.Name == "Inside");
		Assert.Equal(1, Assert.IsType<DependencyPartialParseDiagnostic>(facts.PartialParse).DroppedConstructs);
	}

	[Fact]
	public void ErasingAConditionalTypeParameterCannotInventAReferenceToAProjectType()
	{
		var source = "class Holder\n#if FEATURE\n<T>\n#else\n<U>\n#endif\n{ T value; public void Run() { Target value; } }\nclass T {}";
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, source);
		Assert.DoesNotContain(facts.References, reference => reference.Name == "T" && reference.Reason != "C# preprocessor configuration is not available");
		Assert.DoesNotContain(facts.NavigationDeclarations, declaration => declaration.Name == "Holder.Run");
		Assert.NotNull(facts.PartialParse);
	}

	[Theory]
	[InlineData("/*\n#if TEXT\nInside\n#endif\n*/")]
	[InlineData("string text = @\"\n#if TEXT\nInside\n#endif\n\";")]
	[InlineData("string text = \"\"\"\n#if TEXT\nInside\n#endif\n\"\"\";")]
	public void DirectiveLookingTextIsNotErasedWhenAnotherConditionalRegionIsDamaged(string literal)
	{
		var source = "class Holder {\n" + literal + "\npublic void Run(\n#if FEATURE\nInside first,\n#else\nInside second,\n#endif\nTarget last) { Target value; } }";
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, source);
		Assert.Contains(facts.NavigationDeclarations, declaration => declaration.Name == "Holder.Run");
		var partial = Assert.IsType<DependencyPartialParseDiagnostic>(facts.PartialParse);
		Assert.Equal(1, partial.DroppedConstructs);
		Assert.Equal(LineAt(source, source.IndexOf("#if FEATURE", StringComparison.Ordinal)), Assert.Single(partial.Ranges).StartLine);
	}

	[Fact]
	public void RetryingOneDamagedMethodDoesNotEraseAnIndependentConditionalConstruction()
	{
		var source = "class Holder { public void Run(\n#if FEATURE\nInside first,\n#else\nInside second,\n#endif\nTarget last) {} }\nclass Other {\n#if FEATURE\nConditional value;\n#else\nConditional other;\n#endif\n}";
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, source);
		Assert.Contains(facts.NavigationDeclarations, declaration => declaration.Name == "Holder.Run");
		Assert.Contains(facts.References, reference => reference.Name == "Conditional" && reference.Reason == "C# preprocessor configuration is not available");
		Assert.Equal(1, Assert.IsType<DependencyPartialParseDiagnostic>(facts.PartialParse).DroppedConstructs);
	}

	[Fact]
	public void WellFormedConditionalMembersDoNotTriggerAnotherParse()
	{
		using var extractor = new TreeSitterDependencyFactExtractor();
		var facts = Extract(extractor, "class Holder {\n#if FEATURE\nInside first;\n#else\nInside second;\n#endif\nTarget last;\n}");
		Assert.Null(facts.PartialParse);
		Assert.Equal(1, extractor.ParseCount);
		Assert.Contains(facts.References, reference => reference.Name == "Inside" && reference.Reason == "C# preprocessor configuration is not available");
	}

	private static FileFacts Extract(TreeSitterDependencyFactExtractor extractor, string source) =>
		extractor.Extract(new PreparedDependencySource("Holder.cs", "Holder.cs", "fixture", LanguageId.CSharp,
			"snapshot", "fixture", source), new DependencyFactsLimits(), TestContext.Current.CancellationToken);

	private static int LineAt(string source, int index) => source.AsSpan(0, index).Count('\n') + 1;
}
