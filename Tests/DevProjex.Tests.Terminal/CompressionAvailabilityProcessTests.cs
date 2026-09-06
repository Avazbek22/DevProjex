using System.Diagnostics;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class CompressionAvailabilityProcessTests
{
	[Fact(Timeout = 120_000)]
	public async Task TuiNotifiesAndMarksMetricsWhenContentGrammarsAreMissing()
	{
		using var workspace = new TemporaryDirectory();
		var host = CopyDesktopHostWithEmptyGrammarDirectory(workspace);
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile(
			"project/App.cs",
			"internal sealed class App { public string Run() { return \"tui-kept-full\"; } }\n");
		var profile = workspace.WriteFile(
			"compression-profile.json",
			"""
			{
			  "schemaVersion": 1,
			  "selection": {
			    "roots": null,
			    "extensions": [".cs"],
			    "selectedPaths": null,
			    "gitMode": "none",
			    "exclusions": [],
			    "compressCode": true
			  }
			}
			""");
		var executable = OperatingSystem.IsWindows()
			? Path.ChangeExtension(host, ".exe")
			: Path.Combine(Path.GetDirectoryName(host)!, "DevProjex");
		await using var terminal = await TerminalPtyHarness.StartAsync(
			project,
			[
				"tui", project,
				"--screen", "inline",
				"--no-mouse",
				"--plain",
				"--language", "en",
				"--profile", profile
			],
			columns: 180,
			rows: 34,
			cancellationToken: TestContext.Current.CancellationToken,
			binaryOverride: executable);

		await terminal.WaitForScreenAsync(
			"> PROJECT TREE",
			timeout: TimeSpan.FromSeconds(45),
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("2", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Compression unavailable:",
			timeout: TimeSpan.FromSeconds(45),
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("grammars", terminal.CaptureScreen(), StringComparison.OrdinalIgnoreCase);
		await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Compression unavailable",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact]
	public void CliReportsMissingContentGrammarsInStderrAndStructuredDiagnostics()
	{
		using var workspace = new TemporaryDirectory();
		var host = CopyDesktopHostWithEmptyGrammarDirectory(workspace);
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile(
			"project/App.cs",
			"internal sealed class App { public string Run() { return \"kept-full\"; } }\n");
		var dataRoot = workspace.CreateDirectory("data");

		var analysis = RunCli(
			host,
			dataRoot,
			"--language", "en",
			"analyze", project,
			"--format", "json",
			"--plain",
			"--git-mode", "none",
			"--exclude", "none",
			"--compress-code",
			"--strict");

		Assert.Equal(0, analysis.ExitCode);
		Assert.Contains("DPX-COMPRESSION-UNAVAILABLE", analysis.StandardError, StringComparison.Ordinal);
		Assert.Contains("grammars", analysis.StandardError, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("\"code\": \"DPX-COMPRESSION-UNAVAILABLE\"", analysis.StandardOutput, StringComparison.Ordinal);

		// A non-empty delivery source makes this a language-specific missing grammar rather than
		// the global empty-directory case exercised above. Export must discover it before JSON is written.
		File.WriteAllText(
			Path.Combine(Path.GetDirectoryName(host)!, "grammars", "delivery-marker.txt"),
			"not a grammar");
		var context = RunCli(
			host,
			dataRoot,
			"--language", "en",
			"export", "context", project,
			"--view", "content",
			"--format", "json",
			"--git-mode", "none",
			"--exclude", "none",
			"--compress-code",
			"--plain",
			"-o", "-");

		Assert.Equal(0, context.ExitCode);
		Assert.Contains("DPX-COMPRESSION-UNAVAILABLE", context.StandardError, StringComparison.Ordinal);
		Assert.Contains("language 'csharp'", context.StandardError, StringComparison.Ordinal);
		Assert.Contains("DPX-COMPRESSION-UNAVAILABLE", context.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("kept-full", context.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealMcpProcessReportsMissingContentGrammarsOutsideUntrustedData()
	{
		using var workspace = new TemporaryDirectory();
		var host = CopyDesktopHostWithEmptyGrammarDirectory(workspace);
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile(
			"project/App.cs",
			"internal sealed class App { public string Run() { return \"mcp-kept-full\"; } }\n");

		var startInfo = CreateStartInfo(host, workspace.CreateDirectory("data"));
		startInfo.ArgumentList.Add("mcp");
		startInfo.ArgumentList.Add("--root");
		startInfo.ArgumentList.Add(project);
		startInfo.ArgumentList.Add("--git-mode");
		startInfo.ArgumentList.Add("none");
		using var process = Process.Start(startInfo) ??
		                    throw new InvalidOperationException("MCP process did not start.");
		var standardErrorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		timeout.CancelAfter(TimeSpan.FromMinutes(2));
		await using (var client = await McpClient.CreateAsync(
			new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream),
			clientOptions: null,
			loggerFactory: null,
			timeout.Token))
		{
			var tools = await client.ListToolsAsync(options: null, timeout.Token);
			var analysisSchema = Assert.IsType<JsonElement>(
				tools.Single(static tool => tool.Name == "analyze").ProtocolTool.OutputSchema);
			Assert.True(
				analysisSchema.GetProperty("properties").TryGetProperty("compressionUnavailable", out _));

			var analysis = await client.CallToolAsync(
				"analyze",
				new Dictionary<string, object?> { ["detail"] = "signatures" },
				progress: null,
				options: null,
				timeout.Token);
			var analysisText = string.Join(
				"\n",
				analysis.Content.OfType<TextContentBlock>().Select(static block => block.Text));
			Assert.Contains("[Compression unavailable]", analysisText, StringComparison.Ordinal);
			Assert.True(analysisText.LastIndexOf("[Compression unavailable]", StringComparison.Ordinal) >
			            analysisText.LastIndexOf("</untrusted-data-", StringComparison.Ordinal));
			var unavailable = analysis.StructuredContent!.Value.GetProperty("compressionUnavailable");
			Assert.Contains("grammars", unavailable.GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);

			var pack = await client.CallToolAsync(
				"pack_context",
				new Dictionary<string, object?>
				{
					["detail"] = "signatures",
					["view"] = "content",
					["format"] = "text"
				},
				progress: null,
				options: null,
				timeout.Token);
			var packText = string.Join(
				"\n",
				pack.Content.OfType<TextContentBlock>().Select(static block => block.Text));
			Assert.Contains("mcp-kept-full", packText, StringComparison.Ordinal);
			Assert.Contains("[Compression unavailable]", packText, StringComparison.Ordinal);
			Assert.True(packText.LastIndexOf("[Compression unavailable]", StringComparison.Ordinal) >
			            packText.LastIndexOf("</untrusted-data-", StringComparison.Ordinal));
		}

		process.StandardInput.Close();
		await process.WaitForExitAsync(timeout.Token)
			.WaitAsync(TimeSpan.FromSeconds(15), timeout.Token);
		var standardError = await standardErrorTask;
		Assert.Equal(0, process.ExitCode);
		Assert.True(string.IsNullOrWhiteSpace(standardError), standardError);
	}

	private static string CopyDesktopHostWithEmptyGrammarDirectory(TemporaryDirectory workspace)
	{
		var sourceAssembly = PublishedApplicationLocator.FindApplicationAssembly();
		var sourceDirectory = Path.GetDirectoryName(sourceAssembly)!;
		var destinationDirectory = workspace.CreateDirectory("host");
		foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
		{
			var relative = Path.GetRelativePath(sourceDirectory, source);
			if (relative.Equals("grammars", StringComparison.OrdinalIgnoreCase) ||
			    relative.StartsWith($"grammars{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			var destination = Path.Combine(destinationDirectory, relative);
			Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
			File.Copy(source, destination);
		}
		Directory.CreateDirectory(Path.Combine(destinationDirectory, "grammars"));
		return Path.Combine(destinationDirectory, Path.GetFileName(sourceAssembly));
	}

	private static TerminalTestProcessResult RunCli(
		string host,
		string dataRoot,
		params string[] arguments)
	{
		var startInfo = CreateStartInfo(host, dataRoot);
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		return TerminalTestProcess.Run(startInfo, TimeSpan.FromMinutes(1));
	}

	private static ProcessStartInfo CreateStartInfo(string host, string dataRoot)
	{
		var startInfo = new ProcessStartInfo("dotnet")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WorkingDirectory = Path.GetDirectoryName(host)!
		};
		startInfo.ArgumentList.Add(host);
		startInfo.Environment[InvocationEnvironment.TerminalHostVariable] = "1";
		startInfo.Environment[InvocationEnvironment.InternalDataRootVariable] = dataRoot;
		startInfo.Environment["DOTNET_NOLOGO"] = "1";
		return startInfo;
	}
}
