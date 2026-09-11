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

		// The declaration heads its hits inside the file's own block, the way the path does, and
		// location is spelled exactly once: no row anywhere repeats the path beside a line number.
		Assert.Contains("src/App.cs\nin P.App\n5:", Normalize(code), StringComparison.Ordinal);
		Assert.Contains("src/Helper.cs\nin P.Helper\n5:", Normalize(code), StringComparison.Ordinal);
		Assert.DoesNotContain("src/App.cs:5:", code, StringComparison.Ordinal);
		Assert.DoesNotContain("src/Helper.cs:5:", code, StringComparison.Ordinal);
		Assert.Contains("[Symbols] annotated=2 · files-without-declarations=0.", code, StringComparison.Ordinal);

		// A declaration name is project text, so it stays inside the untrusted block with the match
		// lines, and only the counts are trusted.
		var untrustedEnd = code.LastIndexOf("</untrusted-data-", StringComparison.Ordinal);
		Assert.True(untrustedEnd > 0);
		Assert.True(
			code.IndexOf("in P.App", StringComparison.Ordinal) < untrustedEnd,
			"The declaration name must not be reported outside the untrusted block.");
		Assert.True(code.IndexOf("[Symbols]", StringComparison.Ordinal) > untrustedEnd);
	}

	[Fact]
	public async Task RealProcessWritesOneHeaderForARunOfHitsInTheSameDeclaration()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("run-project");
		workspace.WriteFile(
			"run-project/src/App.cs",
			"namespace P;\n\npublic sealed class App\n{\n\tpublic int One() => 1;\n\n\tpublic int Two() => 2;\n}\n\npublic sealed class Other\n{\n\tpublic int Three() => 3;\n}\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var text = Normalize(AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?> { ["pattern"] = "=> [0-9]", ["context_lines"] = 0 })));

		// Two hits share a declaration and are headed once; the third changes declaration and is
		// headed again. That collapsing is the whole saving over a row per hit.
		Assert.Equal(1, CountOccurrences(text, "in P.App\n"));
		Assert.Equal(1, CountOccurrences(text, "in P.Other\n"));
		Assert.Contains("[Symbols] annotated=3 · files-without-declarations=0.", text, StringComparison.Ordinal);
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

		var prose = Normalize(AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?> { ["pattern"] = "marker", ["context_lines"] = 0 })));

		// Matches are grouped under their path, so the file heads its own block and the
		// matched line carries only its number. Nothing declares anything here, so no header
		// is written and the coverage notice says so rather than going silent.
		Assert.Contains("README.md\n3:marker line", prose, StringComparison.Ordinal);
		Assert.DoesNotContain("\nin ", prose, StringComparison.Ordinal);
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

		var wide = Normalize(AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?>
			{
				["pattern"] = "public string Needle",
				["context_lines"] = 3,
				["max_results"] = 200
			})));

		// The cap fired, so the remaining characters went to matches and no header was written at
		// all: turning naming on cannot push a capped response past the size it already had.
		Assert.Contains("[Search truncated]", wide, StringComparison.Ordinal);
		Assert.DoesNotContain("\nin P.Wide", wide, StringComparison.Ordinal);
		Assert.DoesNotContain("[Symbols]", wide, StringComparison.Ordinal);
		Assert.True(wide.Length <= 18_000, $"Capped search returned {wide.Length} characters.");
	}

	[Fact]
	public async Task RealProcessNeverEndsASearchBlockWithADeclarationHeader()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("tail-project");
		for (var file = 0; file < 30; file++)
		{
			var lines = new StringBuilder();
			lines.Append("namespace P;\n\npublic sealed class Tail");
			lines.Append(file.ToString("D2", CultureInfo.InvariantCulture));
			lines.Append("\n{\n");
			for (var member = 0; member < 12; member++)
			{
				lines.Append("\tpublic string Marker");
				lines.Append(member.ToString("D2", CultureInfo.InvariantCulture));
				lines.Append("() => \"");
				lines.Append(new string('q', 240));
				lines.Append("\";\n");
			}

			lines.Append("}\n");
			workspace.WriteFile($"tail-project/src/Tail{file:D2}.cs", lines.ToString());
		}

		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		// One search that fits and one the cap cuts: a header is never the last thing either one
		// leaves inside the untrusted block, so no caller is shown a name with nothing under it.
		foreach (var arguments in new[]
		{
			new Dictionary<string, object?> { ["pattern"] = "Marker00", ["context_lines"] = 0 },
			new Dictionary<string, object?>
			{
				["pattern"] = "public string Marker",
				["context_lines"] = 2,
				["max_results"] = 200
			}
		})
		{
			var body = SpotlightBody(Normalize(AllProcessText(await CallAsync(server, "search_project", arguments))));
			var lastLine = body
				.Split('\n', StringSplitOptions.RemoveEmptyEntries)
				.LastOrDefault(static line => line.Trim().Length > 0);
			Assert.NotNull(lastLine);
			Assert.False(
				lastLine!.StartsWith("in ", StringComparison.Ordinal),
				$"A search block ended with a declaration header: {lastLine}");
			Assert.False(
				lastLine.StartsWith("--", StringComparison.Ordinal),
				$"A search block ended with a group separator: {lastLine}");
		}
	}

	private static string Normalize(string text) =>
		text.Replace("\r\n", "\n", StringComparison.Ordinal);

	private static int CountOccurrences(string text, string value)
	{
		var count = 0;
		for (var index = text.IndexOf(value, StringComparison.Ordinal);
		     index >= 0;
		     index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
		{
			count++;
		}

		return count;
	}

	private static string SpotlightBody(string text)
	{
		var open = text.IndexOf("<untrusted-data-", StringComparison.Ordinal);
		var openEnd = open < 0 ? -1 : text.IndexOf('\n', open);
		var close = text.LastIndexOf("</untrusted-data-", StringComparison.Ordinal);
		return openEnd < 0 || close < openEnd ? text : text[(openEnd + 1)..close];
	}
}
