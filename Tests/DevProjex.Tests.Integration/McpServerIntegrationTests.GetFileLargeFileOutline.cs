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
	public async Task GetFileAddsTheStructureOnceAfterTheThirdRangedReadOfALargeFile()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "pkg/client.py", LargePythonModule());
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var first = Text(await server.CallAsync("get_file", Range("pkg/client.py", 1, 406)));
		var second = Text(await server.CallAsync("get_file", Range("pkg/client.py", 205, 210)));
		var third = Text(await server.CallAsync("get_file", new Dictionary<string, object?>
		{
			["path"] = "pkg/client.py",
			["symbol"] = "Server.serve"
		}));
		var fourth = Text(await server.CallAsync("get_file", Range("pkg/client.py", 5, 8)));

		Assert.Contains("Lines: 1-406 of 407\n", first, StringComparison.Ordinal);
		Assert.Contains("value_399", first, StringComparison.Ordinal);
		foreach (var withoutStructure in new[] { first, second, fourth })
		{
			Assert.DoesNotContain("Structure:", withoutStructure, StringComparison.Ordinal);
			Assert.DoesNotContain("[Large file]", withoutStructure, StringComparison.Ordinal);
		}
		Assert.Contains("Lines: 206-406 of 407\n", third, StringComparison.Ordinal);
		Assert.Contains("Structure:\n5-204 Client (type)\n  6-204 Client.send (function)\n205-406 Server (type)", third, StringComparison.Ordinal);
		Assert.Contains(LargeFileStructureOnceText, OutsideUntrustedText(third), StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetFileSkipsTheStructureOnRangedReadsAfterAReadWithoutARange()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "pkg/client.py", LargePythonModule());
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var outline = Text(await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "pkg/client.py" }));
		var ranged = new List<string>();
		for (var read = 0; read < 3; read++)
			ranged.Add(Text(await server.CallAsync("get_file", Range("pkg/client.py", 10 + read, 20 + read))));

		Assert.Contains("Structure:", outline, StringComparison.Ordinal);
		Assert.All(ranged, text => Assert.DoesNotContain("Structure:", text, StringComparison.Ordinal));
	}

	[Fact]
	public async Task GetFileCountsRangedReadsAgainAfterTheFileChanges()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "pkg/client.py", LargePythonModule());
		var path = Path.Combine(project, "pkg", "client.py");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);
		var range = Range("pkg/client.py", 5, 8);

		await server.CallAsync("get_file", range);
		await server.CallAsync("get_file", range);
		var third = Text(await server.CallAsync("get_file", range));
		File.WriteAllText(path, LargePythonModule().Replace("class Server:", "class Worker:", StringComparison.Ordinal));
		var changedFirst = Text(await server.CallAsync("get_file", range));
		var changedSecond = Text(await server.CallAsync("get_file", range));
		var changedThird = Text(await server.CallAsync("get_file", range));

		Assert.Contains("205-406 Server (type)", third, StringComparison.Ordinal);
		Assert.DoesNotContain("Structure:", changedFirst, StringComparison.Ordinal);
		Assert.DoesNotContain("Structure:", changedSecond, StringComparison.Ordinal);
		Assert.Contains("205-406 Worker (type)", changedThird, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetFileReadsAMidSizedFileWholeWithoutTheStructure()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var source = new StringBuilder();
		for (var index = 0; index < 175; index++)
			source.Append($"def function_number_{index}(value):\n    return value + {index} * 1000000000 + 2000000000 + 3000\n");
		var content = source.ToString();
		Assert.InRange(content.Length, 13_000, 19_000);
		WriteProjectFile(project, "pkg/functions.py", content);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var text = Text(await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "pkg/functions.py" }));

		Assert.Contains("Lines: 1-351 of 351\n", text, StringComparison.Ordinal);
		Assert.Contains("def function_number_174(value):", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Structure:", text, StringComparison.Ordinal);
		Assert.DoesNotContain("[Large file]", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetFileListsTheTopLevelWithMemberCountsForALargeStructure()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var source = new StringBuilder("import json\n\n");
		for (var index = 0; index < 30; index++)
		{
			source.Append($"class Service{index}:\n");
			foreach (var method in new[] { "load", "save" })
			{
				source.Append($"    def {method}(self):\n");
				for (var line = 0; line < 4; line++)
					source.Append($"        value_{line} = json.dumps({{'index': {index}, 'line': {line}, 'padding': 'xxxxxxxxxxxxxxxx'}})\n");
			}
		}
		source.Append("def helper():\n    return 1\n");
		source.Append("class Lonely:\n    def run(self):\n        return 1\n");
		WriteProjectFile(project, "pkg/services.py", source.ToString());
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var text = Text(await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "pkg/services.py" }));

		Assert.Contains("Structure:\n3-13 Service0 (type, 2 members)\n14-24 Service1 (type, 2 members)\n", text, StringComparison.Ordinal);
		Assert.Contains("322-332 Service29 (type, 2 members)\n333-334 helper (function)\n335-337 Lonely (type, 1 member)\n", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Service0.load", text, StringComparison.Ordinal);
		Assert.DoesNotContain("more)", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetFileListsTheTopLevelHeadingsWithSubheadingCountsForALargeMarkdownStructure()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var markdown = new StringBuilder();
		for (var index = 0; index < 15; index++)
		{
			markdown.Append($"# Part {index}\n").Append(Filler(10));
			for (var section = 0; section < 2; section++)
				markdown.Append($"## Section {index}.{section}\n").Append(Filler(10));
		}
		WriteProjectFile(project, "docs/book.md", markdown.ToString());
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var text = Text(await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "docs/book.md" }));

		Assert.Contains("Structure:\n1-33 Part 0 (2 members)\n34-66 Part 1 (2 members)\n", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Section 0.0", text[text.IndexOf("Structure:", StringComparison.Ordinal)..], StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetFileKeepsTheStructureWithinItsCharacterCap()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var source = new StringBuilder();
		const int functions = 60;
		for (var index = 0; index < functions; index++)
		{
			source.Append($"def function_with_a_rather_long_and_descriptive_name_{index}(value):\n");
			for (var line = 0; line < 6; line++)
				source.Append($"    value = value + {index} * {line} + 1000000000 + 2000000000\n");
		}
		WriteProjectFile(project, "pkg/long_names.py", source.ToString());
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var text = Text(await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "pkg/long_names.py" }));

		var structureStart = text.IndexOf("Structure:\n", StringComparison.Ordinal) + "Structure:\n".Length;
		var more = System.Text.RegularExpressions.Regex.Match(text[structureStart..], "\\n\\(\\+(?<count>\\d+) more\\)\\n");
		Assert.True(more.Success, text);
		var map = text.Substring(structureStart, more.Index + more.Length);
		Assert.True(map.Length <= 2_500, $"structure is {map.Length} characters");
		var listed = map.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1;
		Assert.Equal(functions, listed + int.Parse(more.Groups["count"].Value, System.Globalization.CultureInfo.InvariantCulture));
		Assert.StartsWith("1-7 function_with_a_rather_long_and_descriptive_name_0 (function)\n", map, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetFileListsTheMethodsOfASingleLargeCSharpClass()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "src/Big.cs", CSharpClasses(("Big", 45)));
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var text = Text(await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "src/Big.cs" }));

		Assert.Contains("Structure:\n3-365 Fixture.Big (type, 45 members)\n  5-12 Fixture.Big.Method0 (method)\n  13-20 Fixture.Big.Method1 (method)\n", text, StringComparison.Ordinal);
		Assert.Contains("  69-76 Fixture.Big.Method8 (method)\n", text, StringComparison.Ordinal);
		Assert.Contains("  357-364 Fixture.Big.Method44 (method)\n", text, StringComparison.Ordinal);
		Assert.DoesNotContain(" more)", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetFileListsTheMembersOfBothTopLevelTypesOfALargeFile()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "src/Pair.cs", CSharpClasses(("Reader", 22), ("Writer", 22)));
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var text = Text(await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "src/Pair.cs" }));

		Assert.Contains("Structure:\n3-181 Fixture.Reader (type, 22 members)\n  5-12 Fixture.Reader.Method0 (method)\n", text, StringComparison.Ordinal);
		Assert.Contains(
			"  173-180 Fixture.Reader.Method21 (method)\n182-360 Fixture.Writer (type, 22 members)\n  184-191 Fixture.Writer.Method0 (method)\n",
			text,
			StringComparison.Ordinal);
	}

	// Classes of methods eight lines each, after a two-line preamble.
	private static string CSharpClasses(params (string Name, int Methods)[] classes)
	{
		var source = new StringBuilder("namespace Fixture;\n\n");
		foreach (var (name, methods) in classes)
		{
			source.Append($"public sealed class {name}\n{{\n");
			for (var index = 0; index < methods; index++)
			{
				source.Append($"\tpublic int Method{index}(int value)\n\t{{\n");
				for (var line = 0; line < 5; line++)
					source.Append($"\t\tvalue = checked(value + {index} * {line} + 1000000000 - 2000000000 + 3000000000 - 1000000000);\n");
				source.Append("\t}\n");
			}
			source.Append("}\n");
		}
		return source.ToString();
	}

	private static Dictionary<string, object?> Range(string path, int start, int end) => new()
	{
		["path"] = path,
		["start_line"] = start,
		["end_line"] = end
	};

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
