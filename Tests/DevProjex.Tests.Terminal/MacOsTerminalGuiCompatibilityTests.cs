using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace DevProjex.Tests.Terminal;

public sealed class MacOsTerminalGuiCompatibilityTests
{
	[Fact]
	public void PinnedTerminalGuiApplicationFactoryContractIsAvailable()
	{
		using var application =
			TerminalGuiApplicationFactory.CreateMacOsApplication();
		var componentFactoryField = application.GetType().GetField(
			"_componentFactory",
			BindingFlags.Instance | BindingFlags.NonPublic);

		Assert.False(application.Initialized);
		Assert.NotNull(componentFactoryField);
		Assert.IsType<MacOsAnsiComponentFactory>(
			componentFactoryField.GetValue(application));
	}

	[Fact]
	public void HybridFactoryUsesMacOsConsoleProcessorAndAnsiOutput()
	{
		var factory = new MacOsAnsiComponentFactory();
		var processor = factory.CreateInputProcessor(
			new ConcurrentQueue<ConsoleKeyInfo>());
		using var output = factory.CreateOutput();
		try
		{
			Assert.IsType<MacOsNetInputProcessor>(processor);
			Assert.IsType<MacOsAnsiOutput>(output);
			Assert.Equal(
				MacOsAnsiComponentFactory.DriverName,
				factory.GetDriverName());
		}
		finally
		{
			(processor as IDisposable)?.Dispose();
		}
	}

	[Fact]
	public void ConsoleProcessorSuppressesLegacyPrintableAfterKittySequence()
	{
		var queue = new ConcurrentQueue<ConsoleKeyInfo>();
		using var processor = new MacOsNetInputProcessor(queue);
		var keyDown = new List<Key>();
		processor.KeyDown += (_, key) => keyDown.Add(key);

		EnqueueRawCharacters(queue, "\u001b[97u");
		processor.ProcessQueue();
		queue.Enqueue(CreateConsoleKeyInfo('a', ConsoleKey.A));
		processor.ProcessQueue();

		var key = Assert.Single(keyDown);
		Assert.Equal(Key.A, key);
	}

	[Fact]
	public void ConsoleProcessorPreservesRepeatedLegacyPrintableInput()
	{
		var queue = new ConcurrentQueue<ConsoleKeyInfo>();
		using var processor = new MacOsNetInputProcessor(queue);
		var keyDown = new List<Key>();
		processor.KeyDown += (_, key) => keyDown.Add(key);

		queue.Enqueue(CreateConsoleKeyInfo('a', ConsoleKey.A));
		queue.Enqueue(CreateConsoleKeyInfo('a', ConsoleKey.A));
		processor.ProcessQueue();

		Assert.Equal(2, keyDown.Count);
		Assert.All(keyDown, static key => Assert.Equal(Key.A, key));
	}

	[Fact]
	public void ConsoleProcessorSuppressesAssociatedTextLegacyDuplicate()
	{
		var queue = new ConcurrentQueue<ConsoleKeyInfo>();
		using var processor = new MacOsNetInputProcessor(queue);
		var keyDown = new List<Key>();
		processor.KeyDown += (_, key) => keyDown.Add(key);

		EnqueueRawCharacters(queue, "\u001b[49;2;33u");
		processor.ProcessQueue();
		queue.Enqueue(CreateConsoleKeyInfo('!', ConsoleKey.D1, shift: true));
		processor.ProcessQueue();

		var key = Assert.Single(keyDown);
		Assert.Equal("!", key.GetPrintableText());
	}

	[Fact]
	public void MacOsOutputForwardsProbeButFiltersKittyKeyboardMutations()
	{
		using IOutput output = new MacOsAnsiOutput(AppModel.FullScreen);
		var initialOutput = output.GetLastOutput();

		output.Write(EscSeqUtils.CSI_QueryKittyKeyboardFlags.Request);
		var outputAfterProbe = output.GetLastOutput();
		Assert.Equal(
			initialOutput + EscSeqUtils.CSI_QueryKittyKeyboardFlags.Request,
			outputAfterProbe);

		output.Write(
			EscSeqUtils.CSI_EnableKittyKeyboardFlags(
				EscSeqUtils.KittyKeyboardRequestedFlags));
		output.Write(EscSeqUtils.CSI_DisableKittyKeyboardFlags);

		Assert.Equal(outputAfterProbe, output.GetLastOutput());
	}

	[Fact]
	public void ConsoleInputCapturesControlCAndRestoresItAfterReading()
	{
		var keySource = new TestConsoleKeySource(
			treatControlCAsInput: false,
			new ConsoleKeyInfo(
				'a',
				ConsoleKey.A,
				shift: false,
				alt: false,
				control: false));

		using (var input = new MacOsConsoleInput(keySource))
		{
			Assert.True(keySource.TreatControlCAsInput);
			Assert.True(input.Peek());
			Assert.Equal('a', Assert.Single(input.Read()).KeyChar);
			Assert.False(input.Peek());
		}

		Assert.False(keySource.TreatControlCAsInput);
		Assert.Equal([true], keySource.InterceptValues);
	}

