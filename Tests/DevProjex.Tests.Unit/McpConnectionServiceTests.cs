using DevProjex.Application.Services;
using DevProjex.Infrastructure.TerminalCommands;

namespace DevProjex.Tests.Unit;

[Collection(ProcessEnvironmentCollection.Name)]
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
	[InlineData((int)McpConnectionClient.ClaudeCode, "claude")]
	[InlineData((int)McpConnectionClient.Codex, "codex")]
	public async Task Connect_CommandLineClient_InspectsThenAddsWithExactArguments(
		int clientValue,
		string commandName)
	{
		using var project = new TemporaryDirectory();
		var client = (McpConnectionClient)clientValue;
		var runner = new RecordingProcessRunner(new McpConnectionProcessResult(0, "added", string.Empty));
		var (service, clientExecutable) = CreateCommandLineService(project.Path, commandName, runner);
		var devProjexExecutable = Path.Combine(project.Path, "DevProjex.exe");

		var result = await service.ConnectAsync(
			Request(client, McpConnectionMode.Live, devProjexExecutable, project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.Connected, result.Status);
		Assert.False(result.Replaced);
		Assert.Equal(commandName, result.NextCommand);
		Assert.Single(runner.Requests);
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
			runner.Requests[0],
			clientExecutable,
			project.Path,
			expectedAddArguments);
		Assert.Contains("add: added", result.CommandOutput, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Connect_CommandLineClient_ReportsUpdatedAfterSuccessfulRemoval()
	{
		using var project = new TemporaryDirectory();
		var previousExecutable = Path.Combine(project.Path, "old.exe");
		var runner = new RecordingProcessRunner(
			new McpConnectionProcessResult(0, "removed", string.Empty),
			new McpConnectionProcessResult(0, "added", string.Empty));
		var (service, _) = CreateCommandLineService(
			project.Path,
			"claude",
			runner,
			claudeUserConfigurationReader: CreateClaudeConfigurationReader(
				previousExecutable,
				["mcp", "--root", project.Path, "--live"],
				new Dictionary<string, string>()));

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
		Assert.DoesNotContain("--live", runner.Requests[1].Arguments);
	}

	[Fact]
	public async Task Connect_Codex_MissingUserEntryReportsNewConnection()
	{
		using var project = new TemporaryDirectory();
		var runner = new RecordingProcessRunner(
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
		Assert.Single(runner.Requests);
		Assert.Contains("Read smaller ranges when needed", result.UserMessage, StringComparison.Ordinal);
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
	public async Task Connect_Claude_InspectsTheProjectConfigurationWithoutInvokingGet()
	{
		using var project = new TemporaryDirectory();
		var runner = new RecordingProcessRunner(
			new McpConnectionProcessResult(0, "added", string.Empty));
		var (service, clientExecutable) = CreateCommandLineService(project.Path, "claude", runner);
		var executable = Path.Combine(project.Path, "DevProjex.exe");

		var result = await service.ConnectAsync(
			Request(McpConnectionClient.ClaudeCode, McpConnectionMode.Live, executable, project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.Connected, result.Status);
		var request = Assert.Single(runner.Requests);
		AssertProcessRequest(
			request,
			clientExecutable,
			project.Path,
			[
				"mcp", "add", "--scope", "local", "devprojex", "--", executable,
				"mcp", "--root", project.Path, "--live"
			]);
	}

	[Fact]
	public async Task Connect_Claude_ConcurrentExistingEntryIsAcceptedWithoutRemovingIt()
	{
		using var project = new TemporaryDirectory();
		var executable = Path.Combine(project.Path, "DevProjex.exe");
		var reader = new SequenceClaudeUserConfigurationReader(
			new McpClaudeUserConfigurationRead(true, null),
			CreateClaudeConfigurationRead(
				executable,
				["mcp", "--root", project.Path, "--live"],
				new Dictionary<string, string>()));
		var runner = new RecordingProcessRunner(
			new McpConnectionProcessResult(
				1,
				string.Empty,
				"MCP server devprojex already exists in local config"));
		var (service, _) = CreateCommandLineService(
			project.Path,
			"claude",
			runner,
			claudeUserConfigurationReader: reader);

		var result = await service.ConnectAsync(
			Request(McpConnectionClient.ClaudeCode, McpConnectionMode.Live, executable, project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.Updated, result.Status);
		Assert.True(result.Replaced);
		Assert.Single(runner.Requests);
		Assert.Equal(2, reader.ReadCount);
	}

	[Fact]
	public async Task Connect_Codex_UnreadableUserConfigurationDoesNotRemoveOrAdd()
	{
		using var project = new TemporaryDirectory();
		var runner = new RecordingProcessRunner();
		var (service, _) = CreateCommandLineService(
			project.Path,
			"codex",
			runner,
			new StubCodexUserConfigurationReader(new McpCodexUserConfigurationRead(false, null)));

		var result = await service.ConnectAsync(
			Request(
				McpConnectionClient.Codex,
				McpConnectionMode.Standard,
				Path.Combine(project.Path, "DevProjex.exe"),
				project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.ProcessFailed, result.Status);
		Assert.True(result.RequiresManualConfiguration);
		Assert.Empty(runner.Requests);
	}

	[Fact]
	public void CodexUserConfiguration_InvalidServerTableFailsClosed()
	{
		var configurationPath = Path.Combine(Path.GetTempPath(), "codex-reader", "config.toml");
		var reader = new McpCodexUserConfigurationReader(new McpCodexUserConfigurationReaderOptions
		{
			CodexHomeProvider = () => Path.GetDirectoryName(configurationPath),
			FileExists = path => string.Equals(path, configurationPath, StringComparison.OrdinalIgnoreCase),
			ReadAllText = _ => "mcp_servers = 42"
		});

		var result = reader.Read();

		Assert.False(result.Succeeded);
		Assert.Null(result.Connection);
	}

	[Fact]
	public void CodexUserConfiguration_MissingServerIsAnEstablishedEmptyLayer()
	{
		var configurationPath = Path.Combine(Path.GetTempPath(), "codex-reader", "config.toml");
		var reader = new McpCodexUserConfigurationReader(new McpCodexUserConfigurationReaderOptions
		{
			CodexHomeProvider = () => Path.GetDirectoryName(configurationPath),
			FileExists = path => string.Equals(path, configurationPath, StringComparison.OrdinalIgnoreCase),
			ReadAllText = _ => "model = \"gpt-5\""
		});

		var result = reader.Read();

		Assert.True(result.Succeeded);
		Assert.Null(result.Connection);
	}

	[Fact]
	public void ClaudeUserConfiguration_ReadsTheProjectLocalEntryWithoutRunningTheClient()
	{
		using var project = new TemporaryDirectory();
		using var configurationDirectory = new TemporaryDirectory();
		var executable = Path.Combine(project.Path, "DevProjex.exe");
		var rawEntry = JsonSerializer.Serialize(new
		{
			type = "stdio",
			command = executable,
			args = new[] { "mcp", "--root", project.Path, "--live" },
			env = new Dictionary<string, string> { ["KEEP"] = "yes" },
			metadata = new { preserve = true }
		});
		configurationDirectory.CreateFile(
			".claude.json",
			$$"""
			{
			  "projects": {
			    {{JsonSerializer.Serialize(project.Path.Replace('\\', '/'))}}: {
			      "mcpServers": {
			        "devprojex": {{rawEntry}}
			      }
			    }
			  }
			}
			""");
		var reader = new McpClaudeUserConfigurationReader(new McpClaudeUserConfigurationReaderOptions
		{
			ConfigurationDirectoryProvider = () => configurationDirectory.Path
		});

		var result = reader.Read(project.Path);

		Assert.True(result.Succeeded);
		var connection = Assert.IsType<McpClaudeUserConnection>(result.Connection);
		Assert.Equal(executable, connection.Command);
		Assert.Equal(["mcp", "--root", project.Path, "--live"], connection.Arguments);
		Assert.Equal("yes", connection.Environment["KEEP"]);
		Assert.Contains("metadata", connection.RawJson, StringComparison.Ordinal);
	}

	[Fact]
	public void ClaudeUserConfiguration_InvalidJsonFailsClosed()
	{
		using var configurationDirectory = new TemporaryDirectory();
		configurationDirectory.CreateFile(".claude.json", "{ broken");
		var reader = new McpClaudeUserConfigurationReader(new McpClaudeUserConfigurationReaderOptions
		{
			ConfigurationDirectoryProvider = () => configurationDirectory.Path
		});

		var result = reader.Read(configurationDirectory.Path);

		Assert.False(result.Succeeded);
		Assert.Null(result.Connection);
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
		Assert.Single(runner.Requests);
	}

	[Fact]
	public async Task Connect_CommandLineClient_ReportsIncompleteCapturedOutput()
	{
		using var project = new TemporaryDirectory();
		var runner = new RecordingProcessRunner(
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
		var previousExecutable = Path.Combine(project.Path, "old.exe");
		var runner = new RecordingProcessRunner(
			new McpConnectionProcessResult(0, "removed", string.Empty),
			new McpConnectionProcessResult(5, string.Empty, "permission denied"),
			new McpConnectionProcessResult(1, string.Empty, "No local-scoped MCP server found with name: devprojex"),
			new McpConnectionProcessResult(0, "restored", string.Empty));
		var (service, _) = CreateCommandLineService(
			project.Path,
			"claude",
			runner,
			claudeUserConfigurationReader: CreateClaudeConfigurationReader(
				previousExecutable,
				["mcp", "--root", project.Path, "--live"],
				new Dictionary<string, string>()));

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
		var runner = new RecordingProcessRunner();
		var (service, _) = CreateCommandLineService(
			currentProject.Path,
			"codex",
			runner,
			CreateCodexConfigurationReader(
				previousExecutable,
				["mcp", "--root", previousProject.Path, "--live"],
				new Dictionary<string, string>()));
		var request = Request(
			McpConnectionClient.Codex,
			McpConnectionMode.Standard,
			Path.Combine(currentProject.Path, "DevProjex.exe"),
			currentProject.Path);

		var inspection = await service.InspectAsync(request, TestContext.Current.CancellationToken);

		Assert.True(inspection.Exists);
		Assert.True(inspection.RequiresProjectReplacement);
		Assert.Equal(previousProject.Path, inspection.ExistingProjectRoot);
		Assert.Empty(runner.Requests);
	}

	[Fact]
	public async Task Inspect_Codex_UsesUserConfigurationInsteadOfProjectConfiguration()
	{
		using var currentProject = new TemporaryDirectory();
		using var userProject = new TemporaryDirectory();
		using var projectConfiguration = new TemporaryDirectory();
		var userExecutable = Path.Combine(userProject.Path, "DevProjex.exe");
		var projectExecutable = Path.Combine(projectConfiguration.Path, "DevProjex.exe");
		var runner = new RecordingProcessRunner(
			CodexConnection(projectExecutable, projectConfiguration.Path, live: true));
		var reader = CreateCodexConfigurationReader(
			userExecutable,
			["mcp", "--root", userProject.Path, "--hide-private-data"],
			new Dictionary<string, string> { ["DPX_MODE"] = "safe" });
		var (service, _) = CreateCommandLineService(currentProject.Path, "codex", runner, reader);

		var inspection = await service.InspectAsync(
			Request(
				McpConnectionClient.Codex,
				McpConnectionMode.Standard,
				Path.Combine(currentProject.Path, "DevProjex.exe"),
				currentProject.Path),
			TestContext.Current.CancellationToken);

		Assert.True(inspection.Exists);
		Assert.Equal(userProject.Path, inspection.ExistingProjectRoot);
		Assert.Empty(runner.Requests);
	}

	[Fact]
	public async Task Connect_Codex_DifferentProjectRefusesBeforeMutation()
	{
		using var currentProject = new TemporaryDirectory();
		using var previousProject = new TemporaryDirectory();
		var runner = new RecordingProcessRunner();
		var previousExecutable = Path.Combine(previousProject.Path, "DevProjex.exe");
		var (service, _) = CreateCommandLineService(
			currentProject.Path,
			"codex",
			runner,
			CreateCodexConfigurationReader(
				previousExecutable,
				["mcp", "--root", previousProject.Path, "--live"],
				new Dictionary<string, string>()));

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
		Assert.Empty(runner.Requests);
	}

	[Fact]
	public async Task Replace_Codex_AddFailureRestoresPreviousConnection()
	{
		using var currentProject = new TemporaryDirectory();
		using var previousProject = new TemporaryDirectory();
		var previousExecutable = Path.Combine(previousProject.Path, "DevProjex.exe");
		var previousArguments = new[]
		{
			"mcp", "--root", previousProject.Path, "--hide-private-data", "--live"
		};
		var previousEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["DPX_PROFILE"] = "private",
			["DPX_CHANNEL"] = "stable"
		};
		var runner = new RecordingProcessRunner(
			new McpConnectionProcessResult(0, "removed", string.Empty),
			new McpConnectionProcessResult(5, string.Empty, "permission denied"),
			new McpConnectionProcessResult(1, string.Empty, "No MCP server named 'devprojex' found."),
			new McpConnectionProcessResult(0, "restored", string.Empty));
		var (service, _) = CreateCommandLineService(
			currentProject.Path,
			"codex",
			runner,
			CreateCodexConfigurationReader(
				previousExecutable,
				previousArguments,
				previousEnvironment));
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
		Assert.Equal(4, runner.Requests.Count);
		Assert.Equal(
			[
				"mcp", "add", "devprojex",
				"--env", "DPX_PROFILE=private",
				"--env", "DPX_CHANNEL=stable",
				"--", previousExecutable,
				"mcp", "--root", previousProject.Path, "--hide-private-data", "--live"
			],
			runner.Requests[3].Arguments);
	}

	[Fact]
	public async Task Replace_Claude_AddFailureRestoresOriginalJsonEntry()
	{
		using var project = new TemporaryDirectory();
		var previousExecutable = Path.Combine(project.Path, "previous.exe");
		var previousArguments = new[]
		{
			"mcp", "--root", project.Path, "--hide-private-data", "--live"
		};
		var previousEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["DPX_PROFILE"] = "private"
		};
		var rawJson = JsonSerializer.Serialize(new
		{
			type = "stdio",
			command = previousExecutable,
			args = previousArguments,
			env = previousEnvironment,
			metadata = new { keep = true }
		});
		var runner = new RecordingProcessRunner(
			new McpConnectionProcessResult(0, "removed", string.Empty),
			new McpConnectionProcessResult(5, string.Empty, "permission denied"),
			new McpConnectionProcessResult(
				1,
				string.Empty,
				"No MCP server named \"devprojex\" in local scope"),
			new McpConnectionProcessResult(0, "restored", string.Empty));
		var (service, _) = CreateCommandLineService(
			project.Path,
			"claude",
			runner,
			claudeUserConfigurationReader: CreateClaudeConfigurationReader(
				previousExecutable,
				previousArguments,
				previousEnvironment,
				rawJson));

		var result = await service.ConnectAsync(
			Request(
				McpConnectionClient.ClaudeCode,
				McpConnectionMode.Standard,
				Path.Combine(project.Path, "DevProjex.exe"),
				project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.ProcessFailed, result.Status);
		Assert.Contains("restored", result.UserMessage, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(
			["mcp", "add-json", "--scope", "local", "devprojex"],
			runner.Requests[3].Arguments.Take(5));
		using var restored = JsonDocument.Parse(runner.Requests[3].Arguments[5]);
		Assert.Equal("stdio", restored.RootElement.GetProperty("type").GetString());
		Assert.Equal(previousExecutable, restored.RootElement.GetProperty("command").GetString());
		Assert.Equal(
			previousArguments,
			restored.RootElement.GetProperty("args").EnumerateArray().Select(static value => value.GetString()));
		Assert.Equal(
			"private",
			restored.RootElement.GetProperty("env").GetProperty("DPX_PROFILE").GetString());
		Assert.True(restored.RootElement.GetProperty("metadata").GetProperty("keep").GetBoolean());
	}

	[Fact]
	public async Task Replace_Codex_CanceledAddRestoresPreviousConnectionBeforeCancellationEscapes()
	{
		using var currentProject = new TemporaryDirectory();
		using var previousProject = new TemporaryDirectory();
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);
		var previousExecutable = Path.Combine(previousProject.Path, "DevProjex.exe");
		var runner = new CancelingAddProcessRunner(cancellation);
		var (service, _) = CreateCommandLineService(
			currentProject.Path,
			"codex",
			runner,
			CreateCodexConfigurationReader(
				previousExecutable,
				["mcp", "--root", previousProject.Path],
				new Dictionary<string, string>()));
		var request = Request(
			McpConnectionClient.Codex,
			McpConnectionMode.Live,
			Path.Combine(currentProject.Path, "DevProjex.exe"),
			currentProject.Path);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReplaceAsync(
			request,
			previousProject.Path,
			cancellation.Token));

		Assert.Equal(4, runner.Requests.Count);
		Assert.Equal(
			["mcp", "add", "devprojex", "--", previousExecutable, "mcp", "--root", previousProject.Path],
			runner.Requests[3].Arguments);
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
		Assert.Contains("configuration was written", result.UserMessage, StringComparison.Ordinal);
		Assert.Equal("Restart Cursor", result.NextCommand);
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
		Assert.Equal("Enable the server in VS Code", result.NextCommand);
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

	[Theory]
	[InlineData((int)McpConnectionClient.Cursor, ".cursor", "mcpServers")]
	[InlineData((int)McpConnectionClient.VsCode, ".vscode", "servers")]
	public async Task Connect_ProjectClient_PreservesFieldsOnTheExistingDevProjexEntry(
		int clientValue,
		string directory,
		string container)
	{
		using var project = new TemporaryDirectory();
		var typeProperty = (McpConnectionClient)clientValue == McpConnectionClient.VsCode
			? "\"type\": \"stdio\","
			: string.Empty;
		var existing = $$"""
			{
			  "{{container}}": {
			    "devprojex": {
			      {{typeProperty}}
			      "command": "old",
			      "args": [],
			      "envFile": "${workspaceFolder}/.env",
			    "sandboxEnabled": true,
			    "env": { "KEEP": "yes" },
			    "dev": { "watch": true },
			    "cwd": "keep-me"
			    }
			  }
			}
			""";
		var targetPath = project.CreateFile(Path.Combine(directory, "mcp.json"), existing);
		var service = CreateService(new McpClientExecutableLocator(), new RecordingProcessRunner());
		var executable = Path.Combine(project.Path, "DevProjex.exe");

		var result = await service.ConnectAsync(
			Request((McpConnectionClient)clientValue, McpConnectionMode.Live, executable, project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.Updated, result.Status);
		using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
			targetPath,
			TestContext.Current.CancellationToken));
		var entry = document.RootElement.GetProperty(container).GetProperty("devprojex");
		Assert.Equal("${workspaceFolder}/.env", entry.GetProperty("envFile").GetString());
		Assert.True(entry.GetProperty("sandboxEnabled").GetBoolean());
		Assert.Equal("yes", entry.GetProperty("env").GetProperty("KEEP").GetString());
		Assert.True(entry.GetProperty("dev").GetProperty("watch").GetBoolean());
		Assert.Equal("keep-me", entry.GetProperty("cwd").GetString());
		AssertConnection(
			entry,
			executable,
			project.Path,
			expectLive: true,
			expectVsCodeType: (McpConnectionClient)clientValue == McpConnectionClient.VsCode);
	}

	[Fact]
	public async Task Connect_Codex_PreservesExtendedTableWhenUpdatingCommandAndArguments()
	{
		using var project = new TemporaryDirectory();
		using var codexHome = new TemporaryDirectory();
		var previousExecutable = Path.Combine(project.Path, "old.exe");
		var executable = Path.Combine(project.Path, "DevProjex.exe");
		var configurationPath = codexHome.CreateFile(
			"config.toml",
			$$"""
			model = "gpt-5"

			[mcp_servers.devprojex]
			command  =  "{{previousExecutable.Replace("\\", "\\\\", StringComparison.Ordinal)}}" # keep command note
			args = ["mcp", "--root", "{{project.Path.Replace("\\", "\\\\", StringComparison.Ordinal)}}"] # keep args note
			cwd = "C:/keep/work"
			env_vars = ["KEEP_FROM_HOST"]
			startup_timeout_sec = 21
			tool_timeout_sec = 34
			enabled_tools = ["get_file"]

			[mcp_servers.devprojex.env]
			KEEP = "yes"
			""".ReplaceLineEndings("\r\n"));
		var before = await File.ReadAllTextAsync(configurationPath, TestContext.Current.CancellationToken);
		var reader = new McpCodexUserConfigurationReader(new McpCodexUserConfigurationReaderOptions
		{
			CodexHomeProvider = () => codexHome.Path
		});
		var runner = new RecordingProcessRunner(
			CodexConnection(executable, project.Path, live: true),
			new McpConnectionProcessResult(0, "unused", string.Empty));
		var (service, _) = CreateCommandLineService(project.Path, "codex", runner, reader);

		var result = await service.ConnectAsync(
			Request(McpConnectionClient.Codex, McpConnectionMode.Live, executable, project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.Updated, result.Status);
		Assert.Single(runner.Requests);
		Assert.Equal(["mcp", "get", "devprojex", "--json"], runner.Requests[0].Arguments);
		var after = await File.ReadAllTextAsync(configurationPath, TestContext.Current.CancellationToken);
		Assert.Contains("cwd = \"C:/keep/work\"", after, StringComparison.Ordinal);
		Assert.Contains("env_vars = [\"KEEP_FROM_HOST\"]", after, StringComparison.Ordinal);
		Assert.Contains("startup_timeout_sec = 21", after, StringComparison.Ordinal);
		Assert.Contains("tool_timeout_sec = 34", after, StringComparison.Ordinal);
		Assert.Contains("enabled_tools = [\"get_file\"]", after, StringComparison.Ordinal);
		Assert.Contains("KEEP = \"yes\"", after, StringComparison.Ordinal);
		Assert.Contains("command  =  ", after, StringComparison.Ordinal);
		Assert.Contains("# keep command note", after, StringComparison.Ordinal);
		Assert.Contains("# keep args note", after, StringComparison.Ordinal);
		Assert.Contains(executable.Replace("\\", "\\\\", StringComparison.Ordinal), after, StringComparison.Ordinal);
		Assert.Equal(before, await File.ReadAllTextAsync(
			configurationPath + ".devprojex.bak",
			TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task Connect_Codex_ProjectOverrideLeavesProjectConfigurationUntouched()
	{
		using var project = new TemporaryDirectory();
		using var codexHome = new TemporaryDirectory();
		var executable = Path.Combine(project.Path, "DevProjex.exe");
		var projectConfiguration = project.CreateFile(
			Path.Combine(".codex", "config.toml"),
			"[mcp_servers.devprojex]\ncommand = \"project-override\"\nargs = []\n");
		var projectBefore = await File.ReadAllBytesAsync(
			projectConfiguration,
			TestContext.Current.CancellationToken);
		var configurationPath = codexHome.CreateFile(
			"config.toml",
			$$"""
			[mcp_servers.devprojex]
			command = "old"
			args = ["mcp", "--root", "{{project.Path.Replace("\\", "\\\\", StringComparison.Ordinal)}}"]
			cwd = "C:/keep/work"
			""".ReplaceLineEndings("\r\n"));
		var reader = new McpCodexUserConfigurationReader(new McpCodexUserConfigurationReaderOptions
		{
			CodexHomeProvider = () => codexHome.Path
		});
		var runner = new RecordingProcessRunner(
			CodexConnection("project-override", project.Path, live: true));
		var (service, _) = CreateCommandLineService(project.Path, "codex", runner, reader);

		var result = await service.ConnectAsync(
			Request(McpConnectionClient.Codex, McpConnectionMode.Live, executable, project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.InvalidConfiguration, result.Status);
		Assert.Equal("A project configuration overrides the global registration", result.UserMessage);
		Assert.Equal(
			projectBefore,
			await File.ReadAllBytesAsync(projectConfiguration, TestContext.Current.CancellationToken));
		Assert.Contains(executable.Replace("\\", "\\\\", StringComparison.Ordinal),
			await File.ReadAllTextAsync(configurationPath, TestContext.Current.CancellationToken),
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task Connect_Codex_VerificationFailureRestoresTheCompleteConfiguration()
	{
		using var project = new TemporaryDirectory();
		using var codexHome = new TemporaryDirectory();
		var configurationPath = codexHome.CreateFile(
			"config.toml",
			$$"""
			model = "keep-formatting"

			[mcp_servers.devprojex]
			command = "old"
			args = ["mcp", "--root", "{{project.Path.Replace("\\", "\\\\", StringComparison.Ordinal)}}"]
			cwd = "C:/keep/work"
			""");
		var before = await File.ReadAllTextAsync(configurationPath, TestContext.Current.CancellationToken);
		var reader = new McpCodexUserConfigurationReader(new McpCodexUserConfigurationReaderOptions
		{
			CodexHomeProvider = () => codexHome.Path
		});
		var runner = new RecordingProcessRunner(
			new McpConnectionProcessResult(1, string.Empty, "configuration unavailable"));
		var (service, _) = CreateCommandLineService(project.Path, "codex", runner, reader);

		var result = await service.ConnectAsync(
			Request(
				McpConnectionClient.Codex,
				McpConnectionMode.Live,
				Path.Combine(project.Path, "DevProjex.exe"),
				project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpConnectionStatus.ProcessFailed, result.Status);
		Assert.Equal(before, await File.ReadAllTextAsync(
			configurationPath,
			TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task Connect_CommandLineClients_CarryAppImageExtractionEnvironment()
	{
		using var project = new TemporaryDirectory();
		var previous = Environment.GetEnvironmentVariable("APPIMAGE_EXTRACT_AND_RUN");
		try
		{
			Environment.SetEnvironmentVariable("APPIMAGE_EXTRACT_AND_RUN", "1");
			foreach (var (client, command, option) in new[]
					 {
						 (McpConnectionClient.ClaudeCode, "claude", "-e"),
						 (McpConnectionClient.Codex, "codex", "--env")
					 })
			{
				var runner = new RecordingProcessRunner(new McpConnectionProcessResult(0, "added", string.Empty));
				var (service, _) = CreateCommandLineService(project.Path, command, runner);

				var result = await service.ConnectAsync(
					Request(client, McpConnectionMode.Live, "/tmp/DevProjex.AppImage", project.Path),
					TestContext.Current.CancellationToken);

				Assert.True(result.Succeeded);
				var request = Assert.Single(runner.Requests);
				var optionIndex = request.Arguments.ToList().IndexOf(option);
				Assert.True(optionIndex >= 0);
				Assert.Equal("APPIMAGE_EXTRACT_AND_RUN=1", request.Arguments[optionIndex + 1]);
			}
		}
		finally
		{
			Environment.SetEnvironmentVariable("APPIMAGE_EXTRACT_AND_RUN", previous);
		}
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
		Assert.Contains("Restart Claude Desktop", result.UserMessage, StringComparison.Ordinal);
		Assert.Contains("mcpServers", result.ManualConfiguration, StringComparison.Ordinal);
		Assert.Contains(result.SuggestedConfigPaths!, path => path.Contains("APPDATA", StringComparison.Ordinal));
		Assert.Contains(result.SuggestedConfigPaths!, path => path.Contains("Library/Application Support", StringComparison.Ordinal));
	}

	private static (McpConnectionService Service, string ClientExecutable) CreateCommandLineService(
		string projectRoot,
		string commandName,
		IMcpConnectionProcessRunner runner,
		IMcpCodexUserConfigurationReader? codexUserConfigurationReader = null,
		IMcpClaudeUserConfigurationReader? claudeUserConfigurationReader = null)
	{
		var bin = Path.Combine(projectRoot, "client-bin");
		var clientExecutable = Path.GetFullPath(Path.Combine(bin, commandName + ".exe"));
		var locator = new McpClientExecutableLocator(new McpClientExecutableLocatorOptions
		{
			Platform = TerminalCommandHostPlatform.Windows,
			PathVariableProvider = () => bin,
			FileExists = path => string.Equals(path, clientExecutable, StringComparison.OrdinalIgnoreCase)
		});
		return (CreateService(
			locator,
			runner,
			codexUserConfigurationReader,
			claudeUserConfigurationReader), clientExecutable);
	}

	private static McpConnectionService CreateService(
		McpClientExecutableLocator locator,
		IMcpConnectionProcessRunner runner,
		IMcpCodexUserConfigurationReader? codexUserConfigurationReader = null,
		IMcpClaudeUserConfigurationReader? claudeUserConfigurationReader = null) =>
		new(
			CreateLocalization(),
			locator,
			runner,
			new McpProjectConfigurationWriter(),
			codexUserConfigurationReader ?? new StubCodexUserConfigurationReader(
				new McpCodexUserConfigurationRead(true, null)),
			claudeUserConfigurationReader ?? new StubClaudeUserConfigurationReader(
				new McpClaudeUserConfigurationRead(true, null)));

	private static IMcpCodexUserConfigurationReader CreateCodexConfigurationReader(
		string command,
		IReadOnlyList<string> arguments,
		IReadOnlyDictionary<string, string> environment)
	{
		var configPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "codex-home-contract", "config.toml"));
		var escapedCommand = command.Replace("\\", "\\\\", StringComparison.Ordinal);
		var serializedArguments = string.Join(
			", ",
			arguments.Select(value => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal)}\""));
		var serializedEnvironment = string.Join(
			Environment.NewLine,
			environment.Select(pair =>
				$"{pair.Key} = \"{pair.Value.Replace("\\", "\\\\", StringComparison.Ordinal)}\""));
		var source = $"""
			[mcp_servers.devprojex]
			command = "{escapedCommand}"
			args = [{serializedArguments}]
			[mcp_servers.devprojex.env]
			{serializedEnvironment}
			""";
		return new McpCodexUserConfigurationReader(new McpCodexUserConfigurationReaderOptions
		{
			CodexHomeProvider = () => Path.GetDirectoryName(configPath),
			FileExists = path => string.Equals(path, configPath, StringComparison.OrdinalIgnoreCase),
			ReadAllText = path => string.Equals(path, configPath, StringComparison.OrdinalIgnoreCase)
				? source
				: throw new FileNotFoundException()
		});
	}

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
					: ["mcp", "--root", projectRoot],
				env = (object?)null,
				env_vars = Array.Empty<string>(),
				cwd = (string?)null
			}
		});
		return new McpConnectionProcessResult(0, payload, string.Empty);
	}

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
			["Mcp.Connect.ProjectConfigurationWritten"] = "{0}: configuration was written to {1}",
			["Mcp.Connect.Cursor.NextStep"] = "Restart Cursor",
			["Mcp.Connect.VsCode.NextStep"] = "Enable the server in VS Code",
			["Mcp.Connect.RestartClient"] = "Restart {0}",
			["Mcp.Connect.ManualConfiguration"] = "Manual configuration",
			["Mcp.Connect.ManualConfigurationRestart"] = "Restart Claude Desktop after applying the configuration",
			["Mcp.Connect.Codex.ResponseHint"] = "Read smaller ranges when needed",
			["Mcp.Connect.Codex.ProjectOverride"] = "A project configuration overrides the global registration",
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

	private sealed class StubCodexUserConfigurationReader(McpCodexUserConfigurationRead result)
		: IMcpCodexUserConfigurationReader
	{
		public McpCodexUserConfigurationRead Read() => result;
	}

	private static IMcpClaudeUserConfigurationReader CreateClaudeConfigurationReader(
		string command,
		IReadOnlyList<string> arguments,
		IReadOnlyDictionary<string, string> environment,
		string? rawJson = null)
	{
		rawJson ??= JsonSerializer.Serialize(new
		{
			type = "stdio",
			command,
			args = arguments,
			env = environment
		});
		return new StubClaudeUserConfigurationReader(
			CreateClaudeConfigurationRead(command, arguments, environment, rawJson));
	}

	private static McpClaudeUserConfigurationRead CreateClaudeConfigurationRead(
		string command,
		IReadOnlyList<string> arguments,
		IReadOnlyDictionary<string, string> environment,
		string? rawJson = null)
	{
		rawJson ??= JsonSerializer.Serialize(new
		{
			type = "stdio",
			command,
			args = arguments,
			env = environment
		});
		return new McpClaudeUserConfigurationRead(
			true,
			new McpClaudeUserConnection(command, arguments, environment, rawJson));
	}

	private sealed class StubClaudeUserConfigurationReader(McpClaudeUserConfigurationRead result)
		: IMcpClaudeUserConfigurationReader
	{
		public McpClaudeUserConfigurationRead Read(string projectRoot) => result;
	}

	private sealed class SequenceClaudeUserConfigurationReader(
		params McpClaudeUserConfigurationRead[] results) : IMcpClaudeUserConfigurationReader
	{
		private readonly Queue<McpClaudeUserConfigurationRead> _results = new(results);

		public int ReadCount { get; private set; }

		public McpClaudeUserConfigurationRead Read(string projectRoot)
		{
			ReadCount++;
			return _results.Dequeue();
		}
	}

	private sealed class CancelingAddProcessRunner(
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
				1 => Task.FromResult(new McpConnectionProcessResult(0, "removed", string.Empty)),
				2 => CancelAdd(),
				3 => Task.FromResult(new McpConnectionProcessResult(
					1,
					string.Empty,
					"No MCP server named 'devprojex' found.")),
				4 => Task.FromResult(new McpConnectionProcessResult(0, "restored", string.Empty)),
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
