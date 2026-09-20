using DevProjex.Application.Services;
using DevProjex.Infrastructure.TerminalCommands;

namespace DevProjex.Tests.Unit;

public sealed class McpConnectionServiceTests
{
	[Theory]
	[InlineData(".exe")]
	[InlineData(".cmd")]
	public void ExecutableLocator_FindsWindowsExecutableAndCommandShim(string suffix)
	{
		using var temp = new TemporaryDirectory();
		var expected = Path.GetFullPath(Path.Combine(temp.Path, "claude" + suffix));
		var locator = new McpClientExecutableLocator(new McpClientExecutableLocatorOptions
		{
			Platform = TerminalCommandHostPlatform.Windows,
			PathVariableProvider = () => temp.Path,
			PathExtensionsProvider = () => ".CMD;.EXE",
			FileExists = path => string.Equals(path, expected, StringComparison.OrdinalIgnoreCase)
		});

		Assert.Equal(expected, locator.Find("claude"));
	}

	[Fact]
	public void ExecutableLocator_FindsExtensionlessUnixShim()
	{
		const string directory = "/client-tools";
		var expected = Path.GetFullPath(Path.Combine(directory, "codex"));
		var locator = new McpClientExecutableLocator(new McpClientExecutableLocatorOptions
		{
			Platform = TerminalCommandHostPlatform.Linux,
			PathVariableProvider = () => directory,
			FileExists = path => string.Equals(path, expected, StringComparison.Ordinal),
			IsExecutable = path => string.Equals(path, expected, StringComparison.Ordinal)
		});

		Assert.Equal(expected, locator.Find("codex"));
	}

	[Fact]
	public void ExecutableLocator_SkipsNonExecutableUnixCandidate()
	{
		const string firstDirectory = "client-a";
		const string secondDirectory = "client-b";
		var first = Path.GetFullPath(Path.Combine(firstDirectory, "codex"));
		var expected = Path.GetFullPath(Path.Combine(secondDirectory, "codex"));
		var locator = new McpClientExecutableLocator(new McpClientExecutableLocatorOptions
		{
			Platform = TerminalCommandHostPlatform.Linux,
			PathVariableProvider = () => $"{firstDirectory}:{secondDirectory}",
			FileExists = path => path.Equals(first, StringComparison.Ordinal) ||
								 path.Equals(expected, StringComparison.Ordinal),
			IsExecutable = path => path.Equals(expected, StringComparison.Ordinal)
		});

		Assert.Equal(expected, locator.Find("codex"));
	}

	[Fact]
	public async Task ProcessRunner_ExecutesWindowsCommandShimFromQuotedPath()
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		var shimDirectory = Path.Combine(temp.Path, "client tools Юникод");
		Directory.CreateDirectory(shimDirectory);
		var probePath = Path.Combine(shimDirectory, "probe.ps1");
		await File.WriteAllTextAsync(
			probePath,
			"[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)\r\n" +
			"foreach ($value in $args) { [Console]::Out.WriteLine($value) }\r\n",
			TestContext.Current.CancellationToken);
		var shimPath = Path.Combine(shimDirectory, "probe.cmd");
		await File.WriteAllTextAsync(
			shimPath,
			"@echo off\r\n" +
			"pwsh -NoLogo -NoProfile -NonInteractive -File \"%~dp0probe.ps1\" %*\r\n",
			TestContext.Current.CancellationToken);
		var runner = new McpConnectionProcessRunner();
		var arguments = new[]
		{
			"alpha",
			"root with spaces Юникод",
			@"C:\path\with\slashes",
			@"C:\100%\%PATH%\root",
			"ampersand & caret ^ parentheses () exclamation !"
		};

		var result = await runner.RunAsync(
			new McpConnectionProcessRequest(
				shimPath,
				arguments,
				temp.Path,
				TimeSpan.FromSeconds(10)),
			TestContext.Current.CancellationToken);

		Assert.True(result.Succeeded, result.CombinedOutput);
		Assert.Equal(
			arguments,
			result.StandardOutput
				.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
	}

