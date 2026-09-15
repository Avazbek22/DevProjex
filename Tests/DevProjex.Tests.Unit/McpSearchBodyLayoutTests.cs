using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpSearchBodyLayoutTests
{
	[Fact]
	public void AnExactNameAtTheLastFittingHitKeepsItsSelectionAndEveryEvidenceAddress()
	{
		var first = Group("a.cs", [Line(1, true, new string('a', 7_500))]);
		var last = Group("b.cs", [Line(1, true, new string('b', 7_500))]);
		var original = Render(first, last);
		var preview = Preview("b.cs", "Needle", "string Needle() => \"" + new string('x', 2_500) + "\";");
		var declarations = new[] { new McpSearchDeclaration("a.cs", "Other", 1, 1), preview.Declaration };
		var candidates = new[]
		{
			new McpSearchCandidate(first, 1, 0, "needle", 1, Preview("a.cs", "Other", "other")),
			new McpSearchCandidate(last, 1, 0, "needle", 1, preview)
		};
		var selected = McpSearchSymbols.SelectDeclarationBodyPreview(candidates, declarations,
			new McpSearchRegex("Needle", ignoreCase: false));

		var plan = DevProjexMcpTools.PlanSearchDeclarationBody(original, declarations, selected);
		var reduced = DevProjexMcpTools.RenderSearchGroups([first, last], 200, 13_000);

		Assert.Equal(preview.Declaration, plan.Preview!.Declaration);
		Assert.Empty(plan.Preview.Text);
		Assert.Equal(original.WrittenHits, plan.Rendered.WrittenHits);
		Assert.Equal(original.Output.ToString(), plan.Rendered.Output.ToString());
		Assert.Equal(2, original.WrittenHits.Select(hit => hit.RelativePath).Distinct().Count());
		Assert.Single(reduced.WrittenHits.Select(hit => hit.RelativePath).Distinct());
		Assert.Equal(Addresses(original), Addresses(plan.Rendered));
	}

	[Fact]
	public void BodyReplacesOnlyRepeatedContextInsideItsOwnDeclaration()
	{
		var own = Group("a.cs", [Line(1, false, "outside"), Line(2, false, "signature"),
			Line(3, true, "needle"), Line(4, false, "tail"), Line(5, false, "outside tail")]);
		var other = Group("b.cs", [Line(2, false, "signature"), Line(3, true, "needle")]);
		var original = Render(own, other);
		var preview = Preview("a.cs", "Read", "signature\nneedle\ntail", 2, 4);

		var plan = Plan(original, preview);

		Assert.Equal(preview, plan.Preview);
		Assert.Equal(original.WrittenHits, plan.Rendered.WrittenHits);
		Assert.DoesNotContain("2-signature", plan.Rendered.Output.ToString().Split(Environment.NewLine)
			.TakeWhile(line => line != "b.cs"));
		Assert.DoesNotContain("4-tail", plan.Rendered.Output.ToString(), StringComparison.Ordinal);
		Assert.Contains("1-outside", plan.Rendered.Output.ToString(), StringComparison.Ordinal);
		Assert.Contains("5-outside tail", plan.Rendered.Output.ToString(), StringComparison.Ordinal);
		Assert.Contains("b.cs" + Environment.NewLine + "2-signature", plan.Rendered.Output.ToString(), StringComparison.Ordinal);
		AssertOffsets(plan.Rendered);
	}

	[Fact]
	public void InsufficientSpaceCutsTheBodyAtAWholeLineInsteadOfRemovingMatches()
	{
		var original = Render(Group("a.cs", [Line(1, true, new string('a', 15_000))]));
		var preview = Preview("a.cs", "Read", "signature\n" + new string('x', 600) + "\n" + new string('y', 600), 1, 3);

		var plan = Plan(original, preview);

		Assert.Equal("signature\n" + new string('x', 600), plan.Preview!.Text);
		Assert.Equal(1, plan.Preview.RemainingLines);
		Assert.Equal(original.Output.ToString(), plan.Rendered.Output.ToString());
		Assert.Equal(original.ShownMatches, plan.Rendered.ShownMatches);
	}

	[Fact]
	public void ContextIsRetainedWhenItsBodyLineCannotBePrinted()
	{
		var original = Render(Group("a.cs", [Line(1, true, new string('a', 15_000)), Line(3, false, "tail")]));
		var preview = Preview("a.cs", "Read", "signature\n" + new string('x', 1_000) + "\ntail", 1, 3);

		var plan = Plan(original, preview);

		Assert.Equal("signature", plan.Preview!.Text);
		Assert.Equal(2, plan.Preview.RemainingLines);
		Assert.Contains("3-tail", plan.Rendered.Output.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public void ACompleteBodyCanReclaimLaterContextEvenWhenItsFirstPrefixDoesNotFit()
	{
		var text = new string('x', 2_000);
		var original = Render(Group("a.cs", [Line(1, true, new string('a', 13_800)), Line(3, false, text)]));
		var preview = Preview("a.cs", "Read", "signature\n" + text, 2, 3);

		var plan = Plan(original, preview);

		Assert.Equal(preview.Text, plan.Preview!.Text);
		Assert.DoesNotContain("3-", plan.Rendered.Output.ToString(), StringComparison.Ordinal);
		Assert.Equal(original.WrittenHits, plan.Rendered.WrittenHits);
	}

	[Fact]
	public void APartialCachedLineDoesNotReclaimTheUnprintedRestOfItsContext()
	{
		var original = Render(Group("a.cs", [Line(1, true, "needle"), Line(2, false, new string('x', 3_500))]));
		var preview = Preview("a.cs", "Read", "needle\n" + new string('x', 2_900), 1, 2) with { RemainingLines = 1 };

		var plan = Plan(original, preview);

		Assert.Equal(original.Output.ToString(), plan.Rendered.Output.ToString());
		Assert.Equal(1, plan.Preview!.RemainingLines);
	}

	[Fact]
	public void ReclaimedUnicodeContextKeepsOffsetsAndGroupOwnership()
	{
		var original = Render(Group("a.cs", [Line(1, false, "λ 😀 DEVPROJEX_REDACTED[token#1]"),
			Line(2, true, "needle"), Line(3, false, "終")]), Group("b.cs", [Line(1, true, "needle")]));
		var preview = Preview("a.cs", "Read", "λ 😀 DEVPROJEX_REDACTED[token#1]\nneedle\n終", 1, 3);

		var plan = Plan(original, preview);

		Assert.Equal(original.WrittenHits, plan.Rendered.WrittenHits);
		Assert.True(plan.Rendered.RenderedLines[0].StartsGroup);
		AssertOffsets(plan.Rendered);
		Assert.Contains("DEVPROJEX_REDACTED[token#1]", plan.Preview!.Text, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void AnAbsentOrAmbiguousPreviewLeavesTheEvidenceUntouched(bool ambiguous)
	{
		var original = Render(Group("a.cs", [Line(1, true, "needle")]));
		var preview = ambiguous ? Preview("a.cs", "Read", "needle") with { IsAddressable = false } : null;

		var declarations = preview is null ? Array.Empty<McpSearchDeclaration>() : [preview.Declaration];
		var plan = DevProjexMcpTools.PlanSearchDeclarationBody(original, declarations, preview);

		Assert.Same(original, plan.Rendered);
		Assert.Same(preview, plan.Preview);
	}

	private static DevProjexMcpTools.McpSearchBodyLayout Plan(
		DevProjexMcpTools.McpSearchRenderSlice rendered, McpSearchDeclarationPreview preview) =>
		DevProjexMcpTools.PlanSearchDeclarationBody(rendered, [preview.Declaration], preview);

	private static McpSearchDeclarationPreview Preview(string path, string name, string text, int start = 1, int end = 1) =>
		new(new McpSearchDeclaration(path, name, start, end), text, 0, true);

	private static McpSearchGroupLine Line(int number, bool match, string text) =>
		new(number, match, $"{number}{(match ? ':' : '-')}{text}");

	private static McpSearchRenderedGroup Group(string path, McpSearchGroupLine[] lines) =>
		new(path, path, lines.Where(line => line.IsMatch).Select(line => line.LineNumber).ToArray(), lines);

	private static DevProjexMcpTools.McpSearchRenderSlice Render(params McpSearchRenderedGroup[] groups) =>
		DevProjexMcpTools.RenderSearchGroups(groups, 200, 16_000);

	private static McpSearchHitKey[] Addresses(DevProjexMcpTools.McpSearchRenderSlice slice) =>
		slice.WrittenHits.Select(hit => new McpSearchHitKey(hit.RelativePath, hit.Line)).ToArray();

	private static void AssertOffsets(DevProjexMcpTools.McpSearchRenderSlice slice)
	{
		var text = slice.Output.ToString();
		foreach (var line in slice.RenderedLines)
			Assert.StartsWith($"{line.LineNumber}{(line.IsMatch ? ':' : '-')}", text[line.Offset..], StringComparison.Ordinal);
	}
}
