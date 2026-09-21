using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
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
	JsoncRequiresManualUpdate,
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
			var text = string.Empty;
			try
			{
				var file = new FileInfo(targetPath);
				if (file.Length > MaximumConfigurationBytes)
					return Failure(targetPath, McpProjectConfigurationError.InvalidData);
				text = await File.ReadAllTextAsync(targetPath, cancellationToken).ConfigureAwait(false);
				root = JsonNode.Parse(text) as JsonObject ?? throw new JsonException();
			}
			catch (JsonException)
			{
				if (request.Client == McpConnectionClient.VsCode && IsValidJsonc(text))
					return Failure(targetPath, McpProjectConfigurationError.JsoncRequiresManualUpdate);
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
		var generatedEntry = generatedRoot[containerName]!["devprojex"]!.AsObject();
		if (!replaced)
		{
			servers["devprojex"] = generatedEntry.DeepClone();
		}
		else if (servers["devprojex"] is JsonObject existingEntry)
		{
			existingEntry["command"] = generatedEntry["command"]!.DeepClone();
			existingEntry["args"] = generatedEntry["args"]!.DeepClone();
			if (generatedEntry["env"] is JsonObject requiredEnvironment)
			{
				if (existingEntry["env"] is null)
					existingEntry["env"] = new JsonObject();
				if (existingEntry["env"] is not JsonObject existingEnvironment)
					return Failure(targetPath, McpProjectConfigurationError.InvalidData);
				foreach (var pair in requiredEnvironment)
					existingEnvironment[pair.Key] = pair.Value?.DeepClone();
			}
		}
		else
		{
			return Failure(targetPath, McpProjectConfigurationError.InvalidData);
		}
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

	private static bool IsValidJsonc(string text)
	{
		try
		{
			return JsonNode.Parse(
				text,
				nodeOptions: null,
				documentOptions: new JsonDocumentOptions
				{
					CommentHandling = JsonCommentHandling.Skip,
					AllowTrailingCommas = true
				}) is JsonObject;
		}
		catch (JsonException)
		{
			return false;
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

public sealed record McpConnectionInspection(
	bool Exists,
	string? ExistingProjectRoot,
	bool RequiresProjectReplacement,
	string? ErrorMessage = null)
{
	public bool Succeeded => ErrorMessage is null;
}

public interface IMcpConnectionReplacementService
{
	Task<McpConnectionInspection> InspectAsync(
		McpConnectionRequest request,
		CancellationToken cancellationToken = default);

	Task<McpConnectionResult> ReplaceAsync(
		McpConnectionRequest request,
		string expectedExistingProjectRoot,
		CancellationToken cancellationToken = default);
}

public sealed class McpConnectionService : IMcpConnectionService, IMcpConnectionReplacementService
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
	private readonly IMcpCodexUserConfigurationReader _codexUserConfigurationReader;
	private readonly IMcpClaudeUserConfigurationReader _claudeUserConfigurationReader;

	public McpConnectionService(LocalizationService localization)
		: this(
			localization,
			new McpClientExecutableLocator(),
			new McpConnectionProcessRunner(),
			new McpProjectConfigurationWriter(),
			new McpCodexUserConfigurationReader(),
			new McpClaudeUserConfigurationReader())
	{
	}

	internal McpConnectionService(
		LocalizationService localization,
		McpClientExecutableLocator locator,
		IMcpConnectionProcessRunner processRunner,
		McpProjectConfigurationWriter configurationWriter,
		IMcpCodexUserConfigurationReader? codexUserConfigurationReader = null,
		IMcpClaudeUserConfigurationReader? claudeUserConfigurationReader = null)
	{
		_localization = localization ?? throw new ArgumentNullException(nameof(localization));
		_locator = locator ?? throw new ArgumentNullException(nameof(locator));
		_processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
		_configurationWriter = configurationWriter ?? throw new ArgumentNullException(nameof(configurationWriter));
		_codexUserConfigurationReader =
			codexUserConfigurationReader ?? new McpCodexUserConfigurationReader();
		_claudeUserConfigurationReader =
			claudeUserConfigurationReader ?? new McpClaudeUserConfigurationReader();
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

	public async Task<McpConnectionInspection> InspectAsync(
		McpConnectionRequest request,
		CancellationToken cancellationToken = default)
	{
		ValidateRequest(request);
		if (request.Client is not (McpConnectionClient.ClaudeCode or McpConnectionClient.Codex))
			return new McpConnectionInspection(false, null, false);

		var commandName = request.Client == McpConnectionClient.ClaudeCode ? "claude" : "codex";
		var executable = _locator.Find(commandName);
		if (executable is null)
		{
			return new McpConnectionInspection(
				false,
				null,
				false,
				_localization.Format("Mcp.Connect.ClientNotFound", DisplayName(request.Client)));
		}

		var read = await ReadExistingConnectionAsync(
			request,
			executable,
			cancellationToken).ConfigureAwait(false);
		if (read.Error is not null)
			return new McpConnectionInspection(false, null, false, read.Error);
		if (read.Snapshot is null)
			return new McpConnectionInspection(false, null, false);

		var differentProject = request.Client == McpConnectionClient.Codex &&
			!PathsEqual(read.Snapshot.ProjectRoot, request.ProjectRoot);
		return new McpConnectionInspection(
			true,
			read.Snapshot.ProjectRoot,
			differentProject);
	}

	public Task<McpConnectionResult> ReplaceAsync(
		McpConnectionRequest request,
		string expectedExistingProjectRoot,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedExistingProjectRoot);
		ValidateRequest(request);
		return ConnectCommandLineClientAsync(
			request,
			request.Client == McpConnectionClient.ClaudeCode ? "claude" : "codex",
			request.Client == McpConnectionClient.ClaudeCode ? "Mcp.Connect.ClaudeCode" : "Mcp.Connect.Codex",
			request.Client == McpConnectionClient.ClaudeCode ? "claude" : "codex",
			cancellationToken,
			expectedExistingProjectRoot: Path.GetFullPath(expectedExistingProjectRoot));
	}

	private async Task<McpConnectionResult> ConnectCommandLineClientAsync(
		McpConnectionRequest request,
		string commandName,
		string localizationPrefix,
		string nextCommand,
		CancellationToken cancellationToken,
		string? expectedExistingProjectRoot = null)
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
		var existing = await ReadExistingConnectionAsync(
			request,
			executable,
			cancellationToken).ConfigureAwait(false);
		AppendOutput(output, "get", existing.ProcessResult);
		if (existing.Error is not null)
		{
			if (existing.ProcessResult.Succeeded)
			{
				return new McpConnectionResult(
					McpConnectionStatus.ProcessFailed,
					WithManualFallback(existing.Error),
					ManualConfiguration: manual,
					CommandOutput: string.Join(Environment.NewLine, output));
			}
			return CreateProcessFailure(
				request,
				existing.ProcessResult,
				output,
				manual,
				previousConnectionRemoved: false);
		}

		var snapshot = existing.Snapshot;
		if (request.Client == McpConnectionClient.Codex &&
			snapshot is not null &&
			!PathsEqual(snapshot.ProjectRoot, request.ProjectRoot))
		{
			if (expectedExistingProjectRoot is null)
			{
				return new McpConnectionResult(
					McpConnectionStatus.InvalidConfiguration,
					_localization.Format(
						"Mcp.Connect.ReplaceRequired",
						snapshot.ProjectRoot,
						request.ProjectRoot),
					ManualConfiguration: manual,
					CommandOutput: string.Join(Environment.NewLine, output));
			}
			if (!PathsEqual(snapshot.ProjectRoot, expectedExistingProjectRoot))
			{
				return new McpConnectionResult(
					McpConnectionStatus.InvalidConfiguration,
					_localization["Mcp.Connect.ConnectionChanged"],
					ManualConfiguration: manual,
					CommandOutput: string.Join(Environment.NewLine, output));
			}
		}
		if (request.Client == McpConnectionClient.Codex &&
			snapshot is not null &&
			existing.CodexConfiguration?.Connection?.HasExtendedFields == true &&
			_codexUserConfigurationReader is IMcpCodexUserConfigurationStore codexStore)
		{
			return await UpdateExtendedCodexConnectionAsync(
				request,
				executable,
				localizationPrefix,
				nextCommand,
				manual,
				output,
				existing.CodexConfiguration,
				codexStore,
				cancellationToken).ConfigureAwait(false);
		}

		IReadOnlyList<string> removeArguments = request.Client == McpConnectionClient.ClaudeCode
			? ["mcp", "remove", "-s", "local", "devprojex"]
			: ["mcp", "remove", "devprojex"];
		var replaced = snapshot is not null;
		if (replaced)
		{
			var remove = await RunClientCommandAsync(
				executable,
				removeArguments,
				request.ProjectRoot,
				cancellationToken).ConfigureAwait(false);
			AppendOutput(output, "remove", remove);
			if (!remove.Succeeded)
				return CreateProcessFailure(request, remove, output, manual, previousConnectionRemoved: false);
		}

		var arguments = CreateAddArguments(request.Client, request.ExecutablePath, request.ProjectRoot, request.Mode);
		McpConnectionProcessResult add;
		try
		{
			add = await RunClientCommandAsync(
				executable,
				arguments,
				request.ProjectRoot,
				cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && snapshot is not null)
		{
			await TryRestoreConnectionAsync(
				request,
				executable,
				removeArguments,
				snapshot,
				output).ConfigureAwait(false);
			throw;
		}
		AppendOutput(output, "add", add);
		if (!add.Succeeded)
		{
			if (request.Client == McpConnectionClient.ClaudeCode &&
				snapshot is null &&
				IsClaudeAlreadyExists(add) &&
				TryReadMatchingClaudeConnection(request, out _))
			{
				return new McpConnectionResult(
					McpConnectionStatus.Updated,
					_localization[$"{localizationPrefix}.Updated"],
					NextCommand: nextCommand,
					CommandOutput: string.Join(Environment.NewLine, output),
					Replaced: true);
			}
			if (snapshot is null)
				return CreateProcessFailure(request, add, output, manual, previousConnectionRemoved: false);
			var restored = await TryRestoreConnectionAsync(
				request,
				executable,
				removeArguments,
				snapshot,
				output).ConfigureAwait(false);
			return CreateProcessFailure(
				request,
				add,
				output,
				manual,
				previousConnectionRemoved: true,
				previousConnectionRestored: restored);
		}

		var status = replaced ? McpConnectionStatus.Updated : McpConnectionStatus.Connected;
		var message = _localization[$"{localizationPrefix}.{(replaced ? "Updated" : "Connected")}"];
		if (request.Client == McpConnectionClient.Codex)
			message += " " + _localization["Mcp.Connect.Codex.ResponseHint"];
		return new McpConnectionResult(
			status,
			message,
			NextCommand: nextCommand,
			CommandOutput: string.Join(Environment.NewLine, output),
			Replaced: replaced);
	}

	private async Task<McpConnectionResult> UpdateExtendedCodexConnectionAsync(
		McpConnectionRequest request,
		string executable,
		string localizationPrefix,
		string nextCommand,
		string manual,
		ICollection<string> output,
		McpCodexUserConfigurationRead configuration,
		IMcpCodexUserConfigurationStore store,
		CancellationToken cancellationToken)
	{
		var arguments = CreateServerArguments(request.ProjectRoot, request.Mode);
		var requiredEnvironment = McpConnectionFragmentGenerator.GetRequiredServerEnvironment();
		var write = await store.UpdateConnectionAsync(
			configuration,
			request.ExecutablePath,
			arguments,
			requiredEnvironment,
			cancellationToken).ConfigureAwait(false);
		if (!write.Succeeded)
		{
			return new McpConnectionResult(
				McpConnectionStatus.ProcessFailed,
				WithManualFallback(_localization.Format(
					"Mcp.Connect.CommandFailed",
					DisplayName(request.Client),
					write.Error ?? _localization["Mcp.Connect.UnknownError"])),
				ManualConfiguration: manual,
				Replaced: true);
		}

		var verify = await RunClientCommandAsync(
			executable,
			["mcp", "get", "devprojex", "--json"],
			request.ProjectRoot,
			cancellationToken).ConfigureAwait(false);
		AppendOutput(output, "get", verify);
		if (!verify.Succeeded || !TryParseCodexConnection(verify.StandardOutput, out var effective))
		{
			using var recoveryCts = new CancellationTokenSource(ClientCommandTimeout);
			var restored = await store.RestoreAsync(configuration, recoveryCts.Token).ConfigureAwait(false);
			return CreateProcessFailure(
				request,
				verify,
				output.ToArray(),
				manual,
				previousConnectionRemoved: true,
				previousConnectionRestored: restored);
		}
		if (!ConnectionMatches(effective, request.ExecutablePath, arguments, requiredEnvironment))
		{
			return new McpConnectionResult(
				McpConnectionStatus.InvalidConfiguration,
				_localization["Mcp.Connect.Codex.ProjectOverride"],
				ManualConfiguration: manual,
				CommandOutput: string.Join(Environment.NewLine, output),
				Replaced: true);
		}

		var message = _localization[$"{localizationPrefix}.Updated"];
		if (request.Client == McpConnectionClient.Codex)
			message += " " + _localization["Mcp.Connect.Codex.ResponseHint"];
		return new McpConnectionResult(
			McpConnectionStatus.Updated,
			message,
			NextCommand: nextCommand,
			CommandOutput: string.Join(Environment.NewLine, output),
			Replaced: true);
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

		var clientName = DisplayName(request.Client);
		var relativePath = PathUtility.GetPortableRelativePath(request.ProjectRoot, result.TargetPath);
		var nextStepKey = request.Client == McpConnectionClient.VsCode
			? "Mcp.Connect.VsCode.NextStep"
			: "Mcp.Connect.Cursor.NextStep";
		return new McpConnectionResult(
			result.Replaced ? McpConnectionStatus.Updated : McpConnectionStatus.Connected,
			_localization.Format("Mcp.Connect.ProjectConfigurationWritten", clientName, relativePath),
			NextCommand: _localization[nextStepKey],
			TargetPath: result.TargetPath,
			Replaced: result.Replaced);
	}

	private McpConnectionResult CreateManualResult(McpConnectionRequest request) =>
		new(
			McpConnectionStatus.ManualConfiguration,
			_localization["Mcp.Connect.ManualConfigurationRestart"],
			ManualConfiguration: CreatePrintableConfiguration(request),
			SuggestedConfigPaths: ClaudeDesktopConfigurationPaths);

	private McpConnectionResult CreateProcessFailure(
		McpConnectionRequest request,
		McpConnectionProcessResult processResult,
		IReadOnlyList<string> output,
		string manual,
		bool previousConnectionRemoved,
		bool previousConnectionRestored = false)
	{
		var status = processResult.TimedOut
			? McpConnectionStatus.TimedOut
			: McpConnectionStatus.ProcessFailed;
		var detail = processResult.TimedOut
			? _localization["Mcp.Connect.CommandTimedOut"]
			: processResult.StartError ?? processResult.CombinedOutput;
		if (string.IsNullOrWhiteSpace(detail))
			detail = _localization["Mcp.Connect.UnknownError"];
		var message = previousConnectionRestored
			? _localization.Format(
				"Mcp.Connect.CommandFailedRestored",
				DisplayName(request.Client),
				detail)
			: previousConnectionRemoved
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
		McpProjectConfigurationError.JsoncRequiresManualUpdate =>
			_localization["Mcp.Connect.VsCodeJsoncManual"],
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

	private async Task<ExistingConnectionRead> ReadExistingConnectionAsync(
		McpConnectionRequest request,
		string executable,
		CancellationToken cancellationToken)
	{
		if (request.Client == McpConnectionClient.ClaudeCode)
		{
			var read = _claudeUserConfigurationReader.Read(request.ProjectRoot);
			var localRead = new McpConnectionProcessResult(0, string.Empty, string.Empty);
			if (!read.Succeeded)
			{
				return new ExistingConnectionRead(
					localRead,
					null,
					_localization.Format(
						"Mcp.Connect.CommandFailed",
						DisplayName(request.Client),
						_localization["Mcp.Connect.InspectionFailed"]));
			}
			if (read.Connection is null)
				return new ExistingConnectionRead(localRead, null, null);
			if (!TryCreateSnapshot(
					read.Connection.Command,
					read.Connection.Arguments,
					read.Connection.Environment,
					out var claudeSnapshot,
					read.Connection.RawJson))
			{
				return new ExistingConnectionRead(
					localRead,
					null,
					_localization.Format(
						"Mcp.Connect.CommandFailed",
						DisplayName(request.Client),
						_localization["Mcp.Connect.InspectionFailed"]));
			}
			return new ExistingConnectionRead(localRead, claudeSnapshot, null);
		}

		if (request.Client == McpConnectionClient.Codex)
		{
			var read = _codexUserConfigurationReader.Read();
			var localRead = new McpConnectionProcessResult(0, string.Empty, string.Empty);
			if (!read.Succeeded)
			{
				return new ExistingConnectionRead(
					localRead,
					null,
					_localization.Format(
						"Mcp.Connect.CommandFailed",
						DisplayName(request.Client),
						_localization["Mcp.Connect.InspectionFailed"]));
			}
			if (read.Connection is null)
				return new ExistingConnectionRead(localRead, null, null);
			if (!TryCreateSnapshot(
					read.Connection.Command,
					read.Connection.Arguments,
					read.Connection.Environment,
					out var codexSnapshot))
			{
				return new ExistingConnectionRead(
					localRead,
					null,
					_localization.Format(
						"Mcp.Connect.CommandFailed",
						DisplayName(request.Client),
						_localization["Mcp.Connect.InspectionFailed"]));
			}
			return new ExistingConnectionRead(localRead, codexSnapshot, null, read);
		}

		IReadOnlyList<string> arguments = ["mcp", "get", "devprojex", "--json"];
		var result = await RunClientCommandAsync(
			executable,
			arguments,
			request.ProjectRoot,
			cancellationToken).ConfigureAwait(false);
		if (IsMissingServer(request.Client, result, McpClientCommandOperation.Get))
			return new ExistingConnectionRead(result, null, null);
		if (!result.Succeeded)
		{
			var detail = result.TimedOut
				? _localization["Mcp.Connect.CommandTimedOut"]
				: result.StartError ?? result.CombinedOutput;
			return new ExistingConnectionRead(
				result,
				null,
				_localization.Format(
					"Mcp.Connect.CommandFailed",
					DisplayName(request.Client),
					string.IsNullOrWhiteSpace(detail) ? _localization["Mcp.Connect.UnknownError"] : detail));
		}

		if (!TryParseExistingConnection(request.Client, result.StandardOutput, out var snapshot))
		{
			return new ExistingConnectionRead(
				result,
				null,
				_localization.Format(
					"Mcp.Connect.CommandFailed",
					DisplayName(request.Client),
					_localization["Mcp.Connect.InspectionFailed"]));
		}
		return new ExistingConnectionRead(result, snapshot, null);
	}

	private async Task<bool> TryRestoreConnectionAsync(
		McpConnectionRequest request,
		string executable,
		IReadOnlyList<string> removeArguments,
		CommandLineConnectionSnapshot snapshot,
		ICollection<string> output)
	{
		using var recoveryCts = new CancellationTokenSource(ClientCommandTimeout);
		var cleanup = await RunClientCommandAsync(
			executable,
			removeArguments,
			request.ProjectRoot,
			recoveryCts.Token).ConfigureAwait(false);
		AppendOutput(output, "rollback-remove", cleanup);
		if (!cleanup.Succeeded &&
			!IsMissingServer(request.Client, cleanup, McpClientCommandOperation.Remove))
			return false;

		var restore = await RunClientCommandAsync(
			executable,
			CreateRestoreArguments(request.Client, snapshot),
			request.ProjectRoot,
			recoveryCts.Token).ConfigureAwait(false);
		AppendOutput(output, "rollback-add", restore);
		return restore.Succeeded;
	}

	private static List<string> CreateAddArguments(
		McpConnectionClient client,
		string executablePath,
		string projectRoot,
		McpConnectionMode mode)
	{
		var arguments = client == McpConnectionClient.ClaudeCode
			? new List<string> { "mcp", "add", "--scope", "local", "devprojex", "--" }
			: ["mcp", "add", "devprojex", "--"];
		var requiredEnvironment = McpConnectionFragmentGenerator.GetRequiredServerEnvironment();
		if (requiredEnvironment.Count > 0)
		{
			arguments.RemoveAt(arguments.Count - 1);
			foreach (var pair in requiredEnvironment)
			{
				arguments.Add(client == McpConnectionClient.ClaudeCode ? "-e" : "--env");
				arguments.Add($"{pair.Key}={pair.Value}");
			}
			arguments.Add("--");
		}
		arguments.AddRange([executablePath, "mcp", "--root", projectRoot]);
		if (mode == McpConnectionMode.Live)
			arguments.Add("--live");
		return arguments;
	}

	private static string[] CreateServerArguments(string projectRoot, McpConnectionMode mode) =>
		mode == McpConnectionMode.Live
			? ["mcp", "--root", projectRoot, "--live"]
			: ["mcp", "--root", projectRoot];

	private static bool ConnectionMatches(
		CommandLineConnectionSnapshot actual,
		string expectedCommand,
		IReadOnlyList<string> expectedArguments,
		IReadOnlyDictionary<string, string> requiredEnvironment)
	{
		if (!string.Equals(
				actual.Command,
				expectedCommand,
				OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
			!actual.Arguments.SequenceEqual(expectedArguments, StringComparer.Ordinal))
		{
			return false;
		}
		return requiredEnvironment.All(pair =>
			actual.Environment.TryGetValue(pair.Key, out var value) &&
			string.Equals(value, pair.Value, StringComparison.Ordinal));
	}

	private static List<string> CreateRestoreArguments(
		McpConnectionClient client,
		CommandLineConnectionSnapshot snapshot)
	{
		if (client == McpConnectionClient.ClaudeCode)
		{
			var payload = snapshot.RawConfiguration ?? JsonSerializer.Serialize(new
			{
				type = "stdio",
				command = snapshot.Command,
				args = snapshot.Arguments,
				env = snapshot.Environment
			});
			return ["mcp", "add-json", "--scope", "local", "devprojex", payload];
		}

		var arguments = new List<string> { "mcp", "add", "devprojex" };
		foreach (var pair in snapshot.Environment)
			arguments.AddRange(["--env", $"{pair.Key}={pair.Value}"]);
		arguments.Add("--");
		arguments.Add(snapshot.Command);
		arguments.AddRange(snapshot.Arguments);
		return arguments;
	}

	private static bool TryParseExistingConnection(
		McpConnectionClient client,
		string output,
		out CommandLineConnectionSnapshot snapshot)
	{
		if (client == McpConnectionClient.Codex)
			return TryParseCodexConnection(output, out snapshot);
		return TryParseClaudeConnection(output, out snapshot);
	}

	private static bool TryParseCodexConnection(
		string output,
		out CommandLineConnectionSnapshot snapshot)
	{
		snapshot = default!;
		try
		{
			using var document = JsonDocument.Parse(output);
			var transport = document.RootElement.GetProperty("transport");
			if (!string.Equals(transport.GetProperty("type").GetString(), "stdio", StringComparison.OrdinalIgnoreCase))
				return false;
			var command = transport.GetProperty("command").GetString();
			var arguments = transport.GetProperty("args")
				.EnumerateArray()
				.Select(static value => value.GetString() ?? string.Empty)
				.ToArray();
			var environment = new Dictionary<string, string>(StringComparer.Ordinal);
			if (transport.TryGetProperty("env", out var environmentElement) &&
				environmentElement.ValueKind != JsonValueKind.Null)
			{
				if (environmentElement.ValueKind != JsonValueKind.Object)
					return false;
				foreach (var property in environmentElement.EnumerateObject())
				{
					if (property.Value.ValueKind != JsonValueKind.String)
						return false;
					environment[property.Name] = property.Value.GetString() ?? string.Empty;
				}
			}
			else if (transport.TryGetProperty("env_vars", out environmentElement) &&
				environmentElement.ValueKind == JsonValueKind.Object)
			{
				foreach (var property in environmentElement.EnumerateObject())
				{
					if (property.Value.ValueKind != JsonValueKind.String)
						return false;
					environment[property.Name] = property.Value.GetString() ?? string.Empty;
				}
			}
			return TryCreateSnapshot(
				command,
				arguments,
				environment,
				out snapshot);
		}
		catch (Exception exception) when (exception is JsonException or InvalidOperationException)
		{
			return false;
		}
	}

	private static bool TryParseClaudeConnection(
		string output,
		out CommandLineConnectionSnapshot snapshot)
	{
		snapshot = default!;
		if (TryParseClaudeJsonConnection(output, out snapshot))
			return true;

		var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
		var command = lines.FirstOrDefault(static line => line.TrimStart().StartsWith("Command: ", StringComparison.Ordinal));
		var args = lines.FirstOrDefault(static line => line.TrimStart().StartsWith("Args: ", StringComparison.Ordinal));
		if (command is null || args is null)
			return false;
		var commandValue = command.TrimStart()["Command: ".Length..].Trim();
		var argumentText = args.TrimStart()["Args: ".Length..].Trim();
		const string prefix = "mcp --root ";
		if (!argumentText.StartsWith(prefix, StringComparison.Ordinal))
			return false;
		var live = argumentText.EndsWith(" --live", StringComparison.Ordinal);
		var root = live
			? argumentText[prefix.Length..^" --live".Length]
			: argumentText[prefix.Length..];
		return TryCreateSnapshot(
			commandValue,
			live ? ["mcp", "--root", root, "--live"] : ["mcp", "--root", root],
			new Dictionary<string, string>(StringComparer.Ordinal),
			out snapshot);
	}

	private static bool TryParseClaudeJsonConnection(
		string output,
		out CommandLineConnectionSnapshot snapshot)
	{
		snapshot = default!;
		try
		{
			using var document = JsonDocument.Parse(output);
			var root = document.RootElement;
			if (root.TryGetProperty("server", out var server))
				root = server;
			if (root.TryGetProperty("type", out var type) &&
				!string.Equals(type.GetString(), "stdio", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			var command = root.GetProperty("command").GetString();
			var arguments = root.GetProperty("args")
				.EnumerateArray()
				.Select(static value => value.GetString() ?? string.Empty)
				.ToArray();
			var environment = new Dictionary<string, string>(StringComparer.Ordinal);
			if (root.TryGetProperty("env", out var environmentElement))
			{
				if (environmentElement.ValueKind != JsonValueKind.Object)
					return false;
				foreach (var property in environmentElement.EnumerateObject())
				{
					if (property.Value.ValueKind != JsonValueKind.String)
						return false;
					environment[property.Name] = property.Value.GetString() ?? string.Empty;
				}
			}
			return TryCreateSnapshot(command, arguments, environment, out snapshot);
		}
		catch (Exception exception) when (exception is JsonException or InvalidOperationException)
		{
			return false;
		}
	}

	private static bool TryCreateSnapshot(
		string? command,
		IReadOnlyList<string> arguments,
		IReadOnlyDictionary<string, string> environment,
		out CommandLineConnectionSnapshot snapshot,
		string? rawConfiguration = null)
	{
		snapshot = default!;
		var rootIndex = arguments
			.Select((value, index) => (value, index))
			.FirstOrDefault(static pair => pair.value == "--root")
			.index;
		if (string.IsNullOrWhiteSpace(command) ||
			arguments.Count < 3 ||
			arguments[0] != "mcp" ||
			rootIndex <= 0 ||
			rootIndex + 1 >= arguments.Count ||
			!Path.IsPathFullyQualified(arguments[rootIndex + 1]))
			return false;
		snapshot = new CommandLineConnectionSnapshot(
			command,
			arguments.ToArray(),
			new Dictionary<string, string>(environment, StringComparer.Ordinal),
			Path.GetFullPath(arguments[rootIndex + 1]),
			arguments.Contains("--live", StringComparer.Ordinal)
				? McpConnectionMode.Live
				: McpConnectionMode.Standard,
			rawConfiguration);
		return true;
	}

	private static bool PathsEqual(string left, string right) =>
		string.Equals(
			Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

	private static bool IsMissingServer(
		McpConnectionClient client,
		McpConnectionProcessResult result,
		McpClientCommandOperation operation)
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
					IsClaudeMissingServerLine(line, operation),
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

	private static bool IsClaudeMissingServerLine(
		string line,
		McpClientCommandOperation operation)
	{
		if (operation == McpClientCommandOperation.Remove)
		{
			return line.Equals(
					"No local-scoped MCP server found with name: devprojex",
					StringComparison.OrdinalIgnoreCase) ||
				line.Equals(
					"No MCP server named \"devprojex\" in local scope",
					StringComparison.OrdinalIgnoreCase);
		}

		return line.Equals(
				"No MCP server found with name: devprojex",
				StringComparison.OrdinalIgnoreCase) ||
			line.Equals(
				"No MCP server found with name: \"devprojex\"",
				StringComparison.OrdinalIgnoreCase) ||
			line.Equals(
				"No MCP server found with name: 'devprojex'",
				StringComparison.OrdinalIgnoreCase) ||
			line.Equals(
				"No local-scoped MCP server found with name: devprojex",
				StringComparison.OrdinalIgnoreCase) ||
			line.Equals(
				"No MCP server named \"devprojex\" in local scope",
				StringComparison.OrdinalIgnoreCase);
	}

	private bool TryReadMatchingClaudeConnection(
		McpConnectionRequest request,
		out CommandLineConnectionSnapshot snapshot)
	{
		snapshot = default!;
		var read = _claudeUserConfigurationReader.Read(request.ProjectRoot);
		if (!read.Succeeded || read.Connection is null ||
			!TryCreateSnapshot(
				read.Connection.Command,
				read.Connection.Arguments,
				read.Connection.Environment,
				out snapshot,
				read.Connection.RawJson))
		{
			return false;
		}
		return ConnectionMatches(
			snapshot,
			request.ExecutablePath,
			CreateServerArguments(request.ProjectRoot, request.Mode),
			McpConnectionFragmentGenerator.GetRequiredServerEnvironment());
	}

	private static bool IsClaudeAlreadyExists(McpConnectionProcessResult result)
	{
		if (result.TimedOut || result.StartError is not null)
			return false;
		return result.CombinedOutput
			.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(static line => line.StartsWith("Error: ", StringComparison.OrdinalIgnoreCase)
				? line[7..]
				: line)
			.Any(static line =>
				line.Equals(
					"MCP server devprojex already exists in local config",
					StringComparison.OrdinalIgnoreCase) ||
				line.Equals(
					"MCP server with name devprojex already exists in local scope",
					StringComparison.OrdinalIgnoreCase));
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

	private sealed record CommandLineConnectionSnapshot(
		string Command,
		IReadOnlyList<string> Arguments,
		IReadOnlyDictionary<string, string> Environment,
		string ProjectRoot,
		McpConnectionMode Mode,
		string? RawConfiguration);

	private enum McpClientCommandOperation
	{
		Get,
		Remove
	}

	private sealed record ExistingConnectionRead(
		McpConnectionProcessResult ProcessResult,
		CommandLineConnectionSnapshot? Snapshot,
		string? Error,
		McpCodexUserConfigurationRead? CodexConfiguration = null);
}
