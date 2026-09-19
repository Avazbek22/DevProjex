using System.ComponentModel;
using System.Security;
using DevProjex.Application.Services;
using DevProjex.Infrastructure.Processes;

namespace DevProjex.Infrastructure.TerminalCommands;

internal sealed record McpClientLaunchProcessRequest(
	string ExecutablePath,
	IReadOnlyList<string> Arguments,
	string WorkingDirectory,
	string? RawArguments = null,
	IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
	bool WaitForDispatcher = false,
	TimeSpan? DispatcherTimeout = null,
	bool CreateNoWindow = false,
	bool CaptureDispatcherOutput = true,
	bool UseShellExecute = false,
	bool DispatcherTimeoutMeansSuccess = false,
	bool TerminateOnCancellation = true);

internal sealed record McpClientLaunchAttemptResult(
	bool Succeeded,
	string? ErrorMessage = null,
	McpClientLaunchFailure Failure = McpClientLaunchFailure.None,
	int? ExitCode = null,
	double? TimeoutSeconds = null);

internal enum McpClientLaunchFailure
{
	None,
	ProcessDidNotStart,
	DispatcherTimedOut,
	DispatcherExited
}

internal static class McpClientLaunchTiming
{
	public static readonly TimeSpan DispatcherObservationTimeout = TimeSpan.FromMilliseconds(750);
}

internal interface IMcpClientLaunchProcessRunner
{
	Task<McpClientLaunchAttemptResult> StartAsync(
		McpClientLaunchProcessRequest request,
		CancellationToken cancellationToken);
}

internal interface IMcpClientExternalLinkLauncher
{
	Task<McpClientLaunchAttemptResult> TryOpenAsync(
		string url,
		string workingDirectory,
		CancellationToken cancellationToken);
}

internal sealed class McpClientLaunchProcessRunner : IMcpClientLaunchProcessRunner
{
	private const int MaximumDispatcherOutputCharacters = 4096;
	private static readonly TimeSpan DefaultDispatcherTimeout = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan OutputCloseTimeout = TimeSpan.FromMilliseconds(500);

