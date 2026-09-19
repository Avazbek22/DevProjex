using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using DevProjex.Application.Services;
using DevProjex.Infrastructure.Processes;

namespace DevProjex.Infrastructure.TerminalCommands;

internal sealed record McpConnectionProcessRequest(
	string ExecutablePath,
	IReadOnlyList<string> Arguments,
	string WorkingDirectory,
	TimeSpan Timeout);

internal sealed record McpConnectionProcessResult(
	int? ExitCode,
	string StandardOutput,
	string StandardError,
	bool TimedOut = false,
	string? StartError = null,
	bool OutputIncomplete = false)
{
	public bool Succeeded => ExitCode == 0 && !TimedOut && StartError is null;

	public string CombinedOutput
	{
		get
		{
			var parts = new[] { StandardOutput.Trim(), StandardError.Trim() }
				.Where(static value => value.Length > 0);
			return string.Join(Environment.NewLine, parts);
		}
	}
}

internal interface IMcpConnectionProcessRunner
{
	Task<McpConnectionProcessResult> RunAsync(
		McpConnectionProcessRequest request,
		CancellationToken cancellationToken);
}

internal sealed record McpClientExecutableLocatorOptions
{
	public TerminalCommandHostPlatform Platform { get; init; } = TerminalCommandSetupService.DetectPlatform();
	public Func<string?> PathVariableProvider { get; init; } =
		() => Environment.GetEnvironmentVariable("PATH");
	public Func<string?> PathExtensionsProvider { get; init; } =
		() => Environment.GetEnvironmentVariable("PATHEXT");
	public Func<string, bool> FileExists { get; init; } = File.Exists;
	public Func<string, bool> IsExecutable { get; init; } = IsExecutableFile;

	private static bool IsExecutableFile(string path)
	{
		if (OperatingSystem.IsWindows())
			return true;

		try
		{
			const UnixFileMode executeBits =
				UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
			return (File.GetUnixFileMode(path) & executeBits) != 0;
		}
		catch (Exception exception) when (exception is
				   IOException or
				   UnauthorizedAccessException or
				   PlatformNotSupportedException)
		{
			return false;
		}
	}
}

internal sealed class McpClientExecutableLocator(McpClientExecutableLocatorOptions? options = null)
{
	private readonly McpClientExecutableLocatorOptions _options = options ?? new McpClientExecutableLocatorOptions();

	public string? Find(string commandName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
		var path = _options.PathVariableProvider();
		if (string.IsNullOrWhiteSpace(path))
			return null;

		var suffixes = ResolveSuffixes();
		var separator = _options.Platform == TerminalCommandHostPlatform.Windows ? ';' : ':';
		foreach (var rawDirectory in path.Split(separator, StringSplitOptions.RemoveEmptyEntries))
		{
			var directory = rawDirectory.Trim().Trim('"');
			if (directory.Length == 0)
				continue;

			foreach (var suffix in suffixes)
			{
				try
				{
					var candidate = Path.GetFullPath(Path.Combine(directory, commandName + suffix));
					if (_options.FileExists(candidate) &&
						(_options.Platform == TerminalCommandHostPlatform.Windows || _options.IsExecutable(candidate)))
						return candidate;
				}
				catch (Exception exception) when (exception is
						   ArgumentException or
						   NotSupportedException or
						   PathTooLongException)
				{
					// Ignore malformed PATH entries and continue searching known-good locations.
				}
			}
		}

		return null;
	}

	private IReadOnlyList<string> ResolveSuffixes()
	{
		if (_options.Platform != TerminalCommandHostPlatform.Windows)
			return [string.Empty];

		var configured = (_options.PathExtensionsProvider() ?? string.Empty)
			.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Where(static extension => extension.Length > 1 && extension[0] == '.')
			.Select(static extension => extension.ToLowerInvariant());
		return new[] { ".exe", ".cmd", ".bat", string.Empty }
			.Concat(configured)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}
}

internal sealed class McpConnectionProcessRunner : IMcpConnectionProcessRunner
{
	private const int MaximumOutputCharacters = 64 * 1024;
	private static readonly TimeSpan OutputCloseTimeout = TimeSpan.FromSeconds(1);

