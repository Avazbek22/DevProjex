using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;
using System.Text.Json;

namespace DevProjex.Tests.Unit;

public sealed class DependencyIndexSchedulingTests
{
	[Theory]
	[InlineData(1024L)]
	[InlineData(1024L * 1024)]
	public async Task SerialAndBoundedExtractionKeepIdenticalFactsEdgesCoverageAndInvalidation(long budget)
	{
		using var fixture = new TemporaryDirectory();
		var first = fixture.CreateFile("lib/first.ts", "export class Value {}\n");
		var second = fixture.CreateFile("lib/second.ts", "export class Value {}\n");
		var consumer = fixture.CreateFile("app/main.ts", "import { Value } from 'alias';\n");
		var config = fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{\"paths\":{\"alias\":[\"./lib/first.ts\"]}}}");
		var project = fixture.CreateFile("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var model = fixture.CreateFile("App/Model.cs", "namespace Demo; public class Model {}\n");
		var store = fixture.CreateFile("App/Store.cs", "namespace Demo; public class Store { Model value; }\n");
		string[] manifest = [store, second, consumer, first, model];
		string[] readable = [.. manifest, config, project];
		var limits = new DependencyFactsLimits(MaximumAccumulatedFactBytes: budget);
		using var serial = new DependencyFactsEngine(new TreeSitterDependencyFactExtractor(),
			new FileDependencyConfigurationProvider(), limits, 1);
		using var parallel = new DependencyFactsEngine(new TreeSitterDependencyFactExtractor(),
			new FileDependencyConfigurationProvider(), limits, 8, minimumPipelinedFiles: 1);

		await CompareAsync();
		await CompareAsync();
		File.WriteAllText(config, "{\"compilerOptions\":{\"paths\":{\"alias\":[\"./lib/second.ts\"]}}}");
		File.SetLastWriteTimeUtc(config, File.GetLastWriteTimeUtc(config).AddSeconds(2));
		await CompareAsync();
		File.AppendAllText(store, "public class Extra {}\n");
		await CompareAsync();
		File.Delete(second);
		await CompareAsync();

		async Task CompareAsync()
		{
			var expected = await serial.IndexAsync(fixture.Path, manifest,
				cancellationToken: TestContext.Current.CancellationToken, readableProjectFiles: readable);
			var actual = await parallel.IndexAsync(fixture.Path, manifest,
				cancellationToken: TestContext.Current.CancellationToken, readableProjectFiles: readable);
			Assert.Equal(Serialize(expected), Serialize(actual));
			Assert.Equal(expected.Metrics.ResolutionCacheHit, actual.Metrics.ResolutionCacheHit);
			Assert.Equal(expected.Metrics.AccumulatedFactBytes, actual.Metrics.AccumulatedFactBytes);
		}
	}

	private static string Serialize(DependencyIndexSnapshot index) => JsonSerializer.Serialize(new
	{
		index.ManifestGeneration,
		index.DeclarationRevision,
		index.Files,
		index.Declarations,
		index.Edges,
		index.EdgesBySource,
		index.EdgesByTarget,
		index.Coverage
	});

	[Fact]
	public async Task CompletedPrefixStartsTheNextFileWithoutWaitingForTheWholeWindow()
	{
		using var fixture = new TemporaryDirectory();
		var files = Enumerable.Range(0, 4).Select(index => fixture.CreateFile($"{index}.cs", "class Model {}"))
			.ToArray();
		using var extractor = new GatedExtractor();
		using var engine = new DependencyFactsEngine(extractor, new Configuration(), null, 2, minimumPipelinedFiles: 1);
		var indexing = engine.IndexAsync(fixture.Path, files, cancellationToken: TestContext.Current.CancellationToken);
		try
		{
			await extractor.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
			await extractor.NextStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
			Assert.False(extractor.Release.Task.IsCompleted);
		}
		finally
		{
			extractor.Release.TrySetResult();
			await indexing;
		}
		Assert.InRange(extractor.MaximumActive, 1, 2);
	}

	[Fact]
	public async Task LookAheadKeepsWorkersBusyWhenTheFirstFileIsBlocked()
	{
		using var fixture = new TemporaryDirectory();
		var files = Enumerable.Range(0, 4).Select(index => fixture.CreateFile($"{index}.cs", "class Model {}"))
			.ToArray();
		using var extractor = new GatedExtractor(blockedIndex: 0);
		using var engine = new DependencyFactsEngine(extractor, new Configuration(), null, 2, minimumPipelinedFiles: 1);
		var indexing = engine.IndexAsync(fixture.Path, files, cancellationToken: TestContext.Current.CancellationToken);
		try
		{
			await extractor.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
			await extractor.NextStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
			Assert.False(extractor.Release.Task.IsCompleted);
		}
		finally
		{
			extractor.Release.TrySetResult();
			await indexing;
		}
		Assert.InRange(extractor.MaximumActive, 1, 2);
	}

	[Fact]
	public async Task AProducerFailureCancelsAndObservesEveryOtherProducer()
	{
		using var fixture = new TemporaryDirectory();
		var files = Enumerable.Range(0, 4).Select(index => fixture.CreateFile($"{index}.cs", "class Model {}"))
			.ToArray();
		var failure = new IOException("Cannot read source.");
		using var extractor = new GatedExtractor(failure);
		using var engine = new DependencyFactsEngine(extractor, new Configuration(), null, 2, minimumPipelinedFiles: 1);

		var observed = await Assert.ThrowsAsync<IOException>(() => engine.IndexAsync(fixture.Path, files,
			cancellationToken: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

		Assert.Same(failure, observed);
		Assert.Equal(0, extractor.Active);
	}

	[Fact]
	public async Task CancellationWaitsForEveryActiveProducer()
	{
		using var fixture = new TemporaryDirectory();
		var files = Enumerable.Range(0, 4).Select(index => fixture.CreateFile($"{index}.cs", "class Model {}"))
			.ToArray();
		using var extractor = new GatedExtractor();
		using var engine = new DependencyFactsEngine(extractor, new Configuration(), null, 2, minimumPipelinedFiles: 1);
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var indexing = engine.IndexAsync(fixture.Path, files, cancellationToken: cancellation.Token);
		await extractor.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => indexing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
		Assert.Equal(0, extractor.Active);
	}

	[Fact]
	public async Task AProgressFailureDrainsProducersAndKeepsTheOriginalException()
	{
		using var fixture = new TemporaryDirectory();
		var files = Enumerable.Range(0, 4).Select(index => fixture.CreateFile($"{index}.cs", "class Model {}"))
			.ToArray();
		using var extractor = new GatedExtractor();
		using var engine = new DependencyFactsEngine(extractor, new Configuration(), null, 2, minimumPipelinedFiles: 1);
		var failure = new InvalidOperationException("Progress callback failed.");

		var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.IndexAsync(fixture.Path, files,
			new FailingProgress(failure), TestContext.Current.CancellationToken)
			.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

		Assert.Same(failure, observed);
		Assert.Equal(0, extractor.Active);
	}

	[Fact]
	public async Task WarmManifestReturnsWithoutPreparingFilesOrAllocatingPipelineResources()
	{
		using var fixture = new TemporaryDirectory();
		var files = Enumerable.Range(0, 4).Select(index => fixture.CreateFile($"{index}.cs", "class Model {}"))
			.ToArray();
		using var serialExtractor = new GatedExtractor();
		using var pipelinedExtractor = new GatedExtractor();
		serialExtractor.Release.SetResult();
		pipelinedExtractor.Release.SetResult();
		using var serial = new DependencyFactsEngine(serialExtractor, new Configuration(), null, 2, int.MaxValue);
		using var pipelined = new DependencyFactsEngine(pipelinedExtractor, new Configuration(), null, 2, 1);
		using var cold = DependencyEngineDiagnostics.BeginMeasurement();
		var expected = await serial.IndexAsync(fixture.Path, files, cancellationToken: TestContext.Current.CancellationToken);
		var actual = await pipelined.IndexAsync(fixture.Path, files, cancellationToken: TestContext.Current.CancellationToken);
		Assert.Equal(Serialize(expected), Serialize(actual));
		Assert.Equal(3, cold.Capture().ExtractionPipelineResourceCreations);
		using var warm = DependencyEngineDiagnostics.BeginMeasurement();
		for (var repeat = 0; repeat < 32; repeat++)
		{
			AssertWarmSnapshot(serial);
			AssertWarmSnapshot(pipelined);
		}

		Assert.Equal(0, warm.Capture().ExtractionPipelineResourceCreations);
		Assert.Equal(64, warm.Capture().ResolutionCacheHits);
		Assert.Equal(files.Length, serialExtractor.PrepareCount);
		Assert.Equal(files.Length, pipelinedExtractor.PrepareCount);
		Assert.Equal(files.Length, serialExtractor.ExtractCount);
		Assert.Equal(files.Length, pipelinedExtractor.ExtractCount);

		void AssertWarmSnapshot(DependencyFactsEngine engine)
		{
			var call = engine.IndexAsync(fixture.Path, files, cancellationToken: TestContext.Current.CancellationToken);
			Assert.True(call.IsCompletedSuccessfully);
			Assert.True(call.GetAwaiter().GetResult().Metrics.ResolutionCacheHit);
		}
	}

	[Fact]
	public async Task CancellationCallbackFailuresDoNotMaskProgressFailureOrSkipDraining()
	{
		using var fixture = new TemporaryDirectory();
		var files = Enumerable.Range(0, 4).Select(index => fixture.CreateFile($"{index}.cs", "class Model {}"))
			.ToArray();
		var failure = new InvalidOperationException("Progress callback failed.");
		using var extractor = new GatedExtractor(throwDuringCancellation: true);
		using var engine = new DependencyFactsEngine(extractor, new Configuration(), null, 2, minimumPipelinedFiles: 1);

		var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.IndexAsync(fixture.Path, files,
			new FailingProgress(failure), TestContext.Current.CancellationToken)
			.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

		Assert.Same(failure, observed);
		Assert.Equal(0, extractor.Active);
	}

	private sealed class FailingProgress(Exception failure) : IProgress<DependencyIndexProgress>
	{
		public void Report(DependencyIndexProgress value) => throw failure;
	}

	private sealed class GatedExtractor(Exception? failure = null, int blockedIndex = 1, bool throwDuringCancellation = false) : IDependencyFactExtractor
	{
		private int _active;
		private int _maximum;
		private int _preparations;
		private int _extractions;
		public int PrepareCount => Volatile.Read(ref _preparations);
		public int ExtractCount => Volatile.Read(ref _extractions);
		public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource NextStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public int Active => Volatile.Read(ref _active);
		public int MaximumActive => Volatile.Read(ref _maximum);
		public int ParseCount => 0;
		public int CompiledQuerySetCount => 0;

		public async ValueTask<PreparedDependencySource> PrepareAsync(string root, string path,
			DependencyResolverConfiguration configuration, DependencyFactsLimits limits, CancellationToken token,
			string? contentIdentity = null)
		{
			Interlocked.Increment(ref _preparations);
			var active = Interlocked.Increment(ref _active);
			RecordMaximumConcurrency(active);
			try
			{
				var name = Path.GetFileNameWithoutExtension(path);
				if (name == (failure is null ? blockedIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0"))
				{
					var release = Release.Task.WaitAsync(token);
					using var registration = throwDuringCancellation
						? token.Register(static () => throw new InvalidOperationException("Cancellation callback failed."))
						: default;
					Blocked.TrySetResult();
					await release;
				}
				if (failure is not null && name == "1")
				{
					await Blocked.Task.WaitAsync(token);
					throw failure;
				}
				if (name == "2") NextStarted.TrySetResult();
				return new(path, Path.GetFileName(path), "fixture", LanguageId.CSharp, name, "fixture", string.Empty);
			}
			finally { Interlocked.Decrement(ref _active); }
		}

		private void RecordMaximumConcurrency(int active)
		{
			int previous;
			do { previous = _maximum; if (previous >= active) return; }
			while (Interlocked.CompareExchange(ref _maximum, active, previous) != previous);
		}

		public FileFacts Extract(PreparedDependencySource source, DependencyFactsLimits limits)
		{
			Interlocked.Increment(ref _extractions);
			return new(source.RelativePath, source.ScopeId, source.LanguageId, source.ContentFingerprint, 0,
				DependencyFileStatus.Supported, null, false, new Dictionary<string, int>(), [], [], [], [],
				new Dictionary<string, string>(), [], new Dictionary<string, string>(), []);
		}
		public void Dispose() { }
	}

	private sealed class Configuration : IDependencyConfigurationProvider
	{
		public Task<DependencyResolverConfiguration> ReadAsync(string root, IReadOnlyList<string> files, CancellationToken token) =>
			Task.FromResult(new DependencyResolverConfiguration("fixture", [], new Dictionary<string, PackageMapDescriptor>(),
				new HashSet<string>(), new Dictionary<string, IReadOnlySet<string>>(), new HashSet<string>()));
	}
}
