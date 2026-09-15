using System.Text;
using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessPreservesTheExactNameMatchAtTheEndOfTheSearchSlice()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var source = new StringBuilder("sealed class Sample\n{\n    string Configure()\n    {\n");
		for (var index = 0; index < 100; index++)
			source.Append("        var value").Append(index.ToString("D3"))
				.Append(" = \"Needle ").Append(new string('x', 105)).Append("\";\n");
		source.Append("        return value000;\n    }\n    string Needle() => \"Needle ")
			.Append(new string('y', 1_000)).Append("\";\n}\n");
		workspace.WriteFile("project/Sample.cs", source.ToString());
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));

		var text = Normalize(AllProcessText(await CallAsync(server, "search_project", new Dictionary<string, object?>
		{
			["pattern"] = "Needle", ["context_lines"] = 0, ["max_results"] = 200
		})));

		Assert.Contains("107:    string Needle()", text, StringComparison.Ordinal);
		Assert.Contains("Sample.Needle 107-107", text, StringComparison.Ordinal);
		Assert.Contains("matches written=101", text, StringComparison.Ordinal);
		Assert.DoesNotContain("symbol\":\"Sample.Configure\"", text, StringComparison.Ordinal);
		Assert.InRange(SpotlightBody(text).Length, 1, 16_000);
	}

	[Fact]
	public async Task RealProcessKeepsEveryMatchingFileWhenTheBodyWouldNeedItsSpace()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Target.cs", "sealed class Target\n{\n    string Needle() => \"Needle " +
			new string('y', 1_000) + "\";\n}\n");
		for (var index = 0; index < 25; index++)
			workspace.WriteFile($"project/file{index:D2}.txt", "Needle " + new string('x', 550) + "\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));

		var text = Normalize(AllProcessText(await CallAsync(server, "search_project", new Dictionary<string, object?>
		{
			["pattern"] = "Needle", ["context_lines"] = 0, ["max_results"] = 200
		})));

		for (var index = 0; index < 25; index++)
			Assert.Contains($"file{index:D2}.txt\n1:Needle ", text, StringComparison.Ordinal);
		Assert.Contains("3:    string Needle()", text, StringComparison.Ordinal);
		Assert.Contains("matches written=26", text, StringComparison.Ordinal);
		Assert.DoesNotContain("[Search stored]", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Best declaration body", text, StringComparison.Ordinal);
		Assert.InRange(SpotlightBody(text).Length, 1, 16_000);
	}

	[Fact]
	public async Task RealProcessPrintsOverlappingDeclarationContextOnlyOnce()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Target.cs", "sealed class Target\n{\n    string Read()\n    {\n" +
			"        var before = \"context-before\";\n        var marker = \"needle\";\n" +
			"        return before + marker;\n    }\n}\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));

		var text = Normalize(AllProcessText(await CallAsync(server, "search_project", new Dictionary<string, object?>
		{
			["pattern"] = "needle", ["context_lines"] = 3
		})));

		Assert.Contains("6:        var marker", text, StringComparison.Ordinal);
		Assert.Contains("symbol\":\"Target.Read\"", text, StringComparison.Ordinal);
		Assert.Equal(1, SpotlightBody(text).Split("var before =", StringSplitOptions.None).Length - 1);
		Assert.Equal(1, SpotlightBody(text).Split("return before + marker;", StringSplitOptions.None).Length - 1);
		Assert.Contains("matches written=1", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessBodyPlacementKeepsEveryRetainedMatchInlineOrInTheStoredPack()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Target.cs", "sealed class Target\n{\n    string Needle() => \"Needle " +
			new string('y', 1_000) + "\";\n}\n");
		for (var index = 0; index < 25; index++)
			workspace.WriteFile($"project/file{index:D2}.txt", string.Concat(Enumerable.Range(1, 2)
				.Select(line => $"Needle file-{index:D2}-line-{line} {new string('x', 525)}\n")));
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));

		var text = Normalize(AllProcessText(await CallAsync(server, "search_project", new Dictionary<string, object?>
		{
			["pattern"] = "Needle", ["context_lines"] = 0, ["max_results"] = 200
		})));
		var stored = Regex.Match(text, @"\[Search stored\] pack_id=([0-9a-f]+)", RegexOptions.CultureInvariant,
			TimeSpan.FromSeconds(2));
		Assert.True(stored.Success, text);
		var page = Normalize(AllProcessText(await CallAsync(server, "read_pack", new Dictionary<string, object?>
		{
			["pack_id"] = stored.Groups[1].Value
		})));

		for (var index = 0; index < 25; index++)
		{
			Assert.Contains($"file{index:D2}.txt\n1:Needle ", text, StringComparison.Ordinal);
			for (var line = 1; line <= 2; line++)
				Assert.Contains($"{line}:Needle file-{index:D2}-line-{line}", text + page, StringComparison.Ordinal);
		}
		Assert.Contains("3:    string Needle()", text, StringComparison.Ordinal);
		Assert.InRange(SpotlightBody(text).Length, 1, 16_000);
	}
}