	public async Task<McpConnectionProcessResult> RunAsync(
		McpConnectionProcessRequest request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		Process? process = null;
		try
		{
			var startInfo = CreateStartInfo(request);
			process = new Process { StartInfo = startInfo };
			if (!process.Start())
				return new McpConnectionProcessResult(null, string.Empty, string.Empty, StartError: string.Empty);

			using var outputReadCancellation = new CancellationTokenSource();
			var standardOutput = BoundedTextReader.ReadAsync(
				process.StandardOutput,
				MaximumOutputCharacters,
				outputReadCancellation.Token);
			var standardError = BoundedTextReader.ReadAsync(
				process.StandardError,
				MaximumOutputCharacters,
				outputReadCancellation.Token);
			using var timeout = new CancellationTokenSource(request.Timeout);
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				timeout.Token);
			try
			{
				await ExternalProcessLifetime.WaitForExitOrTerminateAsync(process, linked.Token)
					.ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
			{
				var timedOutOutput = await ReadCompletedOutputAsync(
					standardOutput,
					outputReadCancellation).ConfigureAwait(false);
				var timedOutError = await ReadCompletedOutputAsync(
					standardError,
					outputReadCancellation).ConfigureAwait(false);
				return new McpConnectionProcessResult(
					null,
					FormatOutput(timedOutOutput.Result, "stdout"),
					FormatOutput(timedOutError.Result, "stderr"),
					TimedOut: true,
					OutputIncomplete: !timedOutOutput.Completed || !timedOutError.Completed);
			}

			var output = await ReadCompletedOutputAsync(
				standardOutput,
				outputReadCancellation).ConfigureAwait(false);
			var error = await ReadCompletedOutputAsync(
				standardError,
				outputReadCancellation).ConfigureAwait(false);
			return new McpConnectionProcessResult(
				process.ExitCode,
				FormatOutput(output.Result, "stdout"),
				FormatOutput(error.Result, "stderr"),
				OutputIncomplete: !output.Completed || !error.Completed);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception) when (exception is
				   Win32Exception or
				   InvalidOperationException or
				   IOException or
				   UnauthorizedAccessException)
		{
			return new McpConnectionProcessResult(
				null,
				string.Empty,
				string.Empty,
				StartError: exception.Message);
		}
		finally
		{
			process?.Dispose();
		}
	}

	private static async Task<CompletedOutputRead> ReadCompletedOutputAsync(
		Task<BoundedTextReadResult> output,
		CancellationTokenSource outputReadCancellation)
	{
		try
		{
			return new CompletedOutputRead(
				await output.WaitAsync(OutputCloseTimeout).ConfigureAwait(false),
				Completed: true);
		}
		catch (TimeoutException)
		{
			outputReadCancellation.Cancel();
			try
			{
				await output.WaitAsync(OutputCloseTimeout).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is
					   IOException or
					   ObjectDisposedException or
					   OperationCanceledException or
					   TimeoutException)
			{
				// The process outcome is already known; a descendant may still own the pipe.
			}
			return new CompletedOutputRead(default, Completed: false);
		}
		catch (Exception exception) when (exception is IOException or ObjectDisposedException)
		{
			return new CompletedOutputRead(default, Completed: false);
		}
		catch (OperationCanceledException) when (outputReadCancellation.IsCancellationRequested)
		{
			return new CompletedOutputRead(default, Completed: false);
		}
	}

	private static string FormatOutput(BoundedTextReadResult output, string streamName) =>
		output.ExceededLimit
			? $"[{streamName} exceeded {MaximumOutputCharacters} characters]"
			: output.Text ?? string.Empty;

	private static ProcessStartInfo CreateStartInfo(McpConnectionProcessRequest request)
	{
		var startInfo = new ProcessStartInfo
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WorkingDirectory = request.WorkingDirectory
		};

