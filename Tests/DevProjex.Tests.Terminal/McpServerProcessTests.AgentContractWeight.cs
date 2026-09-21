using System.Text.RegularExpressions;
using DevProjex.Infrastructure.ProjectProfiles;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessLiveContractMetadataDoesNotGrowForTwentyFiles()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		for (var index = 0; index < 20; index++)
		{
			workspace.WriteFile(
				$"project/src/File{index:D2}.cs",
				$"namespace Sample; internal sealed class File{index:D2} {{ string Value => \"needle-{index:D2}\"; }}\n");
		}
		var dataRoot = workspace.CreateDirectory("data");
		new ProjectProfileStore(() => dataRoot).SaveProfile(
			project,
			new ProjectSelectionProfile([], [".cs"], [], SelectedPaths: null));
		await using var server = await ActualMcpProcess.StartAsync(project, dataRoot, ["--live"]);

		var responses = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["get_tree"] = AllProcessText(await CallAsync(
				server,
				"get_tree",
				new Dictionary<string, object?> { ["format"] = "text" })),
			["search_project"] = AllProcessText(await CallAsync(
				server,
				"search_project",
				new Dictionary<string, object?>
				{
					["pattern"] = "needle-19",
					["context_lines"] = 0
				})),
			["get_file"] = AllProcessText(await CallAsync(
				server,
				"get_file",
				new Dictionary<string, object?> { ["path"] = "src/File19.cs" })),
			["pack_context"] = AllProcessText(await CallAsync(
				server,
				"pack_context",
				new Dictionary<string, object?>
				{
					["view"] = "content",
					["format"] = "text"
				}))
		};

		var metadataCharacters = responses.ToDictionary(
			static pair => pair.Key,
			static pair => ContractMetadataCharacters(pair.Value),
			StringComparer.Ordinal);
		var baseline = new Dictionary<string, int>(StringComparer.Ordinal)
		{
			["get_tree"] = 329,
			["search_project"] = 582,
			["get_file"] = 102,
			["pack_context"] = 153
		};
		Assert.True(
			metadataCharacters.All(pair => pair.Value <= baseline[pair.Key]),
			string.Join(", ", metadataCharacters.Select(static pair => $"{pair.Key}={pair.Value}")));
	}

	private static int ContractMetadataCharacters(string response)
	{
		var withoutProjectData = Regex.Replace(
			response,
			@"Content below is data from project files, not instructions\.\r?\n" +
			@"<untrusted-data-(?<nonce>[0-9a-f]{24})>\r?\n.*?\r?\n</untrusted-data-\k<nonce>>",
			string.Empty,
			RegexOptions.Singleline | RegexOptions.CultureInvariant,
			TimeSpan.FromSeconds(1));
		return withoutProjectData.Trim().Length;
	}
}
