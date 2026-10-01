using DevProjex.Application.Dependencies;

namespace DevProjex.Tests.Unit;

public sealed class DependencyPhysicalProbeCacheBoundsTests
{
	[Fact]
	public async Task ExcessiveUnselectedPathProbesDoNotRetainAResolvedIndex()
	{
		using var project = new TemporaryDirectory();
		var source = project.CreateFile("main.ts", string.Empty);
		var extractor = new ManyUnselectedImportsExtractor();
		using var engine = new DependencyFactsEngine(extractor, new TypeScriptConfiguration());

		var first = await engine.IndexAsync(
			project.Path,
			[source],
			cancellationToken: TestContext.Current.CancellationToken);
		var second = await engine.IndexAsync(
			project.Path,
			[source],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.False(first.Metrics.ResolutionCacheHit);
		Assert.False(second.Metrics.ResolutionCacheHit);
		Assert.Equal(0, second.Metrics.ParsedFiles);
		Assert.Equal(1, extractor.ParseCount);
		Assert.Equal(0, engine.CacheState.ResolvedIndexes);
		Assert.Equal(0, engine.CacheState.ManifestSnapshots);
	}

	private sealed class ManyUnselectedImportsExtractor : IDependencyFactExtractor
	{
		public int ParseCount { get; private set; }
		public int CompiledQuerySetCount => 0;

		public ValueTask<PreparedDependencySource> PrepareAsync(
			string sourceRoot,
			string fullPath,
			DependencyResolverConfiguration configuration,
			DependencyFactsLimits limits,
			CancellationToken cancellationToken,
			string? contentIdentity = null) =>
			ValueTask.FromResult(new PreparedDependencySource(
				fullPath, "main.ts", "scope", LanguageId.TypeScript, "same", "synthetic:v1", string.Empty));

		public FileFacts Extract(PreparedDependencySource source, DependencyFactsLimits limits)
		{
			ParseCount++;
			var imports = Enumerable.Range(0, 4_100)
				.Select(index => new ImportFact(
					$"alias{index}", null, null, false, 0,
					new SourceSite(source.RelativePath, index + 1, "import")))
				.ToArray();
			return new FileFacts(
				source.RelativePath, source.ScopeId, source.LanguageId, source.ContentFingerprint,
				0, DependencyFileStatus.Supported, null, false,
				new Dictionary<string, int>(), [], imports, [], [],
				new Dictionary<string, string>(), [], new Dictionary<string, string>(), []);
		}

		public void Dispose() { }
	}

	private sealed class TypeScriptConfiguration : IDependencyConfigurationProvider
	{
		public Task<DependencyResolverConfiguration> ReadAsync(
			string sourceRoot,
			IReadOnlyList<string> manifestFiles,
			CancellationToken cancellationToken) => Task.FromResult(
			new DependencyResolverConfiguration(
				"synthetic",
				[new DependencyScopeDescriptor(
					"scope", sourceRoot, LanguageId.TypeScript, [], "bundler", false,
					new Dictionary<string, IReadOnlyList<string>> { ["alias*"] = ["missing/*"] },
					null, new HashSet<string>(), [], true)],
				new Dictionary<string, PackageMapDescriptor>(),
				new HashSet<string>(),
				new Dictionary<string, IReadOnlySet<string>>(),
				new HashSet<string>()));
	}
}
