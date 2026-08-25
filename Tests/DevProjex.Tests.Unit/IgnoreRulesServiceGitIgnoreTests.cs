namespace DevProjex.Tests.Unit;

public sealed class IgnoreRulesServiceGitIgnoreTests
{
	[Fact]
	public void ScanContextPreservesLiteralBackslashesInsideUnixFileNames()
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip("Windows treats a backslash as a directory separator.");
			return;
		}

		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "literal/name.txt\n");
		var filePath = temp.CreateFile("literal\\name.txt", "content");
		var rules = new IgnoreRulesService(new SmartIgnoreService([]))
			.Build(temp.Path, [IgnoreOptionId.UseGitIgnore]);

		var evaluation = rules.CreateGitIgnoreScanContext(temp.Path)
			.Evaluate(filePath, "literal\\name.txt", isDirectory: false, "literal\\name.txt");

		Assert.False(evaluation.IsIgnored);
	}

	[Fact]
	public void AdministrativeNameMatrix_DependsOnSelectedGitModeAndPlatformPathSemantics()
	{
		using var temp = new TemporaryDirectory();
		var service = new IgnoreRulesService(new SmartIgnoreService([]));
		var activeRules = service.Build(temp.Path, [IgnoreOptionId.UseGitIgnore]);
		var trackedRules = service.Build(temp.Path, [IgnoreOptionId.TrackedGitFilesOnly]);
		var active = activeRules
			.CreateGitIgnoreScanContext(temp.Path);
		var disabled = service.Build(temp.Path, [])
			.CreateGitIgnoreScanContext(temp.Path);
		var cases = new Dictionary<string, bool>(StringComparer.Ordinal)
		{
			[".git"] = true,
			[".github"] = false,
			[".git-owned"] = false,
			[".gitattributes"] = false,
			[".gitignore"] = false,
			["git"] = false,
			[".Git"] = OperatingSystem.IsWindows()
		};
		var tracked = trackedRules
			.CreateGitIgnoreScanContext(temp.Path)
			.WithTrackedPathIndex(new GitTrackedPathIndex(
				temp.Path,
				cases.Keys.Select(static name => $"nested/{name}")));

		foreach (var (name, expectedIgnored) in cases)
		{
			var path = Path.Combine(temp.Path, "nested", name);
			Assert.Equal(
				expectedIgnored,
				active.Evaluate(path, $"nested/{name}", isDirectory: true, name).IsIgnored);
			Assert.Equal(
				expectedIgnored,
				tracked.Evaluate(path, $"nested/{name}", isDirectory: true, name).IsIgnored);
			Assert.Equal(expectedIgnored, activeRules.IsGitIgnored(path, isDirectory: true, name));
			Assert.False(disabled.Evaluate(path, $"nested/{name}", isDirectory: true, name).IsIgnored);
		}

		Assert.True(active.Evaluate(
			Path.Combine(temp.Path, ".git"),
			".git",
			isDirectory: false,
			".git").IsIgnored);
		Assert.True(trackedRules.IsGitIgnored(
			Path.Combine(temp.Path, ".git"),
			isDirectory: true,
			".git"));
	}

	[Fact]
	public void Build_WhenGitIgnoreOptionSelectedAndFileMissing_KeepsRequestedModeActive()
	{
		var tempRoot = Path.Combine(Path.GetTempPath(), $"devprojex-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempRoot);
		try
		{
			var service = new IgnoreRulesService(new SmartIgnoreService([]));

			var rules = service.Build(tempRoot, [IgnoreOptionId.UseGitIgnore]);

			Assert.True(rules.UseGitIgnore);
			Assert.Equal(GitFilteringMode.RespectGitIgnore, rules.GitFilteringMode);
			Assert.Same(GitIgnoreMatcher.Empty, rules.GitIgnoreMatcher);

			var context = rules.CreateGitIgnoreScanContext(tempRoot);
			var gitMetadata = Path.Combine(tempRoot, ".git");
			var lookalike = Path.Combine(tempRoot, ".github");
			Assert.True(context.Evaluate(gitMetadata, ".git", isDirectory: true, ".git").IsIgnored);
			Assert.False(context.Evaluate(lookalike, ".github", isDirectory: true, ".github").IsIgnored);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void Build_WhenOnlyGitIgnoreIsSelected_DoesNotActivateSmartIgnore()
	{
		var tempRoot = Path.Combine(Path.GetTempPath(), $"devprojex-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempRoot);
		try
		{
			var smartResult = new SmartIgnoreResult(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin", "obj" },
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Thumbs.db" });
			var service = new IgnoreRulesService(new SmartIgnoreService([new StubSmartIgnoreRule(smartResult)]));

			var rules = service.Build(tempRoot, [IgnoreOptionId.UseGitIgnore]);

			Assert.True(rules.UseGitIgnore);
			Assert.Same(GitIgnoreMatcher.Empty, rules.GitIgnoreMatcher);
			Assert.Empty(rules.SmartIgnoredFolders);
			Assert.Empty(rules.SmartIgnoredFiles);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void Build_WhenGitIgnoreExists_ParsesPatternsAndNegation()
	{
		var tempRoot = Path.Combine(Path.GetTempPath(), $"devprojex-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempRoot);
		try
		{
			File.WriteAllLines(Path.Combine(tempRoot, ".gitignore"), [
				"bin/",
				"*.log",
				"!important.log",
				"nested/cache/"
			]);

			var service = new IgnoreRulesService(new SmartIgnoreService([]));
			var rules = service.Build(tempRoot, [IgnoreOptionId.UseGitIgnore]);

			Assert.True(rules.UseGitIgnore);
			Assert.False(ReferenceEquals(rules.GitIgnoreMatcher, GitIgnoreMatcher.Empty));

			var binDir = Path.Combine(tempRoot, "bin");
			var normalLog = Path.Combine(tempRoot, "service.log");
			var importantLog = Path.Combine(tempRoot, "important.log");
			var nestedCacheDir = Path.Combine(tempRoot, "nested", "cache");

			Assert.True(rules.GitIgnoreMatcher.IsIgnored(binDir, isDirectory: true, "bin"));
			Assert.True(rules.GitIgnoreMatcher.IsIgnored(normalLog, isDirectory: false, "service.log"));
			Assert.False(rules.GitIgnoreMatcher.IsIgnored(importantLog, isDirectory: false, "important.log"));
			Assert.True(rules.GitIgnoreMatcher.IsIgnored(nestedCacheDir, isDirectory: true, "cache"));
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void Build_WhenGitIgnoreChanges_RebuildsMatcherFromUpdatedContent()
	{
		var tempRoot = Path.Combine(Path.GetTempPath(), $"devprojex-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempRoot);
		try
		{
			var gitIgnorePath = Path.Combine(tempRoot, ".gitignore");
			File.WriteAllText(gitIgnorePath, "bin/");

			var service = new IgnoreRulesService(new SmartIgnoreService([]));
			var firstRules = service.Build(tempRoot, [IgnoreOptionId.UseGitIgnore]);
			Assert.True(firstRules.GitIgnoreMatcher.IsIgnored(Path.Combine(tempRoot, "bin"), isDirectory: true, "bin"));
			Assert.False(firstRules.GitIgnoreMatcher.IsIgnored(Path.Combine(tempRoot, "dist"), isDirectory: true, "dist"));

			File.WriteAllText(gitIgnorePath, "dist/");
			var secondRules = service.Build(tempRoot, [IgnoreOptionId.UseGitIgnore]);

			Assert.False(secondRules.GitIgnoreMatcher.IsIgnored(Path.Combine(tempRoot, "bin"), isDirectory: true, "bin"));
			Assert.True(secondRules.GitIgnoreMatcher.IsIgnored(Path.Combine(tempRoot, "dist"), isDirectory: true, "dist"));
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}
}
