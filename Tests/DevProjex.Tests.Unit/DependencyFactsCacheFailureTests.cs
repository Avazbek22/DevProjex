using DevProjex.Application.Dependencies;

namespace DevProjex.Tests.Unit;

public sealed class DependencyFactsCacheFailureTests(ITestOutputHelper output)
{
	[Theory]
	[InlineData(ExtractionFailure.NonCacheable)]
	[InlineData(ExtractionFailure.Fault)]
	[InlineData(ExtractionFailure.Cancellation)]
	public async Task FailedExtractions_DoNotRetainEvictionEntries(ExtractionFailure failure)
	{
		using var fixture = new TemporaryDirectory();
		var file = fixture.CreateFile("Source.cs", string.Empty);
		var extractor = new ControlledExtractor { Failure = failure };
		using var engine = new DependencyFactsEngine(extractor, new ConfigurationProvider());

		for (var iteration = 0; iteration < 100; iteration++)
			await AssertExtractionFailureAsync(engine, extractor, fixture.Path, file);

		output.WriteLine($"{failure}: parses={extractor.ParseCount}, state={engine.CacheState}");
		Assert.Equal(100, extractor.ParseCount);
		Assert.Equal(0, engine.CacheState.Files);
		Assert.Equal(0, engine.CacheState.FileEvictionEntries);
		Assert.Equal(0, engine.CacheState.FileBytes);
		Assert.Equal(0, engine.CacheState.ResolvedIndexes);
		Assert.Equal(0, engine.CacheState.IndexEvictionEntries);
	}

