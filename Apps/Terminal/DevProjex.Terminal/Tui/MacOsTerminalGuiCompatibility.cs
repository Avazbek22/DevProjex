using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Time;

namespace DevProjex.Terminal.Tui;

internal static class TerminalGuiApplicationFactory
{
	private const string ComponentFactoryFieldName = "_componentFactory";

	public static TerminalGuiApplicationContext Create()
	{
		if (!OperatingSystem.IsMacOS())
		{
			return new TerminalGuiApplicationContext(
				global::Terminal.Gui.App.Application.Create(),
				NullMacOsTerminalInputFailureSource.Instance);
		}

		return CreateMacOsApplication();
	}

	internal static TerminalGuiApplicationContext CreateMacOsApplication()
	{
		var application = global::Terminal.Gui.App.Application.Create();
		var inputFailureSource =
			new MacOsTerminalInputFailureSource();
		var componentFactoryField = application.GetType().GetField(
			ComponentFactoryFieldName,
			BindingFlags.Instance | BindingFlags.NonPublic);
		if (componentFactoryField is null ||
		    componentFactoryField.FieldType != typeof(IComponentFactory) ||
		    !componentFactoryField.IsInitOnly ||
		    componentFactoryField.GetValue(application) is not null)
		{
			application.Dispose();
			inputFailureSource.Dispose();
			throw new InvalidOperationException(
				"Terminal.Gui macOS compatibility backend is unavailable.");
		}

		try
		{
			// Terminal.Gui 2.4.17 exposes custom driver registration, but its
			// ApplicationImpl ignores registered factories. Keep its public
			// lifecycle and replace only the factory before Init observes it.
			componentFactoryField.SetValue(
				application,
				new MacOsAnsiComponentFactory(
					inputFailureSource.Report,
					() => !application.Mouse.IsMouseDisabled));
			return new TerminalGuiApplicationContext(
				application,
				inputFailureSource);
		}
		catch (Exception exception)
			when (exception is ArgumentException or
			      FieldAccessException or
			      TargetException)
		{
			application.Dispose();
			inputFailureSource.Dispose();
			throw new InvalidOperationException(
				"Terminal.Gui macOS compatibility backend could not be created.",
				exception);
		}
	}
}

internal interface IMacOsTerminalInputFailureSource : IDisposable
{
	CancellationToken Token { get; }

	void Report(MacOsTerminalRestoreException exception);

	void ThrowIfReported();
}

internal sealed class MacOsTerminalInputFailureSource :
	IMacOsTerminalInputFailureSource
{
	private readonly CancellationTokenSource _cancellation = new();
	private readonly object _gate = new();
	private bool _disposed;
	private MacOsTerminalRestoreException? _failure;

	public CancellationToken Token => _cancellation.Token;

	public void Report(MacOsTerminalRestoreException exception)
	{
		ArgumentNullException.ThrowIfNull(exception);
		var cancel = false;
		lock (_gate)
		{
			if (_failure is not null)
				return;

			_failure = exception;
			cancel = !_disposed;
		}

		if (!cancel)
			return;

		try
		{
			_cancellation.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// Disposal already stops the application lifecycle.
		}
	}

	public void ThrowIfReported()
	{
		MacOsTerminalRestoreException? failure;
		lock (_gate)
			failure = _failure;
		if (failure is not null)
			ExceptionDispatchInfo.Capture(failure).Throw();
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed)
				return;

			_disposed = true;
			_cancellation.Dispose();
		}
	}
}

internal sealed class NullMacOsTerminalInputFailureSource :
	IMacOsTerminalInputFailureSource
{
	public static NullMacOsTerminalInputFailureSource Instance { get; } =
		new();

	private NullMacOsTerminalInputFailureSource()
	{
	}

	public CancellationToken Token => CancellationToken.None;

	public void Report(MacOsTerminalRestoreException exception)
	{
		ArgumentNullException.ThrowIfNull(exception);
		throw exception;
	}

	public void ThrowIfReported()
	{
	}

	public void Dispose()
	{
	}
}

internal sealed class TerminalGuiApplicationContext(
	IApplication application,
	IMacOsTerminalInputFailureSource inputFailureSource) :
	IDisposable
{
	private int _disposed;

	public IApplication Application { get; } =
		application ?? throw new ArgumentNullException(nameof(application));

	public CancellationToken InputFailureToken =>
		inputFailureSource.Token;

	public void ThrowIfInputFailed() =>
		inputFailureSource.ThrowIfReported();

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;

		try
		{
			Application.Dispose();
		}
		finally
		{
			inputFailureSource.Dispose();
		}
	}
}

