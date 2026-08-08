using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Hex1b;
using XTerm.Options;
using XTermTerminal = XTerm.Terminal;

namespace DevProjex.Tests.Terminal;

internal sealed class TerminalPtyHarness : IAsyncDisposable
{
	private const string SkipInteractiveTuiTestsVariable =
		"DEVPROJEX_SKIP_TUI_PTY_TESTS";
	internal const string DataRootDirectoryName = "dpx-pty";
	public const string ShellCompletionMarker = "__DEVPROJEX_SHELL_RESTORED__";
	public const string ShellTerminalStateRestoredMarker =
		"__DEVPROJEX_TERMIOS_RESTORED__";
	public const string ShellTerminalStateMismatchMarker =
		"__DEVPROJEX_TERMIOS_MISMATCH__";
	public const string ShellSettledTerminalStateRestoredMarker =
		"__DEVPROJEX_SETTLED_TERMIOS_RESTORED__";
	public const string ShellSettledTerminalStateMismatchMarker =
		"__DEVPROJEX_SETTLED_TERMIOS_MISMATCH__";
	public const string ShellTerminalPropertiesRestoredMarker =
		"__DEVPROJEX_SHELL_PROPERTIES_RESTORED__";
	public const string ShellLineInputAcceptedMarker =
		"__DEVPROJEX_SHELL_LINE_INPUT_ACCEPTED__";
	public const string ShellVersionProbeMarker =
		"__DEVPROJEX_SHELL_VERSION_OK__";
	public const string ShellUsabilityVerifiedMarker =
		"__DEVPROJEX_SHELL_USABLE__";
	internal const string ShellInputSentinel =
		"__DEVPROJEX_FRESH_SHELL_INPUT__";
	internal const string ShellEchoProbe = "terminal-ok";
	private const string ShellHandshakeMarker = "__DEVPROJEX_SHELL_HANDSHAKE__";
	private const string CursorVisibleSequence = "\u001b[?25h";
	private const string CursorHiddenSequence = "\u001b[?25l";
	private const string WindowSizeQuery = "\u001b[18t";
	private readonly Hex1bTerminalChildProcess _process;
	private readonly XTermTerminal _terminal;
	private readonly CancellationTokenSource _readerCts = new();
	private readonly SemaphoreSlim _writerGate = new(1, 1);
	private readonly object _terminalGate = new();
	private readonly StringBuilder _rawOutput = new();
	private readonly StringBuilder _terminalQueryScanBuffer = new();
	private readonly StringBuilder _terminalResponseLog = new();
	private readonly TaskCompletionSource<int> _exit =
		new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly Channel<string> _terminalResponses =
		Channel.CreateUnbounded<string>(
			new UnboundedChannelOptions
			{
				SingleReader = true,
				SingleWriter = true
			});
	private readonly Task _readerTask;
	private readonly Task _exitMonitorTask;
	private readonly Task _terminalResponseTask;
	private readonly string _dataRoot;
	private readonly bool _verifyExecutableRelaunch;

