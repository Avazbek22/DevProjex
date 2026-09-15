using System.Globalization;
using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Theory]
	[InlineData("off", 0, "full")]
	[InlineData("1800", 1_800, "full")]
	[InlineData("3000", 3_000, "full")]
	[InlineData("1", 1, "full")]
	[InlineData("16000", 16_000, "full")]
	[InlineData("off", 0, "reduced")]
	[InlineData("1800", 1_800, "reduced")]
	[InlineData("3000", 3_000, "reduced")]
	[InlineData("1", 1, "reduced")]
	[InlineData("16000", 16_000, "reduced")]
	public async Task RealProcessSearchBodyLimitKeepsRangesAndDescribesTheActiveLimit(string value, int limit, string toolSet)
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateBodyLimitProject(workspace);
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"),
			["--search-body-chars", value, "--tool-set", toolSet]);
		var tools = await server.Client.ListToolsAsync(options: null, TestContext.Current.CancellationToken);
		var description = Assert.IsType<string>(Assert.Single(tools, tool => tool.Name == "search_project").Description);
		Assert.InRange(description.Length, 1, 800);
		Assert.InRange(description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, 40, 100);
		var instructions = Assert.IsType<string>(server.Client.ServerInstructions);
		Assert.InRange(instructions.Length, InstructionsFloor, InstructionsCeiling);
		var text = Normalize(await SearchAsync(server, "body-cap-marker"));
		Assert.Contains("Declarations found (path, symbol, lines):", text, StringComparison.Ordinal);
		Assert.Contains("Sample.Read", text, StringComparison.Ordinal);
		Assert.Contains("Other.Read", text, StringComparison.Ordinal);
		Assert.Contains("Sample.Read 3-97", text, StringComparison.Ordinal);
		Assert.InRange(SpotlightBody(text).Length, 1, 16_000);
		if (limit == 0)
		{
			Assert.DoesNotContain("Best declaration body", text, StringComparison.Ordinal);
			Assert.DoesNotContain("declaration body", description, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("declaration body", instructions, StringComparison.OrdinalIgnoreCase);
		}
		else
		{
			var displayedLimit = limit.ToString("N0", CultureInfo.InvariantCulture);
			Assert.Contains(displayedLimit, description, StringComparison.Ordinal);
			Assert.Contains(displayedLimit, instructions, StringComparison.Ordinal);
			Assert.Contains("Best declaration body (1 of 2):", text, StringComparison.Ordinal);
			Assert.InRange(ExtractBestDeclarationBody(text).Length, 1, limit);
			if (limit <= 3_000)
			{
				var remaining = Regex.Match(text, @"Declaration body truncated: ([1-9][0-9]*) line\(s\) remain");
				Assert.True(remaining.Success, text);
				if (limit > 1)
					Assert.Equal(95 - ExtractBestDeclarationBody(text).Split('\n').Length,
						int.Parse(remaining.Groups[1].Value, CultureInfo.InvariantCulture));
			}
		}
	}

	[Fact]
	public async Task RealProcessDefaultBodyLimitPreservesSearchTextAndDisabledBodyKeepsMatches()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateBodyLimitProject(workspace);
		var responses = new List<string>();
		foreach (var arguments in new string[][] { [], ["--search-body-chars", "1800"], ["--search-body-chars", "3000"], ["--search-body-chars", "off"] })
		{
			await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory(Guid.NewGuid().ToString("N")), arguments);
			if (arguments.Length == 0)
				Assert.Contains("up to 1,800 characters", server.Client.ServerInstructions, StringComparison.Ordinal);
			var response = Normalize(await SearchAsync(server, "body-cap-marker"));
			responses.Add(Regex.Replace(response, @"untrusted-data-[0-9a-f]{24}", "untrusted-data-nonce"));
		}
		Assert.Equal(responses[0], responses[1]);
		Assert.NotEqual(responses[0], responses[2]);
		Assert.InRange(ExtractBestDeclarationBody(responses[0]).Length, 1, 1_800);
		Assert.InRange(ExtractBestDeclarationBody(responses[2]).Length, 1_801, 3_000);
		Assert.DoesNotContain("Best declaration body", responses[3], StringComparison.Ordinal);
		Assert.Contains("Sample.Read 3-97", responses[3], StringComparison.Ordinal);
		var numberedMatches = Regex.Matches(responses[0], @"(?m)^\s*[0-9]+:.*body-cap-marker").Count;
		Assert.Equal(2, numberedMatches);
		Assert.Equal(numberedMatches, Regex.Matches(responses[3], @"(?m)^\s*[0-9]+:.*body-cap-marker").Count);
	}

	[Theory]
	[InlineData("0")]
	[InlineData("-1")]
	[InlineData("16001")]
	[InlineData("1.5")]
	[InlineData("no")]
	[InlineData("")]
	[InlineData("999999999999999999999")]
	public async Task McpSearchBodyLimitRejectsInvalidValuesBeforeStartup(string value)
	{
		var environment = new TestTerminalEnvironment();
		var exitCode = await new TerminalApplication(environment).RunAsync(
			["mcp", "--search-body-chars", value, "--language", "en"], TestContext.Current.CancellationToken);
		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		if (value.Length == 0)
		{
			Assert.Contains("DPX-CLI-MISSING-VALUE", environment.StandardError, StringComparison.Ordinal);
			Assert.Contains("--search-body-chars", environment.StandardError, StringComparison.Ordinal);
		}
		else
			Assert.Contains("--search-body-chars must be off or an integer from 1 to 16000.", environment.StandardError, StringComparison.Ordinal);
	}

	private static string CreateBodyLimitProject(TemporaryDirectory workspace)
	{
		var project = workspace.CreateDirectory("body-limit-project");
		workspace.WriteFile("body-limit-project/Sample.cs", "sealed class Sample\n{\n    string Read()\n    {\n" +
			"        var marker = \"body-cap-marker\";\n" +
			string.Join("\n", Enumerable.Range(0, 90).Select(index => "        // " + index + " " + new string('x', 70))) +
			"\n        return marker;\n    }\n}\n");
		workspace.WriteFile("body-limit-project/ZOther.cs", "sealed class Other\n{\n    string Read() => \"body-cap-marker\";\n}\n");
		return project;
	}
}
