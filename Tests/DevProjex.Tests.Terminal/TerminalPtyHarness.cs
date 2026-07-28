using System.Diagnostics;
using System.Runtime.InteropServices;
using Porta.Pty;
using XTerm;
using XTerm.Options;
using XTermTerminal = XTerm.Terminal;

namespace DevProjex.Tests.Terminal;

internal sealed class TerminalPtyHarness : IAsyncDisposable
{
	private readonly IPtyConnection _connection;
	private readonly XTermTerminal _terminal;
	private readonly CancellationTokenSource _readerCts = new();
	private readonly SemaphoreSlim _writerGate = new(1, 1);
	private readonly object _terminalGate = new();
	private readonly StringBuilder _rawOutput = new();
	private readonly TaskCompletionSource<int> _exit =
		new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly Task _readerTask;
	private readonly string _dataRoot;

	private TerminalPtyHarness(
		IPtyConnection connection,
		XTermTerminal terminal,
		string dataRoot)
	{
		_connection = connection;
		_terminal = terminal;
		_dataRoot = dataRoot;
		_connection.ProcessExited += (_, args) => _exit.TrySetResult(args.ExitCode);
		_readerTask = Task.Run(ReadOutputAsync);
	}

	public bool HasExited => _exit.Task.IsCompleted;
	public string RawOutput => CaptureRawOutput();
	public int Columns
	{
		get
		{
			lock (_terminalGate)
				return _terminal.Cols;
		}
	}

	public int Rows
	{
		get
		{
			lock (_terminalGate)
				return _terminal.Rows;
		}
	}

	public static async Task<TerminalPtyHarness> StartAsync(
		string workingDirectory,
		IReadOnlyList<string>? arguments = null,
		int columns = 120,
		int rows = 30,
		IReadOnlyDictionary<string, string>? environment = null,
		CancellationToken cancellationToken = default)
	{
		var binary = PublishedApplicationLocator.FindExecutable();
		var launchArguments = arguments?.ToArray() ?? [];
		if (OperatingSystem.IsWindows() &&
		    Environment.GetEnvironmentVariable("DEVPROJEX_TUI_TEST_BINARY") is null &&
		    File.Exists(Path.ChangeExtension(binary, ".dll")))
		{
			launchArguments =
			[
				Path.ChangeExtension(binary, ".dll"),
				.. launchArguments
			];
			binary = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
		}
		var dataRoot = Path.Combine(
			Path.GetTempPath(),
			"DevProjex.Tests.Terminal.Pty",
			Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dataRoot);
		var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["DEVPROJEX_TERMINAL_HOST"] = "1",
			["TERM"] = "xterm-256color",
			["CI"] = string.Empty,
			["NO_COLOR"] = string.Empty,
			["DEVPROJEX_INTERNAL_DATA_ROOT"] = Path.Combine(dataRoot, "devprojex"),
			["XDG_CONFIG_HOME"] = Path.Combine(dataRoot, "config"),
			["XDG_DATA_HOME"] = Path.Combine(dataRoot, "data"),
			["XDG_CACHE_HOME"] = Path.Combine(dataRoot, "cache"),
			["LOCALAPPDATA"] = Path.Combine(dataRoot, "local"),
			["APPDATA"] = Path.Combine(dataRoot, "roaming")
		};
		if (environment is not null)
		{
			foreach (var pair in environment)
				variables[pair.Key] = pair.Value;
		}
		var (host, commandLine, startupInput) = CreateShellCommand(
			binary,
			launchArguments);