	private TerminalPtyHarness(
		Hex1bTerminalChildProcess process,
		XTermTerminal terminal,
		string dataRoot,
		bool verifyExecutableRelaunch)
	{
		_process = process;
		_terminal = terminal;
		_dataRoot = dataRoot;
		_verifyExecutableRelaunch = verifyExecutableRelaunch;
		_terminal.DataReceived += (_, args) =>
		{
			if (!string.IsNullOrEmpty(args.Data))
				_terminalResponses.Writer.TryWrite(args.Data);
		};
		_terminalResponseTask = Task.Run(WriteTerminalResponsesAsync);
		_readerTask = Task.Run(ReadOutputAsync);
		_exitMonitorTask = Task.Run(ObserveExitAsync);
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
		CancellationToken cancellationToken = default,
		Action<string>? initializeDataRoot = null,
		bool writeShellCompletionMarker = false,
		bool useProgressCheckpointHost = false,
		bool verifyExecutableRelaunch = false)
	{
		if (string.Equals(
			    Environment.GetEnvironmentVariable(SkipInteractiveTuiTestsVariable),
			    "1",
			    StringComparison.Ordinal))
		{
			Assert.Skip(
				"Interactive TUI PTY journeys are disabled in CI while the TUI is pending removal.");
		}

		var binary = useProgressCheckpointHost
			? PublishedApplicationLocator.FindProgressCheckpointHostExecutable()
			: PublishedApplicationLocator.FindExecutable();
		var launchArguments = arguments?.ToArray() ?? [];
		var launchesThroughDotNetHost = false;
		if (OperatingSystem.IsWindows() &&
		    (useProgressCheckpointHost ||
		     Environment.GetEnvironmentVariable("DEVPROJEX_TUI_TEST_BINARY") is null) &&
		    File.Exists(Path.ChangeExtension(binary, ".dll")))
		{
			launchArguments =
			[
				Path.ChangeExtension(binary, ".dll"),
				.. launchArguments
			];
			binary = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
			launchesThroughDotNetHost = true;
		}
		var dataRoot = Path.Combine(
			Path.GetTempPath(),
			DataRootDirectoryName,
			Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dataRoot);
		var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["DEVPROJEX_TERMINAL_HOST"] = "1",
			["TERM"] = "xterm-256color",
			["CI"] = string.Empty,
			["TMUX"] = string.Empty,
			["ZELLIJ"] = string.Empty,
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
		if (OperatingSystem.IsWindows() && !variables.ContainsKey("NO_COLOR"))
			variables["NO_COLOR"] = string.Empty;
		initializeDataRoot?.Invoke(variables["DEVPROJEX_INTERNAL_DATA_ROOT"]);
		var (host, commandLine, startupInput) = CreateShellCommand(
			binary,
			launchArguments,
			variables,
			writeShellCompletionMarker,
			verifyExecutableRelaunch && !useProgressCheckpointHost,
			launchesThroughDotNetHost);

		var process = new Hex1bTerminalChildProcess(
			host,
			commandLine,
			workingDirectory,
			variables,
			inheritEnvironment: true,
			initialWidth: columns,
			initialHeight: rows);
		await process.StartAsync(cancellationToken).ConfigureAwait(false);
		var terminal = new XTermTerminal(
			new TerminalOptions
			{
				Cols = columns,
				Rows = rows,
				Scrollback = 200,
				TermName = "xterm-256color"
			});
		var harness = new TerminalPtyHarness(
			process,
			terminal,
			dataRoot,
			verifyExecutableRelaunch && !OperatingSystem.IsWindows());
		if (startupInput is not null)
			await harness.SendAsync(startupInput + "\r", cancellationToken).ConfigureAwait(false);
		return harness;
	}

	private static (string Host, string[] CommandLine, string? StartupInput) CreateShellCommand(
		string binary,
		IReadOnlyList<string> arguments,
		IReadOnlyDictionary<string, string> environment,
		bool writeShellCompletionMarker,
		bool verifyExecutableRelaunch,
		bool launchesThroughDotNetHost)
	{
		if (OperatingSystem.IsWindows())
		{
			if (ShouldLaunchWindowsDotNetHostDirectly(
				    writeShellCompletionMarker,
				    verifyExecutableRelaunch,
				    launchesThroughDotNetHost))
			{
				return (binary, arguments.ToArray(), null);
			}

			var host = Environment.GetEnvironmentVariable("COMSPEC") ??
			           Path.Combine(Environment.SystemDirectory, "cmd.exe");
			var launch = BuildWindowsLaunchCommand(
				binary,
				arguments,
				launchesThroughDotNetHost);
			var clearInheritedNoColor = environment.ContainsKey("NO_COLOR")
				? string.Empty
				: "set NO_COLOR=&& ";
			var completion = writeShellCompletionMarker
				? $" & set \"dpx_exit=!errorlevel!\" & echo({EscapeMarkerForCommandPrompt(ShellHandshakeMarker)}" +
				  " & set /p \"dpx_sync=\""
				: " & exit /b !errorlevel!";
			return (
				host,
				["/d", "/q", "/v:on"],
				clearInheritedNoColor +
				$"set {InvocationEnvironment.TerminalHostVariable}=1&& " +
				launch +
				completion);
		}

		// Hex1b's Unix forkpty adapter inherits the test host environment. Use env
		// inside the child shell so parallel journeys retain isolated production stores.
		var environmentArguments = environment
			.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
			.Select(static pair => QuoteForPosixShell($"{pair.Key}={pair.Value}"))
			.ToArray();
		var unixNoColorArguments = environment.ContainsKey("NO_COLOR")
			? string.Empty
			: "-u NO_COLOR ";
		var invocationPrefix = "env " + unixNoColorArguments + string.Join(
			' ',
			environmentArguments.Append(QuoteForPosixShell(binary)));
		var invocation = string.Join(
			' ',
			new[] { invocationPrefix }.Concat(arguments.Select(QuoteForPosixShell)));
		var versionProbeInvocation = verifyExecutableRelaunch
			? invocationPrefix + " --version"
			: null;
		var shellCommand = BuildUnixShellCommand(
			invocation,
			writeShellCompletionMarker,
			versionProbeInvocation);
		return (
			"/bin/sh",
			["-c", shellCommand],
			null);
	}