	public async Task<McpClientLaunchAttemptResult> StartAsync(
		McpClientLaunchProcessRequest request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		cancellationToken.ThrowIfCancellationRequested();
		if (request.RawArguments is not null && request.Arguments.Count > 0)
			throw new ArgumentException("Raw arguments and structured arguments cannot be combined.", nameof(request));

		Process? process = null;
		CancellationTokenSource? outputReadCancellation = null;
		Task<BoundedTextReadResult>? output = null;
		Task<BoundedTextReadResult>? error = null;
		try
		{
			var captureOutput = request.WaitForDispatcher && request.CaptureDispatcherOutput;
			var startInfo = new ProcessStartInfo
			{
				FileName = request.ExecutablePath,
				UseShellExecute = request.UseShellExecute,
				CreateNoWindow = request.CreateNoWindow,
				WorkingDirectory = request.WorkingDirectory,
				RedirectStandardOutput = captureOutput,
				RedirectStandardError = captureOutput
			};
			if (request.RawArguments is not null)
			{
				startInfo.Arguments = request.RawArguments;
			}
			else
			{
				foreach (var argument in request.Arguments)
					startInfo.ArgumentList.Add(argument);
			}

			if (request.EnvironmentVariables is not null)
			{
				foreach (var (name, value) in request.EnvironmentVariables)
					startInfo.Environment[name] = value;
			}

			process = Process.Start(startInfo);
			if (process is null)
			{
				return new McpClientLaunchAttemptResult(
					false,
					Failure: McpClientLaunchFailure.ProcessDidNotStart);
			}
			if (!request.WaitForDispatcher)
				return new McpClientLaunchAttemptResult(true);

			if (captureOutput)
			{
				outputReadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				output = BoundedTextReader.ReadAsync(
					process.StandardOutput,
					MaximumDispatcherOutputCharacters,
					outputReadCancellation.Token);
				error = BoundedTextReader.ReadAsync(
					process.StandardError,
					MaximumDispatcherOutputCharacters,
					outputReadCancellation.Token);
			}
			using var timeout = new CancellationTokenSource(request.DispatcherTimeout ?? DefaultDispatcherTimeout);
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				timeout.Token);
			try
			{
				await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
			{
				if (request.DispatcherTimeoutMeansSuccess)
				{
					outputReadCancellation?.Cancel();
					if (output is not null && error is not null && outputReadCancellation is not null)
					{
						await ReadCompletedOutputAsync(output, outputReadCancellation).ConfigureAwait(false);
						await ReadCompletedOutputAsync(error, outputReadCancellation).ConfigureAwait(false);
					}
					return new McpClientLaunchAttemptResult(true);
				}

				TryTerminate(process);
				outputReadCancellation?.Cancel();
				if (output is not null && error is not null && outputReadCancellation is not null)
				{
					await ReadCompletedOutputAsync(output, outputReadCancellation).ConfigureAwait(false);
					await ReadCompletedOutputAsync(error, outputReadCancellation).ConfigureAwait(false);
				}
				return new McpClientLaunchAttemptResult(
					false,
					Failure: McpClientLaunchFailure.DispatcherTimedOut,
					TimeoutSeconds: (request.DispatcherTimeout ?? DefaultDispatcherTimeout).TotalSeconds);
			}

			var outputRead = output is null || outputReadCancellation is null
				? default
				: await ReadCompletedOutputAsync(output, outputReadCancellation).ConfigureAwait(false);
			var errorRead = error is null || outputReadCancellation is null
				? default
				: await ReadCompletedOutputAsync(error, outputReadCancellation).ConfigureAwait(false);
			var standardOutput = FormatOutput(outputRead, "stdout");
			var standardError = FormatOutput(errorRead, "stderr");
			if (process.ExitCode == 0)
				return new McpClientLaunchAttemptResult(true);

			var detail = CombineOutput(standardError, standardOutput);
			return new McpClientLaunchAttemptResult(
				false,
				detail.Length == 0 ? null : detail,
				McpClientLaunchFailure.DispatcherExited,
				process.ExitCode);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			if (process is not null && request.TerminateOnCancellation)
				TryTerminate(process);
			throw;
		}
		catch (Exception exception) when (exception is
				   Win32Exception or
				   InvalidOperationException or
				   IOException or
				   UnauthorizedAccessException or
				   SecurityException or
				   NotSupportedException)
		{
			return new McpClientLaunchAttemptResult(false, exception.Message);
		}
		finally
		{
			outputReadCancellation?.Cancel();
			outputReadCancellation?.Dispose();
			process?.Dispose();
		}
	}

	internal static async Task<McpClientLaunchOutputRead> ReadCompletedOutputAsync(
		Task<BoundedTextReadResult> output,
		CancellationTokenSource outputReadCancellation,
		TimeSpan? closeTimeout = null)
	{
		try
		{
			return new McpClientLaunchOutputRead(
				await output.WaitAsync(closeTimeout ?? OutputCloseTimeout).ConfigureAwait(false),
				Completed: true);
		}
		catch (TimeoutException)
		{
			outputReadCancellation.Cancel();
			try
			{
				await output.WaitAsync(closeTimeout ?? OutputCloseTimeout).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is
					   IOException or
					   ObjectDisposedException or
					   OperationCanceledException or
					   TimeoutException)
			{
				ObserveLaterFault(output);
			}
			return default;
		}
		catch (Exception exception) when (exception is IOException or ObjectDisposedException)
		{
			return default;
		}
		catch (OperationCanceledException) when (outputReadCancellation.IsCancellationRequested)
		{
			return default;
		}
	}

	private static void ObserveLaterFault(Task output)
	{
		_ = output.ContinueWith(
			static completed => _ = completed.Exception,
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
			TaskScheduler.Default);
	}

	private static string FormatOutput(McpClientLaunchOutputRead output, string streamName) => !output.Completed
		? string.Empty
		: output.Result.ExceededLimit
		? $"[{streamName} exceeded {MaximumDispatcherOutputCharacters} characters]"
		: output.Result.Text.Trim();