internal sealed class MacOsAnsiComponentFactory :
	ComponentFactoryImpl<ConsoleKeyInfo>
{
	internal const string DriverName = "devprojex-macos-ansi";
	private readonly Func<bool> _isMouseEnabled;
	private readonly object _terminalIoGate = new();
	private readonly Lazy<IMacOsTerminalLifecycle> _terminalLifecycle;
	private readonly Action<MacOsTerminalRestoreException>
		_reportInputFailure;

	public MacOsAnsiComponentFactory()
		: this(
			static exception => throw exception,
			static () => true)
	{
	}

	internal MacOsAnsiComponentFactory(
		Action<MacOsTerminalRestoreException> reportInputFailure,
		Func<bool> isMouseEnabled,
		Func<IMacOsTerminalLifecycle>? createTerminalLifecycle = null)
	{
		_reportInputFailure = reportInputFailure ??
			throw new ArgumentNullException(nameof(reportInputFailure));
		_isMouseEnabled = isMouseEnabled ??
			throw new ArgumentNullException(nameof(isMouseEnabled));
		_terminalLifecycle = new Lazy<IMacOsTerminalLifecycle>(
			createTerminalLifecycle ??
			(static () => MacOsConsoleTerminalLifecycle.Capture()),
			LazyThreadSafetyMode.ExecutionAndPublication);
	}

	public override string? GetDriverName() => DriverName;

	public override IInput<ConsoleKeyInfo> CreateInput() =>
		new MacOsConsoleInput(
			SystemConsoleKeySource.Instance,
			_terminalLifecycle.Value,
			_reportInputFailure,
			_terminalIoGate);

	public override IInputProcessor CreateInputProcessor(
		ConcurrentQueue<ConsoleKeyInfo> inputBuffer,
		ITimeProvider? timeProvider = null) =>
		new MacOsNetInputProcessor(inputBuffer, timeProvider);

	public override IOutput CreateOutput() =>
		new MacOsAnsiOutput(
			AppModel,
			() => _terminalLifecycle.Value,
			_isMouseEnabled,
			_reportInputFailure,
			_terminalIoGate);

	public override ISizeMonitor CreateSizeMonitor(
		IOutput consoleOutput,
		IOutputBuffer outputBuffer) =>
		CreateAnsiFactory().CreateSizeMonitor(consoleOutput, outputBuffer);

	private AnsiComponentFactory CreateAnsiFactory() =>
		new()
		{
			AppModel = AppModel
	};
}

internal sealed class MacOsNetInputProcessor(
	ConcurrentQueue<ConsoleKeyInfo> inputBuffer,
	ITimeProvider? timeProvider = null) :
	NetInputProcessor(inputBuffer, timeProvider)
{
	private string _currentInputText = string.Empty;
	private string _pendingPrintableSuppression = string.Empty;

	protected override void Process(ConsoleKeyInfo input)
	{
		_currentInputText = input.KeyChar == '\0'
			? string.Empty
			: input.KeyChar.ToString();
		try
		{
			base.Process(input);
		}
		finally
		{
			_currentInputText = string.Empty;
		}
	}

	protected override Key OnKeyboardEventParsed(Key keyEvent)
	{
		_pendingPrintableSuppression = string.Empty;
		if (keyEvent.EventType != KeyEventType.Press ||
		    keyEvent.IsAlt ||
		    keyEvent.IsCtrl ||
		    keyEvent.IsModifierOnly)
		{
			return keyEvent;
		}

		var printableText = keyEvent.GetPrintableText();
		if (!string.IsNullOrEmpty(printableText))
			_pendingPrintableSuppression = printableText;
		return keyEvent;
	}

	protected override bool ShouldSuppressFallbackKeyDown(Key key)
	{
		if (string.IsNullOrEmpty(_pendingPrintableSuppression))
			return false;

		var printableText = key.GetPrintableText();
		var suppress =
			string.Equals(
				printableText,
				_pendingPrintableSuppression,
				StringComparison.Ordinal) ||
			string.Equals(
				_currentInputText,
				_pendingPrintableSuppression,
				StringComparison.Ordinal);
		_pendingPrintableSuppression = string.Empty;
		return suppress;
	}
}

internal sealed class MacOsAnsiOutput :
	AnsiOutput,
	IOutput
{
	private static readonly string KittyKeyboardEnableSequence =
		EscSeqUtils.CSI_EnableKittyKeyboardFlags(
			EscSeqUtils.KittyKeyboardRequestedFlags);
	private readonly AppModel _appModel;
	private readonly Func<IMacOsTerminalLifecycle> _getTerminalLifecycle;
	private readonly Func<bool> _isMouseEnabled;
	private readonly Action<MacOsTerminalRestoreException>
		_reportTerminalFailure;
	private readonly object _terminalIoGate;

	public MacOsAnsiOutput(AppModel appModel)
		: this(
			appModel,
			static () => NullMacOsTerminalLifecycle.Instance,
			static () => true,
			static exception => throw exception,
			new object())
	{
	}

	internal MacOsAnsiOutput(
		AppModel appModel,
		Func<IMacOsTerminalLifecycle> getTerminalLifecycle,
		Func<bool> isMouseEnabled,
		Action<MacOsTerminalRestoreException>? reportTerminalFailure = null,
		object? terminalIoGate = null)
		: base(appModel)
	{
		_appModel = appModel;
		_getTerminalLifecycle = getTerminalLifecycle ??
			throw new ArgumentNullException(nameof(getTerminalLifecycle));
		_isMouseEnabled = isMouseEnabled ??
			throw new ArgumentNullException(nameof(isMouseEnabled));
		_reportTerminalFailure = reportTerminalFailure ??
			(static exception => throw exception);
		_terminalIoGate = terminalIoGate ?? new object();
	}

	void IOutput.Write(ReadOnlySpan<char> text)
	{
		// Terminal.Gui enables this protocol only for its ANSI input processor.
		// Preserve capability probing, but do not mutate the parent terminal when
		// the macOS compatibility backend uses ConsoleKeyInfo input.
		if (text.SequenceEqual(KittyKeyboardEnableSequence) ||
		    text.SequenceEqual(EscSeqUtils.CSI_DisableKittyKeyboardFlags))
		{
			return;
		}

		base.Write(text);
	}

	void IOutput.Suspend()
	{
		MacOsTerminalRestoreException? failure = null;
		lock (_terminalIoGate)
		{
			var terminalLifecycle = _getTerminalLifecycle();
			try
			{
				base.Write(EscSeqUtils.CSI_DisableBracketedPaste);
				base.Write(EscSeqUtils.CSI_DisableMouseEvents);
				base.Write(EscSeqUtils.CSI_ResetAttributes);
				if (_appModel == AppModel.FullScreen)
				{
					base.Write(
						EscSeqUtils.CSI_RestoreCursorAndRestoreAltBufferWithBackscroll);
				}
				base.Write(EscSeqUtils.CSI_ShowCursor);

				terminalLifecycle.SuspendAndResume();

				if (_appModel == AppModel.FullScreen)
				{
					base.Write(
						EscSeqUtils.CSI_SaveCursorAndActivateAltBufferNoBackscroll);
				}
				if (_isMouseEnabled())
					base.Write(EscSeqUtils.CSI_EnableMouseEvents);
				base.Write(EscSeqUtils.CSI_EnableBracketedPaste);
			}
			catch (Exception exception)
			{
				failure = exception as MacOsTerminalRestoreException ??
					new MacOsTerminalRestoreException(exception);
				try
				{
					terminalLifecycle.RestoreForShell();
				}
				catch (Exception restoreException)
				{
					failure = new MacOsTerminalRestoreException(
						new AggregateException(
							failure,
							restoreException));
				}
			}
		}

		if (failure is not null)
			_reportTerminalFailure(failure);
	}
}

