using DevProjex.Application.Services;
using DevProjex.Kernel.Abstractions;
using DevProjex.Tests.Unit.Helpers;

namespace DevProjex.Tests.Unit;

public sealed class IgnoreRulesServiceGitIgnoreTests
{
	[Fact]
	public void Build_WhenGitIgnoreOptionSelectedAndFileMissing_DisablesGitIgnore()
	{
		var tempRoot = Path.Combine(Path.GetTempPath(), $"devprojex-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempRoot);
		try
		{
			var service = new IgnoreRulesService(new SmartIgnoreService(Array.Empty<ISmartIgnoreRule>()));

			var rules = service.Build(tempRoot, new[] { IgnoreOptionId.UseGitIgnore });

			Assert.False(rules.UseGitIgnore);
			Assert.Same(GitIgnoreMatcher.Empty, rules.GitIgnoreMatcher);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void Build_WhenGitIgnoreMissing_DisablesSmartIgnoreEvenIfOptionSelected()
	{
		var tempRoot = Path.Combine(Path.GetTempPath(), $"devprojex-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempRoot);
		try
		{
			var smartResult = new SmartIgnoreResult(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin", "obj" },
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Thumbs.db" });
			var service = new IgnoreRulesService(new SmartIgnoreService(new[] { new StubSmartIgnoreRule(smartResult) }));

			var rules = service.Build(tempRoot, new[] { IgnoreOptionId.UseGitIgnore });

			Assert.False(rules.UseGitIgnore);
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
			File.WriteAllLines(Path.Combine(tempRoot, ".gitignore"), new[]
			{
				"bin/",
				"*.log",
				"!important.log",
				"nested/cache/"
			});

			var service = new IgnoreRulesService(new SmartIgnoreService(Array.Empty<ISmartIgnoreRule>()));
			var rules = service.Build(tempRoot, new[] { IgnoreOptionId.UseGitIgnore });

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

			var service = new IgnoreRulesService(new SmartIgnoreService(Array.Empty<ISmartIgnoreRule>()));
			var firstRules = service.Build(tempRoot, new[] { IgnoreOptionId.UseGitIgnore });
			Assert.True(firstRules.GitIgnoreMatcher.IsIgnored(Path.Combine(tempRoot, "bin"), isDirectory: true, "bin"));
			Assert.False(firstRules.GitIgnoreMatcher.IsIgnored(Path.Combine(tempRoot, "dist"), isDirectory: true, "dist"));

			File.WriteAllText(gitIgnorePath, "dist/");
			var secondRules = service.Build(tempRoot, new[] { IgnoreOptionId.UseGitIgnore });

			Assert.False(secondRules.GitIgnoreMatcher.IsIgnored(Path.Combine(tempRoot, "bin"), isDirectory: true, "bin"));
			Assert.True(secondRules.GitIgnoreMatcher.IsIgnored(Path.Combine(tempRoot, "dist"), isDirectory: true, "dist"));
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}
}
