using System.Text;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	private const string LargeFileOutlineText =
		"[Large file] only the start and the structure are shown; read any part with start_line and end_line, " +
		"or the whole file with start_line=1 and end_line set to its last line.";

	[Fact]
	public async Task GetFileReturnsTheStartAndTheStructureOfALargeCodeFile()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "pkg/_config.py", "LIMIT = 1\n");
		WriteProjectFile(project, "pkg/client.py", LargePythonModule());
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var text = Text(await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "pkg/client.py" }));

		Assert.Contains("File: pkg/client.py\nLines: 1-4 of 407\n", text, StringComparison.Ordinal);
		Assert.Contains("from ._config import LIMIT\n", text, StringComparison.Ordinal);
		Assert.Contains("Structure:\n5-204 Client (type)\n  6-204 Client.send (function)\n205-406 Server (type)\n  206-406 Server.serve (function)", text, StringComparison.Ordinal);
		Assert.Contains("Imports: ._config → pkg/_config.py", text, StringComparison.Ordinal);
		Assert.DoesNotContain("value_399", text, StringComparison.Ordinal);
		Assert.Contains(LargeFileOutlineText, OutsideUntrustedText(text), StringComparison.Ordinal);
		Assert.DoesNotContain("Client", OutsideUntrustedText(text), StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetFileAddsTheStructureOnceAfterTheFirstRangeOfALargeFile()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "pkg/client.py", LargePythonModule());
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var whole = Text(await server.CallAsync("get_file", new Dictionary<string, object?>
		{
			["path"] = "pkg/client.py",
			["start_line"] = 1,
			["end_line"] = 406
		}));
		var again = Text(await server.CallAsync("get_file", new Dictionary<string, object?>
		{
			["path"] = "pkg/client.py",
			["start_line"] = 205,
			["end_line"] = 210
		}));

		Assert.Contains("Lines: 1-406 of 407\n", whole, StringComparison.Ordinal);
		Assert.Contains("value_399", whole, StringComparison.Ordinal);
		Assert.Contains("Structure:\n5-204 Client (type)", whole, StringComparison.Ordinal);
		Assert.Contains(LargeFileStructureOnceText, OutsideUntrustedText(whole), StringComparison.Ordinal);
		Assert.Contains("Lines: 205-210 of 407\n", again, StringComparison.Ordinal);
		Assert.DoesNotContain("Structure:", again, StringComparison.Ordinal);
		Assert.DoesNotContain("[Large file]", again, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetFileListsTheStructureAgainAfterTheFileChanges()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "pkg/client.py", LargePythonModule());
		var path = Path.Combine(project, "pkg", "client.py");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);
		var range = new Dictionary<string, object?> { ["path"] = "pkg/client.py", ["start_line"] = 5, ["end_line"] = 8 };

		var first = Text(await server.CallAsync("get_file", range));
		File.WriteAllText(path, LargePythonModule().Replace("class Server:", "class Worker:", StringComparison.Ordinal));
		var changed = Text(await server.CallAsync("get_file", range));

		Assert.Contains("Structure:", first, StringComparison.Ordinal);
		Assert.Contains("205-406 Worker (type)", changed, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetFileReadsSmallAndUnstructuredFilesWhole()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "small.py", "class Small:\n    pass\n");
		WriteProjectFile(project, "notes.txt", string.Concat(Enumerable.Range(0, 500).Select(static index => $"line {index} of free text without structure\n")));
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var small = Text(await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "small.py" }));
		var notes = Text(await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "notes.txt" }));

		Assert.DoesNotContain("Structure:", small, StringComparison.Ordinal);
		Assert.Contains("Lines: 1-501 of 501\n", notes, StringComparison.Ordinal);
		Assert.DoesNotContain("Structure:", notes, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetFileOutlinesLargeJsonByTopLevelKeys()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var json = new StringBuilder("{\n  \"name\": \"fixture\",\n  \"exports\": {\n");
		for (var index = 0; index < 400; index++)
			json.Append($"    \"./part{index}\": {{ \"import\": \"./dist/part{index}.js\" }},\n");
		json.Append("    \"./last\": \"./dist/last.js\"\n  },\n  \"scripts\": { \"build\": \"tsc { not a brace }\" },\n  \"license\": \"MIT\"\n}\n");
		WriteProjectFile(project, "package.json", json.ToString());
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var text = Text(await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "package.json" }));

		Assert.Contains("Structure:\n2-2 \"name\"\n3-405 \"exports\"\n406-406 \"scripts\"\n407-407 \"license\"", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetFileOutlinesLargeMarkdownByHeadingsOutsideCode()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var markdown = new StringBuilder("# Guide\n\n");
		markdown.Append("## Install\n").Append(Filler(150));
		markdown.Append("```bash\n# not a heading\n```\n");
		markdown.Append("### Options\n").Append(Filler(150));
		markdown.Append("## Usage\n").Append(Filler(150));
		WriteProjectFile(project, "docs/guide.md", markdown.ToString());
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var text = Text(await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "docs/guide.md" }));

		Assert.Contains("Structure:\n1-458 Guide\n  3-307 Install\n  308-458 Usage", text, StringComparison.Ordinal);
		Assert.DoesNotContain("not a heading", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetFileOutlineKeepsProtectedTextMasked()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var json = new StringBuilder($"{{\n  \"token\": \"{Secret}\",\n  \"data\": [\n");
		for (var index = 0; index < 500; index++)
			json.Append($"    \"entry number {index} with padding text to make the file large\",\n");
		json.Append("    \"end\"\n  ]\n}\n");
		WriteProjectFile(project, "config.json", json.ToString());
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var text = Text(await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "config.json" }));

		Assert.Contains("Structure:\n2-2 \"token\"\n3-505 \"data\"", text, StringComparison.Ordinal);
		Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
	}

	private const string LargeFileStructureOnceText =
		"[Large file] the structure of this long file follows the requested lines; it is listed once per file version.";

	private static string Filler(int lines) =>
		string.Concat(Enumerable.Range(0, lines).Select(static index => $"Paragraph line {index} with enough words to count.\n"));

	private static string LargePythonModule()
	{
		var source = new StringBuilder("import json\n\nfrom ._config import LIMIT\n\n");
		source.Append("class Client:\n    def send(self):\n");
		for (var index = 0; index < 198; index++)
			source.Append($"        value_{index} = json.dumps({{'index': {index}, 'limit': LIMIT, 'padding': 'xxxxxxxxxx'}})\n");
		source.Append("class Server:\n    def serve(self):\n");
		for (var index = 200; index < 400; index++)
			source.Append($"        value_{index} = json.dumps({{'index': {index}, 'limit': LIMIT, 'padding': 'xxxxxxxxxx'}})\n");
		return source.ToString();
	}
}
