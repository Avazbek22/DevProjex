using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpDeclarationBodySelectionTests
{
	[Fact]
	public void ANameMatchInTheLeadingCommentSelectsOnlyTheOwnBody()
	{
		var preview = Preview("Sample.Read", 10, 12);
		var selected = Select("Read", [preview], [Candidate(preview, 8)]);
		Assert.Same(preview, selected);
		Assert.Equal(10, selected!.Declaration.StartLine);
		Assert.Equal(12, selected.Declaration.EndLine);
		Assert.Equal("own body", selected.Text);
	}

	[Fact]
	public void AnUnrelatedExampleDoesNotBeatAMatchInCode()
	{
		var example = Preview("Unrelated", 10, 12);
		var own = Preview("Execute", 20, 22);
		Assert.Same(own, Select("needle", [example, own], [Candidate(example, 8), Candidate(own, 21)]));
	}

	[Fact]
	public void AnUnrelatedCommentAloneKeepsTheDeclarationButHasNoBody()
	{
		var preview = Preview("Unrelated", 10, 12);
		var declarations = new[] { preview.Declaration };
		var selected = McpSearchSymbols.SelectDeclarationBodyPreview([Candidate(preview, 8)], declarations, new("needle", false));
		Assert.Null(selected);
		Assert.Equal(preview.Declaration, Assert.Single(declarations));
	}

	[Fact]
	public void EqualNameQualityCountsDistinctOwnAndLeadingCommentLinesTogether()
	{
		var first = Preview("First.Read", 10, 13);
		var second = Preview("Second.Read", 20, 23);
		Assert.Same(second, Select("Read", [first, second],
			[Candidate(first, 8), Candidate(first, 11), Candidate(first, 11),
			 Candidate(second, 18), Candidate(second, 21), Candidate(second, 22)]));
	}

	[Fact]
	public void APatternWithoutLiteralTermsUsesTheFirstOwnRangeHit()
	{
		var example = Preview("Unrelated", 10, 12);
		var own = Preview("Execute", 20, 22);
		Assert.False(new McpSearchRegex(".*", false).HasDeclarationBodyLiteralTerms);
		Assert.Same(own, Select(".*", [example, own], [Candidate(example, 8), Candidate(own, 21)]));
	}

	[Theory]
	[InlineData("Needle", false)]
	[InlineData(".*", false)]
	[InlineData("Read|Needle", false)]
	[InlineData("Other", true)]
	public void OwnRangeOnlyInputsKeepTheBaselineChoice(string pattern, bool secondWins)
	{
		var first = Preview("Needle", 10, 12);
		var second = Preview("Other", 20, 22);
		Assert.Same(secondWins ? second : first, Select(pattern, [first, second],
			[Candidate(first, 10), Candidate(first, 11), Candidate(second, 21)]));
	}

	private static McpSearchDeclarationPreview Preview(string name, int start, int end) =>
		new(new("sample.ts", name, start, end), "own body", 0, true);

	[Theory]
	[InlineData(@"json\s*\(", "Context.json")]
	[InlineData(@"json\s*\(", "HonoRequest.json")]
	[InlineData("JSONRespond|TypedResponse|_status|_data", "JSONRespond")]
	[InlineData(@"json\s*\([^\n]*\)", "Context.json")]
	[InlineData("\"\\./(body-limit|ip-restriction)\"|middleware/(body-limit|ip-restriction)|ip-restriction|body-limit", "ipRestriction")]
	public void ChangedRecordedBodyChoicesKeepPositiveNameQuality(string pattern, string name)
	{
		Assert.True(new McpSearchRegex(pattern, false).DeclarationBodyNameMatchQuality(name) > McpDeclarationBodyNameMatchQuality.None);
	}

	private static McpSearchCandidate Candidate(McpSearchDeclarationPreview preview, int line) =>
		new(new("sample.ts", "sample.ts", [line], [new(line, true, $"{line}:needle")]), line, 0, "needle", 6, preview);

	private static McpSearchDeclarationPreview? Select(string pattern, McpSearchDeclarationPreview[] previews,
		McpSearchCandidate[] candidates) =>
		McpSearchSymbols.SelectDeclarationBodyPreview(candidates, previews.Select(static preview => preview.Declaration).ToArray(), new(pattern, false));
}
