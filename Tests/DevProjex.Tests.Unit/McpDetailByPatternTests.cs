using DevProjex.Application.Compression;
using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpDetailByPatternTests
{
	private static readonly CodeTransformKinds Compact =
		CodeTransformKinds.Comments | CodeTransformKinds.BlankLines;
	private static readonly CodeTransformKinds Signatures =
		CodeTransformKinds.Bodies | CodeTransformKinds.Comments | CodeTransformKinds.BlankLines;

	private static McpJsonArguments Arguments(string? detailByPattern)
	{
		var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
		if (detailByPattern is not null)
		{
			using var document = JsonDocument.Parse(detailByPattern);
			values["detail_by_pattern"] = document.RootElement.Clone();
		}
		return new McpJsonArguments(values, McpJsonArguments.FreezeAllowed("detail_by_pattern"));
	}

	private static IReadOnlyList<ContentDetailOverride>? Parse(string json) =>
		McpDetailOverrides.Parse(Arguments(json));

	private static McpToolException ParseFailure(string json) =>
		Assert.Throws<McpToolException>(() => Parse(json));

	[Fact]
	public void AnAbsentParameterProducesNoOverrides()
	{
		Assert.Null(McpDetailOverrides.Parse(Arguments(null)));
	}

	[Fact]
	public void EntriesKeepCallerOrderAndMapEachLevelToItsKinds()
	{
		var overrides = Parse("""
			[
			  { "patterns": ["src/**"], "detail": "signatures" },
			  { "patterns": ["src/api/**", "src/gen/**"], "detail": "compact" },
			  { "patterns": ["src/api/client.cs"], "detail": "full" }
			]
			""")!;

		Assert.Equal(3, overrides.Count);
		Assert.Equal(Signatures, overrides[0].RequestedKinds);
		Assert.Equal(Compact, overrides[1].RequestedKinds);
		Assert.Equal(CodeTransformKinds.None, overrides[2].RequestedKinds);
		Assert.Equal(["src/api/**", "src/gen/**"], overrides[1].Patterns.Patterns);
	}

	[Fact]
	public void PatternsUseTheIncludeFilterSyntaxIncludingBraceAlternatives()
	{
		var overrides = Parse("""[{ "patterns": ["**/*.{ts,tsx}"], "detail": "compact" }]""")!;

		var set = Assert.Single(overrides).Patterns;
		Assert.True(set.Matches("app/view.tsx"));
		Assert.True(set.Matches("view.ts"));
		Assert.False(set.Matches("view.js"));
	}

	[Theory]
	[InlineData("""[{ "patterns": ["src/**"], "detail": "verbose" }]""", "detail_by_pattern[0]")]
	[InlineData("""[{ "patterns": ["src/**"], "detail": "FULL" }]""", "detail_by_pattern[0]")]
	[InlineData("""[{ "patterns": ["src/**"] }]""", "detail_by_pattern[0]")]
	[InlineData("""[{ "detail": "compact" }]""", "detail_by_pattern[0]")]
	[InlineData("""[{ "patterns": [], "detail": "compact" }]""", "detail_by_pattern[0]")]
	[InlineData("""[{ "patterns": [""], "detail": "compact" }]""", "detail_by_pattern[0]")]
	[InlineData("""[{ "patterns": ["../escape"], "detail": "compact" }]""", "detail_by_pattern[0]")]
	[InlineData("""[{ "patterns": ["src\\win"], "detail": "compact" }]""", "detail_by_pattern[0]")]
	[InlineData("""[{ "patterns": ["!negated"], "detail": "compact" }]""", "detail_by_pattern[0]")]
	[InlineData("""[{ "patterns": ["cls[ab]"], "detail": "compact" }]""", "detail_by_pattern[0]")]
	[InlineData("""[{ "patterns": ["src/**"], "detail": "compact", "extra": 1 }]""", "detail_by_pattern[0]")]
	[InlineData("""["src/**"]""", "detail_by_pattern[0]")]
	public void AnInvalidEntryNamesItsIndex(string json, string expectedIndexText)
	{
		var failure = ParseFailure(json);

		Assert.Equal(McpErrorCodes.InvalidArguments, failure.Code);
		Assert.Contains(expectedIndexText, failure.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void ALaterInvalidEntryNamesItsOwnIndex()
	{
		var failure = ParseFailure("""
			[
			  { "patterns": ["a"], "detail": "compact" },
			  { "patterns": ["b"], "detail": "compact" },
			  { "patterns": ["c"], "detail": "sparse" }
			]
			""");

		Assert.Contains("detail_by_pattern[2]", failure.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void TheEntryCountIsBounded()
	{
		var entries = string.Join(
			",",
			Enumerable.Range(0, 17).Select(index => $"{{ \"patterns\": [\"p{index}\"], \"detail\": \"compact\" }}"));

		var failure = ParseFailure($"[{entries}]");

		Assert.Equal(McpErrorCodes.InvalidArguments, failure.Code);
		Assert.Contains("16", failure.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void ThePatternCountPerEntryIsBounded()
	{
		var patterns = string.Join(",", Enumerable.Range(0, 33).Select(index => $"\"p{index}\""));

		var failure = ParseFailure($$"""[{ "patterns": [{{patterns}}], "detail": "compact" }]""");

		Assert.Contains("detail_by_pattern[0]", failure.Message, StringComparison.Ordinal);
		Assert.Contains("32", failure.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void AnEmptyArrayIsAcceptedAndMeansNoMix()
	{
		Assert.Null(Parse("[]"));
	}

	[Fact]
	public void ANonArrayValueIsRejected()
	{
		var failure = ParseFailure("\"src/**\"");

		Assert.Equal(McpErrorCodes.InvalidArguments, failure.Code);
		Assert.Contains("detail_by_pattern", failure.Message, StringComparison.Ordinal);
	}
}
