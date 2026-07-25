using System.Collections;

namespace DevProjex.Tests.Unit;

public sealed class IgnoreRulesCachingBehaviorTests
{
	[Fact]
	public void ShouldApplySmartIgnore_ForFilePath_UsesParentDirectoryAsCacheKey()
	{
		using var temp = new TemporaryDirectory();
		var scopeRoot = temp.CreateFolder("workspace");
		var parentDirectory = Path.Combine(scopeRoot, "src");
		var filePath = Path.Combine(parentDirectory, "Program.cs");

		var rules = CreateSmartRules(scopeRoot);

		var applies = rules.ShouldApplySmartIgnore(filePath, isDirectory: false);

		Assert.True(applies);
		var cache = GetPrivateDictionary(rules, "_smartScopeApplicabilityCache");
		Assert.NotNull(cache);
		Assert.True(cache!.Contains(parentDirectory));
		Assert.False(cache.Contains(filePath));
	}

	[Fact]
	public void ShouldApplySmartIgnore_CacheOverflowEvictsOldestWithoutFlushingHotEntries()
	{
		using var temp = new TemporaryDirectory();
		var scopeRoot = temp.CreateFolder("workspace");
		var rules = CreateSmartRules(scopeRoot);
		var limit = GetPrivateIntConstant(typeof(IgnoreRules), "SmartScopeApplicabilityCacheLimit");

		for (var i = 0; i < limit + 2; i++)
		{
			var parentPath = Path.Combine(scopeRoot, $"dir-{i:D4}");
			var filePath = Path.Combine(parentPath, "file.txt");
			Assert.True(rules.ShouldApplySmartIgnore(filePath, isDirectory: false));
		}

		var cache = GetPrivateDictionary(rules, "_smartScopeApplicabilityCache");
		Assert.NotNull(cache);
		Assert.InRange(cache!.Count, 1, limit);
		Assert.False(cache.Contains(Path.Combine(scopeRoot, "dir-0000")));
		Assert.True(cache.Contains(Path.Combine(scopeRoot, $"dir-{limit + 1:D4}")));

		var outsidePath = Path.Combine(temp.Path, "outside", "x.txt");
		Assert.False(rules.ShouldApplySmartIgnore(outsidePath, isDirectory: false));
	}

	[Fact]
	public void EvaluateGitIgnore_ForFilePath_UsesParentDirectoryAsCacheKey()
	{
		using var temp = new TemporaryDirectory();
		var scopeRoot = temp.CreateFolder("repo");
		var srcDirectory = Path.Combine(scopeRoot, "src");
		var filePath = Path.Combine(srcDirectory, "artifact.tmp");

		var rules = CreateGitRules(scopeRoot);

		var evaluation = rules.EvaluateGitIgnore(filePath, isDirectory: false, name: "artifact.tmp");

		Assert.True(evaluation.IsIgnored);
		var cache = GetPrivateDictionary(rules, "_scopedMatcherChainCache");
		Assert.NotNull(cache);
		Assert.True(cache!.Contains(srcDirectory));
		Assert.False(cache.Contains(filePath));
	}

	[Fact]
	public void EvaluateGitIgnore_ReusesMatcherChainCacheForSameParentDirectory()
	{
		using var temp = new TemporaryDirectory();
		var scopeRoot = temp.CreateFolder("repo");
		var srcDirectory = Path.Combine(scopeRoot, "src");
		var rules = CreateGitRules(scopeRoot);

		var first = Path.Combine(srcDirectory, "first.tmp");
		var second = Path.Combine(srcDirectory, "second.tmp");

		Assert.True(rules.EvaluateGitIgnore(first, isDirectory: false, name: "first.tmp").IsIgnored);
		Assert.True(rules.EvaluateGitIgnore(second, isDirectory: false, name: "second.tmp").IsIgnored);

		var cache = GetPrivateDictionary(rules, "_scopedMatcherChainCache");
		Assert.NotNull(cache);
		Assert.Single(cache!);
	}

	[Fact]
	public void EvaluateGitIgnore_CacheOverflowEvictsOldestWithoutFlushingHotEntries()
	{
		using var temp = new TemporaryDirectory();
		var scopeRoot = temp.CreateFolder("repo");
		var rules = CreateGitRules(scopeRoot);
		var limit = GetPrivateIntConstant(typeof(IgnoreRules), "ScopedMatcherChainCacheLimit");

		for (var i = 0; i < limit + 2; i++)
		{
			var directory = Path.Combine(scopeRoot, $"dir-{i:D4}");
			var filePath = Path.Combine(directory, "artifact.tmp");
			Assert.True(rules.EvaluateGitIgnore(filePath, isDirectory: false, name: "artifact.tmp").IsIgnored);
		}

		var cache = GetPrivateDictionary(rules, "_scopedMatcherChainCache");
		Assert.NotNull(cache);
		Assert.InRange(cache!.Count, 1, limit);
		Assert.False(cache.Contains(Path.Combine(scopeRoot, "dir-0000")));
		Assert.True(cache.Contains(Path.Combine(scopeRoot, $"dir-{limit + 1:D4}")));

		var outside = Path.Combine(temp.Path, "outside", "artifact.tmp");
		Assert.False(rules.EvaluateGitIgnore(outside, isDirectory: false, name: "artifact.tmp").IsIgnored);
	}

	private static IgnoreRules CreateSmartRules(string scopeRoot)
	{
		return new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			UseSmartIgnore = true,
			SmartIgnoreScopeRoots = [scopeRoot]
		};
	}

	private static IgnoreRules CreateGitRules(string scopeRoot)
	{
		var rootMatcher = GitIgnoreMatcher.Build(scopeRoot, ["*.tmp"]);
		var nestedScope = Path.Combine(scopeRoot, "nested-scope");
		var nestedMatcher = GitIgnoreMatcher.Build(nestedScope, ["*.tmp"]);

		return new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			UseGitIgnore = true,
			ScopedGitIgnoreMatchers =
			[
				new ScopedGitIgnoreMatcher(scopeRoot, rootMatcher),
				new ScopedGitIgnoreMatcher(nestedScope, nestedMatcher)
			]
		};
	}

	private static IDictionary? GetPrivateDictionary(IgnoreRules rules, string fieldName)
	{
		var field = typeof(IgnoreRules).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(field);
		return field!.GetValue(rules) as IDictionary;
	}

	private static int GetPrivateIntConstant(Type type, string fieldName)
	{
		var field = type.GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
		Assert.NotNull(field);
		return (int)field!.GetRawConstantValue()!;
	}
}