		if (OperatingSystem.IsWindows() &&
			(request.ExecutablePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
			 request.ExecutablePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
		{
			startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
			var command = BuildWindowsCommand(startInfo, request.ExecutablePath, request.Arguments);
			startInfo.Arguments = $"/d /v:on /s /c \"{command}\"";
			return startInfo;
		}

		startInfo.FileName = request.ExecutablePath;
		foreach (var argument in request.Arguments)
			startInfo.ArgumentList.Add(argument);
		return startInfo;
	}

	private static string BuildWindowsCommand(
		ProcessStartInfo startInfo,
		string executablePath,
		IReadOnlyList<string> arguments)
	{
		var values = new[] { executablePath }.Concat(arguments).ToArray();
		var placeholders = new string[values.Length];
		for (var index = 0; index < values.Length; index++)
		{
			var variable = $"DEVPROJEX_MCP_COMMAND_VALUE_{index}";
			startInfo.Environment[variable] = EscapeWindowsDelayedExpansion(values[index]);
			placeholders[index] = $"\"!{variable}!\"";
		}

		return string.Join(' ', placeholders);
	}

	private static string EscapeWindowsDelayedExpansion(string value) =>
		value.Replace("^", "^^", StringComparison.Ordinal)
			.Replace("!", "^!", StringComparison.Ordinal);

	private readonly record struct CompletedOutputRead(
		BoundedTextReadResult Result,
		bool Completed);
}

internal enum McpProjectConfigurationError
{
	None,
	ResourceUnavailable,
	InvalidData,
	AccessDenied,
	OperationFailed
}

internal sealed record McpProjectConfigurationWriteResult(
	bool Succeeded,
	bool Replaced,
	string TargetPath,
	McpProjectConfigurationError Error = McpProjectConfigurationError.None);

internal sealed class McpProjectConfigurationWriter
{
	private const long MaximumConfigurationBytes = 4 * 1024 * 1024;
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		WriteIndented = true
	};

	public async Task<McpProjectConfigurationWriteResult> WriteAsync(
		McpConnectionRequest request,
		CancellationToken cancellationToken)
	{
		var projectRoot = PathUtility.Normalize(request.ProjectRoot);
		if (!Directory.Exists(projectRoot))
			return Failure(projectRoot, McpProjectConfigurationError.ResourceUnavailable);

		var (directoryName, fileName, containerName) = request.Client switch
		{
			McpConnectionClient.Cursor => (".cursor", "mcp.json", "mcpServers"),
			McpConnectionClient.VsCode => (".vscode", "mcp.json", "servers"),
			_ => throw new ArgumentOutOfRangeException(nameof(request), request.Client, null)
		};
		var directory = Path.Combine(projectRoot, directoryName);
		var targetPath = Path.Combine(directory, fileName);
		if (!PathUtility.IsPathInside(targetPath, projectRoot))
			return Failure(targetPath, McpProjectConfigurationError.InvalidData);
		if (Directory.Exists(directory) && IsSymbolicLink(directory))
			return Failure(targetPath, McpProjectConfigurationError.InvalidData);
		if (File.Exists(directory))
			return Failure(targetPath, McpProjectConfigurationError.ResourceUnavailable);
		if (IsSymbolicLink(targetPath))
			return Failure(targetPath, McpProjectConfigurationError.InvalidData);

		JsonObject root;
		if (File.Exists(targetPath))
		{
			try
			{
				var file = new FileInfo(targetPath);
				if (file.Length > MaximumConfigurationBytes)
					return Failure(targetPath, McpProjectConfigurationError.InvalidData);
				var text = await File.ReadAllTextAsync(targetPath, cancellationToken).ConfigureAwait(false);
				root = JsonNode.Parse(text) as JsonObject ?? throw new JsonException();
			}
			catch (JsonException)
			{
				return Failure(targetPath, McpProjectConfigurationError.InvalidData);
			}
			catch (UnauthorizedAccessException)
			{
				return Failure(targetPath, McpProjectConfigurationError.AccessDenied);
			}
			catch (IOException)
			{
				return Failure(targetPath, McpProjectConfigurationError.ResourceUnavailable);
			}
		}
		else
		{
			root = new JsonObject();
		}

		JsonObject servers;
		if (root[containerName] is null)
		{
			servers = new JsonObject();
			root[containerName] = servers;
		}
		else if (root[containerName] is JsonObject existingServers)
		{
			servers = existingServers;
		}
		else
		{
			return Failure(targetPath, McpProjectConfigurationError.InvalidData);
		}

		var replaced = servers.ContainsKey("devprojex");
		var printable = McpConnectionFragmentGenerator.Generate(
			request.Client,
			request.Mode,
			request.ExecutablePath,
			projectRoot);
		var generatedRoot = JsonNode.Parse(printable)!.AsObject();
		servers["devprojex"] = generatedRoot[containerName]!["devprojex"]!.DeepClone();
		try
		{
			Directory.CreateDirectory(directory);
			await AtomicFileOutput.WriteAsync(
				targetPath,
				overwrite: true,
				async (stream, token) =>
				{
					await JsonSerializer.SerializeAsync(stream, root, SerializerOptions, token)
						.ConfigureAwait(false);
				},
				cancellationToken,
				path => ValidateDestination(projectRoot, directory, path)).ConfigureAwait(false);
			return new McpProjectConfigurationWriteResult(true, replaced, targetPath);
		}
		catch (UnauthorizedAccessException)
		{
			return Failure(targetPath, McpProjectConfigurationError.AccessDenied);
		}
		catch (IOException)
		{
			return Failure(targetPath, McpProjectConfigurationError.OperationFailed);
		}
	}