internal sealed class MacOsConsoleInput : InputImpl<ConsoleKeyInfo>
{
	private readonly IConsoleKeySource _console;
	private readonly ConsoleControlCPolicy _controlCPolicy;
	private readonly Action<MacOsTerminalRestoreException>
		_reportTerminalFailure;
	private readonly object _terminalIoGate;
	private readonly IMacOsTerminalLifecycle _terminalLifecycle;
	private int _disposed;

	public MacOsConsoleInput()
		: this(
			SystemConsoleKeySource.Instance,
			MacOsConsoleTerminalLifecycle.Capture(),
			static exception => throw exception,
			new object())
	{
	}

	internal MacOsConsoleInput(
		Action<MacOsTerminalRestoreException> reportTerminalFailure)
		: this(
			SystemConsoleKeySource.Instance,
			MacOsConsoleTerminalLifecycle.Capture(),
			reportTerminalFailure,
			new object())
	{
	}

	internal MacOsConsoleInput(IConsoleKeySource console)
		: this(
			console,
			NullMacOsTerminalLifecycle.Instance,
			static exception => throw exception,
			new object())
	{
	}

	internal MacOsConsoleInput(
		IConsoleKeySource console,
		IMacOsTerminalLifecycle terminalLifecycle,
		Action<MacOsTerminalRestoreException>? reportTerminalFailure = null,
		object? terminalIoGate = null)
	{
		_console = console ?? throw new ArgumentNullException(nameof(console));
		_terminalLifecycle = terminalLifecycle ??
			throw new ArgumentNullException(nameof(terminalLifecycle));
		_reportTerminalFailure = reportTerminalFailure ??
			(static exception => throw exception);
		_terminalIoGate = terminalIoGate ?? new object();
		_controlCPolicy = new ConsoleControlCPolicy(console);
		try
		{
			_terminalLifecycle.ActivateInput();
		}
		catch
		{
			_controlCPolicy.Dispose();
			_terminalLifecycle.RestoreForShell();
			throw;
		}
	}

	public override bool Peek()
	{
		lock (_terminalIoGate)
		{
			if (ReportTerminalFailure())
				return false;
			bool keyAvailable;
			try
			{
				keyAvailable = _console.KeyAvailable;
			}
			catch (InvalidOperationException)
			{
				return false;
			}
			catch (IOException)
			{
				return false;
			}

			if (ReportTerminalFailure())
				return false;
			return keyAvailable;
		}
	}

	public override IEnumerable<ConsoleKeyInfo> Read()
	{
		while (TryRead(out var keyInfo))
			yield return keyInfo;
	}

	public override void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;

		lock (_terminalIoGate)
		{
			try
			{
				_controlCPolicy.Dispose();
				base.Dispose();
			}
			finally
			{
				_terminalLifecycle.RestoreForShell();
			}
		}
	}

	private bool TryRead(out ConsoleKeyInfo keyInfo)
	{
		lock (_terminalIoGate)
		{
			keyInfo = default;
			if (ReportTerminalFailure())
				return false;

			try
			{
				if (!_console.KeyAvailable)
					return false;

				keyInfo = _console.ReadKey(intercept: true);
			}
			catch (InvalidOperationException)
			{
				return false;
			}
			catch (IOException)
			{
				return false;
			}

			if (ReportTerminalFailure())
			{
				keyInfo = default;
				return false;
			}
			return true;
		}
	}

	private bool ReportTerminalFailure()
	{
		if (!_terminalLifecycle.TryGetContinueRestoreFailure(
			    out var exception))
		{
			return false;
		}

		_reportTerminalFailure(exception);
		return true;
	}
}

internal interface IMacOsTerminalLifecycle
{
	void ActivateInput();

	bool TryGetContinueRestoreFailure(
		out MacOsTerminalRestoreException exception);

	void SuspendAndResume();

	void RestoreForShell();
}

internal interface IMacOsTerminalModePolicy
{
	void EnterApplicationMode();

	void EnterShellMode();
}

internal sealed class MacOsTermInfoTerminalModePolicy(
	MacOsTerminalModeCapabilities capabilities,
	Action<string> writeTerminalMode) :
	IMacOsTerminalModePolicy
{
	public static MacOsTermInfoTerminalModePolicy Capture() =>
		new(
			MacOsTerminalModeCapabilityProvider.Capture(),
			MacOsConsoleNative.Instance.WriteStandardOutput);

	public void EnterApplicationMode() =>
		Write(capabilities.ApplicationMode);

	public void EnterShellMode() =>
		Write(capabilities.ShellMode);

	private void Write(string value)
	{
		if (!string.IsNullOrEmpty(value))
			writeTerminalMode(value);
	}
}

