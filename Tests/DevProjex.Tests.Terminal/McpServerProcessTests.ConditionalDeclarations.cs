using System.Globalization;
using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessSearchAndSymbolReadsRetainAMethodWithConditionalParameters()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("conditional-project");
		const string path = "src/LevelOverrideMap.cs";
		workspace.WriteFile("conditional-project/" + path,
			"namespace Serilog.Core;\nclass LevelOverrideMap\n{\npublic void GetEffectiveLevel(\n#if FEATURE_SPAN\nReadOnlySpan<char> context,\n#else\nstring context,\n#endif\nout int minimumLevel)\n{\nminimumLevel = 7;\n}\n}\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		var search = Normalize(AllProcessText(await CallAsync(server, "search_project",
			new Dictionary<string, object?> { ["pattern"] = "GetEffectiveLevel", ["context_lines"] = 0 })));
		var declaration = Regex.Match(search,
			@"^src/LevelOverrideMap\.cs (?<symbol>Serilog\.Core\.LevelOverrideMap\.GetEffectiveLevel) (?<start>[0-9]+)-(?<end>[0-9]+)$",
			RegexOptions.Multiline, TimeSpan.FromSeconds(2));
		Assert.True(declaration.Success, search);
		Assert.Equal(4, int.Parse(declaration.Groups["start"].Value, CultureInfo.InvariantCulture));
		Assert.Equal(13, int.Parse(declaration.Groups["end"].Value, CultureInfo.InvariantCulture));
		Assert.Contains("minimumLevel = 7;", search, StringComparison.Ordinal);
		var symbol = declaration.Groups["symbol"].Value;
		var scalar = Normalize(AllProcessText(await CallAsync(server, "get_file",
			new Dictionary<string, object?> { ["path"] = path, ["symbol"] = symbol })));
		Assert.Contains("Lines: 4-13 of 15", scalar, StringComparison.Ordinal);
		Assert.Contains("#if FEATURE_SPAN", scalar, StringComparison.Ordinal);
		Assert.Contains("ReadOnlySpan<char> context,", scalar, StringComparison.Ordinal);
		Assert.Contains("string context,", scalar, StringComparison.Ordinal);
		Assert.Contains("minimumLevel = 7;", scalar, StringComparison.Ordinal);
		var batch = Normalize(AllProcessText(await CallAsync(server, "get_file",
			new Dictionary<string, object?> { ["requests"] = new object[] { new { path, symbol } } })));
		Assert.Contains("Lines: 4-13 of 15", batch, StringComparison.Ordinal);
		Assert.Contains("minimumLevel = 7;", batch, StringComparison.Ordinal);
	}
}
