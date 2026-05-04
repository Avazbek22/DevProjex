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
		Assert.False(gitOnly.IncludeSmartIgnore);
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
		Assert.False(rules.UseGitIgnore);
		Assert.True(rules.UseSmartIgnore);
	}

	[Fact]
	public void GetIgnoreOptionsAvailability_UsesScopeCacheWithinTtl_ThenRefreshes()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("proj-no-git/package.json", "{}");

		var service = CreateServiceWithSmartIgnore([]);

		var before = service.GetIgnoreOptionsAvailability(temp.Path, ["proj-no-git"]);
		Assert.False(before.IncludeGitIgnore);

		temp.CreateFile("proj-no-git/.gitignore", "bin/");

		// Must still read from scope cache before TTL expires.
		var withinTtl = service.GetIgnoreOptionsAvailability(temp.Path, ["proj-no-git"]);
		Assert.False(withinTtl.IncludeGitIgnore);

		ExpireScopeCacheEntry(service, temp.Path, ["proj-no-git"]);

		var afterTtl = service.GetIgnoreOptionsAvailability(temp.Path, ["proj-no-git"]);

		Assert.True(afterTtl.IncludeGitIgnore);
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
		Assert.False(rules.UseSmartIgnore);
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
		Assert.False(rules.UseSmartIgnore);
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

	private static IgnoreRulesService CreateServiceWithSmartIgnore(IReadOnlyCollection<string> smartFolders)
	{
		var smartService = new SmartIgnoreService([
			new FixedSmartIgnoreRule(smartFolders)
		]);

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

	private static void ExpireScopeCacheEntry(
		IgnoreRulesService service,
		string rootPath,
		IReadOnlyCollection<string> selectedRootFolders)
	{
		var discoveryField = typeof(IgnoreRulesService).GetField(
			"_projectScopeDiscovery",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(discoveryField);

		var discovery = discoveryField.GetValue(service);
		Assert.NotNull(discovery);

		var discoveryType = discovery.GetType();
		var buildScopeCacheKey = discoveryType.GetMethod(
			"BuildScopeCacheKey",
			BindingFlags.Static | BindingFlags.NonPublic);
		Assert.NotNull(buildScopeCacheKey);

		var cacheKey = (string?)buildScopeCacheKey.Invoke(
			null,
			[
				Path.GetFullPath(rootPath),
				selectedRootFolders
			]);
		Assert.False(string.IsNullOrWhiteSpace(cacheKey));

		var scopeCacheField = discoveryType.GetField(
			"_scopeCache",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(scopeCacheField);

		var scopeCache = scopeCacheField.GetValue(discovery);
		Assert.NotNull(scopeCache);

		var dictionaryType = scopeCache.GetType();
		var tryGetValueMethod = dictionaryType.GetMethod("TryGetValue");
		Assert.NotNull(tryGetValueMethod);

		var arguments = new object?[] { cacheKey, null };
		var found = (bool)tryGetValueMethod.Invoke(scopeCache, arguments)!;
		Assert.True(found);

		var existingEntry = arguments[1];
		Assert.NotNull(existingEntry);

		var entryType = existingEntry.GetType();
		var contextProperty = entryType.GetProperty("Context");
		Assert.NotNull(contextProperty);
		var context = contextProperty.GetValue(existingEntry);
		Assert.NotNull(context);

		var constructor = entryType.GetConstructor([typeof(DateTime), context.GetType()]);
		Assert.NotNull(constructor);

		var expiredEntry = constructor.Invoke([
			DateTime.UtcNow.AddSeconds(-30),
			context
		]);

		var indexer = dictionaryType.GetProperty("Item");
		Assert.NotNull(indexer);
		indexer.SetValue(scopeCache, expiredEntry, [cacheKey]);
	}
}