internal readonly record struct MacOsTerminalModeCapabilities(
	string ApplicationMode,
	string ShellMode)
{
	public static MacOsTerminalModeCapabilities Empty { get; } =
		new(string.Empty, string.Empty);
}

internal static class MacOsTerminalModeCapabilityProvider
{
	private const string TputPath = "/usr/bin/tput";
	private const int CapabilityProcessTimeoutMilliseconds = 5_000;

	public static MacOsTerminalModeCapabilities Capture() =>
		Capture(ReadCapability);

	internal static MacOsTerminalModeCapabilities Capture(
		Func<string, MacOsTerminalCapabilityResult> readCapability)
	{
		ArgumentNullException.ThrowIfNull(readCapability);
		var applicationMode = readCapability("smkx");
		var shellMode = readCapability("rmkx");
		if (applicationMode.ExitCode != 0 ||
		    string.IsNullOrEmpty(applicationMode.Value))
		{
			return MacOsTerminalModeCapabilities.Empty;
		}
		if (shellMode.ExitCode != 0 ||
		    string.IsNullOrEmpty(shellMode.Value))
		{
			throw new MacOsTerminalRestoreException();
		}

		return new MacOsTerminalModeCapabilities(
			applicationMode.Value,
			shellMode.Value);
	}

	internal static ProcessStartInfo CreateTputStartInfo(
		string capability)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(capability);
		var startInfo = new ProcessStartInfo
		{
			FileName = TputPath,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};
		startInfo.ArgumentList.Add(capability);
		return startInfo;
	}

	private static MacOsTerminalCapabilityResult ReadCapability(
		string capability)
	{
		try
		{
			using var process = new Process
			{
				StartInfo = CreateTputStartInfo(capability)
			};
			process.Start();
			process.StandardInput.Close();
			var output = process.StandardOutput.ReadToEndAsync();
			var error = process.StandardError.ReadToEndAsync();
			if (!process.WaitForExit(CapabilityProcessTimeoutMilliseconds))
			{
				process.Kill(entireProcessTree: true);
				if (!process.WaitForExit(CapabilityProcessTimeoutMilliseconds))
					throw new MacOsTerminalRestoreException();
				throw new MacOsTerminalRestoreException();
			}
			Task.WaitAll(output, error);
			return new MacOsTerminalCapabilityResult(
				process.ExitCode,
				output.Result);
		}
		catch (MacOsTerminalRestoreException)
		{
			throw;
		}
		catch (Exception exception)
			when (exception is InvalidOperationException or
			      System.ComponentModel.Win32Exception or
			      IOException or
			      UnauthorizedAccessException or
			      AggregateException)
		{
			throw new MacOsTerminalRestoreException(exception);
		}
	}
}

internal readonly record struct MacOsTerminalCapabilityResult(
	int ExitCode,
	string Value);

internal sealed class NullMacOsTerminalModePolicy :
	IMacOsTerminalModePolicy
{
	public static NullMacOsTerminalModePolicy Instance { get; } = new();

	private NullMacOsTerminalModePolicy()
	{
	}

	public void EnterApplicationMode()
	{
	}

	public void EnterShellMode()
	{
	}
}

internal sealed class MacOsConsoleTerminalLifecycle : IMacOsTerminalLifecycle
{
	internal const int InterruptedSystemCall = 4;
	internal const int MaximumNativeAttempts = 8;

	private readonly IMacOsConsoleNative _native;
	private readonly IMacOsTerminalSignalPolicy _signalPolicy;
	private readonly IMacOsTerminalModePolicy _terminalModePolicy;
	private readonly MacOsConsoleNative.TerminalAttributes _initialAttributes;

	internal MacOsConsoleTerminalLifecycle(
		IMacOsConsoleNative native,
		MacOsConsoleNative.TerminalAttributes initialAttributes,
		IMacOsTerminalSignalPolicy? signalPolicy = null,
		IMacOsTerminalModePolicy? terminalModePolicy = null)
	{
		_native = native ?? throw new ArgumentNullException(nameof(native));
		_initialAttributes = initialAttributes;
		_signalPolicy = signalPolicy ?? NullMacOsTerminalSignalPolicy.Instance;
		_terminalModePolicy =
			terminalModePolicy ?? NullMacOsTerminalModePolicy.Instance;
	}

	public static MacOsConsoleTerminalLifecycle Capture() =>
		Capture(
			MacOsConsoleNative.Instance,
			MacOsTerminalSignalPolicy.Instance,
			MacOsTermInfoTerminalModePolicy.Capture());

	internal static MacOsConsoleTerminalLifecycle Capture(
		IMacOsConsoleNative native) =>
		Capture(
			native,
			NullMacOsTerminalSignalPolicy.Instance,
			NullMacOsTerminalModePolicy.Instance);

	internal static MacOsConsoleTerminalLifecycle Capture(
		IMacOsConsoleNative native,
		IMacOsTerminalSignalPolicy signalPolicy) =>
		Capture(
			native,
			signalPolicy,
			NullMacOsTerminalModePolicy.Instance);