		var connection = await PtyProvider.SpawnAsync(
			new PtyOptions
			{
				Name = "DevProjex terminal test",
				Cols = columns,
				Rows = rows,
				Cwd = workingDirectory,
				App = host,
				CommandLine = commandLine,
				Environment = variables
			},
			cancellationToken).ConfigureAwait(false);
		var terminal = new XTermTerminal(
			new TerminalOptions
			{
				Cols = columns,
				Rows = rows,
				Scrollback = 200,
				TermName = "xterm-256color"
			});
		var harness = new TerminalPtyHarness(connection, terminal, dataRoot);
		if (startupInput is not null)
			await harness.SendAsync(startupInput + "\r", cancellationToken).ConfigureAwait(false);
		return harness;
	}

	private static (string Host, string[] CommandLine, string? StartupInput) CreateShellCommand(
		string binary,
		IReadOnlyList<string> arguments)
	{
		if (OperatingSystem.IsWindows())
		{
			var host = Environment.GetEnvironmentVariable("COMSPEC") ??
			           Path.Combine(Environment.SystemDirectory, "cmd.exe");
			var command = string.Join(
				' ',
				new[] { QuoteForCommandPrompt(binary) }
					.Concat(arguments.Select(QuoteForCommandPrompt)));
			var launch = binary.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
				? $"call {command}"
				: $"start \"\" /wait /b {command}";
			return (
				host,
				["/d", "/q", "/v:on"],
				$"set {InvocationEnvironment.TerminalHostVariable}=1&& " +
				$"{launch} & exit /b !errorlevel!");
		}

		var shellCommand = "exec " + string.Join(
			' ',
			new[] { QuoteForPosixShell(binary) }
				.Concat(arguments.Select(QuoteForPosixShell)));
		return ("/bin/sh", ["-c", shellCommand], null);
	}

	private static string QuoteForCommandPrompt(string value) =>
		$"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

	private static string QuoteForPosixShell(string value) =>
		$"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

	public async Task SendAsync(string input, CancellationToken cancellationToken = default)
	{
		var payload = Encoding.UTF8.GetBytes(input);
		await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await _connection.WriterStream
				.WriteAsync(payload, cancellationToken)
				.ConfigureAwait(false);
			await _connection.WriterStream.FlushAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_writerGate.Release();
		}
	}

	public Task SendEnterAsync(CancellationToken cancellationToken = default) =>
		SendAsync("\r", cancellationToken);

	public Task SendEscapeAsync(CancellationToken cancellationToken = default) =>
		SendAsync("\u001b", cancellationToken);

	public Task SendDownAsync(CancellationToken cancellationToken = default) =>
		SendAsync("\u001b[B", cancellationToken);

	public Task SendUpAsync(CancellationToken cancellationToken = default) =>
		SendAsync("\u001b[A", cancellationToken);

	public Task SendHomeAsync(CancellationToken cancellationToken = default) =>
		SendAsync("\u001b[H", cancellationToken);

	public Task SendEndAsync(CancellationToken cancellationToken = default) =>
		SendAsync("\u001b[F", cancellationToken);

	public Task SendCtrlEndAsync(CancellationToken cancellationToken = default) =>
		SendAsync("\u001b[1;5F", cancellationToken);

	public Task SendLeftAsync(CancellationToken cancellationToken = default) =>
		SendAsync("\u001b[D", cancellationToken);

	public Task SendRightAsync(CancellationToken cancellationToken = default) =>
		SendAsync("\u001b[C", cancellationToken);

	public Task SendTabAsync(CancellationToken cancellationToken = default) =>
		SendAsync("\t", cancellationToken);

	public Task SendShiftTabAsync(CancellationToken cancellationToken = default) =>
		SendAsync("\u001b[Z", cancellationToken);

	public Task SendSpaceAsync(CancellationToken cancellationToken = default) =>
		SendAsync(" ", cancellationToken);

	public Task SendCtrlAAsync(CancellationToken cancellationToken = default) =>
		SendAsync("\u0001", cancellationToken);

	public Task SendCtrlCAsync(CancellationToken cancellationToken = default) =>
		SendAsync("\u0003", cancellationToken);

	public async Task SendMouseClickAsync(
		int column,
		int row,
		int clickCount = 1,
		CancellationToken cancellationToken = default)
	{
		var x = column + 1;
		var y = row + 1;
		for (var click = 0; click < clickCount; click++)
		{
			await SendAsync($"\u001b[<0;{x};{y}M\u001b[<0;{x};{y}m", cancellationToken)
				.ConfigureAwait(false);
			if (click + 1 < clickCount)
				await Task.Delay(40, cancellationToken).ConfigureAwait(false);
		}
	}

	public async Task ResizeAsync(
		int columns,
		int rows,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (_terminalGate)
			_terminal.Resize(columns, rows);
		_connection.Resize(columns, rows);
		await Task.Delay(100, cancellationToken).ConfigureAwait(false);
	}

	public string CaptureScreen()
	{
		lock (_terminalGate)
		{
			var lines = new string[_terminal.Rows];
			var buffer = _terminal.Buffer;
			for (var row = 0; row < lines.Length; row++)
			{
				lines[row] = buffer.Lines[buffer.YDisp + row]?
					.TranslateToString(trimRight: true)
					.TrimEnd() ?? string.Empty;
			}
			return string.Join('\n', lines).TrimEnd();
		}
	}

	public TerminalCellStyle CaptureCellStyle(int row, int column)
	{
		lock (_terminalGate)
		{
			var buffer = _terminal.Buffer;
			var line = buffer.Lines[buffer.YDisp + row];
			if (line is null || column < 0 || column >= line.Length)
				return TerminalCellStyle.Empty;
			var cell = line[column];
			return new TerminalCellStyle(
				cell.Content,
				cell.Attributes.GetFgColorMode(),
				cell.Attributes.GetFgColor(),
				cell.Attributes.GetBgColorMode(),
				cell.Attributes.GetBgColor(),
				cell.Attributes.IsBold(),
				cell.Attributes.IsDim(),
				cell.Attributes.IsInverse());
		}
	}

	public int FindVisibleRow(string text)
	{
		var lines = CaptureScreen().Split('\n');
		for (var index = 0; index < lines.Length; index++)
		{
			if (lines[index].Contains(text, StringComparison.Ordinal))
				return index;
		}
		return -1;
	}

	public async Task<string> WaitForScreenAsync(
		string expected,
		TimeSpan? timeout = null,
		CancellationToken cancellationToken = default)
	{
		var stopwatch = Stopwatch.StartNew();
		var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(15);
		while (stopwatch.Elapsed < effectiveTimeout)
		{
			var screen = CaptureScreen();
			if (screen.Contains(expected, StringComparison.Ordinal))
				return screen;
			if (HasExited)
				throw new Xunit.Sdk.XunitException(
					$"Terminal process exited with code {_connection.ExitCode} before '{expected}' appeared.\n" +
					$"Screen:\n{screen}\nRaw output:\n{CaptureRawOutput()}");
			await Task.Delay(50, cancellationToken).ConfigureAwait(false);
		}

		throw new TimeoutException(
			$"Timed out waiting for '{expected}'.\n{CaptureScreen()}");
	}

	public async Task<string> WaitForScreenWithoutAsync(
		string unexpected,
		TimeSpan? timeout = null,
		CancellationToken cancellationToken = default)
	{
		var stopwatch = Stopwatch.StartNew();
		var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(15);
		while (stopwatch.Elapsed < effectiveTimeout)
		{
			var screen = CaptureScreen();
			if (!screen.Contains(unexpected, StringComparison.Ordinal))
				return screen;
			if (HasExited)
				throw new Xunit.Sdk.XunitException(
					$"Terminal process exited with code {_connection.ExitCode} while waiting for " +
					$"'{unexpected}' to close.\nScreen:\n{screen}\nRaw output:\n{CaptureRawOutput()}");
			await Task.Delay(50, cancellationToken).ConfigureAwait(false);
		}

		throw new TimeoutException(
			$"Timed out waiting for '{unexpected}' to disappear.\n{CaptureScreen()}");
	}

	public async Task<int> WaitForExitAsync(
		TimeSpan? timeout = null,
		CancellationToken cancellationToken = default)
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
		try
		{
			return await _exit.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new TimeoutException(
				$"Timed out waiting for the terminal process to exit.\n" +
				$"Screen:\n{CaptureScreen()}\nRaw output:\n{CaptureRawOutput()}");
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (!HasExited)
			_connection.Kill();
		_readerCts.Cancel();
		try
		{
			await _readerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
		}
		catch
		{
			// The PTY read is expected to end through either EOF or disposal.
		}
		_connection.Dispose();
		_writerGate.Dispose();
		_readerCts.Dispose();
		TryDeleteDataRoot(_dataRoot);
	}

	private static void TryDeleteDataRoot(string dataRoot)
	{
		try
		{
			var parent = Path.GetFullPath(
				Path.Combine(Path.GetTempPath(), "DevProjex.Tests.Terminal.Pty"));
			var candidate = Path.GetFullPath(dataRoot);
			if (!PathUtility.IsPathInside(candidate, parent))
				return;
			if (Directory.Exists(candidate))
				Directory.Delete(candidate, recursive: true);
		}
		catch
		{
			// A failed test must retain its primary assertion instead of failing during best-effort cleanup.
		}
	}

	private async Task ReadOutputAsync()
	{
		var decoder = Encoding.UTF8.GetDecoder();
		var bytes = new byte[8192];
		var characters = new char[8192];
		try
		{
			while (!_readerCts.IsCancellationRequested)
			{
				var count = await _connection.ReaderStream
					.ReadAsync(bytes, _readerCts.Token)
					.ConfigureAwait(false);
				if (count == 0)
					break;
				var characterCount = decoder.GetChars(
					bytes,
					0,
					count,
					characters,
					0,
					flush: false);
				lock (_terminalGate)
				{
					_rawOutput.Append(characters, 0, characterCount);
					_terminal.Write(new string(characters, 0, characterCount));
				}
			}
		}
		catch (OperationCanceledException) when (_readerCts.IsCancellationRequested)
		{
		}
		catch (ObjectDisposedException)
		{
		}
	}

	private string CaptureRawOutput()
	{
		lock (_terminalGate)
			return _rawOutput.ToString();
	}
}

