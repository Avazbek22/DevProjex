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
		using var applicationContext =
			TerminalGuiApplicationFactory.CreateMacOsApplication();
		var application = applicationContext.Application;
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
	public void MacOsSuspendUsesExactLifecycleWithoutStartingAChildProcess()
	{
		var operations = new List<string>();
		var lifecycle = new TestMacOsTerminalLifecycle(operations);
		using IOutput output = new MacOsAnsiOutput(
			AppModel.FullScreen,
			() => lifecycle,
			static () => false);
		var initialOutput = output.GetLastOutput();

		output.Suspend();

		Assert.Equal(["terminal:suspend"], operations);
		var suspendOutput = output.GetLastOutput()[initialOutput.Length..];
		Assert.Contains(
			EscSeqUtils.CSI_DisableBracketedPaste,
			suspendOutput,
			StringComparison.Ordinal);
		Assert.Contains(
			EscSeqUtils.CSI_ResetAttributes,
			suspendOutput,
			StringComparison.Ordinal);
		Assert.Contains(
			EscSeqUtils.CSI_RestoreCursorAndRestoreAltBufferWithBackscroll,
			suspendOutput,
			StringComparison.Ordinal);
		Assert.Contains(
			EscSeqUtils.CSI_SaveCursorAndActivateAltBufferNoBackscroll,
			suspendOutput,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			EscSeqUtils.CSI_EnableMouseEvents,
			suspendOutput,
			StringComparison.Ordinal);
	}

	[Fact]
	public void MacOsSuspendFailureRestoresShellAndStopsPresentationReentry()
	{
		var operations = new List<string>();
		var failure = new MacOsTerminalRestoreException(5);
		var lifecycle = new TestMacOsTerminalLifecycle(operations)
		{
			SuspendException = failure
		};
		var reportedFailures =
			new List<MacOsTerminalRestoreException>();
		using IOutput output = new MacOsAnsiOutput(
			AppModel.FullScreen,
			() => lifecycle,
			static () => true,
			reportedFailures.Add);
		var initialOutput = output.GetLastOutput();

		output.Suspend();

		Assert.Equal(
			["terminal:suspend", "terminal:restore"],
			operations);
		Assert.Same(failure, Assert.Single(reportedFailures));
		var suspendOutput = output.GetLastOutput()[initialOutput.Length..];
		Assert.Contains(
			EscSeqUtils.CSI_RestoreCursorAndRestoreAltBufferWithBackscroll,
			suspendOutput,
			StringComparison.Ordinal);
		Assert.Contains(
			EscSeqUtils.CSI_ShowCursor,
			suspendOutput,
			StringComparison.Ordinal);
		Assert.Contains(
			EscSeqUtils.CSI_ResetAttributes,
			suspendOutput,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			EscSeqUtils.CSI_SaveCursorAndActivateAltBufferNoBackscroll,
			suspendOutput,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			EscSeqUtils.CSI_EnableMouseEvents,
			suspendOutput,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			EscSeqUtils.CSI_EnableBracketedPaste,
			suspendOutput,
			StringComparison.Ordinal);
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
		Assert.Equal(
			["control:False", "control:True", "control:True"],
			keySource.Operations);
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
			[
				"control:True",
				"terminal:activate",
				"control:False",
				"terminal:restore"
			],
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
	public void ConsoleInputReportsRestoreFailureWithoutFaultingItsInputLoop()
	{
		var keySource = new TestConsoleKeySource(
			treatControlCAsInput: false);
		var terminalLifecycle =
			new TestMacOsTerminalLifecycle(keySource.Operations)
			{
				ContinueRestoreFailure =
					new MacOsTerminalRestoreException()
			};
		var reportedFailures =
			new List<MacOsTerminalRestoreException>();
		using var input = new MacOsConsoleInput(
			keySource,
			terminalLifecycle,
			reportedFailures.Add);

		Assert.False(input.Peek());

		Assert.Same(
			terminalLifecycle.ContinueRestoreFailure,
			Assert.Single(reportedFailures));
	}

	[Fact]
	public void InputFailureSourceCancelsRunLoopAndRethrowsTypedFailure()
	{
		using var failureSource =
			new MacOsTerminalInputFailureSource();
		var failure = new MacOsTerminalRestoreException();

		failureSource.Report(failure);
		failureSource.Report(new MacOsTerminalRestoreException());

		Assert.True(failureSource.Token.IsCancellationRequested);
		Assert.Same(
			failure,
			Assert.Throws<MacOsTerminalRestoreException>(
				failureSource.ThrowIfReported));
	}

	[Fact]
	public void DarwinPollDescriptorMatchesBothSupportedMacOsArchitectures()
	{
		Assert.Equal(
			72,
			Marshal.SizeOf<
				MacOsConsoleNative.TerminalAttributes>());
		Assert.Equal(
			24,
			Marshal.OffsetOf<
				MacOsConsoleNative.TerminalAttributes>(
				nameof(MacOsConsoleNative.TerminalAttributes.LocalModes))
				.ToInt32());
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
	public void TermInfoModeCapabilitiesRemainAnExactPair()
	{
		var capabilities =
			MacOsTerminalModeCapabilityProvider.Capture(
				capability => capability switch
				{
					"smkx" => new MacOsTerminalCapabilityResult(
						0,
						"\u001b[?1h\u001b="),
					"rmkx" => new MacOsTerminalCapabilityResult(
						0,
						"\u001b[?1l\u001b>"),
					_ => throw new ArgumentOutOfRangeException(
						nameof(capability))
				});

		Assert.Equal("\u001b[?1h\u001b=", capabilities.ApplicationMode);
		Assert.Equal("\u001b[?1l\u001b>", capabilities.ShellMode);
	}

	[Fact]
	public void TermInfoModeCapabilitiesRejectApplicationModeWithoutShellMode()
	{
		Assert.Throws<MacOsTerminalRestoreException>(
			() => MacOsTerminalModeCapabilityProvider.Capture(
				capability => capability == "smkx"
					? new MacOsTerminalCapabilityResult(0, "\u001b=")
					: new MacOsTerminalCapabilityResult(1, string.Empty)));
	}

	[Fact]
	public void TerminalLifecycleRestoresBeforePollingAndSupportsReentry()
	{
		var native = new TestMacOsConsoleNative(
			(0, 0),
			(0, 0));
		var lifecycle = MacOsConsoleTerminalLifecycle.Capture(native);

		lifecycle.RestoreForShell();
		lifecycle.RestoreForShell();

		Assert.Equal(
			[
				"terminal:foreground",
				"terminal:get",
				"terminal:foreground",
				"terminal:set",
				"terminal:poll",
				"terminal:set",
				"terminal:get",
				"terminal:foreground",
				"terminal:set",
				"terminal:poll",
				"terminal:set",
				"terminal:get"
			],
			native.Operations);
	}

	[Fact]
	public void TerminalLifecycleRetriesInterruptedPoll()
	{
		var native = new TestMacOsConsoleNative(
			(-1, MacOsConsoleTerminalLifecycle.InterruptedSystemCall),
			(-1, MacOsConsoleTerminalLifecycle.InterruptedSystemCall),
			(0, 0));
		var lifecycle = CreateLifecycle(native);

		lifecycle.RestoreForShell();

		Assert.Equal(
			[
				"terminal:foreground",
				"terminal:set",
				"terminal:poll",
				"terminal:poll",
				"terminal:poll",
				"terminal:set",
				"terminal:get"
			],
			native.Operations);
	}

	[Fact]
	public void TerminalLifecycleRetriesInterruptedCaptureAndRestore()
	{
		var native = new TestMacOsConsoleNative();
		native.GetResults.Enqueue(
			(-1, MacOsConsoleTerminalLifecycle.InterruptedSystemCall));
		native.GetResults.Enqueue((0, 0));
		native.SetResults.Enqueue(
			(-1, MacOsConsoleTerminalLifecycle.InterruptedSystemCall));
		native.SetResults.Enqueue((0, 0));

		var lifecycle = MacOsConsoleTerminalLifecycle.Capture(native);
		lifecycle.RestoreForShell();

		Assert.Equal(
			[
				"terminal:foreground",
				"terminal:get",
				"terminal:get",
				"terminal:foreground",
				"terminal:set",
				"terminal:set",
				"terminal:poll",
				"terminal:set",
				"terminal:get"
			],
			native.Operations);
	}

	[Fact]
	public void TerminalLifecycleGuardsTheCompleteNativeTeardownFromContinue()
	{
		var native = new TestMacOsConsoleNative();
		var signalPolicy = new TestMacOsTerminalSignalPolicy(native.Operations);

		var lifecycle = MacOsConsoleTerminalLifecycle.Capture(
			native,
			signalPolicy);
		lifecycle.ActivateInput();
		lifecycle.RestoreForShell();

		Assert.Equal(
			[
				"terminal:foreground",
				"terminal:get",
				"terminal:foreground",
				"signal:session",
				"terminal:set",
				"terminal:get",
				"signal:teardown",
				"terminal:foreground",
				"terminal:set",
				"terminal:poll",
				"terminal:set",
				"terminal:get"
			],
			native.Operations);
	}

	[Fact]
	public void TerminalLifecycleActivatesDeterministicConsoleInputMode()
	{
		var native = new TestMacOsConsoleNative();
		var initialAttributes = CreateAttributes(pendingInput: false);
		var signalPolicy = new TestMacOsTerminalSignalPolicy(native.Operations);
		var terminalModePolicy =
			new TestMacOsTerminalModePolicy(native.Operations);
		var lifecycle = new MacOsConsoleTerminalLifecycle(
			native,
			initialAttributes,
			signalPolicy,
			terminalModePolicy);

		lifecycle.ActivateInput();

		Assert.Equal(
			[
				"terminal:foreground",
				"signal:session",
				"terminal:set",
				"terminal:get",
				"mode:application"
			],
			native.Operations);
		Assert.Equal(
			initialAttributes.ToConsoleInputMode(),
			Assert.Single(native.SetAttributes));
		Assert.Equal(
			initialAttributes.ToConsoleInputMode(),
			signalPolicy.ActiveAttributes);
	}

	[Fact]
	public void TerminalLifecycleSuspendsOnlyAfterExactShellRestore()
	{
		var native = new TestMacOsConsoleNative();
		var signalPolicy = new TestMacOsTerminalSignalPolicy(native.Operations);
		var terminalModePolicy =
			new TestMacOsTerminalModePolicy(native.Operations);
		var lifecycle = new MacOsConsoleTerminalLifecycle(
			native,
			CreateAttributes(pendingInput: false),
			signalPolicy,
			terminalModePolicy);

		lifecycle.SuspendAndResume();

		Assert.Equal(
			[
				"signal:teardown",
				"terminal:foreground",
				"mode:shell",
				"terminal:set",
				"terminal:poll",
				"terminal:set",
				"terminal:get",
				"terminal:suspend",
				"terminal:foreground",
				"signal:session",
				"terminal:set",
				"terminal:get",
				"mode:application"
			],
			native.Operations);
	}

	[Fact]
	public void TerminalLifecycleEndsSignalSessionWhenActivationFails()
	{
		const int nativeError = 9;
		var native = new TestMacOsConsoleNative();
		native.SetResults.Enqueue((-1, nativeError));
		var signalPolicy = new TestMacOsTerminalSignalPolicy(native.Operations);
		var lifecycle = new MacOsConsoleTerminalLifecycle(
			native,
			CreateAttributes(pendingInput: false),
			signalPolicy);

		var exception = Assert.Throws<MacOsTerminalRestoreException>(
			lifecycle.ActivateInput);

		Assert.Equal(nativeError, exception.NativeError);
		Assert.Equal(
			[
				"terminal:foreground",
				"signal:session",
				"terminal:set",
				"signal:teardown"
			],
			native.Operations);
	}

	[Fact]
	public void TerminalLifecycleEndsSignalSessionWhenCaptureFails()
	{
		var native = new TestMacOsConsoleNative
		{
			IsForeground = false
		};
		var signalPolicy = new TestMacOsTerminalSignalPolicy(native.Operations);

		_ = Assert.Throws<MacOsTerminalRestoreException>(
			() => MacOsConsoleTerminalLifecycle.Capture(
				native,
				signalPolicy));

		Assert.Equal(
			[
				"terminal:foreground",
				"signal:teardown"
			],
			native.Operations);
	}

	[Fact]
	public void SignalPolicyCancelsRuntimeContinueAndReappliesOnlyActiveMode()
	{
		var restoreCount = 0;
		var signalPolicy = new MacOsTerminalSignalPolicy(
			_ =>
			{
				restoreCount++;
				return true;
			});

		signalPolicy.HandleContinueCore();
		Assert.Equal(0, restoreCount);

		signalPolicy.BeginSession(
			CreateAttributes(pendingInput: false));
		signalPolicy.HandleContinueCore();
		Assert.Equal(1, restoreCount);
		Assert.False(
			signalPolicy.TryGetContinueRestoreFailure(out _));

		signalPolicy.BeginTerminalTeardown();
		signalPolicy.HandleContinueCore();
		Assert.Equal(1, restoreCount);
	}

	[Fact]
	public void SignalPolicyReportsActiveModeRestoreFailureWithTypedError()
	{
		var signalPolicy = new MacOsTerminalSignalPolicy(
			static _ => false);
		signalPolicy.BeginSession(
			CreateAttributes(pendingInput: false));

		signalPolicy.HandleContinueCore();

		Assert.True(
			signalPolicy.TryGetContinueRestoreFailure(
				out var exception));
		Assert.Equal(
			"DPX-TUI-MACOS-TERMINAL-RESTORE",
			exception.Message);
	}

	[Fact]
	public void ActiveContinueRunsRuntimeRecoveryBeforeExactTermiosRestore()
	{
		var native = new TestMacOsConsoleNative();
		var activeAttributes =
			CreateAttributes(pendingInput: false).ToConsoleInputMode();

		var restored = MacOsTerminalSignalPolicy.TryRestoreActiveConsoleMode(
			native,
			activeAttributes);

		Assert.True(restored);
		Assert.Equal(
			[
				"terminal:foreground",
				"terminal:runtime-continue",
				"terminal:set"
			],
			native.Operations);
		Assert.Equal(activeAttributes, Assert.Single(native.SetAttributes));
	}

	[Fact]
	public void ActiveContinueRetriesInterruptedExactTermiosRestore()
	{
		var native = new TestMacOsConsoleNative();
		native.SetResults.Enqueue(
			(-1, MacOsConsoleTerminalLifecycle.InterruptedSystemCall));
		native.SetResults.Enqueue((0, 0));

		var restored = MacOsTerminalSignalPolicy.TryRestoreActiveConsoleMode(
			native,
			CreateAttributes(pendingInput: false).ToConsoleInputMode());

		Assert.True(restored);
		Assert.Equal(
			[
				"terminal:foreground",
				"terminal:runtime-continue",
				"terminal:set",
				"terminal:set"
			],
			native.Operations);
	}

	[Fact]
	public void ActiveContinueRejectsARuntimeRecoveryInteropFailure()
	{
		var native = new TestMacOsConsoleNative
		{
			ContinueException = new EntryPointNotFoundException()
		};

		var restored = MacOsTerminalSignalPolicy.TryRestoreActiveConsoleMode(
			native,
			CreateAttributes(pendingInput: false).ToConsoleInputMode());

		Assert.False(restored);
		Assert.Equal(
			[
				"terminal:foreground",
				"terminal:runtime-continue"
			],
			native.Operations);
	}

	[Fact]
	public void BackgroundContinueIsASuccessfulNoOp()
	{
		var native = new TestMacOsConsoleNative
		{
			IsForeground = false
		};

		var restored = MacOsTerminalSignalPolicy.TryRestoreActiveConsoleMode(
			native,
			CreateAttributes(pendingInput: false).ToConsoleInputMode());

		Assert.True(restored);
		Assert.Equal(["terminal:foreground"], native.Operations);
	}

	[Fact]
	public async Task SignalPolicySerializesActiveContinueWithTerminalTeardown()
	{
		using var restoreEntered = new ManualResetEventSlim();
		using var releaseRestore = new ManualResetEventSlim();
		var cancellationToken = TestContext.Current.CancellationToken;
		var signalPolicy = new MacOsTerminalSignalPolicy(
			_ =>
			{
				restoreEntered.Set();
				Assert.True(
					releaseRestore.Wait(
						TimeSpan.FromSeconds(5),
						cancellationToken));
				return true;
			});
		signalPolicy.BeginSession(
			CreateAttributes(pendingInput: false));

		var continueTask = Task.Run(
			signalPolicy.HandleContinueCore,
			cancellationToken);
		Assert.True(
			restoreEntered.Wait(
				TimeSpan.FromSeconds(5),
				cancellationToken));
		var teardownTask = Task.Run(
			signalPolicy.BeginTerminalTeardown,
			cancellationToken);
		Assert.False(teardownTask.IsCompleted);

		releaseRestore.Set();
		await continueTask;
		await teardownTask;

		signalPolicy.HandleContinueCore();
		Assert.False(
			signalPolicy.TryGetContinueRestoreFailure(out _));
	}

	[Fact]
	public void TerminalLifecycleReportsNonInterruptedPollFailure()
	{
		const int nativeError = 9;
		var native = new TestMacOsConsoleNative((-1, nativeError));
		var lifecycle = CreateLifecycle(native);

		var exception = Assert.Throws<MacOsTerminalRestoreException>(
			lifecycle.RestoreForShell);

		Assert.Equal(nativeError, exception.NativeError);
		Assert.Equal(
			["terminal:foreground", "terminal:set", "terminal:poll", "terminal:set"],
			native.Operations);
		AssertFinalRestoreMatchesInitial(native);
	}

	[Fact]
	public void TerminalLifecycleBoundsPersistentInterruptRetries()
	{
		var interruptedResults = Enumerable
			.Repeat(
				(-1, MacOsConsoleTerminalLifecycle.InterruptedSystemCall),
				MacOsConsoleTerminalLifecycle.MaximumNativeAttempts)
			.ToArray();
		var native = new TestMacOsConsoleNative(interruptedResults);
		var lifecycle = CreateLifecycle(native);

		var exception = Assert.Throws<MacOsTerminalRestoreException>(
			lifecycle.RestoreForShell);

		Assert.Equal(
			MacOsConsoleTerminalLifecycle.InterruptedSystemCall,
			exception.NativeError);
		Assert.Equal(
			MacOsConsoleTerminalLifecycle.MaximumNativeAttempts,
			native.Operations.Count(
				static operation => operation == "terminal:poll"));
		AssertFinalRestoreMatchesInitial(native);
	}

	[Fact]
	public void TerminalLifecycleTypesRestoreInteropFailure()
	{
		const string rawMessage = "RAW_RESTORE_INTEROP_FAILURE";
		var cause = new DllNotFoundException(rawMessage);
		var native = new TestMacOsConsoleNative
		{
			SetException = cause
		};
		var lifecycle = CreateLifecycle(native);

		var exception = Assert.Throws<MacOsTerminalRestoreException>(
			lifecycle.RestoreForShell);

		Assert.Same(cause, exception.InnerException);
		Assert.Null(exception.NativeError);
		Assert.Equal(
			["terminal:foreground", "terminal:set", "terminal:set"],
			native.Operations);
		AssertFinalRestoreMatchesInitial(native);
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
		var lifecycle = CreateLifecycle(native);

		var exception = Assert.Throws<MacOsTerminalRestoreException>(
			lifecycle.RestoreForShell);

		Assert.Same(cause, exception.InnerException);
		Assert.Null(exception.NativeError);
		Assert.Equal(
			["terminal:foreground", "terminal:set", "terminal:poll", "terminal:set"],
			native.Operations);
		AssertFinalRestoreMatchesInitial(native);
		Assert.DoesNotContain(
			rawMessage,
			exception.Message,
			StringComparison.Ordinal);
	}

	[Fact]
	public void TerminalLifecycleTypesCaptureInteropFailure()
	{
		const string rawMessage = "RAW_CAPTURE_INTEROP_FAILURE";
		var cause = new BadImageFormatException(rawMessage);
		var native = new TestMacOsConsoleNative
		{
			GetException = cause
		};

		var exception = Assert.Throws<MacOsTerminalRestoreException>(
			() => MacOsConsoleTerminalLifecycle.Capture(native));

		Assert.Same(cause, exception.InnerException);
		Assert.Null(exception.NativeError);
		Assert.Equal(["terminal:foreground", "terminal:get"], native.Operations);
		Assert.DoesNotContain(
			rawMessage,
			exception.Message,
			StringComparison.Ordinal);
	}

	[Fact]
	public void TerminalLifecycleRejectsAReportedSuccessfulRestoreWithPendingInput()
	{
		var native = new TestMacOsConsoleNative
		{
			SetPendingInputOnRestore = true,
			LeavePendingInputAfterPoll = true
		};
		var lifecycle = CreateLifecycle(native);

		var exception = Assert.Throws<MacOsTerminalRestoreException>(
			lifecycle.RestoreForShell);

		Assert.Null(exception.NativeError);
		Assert.Equal(
			[
				"terminal:foreground",
				"terminal:set",
				"terminal:poll",
				"terminal:set",
				"terminal:get"
			],
			native.Operations);
	}

	[Fact]
	public void TerminalLifecycleDoesNotMutateABackgroundTerminal()
	{
		var native = new TestMacOsConsoleNative
		{
			IsForeground = false
		};
		var lifecycle = CreateLifecycle(native);

		lifecycle.RestoreForShell();

		Assert.Equal(["terminal:foreground"], native.Operations);
	}

	[Fact]
	public void TerminalLifecycleDoesNotStartInABackgroundTerminal()
	{
		var native = new TestMacOsConsoleNative
		{
			IsForeground = false
		};

		var exception = Assert.Throws<MacOsTerminalRestoreException>(
			() => MacOsConsoleTerminalLifecycle.Capture(native));

		Assert.Null(exception.NativeError);
		Assert.Equal(["terminal:foreground"], native.Operations);
	}

	[Fact]
	public void TerminalLifecyclePreservesAnInitiallyPendingInputState()
	{
		var native = new TestMacOsConsoleNative();
		var initialAttributes = CreateAttributes(pendingInput: true);
		var lifecycle = new MacOsConsoleTerminalLifecycle(
			native,
			initialAttributes);

		lifecycle.RestoreForShell();

		Assert.Equal(
			["terminal:foreground", "terminal:set"],
			native.Operations);
	}

	[Fact]
	public void TerminalLifecycleSuppressesActiveInputControlsOnlyDuringPendingReplay()
	{
		const ulong signalGeneration = 0x00000080;
		const ulong extendedInputProcessing = 0x00000400;
		const ulong outputFlowControl = 0x00000200;
		const ulong inputFlowControl = 0x00000400;
		var native = new TestMacOsConsoleNative();
		var initialAttributes = CreateAttributes(pendingInput: false);
		var lifecycle = new MacOsConsoleTerminalLifecycle(
			native,
			initialAttributes);

		lifecycle.RestoreForShell();

		Assert.Equal(2, native.SetAttributes.Count);
		Assert.Equal(
			0UL,
			native.SetAttributes[0].LocalModes &
			(signalGeneration | extendedInputProcessing));
		Assert.Equal(
			0UL,
			native.SetAttributes[0].InputModes &
			(outputFlowControl | inputFlowControl));
		Assert.Equal(initialAttributes, native.SetAttributes[1]);
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
		Assert.Contains("tcgetattr", source, StringComparison.Ordinal);
		Assert.Contains("tcsetattr", source, StringComparison.Ordinal);
		Assert.Contains(
			"poll(",
			source,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"SystemNative_UninitializeTerminal",
			source,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"SystemNative_ConfigureTerminalForChildProcess",
			source,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"Process.Start",
			source,
			StringComparison.Ordinal);
	}

	private static MacOsConsoleTerminalLifecycle CreateLifecycle(
		IMacOsConsoleNative native) =>
		new(native, CreateAttributes(pendingInput: false));

	private static void AssertFinalRestoreMatchesInitial(
		TestMacOsConsoleNative native)
	{
		Assert.True(native.SetAttributes.Count >= 2);
		Assert.Equal(
			CreateAttributes(pendingInput: false),
			native.SetAttributes[^1]);
	}

	private static MacOsConsoleNative.TerminalAttributes CreateAttributes(
		bool pendingInput)
	{
		const ulong pendingInputMask = 0x20000000;
		const ulong signalGeneration = 0x00000080;
		const ulong extendedInputProcessing = 0x00000400;
		const ulong outputFlowControl = 0x00000200;
		const ulong inputFlowControl = 0x00000400;
		return new MacOsConsoleNative.TerminalAttributes
		{
			InputModes = outputFlowControl | inputFlowControl,
			LocalModes = signalGeneration |
			             extendedInputProcessing |
			             (pendingInput
				             ? pendingInputMask
				             : 0)
		};
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
		public Exception? SuspendException { get; init; }
		public MacOsTerminalRestoreException? ContinueRestoreFailure
		{
			get;
			init;
		}

		public void ActivateInput() =>
			operations.Add("terminal:activate");

		public bool TryGetContinueRestoreFailure(
			out MacOsTerminalRestoreException exception)
		{
			exception = ContinueRestoreFailure!;
			return ContinueRestoreFailure is not null;
		}

		public void RestoreForShell()
		{
			RestoreCount++;
			operations.Add("terminal:restore");
		}

		public void SuspendAndResume()
		{
			operations.Add("terminal:suspend");
			if (SuspendException is not null)
				throw SuspendException;
		}
	}

	private sealed class TestMacOsConsoleNative(
		params (int Result, int NativeError)[] pollResults) :
		IMacOsConsoleNative
	{
		private const ulong PendingInputMask = 0x20000000;
		private readonly Queue<(int Result, int NativeError)> _pollResults =
			new(pollResults);
		private MacOsConsoleNative.TerminalAttributes _currentAttributes =
			CreateAttributes(pendingInput: false);

		public List<string> Operations { get; } = [];
		public List<MacOsConsoleNative.TerminalAttributes> SetAttributes { get; } = [];
		public Queue<(int Result, int NativeError)> GetResults { get; } = [];
		public Queue<(int Result, int NativeError)> SetResults { get; } = [];
		public Exception? GetException { get; init; }
		public Exception? SetException { get; init; }
		public Exception? PollException { get; init; }
		public Exception? ContinueException { get; init; }
		public bool IsForeground { get; init; } = true;
		public bool SetPendingInputOnRestore { get; init; }
		public bool LeavePendingInputAfterPoll { get; init; }
		public int SuspendResult { get; init; }
		public int SuspendError { get; init; }

		public int GetStandardInputAttributes(
			out MacOsConsoleNative.TerminalAttributes attributes,
			out int nativeError)
		{
			Operations.Add("terminal:get");
			if (GetException is not null)
				throw GetException;
			attributes = _currentAttributes;
			var result = GetResults.Count == 0
				? (Result: 0, NativeError: 0)
				: GetResults.Dequeue();
			nativeError = result.NativeError;
			return result.Result;
		}

		public int SetStandardInputAttributes(
			MacOsConsoleNative.TerminalAttributes attributes,
			out int nativeError)
		{
			Operations.Add("terminal:set");
			SetAttributes.Add(attributes);
			if (SetException is not null)
				throw SetException;
			var result = SetResults.Count == 0
				? (Result: 0, NativeError: 0)
				: SetResults.Dequeue();
			if (result.Result >= 0)
			{
				_currentAttributes = attributes;
				if (SetPendingInputOnRestore)
					_currentAttributes.LocalModes |= PendingInputMask;
			}
			nativeError = result.NativeError;
			return result.Result;
		}

		public bool IsStandardInputForeground(out int nativeError)
		{
			Operations.Add("terminal:foreground");
			nativeError = 0;
			return IsForeground;
		}

		public int PollStandardInput(out int nativeError)
		{
			Operations.Add("terminal:poll");
			if (PollException is not null)
				throw PollException;
			var pollResult = _pollResults.Count == 0
				? (Result: 0, NativeError: 0)
				: _pollResults.Dequeue();
			if (pollResult.Result >= 0 && !LeavePendingInputAfterPoll)
				_currentAttributes.LocalModes &= ~PendingInputMask;
			nativeError = pollResult.NativeError;
			return pollResult.Result;
		}

		public void HandleNonCanceledContinue()
		{
			Operations.Add("terminal:runtime-continue");
			if (ContinueException is not null)
				throw ContinueException;
		}

		public int SuspendProcessGroup(out int nativeError)
		{
			Operations.Add("terminal:suspend");
			nativeError = SuspendError;
			return SuspendResult;
		}
	}

	private sealed class TestMacOsTerminalSignalPolicy(
		List<string> operations) :
		IMacOsTerminalSignalPolicy
	{
		public MacOsConsoleNative.TerminalAttributes ActiveAttributes
		{
			get;
			private set;
		}

		public void BeginSession(
			MacOsConsoleNative.TerminalAttributes activeAttributes)
		{
			ActiveAttributes = activeAttributes;
			operations.Add("signal:session");
		}

		public void BeginTerminalTeardown() =>
			operations.Add("signal:teardown");

		public bool TryGetContinueRestoreFailure(
			out MacOsTerminalRestoreException exception)
		{
			exception = null!;
			return false;
		}
	}

	private sealed class TestMacOsTerminalModePolicy(
		List<string> operations) : IMacOsTerminalModePolicy
	{
		public void EnterApplicationMode() =>
			operations.Add("mode:application");

		public void EnterShellMode() =>
			operations.Add("mode:shell");
	}
}
