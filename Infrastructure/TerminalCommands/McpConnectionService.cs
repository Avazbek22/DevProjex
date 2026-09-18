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
	string? StartError = null)
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
					if (_options.FileExists(candidate))
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
				return new McpConnectionProcessResult(null, string.Empty, string.Empty, StartError: "The client process did not start.");

			var standardOutput = BoundedTextReader.ReadAsync(
				process.StandardOutput,
				MaximumOutputCharacters,
				CancellationToken.None);
			var standardError = BoundedTextReader.ReadAsync(
				process.StandardError,
				MaximumOutputCharacters,
				CancellationToken.None);
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
				await BoundedTextReader.ObserveCompletionAsync(standardOutput, standardError).ConfigureAwait(false);
				return new McpConnectionProcessResult(null, string.Empty, string.Empty, TimedOut: true);
			}

			var output = await standardOutput.ConfigureAwait(false);
			var error = await standardError.ConfigureAwait(false);
			return new McpConnectionProcessResult(
				process.ExitCode,
				output.ExceededLimit ? "[stdout exceeded 65536 characters]" : output.Text,
				error.ExceededLimit ? "[stderr exceeded 65536 characters]" : error.Text);
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
			startInfo.ArgumentList.Add("/d");
			startInfo.ArgumentList.Add("/v:off");
			startInfo.ArgumentList.Add("/s");
			startInfo.ArgumentList.Add("/c");
			startInfo.ArgumentList.Add(BuildWindowsCommand(request.ExecutablePath, request.Arguments));
			return startInfo;
		}

		startInfo.FileName = request.ExecutablePath;
		foreach (var argument in request.Arguments)
			startInfo.ArgumentList.Add(argument);
		return startInfo;
	}

	private static string BuildWindowsCommand(
		string executablePath,
		IReadOnlyList<string> arguments)
	{
		var values = new[] { executablePath }.Concat(arguments);
		return string.Join(' ', values.Select(QuoteWindowsCommandArgument));
	}

	private static string QuoteWindowsCommandArgument(string value)
	{
		var escaped = value
			.Replace("%", "%%", StringComparison.Ordinal)
			.Replace("\"", "\"\"", StringComparison.Ordinal);
		return $"\"{escaped}\"";
	}
}

internal sealed record McpProjectConfigurationWriteResult(
	bool Succeeded,
	bool Replaced,
	string TargetPath,
	string? Error = null);

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
			return Failure(projectRoot, "The project root does not exist.");

		var (directoryName, fileName, containerName) = request.Client switch
		{
			McpConnectionClient.Cursor => (".cursor", "mcp.json", "mcpServers"),
			McpConnectionClient.VsCode => (".vscode", "mcp.json", "servers"),
			_ => throw new ArgumentOutOfRangeException(nameof(request), request.Client, null)
		};
		var directory = Path.Combine(projectRoot, directoryName);
		var targetPath = Path.Combine(directory, fileName);
		if (!PathUtility.IsPathInside(targetPath, projectRoot))
			return Failure(targetPath, "The client configuration path is outside the project root.");
		if (Directory.Exists(directory) && IsSymbolicLink(directory))
			return Failure(targetPath, "The client configuration directory is a symbolic link.");
		if (File.Exists(directory))
			return Failure(targetPath, "The client configuration directory path is occupied by a file.");

		JsonObject root;
		if (File.Exists(targetPath))
		{
			try
			{
				var file = new FileInfo(targetPath);
				if (file.Length > MaximumConfigurationBytes)
					return Failure(targetPath, "The existing client configuration is too large to merge safely.");
				var text = await File.ReadAllTextAsync(targetPath, cancellationToken).ConfigureAwait(false);
				root = JsonNode.Parse(
					text,
					nodeOptions: null,
					documentOptions: new JsonDocumentOptions
					{
						AllowTrailingCommas = true,
						CommentHandling = JsonCommentHandling.Skip
					}) as JsonObject ?? throw new JsonException("The root value is not an object.");
			}
			catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
			{
				return Failure(targetPath, $"The existing client configuration could not be read: {exception.Message}");
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
			return Failure(targetPath, $"The '{containerName}' property is not an object.");
		}

		var replaced = servers.ContainsKey("devprojex");
		var printable = McpConnectionFragmentGenerator.Generate(
			request.Client,
			request.Mode,
			request.ExecutablePath,
			projectRoot);
		var generatedRoot = JsonNode.Parse(printable)!.AsObject();
		servers["devprojex"] = generatedRoot[containerName]!["devprojex"]!.DeepClone();
		Directory.CreateDirectory(directory);
		try
		{
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
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure(targetPath, exception.Message);
		}
	}

	private static McpProjectConfigurationWriteResult Failure(string path, string error) =>
		new(false, false, path, error);

	private static string ValidateDestination(string projectRoot, string directory, string path)
	{
		var fullPath = Path.GetFullPath(path);
		if (!PathUtility.IsPathInside(fullPath, projectRoot) || IsSymbolicLink(directory))
			throw new IOException("The client configuration destination is no longer inside the project root.");
		return fullPath;
	}

	private static bool IsSymbolicLink(string path)
	{
		try
		{
			return new DirectoryInfo(path).LinkTarget is not null;
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
		var replaced = remove.Succeeded;
		if (!remove.Succeeded && !IsMissingServer(remove))
			return CreateProcessFailure(request, remove, output, manual);

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
			return CreateProcessFailure(request, add, output, manual);

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
				_localization.Format(
					"Mcp.Connect.ProjectConfigurationFailed",
					DisplayName(request.Client),
					result.Error ?? _localization["Mcp.Connect.UnknownError"]),
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
		string manual)
	{
		var status = processResult.TimedOut
			? McpConnectionStatus.TimedOut
			: McpConnectionStatus.ProcessFailed;
		var detail = processResult.TimedOut
			? _localization["Mcp.Connect.CommandTimedOut"]
			: processResult.StartError ?? processResult.CombinedOutput;
		if (string.IsNullOrWhiteSpace(detail))
			detail = _localization["Mcp.Connect.UnknownError"];
		return new McpConnectionResult(
			status,
			_localization.Format("Mcp.Connect.CommandFailed", DisplayName(request.Client), detail),
			ManualConfiguration: manual,
			CommandOutput: string.Join(Environment.NewLine, output));
	}

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

	private static bool IsMissingServer(McpConnectionProcessResult result)
	{
		if (result.TimedOut || result.StartError is not null)
			return false;
		var text = result.CombinedOutput;
		return text.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
			   text.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
			   text.Contains("no MCP server", StringComparison.OrdinalIgnoreCase) ||
			   text.Contains("no local-scoped MCP server", StringComparison.OrdinalIgnoreCase) ||
			   text.Contains("No server named", StringComparison.OrdinalIgnoreCase);
	}

	private static void AppendOutput(
		ICollection<string> output,
		string operation,
		McpConnectionProcessResult result)
	{
		var text = result.CombinedOutput;
		if (text.Length > 0)
			output.Add($"{operation}: {text}");
	}

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