	[Fact]
	public async Task ProcessRunner_TimeoutPreservesCapturedOutput()
	{
		using var temp = new TemporaryDirectory();
		string executable;
		IReadOnlyList<string> arguments;
		if (OperatingSystem.IsWindows())
		{
			executable = temp.CreateFile("wait.cmd", "@echo off\r\necho before-timeout\r\nset /p DPX_WAIT=\r\n");
			arguments = [];
		}
		else
		{
			executable = "/bin/sh";
			arguments = ["-c", "printf 'before-timeout\\n'; read dpx_wait"];
		}
		var runner = new McpConnectionProcessRunner();

		var result = await runner.RunAsync(
			new McpConnectionProcessRequest(executable, arguments, temp.Path, TimeSpan.FromSeconds(1)),
			TestContext.Current.CancellationToken);

		Assert.True(result.TimedOut);
		Assert.Contains("before-timeout", result.StandardOutput, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(
		(int)McpConnectionClient.ClaudeCode,
		"claude",
		"No local-scoped MCP server found with name: devprojex")]
	[InlineData(
		(int)McpConnectionClient.ClaudeCode,
		"claude",
		"No MCP server named \"devprojex\" in local scope")]
	[InlineData(
		(int)McpConnectionClient.Codex,
		"codex",
		"No MCP server named 'devprojex' found.")]
	public async Task Connect_CommandLineClient_InspectsThenAddsWithExactArguments(
		int clientValue,
		string commandName,
		string missingServerMessage)
	{
		using var project = new TemporaryDirectory();
		var client = (McpConnectionClient)clientValue;
		var runner = new RecordingProcessRunner(
			new McpConnectionProcessResult(1, string.Empty, missingServerMessage),
			new McpConnectionProcessResult(0, "added", string.Empty));
		var (service, clientExecutable) = CreateCommandLineService(project.Path, commandName, runner);
		var devProjexExecutable = Path.Combine(project.Path, "DevProjex.exe");

		var result = await service.ConnectAsync(
			Request(client, McpConnectionMode.Live, devProjexExecutable, project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.Connected, result.Status);
		Assert.False(result.Replaced);
		Assert.Equal(commandName, result.NextCommand);
		Assert.Equal(2, runner.Requests.Count);
		var expectedGetArguments = client == McpConnectionClient.ClaudeCode
			? new[] { "mcp", "get", "devprojex" }
			: ["mcp", "get", "devprojex", "--json"];
		AssertProcessRequest(
			runner.Requests[0],
			clientExecutable,
			project.Path,
			expectedGetArguments);
		var expectedAddArguments = client == McpConnectionClient.ClaudeCode
			? new[]
			{
				"mcp", "add", "--scope", "local", "devprojex", "--", devProjexExecutable,
				"mcp", "--root", project.Path, "--live"
			}
			: [
				"mcp", "add", "devprojex", "--", devProjexExecutable,
				"mcp", "--root", project.Path, "--live"
			];
		AssertProcessRequest(
			runner.Requests[1],
			clientExecutable,
			project.Path,
			expectedAddArguments);
		Assert.Contains($"get: {missingServerMessage}", result.CommandOutput, StringComparison.Ordinal);
		Assert.Contains("add: added", result.CommandOutput, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Connect_CommandLineClient_ReportsUpdatedAfterSuccessfulRemoval()
	{
		using var project = new TemporaryDirectory();
		var runner = new RecordingProcessRunner(
			ClaudeConnection(Path.Combine(project.Path, "old.exe"), project.Path, live: true),
			new McpConnectionProcessResult(0, "removed", string.Empty),
			new McpConnectionProcessResult(0, "added", string.Empty));
		var (service, _) = CreateCommandLineService(project.Path, "claude", runner);

		var result = await service.ConnectAsync(
			Request(
				McpConnectionClient.ClaudeCode,
				McpConnectionMode.Standard,
				Path.Combine(project.Path, "DevProjex.exe"),
				project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.Updated, result.Status);
		Assert.True(result.Succeeded);
		Assert.True(result.Replaced);
		Assert.DoesNotContain("--live", runner.Requests[2].Arguments);
	}

	[Fact]
	public async Task Connect_Codex_MissingServerWithZeroExitCodeReportsNewConnection()
	{
		using var project = new TemporaryDirectory();
		var runner = new RecordingProcessRunner(
			new McpConnectionProcessResult(0, "No MCP server named 'devprojex' found.", string.Empty),
			new McpConnectionProcessResult(0, "Added global MCP server 'devprojex'.", string.Empty));
		var (service, _) = CreateCommandLineService(project.Path, "codex", runner);

		var result = await service.ConnectAsync(
			Request(
				McpConnectionClient.Codex,
				McpConnectionMode.Standard,
				Path.Combine(project.Path, "DevProjex.exe"),
				project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.Connected, result.Status);
		Assert.False(result.Replaced);
	}

	[Fact]
	public async Task Connect_CommandLineClient_DoesNotTreatUnrelatedNotFoundErrorAsMissingServer()
	{
		using var project = new TemporaryDirectory();
		var runner = new RecordingProcessRunner(
			new McpConnectionProcessResult(127, string.Empty, "node: not found"));
		var (service, _) = CreateCommandLineService(project.Path, "claude", runner);

		var result = await service.ConnectAsync(
			Request(
				McpConnectionClient.ClaudeCode,
				McpConnectionMode.Live,
				Path.Combine(project.Path, "DevProjex.exe"),
				project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.ProcessFailed, result.Status);
		Assert.Single(runner.Requests);
		Assert.Contains("node: not found", result.UserMessage, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Connect_CommandLineClient_ReturnsManualFallbackWhenClientIsNotFound()
	{
		using var project = new TemporaryDirectory();
		var runner = new RecordingProcessRunner();
		var service = CreateService(
			new McpClientExecutableLocator(new McpClientExecutableLocatorOptions
			{
				Platform = TerminalCommandHostPlatform.Windows,
				PathVariableProvider = () => project.Path,
				FileExists = static _ => false
			}),
			runner);

		var result = await service.ConnectAsync(
			Request(
				McpConnectionClient.ClaudeCode,
				McpConnectionMode.Live,
				Path.Combine(project.Path, "DevProjex.exe"),
				project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.ClientNotFound, result.Status);
		Assert.True(result.RequiresManualConfiguration);
		Assert.Contains("claude mcp add", result.ManualConfiguration, StringComparison.Ordinal);
		Assert.Empty(runner.Requests);
	}

	[Fact]
	public async Task Connect_CommandLineClient_ReportsAddFailureWithCapturedOutput()
	{
		using var project = new TemporaryDirectory();
		var runner = new RecordingProcessRunner(
			new McpConnectionProcessResult(1, string.Empty, "No MCP server named 'devprojex' found."),
			new McpConnectionProcessResult(5, "partial output", "permission denied"));
		var (service, _) = CreateCommandLineService(project.Path, "codex", runner);

		var result = await service.ConnectAsync(
			Request(
				McpConnectionClient.Codex,
				McpConnectionMode.Live,
				Path.Combine(project.Path, "DevProjex.exe"),
				project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.ProcessFailed, result.Status);
		Assert.True(result.RequiresManualConfiguration);
		Assert.Contains("partial output", result.CommandOutput, StringComparison.Ordinal);
		Assert.Contains("permission denied", result.CommandOutput, StringComparison.Ordinal);
		Assert.Contains("Use manual configuration", result.UserMessage, StringComparison.Ordinal);
		Assert.Equal(2, runner.Requests.Count);
	}

	[Fact]
	public async Task Connect_CommandLineClient_ReportsIncompleteCapturedOutput()
	{
		using var project = new TemporaryDirectory();
		var runner = new RecordingProcessRunner(
			new McpConnectionProcessResult(
				1,
				string.Empty,
				"No MCP server named 'devprojex' found."),
			new McpConnectionProcessResult(
				0,
				"added",
				string.Empty,
				OutputIncomplete: true));
		var (service, _) = CreateCommandLineService(project.Path, "codex", runner);

		var result = await service.ConnectAsync(
			Request(
				McpConnectionClient.Codex,
				McpConnectionMode.Live,
				Path.Combine(project.Path, "DevProjex.exe"),
				project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.Connected, result.Status);
		Assert.Contains("Output incomplete", result.CommandOutput, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Connect_CommandLineClient_ReportsRemovalWhenReplacementAddFails()
	{
		using var project = new TemporaryDirectory();
		var runner = new RecordingProcessRunner(
			ClaudeConnection(Path.Combine(project.Path, "old.exe"), project.Path, live: true),
			new McpConnectionProcessResult(0, "removed", string.Empty),
			new McpConnectionProcessResult(5, string.Empty, "permission denied"),
			new McpConnectionProcessResult(1, string.Empty, "No local-scoped MCP server found with name: devprojex"),
			new McpConnectionProcessResult(0, "restored", string.Empty));
		var (service, _) = CreateCommandLineService(project.Path, "claude", runner);

		var result = await service.ConnectAsync(
			Request(
				McpConnectionClient.ClaudeCode,
				McpConnectionMode.Live,
				Path.Combine(project.Path, "DevProjex.exe"),
				project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.ProcessFailed, result.Status);
		Assert.Equal("Claude Code failed: permission denied; previous connection restored", result.UserMessage);
		Assert.True(result.RequiresManualConfiguration);
	}

	[Fact]
	public async Task Inspect_Codex_DifferentProjectRequiresExplicitReplacement()
	{
		using var currentProject = new TemporaryDirectory();
		using var previousProject = new TemporaryDirectory();
		var previousExecutable = Path.Combine(previousProject.Path, "DevProjex.exe");
		var runner = new RecordingProcessRunner(
			CodexConnection(previousExecutable, previousProject.Path, live: true));
		var (service, _) = CreateCommandLineService(currentProject.Path, "codex", runner);
		var request = Request(
			McpConnectionClient.Codex,
			McpConnectionMode.Standard,
			Path.Combine(currentProject.Path, "DevProjex.exe"),
			currentProject.Path);

		var inspection = await service.InspectAsync(request, TestContext.Current.CancellationToken);

		Assert.True(inspection.Exists);
		Assert.True(inspection.RequiresProjectReplacement);
		Assert.Equal(previousProject.Path, inspection.ExistingProjectRoot);
		Assert.Single(runner.Requests);
		Assert.Equal(["mcp", "get", "devprojex", "--json"], runner.Requests[0].Arguments);
	}

	[Fact]
	public async Task Connect_Codex_DifferentProjectRefusesBeforeMutation()
	{
		using var currentProject = new TemporaryDirectory();
		using var previousProject = new TemporaryDirectory();
		var runner = new RecordingProcessRunner(
			CodexConnection(Path.Combine(previousProject.Path, "DevProjex.exe"), previousProject.Path, live: true));
		var (service, _) = CreateCommandLineService(currentProject.Path, "codex", runner);

		var result = await service.ConnectAsync(
			Request(
				McpConnectionClient.Codex,
				McpConnectionMode.Live,
				Path.Combine(currentProject.Path, "DevProjex.exe"),
				currentProject.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.InvalidConfiguration, result.Status);
		Assert.Contains(previousProject.Path, result.UserMessage, StringComparison.Ordinal);
		Assert.Contains(currentProject.Path, result.UserMessage, StringComparison.Ordinal);
		Assert.Single(runner.Requests);
	}

	[Fact]
	public async Task Replace_Codex_AddFailureRestoresPreviousConnection()
	{
		using var currentProject = new TemporaryDirectory();
		using var previousProject = new TemporaryDirectory();
		var previousExecutable = Path.Combine(previousProject.Path, "DevProjex.exe");
		var runner = new RecordingProcessRunner(
			CodexConnection(previousExecutable, previousProject.Path, live: true),
			new McpConnectionProcessResult(0, "removed", string.Empty),
			new McpConnectionProcessResult(5, string.Empty, "permission denied"),
			new McpConnectionProcessResult(1, string.Empty, "No MCP server named 'devprojex' found."),
			new McpConnectionProcessResult(0, "restored", string.Empty));
		var (service, _) = CreateCommandLineService(currentProject.Path, "codex", runner);
		var request = Request(
			McpConnectionClient.Codex,
			McpConnectionMode.Standard,
			Path.Combine(currentProject.Path, "DevProjex.exe"),
			currentProject.Path);

		var result = await service.ReplaceAsync(
			request,
			previousProject.Path,
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.ProcessFailed, result.Status);
		Assert.Contains("restored", result.UserMessage, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(5, runner.Requests.Count);
		Assert.Equal(
			["mcp", "add", "devprojex", "--", previousExecutable, "mcp", "--root", previousProject.Path, "--live"],
			runner.Requests[4].Arguments);
	}

	[Fact]
	public async Task Replace_Codex_CanceledAddRestoresPreviousConnectionBeforeCancellationEscapes()
	{
		using var currentProject = new TemporaryDirectory();
		using var previousProject = new TemporaryDirectory();
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);
		var previousExecutable = Path.Combine(previousProject.Path, "DevProjex.exe");
		var runner = new CancelingAddProcessRunner(
			CodexConnection(previousExecutable, previousProject.Path, live: false),
			cancellation);
		var (service, _) = CreateCommandLineService(currentProject.Path, "codex", runner);
		var request = Request(
			McpConnectionClient.Codex,
			McpConnectionMode.Live,
			Path.Combine(currentProject.Path, "DevProjex.exe"),
			currentProject.Path);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReplaceAsync(
			request,
			previousProject.Path,
			cancellation.Token));

		Assert.Equal(5, runner.Requests.Count);
		Assert.Equal(
			["mcp", "add", "devprojex", "--", previousExecutable, "mcp", "--root", previousProject.Path],
			runner.Requests[4].Arguments);
	}

	[Fact]
	public async Task Connect_CommandLineClient_ReportsProcessStartFailure()
	{
		using var project = new TemporaryDirectory();
		var runner = new RecordingProcessRunner(
			new McpConnectionProcessResult(null, string.Empty, string.Empty, StartError: "cannot start"));
		var (service, _) = CreateCommandLineService(project.Path, "codex", runner);

		var result = await service.ConnectAsync(
			Request(
				McpConnectionClient.Codex,
				McpConnectionMode.Standard,
				Path.Combine(project.Path, "DevProjex.exe"),
				project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.ProcessFailed, result.Status);
		Assert.Contains("cannot start", result.UserMessage, StringComparison.Ordinal);
		Assert.True(result.RequiresManualConfiguration);
	}

	[Fact]
	public async Task Connect_CommandLineClient_ReportsTimeoutWithoutRunningAdd()
	{
		using var project = new TemporaryDirectory();
		var runner = new RecordingProcessRunner(
			new McpConnectionProcessResult(null, string.Empty, string.Empty, TimedOut: true));
		var (service, _) = CreateCommandLineService(project.Path, "claude", runner);

		var result = await service.ConnectAsync(
			Request(
				McpConnectionClient.ClaudeCode,
				McpConnectionMode.Live,
				Path.Combine(project.Path, "DevProjex.exe"),
				project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.TimedOut, result.Status);
		Assert.True(result.RequiresManualConfiguration);
		Assert.Single(runner.Requests);
	}

	[Fact]
	public async Task Connect_Cursor_MergesOnlyDevProjexAndCommitsAtomically()
	{
		using var project = new TemporaryDirectory();
		const string existing = """
			{
			  "version": 7,
			  "custom": { "preserve": true },
			  "mcpServers": {
			    "other": { "command": "other-tool", "args": ["--keep"] },
			    "devprojex": { "command": "old", "args": [] }
			  }
			}
			""";
		var targetPath = project.CreateFile(Path.Combine(".cursor", "mcp.json"), existing);
		var service = CreateService(
			new McpClientExecutableLocator(),
			new RecordingProcessRunner());
		var executable = Path.Combine(project.Path, "DevProjex.exe");

		var result = await service.ConnectAsync(
			Request(McpConnectionClient.Cursor, McpConnectionMode.Live, executable, project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.Updated, result.Status);
		Assert.True(result.Replaced);
		Assert.Equal(targetPath, result.TargetPath);
		using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
			targetPath,
			TestContext.Current.CancellationToken));
		var root = document.RootElement;
		Assert.Equal(7, root.GetProperty("version").GetInt32());
		Assert.True(root.GetProperty("custom").GetProperty("preserve").GetBoolean());
		var servers = root.GetProperty("mcpServers");
		Assert.Equal("other-tool", servers.GetProperty("other").GetProperty("command").GetString());
		Assert.Equal("--keep", servers.GetProperty("other").GetProperty("args")[0].GetString());
		AssertConnection(
			servers.GetProperty("devprojex"),
			executable,
			project.Path,
			expectLive: true,
			expectVsCodeType: false);
		AssertNoTemporaryFiles(Path.GetDirectoryName(targetPath)!);
	}

	[Fact]
	public async Task Connect_VsCode_MergesServersAndPreservesUnrelatedRootProperties()
	{
		using var project = new TemporaryDirectory();
		const string existing = """
			{
			  "inputs": [{ "id": "token", "type": "promptString" }],
			  "servers": {
			    "remote": { "type": "http", "url": "https://example.invalid/mcp" }
			  }
			}
			""";
		var targetPath = project.CreateFile(Path.Combine(".vscode", "mcp.json"), existing);
		var service = CreateService(
			new McpClientExecutableLocator(),
			new RecordingProcessRunner());
		var executable = Path.Combine(project.Path, "DevProjex.exe");

		var result = await service.ConnectAsync(
			Request(McpConnectionClient.VsCode, McpConnectionMode.Standard, executable, project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.Connected, result.Status);
		Assert.False(result.Replaced);
		using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
			targetPath,
			TestContext.Current.CancellationToken));
		var root = document.RootElement;
		Assert.Equal("token", root.GetProperty("inputs")[0].GetProperty("id").GetString());
		var servers = root.GetProperty("servers");
		Assert.Equal("http", servers.GetProperty("remote").GetProperty("type").GetString());
		Assert.Equal(
			"https://example.invalid/mcp",
			servers.GetProperty("remote").GetProperty("url").GetString());
		AssertConnection(
			servers.GetProperty("devprojex"),
			executable,
			project.Path,
			expectLive: false,
			expectVsCodeType: true);
		AssertNoTemporaryFiles(Path.GetDirectoryName(targetPath)!);
	}

	[Fact]
	public async Task Connect_ProjectClient_InvalidJsonRemainsByteForByteUntouched()
	{
		using var project = new TemporaryDirectory();
		const string invalid = "{ invalid json\r\n";
		var targetPath = project.CreateFile(Path.Combine(".cursor", "mcp.json"), invalid);
		var before = await File.ReadAllBytesAsync(
			targetPath,
			TestContext.Current.CancellationToken);
		var service = CreateService(
			new McpClientExecutableLocator(),
			new RecordingProcessRunner());

		var result = await service.ConnectAsync(
			Request(
				McpConnectionClient.Cursor,
				McpConnectionMode.Live,
				Path.Combine(project.Path, "DevProjex.exe"),
				project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.InvalidConfiguration, result.Status);
		Assert.True(result.RequiresManualConfiguration);
		Assert.Contains("Invalid data", result.UserMessage, StringComparison.Ordinal);
		Assert.Contains("Use manual configuration", result.UserMessage, StringComparison.Ordinal);
		Assert.Equal(
			before,
			await File.ReadAllBytesAsync(
				targetPath,
				TestContext.Current.CancellationToken));
		AssertNoTemporaryFiles(Path.GetDirectoryName(targetPath)!);
	}

	[Theory]
	[InlineData("{ // keep this comment\n  \"mcpServers\": {}\n}\n")]
	[InlineData("{\n  \"mcpServers\": {},\n}\n")]
	public async Task Connect_ProjectClient_JsonExtensionsRemainByteForByteUntouched(string existing)
	{
		using var project = new TemporaryDirectory();
		var targetPath = project.CreateFile(Path.Combine(".cursor", "mcp.json"), existing);
		var before = await File.ReadAllBytesAsync(targetPath, TestContext.Current.CancellationToken);
		var service = CreateService(new McpClientExecutableLocator(), new RecordingProcessRunner());

		var result = await service.ConnectAsync(
			Request(
				McpConnectionClient.Cursor,
				McpConnectionMode.Live,
				Path.Combine(project.Path, "DevProjex.exe"),
				project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.InvalidConfiguration, result.Status);
		Assert.Equal(before, await File.ReadAllBytesAsync(targetPath, TestContext.Current.CancellationToken));
	}

	[Theory]
	[InlineData("{ // keep this comment\n  \"servers\": {}\n}\n")]
	[InlineData("{\n  \"servers\": {},\n}\n")]
	public async Task Connect_VsCode_JsoncRemainsUntouchedAndReturnsCopyableConfiguration(string existing)
	{
		using var project = new TemporaryDirectory();
		var targetPath = project.CreateFile(Path.Combine(".vscode", "mcp.json"), existing);
		var before = await File.ReadAllBytesAsync(targetPath, TestContext.Current.CancellationToken);
		var service = CreateService(new McpClientExecutableLocator(), new RecordingProcessRunner());

		var result = await service.ConnectAsync(
			Request(
				McpConnectionClient.VsCode,
				McpConnectionMode.Live,
				Path.Combine(project.Path, "DevProjex.exe"),
				project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.InvalidConfiguration, result.Status);
		Assert.Contains("comments", result.UserMessage, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("\"servers\"", result.ManualConfiguration, StringComparison.Ordinal);
		Assert.Equal(before, await File.ReadAllBytesAsync(targetPath, TestContext.Current.CancellationToken));
	}

	[Theory]
	[InlineData("[]")]
	[InlineData("{ \"mcpServers\": [] }")]
	public async Task Connect_ProjectClient_InvalidJsonShapeRemainsByteForByteUntouched(string existing)
	{
		using var project = new TemporaryDirectory();
		var targetPath = project.CreateFile(Path.Combine(".cursor", "mcp.json"), existing);
		var before = await File.ReadAllBytesAsync(targetPath, TestContext.Current.CancellationToken);
		var service = CreateService(new McpClientExecutableLocator(), new RecordingProcessRunner());

		var result = await service.ConnectAsync(
			Request(
				McpConnectionClient.Cursor,
				McpConnectionMode.Live,
				Path.Combine(project.Path, "DevProjex.exe"),
				project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.InvalidConfiguration, result.Status);
		Assert.Equal(before, await File.ReadAllBytesAsync(targetPath, TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task Connect_ProjectClient_MissingRootReturnsManualFallback()
	{
		using var temp = new TemporaryDirectory();
		var missingRoot = Path.Combine(temp.Path, "missing-project");
		var service = CreateService(new McpClientExecutableLocator(), new RecordingProcessRunner());

		var result = await service.ConnectAsync(
			Request(
				McpConnectionClient.VsCode,
				McpConnectionMode.Standard,
				Path.Combine(temp.Path, "DevProjex.exe"),
				missingRoot),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.InvalidConfiguration, result.Status);
		Assert.True(result.RequiresManualConfiguration);
		Assert.Contains("Resource unavailable", result.UserMessage, StringComparison.Ordinal);
		Assert.False(Directory.Exists(missingRoot));
	}

	[Fact]
	public async Task Connect_ProjectClient_OccupiedConfigurationDirectoryReturnsManualFallback()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile(".cursor", "occupied");
		var service = CreateService(new McpClientExecutableLocator(), new RecordingProcessRunner());

		var result = await service.ConnectAsync(
			Request(
				McpConnectionClient.Cursor,
				McpConnectionMode.Live,
				Path.Combine(project.Path, "DevProjex.exe"),
				project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.InvalidConfiguration, result.Status);
		Assert.True(result.RequiresManualConfiguration);
		Assert.Contains("Resource unavailable", result.UserMessage, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Connect_ProjectClient_SymbolicLinkConfigurationRemainsUntouched()
	{
		using var project = new TemporaryDirectory();
		using var outside = new TemporaryDirectory();
		var configurationDirectory = project.CreateFolder(".cursor");
		var outsidePath = outside.CreateFile("mcp.json", "{ \"outside\": true }");
		var targetPath = Path.Combine(configurationDirectory, "mcp.json");
		try
		{
			File.CreateSymbolicLink(targetPath, outsidePath);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
			return;
		}
		var service = CreateService(new McpClientExecutableLocator(), new RecordingProcessRunner());

		var result = await service.ConnectAsync(
			Request(
				McpConnectionClient.Cursor,
				McpConnectionMode.Live,
				Path.Combine(project.Path, "DevProjex.exe"),
				project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.InvalidConfiguration, result.Status);
		Assert.Equal("{ \"outside\": true }", await File.ReadAllTextAsync(
			outsidePath,
			TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task Connect_ProjectClient_DanglingSymbolicLinkConfigurationRemainsUntouched()
	{
		using var project = new TemporaryDirectory();
		using var outside = new TemporaryDirectory();
		var configurationDirectory = project.CreateFolder(".cursor");
		var missingTarget = Path.Combine(outside.Path, "missing.json");
		var targetPath = Path.Combine(configurationDirectory, "mcp.json");
		try
		{
			File.CreateSymbolicLink(targetPath, missingTarget);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
			return;
		}
		var linkTarget = new FileInfo(targetPath).LinkTarget;
		var service = CreateService(new McpClientExecutableLocator(), new RecordingProcessRunner());

		var result = await service.ConnectAsync(
			Request(
				McpConnectionClient.Cursor,
				McpConnectionMode.Live,
				Path.Combine(project.Path, "DevProjex.exe"),
				project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.InvalidConfiguration, result.Status);
		Assert.Equal(linkTarget, new FileInfo(targetPath).LinkTarget);
		Assert.False(File.Exists(missingTarget));
	}

	[Fact]
	public async Task Connect_Json_ReturnsManualConfigurationAndDesktopPaths()
	{
		using var project = new TemporaryDirectory();
		var service = CreateService(
			new McpClientExecutableLocator(),
			new RecordingProcessRunner());

		var result = await service.ConnectAsync(
			Request(
				McpConnectionClient.Json,
				McpConnectionMode.Live,
				Path.Combine(project.Path, "DevProjex.exe"),
				project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.ManualConfiguration, result.Status);
		Assert.Contains("mcpServers", result.ManualConfiguration, StringComparison.Ordinal);
		Assert.Contains(result.SuggestedConfigPaths!, path => path.Contains("APPDATA", StringComparison.Ordinal));
		Assert.Contains(result.SuggestedConfigPaths!, path => path.Contains("Library/Application Support", StringComparison.Ordinal));
	}

	private static (McpConnectionService Service, string ClientExecutable) CreateCommandLineService(
		string projectRoot,
		string commandName,
		IMcpConnectionProcessRunner runner)
	{
		var bin = Path.Combine(projectRoot, "client-bin");
		var clientExecutable = Path.GetFullPath(Path.Combine(bin, commandName + ".exe"));
		var locator = new McpClientExecutableLocator(new McpClientExecutableLocatorOptions
		{
			Platform = TerminalCommandHostPlatform.Windows,
			PathVariableProvider = () => bin,
			FileExists = path => string.Equals(path, clientExecutable, StringComparison.OrdinalIgnoreCase)
		});
		return (CreateService(locator, runner), clientExecutable);
	}

	private static McpConnectionService CreateService(
		McpClientExecutableLocator locator,
		IMcpConnectionProcessRunner runner) =>
		new(
			CreateLocalization(),
			locator,
			runner,
			new McpProjectConfigurationWriter());

	private static McpConnectionRequest Request(
		McpConnectionClient client,
		McpConnectionMode mode,
		string executablePath,
		string projectRoot) =>
		new(client, mode, Path.GetFullPath(executablePath), Path.GetFullPath(projectRoot));

	private static void AssertProcessRequest(
		McpConnectionProcessRequest request,
		string expectedExecutable,
		string expectedWorkingDirectory,
		IReadOnlyList<string> expectedArguments)
	{
		Assert.Equal(expectedExecutable, request.ExecutablePath);
		Assert.Equal(expectedWorkingDirectory, request.WorkingDirectory);
		Assert.Equal(TimeSpan.FromSeconds(15), request.Timeout);
		Assert.Equal(expectedArguments, request.Arguments);
	}

	private static void AssertConnection(
		JsonElement server,
		string expectedExecutable,
		string expectedProjectRoot,
		bool expectLive,
		bool expectVsCodeType)
	{
		if (expectVsCodeType)
			Assert.Equal("stdio", server.GetProperty("type").GetString());
		else
			Assert.False(server.TryGetProperty("type", out _));
		Assert.Equal(expectedExecutable, server.GetProperty("command").GetString());
		var arguments = server.GetProperty("args")
			.EnumerateArray()
			.Select(static value => value.GetString())
			.ToArray();
		Assert.Equal(
			expectLive
				? ["mcp", "--root", expectedProjectRoot, "--live"]
				: ["mcp", "--root", expectedProjectRoot],
			arguments);
	}

	private static void AssertNoTemporaryFiles(string directory) =>
		Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp", SearchOption.TopDirectoryOnly));

	private static McpConnectionProcessResult CodexConnection(
		string executable,
		string projectRoot,
		bool live)
	{
		var payload = JsonSerializer.Serialize(new
		{
			name = "devprojex",
			enabled = true,
			transport = new
			{
				type = "stdio",
				command = executable,
				args = live
					? new[] { "mcp", "--root", projectRoot, "--live" }
					: ["mcp", "--root", projectRoot]
			}
		});
		return new McpConnectionProcessResult(0, payload, string.Empty);
	}

	private static McpConnectionProcessResult ClaudeConnection(
		string executable,
		string projectRoot,
		bool live) =>
		new(
			0,
			$"""
			devprojex:
			  Scope: Local config (private to you in this project)
			  Type: stdio
			  Command: {executable}
			  Args: mcp --root {projectRoot}{(live ? " --live" : string.Empty)}
			  Environment:
			""",
			string.Empty);

	private static LocalizationService CreateLocalization()
	{
		var values = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["Mcp.Connect.ClaudeCode.Connected"] = "Claude connected",
			["Mcp.Connect.ClaudeCode.Updated"] = "Claude updated",
			["Mcp.Connect.Codex.Connected"] = "Codex connected",
			["Mcp.Connect.Codex.Updated"] = "Codex updated",
			["Mcp.Connect.ClientNotFound"] = "{0} not found",
			["Mcp.Connect.CommandFailed"] = "{0}: {1}",
			["Mcp.Connect.CommandFailedAfterRemoval"] = "Previous {0} connection removed: {1}",
			["Mcp.Connect.CommandFailedRestored"] = "{0} failed: {1}; previous connection restored",
			["Mcp.Connect.ReplaceRequired"] = "Replace {0} with {1}",
			["Mcp.Connect.ConnectionChanged"] = "Connection changed",
			["Mcp.Connect.InspectionFailed"] = "Connection could not be inspected",
			["Mcp.Connect.VsCodeJsoncManual"] = "The file contains comments or trailing commas and must be updated manually",
			["Mcp.Connect.ManualFallbackHint"] = "Use manual configuration",
			["Mcp.Connect.OutputIncomplete"] = "Output incomplete",
			["Mcp.Connect.CommandTimedOut"] = "Command timed out",
			["Mcp.Connect.UnknownError"] = "Unknown error",
			["Mcp.Connect.ProjectConfigurationFailed"] = "{0}: {1}",
			["Mcp.Connect.ProjectConfigurationUpdated"] = "{0}: {1}; restart {2}",
			["Mcp.Connect.RestartClient"] = "Restart {0}",
			["Mcp.Connect.ManualConfiguration"] = "Manual configuration",
			["Desktop.Error.ResourceUnavailable"] = "Resource unavailable",
			["Desktop.Error.InvalidData"] = "Invalid data",
			["Desktop.Error.AccessDenied"] = "Access denied",
			["Desktop.Error.OperationFailed"] = "Operation failed"
		};
		return new LocalizationService(
			new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
			{
				[AppLanguage.En] = values
			}),
			AppLanguage.En);
	}

	private sealed class RecordingProcessRunner : IMcpConnectionProcessRunner
	{
		private readonly Queue<McpConnectionProcessResult> _results;

		public RecordingProcessRunner(params McpConnectionProcessResult[] results)
		{
			_results = new Queue<McpConnectionProcessResult>(results);
		}

		public List<McpConnectionProcessRequest> Requests { get; } = [];

		public Task<McpConnectionProcessResult> RunAsync(
			McpConnectionProcessRequest request,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Requests.Add(request);
			if (_results.Count == 0)
				throw new InvalidOperationException("No fake MCP process result was configured.");
			return Task.FromResult(_results.Dequeue());
		}
	}

	private sealed class CancelingAddProcessRunner(
		McpConnectionProcessResult existing,
		CancellationTokenSource cancellation) : IMcpConnectionProcessRunner
	{
		public List<McpConnectionProcessRequest> Requests { get; } = [];

		public Task<McpConnectionProcessResult> RunAsync(
			McpConnectionProcessRequest request,
			CancellationToken cancellationToken)
		{
			Requests.Add(request);
			return Requests.Count switch
			{
				1 => Task.FromResult(existing),
				2 => Task.FromResult(new McpConnectionProcessResult(0, "removed", string.Empty)),
				3 => CancelAdd(),
				4 => Task.FromResult(new McpConnectionProcessResult(
					1,
					string.Empty,
					"No MCP server named 'devprojex' found.")),
				5 => Task.FromResult(new McpConnectionProcessResult(0, "restored", string.Empty)),
				_ => throw new InvalidOperationException("Unexpected MCP client command.")
			};
		}

		private Task<McpConnectionProcessResult> CancelAdd()
		{
			cancellation.Cancel();
			return Task.FromCanceled<McpConnectionProcessResult>(cancellation.Token);
		}
	}
}
