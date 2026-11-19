using System.Globalization;
using System.Text;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	private const int TruncatedBodyHeadLimit = 400;

	[Fact]
	public async Task SearchShowsOnlyThePythonSignatureAndDocstringOfALongFunction()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var source = new StringBuilder("import functools\n\n\n@functools.lru_cache(maxsize=None)\ndef long_python_function(\n" +
			"    alpha: int,\n    beta: dict[str, int] = {},\n) -> int:\n    \"\"\"Add every value the caller passes.\n\n" +
			"    More words follow here.\n    \"\"\"\n    total = alpha\n");
		AppendFiller(source, "    total += beta.get(\"key{0:D3}\", 0)  # python-head-filler\n");
		source.Append("    return total\n");
		WriteProjectFile(project, "pkg/long_module.py", source.ToString());
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var text = await SearchTextAsync(server, "def long_python_function");

		var head = BestDeclarationHead(text);
		Assert.Equal(
			"def long_python_function(\n    alpha: int,\n    beta: dict[str, int] = {},\n) -> int:\n" +
			"    \"\"\"Add every value the caller passes.",
			head);
		AssertTruncatedHead(text, head, startLine: 5, endLine: 134,
			"get_file {\"path\":\"pkg/long_module.py\",\"symbol\":\"long_python_function\"}");
		Assert.DoesNotContain("python-head-filler", head, StringComparison.Ordinal);
	}

	[Fact]
	public async Task SearchShowsOnlyTheCSharpSignatureWithItsAttributeOfALongMethod()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var source = new StringBuilder("namespace Demo;\n\npublic sealed class Worker\n{\n    /// <summary>Computes a total.</summary>\n" +
			"    [Obsolete(\"Use the new one\")]\n    public int ComputeLongTotal(\n        int alpha,\n        int[] values)\n    {\n" +
			"        var total = alpha;\n");
		AppendFiller(source, "        total += values[{0}]; // csharp-head-filler\n");
		source.Append("        return total;\n    }\n}\n");
		WriteProjectFile(project, "src/Worker.cs", source.ToString());
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var text = await SearchTextAsync(server, "ComputeLongTotal");

		var head = BestDeclarationHead(text);
		Assert.Equal(
			"    [Obsolete(\"Use the new one\")]\n    public int ComputeLongTotal(\n        int alpha,\n        int[] values)\n    {",
			head);
		AssertTruncatedHead(text, head, startLine: 6, endLine: 133,
			"get_file {\"path\":\"src/Worker.cs\",\"symbol\":\"Demo.Worker.ComputeLongTotal\"}");
	}

	[Fact]
	public async Task SearchShowsOnlyTheTypeScriptSignatureOfALongFunction()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var source = new StringBuilder("export function longTypeScriptFunction(\n  alpha: number,\n  options: { beta: string; gamma: number },\n" +
			"): number {\n  let total = alpha;\n");
		AppendFiller(source, "  total += options.gamma * {0}; // typescript-head-filler\n");
		source.Append("  return total;\n}\n");
		WriteProjectFile(project, "src/long.ts", source.ToString());
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var text = await SearchTextAsync(server, "longTypeScriptFunction");

		var head = BestDeclarationHead(text);
		Assert.Equal(
			"export function longTypeScriptFunction(\n  alpha: number,\n  options: { beta: string; gamma: number },\n): number {",
			head);
		AssertTruncatedHead(text, head, startLine: 1, endLine: 127,
			"get_file {\"path\":\"src/long.ts\",\"symbol\":\"longTypeScriptFunction\"}");
	}

	[Fact]
	public async Task SearchKeepsAFittingDeclarationBodyWhole()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		const string function = "def short_function(value):\n    \"\"\"Double it.\"\"\"\n    doubled = value * 2\n    return doubled";
		WriteProjectFile(project, "pkg/short_module.py", "import os\n\n\n" + function + "\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var text = await SearchTextAsync(server, "def short_function");

		Assert.Contains("\nlines 4-7\n" + function + "\n", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Declaration body truncated", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task SearchCutsAnOverlongSignatureAtAWholeLineWithinTheHeadLimit()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var source = new StringBuilder("def overlong_signature(\n");
		for (var parameter = 0; parameter < 40; parameter++)
			source.Append("    parameter_number_").Append(parameter.ToString("D2", CultureInfo.InvariantCulture)).Append(": int = 0,\n");
		source.Append("):\n    total = 0\n");
		AppendFiller(source, "    total += {0}\n");
		source.Append("    return total\n");
		WriteProjectFile(project, "pkg/overlong.py", source.ToString());
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var text = await SearchTextAsync(server, "def overlong_signature");

		var head = BestDeclarationHead(text);
		Assert.StartsWith("def overlong_signature(\n    parameter_number_00: int = 0,", head, StringComparison.Ordinal);
		Assert.EndsWith(": int = 0,", head, StringComparison.Ordinal);
		Assert.True(head.Length + "    parameter_number_00: int = 0,".Length + 1 > TruncatedBodyHeadLimit, head);
		AssertTruncatedHead(text, head, startLine: 1, endLine: 164,
			"get_file {\"path\":\"pkg/overlong.py\",\"symbol\":\"overlong_signature\"}");
	}

	[Fact]
	public async Task SearchKeepsProtectedTextMaskedInATruncatedHead()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var source = new StringBuilder("def connect_with_token(\n    token=\"" + Secret + "\",\n):\n    session = token\n");
		AppendFiller(source, "    session += \"{0}\"\n");
		source.Append("    return session\n");
		WriteProjectFile(project, "pkg/connect.py", source.ToString());
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var text = await SearchTextAsync(server, "def connect_with_token");

		var head = BestDeclarationHead(text);
		Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[github-pat#1]", head, StringComparison.Ordinal);
		Assert.EndsWith("):", head, StringComparison.Ordinal);
		AssertTruncatedHead(text, head, startLine: 1, endLine: 125,
			"get_file {\"path\":\"pkg/connect.py\",\"symbol\":\"connect_with_token\"}");
	}

	private static void AppendFiller(StringBuilder source, string lineFormat)
	{
		for (var line = 0; line < 120; line++)
			source.Append(string.Format(CultureInfo.InvariantCulture, lineFormat, line));
	}

	private static async Task<string> SearchTextAsync(McpTestServer server, string pattern)
	{
		var result = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = pattern,
			["context_lines"] = 0
		});
		Assert.NotEqual(true, result.IsError);
		return Text(result).Replace("\r\n", "\n", StringComparison.Ordinal);
	}

	private static string BestDeclarationHead(string text)
	{
		var heading = text.IndexOf("Best declaration body (1 of ", StringComparison.Ordinal);
		Assert.True(heading >= 0, text);
		var lines = text.IndexOf("\nlines ", heading, StringComparison.Ordinal);
		var start = text.IndexOf('\n', lines + 1) + 1;
		var truncated = text.IndexOf("\n[Declaration body truncated:", start, StringComparison.Ordinal);
		Assert.True(truncated >= 0, text);
		return text[start..truncated];
	}

	private static void AssertTruncatedHead(string text, string head, int startLine, int endLine, string readRoute)
	{
		Assert.InRange(head.Length, 1, TruncatedBodyHeadLimit);
		var shownLines = head.Split('\n').Length;
		var remaining = endLine - startLine + 1 - shownLines;
		var notice = string.Create(
			CultureInfo.InvariantCulture,
			$"[Declaration body truncated: {remaining} line(s) remain; read lines {startLine + shownLines}-{endLine} with get_file.]");
		Assert.Contains(notice, text, StringComparison.Ordinal);
		Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"\nlines {startLine}-{endLine}\n"), text, StringComparison.Ordinal);
		Assert.Contains(readRoute, text, StringComparison.Ordinal);

		var opening = text.IndexOf("<untrusted-data-", StringComparison.Ordinal);
		var closing = text.LastIndexOf("</untrusted-data-", StringComparison.Ordinal);
		Assert.True(opening >= 0 && closing > opening, text);
		foreach (var item in new[] { head, notice, readRoute })
		{
			var position = text.IndexOf(item, StringComparison.Ordinal);
			Assert.InRange(position, opening + 1, closing - 1);
			Assert.InRange(text.LastIndexOf(item, StringComparison.Ordinal), opening + 1, closing - 1);
		}
	}
}