	private static McpProjectConfigurationWriteResult Failure(
		string path,
		McpProjectConfigurationError error) =>
		new(false, false, path, error);

	private static string ValidateDestination(string projectRoot, string directory, string path)
	{
		var fullPath = Path.GetFullPath(path);
		if (!PathUtility.IsPathInside(fullPath, projectRoot) ||
			IsSymbolicLink(directory) ||
			IsSymbolicLink(fullPath))
			throw new IOException("The client configuration destination is no longer inside the project root.");
		return fullPath;
	}

	private static bool IsSymbolicLink(string path)
	{
		return IsSymbolicLink(new FileInfo(path)) ||
			   IsSymbolicLink(new DirectoryInfo(path));
	}

	private static bool IsSymbolicLink(FileSystemInfo entry)
	{
		try
		{
			if (entry.LinkTarget is not null)
				return true;
			return entry.Exists &&
				   (entry.Attributes & FileAttributes.ReparsePoint) != 0;
		}
		catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
		{
			return false;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return true;
		}
	}
}

public sealed class McpConnectionService : IMcpConnectionService
{
	private static readonly TimeSpan ClientCommandTimeout = TimeSpan.FromSeconds(15);
	private static readonly string[] ClaudeDesktopConfigurationPaths =
	[
		@"%APPDATA%\Claude\claude_desktop_config.json",
		"~/Library/Application Support/Claude/claude_desktop_config.json"
	];
	private readonly LocalizationService _localization;
	private readonly McpClientExecutableLocator _locator;
	private readonly IMcpConnectionProcessRunner _processRunner;
	private readonly McpProjectConfigurationWriter _configurationWriter;

	public McpConnectionService(LocalizationService localization)
		: this(
			localization,
			new McpClientExecutableLocator(),
			new McpConnectionProcessRunner(),
			new McpProjectConfigurationWriter())
	{
	}

	internal McpConnectionService(
		LocalizationService localization,
		McpClientExecutableLocator locator,
		IMcpConnectionProcessRunner processRunner,
		McpProjectConfigurationWriter configurationWriter)
	{
		_localization = localization ?? throw new ArgumentNullException(nameof(localization));
		_locator = locator ?? throw new ArgumentNullException(nameof(locator));
		_processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
		_configurationWriter = configurationWriter ?? throw new ArgumentNullException(nameof(configurationWriter));
	}

	public string CreatePrintableConfiguration(McpConnectionRequest request)
	{
		ValidateRequest(request);
		return McpConnectionFragmentGenerator.Generate(
			request.Client,
			request.Mode,
			request.ExecutablePath,
			Path.GetFullPath(request.ProjectRoot));
	}

	public async Task<McpConnectionResult> ConnectAsync(
		McpConnectionRequest request,
		CancellationToken cancellationToken = default)
	{
		ValidateRequest(request);
		return request.Client switch
		{
			McpConnectionClient.ClaudeCode => await ConnectCommandLineClientAsync(
				request,
				"claude",
				"Mcp.Connect.ClaudeCode",
				"claude",
				cancellationToken).ConfigureAwait(false),
			McpConnectionClient.Codex => await ConnectCommandLineClientAsync(
				request,
				"codex",
				"Mcp.Connect.Codex",
				"codex",
				cancellationToken).ConfigureAwait(false),
			McpConnectionClient.Cursor or McpConnectionClient.VsCode =>
				await ConnectProjectClientAsync(request, cancellationToken).ConfigureAwait(false),
			McpConnectionClient.Json => CreateManualResult(request),
			_ => throw new ArgumentOutOfRangeException(nameof(request), request.Client, null)
		};
	}

