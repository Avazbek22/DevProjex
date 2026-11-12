using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Theory]
	[InlineData("get_tree", "tracked_only", "false", "5")]
	[InlineData("get_tree", "git_scope", "\"staged\"", "\"invalid\"")]
	[InlineData("get_tree", "max_file_bytes", "100000", "0")]
	[InlineData("search_project", "tracked_only", "false", "5")]
	[InlineData("search_project", "git_scope", "\"staged\"", "\"invalid\"")]
	[InlineData("related_files", "exclude_patterns", "\"absent/**\"", "[1]")]
	[InlineData("related_files", "profile", "\"standard\"", "5")]
	[InlineData("related_files", "tracked_only", "false", "5")]
	[InlineData("related_files", "git_scope", "\"staged\"", "\"invalid\"")]
	[InlineData("related_files", "max_file_bytes", "100000", "0")]
	[InlineData("get_file", "profile", "\"standard\"", "5")]
	[InlineData("get_diff", "profile", "\"standard\"", "5")]
	[InlineData("get_diff", "exclude_patterns", "\"absent/**\"", "[1]")]
	[InlineData("get_diff", "tracked_only", "false", "5")]
	[InlineData("get_diff", "max_file_bytes", "100000", "0")]
	public async Task RealProcessHiddenParametersKeepTheirResultsAndValidation(string tool, string parameter, string valid, string invalid)
	{
		using var workspace = new TemporaryDirectory();
		var root = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Anchor.ts", "export const anchor = 1;\n");
		InitializeIsolatedRepository(root);
		RunGit(root, "add", "Anchor.ts");
		RunGit(root, "commit", "--quiet", "-m", "baseline");
		workspace.WriteFile("project/Anchor.ts", "export const anchor = 2;\n");
		RunGit(root, "add", "Anchor.ts");
		await using var reduced = await ActualMcpProcess.StartAsync(root, workspace.CreateDirectory("reduced"), ["--tool-set", "reduced"]);
		await using var full = await ActualMcpProcess.StartAsync(root, workspace.CreateDirectory("full"));
		foreach (var value in new[] { valid, invalid })
		{
			using var document = JsonDocument.Parse(value);
			var arguments = new Dictionary<string, object?> { [parameter] = document.RootElement.Clone() };
			if (tool is "get_file" or "related_files") arguments["path"] = "Anchor.ts";
			if (tool == "search_project") arguments["pattern"] = "anchor";
			if (tool == "get_tree") arguments["format"] = "text";
			var a = await CallAsync(full, tool, arguments);
			var b = await CallAsync(reduced, tool, arguments);
			Assert.Equal(value == invalid, a.IsError == true);
			Assert.Equal(a.IsError, b.IsError);
			Assert.Equal(StableCatalogResponse(AllProcessText(a)), StableCatalogResponse(AllProcessText(b)));
		}
	}

	private static string StableCatalogResponse(string text) => Regex.Replace(text, "untrusted-data-[a-f0-9]{24}",
		"untrusted-data-NONCE", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
}
