namespace DevProjex.Tests.Unit;

public sealed class IgnoreRulesServiceCacheAndLimitsTests
{
	public static IEnumerable<object[]> EquivalentRootSelectionCases()
	{
		yield return [new[] { "proj-git", "proj-no-git" }];
		yield return [new[] { "proj-no-git", "proj-git" }];
		yield return [new[] { "proj-git", "proj-no-git", "proj-git" }];
		yield return [new[] { "proj-no-git", "proj-git", "proj-no-git" }];
		yield return [new[] { "proj-git", "proj-git", "proj-no-git", "proj-no-git" }];
		yield return [new[] { "proj-no-git", "proj-no-git", "proj-git", "proj-git" }];
	}

	[Theory]
	[MemberData(nameof(EquivalentRootSelectionCases))]
	public void Build_EquivalentSelectedRootSets_ProduceEquivalentRules(string[] selectedRoots)
	{
		using var temp = new TemporaryDirectory();
		SeedMixedWorkspace(temp);

		var service = CreateServiceWithSmartIgnore(["node_modules"]);
		var rules = service.Build(
			temp.Path,
			[IgnoreOptionId.UseGitIgnore, IgnoreOptionId.SmartIgnore],
			selectedRoots);

		Assert.True(rules.UseGitIgnore);
		Assert.True(rules.UseSmartIgnore);
		Assert.Single(rules.ScopedGitIgnoreMatchers);

		var gitIgnoredFile = Path.Combine(temp.Path, "proj-git", "bin", "out.dll");
		Assert.True(rules.IsGitIgnored(gitIgnoredFile, isDirectory: false, "out.dll"));

		var noGitPath = Path.Combine(temp.Path, "proj-no-git", "src");
		Assert.True(rules.ShouldApplySmartIgnore(noGitPath));
	}

	[Fact]
	public void GetIgnoreOptionsAvailability_CacheKeyDependsOnSelectedRoots()
	{
		using var temp = new TemporaryDirectory();
		SeedMixedWorkspace(temp);

		var service = CreateServiceWithSmartIgnore(["node_modules"]);

		var noGitOnly = service.GetIgnoreOptionsAvailability(temp.Path, ["proj-no-git"]);
		Assert.False(noGitOnly.IncludeGitIgnore);
		Assert.True(noGitOnly.IncludeSmartIgnore);

		var gitOnly = service.GetIgnoreOptionsAvailability(temp.Path, ["proj-git"]);
		Assert.True(gitOnly.IncludeGitIgnore);
		Assert.True(gitOnly.IncludeSmartIgnore);
	}

	[Fact]
	public void GetIgnoreOptionsAvailability_PythonProjectWithOnlyIdeaGitIgnore_DoesNotExposeGitIgnore()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("requirements.txt", "pytest\n");
		temp.CreateFile("main.py", "print('ok')\n");
		temp.CreateFile("__pycache__/main.pyc", "binary");
		temp.CreateFile(".idea/.gitignore", "# JetBrains internal ignore file\n");
		temp.CreateFile(".idea/workspace.xml", "<project />\n");

		var service = new IgnoreRulesService(new SmartIgnoreService([new PythonArtifactsIgnoreRule()]));
		var availability = service.GetIgnoreOptionsAvailability(temp.Path, []);
		var rules = service.Build(
			temp.Path,
			[IgnoreOptionId.SmartIgnore, IgnoreOptionId.UseGitIgnore],
			selectedRootFolders: []);