	private async Task<McpConnectionResult> ConnectCommandLineClientAsync(
		McpConnectionRequest request,
		string commandName,
		string localizationPrefix,
		string nextCommand,
		CancellationToken cancellationToken)
	{
		var executable = _locator.Find(commandName);
		var manual = CreatePrintableConfiguration(request);
		if (executable is null)
		{
			return new McpConnectionResult(
				McpConnectionStatus.ClientNotFound,
				_localization.Format("Mcp.Connect.ClientNotFound", DisplayName(request.Client)),
				ManualConfiguration: manual);
		}

		var output = new List<string>();
		IReadOnlyList<string> removeArguments = request.Client == McpConnectionClient.ClaudeCode
			? ["mcp", "remove", "devprojex", "--scope", "local"]
			: ["mcp", "remove", "devprojex"];
		var remove = await RunClientCommandAsync(
			executable,
			removeArguments,
			request.ProjectRoot,
			cancellationToken).ConfigureAwait(false);
		AppendOutput(output, "remove", remove);
		var missingServer = IsMissingServer(request.Client, remove);
		var replaced = remove.Succeeded && !missingServer;
		if (!remove.Succeeded && !missingServer)
			return CreateProcessFailure(request, remove, output, manual, previousConnectionRemoved: false);

		var arguments = new List<string>
		{
			"mcp", "add", "devprojex", "--", request.ExecutablePath, "mcp", "--root", request.ProjectRoot
		};
		if (request.Mode == McpConnectionMode.Live)
			arguments.Add("--live");
		var add = await RunClientCommandAsync(
			executable,
			arguments,
			request.ProjectRoot,
			cancellationToken).ConfigureAwait(false);
		AppendOutput(output, "add", add);
		if (!add.Succeeded)
			return CreateProcessFailure(request, add, output, manual, previousConnectionRemoved: replaced);

		var status = replaced ? McpConnectionStatus.Updated : McpConnectionStatus.Connected;
		return new McpConnectionResult(
			status,
			_localization[$"{localizationPrefix}.{(replaced ? "Updated" : "Connected")}"],
			NextCommand: nextCommand,
			CommandOutput: string.Join(Environment.NewLine, output),
			Replaced: replaced);
	}

	private async Task<McpConnectionResult> ConnectProjectClientAsync(
		McpConnectionRequest request,
		CancellationToken cancellationToken)
	{
		var result = await _configurationWriter.WriteAsync(request, cancellationToken).ConfigureAwait(false);
		var manual = CreatePrintableConfiguration(request);
		if (!result.Succeeded)
		{
			return new McpConnectionResult(
				McpConnectionStatus.InvalidConfiguration,
				WithManualFallback(_localization.Format(
						"Mcp.Connect.ProjectConfigurationFailed",
						DisplayName(request.Client),
						ProjectConfigurationErrorText(result.Error))),
				ManualConfiguration: manual,
				TargetPath: result.TargetPath);
		}

		return new McpConnectionResult(
			result.Replaced ? McpConnectionStatus.Updated : McpConnectionStatus.Connected,
			_localization.Format(
				"Mcp.Connect.ProjectConfigurationUpdated",
				DisplayName(request.Client),
				PathUtility.GetPortableRelativePath(request.ProjectRoot, result.TargetPath),
				DisplayName(request.Client)),
			NextCommand: _localization.Format("Mcp.Connect.RestartClient", DisplayName(request.Client)),
			TargetPath: result.TargetPath,
			Replaced: result.Replaced);
	}

	private McpConnectionResult CreateManualResult(McpConnectionRequest request) =>
		new(
			McpConnectionStatus.ManualConfiguration,
			_localization["Mcp.Connect.ManualConfiguration"],
			ManualConfiguration: CreatePrintableConfiguration(request),
			SuggestedConfigPaths: ClaudeDesktopConfigurationPaths);