	internal static MacOsConsoleTerminalLifecycle Capture(
		IMacOsConsoleNative native,
		IMacOsTerminalSignalPolicy signalPolicy,
		IMacOsTerminalModePolicy terminalModePolicy)
	{
		ArgumentNullException.ThrowIfNull(native);
		ArgumentNullException.ThrowIfNull(signalPolicy);
		ArgumentNullException.ThrowIfNull(terminalModePolicy);
		try
		{
			if (!native.IsStandardInputForeground(out var foregroundError))
			{
				if (foregroundError != 0)
					throw new MacOsTerminalRestoreException(foregroundError);
				throw new MacOsTerminalRestoreException();
			}

			for (var attempt = 0; attempt < MaximumNativeAttempts; attempt++)
			{
				var result = native.GetStandardInputAttributes(
					out var attributes,
					out var nativeError);
				if (result >= 0)
				{
					return new MacOsConsoleTerminalLifecycle(
						native,
						attributes,
						signalPolicy,
						terminalModePolicy);
				}
				if (nativeError != InterruptedSystemCall)
					throw new MacOsTerminalRestoreException(nativeError);
			}

			throw new MacOsTerminalRestoreException(InterruptedSystemCall);
		}
		catch (Exception exception)
			when (IsNativeInteropFailure(exception))
		{
			signalPolicy.BeginTerminalTeardown();
			throw new MacOsTerminalRestoreException(exception);
		}
		catch
		{
			signalPolicy.BeginTerminalTeardown();
			throw;
		}
	}

	public void ActivateInput()
	{
		if (!_native.IsStandardInputForeground(out var nativeError))
		{
			if (nativeError != 0)
				throw new MacOsTerminalRestoreException(nativeError);
			throw new MacOsTerminalRestoreException();
		}

		// Do not capture the physical state here. A delayed SIGCONT callback can
		// otherwise make a transient canonical state the session's recovery
		// target. Derive the same input mode used by .NET Console from the exact
		// shell baseline, publish that target first, and then apply it directly.
		var activeAttributes = _initialAttributes.ToConsoleInputMode();
		_signalPolicy.BeginSession(activeAttributes);
		try
		{
			RestoreAttributes(activeAttributes);
			if (!GetCurrentAttributes().Equals(activeAttributes))
				throw new MacOsTerminalRestoreException();
			_terminalModePolicy.EnterApplicationMode();
		}
		catch
		{
			_signalPolicy.BeginTerminalTeardown();
			throw;
		}
	}

	public bool TryGetContinueRestoreFailure(
		out MacOsTerminalRestoreException exception) =>
		_signalPolicy.TryGetContinueRestoreFailure(out exception);

	public void SuspendAndResume()
	{
		RestoreForShell();
		if (_native.SuspendProcessGroup(out var nativeError) < 0)
			throw new MacOsTerminalRestoreException(nativeError);
		ActivateInput();
	}

	public void RestoreForShell()
	{
		// .NET reapplies its cached raw termios on SIGCONT. Once TUI teardown
		// starts, continuing the process must not race the exact shell restore.
		_signalPolicy.BeginTerminalTeardown();
		try
		{
			if (!_native.IsStandardInputForeground(out var nativeError))
			{
				if (nativeError == 0)
					return;
				throw new MacOsTerminalRestoreException(nativeError);
			}

			MacOsTerminalRestoreException? terminalModeFailure = null;
			try
			{
				_terminalModePolicy.EnterShellMode();
			}
			catch (Exception exception)
			{
				terminalModeFailure =
					exception as MacOsTerminalRestoreException ??
					new MacOsTerminalRestoreException(exception);
			}

			if (_initialAttributes.HasPendingInput)
			{
				RestoreAttributes(_initialAttributes);
				if (terminalModeFailure is not null)
					throw terminalModeFailure;
				return;
			}

			// Darwin sets PENDIN when raw input becomes canonical. poll(POLLIN)
			// enters ttyselect/ttnread and reprocesses the remaining raw queue
			// through the restored line discipline without a userspace read or an
			// explicit flush. Suppress active control-byte handling during that
			// replay so queued input cannot signal the exiting TUI process group,
			// stop output, or discard terminal cleanup output.
			try
			{
				RestoreAttributes(
					_initialAttributes.WithoutActiveInputControls());
				PollStandardInput();
			}
			finally
			{
				RestoreAttributes(_initialAttributes);
			}
			var restoredAttributes = GetCurrentAttributes();
			if (restoredAttributes.HasPendingInput)
				throw new MacOsTerminalRestoreException();
			if (terminalModeFailure is not null)
				throw terminalModeFailure;
		}
		catch (Exception exception)
			when (IsNativeInteropFailure(exception))
		{
			throw new MacOsTerminalRestoreException(exception);
		}
	}

	private static bool IsNativeInteropFailure(Exception exception) =>
		exception is DllNotFoundException or
			EntryPointNotFoundException or
			BadImageFormatException;

	private MacOsConsoleNative.TerminalAttributes GetCurrentAttributes()
	{
		for (var attempt = 0; attempt < MaximumNativeAttempts; attempt++)
		{
			var result = _native.GetStandardInputAttributes(
				out var attributes,
				out var nativeError);
			if (result >= 0)
				return attributes;
			if (nativeError != InterruptedSystemCall)
				throw new MacOsTerminalRestoreException(nativeError);
		}

		throw new MacOsTerminalRestoreException(InterruptedSystemCall);
	}

	private void RestoreAttributes(
		MacOsConsoleNative.TerminalAttributes attributes)
	{
		for (var attempt = 0; attempt < MaximumNativeAttempts; attempt++)
		{
			var result = _native.SetStandardInputAttributes(
				attributes,
				out var nativeError);
			if (result >= 0)
				return;
			if (nativeError != InterruptedSystemCall)
				throw new MacOsTerminalRestoreException(nativeError);
		}

		throw new MacOsTerminalRestoreException(InterruptedSystemCall);
	}

	private void PollStandardInput()
	{
		for (var attempt = 0; attempt < MaximumNativeAttempts; attempt++)
		{
			var result = _native.PollStandardInput(out var nativeError);
			if (result >= 0)
				return;
			if (nativeError != InterruptedSystemCall)
				throw new MacOsTerminalRestoreException(nativeError);
		}

		throw new MacOsTerminalRestoreException(InterruptedSystemCall);
	}
}