		Assert.False(availability.IncludeGitIgnore);
		Assert.True(availability.IncludeSmartIgnore);
		// Availability controls whether a dynamic checkbox is shown; it never rewrites
		// an already selected Git mode into None.
		Assert.True(rules.UseGitIgnore);
		Assert.True(rules.UseSmartIgnore);
	}

	[Fact]
	public void GetIgnoreOptionsAvailability_UsesScopeCacheWithinTtl_ThenExplicitInvalidationRefreshes()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("proj-no-git/package.json", "{}");

		var service = CreateServiceWithSmartIgnore([]);

		var before = service.GetIgnoreOptionsAvailability(temp.Path, ["proj-no-git"]);
		Assert.False(before.IncludeGitIgnore);

		temp.CreateFile("proj-no-git/.gitignore", "bin/");

		// Interactive toggles keep the hot scope cache until an external refresh boundary invalidates it.
		var withinTtl = service.GetIgnoreOptionsAvailability(temp.Path, ["proj-no-git"]);
		Assert.False(withinTtl.IncludeGitIgnore);

		service.InvalidateCaches(temp.Path);

		var afterInvalidation = service.GetIgnoreOptionsAvailability(temp.Path, ["proj-no-git"]);

		Assert.True(afterInvalidation.IncludeGitIgnore);
	}

	[Fact]
	public void Build_GitIgnoreRewriteWithSameLengthAndTimestamp_InvalidatesMatcher()
	{
		using var temp = new TemporaryDirectory();
		var gitIgnorePath = temp.CreateFile(".gitignore", "old/\n");
		temp.CreateFile("old/file.txt", "old");
		temp.CreateFile("new/file.txt", "new");
		var service = CreateServiceWithSmartIgnore([]);
		var originalTimestamp = File.GetLastWriteTimeUtc(gitIgnorePath);

		var before = service.Build(temp.Path, [IgnoreOptionId.UseGitIgnore], ["old", "new"]);
		Assert.True(before.IsGitIgnored(Path.Combine(temp.Path, "old"), isDirectory: true, "old"));
		Assert.False(before.IsGitIgnored(Path.Combine(temp.Path, "new"), isDirectory: true, "new"));

		File.WriteAllText(gitIgnorePath, "new/\n");
		File.SetLastWriteTimeUtc(gitIgnorePath, originalTimestamp);
		Assert.Equal(5, new FileInfo(gitIgnorePath).Length);

		var after = service.Build(temp.Path, [IgnoreOptionId.UseGitIgnore], ["old", "new"]);

		Assert.False(after.IsGitIgnored(Path.Combine(temp.Path, "old"), isDirectory: true, "old"));
		Assert.True(after.IsGitIgnored(Path.Combine(temp.Path, "new"), isDirectory: true, "new"));
	}

	[Fact]
	public void Build_NestedDiscoveryStopsAfterDepthTwo()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("parent/.gitignore", "# parent");
		temp.CreateFile("parent/level1/level2/.gitignore", "*.l2");
		temp.CreateFile("parent/level1/level2/file.l2", "depth2");
		temp.CreateFile("parent/level1/level2/level3/.gitignore", "*.l3");
		temp.CreateFile("parent/level1/level2/level3/file.l3", "depth3");

		var service = CreateServiceWithSmartIgnore([]);
		var rules = service.Build(temp.Path, [IgnoreOptionId.UseGitIgnore], ["parent"]);

		var depth2File = Path.Combine(temp.Path, "parent", "level1", "level2", "file.l2");
		var depth3File = Path.Combine(temp.Path, "parent", "level1", "level2", "level3", "file.l3");

		Assert.True(rules.IsGitIgnored(depth2File, isDirectory: false, "file.l2"));
		Assert.False(rules.IsGitIgnored(depth3File, isDirectory: false, "file.l3"));
	}

	[Theory]
	[InlineData(260)]
	[InlineData(300)]
	[InlineData(420)]
	public void Build_NestedDiscoveryRespectsMaxDirectoryProbeLimit(int childDirectoryCount)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("parent/.gitignore", "# parent scope");

		for (var i = 0; i < childDirectoryCount; i++)
		{
			var child = $"parent/child-{i:D4}";
			temp.CreateFile($"{child}/.gitignore", "*.tmp");
			temp.CreateFile($"{child}/artifact.tmp", "x");
		}

		var service = CreateServiceWithSmartIgnore([]);
		var rules = service.Build(temp.Path, [IgnoreOptionId.UseGitIgnore], ["parent"]);

		// 1 parent + up to 256 discovered descendants.
		var expectedMax = 257;
		Assert.True(rules.ScopedGitIgnoreMatchers.Count <= expectedMax);
		Assert.True(rules.ScopedGitIgnoreMatchers.Count < childDirectoryCount + 1);
	}

	[Fact]
	public void GetIgnoreOptionsAvailability_SmartIgnoreStaysDisabled_WhenArtifactsOnlyNestedAndNoMarkers()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("workspace/src/node_modules/lib/index.js", "x");

		var service = CreateServiceWithSmartIgnore(["node_modules"]);
		var availability = service.GetIgnoreOptionsAvailability(temp.Path, ["workspace"]);

		Assert.False(availability.IncludeGitIgnore);
		Assert.False(availability.IncludeSmartIgnore);
	}

	[Fact]
	public void GetIgnoreOptionsAvailability_SmartIgnoreEnabled_WhenTopLevelArtifactFolderExists()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("workspace/node_modules/lib/index.js", "x");

		var service = CreateServiceWithSmartIgnore(["node_modules"]);
		var availability = service.GetIgnoreOptionsAvailability(temp.Path, ["workspace"]);

		Assert.False(availability.IncludeGitIgnore);
		Assert.True(availability.IncludeSmartIgnore);
	}

	[Fact]
	public void Build_AfterAvailabilityProbe_ReusesCachedSmartIgnoreResultForSameScope()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("workspace/node_modules/lib/index.js", "x");

		var rule = new CountingSmartIgnoreRule(["node_modules"]);
		var service = new IgnoreRulesService(new SmartIgnoreService([rule]));

		var availability = service.GetIgnoreOptionsAvailability(temp.Path, ["workspace"]);
		var rules = service.Build(temp.Path, [IgnoreOptionId.SmartIgnore], ["workspace"]);

		Assert.True(availability.IncludeSmartIgnore);
		Assert.True(rules.UseSmartIgnore);
		Assert.True(rules.IsSmartIgnoredDirectory(
			Path.Combine(temp.Path, "workspace", "node_modules"),
			"node_modules"));
		Assert.Equal(1, rule.EvaluateCallCount);
	}

	[Fact]
	public void Build_RepeatedInteractiveStatesReuseScopeWork_ExternalInvalidationRecomputesOnce()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("workspace/package.json", "{}");
		temp.CreateFile("workspace/node_modules/lib/index.js", "x");
		var rule = new CountingSmartIgnoreRule(["node_modules"]);
		var service = new IgnoreRulesService(new SmartIgnoreService([rule]));

		for (var iteration = 0; iteration < 64; iteration++)
		{
			var selectedOptions = iteration % 2 == 0
				? new[] { IgnoreOptionId.SmartIgnore }
				: Array.Empty<IgnoreOptionId>();
			_ = service.GetIgnoreOptionsAvailability(temp.Path, ["workspace"]);
			_ = service.Build(temp.Path, selectedOptions, ["workspace"]);
		}

		Assert.Equal(1, rule.EvaluateCallCount);

		service.InvalidateCaches(temp.Path);
		_ = service.Build(temp.Path, [IgnoreOptionId.SmartIgnore], ["workspace"]);

		Assert.Equal(2, rule.EvaluateCallCount);
	}

	[Fact]
	public void RevalidateCaches_RepeatedUnchangedRefreshesReuseScopeAndSmartIgnoreWork()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("workspace/package.json", "{}");
		temp.CreateFile("workspace/node_modules/lib/index.js", "x");
		var rule = new CountingSmartIgnoreRule(["node_modules"]);
		var service = new IgnoreRulesService(new SmartIgnoreService([rule]));

		_ = service.Build(temp.Path, [IgnoreOptionId.SmartIgnore], ["workspace"]);
		for (var refresh = 0; refresh < 32; refresh++)
		{
			Assert.True(service.RevalidateCaches(temp.Path, TestContext.Current.CancellationToken));
			_ = service.Build(temp.Path, [IgnoreOptionId.SmartIgnore], ["workspace"]);
		}

		Assert.Equal(1, rule.EvaluateCallCount);
	}

	[Fact]
	public void RevalidateCaches_SameMetadataGitIgnoreRewriteInvalidatesRuleCache()
	{
		using var temp = new TemporaryDirectory();
		var gitIgnorePath = temp.CreateFile(".gitignore", "old/\n");
		temp.CreateFile("old/file.txt", "old");
		temp.CreateFile("new/file.txt", "new");
		var service = CreateServiceWithSmartIgnore([]);
		var originalTimestamp = File.GetLastWriteTimeUtc(gitIgnorePath);
		var before = service.Build(temp.Path, [IgnoreOptionId.UseGitIgnore], ["old", "new"]);

		File.WriteAllText(gitIgnorePath, "new/\n");
		File.SetLastWriteTimeUtc(gitIgnorePath, originalTimestamp);
		var canReuseRuleCaches = service.RevalidateCaches(temp.Path, TestContext.Current.CancellationToken);
		var after = service.Build(temp.Path, [IgnoreOptionId.UseGitIgnore], ["old", "new"]);

		Assert.False(canReuseRuleCaches);
		Assert.True(before.IsGitIgnored(Path.Combine(temp.Path, "old"), true, "old"));
		Assert.False(before.IsGitIgnored(Path.Combine(temp.Path, "new"), true, "new"));
		Assert.False(after.IsGitIgnored(Path.Combine(temp.Path, "old"), true, "old"));
		Assert.True(after.IsGitIgnored(Path.Combine(temp.Path, "new"), true, "new"));
	}

	[Fact]
	public void RevalidateCaches_ChangedGitIgnoreBeyondMatcherCacheLimitInvalidatesReadyRules()
	{
		using var temp = new TemporaryDirectory();
		var selectedRoots = new List<string>();
		for (var scopeIndex = 0; scopeIndex < 80; scopeIndex++)
		{
			var scope = $"scope-{scopeIndex:D2}";
			selectedRoots.Add(scope);
			temp.CreateFile($"{scope}/.gitignore", "*.old\n");
			temp.CreateFile($"{scope}/probe.old", "old");
			temp.CreateFile($"{scope}/probe.new", "new");
		}

		var service = CreateServiceWithSmartIgnore([]);
		var before = service.Build(temp.Path, [IgnoreOptionId.UseGitIgnore], selectedRoots);
		var changedGitIgnore = Path.Combine(temp.Path, "scope-00", ".gitignore");
		var originalTimestamp = File.GetLastWriteTimeUtc(changedGitIgnore);
		File.WriteAllText(changedGitIgnore, "*.new\n");
		File.SetLastWriteTimeUtc(changedGitIgnore, originalTimestamp);

		var canReuseRuleCaches = service.RevalidateCaches(temp.Path, TestContext.Current.CancellationToken);
		var after = service.Build(temp.Path, [IgnoreOptionId.UseGitIgnore], selectedRoots);

		Assert.True(before.IsGitIgnored(Path.Combine(temp.Path, "scope-00", "probe.old"), false, "probe.old"));
		Assert.False(canReuseRuleCaches);
		Assert.False(after.IsGitIgnored(Path.Combine(temp.Path, "scope-00", "probe.old"), false, "probe.old"));
		Assert.True(after.IsGitIgnored(Path.Combine(temp.Path, "scope-00", "probe.new"), false, "probe.new"));
	}

	[Fact]
	public void Build_CaseDistinctScopes_DoNotLeakGitIgnoreRulesOnCaseSensitiveFileSystems()
	{
		if (OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		temp.CreateFile("App/.gitignore", "*.upper\n");
		temp.CreateFile("App/source.upper", "ignored");
		temp.CreateFile("App/source.lower", "visible");
		temp.CreateFile("app/.gitignore", "*.lower\n");
		temp.CreateFile("app/source.upper", "visible");
		temp.CreateFile("app/source.lower", "ignored");

		// A default case-insensitive macOS volume cannot create both roots; its native
		// path semantics are already covered by the case-insensitive matcher matrix.
		if (Directory.EnumerateDirectories(temp.Path)
			.Select(Path.GetFileName)
			.Distinct(StringComparer.Ordinal)
			.Count() < 2)
		{
			return;
		}

		var service = CreateServiceWithSmartIgnore([]);
		var rules = service.Build(temp.Path, [IgnoreOptionId.UseGitIgnore], ["App", "app"]);

		Assert.Equal(2, rules.ScopedGitIgnoreMatchers.Count);
		Assert.True(rules.IsGitIgnored(Path.Combine(temp.Path, "App", "source.upper"), false, "source.upper"));
		Assert.False(rules.IsGitIgnored(Path.Combine(temp.Path, "App", "source.lower"), false, "source.lower"));
		Assert.False(rules.IsGitIgnored(Path.Combine(temp.Path, "app", "source.upper"), false, "source.upper"));
		Assert.True(rules.IsGitIgnored(Path.Combine(temp.Path, "app", "source.lower"), false, "source.lower"));
	}

	[Fact]
	public void Build_IgnoresReparsePointRootCandidatesDuringScopeDiscovery()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("real/package.json", "{}");
		temp.CreateFile("real/node_modules/lib/index.js", "x");

		if (!TryCreateDirectorySymlink(Path.Combine(temp.Path, "linked"), Path.Combine(temp.Path, "real")))
			return;

		var service = CreateServiceWithSmartIgnore(["node_modules"]);
		var availability = service.GetIgnoreOptionsAvailability(temp.Path, ["linked"]);
		var rules = service.Build(temp.Path, [IgnoreOptionId.SmartIgnore], ["linked"]);

		Assert.False(availability.IncludeSmartIgnore);
		Assert.True(rules.UseSmartIgnore);
	}

	[Fact]
	public void Build_MissingExplicitRootCandidates_DoNotFallbackToWholeWorkspaceScope()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("deleted/package.json", "{}");
		Directory.Delete(Path.Combine(temp.Path, "deleted"), recursive: true);
		temp.CreateFile("sibling/node_modules/lib/index.js", "x");

		var service = CreateServiceWithSmartIgnore(["node_modules"]);
		var availability = service.GetIgnoreOptionsAvailability(temp.Path, ["deleted"]);
		var rules = service.Build(temp.Path, [IgnoreOptionId.SmartIgnore], ["deleted"]);

		Assert.False(availability.IncludeGitIgnore);
		Assert.False(availability.IncludeSmartIgnore);
		Assert.True(rules.UseSmartIgnore);
	}

	private static void SeedMixedWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile("proj-git/.gitignore", "bin/");
		temp.CreateFile("proj-git/App.csproj", "<Project />");
		temp.CreateFile("proj-git/bin/out.dll", "dll");
		temp.CreateFile("proj-git/src/code.cs", "class App {}");

		temp.CreateFile("proj-no-git/package.json", "{}");
		temp.CreateFile("proj-no-git/node_modules/lib/index.js", "x");
		temp.CreateFile("proj-no-git/src/main.ts", "export {}");
	}

	private static IgnoreRulesService CreateServiceWithSmartIgnore(
		IReadOnlyCollection<string> smartFolders,
		ProjectRootFactsProvider? rootFactsProvider = null)
	{
		var smartService = new SmartIgnoreService([
			new FixedSmartIgnoreRule(smartFolders)
		], rootFactsProvider);

		return new IgnoreRulesService(smartService);
	}

	private sealed class FixedSmartIgnoreRule(IReadOnlyCollection<string> folders) : ISmartIgnoreRule
	{
		public SmartIgnoreResult Evaluate(string rootPath)
		{
			return new SmartIgnoreResult(
				new HashSet<string>(folders, StringComparer.OrdinalIgnoreCase),
				new HashSet<string>(StringComparer.OrdinalIgnoreCase));
		}
	}

	private sealed class CountingSmartIgnoreRule(IReadOnlyCollection<string> folders) : ISmartIgnoreRule
	{
		private int _evaluateCallCount;

		public int EvaluateCallCount => Volatile.Read(ref _evaluateCallCount);

		public SmartIgnoreResult Evaluate(string rootPath)
		{
			Interlocked.Increment(ref _evaluateCallCount);
			return new SmartIgnoreResult(
				new HashSet<string>(folders, StringComparer.OrdinalIgnoreCase),
				new HashSet<string>(StringComparer.OrdinalIgnoreCase));
		}
	}

	private static bool TryCreateDirectorySymlink(string linkPath, string targetPath)
	{
		try
		{
			Directory.CreateSymbolicLink(linkPath, targetPath);
			return Directory.Exists(linkPath) &&
			       File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
			return false;
		}
	}

}
