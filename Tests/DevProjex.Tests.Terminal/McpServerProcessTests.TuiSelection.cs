using DevProjex.Infrastructure.ProjectProfiles;
using DevProjex.Kernel.Models;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact(Timeout = 150_000)]
	public async Task TuiSelectPersistsTheLiveContextWindowReadByTheServer()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/global.json", "{}");
		workspace.WriteFile("project/src/Inside.cs", "class Inside { }");
		workspace.WriteFile("project/docs/Outside.cs", "class Outside { }");
		string? dataRoot = null;
		await using var terminal = await TerminalPtyHarness.StartAsync(
			project,
			["tui", project, "--profile", "standard", "--screen", "inline", "--no-mouse", "--language", "en"],
			columns: 160,
			rows: 40,
			cancellationToken: TestContext.Current.CancellationToken,
			initializeDataRoot: root => dataRoot = root);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync(":select src on\r", TestContext.Current.CancellationToken);
		var selectionResult = await terminal.WaitForScreenAsync(
			"paths not found",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.DoesNotContain("Selection: 0 nodes changed", selectionResult, StringComparison.Ordinal);
		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				timeout: TimeSpan.FromSeconds(30),
				cancellationToken: TestContext.Current.CancellationToken));

		Assert.NotNull(dataRoot);
		var stored = new ProjectProfileStore(() => dataRoot!).LookupProfile(
			project,
			TimeSpan.FromSeconds(5));
		Assert.Equal(ProjectProfileLookupStatus.Found, stored.Status);
		Assert.Equal(["src"], stored.Profile!.SelectedPaths);
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			dataRoot!,
			arguments: ["--live"]);
		var tree = await server.Client.CallToolAsync(
			"get_tree",
			new Dictionary<string, object?> { ["format"] = "text" },
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var text = string.Join(
			"\n",
			tree.Content.OfType<TextContentBlock>().Select(static block => block.Text));

		Assert.NotEqual(true, tree.IsError);
		Assert.Contains("src", text, StringComparison.Ordinal);
		Assert.Contains("Inside.cs", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Outside.cs", text, StringComparison.Ordinal);
		Assert.Contains("[Live context]", text, StringComparison.Ordinal);
	}
}
