using System.Diagnostics;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Theory]
	[InlineData("gitignore", false, false)]
	[InlineData("tracked", false, false)]
	[InlineData("none", true, false)]
	[InlineData("unrestricted", true, false)]
	[InlineData("unrestricted", false, true)]
	public async Task RealProcessRepositoryBoundariesAgreeAcrossCliAndMcp(string mode, bool embeddedVisible, bool changes)
	{
		if (!await IsGitAvailableAsync())
			Assert.Skip("Git is not available.");
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Anchor.cs", "anchor");
		RunGit(project, "init", "--quiet");
		RunGit(project, "add", "Anchor.cs");
		var embedded = workspace.CreateDirectory("project/libs/SomeLib");
		workspace.WriteFile("project/libs/SomeLib/Embedded.cs", "embedded");
		RunGit(embedded, "init", "--quiet");
		RunGit(embedded, "add", "Embedded.cs");
		workspace.WriteFile("project/.git/info/exclude", "local/\n");
		workspace.WriteFile("project/local/Excluded.cs", "excluded");

		var cli = CreateBoundaryProcess(project, workspace.CreateDirectory("cli-state"));
		foreach (var argument in new[] { "tree", project, "--git-mode", changes ? "changes" : mode == "unrestricted" ? "none" : mode,
		         "--exclude", "none", "--format", "text" })
			cli.ArgumentList.Add(argument);
		using (var process = Process.Start(cli) ?? throw new InvalidOperationException("CLI did not start."))
		{
			process.StandardInput.Close();
			var outputTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
			var errorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
			await process.WaitForExitAsync(TestContext.Current.CancellationToken)
				.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
			var output = await outputTask;
			Assert.True(process.ExitCode == 0, await errorTask);
			Assert.Equal(embeddedVisible, output.Contains("Embedded.cs", StringComparison.Ordinal));
			Assert.Contains("Anchor.cs", output, StringComparison.Ordinal);
		}

		var server = CreateBoundaryProcess(project, workspace.CreateDirectory("mcp-state"));
		foreach (var argument in new[] { "mcp", "--root", project })
			server.ArgumentList.Add(argument);
		if (mode == "unrestricted")
			server.ArgumentList.Add("--unrestricted");
		else
		{
			server.ArgumentList.Add("--git-mode");
			server.ArgumentList.Add(mode);
			server.ArgumentList.Add("--exclude");
			server.ArgumentList.Add("none");
		}
		using var serverProcess = Process.Start(server) ?? throw new InvalidOperationException("MCP did not start.");
		var serverErrors = serverProcess.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(60));
		await using (var client = await McpClient.CreateAsync(new StreamClientTransport(
			serverProcess.StandardInput.BaseStream, serverProcess.StandardOutput.BaseStream),
			clientOptions: null, loggerFactory: null, timeout.Token))
		{
			var arguments = new Dictionary<string, object?> { ["format"] = "text" };
			if (changes)
				arguments["git_scope"] = "changes";
			var response = await client.CallToolAsync("get_tree", arguments, progress: null, options: null, timeout.Token);
			Assert.NotEqual(true, response.IsError);
			var output = string.Join("\n", response.Content.OfType<TextContentBlock>().Select(block => block.Text));
			Assert.Equal(embeddedVisible, output.Contains("Embedded.cs", StringComparison.Ordinal));
			Assert.Contains("Anchor.cs", output, StringComparison.Ordinal);
			Assert.Contains("[Effective filters]", output, StringComparison.Ordinal);
			Assert.Equal(mode is "none" or "unrestricted" && !changes,
				output.Contains("Excluded.cs", StringComparison.Ordinal));
		}
		serverProcess.StandardInput.Close();
		await serverProcess.WaitForExitAsync(timeout.Token);
		Assert.True(serverProcess.ExitCode == 0, await serverErrors);
	}

	private static ProcessStartInfo CreateBoundaryProcess(string project, string stateRoot)
	{
		var start = new ProcessStartInfo(PublishedApplicationLocator.FindExecutable())
		{
			WorkingDirectory = project, UseShellExecute = false, CreateNoWindow = true,
			RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
		};
		start.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = stateRoot;
		return start;
	}
}