internal interface IMacOsTerminalSignalPolicy
{
	void BeginSession(
		MacOsConsoleNative.TerminalAttributes activeAttributes);

	void BeginTerminalTeardown();

	bool TryGetContinueRestoreFailure(
		out MacOsTerminalRestoreException exception);
}

internal sealed class MacOsTerminalSignalPolicy : IMacOsTerminalSignalPolicy
{
	private readonly object _gate = new();
	private readonly PosixSignalRegistration _continueRegistration;
	private readonly Func<
		MacOsConsoleNative.TerminalAttributes,
		bool> _restoreActiveConsoleMode;
	private bool _terminalTeardownStarted = true;
	private bool _continueRestoreFailed;
	private MacOsConsoleNative.TerminalAttributes _activeAttributes;

	public static MacOsTerminalSignalPolicy Instance { get; } = new();

	private MacOsTerminalSignalPolicy()
		: this(
			TryRestoreActiveConsoleMode,
			registerSignal: true)
	{
	}

	internal MacOsTerminalSignalPolicy(
		Func<MacOsConsoleNative.TerminalAttributes, bool>
			restoreActiveConsoleMode)
		: this(
			restoreActiveConsoleMode,
			registerSignal: false)
	{
	}

	private MacOsTerminalSignalPolicy(
		Func<MacOsConsoleNative.TerminalAttributes, bool>
			restoreActiveConsoleMode,
		bool registerSignal)
	{
		_restoreActiveConsoleMode = restoreActiveConsoleMode ??
			throw new ArgumentNullException(nameof(restoreActiveConsoleMode));
		if (registerSignal && !OperatingSystem.IsMacOS())
		{
			throw new PlatformNotSupportedException(
				"The Darwin terminal signal policy requires macOS.");
		}

		_continueRegistration = registerSignal
			? PosixSignalRegistration.Create(
				PosixSignal.SIGCONT,
				HandleContinue)
			: null!;
	}

	public void BeginSession(
		MacOsConsoleNative.TerminalAttributes activeAttributes)
	{
		GC.KeepAlive(_continueRegistration);
		lock (_gate)
		{
			_activeAttributes = activeAttributes;
			_continueRestoreFailed = false;
			_terminalTeardownStarted = false;
		}
	}

	public void BeginTerminalTeardown()
	{
		lock (_gate)
			_terminalTeardownStarted = true;
	}

	public bool TryGetContinueRestoreFailure(
		out MacOsTerminalRestoreException exception)
	{
		lock (_gate)
		{
			if (_continueRestoreFailed)
			{
				exception = new MacOsTerminalRestoreException();
				return true;
			}
		}

		exception = null!;
		return false;
	}

	private void HandleContinue(PosixSignalContext context)
	{
		// Always replace .NET's default SIGCONT action. Otherwise its cached raw
		// termios can be written after DevProjex has restored the exact shell
		// snapshot. During an active session, reapply the Console read policy
		// ourselves while holding the same gate used by teardown.
		context.Cancel = true;
		HandleContinueCore();
	}

	internal void HandleContinueCore()
	{
		lock (_gate)
		{
			if (_terminalTeardownStarted)
				return;

			if (!_restoreActiveConsoleMode(_activeAttributes))
				_continueRestoreFailed = true;
		}
	}

	private static bool TryRestoreActiveConsoleMode(
		MacOsConsoleNative.TerminalAttributes activeAttributes) =>
		TryRestoreActiveConsoleMode(
			MacOsConsoleNative.Instance,
			activeAttributes);

	internal static bool TryRestoreActiveConsoleMode(
		IMacOsConsoleNative native,
		MacOsConsoleNative.TerminalAttributes activeAttributes)
	{
		ArgumentNullException.ThrowIfNull(native);
		try
		{
			if (!native.IsStandardInputForeground(out var foregroundError))
			{
				// A background job must not mutate the foreground terminal.
				// This is a successful no-op; a later foreground SIGCONT will
				// reapply the active session state.
				return foregroundError == 0;
			}

			// Run .NET's own synchronous SIGCONT recovery while the DevProjex
			// lifecycle gate is held. This preserves the active terminfo
			// keypad_xmit capability without allowing the runtime to race shell
			// restoration after this PosixSignal callback returns.
			native.HandleNonCanceledContinue();
			for (var attempt = 0;
			     attempt < MacOsConsoleTerminalLifecycle.MaximumNativeAttempts;
			     attempt++)
			{
				var result = native.SetStandardInputAttributes(
					activeAttributes,
					out var nativeError);
				if (result >= 0)
					return true;
				if (nativeError !=
				    MacOsConsoleTerminalLifecycle.InterruptedSystemCall)
				{
					return false;
				}
			}

			return false;
		}
		catch (Exception exception)
			when (exception is DllNotFoundException or
			      EntryPointNotFoundException or
			      BadImageFormatException)
		{
			return false;
		}
	}
}

internal sealed class NullMacOsTerminalSignalPolicy :
	IMacOsTerminalSignalPolicy
{
	public static NullMacOsTerminalSignalPolicy Instance { get; } = new();

	private NullMacOsTerminalSignalPolicy()
	{
	}

	public void BeginSession(
		MacOsConsoleNative.TerminalAttributes activeAttributes)
	{
	}

	public void BeginTerminalTeardown()
	{
	}

	public bool TryGetContinueRestoreFailure(
		out MacOsTerminalRestoreException exception)
	{
		exception = null!;
		return false;
	}
}

internal interface IMacOsConsoleNative
{
	int GetStandardInputAttributes(
		out MacOsConsoleNative.TerminalAttributes attributes,
		out int nativeError);

	int SetStandardInputAttributes(
		MacOsConsoleNative.TerminalAttributes attributes,
		out int nativeError);