	[Fact]
	public void ConsoleInputRestoresAnExistingControlCInputMode()
	{
		var keySource = new TestConsoleKeySource(
			treatControlCAsInput: true);

		using (new MacOsConsoleInput(keySource))
			Assert.True(keySource.TreatControlCAsInput);

		Assert.True(keySource.TreatControlCAsInput);
	}

	[Fact]
	public void ConsoleInputDisposeDoesNotExplicitlyDrainUnreadKeys()
	{
		var keySource = new TestConsoleKeySource(
			treatControlCAsInput: false,
			new ConsoleKeyInfo(
				'x',
				ConsoleKey.X,
				shift: false,
				alt: false,
				control: false));
		var terminalLifecycle =
			new TestMacOsTerminalLifecycle(keySource.Operations);
		using var input = new MacOsConsoleInput(
			keySource,
			terminalLifecycle);

		Assert.True(keySource.TreatControlCAsInput);
		input.Dispose();

		Assert.False(keySource.TreatControlCAsInput);
		Assert.True(keySource.KeyAvailable);
		Assert.Empty(keySource.InterceptValues);
		Assert.Equal(
			["control:True", "control:False", "terminal:restore"],
			keySource.Operations);
		Assert.Equal(1, terminalLifecycle.RestoreCount);
		var operationCountAfterFirstDispose = keySource.Operations.Count;

		input.Dispose();

		Assert.Equal(
			operationCountAfterFirstDispose,
			keySource.Operations.Count);
		Assert.Equal(1, terminalLifecycle.RestoreCount);
	}

	[Fact]
	public void DarwinPollDescriptorMatchesBothSupportedMacOsArchitectures()
	{
		Assert.Equal(
			8,
			Marshal.SizeOf<
				MacOsConsoleNative.PollFileDescriptor>());
		Assert.Equal(
			0,
			Marshal.OffsetOf<
				MacOsConsoleNative.PollFileDescriptor>(
				nameof(MacOsConsoleNative.PollFileDescriptor.Descriptor))
				.ToInt32());
		Assert.Equal(
			4,
			Marshal.OffsetOf<
				MacOsConsoleNative.PollFileDescriptor>(
				nameof(MacOsConsoleNative.PollFileDescriptor.Events))
				.ToInt32());
		Assert.Equal(
			6,
			Marshal.OffsetOf<
				MacOsConsoleNative.PollFileDescriptor>(
				nameof(MacOsConsoleNative.PollFileDescriptor.ResultEvents))
				.ToInt32());
	}

	[Fact]
	public void TerminalLifecycleRestoresBeforePollingAndSupportsReentry()
	{
		var native = new TestMacOsConsoleNative(
			(0, 0),
			(0, 0));
		var lifecycle = new MacOsConsoleTerminalLifecycle(native);

		lifecycle.RestoreForShell();
		lifecycle.RestoreForShell();

		Assert.Equal(
			["terminal:configure", "terminal:poll", "terminal:configure", "terminal:poll"],
			native.Operations);
	}

	[Fact]
	public void TerminalLifecycleRetriesInterruptedPoll()
	{
		var native = new TestMacOsConsoleNative(
			(-1, MacOsConsoleTerminalLifecycle.InterruptedSystemCall),
			(-1, MacOsConsoleTerminalLifecycle.InterruptedSystemCall),
			(0, 0));
		var lifecycle = new MacOsConsoleTerminalLifecycle(native);

		lifecycle.RestoreForShell();

		Assert.Equal(
			["terminal:configure", "terminal:poll", "terminal:poll", "terminal:poll"],
			native.Operations);
	}

	[Fact]
	public void TerminalLifecycleReportsNonInterruptedPollFailure()
	{
		const int nativeError = 9;
		var native = new TestMacOsConsoleNative((-1, nativeError));
		var lifecycle = new MacOsConsoleTerminalLifecycle(native);

		var exception = Assert.Throws<MacOsTerminalRestoreException>(
			lifecycle.RestoreForShell);

		Assert.Equal(nativeError, exception.NativeError);
		Assert.Equal(
			["terminal:configure", "terminal:poll"],
			native.Operations);
	}

	[Fact]
	public void TerminalLifecycleBoundsPersistentInterruptRetries()
	{
		var interruptedResults = Enumerable
			.Repeat(
				(-1, MacOsConsoleTerminalLifecycle.InterruptedSystemCall),
				MacOsConsoleTerminalLifecycle.MaximumPollAttempts)
			.ToArray();
		var native = new TestMacOsConsoleNative(interruptedResults);
		var lifecycle = new MacOsConsoleTerminalLifecycle(native);

		var exception = Assert.Throws<MacOsTerminalRestoreException>(
			lifecycle.RestoreForShell);

		Assert.Equal(
			MacOsConsoleTerminalLifecycle.InterruptedSystemCall,
			exception.NativeError);
		Assert.Equal(
			MacOsConsoleTerminalLifecycle.MaximumPollAttempts,
			native.Operations.Count(
				static operation => operation == "terminal:poll"));
	}