	private McpConnectionResult CreateProcessFailure(
		McpConnectionRequest request,
		McpConnectionProcessResult processResult,
		IReadOnlyList<string> output,
		string manual,
		bool previousConnectionRemoved)
	{
		var status = processResult.TimedOut
			? McpConnectionStatus.TimedOut
			: McpConnectionStatus.ProcessFailed;
		var detail = processResult.TimedOut
			? _localization["Mcp.Connect.CommandTimedOut"]
			: processResult.StartError ?? processResult.CombinedOutput;
		if (string.IsNullOrWhiteSpace(detail))
			detail = _localization["Mcp.Connect.UnknownError"];
		var message = previousConnectionRemoved
			? _localization.Format(
				"Mcp.Connect.CommandFailedAfterRemoval",
				DisplayName(request.Client),
				detail)
			: WithManualFallback(_localization.Format(
				"Mcp.Connect.CommandFailed",
				DisplayName(request.Client),
				detail));
		return new McpConnectionResult(
			status,
			message,
			ManualConfiguration: manual,
			CommandOutput: string.Join(Environment.NewLine, output));
	}

	private string ProjectConfigurationErrorText(McpProjectConfigurationError error) => error switch
	{
		McpProjectConfigurationError.ResourceUnavailable => _localization["Desktop.Error.ResourceUnavailable"],
		McpProjectConfigurationError.InvalidData => _localization["Desktop.Error.InvalidData"],
		McpProjectConfigurationError.AccessDenied => _localization["Desktop.Error.AccessDenied"],
		McpProjectConfigurationError.OperationFailed => _localization["Desktop.Error.OperationFailed"],
		_ => _localization["Mcp.Connect.UnknownError"]
	};

	private Task<McpConnectionProcessResult> RunClientCommandAsync(
		string executablePath,
		IReadOnlyList<string> arguments,
		string projectRoot,
		CancellationToken cancellationToken) =>
		_processRunner.RunAsync(
			new McpConnectionProcessRequest(
				executablePath,
				arguments,
				projectRoot,
				ClientCommandTimeout),
			cancellationToken);

	private static bool IsMissingServer(
		McpConnectionClient client,
		McpConnectionProcessResult result)
	{
		if (result.TimedOut || result.StartError is not null)
			return false;

		return result.CombinedOutput
			.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(static line => line.StartsWith("Error: ", StringComparison.OrdinalIgnoreCase)
				? line[7..]
				: line)
			.Select(static line => line.TrimEnd('.'))
			.Any(line => client switch
			{
				McpConnectionClient.ClaudeCode =>
					line.Equals(
						"No local-scoped MCP server found with name: devprojex",
						StringComparison.OrdinalIgnoreCase) ||
					line.Equals(
						"No MCP server named \"devprojex\" in local scope",
						StringComparison.OrdinalIgnoreCase),
				McpConnectionClient.Codex =>
					line.Equals(
						"No MCP server named 'devprojex' found",
						StringComparison.OrdinalIgnoreCase) ||
					line.Equals(
						"No server named devprojex",
						StringComparison.OrdinalIgnoreCase),
				_ => false
			});
	}

	private void AppendOutput(
		ICollection<string> output,
		string operation,
		McpConnectionProcessResult result)
	{
		var text = result.CombinedOutput;
		if (text.Length > 0)
			output.Add($"{operation}: {text}");
		if (result.OutputIncomplete)
			output.Add(_localization["Mcp.Connect.OutputIncomplete"]);
	}

	private string WithManualFallback(string message) =>
		$"{message} {_localization["Mcp.Connect.ManualFallbackHint"]}";

	private static string DisplayName(McpConnectionClient client) => client switch
	{
		McpConnectionClient.ClaudeCode => "Claude Code",
		McpConnectionClient.Codex => "Codex",
		McpConnectionClient.Cursor => "Cursor",
		McpConnectionClient.VsCode => "VS Code",
		McpConnectionClient.Json => "JSON",
		_ => throw new ArgumentOutOfRangeException(nameof(client), client, null)
	};

	private static void ValidateRequest(McpConnectionRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentException.ThrowIfNullOrWhiteSpace(request.ExecutablePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectRoot);
		if (!Path.IsPathFullyQualified(request.ExecutablePath))
			throw new ArgumentException("The DevProjex executable path must be absolute.", nameof(request));
		if (!Path.IsPathFullyQualified(request.ProjectRoot))
			throw new ArgumentException("The project root must be absolute.", nameof(request));
	}
}