	bool IsStandardInputForeground(out int nativeError);

	int PollStandardInput(out int nativeError);

	void HandleNonCanceledContinue();

	int SuspendProcessGroup(out int nativeError);
}

internal sealed class MacOsConsoleNative : IMacOsConsoleNative
{
	private const int StandardInputDescriptor = 0;
	private const int StandardOutputDescriptor = 1;
	private const int SetImmediately = 0;
	private const short PollInput = 0x0001;
	private const short PollInvalid = 0x0020;
	private const int BadFileDescriptor = 9;
	private const int DarwinContinueSignal = 19;
	private const int DarwinSuspendSignal = 18;

	public static MacOsConsoleNative Instance { get; } = new();

	private MacOsConsoleNative()
	{
	}

	public int GetStandardInputAttributes(
		out TerminalAttributes attributes,
		out int nativeError)
	{
		var result = tcgetattr(StandardInputDescriptor, out attributes);
		nativeError = result < 0
			? Marshal.GetLastPInvokeError()
			: 0;
		return result;
	}

	public int SetStandardInputAttributes(
		TerminalAttributes attributes,
		out int nativeError)
	{
		var result = tcsetattr(
			StandardInputDescriptor,
			SetImmediately,
			ref attributes);
		nativeError = result < 0
			? Marshal.GetLastPInvokeError()
			: 0;
		return result;
	}

	public bool IsStandardInputForeground(out int nativeError)
	{
		var foregroundProcessGroup = tcgetpgrp(StandardInputDescriptor);
		if (foregroundProcessGroup < 0)
		{
			nativeError = Marshal.GetLastPInvokeError();
			return false;
		}

		nativeError = 0;
		return foregroundProcessGroup == getpgrp();
	}

	public int PollStandardInput(out int nativeError)
	{
		var descriptor = new PollFileDescriptor(
			StandardInputDescriptor,
			PollInput);
		var result = poll(
			ref descriptor,
			descriptorCount: 1,
			timeoutMilliseconds: 0);
		if (result < 0)
		{
			nativeError = Marshal.GetLastPInvokeError();
			return result;
		}

		if ((descriptor.ResultEvents & PollInvalid) != 0)
		{
			nativeError = BadFileDescriptor;
			return -1;
		}

		nativeError = 0;
		return result;
	}

	public void HandleNonCanceledContinue() =>
		SystemNative_HandleNonCanceledPosixSignal(DarwinContinueSignal);

	public int SuspendProcessGroup(out int nativeError)
	{
		var result = killpg(0, DarwinSuspendSignal);
		nativeError = result < 0
			? Marshal.GetLastPInvokeError()
			: 0;
		return result;
	}

	internal void WriteStandardOutput(string value)
	{
		ArgumentNullException.ThrowIfNull(value);
		if (value.Length == 0)
			return;

		var bytes = Encoding.UTF8.GetBytes(value);
		var pinnedBytes = GCHandle.Alloc(bytes, GCHandleType.Pinned);
		try
		{
			var offset = 0;
			var interruptedAttempts = 0;
			while (offset < bytes.Length)
			{
				var written = write(
					StandardOutputDescriptor,
					IntPtr.Add(pinnedBytes.AddrOfPinnedObject(), offset),
					(nuint)(bytes.Length - offset));
				if (written > 0)
				{
					offset += checked((int)written);
					interruptedAttempts = 0;
					continue;
				}

				var nativeError = written < 0
					? Marshal.GetLastPInvokeError()
					: 0;
				interruptedAttempts++;
				if (nativeError == MacOsConsoleTerminalLifecycle.InterruptedSystemCall &&
				    interruptedAttempts <
				    MacOsConsoleTerminalLifecycle.MaximumNativeAttempts)
				{
					continue;
				}

				throw written == 0
					? new MacOsTerminalRestoreException()
					: new MacOsTerminalRestoreException(nativeError);
			}
		}
		finally
		{
			pinnedBytes.Free();
		}
	}

	[DllImport("libc", EntryPoint = "tcgetattr", SetLastError = true)]
	private static extern int tcgetattr(
		int descriptor,
		out TerminalAttributes attributes);

	[DllImport("libc", EntryPoint = "tcsetattr", SetLastError = true)]
	private static extern int tcsetattr(
		int descriptor,
		int optionalActions,
		ref TerminalAttributes attributes);

	[DllImport("libc", EntryPoint = "tcgetpgrp", SetLastError = true)]
	private static extern int tcgetpgrp(int descriptor);

	[DllImport("libc", EntryPoint = "getpgrp")]
	private static extern int getpgrp();

	[DllImport("libc", EntryPoint = "poll", SetLastError = true)]
	private static extern int poll(
		ref PollFileDescriptor descriptor,
		uint descriptorCount,
		int timeoutMilliseconds);

	[DllImport(
		"System.Native",
		EntryPoint = "SystemNative_HandleNonCanceledPosixSignal")]
	private static extern void SystemNative_HandleNonCanceledPosixSignal(
		int signalCode);

	[DllImport("libc", EntryPoint = "killpg", SetLastError = true)]
	private static extern int killpg(int processGroup, int signal);

	[DllImport("libc", EntryPoint = "write", SetLastError = true)]
	private static extern nint write(
		int descriptor,
		IntPtr buffer,
		nuint count);

