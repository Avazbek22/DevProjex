using System.Globalization;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessReservesWithheldCountsBeforeMatchesAndKeepsProjectValuesUntrusted()
	{
		const string marker = "ZzBoundaryMarker";
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("search-project");
		for (var file = 0; file < 30; file++)
			workspace.WriteFile($"search-project/{marker}{file:D2}.txt", string.Join('\n', Enumerable.Repeat(marker + new string('x', 160), 4)));
		workspace.WriteFile($"search-project/{marker}.cs", $"public class {marker} {{ public void Run() {{ }} }}");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		var result = await CallAsync(server, "search_project", new()
		{
			["pattern"] = marker,
			["include_patterns"] = new[] { "*.txt" },
			["context_lines"] = 0,
			["max_results"] = 200
		});
		Assert.NotEqual(true, result.IsError);
		var text = AllProcessText(result);
		var data = BatchData(result);
		Assert.Contains("Withheld matches by file:", data, StringComparison.Ordinal);
		Assert.True(data.Length <= 16_000, data.Length.ToString(CultureInfo.InvariantCulture));
		Assert.DoesNotContain("[Next read] Call read_pack", text, StringComparison.Ordinal);
		Assert.Contains("the rest are counted per file under \"Withheld matches by file\".", text, StringComparison.Ordinal);
		var missing = int.Parse(Regex.Match(text, @"\[(\d+) additional observed matches not shown\.", RegexOptions.CultureInvariant,
			TimeSpan.FromSeconds(2)).Groups[1].Value, CultureInfo.InvariantCulture);
		var distribution = data[(data.IndexOf("Withheld matches by file:", StringComparison.Ordinal) + "Withheld matches by file:".Length)..];
		var counts = Regex.Matches(distribution, @"(?m)^(\d+) [^\n]+\.txt$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2))
			.Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).Sum();
		var tail = Regex.Match(distribution, @"(?m)^(\d+) matches in those files$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
		if (tail.Success) counts += int.Parse(tail.Groups[1].Value, CultureInfo.InvariantCulture);
		Assert.Equal(missing, counts);
		AssertNoNewProjectValueOutsideData(result, marker);
		var fallback = await CallAsync(server, "search_project", new() { ["pattern"] = marker + ".*", ["symbols"] = true, ["max_results"] = 1 });
		Assert.NotEqual(true, fallback.IsError);
		Assert.Contains("[Symbols] symbols=true takes declaration names; this pattern is a regular expression, so it was searched as text.", AllProcessText(fallback), StringComparison.Ordinal);
		AssertNoNewProjectValueOutsideData(fallback, marker);
		var symbol = await CallAsync(server, "get_file", new() { ["requests"] = new[] { new { path = marker + ".cs", symbol = marker + ".Run" } } });
		Assert.NotEqual(true, symbol.IsError);
		AssertNoNewProjectValueOutsideData(symbol, marker);
	}

	private static void AssertNoNewProjectValueOutsideData(CallToolResult result, string marker)
	{
		foreach (var block in result.Content.OfType<TextContentBlock>())
		{
			var trusted = Regex.Replace(block.Text, @"<untrusted-data-(?<nonce>[a-f0-9]{24})>[\s\S]*?</untrusted-data-\k<nonce>>", "",
				RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
			Assert.DoesNotContain(marker, trusted, StringComparison.Ordinal);
		}
	}
}
