using System.Globalization;
using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessPagesAWithheldSearchWithoutSearchingTheProjectAgain()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("withheld-project");
		for (var file = 0; file < 6; file++)
		{
			var lines = new StringBuilder();
			lines.Append("namespace P;\n\npublic sealed class File");
			lines.Append(file.ToString("D2", CultureInfo.InvariantCulture));
			lines.Append("\n{\n");
			for (var member = 0; member < 10; member++)
			{
				lines.Append("\tpublic int Needle");
				lines.Append(member.ToString("D2", CultureInfo.InvariantCulture));
				lines.Append("() => ");
				lines.Append(member.ToString(CultureInfo.InvariantCulture));
				lines.Append(";\n");
			}

			lines.Append("}\n");
			workspace.WriteFile($"withheld-project/src/File{file:D2}.cs", lines.ToString());
		}

		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var searched = Normalize(AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?>
			{
				["pattern"] = "public int Needle",
				["context_lines"] = 0,
				["max_results"] = 5
			})));

		// The five matches max_results allows are spread one per file, so five of the six files are
		// named rather than the first file being exhausted alphabetically.
		foreach (var file in new[] { "File00", "File01", "File02", "File03", "File04" })
			Assert.Contains($"src/{file}.cs\n", searched, StringComparison.Ordinal);
		Assert.Contains("Withheld matches by file:", searched, StringComparison.Ordinal);
		// The count leads, so a path with spaces or trailing digits stays unambiguous. One hit of
		// this file was shown, so nine of its ten are withheld.
		Assert.Contains("9 src/File01.cs", searched, StringComparison.Ordinal);
		Assert.Contains("10 src/File05.cs", searched, StringComparison.Ordinal);
		Assert.Contains("[55 additional observed matches not shown", searched, StringComparison.Ordinal);
		Assert.Contains("[Search observed] matches=60 · matching-files=6 within inspected sources", searched, StringComparison.Ordinal);

		var stored = Regex.Match(searched, @"\[Search stored\] pack_id=([0-9a-f]+) · matches=(\d+) · files=(\d+);");
		Assert.True(stored.Success, searched);
		// Every file withheld something, so every file is stored whole: the store carries all 60,
		// which is what makes a page of it readable rather than a set of fragments.
		Assert.Equal(60, int.Parse(stored.Groups[2].Value, CultureInfo.InvariantCulture));
		Assert.Equal(6, int.Parse(stored.Groups[3].Value, CultureInfo.InvariantCulture));

		// Nothing may be read from the project again. If paging still answers in full, the only
		// scan this result can have come from is the one the search already ran.
		Directory.Delete(Path.Combine(project, "src"), recursive: true);

		var paged = await CallAsync(
			server,
			"read_pack",
			new Dictionary<string, object?> { ["pack_id"] = stored.Groups[1].Value });
		var pagedText = Normalize(AllProcessText(paged));

		Assert.NotEqual(true, paged.IsError);
		Assert.Contains("src/File01.cs\n5:", pagedText, StringComparison.Ordinal);
		Assert.Contains("src/File05.cs\n", pagedText, StringComparison.Ordinal);
		Assert.Contains("public int Needle09() => 9;", pagedText, StringComparison.Ordinal);
		// Paged search text is project data and stays inside the untrusted block.
		Assert.Contains("<untrusted-data-", pagedText, StringComparison.Ordinal);

		// A second search over the deleted sources finds nothing, which is what makes the point:
		// the page above could not have been produced by searching again.
		var rescanned = Normalize(AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?>
			{
				["pattern"] = "public int Needle",
				["context_lines"] = 0,
				["max_results"] = 5
			})));
		Assert.DoesNotContain("public int Needle09", rescanned, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessStoresNothingForASearchThatReturnedEverythingItFound()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("complete-project");
		workspace.WriteFile(
			"complete-project/src/App.cs",
			"namespace P;\n\npublic sealed class App\n{\n\tpublic int Run() => 1;\n}\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var text = Normalize(AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?> { ["pattern"] = "=> 1", ["context_lines"] = 0 })));

		// Nothing was withheld, so nothing is stored and nothing new is said.
		Assert.Contains("src/App.cs\nin P.App.Run\n5:", text, StringComparison.Ordinal);
		Assert.DoesNotContain("[Search stored]", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Withheld matches by file:", text, StringComparison.Ordinal);
		Assert.DoesNotContain("additional matches", text, StringComparison.Ordinal);
		Assert.DoesNotContain("[Search observed]", text, StringComparison.Ordinal);
	}

}