	[Theory]
	[InlineData(ExtractionFailure.NonCacheable)]
	[InlineData(ExtractionFailure.Fault)]
	[InlineData(ExtractionFailure.Cancellation)]
	public async Task FailedExtractionKey_DoesNotEvictNewerSuccessfulGeneration(ExtractionFailure failure)
	{
		using var fixture = new TemporaryDirectory();
		var files = CreateThreeFiles(fixture);
		var extractor = new ControlledExtractor { Failure = failure };
		using var engine = new DependencyFactsEngine(
			extractor,
			new ConfigurationProvider { CanCache = false },
			new DependencyFactsLimits(MaximumCachedFiles: 2));

		await AssertExtractionFailureAsync(engine, extractor, fixture.Path, files[0]);
		extractor.Failure = ExtractionFailure.None;
		await IndexAsync(engine, fixture.Path, files[1]);
		await IndexAsync(engine, fixture.Path, files[0]);
		await IndexAsync(engine, fixture.Path, files[2]);
		var recovered = await IndexAsync(engine, fixture.Path, files[0]);

		output.WriteLine($"{failure}: parses={extractor.ParseCount}, state={engine.CacheState}");
		Assert.Equal(4, extractor.ParseCount);
		Assert.Equal(0, recovered.Metrics.ParsedFiles);
		Assert.Equal(1, recovered.Metrics.ReusedFiles);
		Assert.Equal(2, engine.CacheState.Files);
		Assert.Equal(2, engine.CacheState.FileEvictionEntries);
		Assert.True(engine.CacheState.FileBytes > 0);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task FailedResolutions_DoNotRetainEvictionEntries(bool cancellation)
	{
		using var fixture = new TemporaryDirectory();
		var file = fixture.CreateFile("Source.cs", string.Empty);
		var extractor = new ControlledExtractor();
		var configuration = new ConfigurationProvider();
		using var engine = new DependencyFactsEngine(extractor, configuration);

		for (var iteration = 0; iteration < 100; iteration++)
			await AssertResolutionFailureAsync(engine, configuration, fixture.Path, file, cancellation);

		output.WriteLine($"resolution cancellation={cancellation}: parses={extractor.ParseCount}, state={engine.CacheState}");
		Assert.Equal(1, extractor.ParseCount);
		Assert.Equal(1, engine.CacheState.Files);
		Assert.Equal(1, engine.CacheState.FileEvictionEntries);
		Assert.Equal(0, engine.CacheState.ResolvedIndexes);
		Assert.Equal(0, engine.CacheState.IndexEvictionEntries);
		Assert.Equal(0, engine.CacheState.ResolvedIndexBytes);
		Assert.Equal(0, engine.CacheState.ManifestSnapshots);
		Assert.Equal(0, engine.CacheState.ManifestEvictionEntries);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task FailedResolutionKey_DoesNotEvictNewerSuccessfulGeneration(bool cancellation)
	{
		using var fixture = new TemporaryDirectory();
		var files = CreateThreeFiles(fixture);
		var extractor = new ControlledExtractor();
		var configuration = new ConfigurationProvider();
		using var engine = new DependencyFactsEngine(
			extractor, configuration, new DependencyFactsLimits(MaximumCachedIndexes: 2));

		await AssertResolutionFailureAsync(engine, configuration, fixture.Path, files[0], cancellation);
		configuration.FailResolution = false;
		await IndexAsync(engine, fixture.Path, files[1]);
		await IndexAsync(engine, fixture.Path, files[0]);
		await IndexAsync(engine, fixture.Path, files[2]);
		var recovered = await IndexAsync(engine, fixture.Path, files[0]);

		output.WriteLine($"resolution cancellation={cancellation}: cacheHit={recovered.Metrics.ResolutionCacheHit}, state={engine.CacheState}");
		Assert.True(recovered.Metrics.ResolutionCacheHit);
		Assert.Equal(3, extractor.ParseCount);
		Assert.Equal(2, engine.CacheState.ResolvedIndexes);
		Assert.Equal(2, engine.CacheState.IndexEvictionEntries);
		Assert.Equal(2, engine.CacheState.ManifestSnapshots);
		Assert.Equal(2, engine.CacheState.ManifestEvictionEntries);
	}

	[Fact]
	public async Task ConcurrentGenerations_KeepEvictionEntriesAndWeightsWithinBothBudgets()
	{
		using var fixture = new TemporaryDirectory();
		var files = Enumerable.Range(0, 32)
			.Select(index => fixture.CreateFile($"File{index:D2}.cs", string.Empty)).ToArray();
		var limits = new DependencyFactsLimits(
			MaximumCachedFiles: 4,
			MaximumCachedIndexes: 3,
			MaximumFileCacheBytes: 4096,
			MaximumIndexCacheBytes: 4096);
		using var engine = new DependencyFactsEngine(new ControlledExtractor(), new ConfigurationProvider(), limits);

		var snapshots = await Task.WhenAll(files.Select(file => IndexAsync(engine, fixture.Path, file)));

		var state = engine.CacheState;
		Assert.InRange(state.Files, 1, limits.MaximumCachedFiles);
		Assert.Equal(state.Files, state.FileEvictionEntries);
		Assert.InRange(state.FileBytes, 1, limits.MaximumFileCacheBytes);
		Assert.Equal(state.Files * DependencyFactsEngine.EstimateFileFactsBytes(Assert.Single(snapshots[0].Files)),
			state.FileBytes);
		Assert.InRange(state.ResolvedIndexes, 1, limits.MaximumCachedIndexes);
		Assert.Equal(state.ResolvedIndexes, state.IndexEvictionEntries);
		Assert.InRange(state.ResolvedIndexBytes, 1, limits.MaximumIndexCacheBytes);
		Assert.Equal(state.ResolvedIndexes * snapshots[0].Metrics.ResolvedIndexEstimatedBytes, state.ResolvedIndexBytes);
		Assert.InRange(state.ManifestSnapshots, 0, state.ResolvedIndexes);
		Assert.Equal(state.ManifestSnapshots, state.ManifestEvictionEntries);
	}

	[Fact]
	public async Task CancelledSharedExtractionWaiter_ReturnsBeforeOwnerCompletesWithoutEvictingItsFile()
	{
		using var fixture = new TemporaryDirectory();
		var file = fixture.CreateFile("Source.cs", string.Empty);
		using var waiterCancellation = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);
		var extractionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var releaseExtraction = new ManualResetEventSlim();
		var extractor = new ControlledExtractor
		{
			BeforeExtract = invocation =>
			{
				if (invocation != 1) return;
				extractionStarted.SetResult();
				if (!releaseExtraction.Wait(TimeSpan.FromSeconds(10)))
					throw new TimeoutException("The owner extraction was not released.");
			}
		};
		using var engine = new DependencyFactsEngine(extractor, new ConfigurationProvider());
		using var diagnostics = DependencyEngineDiagnostics.BeginMeasurement();
		var owner = engine.IndexAsync(fixture.Path, [file],
			cancellationToken: TestContext.Current.CancellationToken);

		try
		{
			await extractionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
			var waiter = engine.IndexAsync(fixture.Path, [file], cancellationToken: waiterCancellation.Token);
			Assert.True(SpinWait.SpinUntil(
				() => diagnostics.Capture().FileCacheHits > 0,
				TimeSpan.FromSeconds(5)));

			waiterCancellation.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
				waiter.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
			Assert.False(owner.IsCompleted);
		}
		finally
		{
			releaseExtraction.Set();
		}

		var snapshot = await owner;
		Assert.Equal(DependencyFileStatus.Supported, Assert.Single(snapshot.Files).Status);
		Assert.Equal(1, extractor.ParseCount);
		Assert.Equal(1, engine.CacheState.Files);
		var warm = await engine.IndexAsync(fixture.Path, [file],
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Equal(0, warm.Metrics.ParsedFiles);
		Assert.Equal(1, extractor.ParseCount);
	}

	[Fact]
	public async Task CancelledSharedExtraction_DoesNotCancelAnotherIndexRequest()
	{
		using var fixture = new TemporaryDirectory();
		var file = fixture.CreateFile("Source.cs", string.Empty);
		using var ownerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);
		var extractionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var releaseExtraction = new ManualResetEventSlim();
		var extractor = new ControlledExtractor
		{
			Failure = ExtractionFailure.Cancellation,
			CancelExtraction = ownerCancellation.Cancel,
			BeforeExtract = invocation =>
			{
				if (invocation != 1) return;
				extractionStarted.SetResult();
				if (!releaseExtraction.Wait(TimeSpan.FromSeconds(5)))
					throw new TimeoutException("The shared extraction was not released.");
			}
		};
		using var engine = new DependencyFactsEngine(extractor, new ConfigurationProvider());
		using var diagnostics = DependencyEngineDiagnostics.BeginMeasurement();
		var cancelled = engine.IndexAsync(fixture.Path, [file], cancellationToken: ownerCancellation.Token);

		try
		{
			await extractionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
			var surviving = engine.IndexAsync(fixture.Path, [file],
				cancellationToken: TestContext.Current.CancellationToken);
			Assert.True(SpinWait.SpinUntil(
				() => diagnostics.Capture().FileCacheHits > 0,
				TimeSpan.FromSeconds(5)));
			releaseExtraction.Set();

			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
			var snapshot = await surviving;
			Assert.Equal(DependencyFileStatus.Supported, Assert.Single(snapshot.Files).Status);
			Assert.Equal(2, extractor.ParseCount);
		}
		finally
		{
			releaseExtraction.Set();
		}
	}

	[Fact]
	public async Task CancelledSharedResolution_DoesNotCancelAnotherIndexRequest()
	{
		using var fixture = new TemporaryDirectory();
		var file = fixture.CreateFile("Source.cs", string.Empty);
		using var ownerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);
		var resolutionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var releaseResolution = new ManualResetEventSlim();
		var resolutionInvocation = 0;
		var configuration = new ConfigurationProvider
		{
			BeforeResolve = () =>
			{
				if (Interlocked.Increment(ref resolutionInvocation) != 1)
					return;
				resolutionStarted.SetResult();
				if (!releaseResolution.Wait(TimeSpan.FromSeconds(5)))
					throw new TimeoutException("The shared resolution was not released.");
				ownerCancellation.Cancel();
			}
		};
		using var engine = new DependencyFactsEngine(new ControlledExtractor(), configuration);
		using var diagnostics = DependencyEngineDiagnostics.BeginMeasurement();
		var cancelled = Task.Run(() => engine.IndexAsync(fixture.Path, [file],
			cancellationToken: ownerCancellation.Token));

		try
		{
			await resolutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
			var surviving = Task.Run(() => engine.IndexAsync(fixture.Path, [file],
				cancellationToken: TestContext.Current.CancellationToken));
			Assert.True(SpinWait.SpinUntil(
				() => diagnostics.Capture().IndexCacheJoins > 0,
				TimeSpan.FromSeconds(5)));
			releaseResolution.Set();

			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
			var snapshot = await surviving;
			Assert.Equal(DependencyFileStatus.Supported, Assert.Single(snapshot.Files).Status);
			Assert.False(snapshot.Metrics.ResolutionCacheHit);
			Assert.Equal(1, snapshot.Metrics.ReresolvedFiles);
			Assert.Equal(1, engine.CacheState.ResolvedIndexes);
			Assert.Equal(1, engine.CacheState.IndexEvictionEntries);
			Assert.True(engine.CacheState.ResolvedIndexBytes > 0);
			Assert.True((await engine.IndexAsync(fixture.Path, [file],
				cancellationToken: TestContext.Current.CancellationToken)).Metrics.ResolutionCacheHit);
		}
		finally
		{
			releaseResolution.Set();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
		}
	}