	[Fact]
	public void TerminalLifecycleTypesConfigureInteropFailure()
	{
		const string rawMessage = "RAW_CONFIGURE_INTEROP_FAILURE";
		var cause = new DllNotFoundException(rawMessage);
		var native = new TestMacOsConsoleNative
		{
			ConfigureException = cause
		};
		var lifecycle = new MacOsConsoleTerminalLifecycle(native);

		var exception = Assert.Throws<MacOsTerminalRestoreException>(
			lifecycle.RestoreForShell);

		Assert.Same(cause, exception.InnerException);
		Assert.Null(exception.NativeError);
		Assert.Equal(
			["terminal:configure"],
			native.Operations);
		Assert.DoesNotContain(
			rawMessage,
			exception.Message,
			StringComparison.Ordinal);
	}

	[Fact]
	public void TerminalLifecycleTypesPollInteropFailure()
	{
		const string rawMessage = "RAW_POLL_INTEROP_FAILURE";
		var cause = new EntryPointNotFoundException(rawMessage);
		var native = new TestMacOsConsoleNative
		{
			PollException = cause
		};
		var lifecycle = new MacOsConsoleTerminalLifecycle(native);

		var exception = Assert.Throws<MacOsTerminalRestoreException>(
			lifecycle.RestoreForShell);

		Assert.Same(cause, exception.InnerException);
		Assert.Null(exception.NativeError);
		Assert.Equal(
			["terminal:configure", "terminal:poll"],
			native.Operations);
		Assert.DoesNotContain(
			rawMessage,
			exception.Message,
			StringComparison.Ordinal);
	}

	[Fact]
	public void CompatibilityLayerDoesNotUseTerminalGuiInputImplementations()
	{
		var sourcePath = Path.Combine(
			PublishedApplicationLocator.FindRepositoryRoot(),
			"Apps",
			"Terminal",
			"DevProjex.Terminal",
			"Tui",
			"MacOsTerminalGuiCompatibility.cs");
		var source = File.ReadAllText(sourcePath);

		Assert.Contains(
			"ComponentFactoryImpl<ConsoleKeyInfo>",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"new MacOsNetInputProcessor",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"new MacOsAnsiOutput",
			source,
			StringComparison.Ordinal);
		Assert.DoesNotMatch(
			@"new\s+(AnsiInput|NetInput)\s*\(",
			source);
		Assert.DoesNotContain(
			"Console.Out",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"SystemNative_ConfigureTerminalForChildProcess",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"poll(",
			source,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"SystemNative_UninitializeTerminal",
			source,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"tcsetattr",
			source,
			StringComparison.Ordinal);
	}

	private static void EnqueueRawCharacters(
		ConcurrentQueue<ConsoleKeyInfo> queue,
		string value)
	{
		foreach (var character in value)
			queue.Enqueue(CreateConsoleKeyInfo(character, (ConsoleKey)0));
	}

	private static ConsoleKeyInfo CreateConsoleKeyInfo(
		char character,
		ConsoleKey key,
		bool shift = false)
	{
		return new ConsoleKeyInfo(
			character,
			key,
			shift,
			alt: false,
			control: false);
	}

	private sealed class TestConsoleKeySource : IConsoleKeySource
	{
		private readonly Queue<ConsoleKeyInfo> _keys;
		private bool _treatControlCAsInput;

		public TestConsoleKeySource(
			bool treatControlCAsInput,
			params ConsoleKeyInfo[] keys)
		{
			_treatControlCAsInput = treatControlCAsInput;
			_keys = new Queue<ConsoleKeyInfo>(keys);
		}

		public bool KeyAvailable => _keys.Count > 0;

		public bool TreatControlCAsInput
		{
			get => _treatControlCAsInput;
			set
			{
				_treatControlCAsInput = value;
				Operations.Add($"control:{value}");
			}
		}

		public List<bool> InterceptValues { get; } = [];

		public List<string> Operations { get; } = [];

		public ConsoleKeyInfo ReadKey(bool intercept)
		{
			InterceptValues.Add(intercept);
			Operations.Add("read");
			return _keys.Dequeue();
		}
	}

	private sealed class TestMacOsTerminalLifecycle(
		List<string> operations) : IMacOsTerminalLifecycle
	{
		public int RestoreCount { get; private set; }

		public void RestoreForShell()
		{
			RestoreCount++;
			operations.Add("terminal:restore");
		}
	}

	private sealed class TestMacOsConsoleNative(
		params (int Result, int NativeError)[] pollResults) :
		IMacOsConsoleNative
	{
		private readonly Queue<(int Result, int NativeError)> _pollResults =
			new(pollResults);

		public List<string> Operations { get; } = [];
		public Exception? ConfigureException { get; init; }
		public Exception? PollException { get; init; }

		public void ConfigureTerminalForShell()
		{
			Operations.Add("terminal:configure");
			if (ConfigureException is not null)
				throw ConfigureException;
		}

		public int PollStandardInput(out int nativeError)
		{
			Operations.Add("terminal:poll");
			if (PollException is not null)
				throw PollException;
			var pollResult = _pollResults.Count == 0
				? (Result: 0, NativeError: 0)
				: _pollResults.Dequeue();
			nativeError = pollResult.NativeError;
			return pollResult.Result;
		}
	}
}
