using DevProjex.Application.Dependencies;

namespace DevProjex.Tests.Unit;

public sealed class DependencyFactsManifestGenerationTests(ITestOutputHelper output)
{
	[Fact]
	public async Task EvictedSuccessfulResolution_CannotPublishOverReplacementManifestSnapshot()
	{
		using var fixture = new TemporaryDirectory();
		var originalFile = fixture.CreateFile("Original.cs", string.Empty);
		var evictionFile = fixture.CreateFile("Eviction.cs", string.Empty);
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var release = new ManualResetEventSlim();
		var configuration = new ConfigurationProvider(() =>
		{
			started.SetResult();
			if (!release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))
				throw new TimeoutException("The retired resolution was not released.");
		});
		var extractor = new SyntheticExtractor();
		using var engine = new DependencyFactsEngine(extractor, configuration,
			new DependencyFactsLimits(MaximumCachedIndexes: 1));
		Task<DependencyIndexSnapshot> Request(string file, DependencyManifestContentIdentities? identities = null) =>
			engine.IndexAsync(fixture.Path, [file], cancellationToken: TestContext.Current.CancellationToken,
				contentIdentities: identities);
		var retiredRequest = Task.Run(() => Request(originalFile), TestContext.Current.CancellationToken);
		try
		{
			await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
			Assert.Equal(1, engine.CacheState.ResolvedIndexes);
			Assert.Equal(0, engine.CacheState.ResolvedIndexBytes);
			await Request(evictionFile);
			var replacement = await Request(originalFile);
			var replacementState = engine.CacheState;
			Assert.False(replacement.Metrics.ResolutionCacheHit);
			Assert.Same(replacement.Files, (await Request(originalFile)).Files);
			Assert.Equal(1, replacementState.ResolvedIndexes);
			Assert.Equal(1, replacementState.IndexEvictionEntries);
			Assert.Equal(1, replacementState.ManifestSnapshots);
			Assert.Equal(1, replacementState.ManifestEvictionEntries);
			Assert.Equal(replacement.Metrics.ResolvedIndexEstimatedBytes, replacementState.ResolvedIndexBytes);

			release.Set();
			var retired = await retiredRequest;
			var manifestHit = await Request(originalFile);
			Assert.NotEmpty(replacement.Edges);
			AssertSameContent(replacement, retired);
			AssertSameContent(replacement, manifestHit);
			Assert.NotSame(replacement.Files, retired.Files);
			Assert.True(manifestHit.Metrics.ResolutionCacheHit);
			Assert.Equal(replacementState, engine.CacheState);

			// An identity mismatch bypasses only the manifest snapshot, exposing the live resolved entry.
			var indexHit = await Request(originalFile,
				new DependencyManifestContentIdentities(new Dictionary<string, string>
				{
					[originalFile] = "same-content:explicit-identity"
				}));
			Assert.True(indexHit.Metrics.ResolutionCacheHit);
			Assert.Same(replacement.Files, indexHit.Files);
			AssertSameContent(replacement, indexHit);
			Assert.Equal(2, extractor.ParseCount);
			Assert.Equal(replacementState, engine.CacheState);
			output.WriteLine($"manifestUsesRetiredGraph={ReferenceEquals(retired.Files, manifestHit.Files)}; " +
				$"indexUsesReplacementGraph={ReferenceEquals(replacement.Files, indexHit.Files)}; " +
				$"retainedState={engine.CacheState}");
			Assert.Same(replacement.Files, manifestHit.Files);
			Assert.Same(replacement.FileByPath, manifestHit.FileByPath);
		}
		finally
		{
			release.Set();
			await retiredRequest;
		}
	}

	private static void AssertSameContent(DependencyIndexSnapshot expected, DependencyIndexSnapshot actual)
	{
		Assert.Equal(expected.SourceRoot, actual.SourceRoot);
		Assert.Equal(expected.ManifestGeneration, actual.ManifestGeneration);
		Assert.Equal(expected.DeclarationRevision, actual.DeclarationRevision);
		Assert.Equivalent(expected.Files, actual.Files, strict: true);
		Assert.Equivalent(expected.Declarations, actual.Declarations, strict: true);
		Assert.Equivalent(expected.Edges, actual.Edges, strict: true);
		Assert.Equivalent(expected.EdgesBySource, actual.EdgesBySource, strict: true);
		Assert.Equivalent(expected.EdgesByTarget, actual.EdgesByTarget, strict: true);
		Assert.Equivalent(expected.FileByPath, actual.FileByPath, strict: true);
		Assert.Equivalent(expected.Coverage, actual.Coverage, strict: true);
	}

	private sealed class SyntheticExtractor : IDependencyFactExtractor
	{
		private int _parseCount;
		public int ParseCount => Volatile.Read(ref _parseCount);
		public int CompiledQuerySetCount => 0;

		public ValueTask<PreparedDependencySource> PrepareAsync(
			string sourceRoot, string fullPath, DependencyResolverConfiguration configuration,
			DependencyFactsLimits limits, CancellationToken cancellationToken, string? contentIdentity = null)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var relative = Path.GetRelativePath(sourceRoot, fullPath).Replace('\\', '/');
			return ValueTask.FromResult(new PreparedDependencySource(
				fullPath, relative, "fixture", LanguageId.CSharp, relative, "synthetic:v1", string.Empty));
		}

		public FileFacts Extract(PreparedDependencySource source, DependencyFactsLimits limits)
		{
			Interlocked.Increment(ref _parseCount);
			return new FileFacts(source.RelativePath, source.ScopeId, source.LanguageId, source.ContentFingerprint,
				0, DependencyFileStatus.Supported, null, false, new Dictionary<string, int>(), [], [],
				[new ReferenceFact(EvidenceLayer.TypeReference, "Missing", 0, "type",
					new SourceSite(source.RelativePath, 1, "Missing"))], [],
				new Dictionary<string, string>(), [], new Dictionary<string, string>(), []);
		}

		public void Dispose() { }
	}

	private sealed class ConfigurationProvider(Action firstResolution) : IDependencyConfigurationProvider
	{
		private int _readCount;

		public Task<DependencyResolverConfiguration> ReadAsync(
			string sourceRoot, IReadOnlyList<string> manifestFiles, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var scope = new DependencyScopeDescriptor("fixture", sourceRoot, LanguageId.CSharp,
				[], null, false, new Dictionary<string, IReadOnlyList<string>>(), null, new HashSet<string>(), [], true);
			IReadOnlyList<DependencyScopeDescriptor> scopes = [scope];
			if (Interlocked.Increment(ref _readCount) == 1)
				scopes = new ObservedScopes(scopes, firstResolution);
			return Task.FromResult(new DependencyResolverConfiguration("fixture", scopes,
				new Dictionary<string, PackageMapDescriptor>(), new HashSet<string>(),
				new Dictionary<string, IReadOnlySet<string>>(), new HashSet<string>()));
		}
	}

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
}
