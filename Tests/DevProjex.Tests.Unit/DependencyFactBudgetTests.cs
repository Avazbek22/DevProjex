using DevProjex.Application.Dependencies;

namespace DevProjex.Tests.Unit;

public sealed class DependencyFactBudgetTests
{
	[Fact]
	public async Task AccumulatedBudgetAdmitsFactsInCanonicalOrderAndReportsLimitedFiles()
	{
		using var fixture = new TemporaryDirectory();
		var first = fixture.CreateFile("A.cs", string.Empty);
		var second = fixture.CreateFile("B.cs", string.Empty);
		var firstFacts = BudgetExtractor.CreateFacts("A.cs");
		var oneFileBudget = DependencyFactsEngine.EstimateFileFactsBytes(firstFacts);
		using var engine = new DependencyFactsEngine(
			new BudgetExtractor(),
			new ConfigurationProvider(fixture.Path),
			new DependencyFactsLimits(MaximumAccumulatedFactBytes: oneFileBudget));

		var result = await engine.IndexAsync(
			fixture.Path,
			[second, first],
			cancellationToken: TestContext.Current.CancellationToken);

		var admitted = Assert.Single(result.Files, static file => !file.FactBudgetLimited);
		Assert.Equal("A.cs", admitted.Path);
		var limited = Assert.Single(result.Files, static file => file.FactBudgetLimited);
		Assert.Equal("B.cs", limited.Path);
		Assert.Equal(DependencyFileStatus.ExtractionFailed, limited.Status);
		Assert.Equal(DependencyFactsEngine.AccumulatedFactBudgetReason, limited.StatusReason);
		Assert.Empty(limited.Declarations);
		Assert.Empty(limited.Imports);
		Assert.Empty(limited.References);
		Assert.Equal(["B.cs"], result.Coverage.FactBudgetLimitedFiles);
		Assert.Equal(oneFileBudget, result.Metrics.AccumulatedFactBytes);
		Assert.Equal(oneFileBudget, result.Metrics.MaximumAccumulatedFactBytes);
		Assert.Equal(1, result.Metrics.FactBudgetLimitedFiles);
	}

	[Fact]
	public async Task RejectedResolutionWorkReusesExtractedFactCollections()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("Source.cs", string.Empty);
		var extractor = new ResolutionLimitExtractor();
		using var engine = new DependencyFactsEngine(
			extractor,
			new ConfigurationProvider(fixture.Path),
			new DependencyFactsLimits(MaximumEdgesPerFile: 1));

		var result = await engine.IndexAsync(
			fixture.Path,
			[source],
			cancellationToken: TestContext.Current.CancellationToken);

		var resolved = Assert.Single(result.Files);
		Assert.Same(extractor.Facts!.Imports, resolved.Imports);
		Assert.Same(extractor.Facts.References, resolved.References);
		var edge = Assert.Single(result.Edges);
		Assert.Equal("<limit>", edge.Reference);
		Assert.Contains("edge limit exceeded", edge.Reasons);
	}

	private sealed class BudgetExtractor : IDependencyFactExtractor
	{
		public int ParseCount { get; private set; }
		public int CompiledQuerySetCount => 0;

		public ValueTask<PreparedDependencySource> PrepareAsync(
			string sourceRoot,
			string fullPath,
			DependencyResolverConfiguration configuration,
			DependencyFactsLimits limits,
			CancellationToken cancellationToken,
			string? contentIdentity = null)
		{
			var relative = Path.GetRelativePath(sourceRoot, fullPath).Replace('\\', '/');
			return ValueTask.FromResult(new PreparedDependencySource(
				fullPath,
				relative,
				"scope",
				LanguageId.CSharp,
				relative,
				"fixture",
				string.Empty));
		}

		public FileFacts Extract(PreparedDependencySource source, DependencyFactsLimits limits)
		{
			ParseCount++;
			return CreateFacts(source.RelativePath);
		}

		public static FileFacts CreateFacts(string path)
		{
			var name = Path.GetFileNameWithoutExtension(path);
			return new FileFacts(
				path,
				"scope",
				LanguageId.CSharp,
				path,
				0,
				DependencyFileStatus.Supported,
				null,
				false,
				new Dictionary<string, int>(),
				[new DeclarationFact(
					new SymbolIdentity("scope", LanguageId.CSharp, SymbolKind.Class, name, 0),
					[new SourceSite(path, 1, name)])],
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

	private sealed class ResolutionLimitExtractor : IDependencyFactExtractor
	{
		public int ParseCount { get; private set; }
		public int CompiledQuerySetCount => 0;
		public FileFacts? Facts { get; private set; }

		public ValueTask<PreparedDependencySource> PrepareAsync(
			string sourceRoot,
			string fullPath,
			DependencyResolverConfiguration configuration,
			DependencyFactsLimits limits,
			CancellationToken cancellationToken,
			string? contentIdentity = null) =>
			ValueTask.FromResult(new PreparedDependencySource(
				fullPath,
				"Source.cs",
				"scope",
				LanguageId.CSharp,
				"source",
				"fixture",
				string.Empty));

		public FileFacts Extract(PreparedDependencySource source, DependencyFactsLimits limits)
		{
			ParseCount++;
			Facts = new FileFacts(
				source.RelativePath,
				source.ScopeId,
				source.LanguageId,
				source.ContentFingerprint,
				0,
				DependencyFileStatus.Supported,
				null,
				false,
				new Dictionary<string, int>(),
				[],
				[],
				[
					new ReferenceFact(EvidenceLayer.TypeReference, "A", 0, "type", new SourceSite("Source.cs", 1, "A")),
					new ReferenceFact(EvidenceLayer.TypeReference, "B", 0, "type", new SourceSite("Source.cs", 2, "B"))
				],
				[],
				new Dictionary<string, string>(),
				[],
				new Dictionary<string, string>(),
				[]);
			return Facts;
		}

		public void Dispose()
		{
		}
	}

	private sealed class ConfigurationProvider(string root) : IDependencyConfigurationProvider
	{
		public Task<DependencyResolverConfiguration> ReadAsync(
			string sourceRoot,
			IReadOnlyList<string> manifestFiles,
			CancellationToken cancellationToken) => Task.FromResult(new DependencyResolverConfiguration(
			"fixture",
			[new DependencyScopeDescriptor(
				"scope",
				root,
				LanguageId.CSharp,
				[],
				null,
				false,
				new Dictionary<string, IReadOnlyList<string>>(),
				null,
				new HashSet<string>(),
				[],
				true)],
			new Dictionary<string, PackageMapDescriptor>(),
			new HashSet<string>(),
			new Dictionary<string, IReadOnlySet<string>>(),
			new HashSet<string>()));
	}
}