	[Fact]
	public async Task EvictedCompletion_CannotReleaseReplacementEntryOrItsWeight()
	{
		using var fixture = new TemporaryDirectory();
		var files = CreateThreeFiles(fixture);
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var release = new ManualResetEventSlim();
		var extractor = new ControlledExtractor
		{
			Failure = ExtractionFailure.NonCacheable,
			BeforeExtract = invocation =>
			{
				if (invocation != 1) return;
				started.SetResult();
				if (!release.Wait(TimeSpan.FromSeconds(5)))
					throw new TimeoutException("The stale extraction was not released.");
			}
		};
		using var engine = new DependencyFactsEngine(extractor,
			new ConfigurationProvider { CanCache = false }, new DependencyFactsLimits(MaximumCachedFiles: 1));
		var stale = IndexAsync(engine, fixture.Path, files[0]);
		try
		{
			await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
			extractor.Failure = ExtractionFailure.None;
			await IndexAsync(engine, fixture.Path, files[1]);
			await IndexAsync(engine, fixture.Path, files[0]);
			var replacementState = engine.CacheState;
			release.Set();
			Assert.Equal(DependencyFileStatus.ExtractionFailed, Assert.Single((await stale).Files).Status);
			var repeated = await IndexAsync(engine, fixture.Path, files[0]);

			Assert.Equal(replacementState, engine.CacheState);
			Assert.Equal(1, replacementState.Files);
			Assert.Equal(1, replacementState.FileEvictionEntries);
			Assert.True(replacementState.FileBytes > 0);
			Assert.Equal(0, repeated.Metrics.ParsedFiles);
			Assert.Equal(3, extractor.ParseCount);
		}
		finally
		{
			release.Set();
			await stale;
		}
	}