internal sealed record TerminalCellStyle(
	string Content,
	int ForegroundMode,
	int Foreground,
	int BackgroundMode,
	int Background,
	bool Bold,
	bool Dim,
	bool Inverse)
{
	public static TerminalCellStyle Empty { get; } =
		new(string.Empty, 0, 0, 0, 0, false, false, false);
}

internal static class PublishedApplicationLocator
{
	public static string FindExecutable()
	{
		var explicitPath = Environment.GetEnvironmentVariable("DEVPROJEX_TUI_TEST_BINARY");
		if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
			return Path.GetFullPath(explicitPath);

		var repository = FindRepositoryRoot();
		var configuration = AppContext.BaseDirectory.Contains(
			$"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
			StringComparison.OrdinalIgnoreCase)
			? "Release"
			: "Debug";
		var executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
			? "DevProjex.exe"
			: "DevProjex";
		var path = Path.Combine(
			repository,
			"Apps",
			"Avalonia",
			"DevProjex.Avalonia",
			"bin",
			configuration,
			"net10.0",
			executableName);
		if (File.Exists(path))
			return path;
		throw new FileNotFoundException(
			"Build the DevProjex Avalonia host before running PTY tests, or set DEVPROJEX_TUI_TEST_BINARY.",
			path);
	}

	internal static string FindRepositoryRoot()
	{
		foreach (var origin in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
		{
			var directory = new DirectoryInfo(origin);
			while (directory is not null)
			{
				if (File.Exists(Path.Combine(directory.FullName, "DevProjex.sln")))
					return directory.FullName;
				directory = directory.Parent;
			}
		}
		throw new DirectoryNotFoundException("DevProjex repository root was not found.");
	}
}
