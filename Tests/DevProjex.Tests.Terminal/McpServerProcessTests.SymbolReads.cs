namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessReadsADeclarationByNameInsteadOfAGuessedLineRange()
	{
		using var workspace = new TemporaryDirectory();
		await using var server = await StartSymbolReadProjectAsync(workspace);

		var qualified = await CallAsync(
			server,
			"get_file",
			new Dictionary<string, object?> { ["path"] = "src/App.cs", ["symbol"] = "P.App" });
		var simple = await CallAsync(
			server,
			"get_file",
			new Dictionary<string, object?> { ["path"] = "src/App.cs", ["symbol"] = "App" });

		var qualifiedText = AllProcessText(qualified);
		Assert.NotEqual(true, qualified.IsError);
		Assert.Contains("public sealed class App", qualifiedText, StringComparison.Ordinal);
		Assert.Contains("public int Run()", qualifiedText, StringComparison.Ordinal);
		// The declaration ends where it ends: the trailing file content is not part of it.
		Assert.DoesNotContain("public sealed class Sibling", qualifiedText, StringComparison.Ordinal);

		// A simple name that is unique in the file resolves to the same declaration.
		Assert.NotEqual(true, simple.IsError);
		Assert.Contains("public sealed class App", AllProcessText(simple), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessRefusesASymbolItCannotResolveToExactlyOneDeclaration()
	{
		using var workspace = new TemporaryDirectory();
		await using var server = await StartSymbolReadProjectAsync(workspace);

		var unknown = await CallAsync(
			server,
			"get_file",
			new Dictionary<string, object?> { ["path"] = "src/App.cs", ["symbol"] = "NoSuchThing" });
		var withRange = await CallAsync(
			server,
			"get_file",
			new Dictionary<string, object?>
			{
				["path"] = "src/App.cs",
				["symbol"] = "P.App",
				["start_line"] = 1
			});
		var prose = await CallAsync(
			server,
			"get_file",
			new Dictionary<string, object?> { ["path"] = "README.md", ["symbol"] = "Notes" });

		Assert.True(unknown.IsError);
		Assert.Contains(
			"'symbol' matches no declaration in this file",
			AllProcessText(unknown),
			StringComparison.Ordinal);

		Assert.True(withRange.IsError);
		Assert.Contains(
			"cannot be combined with start_line, end_line, or start_column",
			AllProcessText(withRange),
			StringComparison.Ordinal);

		Assert.True(prose.IsError);
		Assert.Contains(
			"'symbol' is not supported for this file",
			AllProcessText(prose),
			StringComparison.Ordinal);

		// The refusals report what happened in codes and counts and never echo a declaration name.
		foreach (var failure in new[] { unknown, withRange, prose })
			Assert.DoesNotContain("public sealed class", AllProcessText(failure), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessNamesTheLinesOfAnAmbiguousSymbolSoOneCanBeReadByRange()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("overload-read-project");
		workspace.WriteFile(
			"overload-read-project/src/App.cs",
			"namespace P;\n\npublic sealed class App\n{\n\tpublic string Run(int value)\n\t{\n\t\treturn \"first-overload-marker\";\n\t}\n\n\tpublic string Run(string value)\n\t{\n\t\treturn \"second-overload-marker\";\n\t}\n}\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));

		// Overloads share their qualified name, so only line ranges can tell them apart.
		var ambiguous = await CallAsync(
			server,
			"get_file",
			new Dictionary<string, object?> { ["path"] = "src/App.cs", ["symbol"] = "P.App.Run" });
		var ambiguousText = AllProcessText(ambiguous);

		Assert.True(ambiguous.IsError);
		Assert.Contains(
			"'symbol' matches 2 declarations in this file, at lines 5-8, 10-13; read one with start_line and end_line",
			ambiguousText,
			StringComparison.Ordinal);
		Assert.DoesNotContain("overload-marker", ambiguousText, StringComparison.Ordinal);
		Assert.DoesNotContain("Run(", ambiguousText, StringComparison.Ordinal);

		var first = await CallAsync(
			server,
			"get_file",
			new Dictionary<string, object?> { ["path"] = "src/App.cs", ["start_line"] = 5, ["end_line"] = 8 });
		var firstText = AllProcessText(first);

		Assert.NotEqual(true, first.IsError);
		Assert.Contains("first-overload-marker", firstText, StringComparison.Ordinal);
		Assert.DoesNotContain("second-overload-marker", firstText, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessLeavesALineAddressedReadExactlyAsItWas()
	{
		using var workspace = new TemporaryDirectory();
		await using var server = await StartSymbolReadProjectAsync(workspace);

		var byLines = await CallAsync(
			server,
			"get_file",
			new Dictionary<string, object?>
			{
				["path"] = "src/App.cs",
				["start_line"] = 3,
				["end_line"] = 6
			});
		var whole = await CallAsync(
			server,
			"get_file",
			new Dictionary<string, object?> { ["path"] = "src/App.cs" });

		Assert.NotEqual(true, byLines.IsError);
		Assert.NotEqual(true, whole.IsError);
		Assert.Contains("public sealed class App", AllProcessText(byLines), StringComparison.Ordinal);
		Assert.Contains("public sealed class Sibling", AllProcessText(whole), StringComparison.Ordinal);
	}

	private static async ValueTask<ActualMcpProcess> StartSymbolReadProjectAsync(
		TemporaryDirectory workspace)
	{
		var project = workspace.CreateDirectory("symbol-read-project");
		workspace.WriteFile(
			"symbol-read-project/src/App.cs",
			"namespace P;\n\npublic sealed class App\n{\n\tpublic int Run() => 1;\n}\n\npublic sealed class Sibling\n{\n\tpublic int Assist() => 2;\n}\n");
		workspace.WriteFile("symbol-read-project/README.md", "# Notes\n\nmarker line\n");
		return await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
	}
}
