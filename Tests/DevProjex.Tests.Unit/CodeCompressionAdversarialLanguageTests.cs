using DevProjex.Application.Compression;

namespace DevProjex.Tests.Unit;

public sealed class CodeCompressionAdversarialLanguageTests
{
	public static TheoryData<string> LanguageIds()
	{
		var data = new TheoryData<string>();
		foreach (var languageId in CodeCompressionAdversarialFixtures.LanguageIds)
			data.Add(languageId);
		return data;
	}

	[Theory]
	[MemberData(nameof(LanguageIds))]
	public void ProductionShapedSyntaxKeepsStructureAndRemovesImplementation(string languageId)
	{
		var fixture = CodeCompressionAdversarialFixtures.For(languageId);
		using var compressor = CodeCompressionTestHarness.CreateCompressor();
		using var scope = compressor.CreateScope(Path.GetTempPath());

		var analysis = scope.Analyze(
			fixture.Path,
			fixture.Path,
			fixture.Source,
			TestContext.Current.CancellationToken);
		var result = analysis.GetResult(fixture.Source);

		Assert.Equal(CodeCompressionOutcome.Compressed, analysis.Plan.Outcome);
		Assert.Equal(languageId, analysis.Plan.LanguageId);
		Assert.True(result.Text.Length < fixture.Source.Length);
		Assert.All(fixture.RetainedFragments, fragment =>
			Assert.Contains(fragment, result.Text, StringComparison.Ordinal));
		Assert.All(fixture.RemovedFragments, fragment =>
			Assert.DoesNotContain(fragment, result.Text, StringComparison.Ordinal));
		if (languageId.Equals("kotlin", StringComparison.Ordinal))
		{
			Assert.DoesNotContain(
				"= { }",
				result.Text,
				StringComparison.Ordinal);
		}
	}

	[Theory]
	[MemberData(nameof(LanguageIds))]
	public void ProductionShapedSyntaxSupportsBodiesAndCommentsInOneValidatedPlan(string languageId)
	{
		const string commentMarker = "devprojex_adversarial_comment_marker";
		var fixture = CodeCompressionAdversarialFixtures.For(languageId);
		var source = AddLeadingComment(languageId, fixture.Source, commentMarker);
		using var compressor = CodeCompressionTestHarness.CreateCompressor();
		using var scope = compressor.CreateScope(
			Path.GetTempPath(),
			CodeTransformKinds.Bodies | CodeTransformKinds.Comments);

		var analysis = scope.Analyze(
			fixture.Path,
			fixture.Path,
			source,
			TestContext.Current.CancellationToken);
		var result = analysis.GetResult(source);

		Assert.Equal(CodeCompressionOutcome.Compressed, analysis.Plan.Outcome);
		var expectedKinds = CodeTransformKinds.Bodies | CodeTransformKinds.Comments;
		if (analysis.Plan.AffectedKinds != expectedKinds)
		{
			using var diagnosticHarness = CodeCompressionTestHarness.For(languageId);
			using var diagnosticTree = diagnosticHarness.Parser.Parse(source)!;
			using var bodyCursor = diagnosticHarness.Bodies.Execute(diagnosticTree.RootNode);
			var bodies = string.Join(", ", bodyCursor.Captures.Select(static capture =>
				$"{capture.Name}:{capture.Node.Type}[{capture.Node.StartIndex},{capture.Node.EndIndex})"));
			using var preserveCursor = diagnosticHarness.Preserves?.Execute(diagnosticTree.RootNode);
			var preserves = preserveCursor is null
				? "none"
				: string.Join(", ", preserveCursor.Captures.Select(static capture =>
					$"{capture.Name}:{capture.Node.Type}[{capture.Node.StartIndex},{capture.Node.EndIndex})"));
			Assert.Fail(
				$"{languageId}: expected {expectedKinds}, actual {analysis.Plan.AffectedKinds}; " +
				$"edits: {string.Join(", ", analysis.Plan.Edits.Select(static edit => $"[{edit.SourceStart},{edit.SourceEnd})={edit.Kinds}:{edit.Replacement}"))}; " +
				$"bodies: {bodies}; preserves: {preserves}; result: {result.Text}");
		}
		Assert.True(result.Text.Length < source.Length);
		Assert.DoesNotContain(commentMarker, result.Text, StringComparison.Ordinal);
		Assert.All(fixture.RetainedFragments, fragment =>
			Assert.Contains(fragment, result.Text, StringComparison.Ordinal));
		Assert.All(fixture.RemovedFragments, fragment =>
			Assert.DoesNotContain(fragment, result.Text, StringComparison.Ordinal));
		using var harness = CodeCompressionTestHarness.For(languageId);
		Assert.True(
			CountParseDefects(harness, result.Text) <= CountParseDefects(harness, source),
			$"{languageId}: combined syntax edits introduced additional parse defects");
	}

