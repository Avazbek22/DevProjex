using System.Collections.Concurrent;
using System.Reflection;
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
	public void ConsoleInputPreservesPendingTypeAheadWhenRestoringControlC()
	{
		var keySource = new TestConsoleKeySource(
			treatControlCAsInput: false,
			new ConsoleKeyInfo(
				'x',
				ConsoleKey.X,
				shift: false,
				alt: false,
				control: false));
		using var input = new MacOsConsoleInput(keySource);

		Assert.True(keySource.TreatControlCAsInput);
		input.Dispose();

		Assert.False(keySource.TreatControlCAsInput);
		Assert.True(keySource.KeyAvailable);
		Assert.Empty(keySource.InterceptValues);
		Assert.Equal("control:False", keySource.Operations[^1]);
		var operationCountAfterFirstDispose = keySource.Operations.Count;

		input.Dispose();

		Assert.Equal(
			operationCountAfterFirstDispose,
			keySource.Operations.Count);
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
}
