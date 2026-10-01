using System.Collections;
using System.Diagnostics;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class MetricsPipelineCoherentReadTests
{
	[AvaloniaTheory]
	[InlineData(StableSource.Text)]
	[InlineData(StableSource.Empty)]
	[InlineData(StableSource.Unicode)]
	[InlineData(StableSource.Utf8Bom)]
	[InlineData(StableSource.Utf16)]
	[InlineData(StableSource.UnknownBinary)]
	[InlineData(StableSource.KnownBinary)]
	[InlineData(StableSource.Estimated)]
	public async Task StablePhysicalSource_PreservesMetricsWithOnlyFinalAndPublicationVersionChecks(StableSource source)
	{
		using var workspace = new TemporaryDirectory();
		var path = CreateSource(workspace, source);
		var expected = await new FileContentAnalyzer().GetClassifiedMetricsAsync(
			path, TestContext.Current.CancellationToken);
		var contentOpens = 0;
		var analyzer = new FileContentAnalyzer((filePath, bufferSize, share, asynchronous) =>
		{
			Interlocked.Increment(ref contentOpens);
			return new FileStream(filePath, FileMode.Open, FileAccess.Read, share, bufferSize, asynchronous);
		});
		using var fixture = new Fixture(workspace.Path, path, analyzer);

		await fixture.InitializeAsync();

		Assert.True(fixture.Pipeline.HasCompleteBaseline);
		Assert.False(fixture.ViewModel.StatusBusy);
		var cached = Assert.IsType<CachedMetrics>(ReadCachedMetrics(fixture.Pipeline, path));
		Assert.Equal(expected.IsText, cached.HasMetrics);
		Assert.Equal(expected.IsText ? expected.Metrics : null, cached.Metrics);
		Assert.Equal(source == StableSource.KnownBinary ? 0 : 1, contentOpens);
		Assert.Equal(source == StableSource.KnownBinary ? 3 : 2,
			fixture.Io.Snapshot.FileVersionOpenCount);
	}

	[AvaloniaTheory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task UnavailableMetrics_PreserveCompletedZeroMetricsFallback(bool failContentOpen)
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateBinaryFile("unavailable.txt", [0xff, 0x61]);
		var analyzer = failContentOpen
			? new FileContentAnalyzer((_, _, _, _) => throw new IOException("Controlled content-open failure."))
			: new FileContentAnalyzer();
		using var fixture = new Fixture(workspace.Path, path, analyzer);

		await fixture.InitializeAsync();

		Assert.True(fixture.Pipeline.HasCompleteBaseline);
		Assert.False(fixture.ViewModel.StatusBusy);
		var cached = Assert.IsType<CachedMetrics>(ReadCachedMetrics(fixture.Pipeline, path));
		Assert.False(cached.HasMetrics);
		Assert.Null(cached.Metrics);
		Assert.Equal(ContextRootPresentation.FormatLine(workspace.Path).Length, ReadPublishedContentCharacters(fixture.Pipeline));
	}

	[AvaloniaFact]
	public async Task MutationDuringRead_RejectsMetricsWithoutStableHandleIdentity()
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("changing.txt", new string('a', 2048));
		var timestamp = File.GetLastWriteTimeUtc(path);
		var changed = false;
		var analyzer = new FileContentAnalyzer((filePath, _, _, _) =>
			new CallbackReadFileStream(filePath, afterFirstRead: () =>
			{
				File.WriteAllText(filePath, new string('b', 2048));
				File.SetLastWriteTimeUtc(filePath, timestamp.AddHours(1));
				changed = true;
			}));
		using var fixture = new Fixture(workspace.Path, path, analyzer);

		await fixture.InitializeAsync();

		Assert.True(changed);
		Assert.False(fixture.Pipeline.HasCompleteBaseline);
		Assert.Null(ReadCachedMetrics(fixture.Pipeline, path));
		Assert.Equal(ContextRootPresentation.FormatLine(workspace.Path).Length, ReadPublishedContentCharacters(fixture.Pipeline));
	}

	[AvaloniaFact]
	public async Task AtomicReplacementAfterContentRead_IsRejectedByFinalPathCheck()
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("original.txt", new string('a', 2048));
		var replacement = workspace.CreateFile("replacement.txt", "new content");
		var replaced = false;
		var analyzer = new FileContentAnalyzer((filePath, _, _, _) =>
			new CallbackReadFileStream(filePath, afterDispose: () =>
			{
				File.Replace(replacement, filePath, destinationBackupFileName: null);
				replaced = true;
			}));
		using var fixture = new Fixture(workspace.Path, path, analyzer);

		await fixture.InitializeAsync();

		Assert.True(replaced);
		Assert.False(fixture.Pipeline.HasCompleteBaseline);
		Assert.Null(ReadCachedMetrics(fixture.Pipeline, path));
		Assert.Equal(ContextRootPresentation.FormatLine(workspace.Path).Length, ReadPublishedContentCharacters(fixture.Pipeline));
	}

	[AvaloniaFact]
	public async Task CustomVersionProvider_PreservesConservativeVersionContract()
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("custom.txt", "content\n");
		var expected = await new FileContentAnalyzer().GetTextFileMetricsAsync(
			path, TestContext.Current.CancellationToken);
		var versions = new FixedVersionProvider(new MetricsFileSourceVersion(123, 456, false));
		using var fixture = new Fixture(workspace.Path, path, new FileContentAnalyzer(), versions);

		await fixture.InitializeAsync();

		Assert.True(fixture.Pipeline.HasCompleteBaseline);
		var cached = Assert.IsType<CachedMetrics>(ReadCachedMetrics(fixture.Pipeline, path));
		Assert.Equal(expected, cached.Metrics);
		Assert.Equal(versions.Version, cached.SourceVersion);
		Assert.Equal(4, versions.Captures);
		Assert.Equal(4, fixture.Io.Snapshot.FileVersionOpenCount);
	}

	[AvaloniaFact]
	public async Task CustomAnalyzer_PreservesConservativeReadContract()
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("custom.txt", "physical contents");
		var analyzer = new FixedMetricsAnalyzer(new TextFileMetrics(123, 7, 99, false, false));
		using var fixture = new Fixture(workspace.Path, path, analyzer);

		await fixture.InitializeAsync();

		Assert.True(fixture.Pipeline.HasCompleteBaseline);
		Assert.Equal(analyzer.Metrics, Assert.IsType<CachedMetrics>(ReadCachedMetrics(fixture.Pipeline, path)).Metrics);
		Assert.Equal(1, analyzer.Reads);
		Assert.Equal(4, fixture.Io.Snapshot.FileVersionOpenCount);
	}

	[AvaloniaFact]
	public async Task CancellationDuringFailedOpen_DoesNotRetryUnidentifiedResult()
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("canceled.txt", "content");
		using var cancellation = new CancellationTokenSource();
		var attempts = 0;
		var analyzer = new FileContentAnalyzer((_, _, _, _) =>
		{
			Interlocked.Increment(ref attempts);
			cancellation.Cancel();
			throw new IOException("Controlled failure after cancellation.");
		});
		using var fixture = new Fixture(workspace.Path, path, analyzer);

		await fixture.StartAsync(cancellation.Token);

		Assert.True(cancellation.IsCancellationRequested);
		Assert.Equal(1, attempts);
		Assert.False(fixture.Pipeline.HasCompleteBaseline);
		Assert.False(fixture.Pipeline.IsBackgroundActive);
		Assert.False(fixture.ViewModel.StatusBusy);
		Assert.Null(ReadCachedMetrics(fixture.Pipeline, path));
	}

	[AvaloniaFact]
	public async Task CancellationDuringContentRead_DiscardsMetricsAndClosesTheHandle()
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("canceled.txt", new string('a', 2048));
		using var cancellation = new CancellationTokenSource();
		var attempts = 0;
		var closed = false;
		var analyzer = new FileContentAnalyzer((filePath, _, _, _) =>
		{
			Interlocked.Increment(ref attempts);
			return new CallbackReadFileStream(filePath,
				afterFirstRead: cancellation.Cancel,
				afterDispose: () => closed = true);
		});
		using var fixture = new Fixture(workspace.Path, path, analyzer);

		await fixture.StartAsync(cancellation.Token);

		Assert.True(cancellation.IsCancellationRequested);
		Assert.True(closed);
		Assert.Equal(1, attempts);
		Assert.False(fixture.Pipeline.HasCompleteBaseline);
		Assert.False(fixture.Pipeline.IsBackgroundActive);
		Assert.False(fixture.ViewModel.StatusBusy);
		Assert.Null(ReadCachedMetrics(fixture.Pipeline, path));
	}

	private static string CreateSource(TemporaryDirectory workspace, StableSource source)
	{
		const string unicode = "Привет 世界 😀\r\nsecond line\n";
		switch (source)
		{
			case StableSource.Text:
				return workspace.CreateFile("source.txt", "first\r\nsecond\n");
			case StableSource.Empty:
				return workspace.CreateFile("source.txt", string.Empty);
			case StableSource.Unicode:
				return workspace.CreateFile("source.txt", unicode);
			case StableSource.Utf8Bom:
				return workspace.CreateBinaryFile("source.txt", [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(unicode)]);
			case StableSource.Utf16:
				return workspace.CreateBinaryFile("source.txt", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(unicode)]);
			case StableSource.UnknownBinary:
				return workspace.CreateBinaryFile("source.txt", [0x61, 0x00, 0x62]);
			case StableSource.KnownBinary:
				return workspace.CreateBinaryFile("source.png", [0x61, 0x62]);
			case StableSource.Estimated:
				var path = workspace.CreateFile("source.txt", new string('a', 1024));
				using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
					stream.SetLength(11 * 1024 * 1024);
				return path;
			default:
				throw new ArgumentOutOfRangeException(nameof(source));
		}
	}

	private static CachedMetrics? ReadCachedMetrics(MetricsPipeline pipeline, string path)
	{
		var entries = (IDictionary)typeof(MetricsPipeline)
			.GetField("_fileMetricsCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pipeline)!;
		var entry = entries[path];
		if (entry is null)
			return null;
		var raw = entry.GetType().GetProperty("Raw")!.GetValue(entry)!;
		var hasMetrics = Property<bool>(raw, "HasMetrics");
		var metrics = raw.GetType().GetProperty("Metrics")!.GetValue(raw)!;
		return new CachedMetrics(hasMetrics, hasMetrics
			? new TextFileMetrics(
				Property<long>(metrics, "Size"),
				Property<int>(metrics, "LineCount"),
				Property<int>(metrics, "CharCount"),
				Property<bool>(metrics, "IsEmpty"),
				Property<bool>(metrics, "IsWhitespaceOnly"),
				Property<bool>(metrics, "IsEstimated"),
				Property<int>(metrics, "CrLfPairCount"),
				Property<int>(metrics, "TrailingNewlineChars"),
				Property<int>(metrics, "TrailingNewlineLineBreaks"))
			: null, Property<MetricsFileSourceVersion>(entry, "SourceVersion"));
	}

	private static T Property<T>(object source, string name) =>
		(T)source.GetType().GetProperty(name)!.GetValue(source)!;

	private static long ReadPublishedContentCharacters(MetricsPipeline pipeline) =>
		(long)typeof(MetricsPipeline).GetField("_lastStatusContentChars", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(pipeline)!;

	public enum StableSource { Text, Empty, Unicode, Utf8Bom, Utf16, UnknownBinary, KnownBinary, Estimated }
	private sealed record CachedMetrics(bool HasMetrics, TextFileMetrics? Metrics, MetricsFileSourceVersion SourceVersion);

	private sealed class Fixture : IDisposable
	{
		private readonly BackgroundTaskRegistry _background = new();
		private readonly StatusOperationCoordinator _status;
		private readonly BuildTreeResult _tree;
		public MainWindowViewModel ViewModel { get; }
		public MetricsPipeline Pipeline { get; }
		public MetricsPipelineIoTestPoint Io { get; } = new();

		public Fixture(string rootPath, string path, IFileContentAnalyzer analyzer,
			IMetricsFileSourceVersionProvider? versions = null)
		{
			var root = new TreeNodeDescriptor("root", rootPath, true, false, "folder",
				[new TreeNodeDescriptor(Path.GetFileName(path), path, false, false, "text", [])]);
			_tree = new BuildTreeResult(root, false, false, [path]);
			var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
			ViewModel = new MainWindowViewModel(localization, new HelpContentProvider()) { IsProjectLoaded = true };
			ViewModel.TreeNodes.Add(new TreeNodeViewModel(root, parent: null, icon: null));
			_status = new StatusOperationCoordinator(ViewModel, () => false, () => ViewModel.StatusOperationCalculatingData);
			Pipeline = new MetricsPipeline(ViewModel, localization, analyzer, new TreeExportService(), _status,
				() => _tree, () => rootPath, () => new HashSet<string>([rootPath], PathComparer.Default),
				() => TreeTextFormat.Ascii, () => null, () => 1400,
				backgroundTasks: _background, fileSourceVersionProvider: versions, ioTestPoint: Io);
		}

		public async Task InitializeAsync()
		{
			await StartAsync(TestContext.Current.CancellationToken);
			var timer = Stopwatch.StartNew();
			while (_background.TrackedTaskCount != 0)
			{
				Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), "Metrics publication did not complete.");
				await Task.Delay(1, TestContext.Current.CancellationToken);
			}
			Assert.True(Pipeline.HasStatusMetricsSnapshot);
			Assert.False(Pipeline.IsBackgroundActive);
		}

		public Task StartAsync(CancellationToken cancellationToken) =>
			Pipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(_tree, cancellationToken);

		public void Dispose()
		{
			Pipeline.Dispose();
			_status.Dispose();
			_background.Dispose();
		}
	}

	private sealed class FixedVersionProvider(MetricsFileSourceVersion version) : IMetricsFileSourceVersionProvider
	{
		private int _captures;
		public MetricsFileSourceVersion Version => version;
		public int Captures => Volatile.Read(ref _captures);
		public MetricsFileSourceVersion? Capture(string path)
		{
			Interlocked.Increment(ref _captures);
			return version;
		}
	}

	private sealed class FixedMetricsAnalyzer(TextFileMetrics metrics) : IFileContentAnalyzer
	{
		private int _reads;
		public TextFileMetrics Metrics => metrics;
		public int Reads => Volatile.Read(ref _reads);
		public ValueTask<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(string path, CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _reads);
			return ValueTask.FromResult<TextFileMetrics?>(metrics);
		}
		public ValueTask<TextFileContent?> TryReadAsTextAsync(string path, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<TextFileContent?>(null);
		public ValueTask<TextFileContent?> TryReadAsTextAsync(string path, long maxSizeForFullRead, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<TextFileContent?>(null);
	}

	private sealed class CallbackReadFileStream(string path, Action? afterFirstRead = null, Action? afterDispose = null)
		: FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1)
	{
		private bool _readCallbackInvoked;
		private bool _disposeCallbackInvoked;
		public override int Read(Span<byte> buffer)
		{
			var read = base.Read(buffer);
			AfterRead();
			return read;
		}
		public override int Read(byte[] buffer, int offset, int count)
		{
			var read = base.Read(buffer, offset, count);
			AfterRead();
			return read;
		}
		protected override void Dispose(bool disposing)
		{
			base.Dispose(disposing);
			if (disposing && !_disposeCallbackInvoked)
			{
				_disposeCallbackInvoked = true;
				afterDispose?.Invoke();
			}
		}
		private void AfterRead()
		{
			if (_readCallbackInvoked)
				return;
			_readCallbackInvoked = true;
			afterFirstRead?.Invoke();
		}
	}
}
