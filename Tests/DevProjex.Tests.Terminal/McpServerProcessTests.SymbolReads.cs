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
