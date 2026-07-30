using System.Collections.Concurrent;
using System.Reflection;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Time;

namespace DevProjex.Terminal.Tui;

internal static class TerminalGuiApplicationFactory
{
	private const string ComponentFactoryFieldName = "_componentFactory";

	public static IApplication Create()
	{
		if (!OperatingSystem.IsMacOS())
			return global::Terminal.Gui.App.Application.Create();

		return CreateMacOsApplication();
	}

	internal static IApplication CreateMacOsApplication()
	{
		var application = global::Terminal.Gui.App.Application.Create();
		var componentFactoryField = application.GetType().GetField(
			ComponentFactoryFieldName,
			BindingFlags.Instance | BindingFlags.NonPublic);
		if (componentFactoryField is null ||
		    componentFactoryField.FieldType != typeof(IComponentFactory) ||
		    !componentFactoryField.IsInitOnly ||
		    componentFactoryField.GetValue(application) is not null)
		{
			application.Dispose();
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
				new MacOsAnsiComponentFactory());
			return application;
		}
		catch (Exception exception)
			when (exception is ArgumentException or
			      FieldAccessException or
			      TargetException)
		{
			application.Dispose();
			throw new InvalidOperationException(
				"Terminal.Gui macOS compatibility backend could not be created.",
				exception);
		}
	}
}

internal sealed class MacOsAnsiComponentFactory :
	ComponentFactoryImpl<ConsoleKeyInfo>
{
	internal const string DriverName = "devprojex-macos-ansi";

	public override string? GetDriverName() => DriverName;

	public override IInput<ConsoleKeyInfo> CreateInput() =>
		new MacOsConsoleInput();

	public override IInputProcessor CreateInputProcessor(
		ConcurrentQueue<ConsoleKeyInfo> inputBuffer,
		ITimeProvider? timeProvider = null) =>
		new MacOsNetInputProcessor(inputBuffer, timeProvider);

	public override IOutput CreateOutput() =>
		new MacOsAnsiOutput(AppModel);

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

internal sealed class MacOsAnsiOutput(AppModel appModel) :
	AnsiOutput(appModel),
	IOutput
{
	private static readonly string KittyKeyboardEnableSequence =
		EscSeqUtils.CSI_EnableKittyKeyboardFlags(
			EscSeqUtils.KittyKeyboardRequestedFlags);

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
}

internal sealed class MacOsConsoleInput : InputImpl<ConsoleKeyInfo>
{
	private readonly IConsoleKeySource _console;
	private readonly ConsoleControlCPolicy _controlCPolicy;
	private int _disposed;

	public MacOsConsoleInput()
		: this(SystemConsoleKeySource.Instance)
	{
	}

	internal MacOsConsoleInput(IConsoleKeySource console)
	{
		_console = console ?? throw new ArgumentNullException(nameof(console));
		_controlCPolicy = new ConsoleControlCPolicy(console);
	}

	public override bool Peek()
	{
		try
		{
			return _console.KeyAvailable;
		}
		catch (InvalidOperationException)
		{
			return false;
		}
		catch (IOException)
		{
			return false;
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

		_controlCPolicy.Dispose();
		base.Dispose();
	}

	private bool TryRead(out ConsoleKeyInfo keyInfo)
	{
		keyInfo = default;
		try
		{
			if (!_console.KeyAvailable)
				return false;

			keyInfo = _console.ReadKey(intercept: true);
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
		try
		{
			_previousValue = console.TreatControlCAsInput;
			console.TreatControlCAsInput = true;
			_restoreOnDispose = true;
		}
		catch (InvalidOperationException)
		{
			_restoreOnDispose = false;
		}
		catch (IOException)
		{
			_restoreOnDispose = false;
		}
		catch (PlatformNotSupportedException)
		{
			_restoreOnDispose = false;
		}
	}

	public void Dispose()
	{
		if (!_restoreOnDispose)
			return;

		_restoreOnDispose = false;
		try
		{
			_console.TreatControlCAsInput = _previousValue;
		}
		catch (InvalidOperationException)
		{
			_restoreOnDispose = false;
		}
		catch (IOException)
		{
			_restoreOnDispose = false;
		}
		catch (PlatformNotSupportedException)
		{
			_restoreOnDispose = false;
		}
	}
}
