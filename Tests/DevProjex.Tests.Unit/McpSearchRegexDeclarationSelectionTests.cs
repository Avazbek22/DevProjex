using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpSearchRegexDeclarationSelectionTests
{
	[Theory]
	[InlineData("GetEffectiveLevel", "GetEffectiveLevel", "Equal")]
	[InlineData("GetEffectiveLevel", "Serilog.Core.LevelOverrideMap.GetEffectiveLevel", "Equal")]
	[InlineData("Thing", "App::Thing", "Equal")]
	[InlineData("EffectiveLevel", "P.GetEffectiveLevelCore", "Exact")]
	[InlineData("get_effective_level", "P.GetEffectiveLevel", "SeparatorInsensitive")]
	[InlineData("(?:Effective|Current)[A-Z]+Level", "P.GetEffectiveLevel", "Exact")]
	[InlineData("(?<capture>EffectiveLevel)", "P.GetEffectiveLevel", "Exact")]
	[InlineData("(?im)^EffectiveLevel$", "P.GetEffectiveLevel", "Exact")]
	[InlineData(@"Get\.Effective", "P.Get.Effective", "Exact")]
	public void LiteralFragmentsGiveDeclarationNamesTheirExpectedQuality(
		string pattern,
		string declaration,
		string expected)
	{
		var regex = new McpSearchRegex(pattern, ignoreCase: false);

		Assert.True(regex.HasDeclarationBodyLiteralTerms);
		Assert.Equal(expected, regex.DeclarationBodyNameMatchQuality(declaration).ToString());
	}

	[Theory]
	[InlineData(@"^\w{3}[A-Z]+\d$")]
	[InlineData("(?:Ab|Cd)[A-Z]")]
	[InlineData("[A-Z0-9_]+")]
	public void PatternsWithoutLongLiteralFragmentsDoNotInfluenceDeclarationSelection(string pattern)
	{
		var regex = new McpSearchRegex(pattern, ignoreCase: false);

		Assert.False(regex.HasDeclarationBodyLiteralTerms);
		Assert.Equal(
			McpDeclarationBodyNameMatchQuality.None,
			regex.DeclarationBodyNameMatchQuality("P.AnyDeclaration"));
	}

	[Fact]
	public void NegativeAssertionsAndGroupNamesDoNotBecomeSearchTerms()
	{
		var regex = new McpSearchRegex("(?!Forbidden)(?<capture>AllowedValue)", ignoreCase: false);

		Assert.Equal(
			McpDeclarationBodyNameMatchQuality.None,
			regex.DeclarationBodyNameMatchQuality("P.Forbidden.capture"));
		Assert.Equal(
			"Equal",
			regex.DeclarationBodyNameMatchQuality("P.AllowedValue").ToString());
	}
}
