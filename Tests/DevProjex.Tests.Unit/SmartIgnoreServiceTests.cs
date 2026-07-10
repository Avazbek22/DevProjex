namespace DevProjex.Tests.Unit;

public sealed class SmartIgnoreServiceTests
{
	// Verifies smart-ignore rules are merged and de-duplicated.
	[Fact]
	public void Build_MergesRuleResults()
	{
		var rules = new[]
		{
			new StubSmartIgnoreRule(new SmartIgnoreResult(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin" },
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "file.tmp" })),
			new StubSmartIgnoreRule(new SmartIgnoreResult(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "obj" },
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "file.tmp" }))
		};

		var service = new SmartIgnoreService(rules);

		var result = service.Build("/root");

		Assert.Contains("bin", result.FolderNames);
		Assert.Contains("obj", result.FolderNames);
		Assert.Single(result.FileNames);
	}

	// Verifies no rules yield empty ignore sets.
	[Fact]
	public void Build_ReturnsEmptyWhenNoRules()
	{
		var service = new SmartIgnoreService([]);

		var result = service.Build("/root");

		Assert.Empty(result.FolderNames);
		Assert.Empty(result.FileNames);
	}

	// Verifies case-insensitive de-duplication across rules.
	[Fact]
	public void Build_DeduplicatesCaseInsensitive()
	{
		var rules = new[]
		{
			new StubSmartIgnoreRule(new SmartIgnoreResult(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BIN" },
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Thumbs.DB" })),
			new StubSmartIgnoreRule(new SmartIgnoreResult(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin" },
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "thumbs.db" }))
		};

		var service = new SmartIgnoreService(rules);

		var result = service.Build("/root");

		Assert.Single(result.FolderNames);
		Assert.Single(result.FileNames);
	}

	[Fact]
	public void Build_IgnoresFaultyRule_AndContinuesWithHealthyRules()
	{
		var rules = new ISmartIgnoreRule[]
		{
			new ThrowingSmartIgnoreRule(),
			new StubSmartIgnoreRule(new SmartIgnoreResult(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin" },
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Thumbs.db" }))
		};

		var service = new SmartIgnoreService(rules);
		var result = service.Build("/root");

		Assert.Contains("bin", result.FolderNames);
		Assert.Contains("Thumbs.db", result.FileNames);
	}

	[Fact]
	public void HasKnownProjectMarker_DetectsDescriptorMarkerFileAndExtensionCaseInsensitively()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("custom.project", "marker");
		temp.CreateFile("service.XPROJ", "marker");
		var service = new SmartIgnoreService([
			new DescriptorOnlySmartIgnoreRule(
				markerFiles: ["CUSTOM.PROJECT"],
				markerExtensions: [".xproj"])
		]);

		Assert.True(service.HasKnownProjectMarker(temp.Path));
		Assert.True(service.IsKnownProjectMarker("custom.project", ".project"));
		Assert.True(service.IsKnownProjectMarker("service.XPROJ", ".XPROJ"));
	}

	[Fact]
	public void Build_UsesProjectRootFactsAwareRulesWithoutLegacyRootPathEvaluation()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("package.json", "{}");

		var rule = new FactsOnlySmartIgnoreRule();
		var service = new SmartIgnoreService([rule]);

		var result = service.Build(temp.Path);

		Assert.Contains("node_modules", result.FolderNames);
		Assert.Equal(1, rule.FactsEvaluateCallCount);
	}

	private sealed class ThrowingSmartIgnoreRule : ISmartIgnoreRule
	{
		public SmartIgnoreResult Evaluate(string rootPath)
		{
			throw new UnauthorizedAccessException("Access denied.");
		}
	}

	private sealed class DescriptorOnlySmartIgnoreRule(
		IEnumerable<string>? markerFiles = null,
		IEnumerable<string>? markerExtensions = null)
		: ISmartIgnoreRule, ISmartIgnoreRuleDescriptorProvider
	{
		public SmartIgnoreRuleDescriptor Descriptor { get; } = new(
			(markerFiles ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase),
			(markerExtensions ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase),
			new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			new HashSet<string>(StringComparer.OrdinalIgnoreCase));

		public SmartIgnoreResult Evaluate(string rootPath) => SmartIgnoreResult.Empty;
	}

	private sealed class FactsOnlySmartIgnoreRule : IProjectRootFactsSmartIgnoreRule
	{
		private int _factsEvaluateCallCount;

		public int FactsEvaluateCallCount => Volatile.Read(ref _factsEvaluateCallCount);

		public SmartIgnoreResult Evaluate(ProjectRootFacts rootFacts)
		{
			Interlocked.Increment(ref _factsEvaluateCallCount);
			return rootFacts.HasMarkerFile("package.json")
				? new SmartIgnoreResult(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node_modules" },
					new HashSet<string>(StringComparer.OrdinalIgnoreCase))
				: SmartIgnoreResult.Empty;
		}

		public SmartIgnoreResult Evaluate(string rootPath)
		{
			throw new InvalidOperationException("Facts-aware smart ignore rules must not use the legacy IO path.");
		}
	}
}