	[Fact]
	public async Task ZeroByteBudgets_ReleaseEntriesWeightsAndManifestSnapshots()
	{
		using var fixture = new TemporaryDirectory();
		var file = fixture.CreateFile("Source.cs", string.Empty);
		var extractor = new ControlledExtractor();
		using var engine = new DependencyFactsEngine(extractor, new ConfigurationProvider(),
			new DependencyFactsLimits(MaximumFileCacheBytes: 0, MaximumIndexCacheBytes: 0));

		await IndexAsync(engine, fixture.Path, file);
		var repeated = await IndexAsync(engine, fixture.Path, file);

		Assert.Equal(2, extractor.ParseCount);
		Assert.False(repeated.Metrics.ResolutionCacheHit);
		Assert.Equal(default, engine.CacheState);
	}

	private static string[] CreateThreeFiles(TemporaryDirectory fixture) =>
		[fixture.CreateFile("A.cs", string.Empty), fixture.CreateFile("B.cs", string.Empty),
		 fixture.CreateFile("C.cs", string.Empty)];

	private static Task<DependencyIndexSnapshot> IndexAsync(DependencyFactsEngine engine, string root, string file) =>
		engine.IndexAsync(root, [file], cancellationToken: TestContext.Current.CancellationToken);

	private static async Task AssertExtractionFailureAsync(
		DependencyFactsEngine engine, ControlledExtractor extractor, string root, string file)
	{
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		extractor.CancelExtraction = cancellation.Cancel;
		Task<DependencyIndexSnapshot> Request() => engine.IndexAsync(root, [file], cancellationToken: cancellation.Token);
		switch (extractor.Failure)
		{
			case ExtractionFailure.NonCacheable:
				Assert.Equal(DependencyFileStatus.ExtractionFailed, Assert.Single((await Request()).Files).Status);
				break;
			case ExtractionFailure.Fault:
				await Assert.ThrowsAsync<IOException>(Request);
				break;
			case ExtractionFailure.Cancellation:
				await Assert.ThrowsAnyAsync<OperationCanceledException>(Request);
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(extractor));
		}
	}

	private static async Task AssertResolutionFailureAsync(
		DependencyFactsEngine engine, ConfigurationProvider configuration, string root, string file, bool cancellation)
	{
		if (!cancellation)
		{
			configuration.FailResolution = true;
			await Assert.ThrowsAsync<ArgumentException>(() => IndexAsync(engine, root, file));
			return;
		}
		using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var resolutionReached = false;
		configuration.BeforeResolve = () =>
		{
			var state = engine.CacheState;
			Assert.Equal(1, state.ResolvedIndexes);
			Assert.Equal(1, state.IndexEvictionEntries);
			Assert.Equal(0, state.ResolvedIndexBytes);
			resolutionReached = true;
			source.Cancel();
		};
		try
		{
			var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.IndexAsync(
				root, [file], cancellationToken: source.Token));
			Assert.True(resolutionReached);
			Assert.Equal(source.Token, exception.CancellationToken);
		}
		finally
		{
			configuration.BeforeResolve = null;
		}
	}

	public enum ExtractionFailure { None, NonCacheable, Fault, Cancellation }

	private sealed class ObservedScopes(IReadOnlyList<DependencyScopeDescriptor> scopes, Action beforeResolve)
		: IReadOnlyList<DependencyScopeDescriptor>
	{
		private Action? _beforeResolve = beforeResolve;
		public int Count => scopes.Count;
		public DependencyScopeDescriptor this[int index] => scopes[index];

		public IEnumerator<DependencyScopeDescriptor> GetEnumerator()
		{
			Interlocked.Exchange(ref _beforeResolve, null)?.Invoke();
			return scopes.GetEnumerator();
		}

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class ControlledExtractor : IDependencyFactExtractor
	{
		private int _parseCount;
		public int ParseCount => Volatile.Read(ref _parseCount);
		public int CompiledQuerySetCount => 0;
		public ExtractionFailure Failure { get; set; }
		public Action? CancelExtraction { get; set; }
		public Action<int>? BeforeExtract { get; init; }

		public ValueTask<PreparedDependencySource> PrepareAsync(
			string sourceRoot, string fullPath, DependencyResolverConfiguration configuration,
			DependencyFactsLimits limits, CancellationToken cancellationToken, string? contentIdentity = null)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var relative = Path.GetRelativePath(sourceRoot, fullPath).Replace('\\', '/');
			return ValueTask.FromResult(new PreparedDependencySource(
				fullPath, relative, "fixture", LanguageId.CSharp, relative, "synthetic:v1", string.Empty));
		}

		public FileFacts Extract(PreparedDependencySource source, DependencyFactsLimits limits) =>
			Extract(source, limits, CancellationToken.None);

		public FileFacts Extract(PreparedDependencySource source, DependencyFactsLimits limits, CancellationToken cancellationToken)
		{
			var failure = Failure;
			var invocation = Interlocked.Increment(ref _parseCount);
			BeforeExtract?.Invoke(invocation);
			if (failure == ExtractionFailure.Fault)
				throw new IOException("Synthetic extraction failure.");
			if (failure == ExtractionFailure.Cancellation)
			{
				CancelExtraction!();
				cancellationToken.ThrowIfCancellationRequested();
			}
			var canCache = failure != ExtractionFailure.NonCacheable;
			return new FileFacts(source.RelativePath, source.ScopeId, source.LanguageId, source.ContentFingerprint, 0,
				canCache ? DependencyFileStatus.Supported : DependencyFileStatus.ExtractionFailed,
				canCache ? null : "Transient synthetic failure", false, new Dictionary<string, int>(),
				[], [], [], [], new Dictionary<string, string>(), [], new Dictionary<string, string>(), [])
			{ CanCache = canCache };
		}

		public void Dispose() { }
	}

	private sealed class ConfigurationProvider : IDependencyConfigurationProvider
	{
		public bool CanCache { get; init; } = true;
		public bool FailResolution { get; set; }
		public Action? BeforeResolve { get; set; }

		public Task<DependencyResolverConfiguration> ReadAsync(
			string sourceRoot, IReadOnlyList<string> manifestFiles, CancellationToken cancellationToken)
		{
			var scope = new DependencyScopeDescriptor("fixture", sourceRoot, LanguageId.CSharp,
				[], null, false, new Dictionary<string, IReadOnlyList<string>>(), null, new HashSet<string>(), [], true);
			IReadOnlyList<DependencyScopeDescriptor> scopes = FailResolution ? [scope, scope] : [scope];
			if (BeforeResolve is { } beforeResolve)
				scopes = new ObservedScopes(scopes, beforeResolve);
			return Task.FromResult(new DependencyResolverConfiguration("fixture",
				scopes, new Dictionary<string, PackageMapDescriptor>(),
				new HashSet<string>(), new Dictionary<string, IReadOnlySet<string>>(), new HashSet<string>())
			{ CanCache = CanCache });
		}
	}
}
