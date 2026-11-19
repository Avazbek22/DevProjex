using System.Globalization;
using System.Text.RegularExpressions;
using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpWithheldDistributionTests
{
	[Fact]
	public void DistributionReservesItsOwnBoundAndReconcilesTheUnlistedRemainder()
	{
		var counts = Enumerable.Range(0, 35).ToDictionary(index => $"folder/file{index:D2}.txt", index => index + 1, StringComparer.Ordinal);
		var text = DevProjexMcpTools.FormatWithheldDistribution(counts);
		Assert.StartsWith(Environment.NewLine + "Withheld matches by file:" + Environment.NewLine, text, StringComparison.Ordinal);
		Assert.True(text.Length <= 2_000);
		var rows = Regex.Matches(text, @"(?m)^(\d+) [^\r\n]+\.txt\r?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
		Assert.Equal(20, rows.Count);
		var shown = rows.Sum(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
		var remainder = Regex.Match(text, @"(\d+) matches in those files", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
		Assert.Equal(counts.Values.Sum(), shown + int.Parse(remainder.Groups[1].Value, CultureInfo.InvariantCulture));
		Assert.Contains("and 15 more file(s)", text, StringComparison.Ordinal);
		Assert.Equal(text, DevProjexMcpTools.FormatWithheldDistribution(counts.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value)));
	}

	[Fact]
	public void VeryLongPathsKeepAnHonestRemainderAndValuesCannotCreateNewLines()
	{
		var small = DevProjexMcpTools.FormatWithheldDistribution(new Dictionary<string, int> { ["marker\nname.txt"] = 3 });
		Assert.Contains("3 marker\\nname.txt", small, StringComparison.Ordinal);
		Assert.DoesNotContain("\nname.txt", small, StringComparison.Ordinal);
		var longPath = new string('x', 4_000) + ".txt";
		var bounded = DevProjexMcpTools.FormatWithheldDistribution(new Dictionary<string, int> { [longPath] = 12 });
		Assert.Contains("Withheld matches by file:", bounded, StringComparison.Ordinal);
		Assert.Contains("and 1 more file(s)", bounded, StringComparison.Ordinal);
		Assert.Contains("12 matches in those files", bounded, StringComparison.Ordinal);
		Assert.True(bounded.Length <= 2_000);
		Assert.Equal(string.Empty, DevProjexMcpTools.FormatWithheldDistribution(new Dictionary<string, int>()));
	}
}