	[StructLayout(LayoutKind.Explicit, Size = 72)]
	internal struct TerminalAttributes
	{
		private const ulong PendingInput = 0x20000000;
		private const ulong SignalGeneration = 0x00000080;
		private const ulong ExtendedInputProcessing = 0x00000400;
		private const ulong EchoInput = 0x00000008;
		private const ulong CanonicalInput = 0x00000100;
		private const ulong MapNewLineToCarriageReturn = 0x00000040;
		private const ulong IgnoreCarriageReturn = 0x00000080;
		private const ulong MapCarriageReturnToNewLine = 0x00000100;
		private const ulong OutputFlowControl = 0x00000200;
		private const ulong InputFlowControl = 0x00000400;
		private const uint ControlCharacterValueMask = 0x0000ffff;
		private const uint MinimumCharacterCount = 1;

		[FieldOffset(0)]
		internal ulong InputModes;

		[FieldOffset(8)]
		private ulong _outputModes;

		[FieldOffset(16)]
		private ulong _controlModes;

		[FieldOffset(24)]
		internal ulong LocalModes;

		[FieldOffset(32)]
		private ulong _controlCharacters0;

		[FieldOffset(40)]
		private ulong _controlCharacters1;

		[FieldOffset(48)]
		private uint _controlCharacters2;

		[FieldOffset(52)]
		private uint _alignmentPadding;

		[FieldOffset(56)]
		private ulong _inputSpeed;

		[FieldOffset(64)]
		private ulong _outputSpeed;

		internal readonly bool HasPendingInput =>
			(LocalModes & PendingInput) != 0;

		internal readonly TerminalAttributes ToConsoleInputMode()
		{
			var attributes = this;
			attributes.InputModes &=
				~(OutputFlowControl |
				  InputFlowControl |
				  MapNewLineToCarriageReturn |
				  IgnoreCarriageReturn |
				  MapCarriageReturnToNewLine);
			attributes.LocalModes &=
				~(SignalGeneration |
				  EchoInput |
				  CanonicalInput |
				  ExtendedInputProcessing);
			attributes._controlCharacters2 =
				(attributes._controlCharacters2 &
				 ~ControlCharacterValueMask) |
				MinimumCharacterCount;
			return attributes;
		}

		internal readonly TerminalAttributes WithoutActiveInputControls()
		{
			var attributes = this;
			attributes.InputModes &=
				~(OutputFlowControl | InputFlowControl);
			attributes.LocalModes &=
				~(SignalGeneration | ExtendedInputProcessing);
			return attributes;
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct PollFileDescriptor(
		int descriptor,
		short events)
	{
		internal int Descriptor = descriptor;
		internal short Events = events;
		internal short ResultEvents;
	}
}

internal sealed class MacOsTerminalRestoreException : IOException
{
	private const string ErrorCode = "DPX-TUI-MACOS-TERMINAL-RESTORE";

	internal MacOsTerminalRestoreException()
		: base(ErrorCode)
	{
	}

	internal MacOsTerminalRestoreException(int nativeError)
		: base(ErrorCode)
	{
		NativeError = nativeError;
	}

	internal MacOsTerminalRestoreException(Exception innerException)
		: base(
			ErrorCode,
			innerException ??
			throw new ArgumentNullException(nameof(innerException)))
	{
	}

	public int? NativeError { get; }
}

internal sealed class NullMacOsTerminalLifecycle : IMacOsTerminalLifecycle
{
	public static NullMacOsTerminalLifecycle Instance { get; } = new();

	private NullMacOsTerminalLifecycle()
	{
	}

	public void ActivateInput()
	{
	}

	public bool TryGetContinueRestoreFailure(
		out MacOsTerminalRestoreException exception)
	{
		exception = null!;
		return false;
	}

	public void SuspendAndResume()
	{
	}

	public void RestoreForShell()
	{
	}
}

internal interface IConsoleKeySource
{
	bool KeyAvailable { get; }

	bool TreatControlCAsInput { get; set; }

	ConsoleKeyInfo ReadKey(bool intercept);
}

internal sealed class SystemConsoleKeySource : IConsoleKeySource
{
	public static SystemConsoleKeySource Instance { get; } = new();

	private SystemConsoleKeySource()
	{
	}

	public bool KeyAvailable => Console.KeyAvailable;

	public bool TreatControlCAsInput
	{
		get => Console.TreatControlCAsInput;
		set => Console.TreatControlCAsInput = value;
	}

	public ConsoleKeyInfo ReadKey(bool intercept) =>
		Console.ReadKey(intercept);
}

internal sealed class ConsoleControlCPolicy : IDisposable
{
	private readonly IConsoleKeySource _console;
	private readonly bool _previousValue;
	private bool _restoreOnDispose;

	public ConsoleControlCPolicy(IConsoleKeySource console)
	{
		_console = console ?? throw new ArgumentNullException(nameof(console));
		var previousValueRead = false;
		try
		{
			_previousValue = console.TreatControlCAsInput;
			previousValueRead = true;
			if (_previousValue)
				console.TreatControlCAsInput = false;
			console.TreatControlCAsInput = true;
			_restoreOnDispose = true;
		}
		catch (InvalidOperationException)
		{
			if (previousValueRead)
				_ = TryRestorePreviousValue();
			_restoreOnDispose = false;
		}
		catch (IOException)
		{
			if (previousValueRead)
				_ = TryRestorePreviousValue();
			_restoreOnDispose = false;
		}
		catch (PlatformNotSupportedException)
		{
			if (previousValueRead)
				_ = TryRestorePreviousValue();
			_restoreOnDispose = false;
		}
	}

	public void Dispose()
	{
		if (!_restoreOnDispose)
			return;

		_restoreOnDispose = false;
		_ = TryRestorePreviousValue();
	}

	private bool TryRestorePreviousValue()
	{
		try
		{
			_console.TreatControlCAsInput = _previousValue;
			return true;
		}
		catch (InvalidOperationException)
		{
			return false;
		}
		catch (IOException)
		{
			return false;
		}
		catch (PlatformNotSupportedException)
		{
			return false;
		}
	}
}