	private static string CombineOutput(string standardError, string standardOutput) =>
		string.Join(
			Environment.NewLine,
			new[] { standardError, standardOutput }.Where(static value => value.Length > 0));

	private static void TryTerminate(Process process)
	{
		try
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
		}
		catch (Exception exception) when (exception is
				   InvalidOperationException or
				   NotSupportedException or
				   Win32Exception)
		{
			// The dispatcher may have exited between the timeout and termination attempt.
		}
	}

	internal readonly record struct McpClientLaunchOutputRead(
		BoundedTextReadResult Result,
		bool Completed);
}

internal sealed class McpClientExternalLinkLauncher(
	IMcpClientLaunchProcessRunner processRunner) : IMcpClientExternalLinkLauncher
{
	private readonly IMcpClientLaunchProcessRunner _processRunner =
		processRunner ?? throw new ArgumentNullException(nameof(processRunner));

	public Task<McpClientLaunchAttemptResult> TryOpenAsync(
		string url,
		string workingDirectory,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(url);
		ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
		return _processRunner.StartAsync(
			new McpClientLaunchProcessRequest(
				url,
				[],
				workingDirectory,
				WaitForDispatcher: true,
				DispatcherTimeout: McpClientLaunchTiming.DispatcherObservationTimeout,
				CaptureDispatcherOutput: false,
				UseShellExecute: true,
				DispatcherTimeoutMeansSuccess: true,
				TerminateOnCancellation: false),
			cancellationToken);
	}
}

internal sealed record McpClientLaunchServiceOptions
{
	public TerminalCommandHostPlatform Platform { get; init; } = TerminalCommandSetupService.DetectPlatform();
	public Func<string?> WindowsCommandProcessorProvider { get; init; } =
		() => Environment.GetEnvironmentVariable("ComSpec");
}

public sealed class McpClientLaunchService : IMcpClientLaunchService
{
	private static readonly string[] LinuxTerminalCommands =
	[
		"x-terminal-emulator",
		"gnome-terminal",
		"konsole",
		"xfce4-terminal",
		"xterm"
	];

	private readonly McpClientExecutableLocator _locator;
	private readonly IMcpClientLaunchProcessRunner _processRunner;
	private readonly IMcpClientExternalLinkLauncher _externalLinkLauncher;
	private readonly McpClientLaunchServiceOptions _options;
	private readonly LocalizationService _localization;

	public McpClientLaunchService(LocalizationService localization)
	{
		_localization = localization ?? throw new ArgumentNullException(nameof(localization));
		_options = new McpClientLaunchServiceOptions();
		_locator = new McpClientExecutableLocator();
		_processRunner = new McpClientLaunchProcessRunner();
		_externalLinkLauncher = new McpClientExternalLinkLauncher(_processRunner);
	}

	internal McpClientLaunchService(
		LocalizationService localization,
		McpClientExecutableLocator locator,
		IMcpClientLaunchProcessRunner processRunner,
		IMcpClientExternalLinkLauncher externalLinkLauncher,
		McpClientLaunchServiceOptions options)
	{
		_localization = localization ?? throw new ArgumentNullException(nameof(localization));
		_locator = locator ?? throw new ArgumentNullException(nameof(locator));
		_processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
		_externalLinkLauncher = externalLinkLauncher ?? throw new ArgumentNullException(nameof(externalLinkLauncher));
		_options = options ?? throw new ArgumentNullException(nameof(options));
	}

