using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task PublishedCliAndMcpReturnTheSameSearchMatchCoordinates()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("search-parity-project");
		workspace.WriteFile(
			"search-parity-project/src/App.cs",
			"namespace Demo;\npublic sealed class App\n{\n    public string Run() => \"needle first\";\n}\n");
		workspace.WriteFile(
			"search-parity-project/src/Helper.cs",
			"namespace Demo;\npublic sealed class Helper\n{\n    public string Read() => \"needle second\";\n}\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("mcp-data"));

		var mcp = Normalize(AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?>
			{
				["pattern"] = "needle",
				["context_lines"] = 2,
				["max_results"] = 50
			})));
		var cli = RunCliSearch(workspace, project);
		Assert.True(cli.ExitCode == 0, cli.StandardError + cli.StandardOutput);
		using var document = JsonDocument.Parse(cli.StandardOutput);
		var cliMatches = document.RootElement.GetProperty("matches").EnumerateArray()
			.Select(match => $"{match.GetProperty("path").GetString()}:{match.GetProperty("line").GetInt32()}")
			.ToHashSet(StringComparer.Ordinal);
		var mcpMatches = ExtractMcpMatches(mcp, cliMatches.Select(item => item[..item.LastIndexOf(':')]).ToHashSet());

		Assert.Equal(cliMatches.Order(StringComparer.Ordinal), mcpMatches.Order(StringComparer.Ordinal));
		foreach (var declaration in document.RootElement.GetProperty("declarations").EnumerateArray())
		{
			var selector = $"{declaration.GetProperty("path").GetString()} " +
						   $"{declaration.GetProperty("symbol").GetString()} " +
						   $"{declaration.GetProperty("startLine").GetInt32()}-" +
						   declaration.GetProperty("endLine").GetInt32();
			Assert.Contains(selector, mcp, StringComparison.Ordinal);
			if (declaration.GetProperty("body").GetString() is { Length: > 0 } body)
				Assert.Contains(body, mcp, StringComparison.Ordinal);
		}
		Assert.Contains("[Search boundary] complete", mcp, StringComparison.Ordinal);
		Assert.True(document.RootElement.GetProperty("searchBoundary").GetProperty("complete").GetBoolean());
	}

	private static HashSet<string> ExtractMcpMatches(string output, IReadOnlySet<string> paths)
	{
		var matches = new HashSet<string>(StringComparer.Ordinal);
		string? currentPath = null;
		foreach (var line in output.Split('\n'))
		{
			if (paths.Contains(line))
			{
				currentPath = line;
				continue;
			}
			if (currentPath is null)
				continue;
			var numbered = Regex.Match(
				line,
				"^(?<line>[0-9]+):",
				RegexOptions.CultureInvariant,
				TimeSpan.FromSeconds(2));
			if (numbered.Success)
				matches.Add($"{currentPath}:{numbered.Groups["line"].Value}");
		}
		return matches;
	}

	private static TerminalTestProcessResult RunCliSearch(TemporaryDirectory workspace, string project)
	{
		var start = new ProcessStartInfo("dotnet")
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		foreach (var argument in new[]
				 {
					 PublishedApplicationLocator.FindApplicationAssembly(),
					 "search", "needle", project, "--format", "json", "--git-mode", "none",
					 "--exclude", "none", "--language", "en", "--plain", "--progress", "never"
				 })
		{
			start.ArgumentList.Add(argument);
		}
		start.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = workspace.CreateDirectory("cli-data");
		return TerminalTestProcess.Run(start, TimeSpan.FromMinutes(1));
	}
}
