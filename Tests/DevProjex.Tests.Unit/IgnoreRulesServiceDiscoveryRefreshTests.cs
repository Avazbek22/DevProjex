using DevProjex.Application.Diagnostics;

namespace DevProjex.Tests.Unit;

public sealed class IgnoreRulesServiceDiscoveryRefreshTests
{
	[Fact]
	public void RefreshDiscoveryCaches_UnchangedSource_ReusesContentValidatedMatcher()
	{
		using var workspace = new TemporaryDirectory();
		workspace.CreateFile(".gitignore", "*.cache\n");
		workspace.CreateFile("artifact.cache", "ignored");
		var service = CreateService();
		var before = service.Build(workspace.Path, [IgnoreOptionId.UseGitIgnore]);

		RefreshDiscovery(service, workspace.Path);
		using var measurement = IgnorePipelineDiagnostics.BeginMeasurement();
		var after = service.Build(workspace.Path, [IgnoreOptionId.UseGitIgnore]);
		var diagnostics = measurement.Capture();

		Assert.True(after.IsGitIgnored(Path.Combine(workspace.Path, "artifact.cache"), false, "artifact.cache"));
		Assert.Equal(2, diagnostics.GitIgnoreSourceReadRequests);
		Assert.Equal(16, diagnostics.GitIgnoreSourceBytes);
		Assert.True(diagnostics.RootFactsBuilds > 0);
		Assert.Same(before.GitIgnoreMatcher, after.GitIgnoreMatcher);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void RefreshDiscoveryCaches_RewrittenSource_UsesNewContent(bool preserveMetadata)
	{
		using var workspace = new TemporaryDirectory();
		var sourcePath = workspace.CreateFile(".gitignore", "old/\n");
		workspace.CreateFile("old/file.txt", "old");
		workspace.CreateFile("new/file.txt", "new");
		var originalTimestamp = File.GetLastWriteTimeUtc(sourcePath);
		var service = CreateService();
		var before = service.Build(workspace.Path, [IgnoreOptionId.UseGitIgnore]);

		File.WriteAllText(sourcePath, preserveMetadata ? "new/\n" : "new/\n# modified\n");
		if (preserveMetadata)
			File.SetLastWriteTimeUtc(sourcePath, originalTimestamp);
		RefreshDiscovery(service, workspace.Path);
		var after = service.Build(workspace.Path, [IgnoreOptionId.UseGitIgnore]);

		Assert.True(before.IsGitIgnored(Path.Combine(workspace.Path, "old"), true, "old"));
		Assert.False(after.IsGitIgnored(Path.Combine(workspace.Path, "old"), true, "old"));
		Assert.True(after.IsGitIgnored(Path.Combine(workspace.Path, "new"), true, "new"));
		Assert.NotSame(before.GitIgnoreMatcher, after.GitIgnoreMatcher);
	}

	[Fact]
	public void RefreshDiscoveryCaches_NestedSourceAddedAndRemoved_RefreshesDiscovery()
	{
		using var workspace = new TemporaryDirectory();
		workspace.CreateFile("project/package.json", "{}");
		var artifactPath = workspace.CreateFile("project/artifact.cache", "ignored");
		var service = CreateService();
		var before = service.Build(workspace.Path, [IgnoreOptionId.UseGitIgnore], ["project"]);
		Assert.False(before.IsGitIgnored(artifactPath, false, "artifact.cache"));

		var sourcePath = workspace.CreateFile("project/.gitignore", "*.cache\n");
		RefreshDiscovery(service, workspace.Path);
		var added = service.Build(workspace.Path, [IgnoreOptionId.UseGitIgnore], ["project"]);
		Assert.Single(added.ScopedGitIgnoreMatchers);
		Assert.True(added.IsGitIgnored(artifactPath, false, "artifact.cache"));

		File.Delete(sourcePath);
		RefreshDiscovery(service, workspace.Path);
		var removed = service.Build(workspace.Path, [IgnoreOptionId.UseGitIgnore], ["project"]);
		Assert.Empty(removed.ScopedGitIgnoreMatchers);
		Assert.False(removed.IsGitIgnored(artifactPath, false, "artifact.cache"));
	}

	[Fact]
	public void RefreshDiscoveryCaches_RefreshesSmartIgnoreEvaluation()
	{
		using var workspace = new TemporaryDirectory();
		workspace.CreateFile("project/package.json", "{}");
		var rule = new CountingSmartIgnoreRule();
		var service = new IgnoreRulesService(new SmartIgnoreService([rule]));
		_ = service.Build(workspace.Path, [IgnoreOptionId.SmartIgnore], ["project"]);
		Assert.Equal(1, rule.EvaluationCount);

		RefreshDiscovery(service, workspace.Path);
		_ = service.Build(workspace.Path, [IgnoreOptionId.SmartIgnore], ["project"]);
		Assert.Equal(2, rule.EvaluationCount);
	}

	[Fact]
	public void RefreshDiscoveryCaches_ChangedComparisonSemantics_RebuildsMatcher()
	{
		using var workspace = new TemporaryDirectory();
		workspace.CreateFile(".gitignore", "UPPER.cache\n");
		var artifactPath = workspace.CreateFile("upper.cache", "ignored when folded");
		var resolver = new MutableSemanticsResolver();
		var service = CreateService(resolver);
		var before = service.Build(workspace.Path, [IgnoreOptionId.UseGitIgnore]);
		Assert.False(before.IsGitIgnored(artifactPath, false, "upper.cache"));

		resolver.Next = new GitPathComparisonSemantics(IgnoreCase: true, NormalizeUnicode: false);
		RefreshDiscovery(service, workspace.Path);
		var after = service.Build(workspace.Path, [IgnoreOptionId.UseGitIgnore]);

		Assert.Equal(1, resolver.InvalidationCount);
		Assert.NotSame(before.GitIgnoreMatcher, after.GitIgnoreMatcher);
		Assert.True(after.IsGitIgnored(artifactPath, false, "upper.cache"));
	}

	[Fact]
	public void RefreshDiscoveryCaches_UnavailableComparisonSemantics_DoesNotReuseMatcher()
	{
		using var workspace = new TemporaryDirectory();
		workspace.CreateFile(".gitignore", "*.cache\n");
		workspace.CreateFile("artifact.cache", "ignored");
		var resolver = new MutableSemanticsResolver();
		var service = CreateService(resolver);
		var before = service.Build(workspace.Path, [IgnoreOptionId.UseGitIgnore]);
		Assert.NotSame(GitIgnoreMatcher.Empty, before.GitIgnoreMatcher);

		resolver.Next = new GitPathComparisonSemantics(false, false, IsAuthoritative: false);
		RefreshDiscovery(service, workspace.Path);
		var after = service.Build(workspace.Path, [IgnoreOptionId.UseGitIgnore]);

		Assert.Equal(1, resolver.InvalidationCount);
		Assert.True(after.UseGitIgnore);
		Assert.Same(GitIgnoreMatcher.Empty, after.GitIgnoreMatcher);
		Assert.Empty(after.ScopedGitIgnoreMatchers);
	}

	[Fact]
	public void RefreshDiscoveryCaches_CanceledBuild_DoesNotPreventSubsequentBuild()
	{
		using var workspace = new TemporaryDirectory();
		workspace.CreateFile(".gitignore", "*.cache\n");
		var artifactPath = workspace.CreateFile("artifact.cache", "ignored");
		var service = CreateService();
		_ = service.Build(workspace.Path, [IgnoreOptionId.UseGitIgnore]);
		RefreshDiscovery(service, workspace.Path);
		var canceledToken = new CancellationToken(canceled: true);

		var exception = Assert.Throws<OperationCanceledException>(() => service.BuildWithCancellation(
			workspace.Path, [IgnoreOptionId.UseGitIgnore], null, canceledToken));
		Assert.Equal(canceledToken, exception.CancellationToken);
		var after = service.Build(workspace.Path, [IgnoreOptionId.UseGitIgnore]);
		Assert.True(after.IsGitIgnored(artifactPath, false, "artifact.cache"));
	}

	private static void RefreshDiscovery(IgnoreRulesService service, string rootPath) =>
		service.RefreshDiscoveryCaches(rootPath);

	private static IgnoreRulesService CreateService(IGitPathComparisonSemanticsResolver? resolver = null) =>
		new(new SmartIgnoreService([]), pathComparisonSemanticsResolver: resolver);

	private sealed class MutableSemanticsResolver : IGitPathComparisonSemanticsResolver
	{
		private GitPathComparisonSemantics? _cached;
		public GitPathComparisonSemantics Next { get; set; } = new(false, false);
		public int InvalidationCount { get; private set; }

		public GitPathComparisonSemantics Resolve(string scopeRootPath) => _cached ??= Next;

		public void Invalidate(string rootPath)
		{
			InvalidationCount++;
			_cached = null;
		}
	}

	private sealed class CountingSmartIgnoreRule : ISmartIgnoreRule
	{
		private int _evaluationCount;
		public int EvaluationCount => Volatile.Read(ref _evaluationCount);

		public SmartIgnoreResult Evaluate(string rootPath)
		{
			Interlocked.Increment(ref _evaluationCount);
			return new SmartIgnoreResult(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node_modules" },
				new HashSet<string>(StringComparer.OrdinalIgnoreCase));
		}
	}
}