	internal static bool ShouldLaunchWindowsDotNetHostDirectly(
		bool writeShellCompletionMarker,
		bool verifyExecutableRelaunch,
		bool launchesThroughDotNetHost) =>
		launchesThroughDotNetHost &&
		!writeShellCompletionMarker &&
		!verifyExecutableRelaunch;

	internal static string BuildWindowsLaunchCommand(
		string binary,
		IReadOnlyList<string> arguments,
		bool launchesThroughDotNetHost)
	{
		var command = string.Join(
			' ',
			new[] { QuoteForCommandPrompt(binary) }
				.Concat(arguments.Select(QuoteForCommandPrompt)));
		if (binary.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
			return $"call {command}";

		return launchesThroughDotNetHost
			? command
			: $"start \"\" /wait /b {command}";
	}

	internal static string BuildUnixShellCommand(
		string invocation,
		bool writeShellCompletionMarker,
		string? versionProbeInvocation = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(invocation);
		if (!writeShellCompletionMarker)
			return "exec " + invocation;
		if (versionProbeInvocation is not null)
			ArgumentException.ThrowIfNullOrWhiteSpace(versionProbeInvocation);

		var extendedProbe = versionProbeInvocation is null
			? string.Empty
			: $"{versionProbeInvocation} >/dev/null 2>&1; " +
			  "[ \"$?\" -eq 0 ] || exit 99; " +
			  $"printf '%s%s\\n' {SplitMarkerForPosixShell(ShellVersionProbeMarker)}; ";

		return $"dpx_stty_before=$(stty -g 2>/dev/null || true); " +
		       $"{invocation}; dpx_exit=$?; " +
		       "dpx_stty_after=$(stty -g 2>/dev/null || true); " +
		       "if [ -n \"$dpx_stty_before\" ] && " +
		       "[ \"$dpx_stty_before\" = \"$dpx_stty_after\" ]; then " +
		       $"printf '%s%s\\n' {SplitMarkerForPosixShell(ShellTerminalStateRestoredMarker)}; " +
		       "else " +
		       $"printf '%s%s before=%s after=%s\\n' " +
		       $"{SplitMarkerForPosixShell(ShellTerminalStateMismatchMarker)} " +
		       "\"$dpx_stty_before\" \"$dpx_stty_after\"; " +
		       "fi; " +
		       "dpx_stty_flags=$(stty -a 2>/dev/null | tr ';\\n' '  '); " +
		       "[ -n \"$dpx_stty_flags\" ] || exit 98; " +
		       "case \" $dpx_stty_flags \" in " +
		       "*\" -icanon \"*|*\" -echo \"*|*\" -isig \"*) exit 98 ;; esac; " +
		       $"printf '%s%s\\n' {SplitMarkerForPosixShell(ShellTerminalPropertiesRestoredMarker)}; " +
		       $"echo {QuoteForPosixShell(ShellEchoProbe)}; " +
		       $"printf '%s%s\\n' {SplitMarkerForPosixShell(ShellHandshakeMarker)}; " +
		       "IFS= read -r dpx_sync; " +
		       $"[ \"$dpx_sync\" = {QuoteForPosixShell(ShellInputSentinel)} ] || exit 97; " +
		       $"printf '%s%s\\n' {SplitMarkerForPosixShell(ShellLineInputAcceptedMarker)}; " +
		       "dpx_stty_settled=$(stty -g 2>/dev/null || true); " +
		       "if [ -n \"$dpx_stty_before\" ] && " +
		       "[ \"$dpx_stty_before\" = \"$dpx_stty_settled\" ]; then " +
		       $"printf '%s%s\\n' {SplitMarkerForPosixShell(ShellSettledTerminalStateRestoredMarker)}; " +
		       "else " +
		       $"printf '%s%s before=%s settled=%s\\n' " +
		       $"{SplitMarkerForPosixShell(ShellSettledTerminalStateMismatchMarker)} " +
		       "\"$dpx_stty_before\" \"$dpx_stty_settled\"; " +
		       "fi; " +
		       extendedProbe +
		       $"printf '%s%s\\n' {SplitMarkerForPosixShell(ShellUsabilityVerifiedMarker)}; " +
		       $"printf '%s%s\\n' {SplitMarkerForPosixShell(ShellCompletionMarker)}; " +
		       "IFS= read -r dpx_release; exit \"$dpx_exit\"";
	}

	private static string QuoteForCommandPrompt(string value) =>
		$"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

	private static string EscapeMarkerForCommandPrompt(string marker) =>
		marker.Insert(marker.Length / 2, "^");

	private static string QuoteForPosixShell(string value) =>
		$"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

	private static string SplitMarkerForPosixShell(string marker)
	{
		var split = marker.Length / 2;
		return QuoteForPosixShell(marker[..split]) + " " +
		       QuoteForPosixShell(marker[split..]);
	}

	public async Task SendAsync(string input, CancellationToken cancellationToken = default)
	{
		var payload = Encoding.UTF8.GetBytes(input);
		await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await _process.WriteInputAsync(payload, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_writerGate.Release();
		}
	}

	public Task SendEnterAsync(CancellationToken cancellationToken = default) =>
		SendAsync("\r", cancellationToken);

	public async Task CompleteShellRestorationHandshakeAsync(
		CancellationToken cancellationToken = default)
	{
		await WaitForRawOutputAsync(
				ShellHandshakeMarker,
				cancellationToken: cancellationToken)
			.ConfigureAwait(false);
		var echoProbeStart = CaptureRawOutput().LastIndexOf(
			ShellHandshakeMarker,
			StringComparison.Ordinal);
		await SendAsync(
				ShellInputSentinel + "\r",
				cancellationToken)
			.ConfigureAwait(false);
		if (OperatingSystem.IsWindows())
		{
			var validationCommand =
				$"if \"%dpx_sync%\"==\"{ShellInputSentinel}\" (" +
				$"echo {EscapeMarkerForCommandPrompt(ShellCompletionMarker)}" +
				" & set /p \"dpx_release=\" & exit %dpx_exit%) else exit 97";
			await SendAsync(
					validationCommand + "\r",
					cancellationToken)
				.ConfigureAwait(false);
		}
		else
		{
			await WaitForRawOutputAsync(
					ShellLineInputAcceptedMarker,
					cancellationToken: cancellationToken)
				.ConfigureAwait(false);
			var lineInputOutput = CaptureRawOutput()[echoProbeStart..];
			Assert.Contains(
				ShellInputSentinel,
				lineInputOutput,
				StringComparison.Ordinal);

			if (_verifyExecutableRelaunch)
			{
				await WaitForRawOutputAsync(
						ShellVersionProbeMarker,
						cancellationToken: cancellationToken)
					.ConfigureAwait(false);
			}

			await WaitForRawOutputAsync(
					ShellUsabilityVerifiedMarker,
					cancellationToken: cancellationToken)
				.ConfigureAwait(false);
		}
		await WaitForRawOutputAsync(
				ShellCompletionMarker,
				cancellationToken: cancellationToken)
			.ConfigureAwait(false);
		if (OperatingSystem.IsWindows())
		{
			// ConHost versions may report the settled visible cursor immediately
			// before or after the marker. Wait for the resulting mode state instead
			// of requiring one specific output-chunk ordering.
			await WaitForVisibleCursorStateAsync(cancellationToken)
				.ConfigureAwait(false);
		}
	}

	public Task ReleaseParentShellAsync(CancellationToken cancellationToken = default) =>
		SendAsync("exit\r", cancellationToken);

	public async Task SendEscapeAsync(CancellationToken cancellationToken = default)
	{
		await SendAsync("\u001b", cancellationToken).ConfigureAwait(false);
		// A lone Esc must outlive the ANSI driver's escape-sequence window before
		// another key is sent, otherwise the next key is interpreted as Alt+key.
		await Task.Delay(150, cancellationToken).ConfigureAwait(false);
	}

	public Task SendDownAsync(CancellationToken cancellationToken = default) =>
		SendAsync("\u001b[B", cancellationToken);

	public Task SendUpAsync(CancellationToken cancellationToken = default) =>
		SendAsync("\u001b[A", cancellationToken);

	public Task SendHomeAsync(CancellationToken cancellationToken = default) =>
		SendAsync("\u001b[H", cancellationToken);

	public Task SendEndAsync(CancellationToken cancellationToken = default) =>
		SendAsync("\u001b[F", cancellationToken);

	public Task SendPageUpAsync(CancellationToken cancellationToken = default) =>
		SendAsync("\u001b[5~", cancellationToken);

	public Task SendPageDownAsync(CancellationToken cancellationToken = default) =>
		SendAsync("\u001b[6~", cancellationToken);

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

	public Task SendF6Async(CancellationToken cancellationToken = default) =>
		SendAsync("\u001b[17~", cancellationToken);

	public Task SendShiftF6Async(CancellationToken cancellationToken = default) =>
		SendAsync("\u001b[17;2~", cancellationToken);

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
		await _process.ResizeAsync(columns, rows, cancellationToken).ConfigureAwait(false);
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
					$"Terminal process exited with code {_process.ExitCode} before '{expected}' appeared.\n" +
					$"Screen:\n{screen}\nRaw output:\n{CaptureRawOutput()}");
			await Task.Delay(50, cancellationToken).ConfigureAwait(false);
		}

		throw new TimeoutException(
			$"Timed out waiting for '{expected}'.\n{CaptureScreen()}\n" +
			$"Raw output tail:\n{CaptureRawOutputTail()}\n" +
			$"Terminal responses: {CaptureTerminalResponseLog()}");
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
					$"Terminal process exited with code {_process.ExitCode} while waiting for " +
					$"'{unexpected}' to close.\nScreen:\n{screen}\nRaw output:\n{CaptureRawOutput()}");
			await Task.Delay(50, cancellationToken).ConfigureAwait(false);
		}

		throw new TimeoutException(
			$"Timed out waiting for '{unexpected}' to disappear.\n{CaptureScreen()}\n" +
			$"Raw output tail:\n{CaptureRawOutputTail()}\n" +
			$"Terminal responses: {CaptureTerminalResponseLog()}");
	}

	public async Task<string> WaitForStableScreenAsync(
		string required,
		string? forbidden = null,
		TimeSpan? timeout = null,
		CancellationToken cancellationToken = default)
	{
		var stopwatch = Stopwatch.StartNew();
		var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(15);
		var previous = string.Empty;
		var stableSamples = 0;
		while (stopwatch.Elapsed < effectiveTimeout)
		{
			var screen = CaptureScreen();
			if (HasExited)
			{
				throw new Xunit.Sdk.XunitException(
					$"Terminal process exited with code {_process.ExitCode} before the screen " +
					$"stabilized for '{required}'.\nScreen:\n{screen}\nRaw output:\n{CaptureRawOutput()}");
			}

			var matches =
				screen.Contains(required, StringComparison.Ordinal) &&
				(forbidden is null ||
				 !screen.Contains(forbidden, StringComparison.Ordinal));
			if (matches &&
			    string.Equals(previous, screen, StringComparison.Ordinal))
			{
				stableSamples++;
				if (stableSamples >= 3)
					return screen;
			}
			else
			{
				stableSamples = 0;
			}

			previous = screen;
			await Task.Delay(80, cancellationToken).ConfigureAwait(false);
		}

		var forbiddenCondition = forbidden is null
			? string.Empty
			: $" without '{forbidden}'";
		throw new TimeoutException(
			$"Timed out waiting for a stable screen containing '{required}'" +
			$"{forbiddenCondition}.\n{CaptureScreen()}\n" +
			$"Raw output tail:\n{CaptureRawOutputTail()}\n" +
			$"Terminal responses: {CaptureTerminalResponseLog()}");
	}

	private async Task WaitForRawOutputAsync(
		string expected,
		TimeSpan? timeout = null,
		CancellationToken cancellationToken = default)
	{
		var stopwatch = Stopwatch.StartNew();
		var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(15);
		while (stopwatch.Elapsed < effectiveTimeout)
		{
			var output = CaptureRawOutput();
			if (output.Contains(expected, StringComparison.Ordinal))
				return;
			if (HasExited)
			{
				throw new Xunit.Sdk.XunitException(
					$"Terminal process exited with code {_process.ExitCode} before " +
					$"'{expected}' appeared.\n" +
					$"Raw output:\n{output}");
			}
			await Task.Delay(50, cancellationToken).ConfigureAwait(false);
		}

		throw new TimeoutException(
			$"Timed out waiting for '{expected}'.\n" +
			$"Raw output tail:\n{CaptureRawOutputTail()}");
	}

	private async Task WaitForVisibleCursorStateAsync(
		CancellationToken cancellationToken)
	{
		var stopwatch = Stopwatch.StartNew();
		var timeout = TimeSpan.FromSeconds(15);
		while (stopwatch.Elapsed < timeout)
		{
			var output = CaptureRawOutput();
			var visibleIndex = output.LastIndexOf(CursorVisibleSequence, StringComparison.Ordinal);
			var hiddenIndex = output.LastIndexOf(CursorHiddenSequence, StringComparison.Ordinal);
			if (visibleIndex >= 0 && visibleIndex > hiddenIndex)
				return;
			if (HasExited)
			{
				throw new Xunit.Sdk.XunitException(
					$"Terminal process exited with code {_process.ExitCode} before " +
					$"the parent shell restored the cursor.\nRaw output:\n{output}");
			}

			await Task.Delay(50, cancellationToken).ConfigureAwait(false);
		}

		throw new TimeoutException(
			"Timed out waiting for the parent shell to restore the cursor.\n" +
			$"Raw output tail:\n{CaptureRawOutputTail()}");
	}

	public async Task<int> WaitForExitAsync(
		TimeSpan? timeout = null,
		CancellationToken cancellationToken = default)
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
		try
		{
			var exitCode = await _exit.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);

			// Process exit and PTY EOF are separate events. Await the reader so callers
			// never assert against a truncated restoration sequence or a missing shell marker.
			await _readerTask.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
			return exitCode;
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			var pendingOperation = HasExited
				? "the PTY output reader to reach EOF"
				: "the terminal process to exit";
			throw new TimeoutException(
				$"Timed out waiting for {pendingOperation}.\n" +
				$"Screen:\n{CaptureScreen()}\nRaw output:\n{CaptureRawOutput()}");
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (!HasExited)
			_process.Kill();
		_terminalResponses.Writer.TryComplete();
		_readerCts.Cancel();
		try
		{
			await Task.WhenAll(_readerTask, _terminalResponseTask, _exitMonitorTask)
				.WaitAsync(TimeSpan.FromSeconds(2))
				.ConfigureAwait(false);
		}
		catch
		{
			// The PTY read is expected to end through either EOF or disposal.
		}
		await _process.DisposeAsync().ConfigureAwait(false);
		_writerGate.Dispose();
		_readerCts.Dispose();
		TryDeleteDataRoot(_dataRoot);
	}

	private static void TryDeleteDataRoot(string dataRoot)
	{
		try
		{
			var parent = Path.GetFullPath(
				Path.Combine(Path.GetTempPath(), DataRootDirectoryName));
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
		var characters = new char[8192];
		try
		{
			while (!_readerCts.IsCancellationRequested)
			{
				var bytes = await _process
					.ReadOutputAsync(_readerCts.Token)
					.ConfigureAwait(false);
				if (bytes.IsEmpty)
					break;
				var characterCount = decoder.GetChars(bytes.Span, characters, flush: false);
				lock (_terminalGate)
				{
					var output = new string(characters, 0, characterCount);
					_rawOutput.Append(output);
					_terminal.Write(output);
					QueueMissingTerminalResponses(output);
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

	private async Task ObserveExitAsync()
	{
		try
		{
			var exitCode = await _process
				.WaitForExitAsync(_readerCts.Token)
				.ConfigureAwait(false);
			_exit.TrySetResult(exitCode);
		}
		catch (OperationCanceledException) when (_readerCts.IsCancellationRequested)
		{
		}
	}

	private void QueueMissingTerminalResponses(string output)
	{
		_terminalQueryScanBuffer.Append(output);
		while (true)
		{
			var bufferedOutput = _terminalQueryScanBuffer.ToString();
			var queryIndex = bufferedOutput.IndexOf(WindowSizeQuery, StringComparison.Ordinal);
			if (queryIndex < 0)
			{
				var retainedLength = Math.Min(
					WindowSizeQuery.Length - 1,
					_terminalQueryScanBuffer.Length);
				if (_terminalQueryScanBuffer.Length > retainedLength)
				_terminalQueryScanBuffer.Remove(
					0,
					_terminalQueryScanBuffer.Length - retainedLength);
				return;
			}

			_terminalQueryScanBuffer.Remove(0, queryIndex + WindowSizeQuery.Length);

			// XTerm.NET handles color and cursor queries but not CSI 18 t. Terminal.Gui
			// waits for the standard response before its first Linux frame.
			_terminalResponses.Writer.TryWrite(
				$"\u001b[8;{_terminal.Rows};{_terminal.Cols}t");
		}
	}

	private async Task WriteTerminalResponsesAsync()
	{
		try
		{
			await foreach (var response in _terminalResponses.Reader
				               .ReadAllAsync(_readerCts.Token)
				               .ConfigureAwait(false))
			{
				lock (_terminalGate)
					_terminalResponseLog.Append(Convert.ToHexString(Encoding.UTF8.GetBytes(response))).Append(' ');
				await SendAsync(response, _readerCts.Token).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (_readerCts.IsCancellationRequested)
		{
		}
		catch (ObjectDisposedException)
		{
		}
		catch (IOException) when (HasExited)
		{
		}
	}

	private string CaptureRawOutput()
	{
		lock (_terminalGate)
			return _rawOutput.ToString();
	}

	private string CaptureRawOutputTail()
	{
		lock (_terminalGate)
		{
			const int maximumLength = 4_096;
			return _rawOutput.Length <= maximumLength
				? _rawOutput.ToString()
				: _rawOutput.ToString(_rawOutput.Length - maximumLength, maximumLength);
		}
	}

	private string CaptureTerminalResponseLog()
	{
		lock (_terminalGate)
			return _terminalResponseLog.ToString();
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
	private const string ProgressCheckpointHostName =
		"DevProjex.Tests.Terminal.ProgressHost";

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

	public static string FindProgressCheckpointHostExecutable()
	{
		var repository = FindRepositoryRoot();
		var configuration = AppContext.BaseDirectory.Contains(
			$"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
			StringComparison.OrdinalIgnoreCase)
			? "Release"
			: "Debug";
		var executableName = OperatingSystem.IsWindows()
			? $"{ProgressCheckpointHostName}.exe"
			: ProgressCheckpointHostName;
		var path = Path.Combine(
			repository,
			"Tests",
			"DevProjex.Tests.Terminal.ProgressHost",
			"bin",
			configuration,
			"net10.0",
			executableName);
		if (File.Exists(path))
			return path;
		throw new FileNotFoundException(
			"Build the terminal progress checkpoint test host before running progress PTY tests.",
			path);
	}

	internal static string FindRepositoryRoot()
	{
		var explicitRoot = Environment.GetEnvironmentVariable(
			"DEVPROJEX_TUI_TEST_REPOSITORY_ROOT");
		if (!string.IsNullOrWhiteSpace(explicitRoot) &&
		    File.Exists(Path.Combine(explicitRoot, "DevProjex.sln")))
			return Path.GetFullPath(explicitRoot);

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
