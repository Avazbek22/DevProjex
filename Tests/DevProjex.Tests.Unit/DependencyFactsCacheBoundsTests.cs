using DevProjex.Application.Dependencies;

namespace DevProjex.Tests.Unit;

public sealed class DependencyFactsCacheBoundsTests
{
	[Fact]
	public async Task ManifestSnapshotEviction_RemainsBoundedAcrossRepeatedGenerations()
	{
		using var fixture = new TemporaryDirectory();
		var files = Enumerable.Range(0, 17)
			.Select(index => fixture.CreateFile($"File{index:D2}.cs", $"file-{index}"))
			.ToArray();
		using var engine = new DependencyFactsEngine(
			new SyntheticFactExtractor(),
			new EmptyConfigurationProvider(),
			new DependencyFactsLimits(MaximumCachedIndexes: 16));

		for (var iteration = 0; iteration < 10_000; iteration++)
		{
			await engine.IndexAsync(
				fixture.Path,
				[files[iteration % files.Length]],
				cancellationToken: TestContext.Current.CancellationToken);
		}

		Assert.InRange(engine.CacheState.ManifestSnapshots, 0, 16);
		Assert.Equal(engine.CacheState.ManifestSnapshots, engine.CacheState.ManifestEvictionEntries);
	}

	[Fact]
	public async Task ResolvedIndexEstimate_IncludesDeclarationsAndTheirSites()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("Declarations.cs", "declarations");
		using var engine = new DependencyFactsEngine(
			new SyntheticFactExtractor(declarationsPerFile: 200),
			new EmptyConfigurationProvider(),
			new DependencyFactsLimits(MaximumIndexCacheBytes: 4 * 1024));

		_ = await engine.IndexAsync(
			fixture.Path,
			[source],
			cancellationToken: TestContext.Current.CancellationToken);
		var repeated = await engine.IndexAsync(
			fixture.Path,
			[source],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.False(repeated.Metrics.ResolutionCacheHit);
		Assert.Equal(0, engine.CacheState.ResolvedIndexes);
		Assert.Equal(0, engine.CacheState.ResolvedIndexBytes);
	}

	private sealed class SyntheticFactExtractor(int declarationsPerFile = 0) : IDependencyFactExtractor
	{
		private int _parseCount;
		public int ParseCount => Volatile.Read(ref _parseCount);
		public int CompiledQuerySetCount => 0;

		public ValueTask<PreparedDependencySource> PrepareAsync(
			string sourceRoot,
			string fullPath,
			DependencyResolverConfiguration configuration,
			DependencyFactsLimits limits,
			CancellationToken cancellationToken,
			string? contentIdentity = null)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var relative = Path.GetRelativePath(sourceRoot, fullPath).Replace('\\', '/');
			return ValueTask.FromResult(new PreparedDependencySource(
				fullPath,
				relative,
				"fixture",
				LanguageId.CSharp,
				relative,
				"synthetic:v1",
				string.Empty));
		}

		public FileFacts Extract(PreparedDependencySource source, DependencyFactsLimits limits)
		{
			Interlocked.Increment(ref _parseCount);
			var declarations = Enumerable.Range(0, declarationsPerFile)
				.Select(index => new DeclarationFact(
					new SymbolIdentity(
						source.ScopeId,
						source.LanguageId,
						SymbolKind.Class,
						$"Fixture.Type{index:D4}",
						0),
					[new SourceSite(source.RelativePath, index + 1, $"Type{index:D4}")]))
				.ToArray();
			return new FileFacts(
				source.RelativePath,
				source.ScopeId,
				source.LanguageId,
				source.ContentFingerprint,
				0,
				DependencyFileStatus.Supported,
				null,
				false,
				new Dictionary<string, int>(),
				declarations,
				[],
				[],
				[],
				new Dictionary<string, string>(),
				[],
				new Dictionary<string, string>(),
				[]);
		}

		public void Dispose()
		{
		}
	}

	private sealed class EmptyConfigurationProvider : IDependencyConfigurationProvider
	{
		public Task<DependencyResolverConfiguration> ReadAsync(
			string sourceRoot,
			IReadOnlyList<string> manifestFiles,
			CancellationToken cancellationToken) => Task.FromResult(new DependencyResolverConfiguration(
			"fixture",
			[],
			new Dictionary<string, PackageMapDescriptor>(),
			new HashSet<string>(),
			new Dictionary<string, IReadOnlySet<string>>(),
			new HashSet<string>()));
	}
}
