using DevProjex.Application.Dependencies;
using DevProjex.Application.Ranking;

namespace DevProjex.Tests.Unit;

public sealed class DependencyOptimizationWorkTests
{
	[Fact]
	public async Task Diagnostics_CountCanonicalWorkAndCacheReuseWithoutChangingResults()
	{
		using var fixture = new TemporaryDirectory();
		var declarationPath = fixture.CreateFile("Model.cs", string.Empty);
		var referencePath = fixture.CreateFile("Consumer.cs", string.Empty);
		using var engine = new DependencyFactsEngine(new Extractor(declarationPath, referencePath), new Configuration(fixture.Path));
		using var measurement = DependencyEngineDiagnostics.BeginMeasurement();

		var cold = await engine.IndexAsync(fixture.Path, [referencePath, declarationPath],
			cancellationToken: TestContext.Current.CancellationToken);
		var warm = await engine.IndexAsync(fixture.Path, [declarationPath, referencePath],
			cancellationToken: TestContext.Current.CancellationToken);
		ImportanceRankingService.CalculatePageRank(
			[(declarationPath, "Model.cs"), (referencePath, "Consumer.cs")], warm,
			TestContext.Current.CancellationToken);
		var work = measurement.Capture();

		Assert.Equal(cold.Edges, warm.Edges);
		Assert.Equal(4, work.PathNormalizations);
		Assert.Equal(2, work.ManifestSorts);
		Assert.Equal(2, work.DictionaryBuilds);
		Assert.Equal(0, work.FileFactsClones);
		Assert.Equal(1, work.GraphBuilds);
		Assert.True(work.ResolverCandidateProbes >= 1);
		Assert.Equal(1, work.ResolutionCacheHits);
	}

	private sealed class Extractor(string declarationPath, string referencePath) : IDependencyFactExtractor
	{
		public int ParseCount { get; private set; }
		public int CompiledQuerySetCount => 0;

		public ValueTask<PreparedDependencySource> PrepareAsync(string sourceRoot, string fullPath,
			DependencyResolverConfiguration configuration, DependencyFactsLimits limits,
			CancellationToken cancellationToken, string? contentIdentity = null)
		{
			var relative = Path.GetRelativePath(sourceRoot, fullPath).Replace('\\', '/');
			return ValueTask.FromResult(new PreparedDependencySource(fullPath, relative, "scope",
				LanguageId.CSharp, relative, "test", string.Empty));
		}

		public FileFacts Extract(PreparedDependencySource source, DependencyFactsLimits limits)
		{
			ParseCount++;
			var declaration = source.FullPath == declarationPath
				? new[] { new DeclarationFact(new SymbolIdentity("scope", LanguageId.CSharp, SymbolKind.Class,
					"Models.Model", 0), [new SourceSite("Model.cs", 1, "Model")]) { ContainingNamespace = "Models" } }
				: [];
			var references = source.FullPath == referencePath
				? new[] { new ReferenceFact(EvidenceLayer.TypeReference, "Model", 0, "type",
					new SourceSite("Consumer.cs", 1, "Model")) { ContainingNamespace = "Models" } }
				: [];
			return new FileFacts(source.RelativePath, "scope", LanguageId.CSharp, source.ContentFingerprint, 0,
				DependencyFileStatus.Supported, null, false, new Dictionary<string, int>(), declaration, [], references,
				[], new Dictionary<string, string>(), [], new Dictionary<string, string>(), []);
		}

		public void Dispose() { }
	}

	private sealed class Configuration(string root) : IDependencyConfigurationProvider
	{
		public Task<DependencyResolverConfiguration> ReadAsync(string sourceRoot, IReadOnlyList<string> manifestFiles,
			CancellationToken cancellationToken) => Task.FromResult(new DependencyResolverConfiguration("test",
			[new DependencyScopeDescriptor("scope", root, LanguageId.CSharp, [], null, false,
				new Dictionary<string, IReadOnlyList<string>>(), null, new HashSet<string>(), [], true)],
			new Dictionary<string, PackageMapDescriptor>(), new HashSet<string>(),
			new Dictionary<string, IReadOnlySet<string>>(), new HashSet<string>()));
	}
}