	[Theory]
	[MemberData(nameof(LanguageIds))]
	public void EveryCapturedBodyHasExactlyOneSafeInnermostOwner(string languageId)
	{
		var fixture = CodeCompressionAdversarialFixtures.For(languageId);
		using var harness = CodeCompressionTestHarness.For(languageId);
		using var tree = harness.Parser.Parse(fixture.Source)!;
		using var bodiesCursor = harness.Bodies.Execute(tree.RootNode);
		using var declarationsCursor = harness.Declarations.Execute(tree.RootNode);
		var bodies = bodiesCursor.Captures
			.Where(static capture => capture.Name.Equals("body", StringComparison.Ordinal))
			.Select(static capture => new CapturedRange(
				capture.Node.Type,
				capture.Node.StartIndex,
				capture.Node.EndIndex))
			.ToArray();
		var declarations = declarationsCursor.Captures
			.Where(static capture => capture.Name.Equals("declaration", StringComparison.Ordinal))
			.Select(static capture => new CapturedRange(
				capture.Node.Type,
				capture.Node.StartIndex,
				capture.Node.EndIndex))
			.Distinct()
			.ToArray();

		Assert.NotEmpty(bodies);
		Assert.Equal(bodies.Length, bodies.Distinct().Count());
		foreach (var body in bodies)
		{
			var owners = declarations
				.Where(declaration =>
					declaration.Start <= body.Start &&
					declaration.End >= body.End)
				.OrderBy(static declaration => declaration.Length)
				.ToArray();
			Assert.NotEmpty(owners);
			var shortestOwnerLength = owners[0].Length;
			var owner = Assert.Single(
				owners,
				candidate => candidate.Length == shortestOwnerLength);
			Assert.Contains(owner.Type, harness.Pack.ExecutableOwnerKinds);
		}
	}

	[Theory]
	[MemberData(nameof(LanguageIds))]
	public void MalformedSourceFailsClosedWithoutIntroducingAdditionalParseDefects(string languageId)
	{
		var fixture = CodeCompressionAdversarialFixtures.For(languageId);
		using var harness = CodeCompressionTestHarness.For(languageId);
		var originalDefects = CountParseDefects(harness, fixture.MalformedSource);
		using var compressor = CodeCompressionTestHarness.CreateCompressor(harness.Pack);
		using var scope = compressor.CreateScope(Path.GetTempPath());

		var analysis = scope.Analyze(
			fixture.Path,
			fixture.Path,
			fixture.MalformedSource,
			TestContext.Current.CancellationToken);
		var result = analysis.GetResult(fixture.MalformedSource);

		Assert.True(originalDefects > 0, $"{languageId}: malformed fixture parsed without defects");
		if (analysis.Plan.Outcome == CodeCompressionOutcome.Compressed)
		{
			Assert.True(
				CountParseDefects(harness, result.Text) <= originalDefects,
				$"{languageId}: compression introduced additional parse defects");
		}
		else
		{
			Assert.Equal(fixture.MalformedSource, result.Text);
			Assert.Empty(analysis.Plan.Edits);
		}
	}

	private static int CountParseDefects(
		CodeCompressionTestHarness harness,
		string source)
	{
		using var tree = harness.Parser.Parse(source)!;
		var count = 0;
		var nodes = new Stack<TreeSitter.Node>();
		nodes.Push(tree.RootNode);
		while (nodes.TryPop(out var node))
		{
			if (node.IsError ||
			    node.IsMissing ||
			    node.IsNamed && node.StartIndex == node.EndIndex)
			{
				count++;
			}

			foreach (var child in node.Children)
				nodes.Push(child);
		}

		return count;
	}

	private static string AddLeadingComment(
		string languageId,
		string source,
		string marker)
	{
		if (languageId.Equals("php", StringComparison.Ordinal))
		{
			const string openingTag = "<?php";
			var tagIndex = source.IndexOf(openingTag, StringComparison.Ordinal);
			Assert.True(tagIndex >= 0, "the PHP adversarial fixture must contain an opening tag");
			return source.Insert(tagIndex + openingTag.Length, $"\n// {marker}");
		}

		var prefix = languageId is "python" or "ruby" ? "#" : "//";
		return $"{prefix} {marker}\n{source}";
	}

	private sealed record CapturedRange(string Type, int Start, int End)
	{
		public int Length => End - Start;
	}
}
