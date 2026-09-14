using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessGivesTheBodyToADeclarationWhoseNameContainsTheSearchTerm()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("exact-name-project");
		workspace.WriteFile(
			"exact-name-project/src/Definitions.cs",
			"namespace P;\nsealed class Auxiliary\n{\n    string Configure() => \"EffectiveLevel\";\n}\nsealed class Target\n{\n    string GetEffectiveLevelCore() => \"EffectiveLevel\";\n}");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var text = Normalize(await SearchAsync(server, "Effective(Level|Mode)"));

		var firstSelector = text.IndexOf("src/Definitions.cs P.Auxiliary.Configure", StringComparison.Ordinal);
		var selectedSelector = text.IndexOf(
			"src/Definitions.cs P.Target.GetEffectiveLevelCore",
			StringComparison.Ordinal);
		Assert.True(firstSelector >= 0 && selectedSelector > firstSelector, text);
		Assert.Contains(
			"get_file {\"path\":\"src/Definitions.cs\",\"symbol\":\"P.Target.GetEffectiveLevelCore\"}",
			text,
			StringComparison.Ordinal);
		Assert.DoesNotContain("symbol\":\"P.Auxiliary.Configure\"", text, StringComparison.Ordinal);
		Assert.InRange(ExtractBestDeclarationBody(text).Length, 1, 1_800);
		Assert.InRange(SpotlightBody(text).Length, 1, 16_000);

		var scalar = Normalize(AllProcessText(await CallAsync(
			server,
			"get_file",
			new Dictionary<string, object?>
			{
				["path"] = "src/Definitions.cs",
				["symbol"] = "P.Target.GetEffectiveLevelCore"
			})));
		Assert.Contains("GetEffectiveLevelCore", scalar, StringComparison.Ordinal);

		var batch = Normalize(AllProcessText(await CallAsync(
			server,
			"get_file",
			new Dictionary<string, object?>
			{
				["requests"] = new object[]
				{
					new { path = "src/Definitions.cs", symbol = "P.Target.GetEffectiveLevelCore" }
				}
			})));
		Assert.Contains("GetEffectiveLevelCore", batch, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessMatchesDeclarationNamesWithoutCaseOrSeparators()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("normalized-name-project");
		workspace.WriteFile(
			"normalized-name-project/src/Definitions.cs",
			"namespace P;\nsealed class Auxiliary\n{\n    string Configure() => \"get_effective_level\";\n}\nsealed class Target\n{\n    string GetEffectiveLevel() => \"get_effective_level\";\n}");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var text = Normalize(await SearchAsync(server, "get_effective_level"));

		Assert.Contains("symbol\":\"P.Target.GetEffectiveLevel\"", text, StringComparison.Ordinal);
		Assert.DoesNotContain("symbol\":\"P.Auxiliary.Configure\"", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessPrefersAnExactNameTermOverASeparatorInsensitiveMatch()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("name-quality-project");
		workspace.WriteFile(
			"name-quality-project/src/Definitions.cs",
			"namespace P;\nsealed class First\n{\n    string GetEffectiveLevel() => \"get_effective_level\";\n}\nsealed class Second\n{\n    string get_effective_level_handler() => \"get_effective_level\";\n}");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var text = Normalize(await SearchAsync(server, "get_effective_level"));

		Assert.Contains("symbol\":\"P.Second.get_effective_level_handler\"", text, StringComparison.Ordinal);
		Assert.DoesNotContain("symbol\":\"P.First.GetEffectiveLevel\"", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessUsesMatchedLineCountAfterLiteralNameMatchingDoesNotChoose()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("match-count-project");
		workspace.WriteFile(
			"match-count-project/src/Definitions.cs",
			"namespace P;\nsealed class First\n{\n    string Configure() => \"shared_marker\";\n}\nsealed class Second\n{\n    string Read()\n    {\n        var a = \"shared_marker\";\n        var b = \"shared_marker\";\n        return a + b;\n    }\n}");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var text = Normalize(await SearchAsync(server, "shared_marker"));

		Assert.Contains("symbol\":\"P.Second.Read\"", text, StringComparison.Ordinal);
		Assert.DoesNotContain("symbol\":\"P.First.Configure\"", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessKeepsCurrentOrderWhenLiteralTermsDoNotDistinguishNames()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("stable-order-project");
		workspace.WriteFile(
			"stable-order-project/src/Definitions.cs",
			"namespace P;\nsealed class First\n{\n    string Read() => \"unrelated-marker\";\n}\nsealed class Second\n{\n    string Read() => \"unrelated-marker\";\n}");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var text = Normalize(await SearchAsync(server, "unrelated-marker"));

		Assert.Contains("symbol\":\"P.First.Read\"", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessKeepsCurrentOrderWhenThePatternHasNoLiteralFragment()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("nonliteral-pattern-project");
		workspace.WriteFile(
			"nonliteral-pattern-project/src/Definitions.cs",
			"namespace P;\nsealed class First\n{\n    string Read() => \"abc1\";\n}\nsealed class Second\n{\n    string Read()\n    {\n        var a = \"def2\";\n        var b = \"ghi3\";\n        return a + b;\n    }\n}");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var text = Normalize(await SearchAsync(server, @"\w{3}\d"));

		Assert.Contains("symbol\":\"P.First.Read\"", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessBreaksEqualDeclarationMatchesByTheExistingStableOrder()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("equal-name-project");
		workspace.WriteFile(
			"equal-name-project/src/Definitions.cs",
			"namespace P;\nsealed class First\n{\n    string FindThing() => \"Thing\";\n}\nsealed class Second\n{\n    string FindThing() => \"Thing\";\n}");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var first = Normalize(await SearchAsync(server, "Thing"));
		var second = Normalize(await SearchAsync(server, "Thing"));
		var selector = new Regex(
			"get_file \\{\\\"path\\\":\\\"[^\\\"]+\\\",\\\"symbol\\\":\\\"(?<symbol>[^\\\"]+)",
			RegexOptions.CultureInvariant,
			TimeSpan.FromSeconds(2));

		Assert.Equal("P.First.FindThing", selector.Match(first).Groups["symbol"].Value);
		Assert.Equal("P.First.FindThing", selector.Match(second).Groups["symbol"].Value);
	}
}