	public Task<McpClientLaunchResult> OpenAsync(
		McpClientLaunchRequest request,
		CancellationToken cancellationToken = default)
	{
		ValidateRequest(request);
		cancellationToken.ThrowIfCancellationRequested();
		return request.Client switch
		{
			McpConnectionClient.ClaudeCode => OpenTerminalClientAsync(
				"claude",
				request.ProjectRoot,
				cancellationToken),
			McpConnectionClient.Codex => OpenTerminalClientAsync(
				"codex",
				request.ProjectRoot,
				cancellationToken),
			McpConnectionClient.Cursor => OpenEditorAsync(
				"cursor",
				"cursor",
				request.ProjectRoot,
				cancellationToken),
			McpConnectionClient.VsCode => OpenEditorAsync(
				"vscode",
				"code",
				request.ProjectRoot,
				cancellationToken),
			McpConnectionClient.Json => Task.FromResult(new McpClientLaunchResult(
				McpClientLaunchStatus.UnsupportedClient,
				_localization["Mcp.Open.ManualJsonUnsupported"])),
			_ => throw new ArgumentOutOfRangeException(nameof(request), request.Client, null)
		};
	}

	private async Task<McpClientLaunchResult> OpenTerminalClientAsync(
		string commandName,
		string projectRoot,
		CancellationToken cancellationToken)
	{
		var clientExecutable = _locator.Find(commandName);
		var manualCommand = BuildManualCommand(commandName, clientExecutable, projectRoot, includeProjectPath: false);
		if (clientExecutable is null)
		{
			return new McpClientLaunchResult(
				McpClientLaunchStatus.ClientNotFound,
				_localization.Format("Mcp.Open.ClientNotFound", DisplayNameForCommand(commandName)),
				manualCommand);
		}

		var request = BuildTerminalRequest(clientExecutable, projectRoot);
		if (request is null)
		{
			return new McpClientLaunchResult(
				McpClientLaunchStatus.Failed,
				_localization["Mcp.Open.TerminalNotFound"],
				manualCommand);
		}

		var attempt = await _processRunner.StartAsync(request, cancellationToken).ConfigureAwait(false);
		return attempt.Succeeded
			? new McpClientLaunchResult(McpClientLaunchStatus.Opened)
			: new McpClientLaunchResult(
				McpClientLaunchStatus.Failed,
				FormatAttemptError(attempt, "Mcp.Open.TerminalStartFailed"),
				manualCommand);
	}

	private async Task<McpClientLaunchResult> OpenEditorAsync(
		string scheme,
		string commandName,
		string projectRoot,
		CancellationToken cancellationToken)
	{
		var urlAttempt = await _externalLinkLauncher.TryOpenAsync(
			BuildEditorUrl(scheme, projectRoot),
			projectRoot,
			cancellationToken).ConfigureAwait(false);
		if (urlAttempt.Succeeded)
			return new McpClientLaunchResult(McpClientLaunchStatus.Opened);

		var executable = _locator.Find(commandName);
		var manualCommand = BuildManualCommand(commandName, executable, projectRoot, includeProjectPath: true);
		if (executable is null)
		{
			return new McpClientLaunchResult(
				McpClientLaunchStatus.ClientNotFound,
				CombineErrors(
					_localization["Mcp.Open.EditorStartFailed"],
					FormatAttemptError(urlAttempt, "Mcp.Open.EditorStartFailed"),
					_localization.Format("Mcp.Open.ClientNotFound", DisplayNameForCommand(commandName))),
				manualCommand);
		}

		var cliAttempt = await _processRunner.StartAsync(
			BuildEditorCliRequest(executable, projectRoot),
			cancellationToken).ConfigureAwait(false);
		return cliAttempt.Succeeded
			? new McpClientLaunchResult(McpClientLaunchStatus.Opened)
			: new McpClientLaunchResult(
				McpClientLaunchStatus.Failed,
				CombineErrors(
					_localization["Mcp.Open.EditorStartFailed"],
					FormatAttemptError(urlAttempt, "Mcp.Open.EditorStartFailed"),
					FormatAttemptError(cliAttempt, "Mcp.Open.EditorStartFailed")),
				manualCommand);
	}

	private McpClientLaunchProcessRequest BuildEditorCliRequest(
		string executable,
		string projectRoot)
	{
		var isWindowsCommandShim = _options.Platform == TerminalCommandHostPlatform.Windows &&
			(executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
			 executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));
		if (!isWindowsCommandShim)
		{
			return ObserveDispatcher(
				new McpClientLaunchProcessRequest(executable, [projectRoot], projectRoot));
		}

