using System.Globalization;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessNamesTheDeclarationEachSearchHitSitsInsideWithoutBeingAsked()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("symbol-project");
		workspace.WriteFile(
			"symbol-project/src/App.cs",
			"namespace P;\n\npublic sealed class App\n{\n\tpublic int Run() => 1;\n}\n");
		workspace.WriteFile(
			"symbol-project/src/Helper.cs",
			"namespace P;\n\npublic sealed class Helper\n{\n\tpublic int Assist() => 2;\n}\n");
		workspace.WriteFile("symbol-project/README.md", "# Notes\n\nmarker line\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		// No flag is passed: naming is what the tool does.
		var code = AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?> { ["pattern"] = "=> [0-9]", ["context_lines"] = 0 }));

		Assert.Contains("src/App.cs:5:", code, StringComparison.Ordinal);
		Assert.Contains("src/Helper.cs:5:", code, StringComparison.Ordinal);
		Assert.Contains("Enclosing declarations:", code, StringComparison.Ordinal);
		Assert.Contains("src/App.cs:5: P.App", code, StringComparison.Ordinal);
		Assert.Contains("src/Helper.cs:5: P.Helper", code, StringComparison.Ordinal);
		Assert.Contains("[Symbols] annotated=2 · files-without-declarations=0.", code, StringComparison.Ordinal);

		// A declaration name is project text, so it stays inside the untrusted block with the match
		// lines, and only the counts are trusted.
		var untrustedEnd = code.LastIndexOf("</untrusted-data-", StringComparison.Ordinal);
		Assert.True(untrustedEnd > 0);
		Assert.True(
			code.IndexOf("src/App.cs:5: P.App", StringComparison.Ordinal) < untrustedEnd,
			"The declaration name must not be reported outside the untrusted block.");
		Assert.True(code.IndexOf("[Symbols]", StringComparison.Ordinal) > untrustedEnd);
	}

	[Fact]
	public async Task RealProcessCountsAHitInAFileThatDeclaresNothingInsteadOfNamingIt()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("prose-project");
		workspace.WriteFile("prose-project/README.md", "# Notes\n\nmarker line\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var prose = AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?> { ["pattern"] = "marker", ["context_lines"] = 0 }));

		// Matches are grouped under their path, so the file heads its own block and the
		// matched line carries only its number.
		Assert.Contains("README.md", prose, StringComparison.Ordinal);
		Assert.Contains("3:marker line", prose, StringComparison.Ordinal);
		Assert.DoesNotContain("Enclosing declarations:", prose, StringComparison.Ordinal);
		Assert.Contains("[Symbols] annotated=0 · files-without-declarations=1.", prose, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessSpendsNoResponseCharactersOnNamingWhenTheSearchCapAlreadyFired()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("wide-symbol-project");
		for (var file = 0; file < 40; file++)
		{
			var lines = new StringBuilder();
			lines.Append("namespace P;\n\npublic sealed class Wide");
			lines.Append(file.ToString("D2", CultureInfo.InvariantCulture));
			lines.Append("\n{\n");
			for (var member = 0; member < 20; member++)
			{
				// Long enough that twenty of them in forty files cannot fit the search cap.
				lines.Append("\tpublic string Needle");
				lines.Append(member.ToString("D2", CultureInfo.InvariantCulture));
				lines.Append("() => \"");
				lines.Append(new string('p', 280));
				lines.Append("\";\n");
			}

			lines.Append("}\n");
			workspace.WriteFile(
				$"wide-symbol-project/src/Wide{file:D2}.cs",
				lines.ToString());
		}

		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var wide = AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?>
			{
				["pattern"] = "public string Needle",
				["context_lines"] = 3,
				["max_results"] = 200
			}));

		// The cap fired, so the response says so and carries no naming at all: turning naming on
		// cannot push a capped response past the size it already had.
		Assert.Contains("[Search truncated]", wide, StringComparison.Ordinal);
		Assert.DoesNotContain("Enclosing declarations:", wide, StringComparison.Ordinal);
		Assert.DoesNotContain("[Symbols]", wide, StringComparison.Ordinal);
		Assert.True(wide.Length <= 18_000, $"Capped search returned {wide.Length} characters.");
	}
}
