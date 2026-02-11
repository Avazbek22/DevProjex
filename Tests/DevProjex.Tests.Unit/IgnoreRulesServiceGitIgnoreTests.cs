using System;
using System.IO;
using DevProjex.Application.Services;
using DevProjex.Kernel.Models;
using Xunit;

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
			var service = new IgnoreRulesService(new SmartIgnoreService(Array.Empty<DevProjex.Kernel.Abstractions.ISmartIgnoreRule>()));

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

			var service = new IgnoreRulesService(new SmartIgnoreService(Array.Empty<DevProjex.Kernel.Abstractions.ISmartIgnoreRule>()));
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
}