		var commandProcessor = ResolveWindowsCommandProcessor();
		return new McpClientLaunchProcessRequest(
			commandProcessor,
			[],
			projectRoot,
			RawArguments:
				"/d /v:off /s /c \"\"%DEVPROJEX_MCP_CLIENT%\" \"%DEVPROJEX_MCP_PROJECT_ROOT%\"\"",
			EnvironmentVariables: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["DEVPROJEX_MCP_CLIENT"] = executable,
				["DEVPROJEX_MCP_PROJECT_ROOT"] = projectRoot
			},
			WaitForDispatcher: true,
			DispatcherTimeout: McpClientLaunchTiming.DispatcherObservationTimeout,
			CreateNoWindow: true,
			CaptureDispatcherOutput: false,
			DispatcherTimeoutMeansSuccess: true,
			TerminateOnCancellation: false);
	}

	private McpClientLaunchProcessRequest? BuildTerminalRequest(
		string clientExecutable,
		string projectRoot) => _options.Platform switch
		{
			TerminalCommandHostPlatform.Windows => BuildWindowsTerminalRequest(clientExecutable, projectRoot),
			TerminalCommandHostPlatform.MacOS => BuildMacTerminalRequest(clientExecutable, projectRoot),
			TerminalCommandHostPlatform.Linux => BuildLinuxTerminalRequest(clientExecutable, projectRoot),
			_ => null
		};

	private McpClientLaunchProcessRequest BuildWindowsTerminalRequest(
		string clientExecutable,
		string projectRoot)
	{
		var windowsTerminal = _locator.Find("wt");
		var isCommandShim = clientExecutable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
							clientExecutable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
		if (windowsTerminal is not null)
		{
			if (isCommandShim)
			{
				var shimCommandProcessor = ResolveWindowsCommandProcessor();
				return ObserveDispatcher(
					new McpClientLaunchProcessRequest(
						windowsTerminal,
						[
							"-d", projectRoot, shimCommandProcessor,
							"/d", "/v:off", "/s", "/k", "\"%DEVPROJEX_MCP_CLIENT%\""
						],
						projectRoot,
						EnvironmentVariables: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
						{
							["DEVPROJEX_MCP_CLIENT"] = clientExecutable
						}));
			}

			return ObserveDispatcher(
				new McpClientLaunchProcessRequest(
					windowsTerminal,
					["-d", projectRoot, clientExecutable],
					projectRoot));
		}

		var commandProcessor = ResolveWindowsCommandProcessor();
		var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["DEVPROJEX_MCP_CLIENT"] = clientExecutable,
			["DEVPROJEX_MCP_PROJECT_ROOT"] = projectRoot,
			["DEVPROJEX_MCP_COMMAND_PROCESSOR"] = commandProcessor
		};
		return new McpClientLaunchProcessRequest(
			commandProcessor,
			[],
			projectRoot,
			RawArguments:
				"/d /v:off /s /c start \"\" /d \"%DEVPROJEX_MCP_PROJECT_ROOT%\" " +
				"\"%DEVPROJEX_MCP_COMMAND_PROCESSOR%\" /d /v:off /s /k \"\"%DEVPROJEX_MCP_CLIENT%\"\"",
			EnvironmentVariables: environment,
			WaitForDispatcher: true,
			DispatcherTimeout: McpClientLaunchTiming.DispatcherObservationTimeout,
			CreateNoWindow: true,
			CaptureDispatcherOutput: false,
			DispatcherTimeoutMeansSuccess: true,
			TerminateOnCancellation: false);
	}

	private string ResolveWindowsCommandProcessor()
	{
		var commandProcessor = _options.WindowsCommandProcessorProvider();
		return string.IsNullOrWhiteSpace(commandProcessor) ? "cmd.exe" : commandProcessor;
	}

	private McpClientLaunchProcessRequest? BuildMacTerminalRequest(
		string clientExecutable,
		string projectRoot)
	{
		var osascript = _locator.Find("osascript");
		if (osascript is null)
			return null;

		const string script = """
			on run argv
			  set projectRoot to item 1 of argv
			  set clientPath to item 2 of argv
			  tell application "Terminal"
			    do script "cd " & quoted form of projectRoot & " && exec " & quoted form of clientPath
			    activate
			  end tell
			end run
			""";
		return ObserveDispatcher(
			new McpClientLaunchProcessRequest(
				osascript,
				["-e", script, projectRoot, clientExecutable],
				projectRoot,
				CreateNoWindow: true));
	}

	private McpClientLaunchProcessRequest? BuildLinuxTerminalRequest(
		string clientExecutable,
		string projectRoot)
	{
		foreach (var command in LinuxTerminalCommands)
		{
			var terminal = _locator.Find(command);
			if (terminal is null)
				continue;

			IReadOnlyList<string> arguments = command switch
			{
				"gnome-terminal" => ["--", clientExecutable],
				"xfce4-terminal" => ["--execute", clientExecutable],
				_ => ["-e", clientExecutable]
			};
			return ObserveDispatcher(
				new McpClientLaunchProcessRequest(terminal, arguments, projectRoot));
		}

		return null;
	}

	private static string BuildEditorUrl(string scheme, string projectRoot)
	{
		var path = projectRoot.Replace('\\', '/').TrimEnd('/') + "/";
		var builder = new UriBuilder(scheme, "file")
		{
			Path = path
		};
		return builder.Uri.AbsoluteUri;
	}

	private string BuildManualCommand(
		string commandName,
		string? executable,
		string projectRoot,
		bool includeProjectPath)
	{
		var command = executable ?? commandName;
		var rendered = QuoteForShell(command);
		if (includeProjectPath)
			return $"{rendered} {QuoteForShell(projectRoot)}";

		return _options.Platform == TerminalCommandHostPlatform.Windows
			? $"cd /d {QuoteForShell(projectRoot)} && {rendered}"
			: $"cd {QuoteForShell(projectRoot)} && exec {rendered}";
	}

	private string QuoteForShell(string value) => _options.Platform == TerminalCommandHostPlatform.Windows
		? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
		: $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

	private static McpClientLaunchProcessRequest ObserveDispatcher(
		McpClientLaunchProcessRequest request) => request with
		{
			WaitForDispatcher = true,
			DispatcherTimeout = McpClientLaunchTiming.DispatcherObservationTimeout,
			DispatcherTimeoutMeansSuccess = true,
			TerminateOnCancellation = false
		};

	private static string CombineErrors(string fallback, params string?[] errors)
	{
		var combined = errors
			.Where(static error => !string.IsNullOrWhiteSpace(error))
			.Distinct(StringComparer.Ordinal);
		var result = string.Join(" ", combined);
		return result.Length == 0 ? fallback : result;
	}

	private string FormatAttemptError(
		McpClientLaunchAttemptResult attempt,
		string fallbackKey)
	{
		if (!string.IsNullOrWhiteSpace(attempt.ErrorMessage))
			return attempt.ErrorMessage;

		return attempt.Failure switch
		{
			McpClientLaunchFailure.ProcessDidNotStart => _localization["Mcp.Open.ProcessStartFailed"],
			McpClientLaunchFailure.DispatcherTimedOut => _localization.Format(
				"Mcp.Open.DispatcherTimedOut",
				attempt.TimeoutSeconds.GetValueOrDefault()),
			McpClientLaunchFailure.DispatcherExited => _localization.Format(
				"Mcp.Open.DispatcherExited",
				attempt.ExitCode.GetValueOrDefault()),
			_ => _localization[fallbackKey]
		};
	}

	private static string DisplayNameForCommand(string commandName) => commandName switch
	{
		"claude" => "Claude Code",
		"codex" => "Codex",
		"cursor" => "Cursor",
		"code" => "VS Code",
		_ => commandName
	};

	private static void ValidateRequest(McpClientLaunchRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectRoot);
		if (!Path.IsPathFullyQualified(request.ProjectRoot))
			throw new ArgumentException("The project root must be absolute.", nameof(request));
	}
}
