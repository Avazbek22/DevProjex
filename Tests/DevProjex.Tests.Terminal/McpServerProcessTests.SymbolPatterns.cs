using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task RealProcessReadsSimpleSymbolPatternsAndKeepsEveryNameInsideTheDataBoundary(bool exists)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Service.cs", exists ? "public interface Zq9MarkerName { }" : "public interface OtherName { }");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		var result = await CallAsync(server, "search_project", new Dictionary<string, object?>
		{
			["pattern"] = @"interface\s+Zq9MarkerName",
			["symbols"] = true,
			["context_lines"] = 0
		});
		Assert.False(result.IsError is true, AllProcessText(result));
		foreach (var block in result.Content.OfType<TextContentBlock>())
		{
			var outside = Regex.Replace(block.Text, @"<untrusted-data-(?<nonce>[0-9a-f]+)>[\s\S]*?</untrusted-data-\k<nonce>>", "",
				RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
			Assert.DoesNotContain("Zq9MarkerName", outside, StringComparison.Ordinal);
			Assert.Contains("[Symbols] the regular expression was read as 1 declaration name(s); they are listed at the top of the results.", outside, StringComparison.Ordinal);
			Assert.Matches(@"<untrusted-data-[0-9a-f]+>\nPattern read as declaration names: Zq9MarkerName\n", block.Text);
		}
		Assert.Equal(exists, AllProcessText(result).Contains("Service.cs", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData("func Test.*ClientIP")]
	[InlineData(@"class\s+\w+Service")]
	[InlineData("(?i)foo")]
	[InlineData("a+b")]
	[InlineData(@"def\s+(foo|bar)")]
	[InlineData(@"def\s+test_.*")]
	public async Task RealProcessSearchesComplexSymbolPatternsAsText(string pattern)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/File.cs", "public class Example { }");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		var result = await CallAsync(server, "search_project", new Dictionary<string, object?> { ["pattern"] = pattern, ["symbols"] = true });
		Assert.NotEqual(true, result.IsError);
		Assert.Contains("[Symbols] symbols=true takes declaration names; this pattern is a regular expression, so it was searched as text.", AllProcessText(result), StringComparison.Ordinal);
		await using var ordinaryServer = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("ordinary-data"));
		var ordinary = await CallAsync(ordinaryServer, "search_project", new() { ["pattern"] = pattern });
		Assert.Equal(BatchData(ordinary), BatchData(result));
	}
}
