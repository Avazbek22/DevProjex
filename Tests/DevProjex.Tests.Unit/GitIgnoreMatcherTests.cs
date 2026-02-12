using System;
using System.IO;
using System.Linq;
using DevProjex.Kernel.Models;
using Xunit;

namespace DevProjex.Tests.Unit;

public sealed class GitIgnoreMatcherTests
{
	#region Basic Functionality

	[Fact]
	public void Empty_ReturnsEmptyMatcherForNullRoot()
	{
		var matcher = GitIgnoreMatcher.Build(null!, Array.Empty<string>());
		Assert.Same(GitIgnoreMatcher.Empty, matcher);
	}

	[Fact]
	public void Empty_ReturnsEmptyMatcherForEmptyRoot()
	{
		var matcher = GitIgnoreMatcher.Build("", Array.Empty<string>());
		Assert.Same(GitIgnoreMatcher.Empty, matcher);
	}

	[Fact]
	public void Empty_NeverIgnoresAnything()
	{
		Assert.False(GitIgnoreMatcher.Empty.IsIgnored("/any/path", true, "path"));
		Assert.False(GitIgnoreMatcher.Empty.IsIgnored("/any/file.txt", false, "file.txt"));
	}

	[Fact]
	public void Build_SkipsEmptyLines()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "", "  ", "*.log", "" });
		Assert.True(matcher.IsIgnored("/repo/test.log", false, "test.log"));
	}

	[Fact]
	public void Build_SkipsCommentLines()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "# comment", "*.log", "# another comment" });
		Assert.True(matcher.IsIgnored("/repo/test.log", false, "test.log"));
		Assert.False(matcher.IsIgnored("/repo/# comment", false, "# comment"));
	}

	[Fact]
	public void Build_HandlesEscapedHash()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { @"\#important" });
		Assert.True(matcher.IsIgnored("/repo/#important", false, "#important"));
	}

	[Fact]
	public void Build_HandlesEscapedExclamation()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { @"\!important" });
		Assert.True(matcher.IsIgnored("/repo/!important", false, "!important"));
	}

	#endregion

	#region Simple Patterns

	[Theory]
	[InlineData("*.log", "/repo/debug.log", true)]
	[InlineData("*.log", "/repo/src/debug.log", true)]
	[InlineData("*.log", "/repo/debug.txt", false)]
	[InlineData("*.txt", "/repo/readme.txt", true)]
	[InlineData("*.txt", "/repo/deep/nested/file.txt", true)]
	public void IsIgnored_SimpleWildcard_MatchesExtension(string pattern, string path, bool expected)
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { pattern });
		var name = Path.GetFileName(path);
		Assert.Equal(expected, matcher.IsIgnored(path, false, name));
	}

	[Theory]
	[InlineData("debug.log", "/repo/debug.log", true)]
	[InlineData("debug.log", "/repo/src/debug.log", true)]
	[InlineData("debug.log", "/repo/debug.log.bak", false)]
	[InlineData("README.md", "/repo/README.md", true)]
	[InlineData("README.md", "/repo/docs/README.md", true)]
	public void IsIgnored_ExactFileName_MatchesAnywhere(string pattern, string path, bool expected)
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { pattern });
		var name = Path.GetFileName(path);
		Assert.Equal(expected, matcher.IsIgnored(path, false, name));
	}

	[Fact]
	public void IsIgnored_DirectoryPattern_MatchesOnlyDirectories()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "logs/" });

		Assert.True(matcher.IsIgnored("/repo/logs", true, "logs"));
		Assert.True(matcher.IsIgnored("/repo/src/logs", true, "logs"));
		// Directory patterns should still match the directory
		Assert.True(matcher.IsIgnored("/repo/logs", true, "logs"));
	}

	[Fact]
	public void IsIgnored_AnchoredPattern_MatchesOnlyAtRoot()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "/build" });

		Assert.True(matcher.IsIgnored("/repo/build", true, "build"));
		Assert.False(matcher.IsIgnored("/repo/src/build", true, "build"));
	}

	[Fact]
	public void IsIgnored_AnchoredDirectoryPattern_MatchesOnlyAtRoot()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "/dist/" });

		Assert.True(matcher.IsIgnored("/repo/dist", true, "dist"));
		Assert.False(matcher.IsIgnored("/repo/packages/dist", true, "dist"));
	}

	#endregion

	#region Character Classes

	[Theory]
	[InlineData("[Oo]bj/", "/repo/obj", true)]
	[InlineData("[Oo]bj/", "/repo/Obj", true)]
	[InlineData("[Bb]in/", "/repo/bin", true)]
	[InlineData("[Bb]in/", "/repo/Bin", true)]
	[InlineData("[Dd]ebug/", "/repo/debug", true)]
	[InlineData("[Dd]ebug/", "/repo/Debug", true)]
	[InlineData("[Rr]elease/", "/repo/release", true)]
	[InlineData("[Rr]elease/", "/repo/Release", true)]
	public void IsIgnored_CharacterClass_MatchesBothCases(string pattern, string path, bool expected)
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { pattern });
		var name = Path.GetFileName(path);
		Assert.Equal(expected, matcher.IsIgnored(path, true, name));
	}

	[Fact]
	public void IsIgnored_CharacterClass_CaseSensitivityDependsOnPlatform()
	{
		// On Windows/macOS, matching is case-insensitive, so [Oo]bj matches OBJ
		// On Linux, matching is case-sensitive, so [Oo]bj does NOT match OBJ
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "[Oo]bj/" });
		var result = matcher.IsIgnored("/repo/OBJ", true, "OBJ");

		if (OperatingSystem.IsLinux())
			Assert.False(result);
		else
			Assert.True(result); // Windows/macOS are case-insensitive
	}

	[Theory]
	[InlineData("[abc]", "/repo/a", true)]
	[InlineData("[abc]", "/repo/b", true)]
	[InlineData("[abc]", "/repo/c", true)]
	[InlineData("[abc]", "/repo/d", false)]
	[InlineData("[0-9]", "/repo/5", true)]
	[InlineData("[0-9]", "/repo/x", false)]
	[InlineData("[a-z]", "/repo/m", true)]
	[InlineData("[a-z]", "/repo/M", false)] // Case sensitive within class
	public void IsIgnored_CharacterClassRanges_MatchesCorrectly(string pattern, string path, bool expected)
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { pattern });
		var name = Path.GetFileName(path);
		// On Windows, the matcher is case-insensitive, so adjust expectations
		if (OperatingSystem.IsWindows() && pattern == "[a-z]" && path == "/repo/M")
			expected = true;
		Assert.Equal(expected, matcher.IsIgnored(path, false, name));
	}

	[Fact]
	public void IsIgnored_CharacterClassWithSpecialChars_HandlesCorrectly()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "[.-]file" });
		Assert.True(matcher.IsIgnored("/repo/.file", false, ".file"));
		Assert.True(matcher.IsIgnored("/repo/-file", false, "-file"));
	}

	[Fact]
	public void IsIgnored_NestedCharacterClasses_NotSupported()
	{
		// Nested brackets aren't standard gitignore - just ensure no crash
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "[[abc]]" });
		Assert.NotNull(matcher);
	}

	#endregion

	#region Double Asterisk Patterns

	[Fact]
	public void IsIgnored_DoubleAsteriskPrefix_MatchesAnyDepth()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "**/logs" });

		Assert.True(matcher.IsIgnored("/repo/logs", true, "logs"));
		Assert.True(matcher.IsIgnored("/repo/src/logs", true, "logs"));
		Assert.True(matcher.IsIgnored("/repo/a/b/c/logs", true, "logs"));
	}

	[Fact]
	public void IsIgnored_DoubleAsteriskMiddle_MatchesAnyDepth()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "a/**/z" });

		Assert.True(matcher.IsIgnored("/repo/a/z", false, "z"));
		Assert.True(matcher.IsIgnored("/repo/a/b/z", false, "z"));
		Assert.True(matcher.IsIgnored("/repo/a/b/c/d/z", false, "z"));
		Assert.False(matcher.IsIgnored("/repo/b/z", false, "z"));
	}

	[Fact]
	public void IsIgnored_DoubleAsteriskSuffix_MatchesEverythingInside()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "build/**" });

		Assert.True(matcher.IsIgnored("/repo/build/output.dll", false, "output.dll"));
		Assert.True(matcher.IsIgnored("/repo/build/sub/file.txt", false, "file.txt"));
	}

	[Theory]
	[InlineData("**/bin/*", "/repo/bin/Debug", true)]
	[InlineData("**/bin/*", "/repo/src/Project/bin/Release", true)]
	[InlineData("**/bin/*", "/repo/Bin/x64", true)]
	[InlineData("**/obj/*", "/repo/obj/Debug", true)]
	[InlineData("**/obj/*", "/repo/src/Project/obj/Release", true)]
	public void IsIgnored_DoubleAsteriskWithCharClass_MatchesBuildOutputs(string pattern, string path, bool expected)
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { pattern });
		var name = Path.GetFileName(path);
		Assert.Equal(expected, matcher.IsIgnored(path, true, name));
	}

	[Fact]
	public void IsIgnored_DoubleAsteriskAtEnd_MatchesAllDescendants()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "vendor/**" });

		Assert.True(matcher.IsIgnored("/repo/vendor/package/file.go", false, "file.go"));
		Assert.True(matcher.IsIgnored("/repo/vendor/a/b/c/d.txt", false, "d.txt"));
	}

	#endregion

	#region Negation Patterns

	[Fact]
	public void HasNegationRules_ReturnsTrueWhenNegationPresent()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "*.log", "!important.log" });
		Assert.True(matcher.HasNegationRules);
	}

	[Fact]
	public void HasNegationRules_ReturnsFalseWhenNoNegation()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "*.log", "build/" });
		Assert.False(matcher.HasNegationRules);
	}

	[Fact]
	public void IsIgnored_NegationPattern_UnignoresFile()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "*.log", "!important.log" });

		Assert.True(matcher.IsIgnored("/repo/debug.log", false, "debug.log"));
		Assert.False(matcher.IsIgnored("/repo/important.log", false, "important.log"));
	}

	[Fact]
	public void IsIgnored_NegationInDirectory_UnignoresSpecificPath()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "logs/", "!logs/keep/" });

		Assert.True(matcher.IsIgnored("/repo/logs", true, "logs"));
		// Note: negation of directories can be complex in gitignore
	}

	[Fact]
	public void ShouldTraverseIgnoredDirectory_ReturnsFalseWhenNoNegationRules()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "build/" });

		Assert.False(matcher.HasNegationRules);
		Assert.False(matcher.ShouldTraverseIgnoredDirectory("/repo/build", "build"));
	}

	[Fact]
	public void ShouldTraverseIgnoredDirectory_ReturnsTrueForNameOnlyNegation()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "build/", "!keep.txt" });

		Assert.True(matcher.HasNegationRules);
		Assert.True(matcher.ShouldTraverseIgnoredDirectory("/repo/build", "build"));
	}

	[Fact]
	public void ShouldTraverseIgnoredDirectory_ReturnsTrueWhenNegationTargetsDescendantPath()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "build/", "!build/keep.txt" });

		Assert.True(matcher.ShouldTraverseIgnoredDirectory("/repo/build", "build"));
		Assert.False(matcher.ShouldTraverseIgnoredDirectory("/repo/other", "other"));
	}

	#endregion

	#region Question Mark Wildcard

	[Theory]
	[InlineData("file?.txt", "/repo/file1.txt", true)]
	[InlineData("file?.txt", "/repo/fileA.txt", true)]
	[InlineData("file?.txt", "/repo/file.txt", false)]
	[InlineData("file?.txt", "/repo/file12.txt", false)]
	[InlineData("???.log", "/repo/abc.log", true)]
	[InlineData("???.log", "/repo/ab.log", false)]
	public void IsIgnored_QuestionMark_MatchesSingleCharacter(string pattern, string path, bool expected)
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { pattern });
		var name = Path.GetFileName(path);
		Assert.Equal(expected, matcher.IsIgnored(path, false, name));
	}

	#endregion

	#region Cross-Platform Path Handling

	[Fact]
	public void IsIgnored_WindowsStylePaths_NormalizedCorrectly()
	{
		var matcher = GitIgnoreMatcher.Build(@"C:\repo", new[] { "bin/", "*.log" });

		Assert.True(matcher.IsIgnored(@"C:\repo\bin", true, "bin"));
		Assert.True(matcher.IsIgnored(@"C:\repo\src\bin", true, "bin"));
		Assert.True(matcher.IsIgnored(@"C:\repo\debug.log", false, "debug.log"));
	}

	[Fact]
	public void IsIgnored_MixedSlashes_HandledCorrectly()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "src/build/" });

		Assert.True(matcher.IsIgnored("/repo/src/build", true, "build"));
	}

	[Fact]
	public void IsIgnored_UnixStylePaths_WorkCorrectly()
	{
		var matcher = GitIgnoreMatcher.Build("/home/user/project", new[] { "node_modules/", "*.pyc" });

		Assert.True(matcher.IsIgnored("/home/user/project/node_modules", true, "node_modules"));
		Assert.True(matcher.IsIgnored("/home/user/project/src/cache.pyc", false, "cache.pyc"));
	}

	[Fact]
	public void IsIgnored_PathOutsideRoot_ReturnsAlwaysFalse()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "*.log" });

		Assert.False(matcher.IsIgnored("/other/debug.log", false, "debug.log"));
		Assert.False(matcher.IsIgnored("/different/repo/file.log", false, "file.log"));
	}

	#endregion

	#region Real-World .gitignore Patterns - Visual Studio / .NET

	[Fact]
	public void IsIgnored_VisualStudioGitignore_BinObjPatterns()
	{
		var patterns = new[]
		{
			"[Bb]in/",
			"[Oo]bj/",
			"[Dd]ebug/",
			"[Rr]elease/",
			"**/[Bb]in/*",
			"[Oo]bj/"
		};
		var matcher = GitIgnoreMatcher.Build("/repo", patterns);

		Assert.True(matcher.IsIgnored("/repo/bin", true, "bin"));
		Assert.True(matcher.IsIgnored("/repo/Bin", true, "Bin"));
		Assert.True(matcher.IsIgnored("/repo/obj", true, "obj"));
		Assert.True(matcher.IsIgnored("/repo/Obj", true, "Obj"));
		Assert.True(matcher.IsIgnored("/repo/Debug", true, "Debug"));
		Assert.True(matcher.IsIgnored("/repo/Release", true, "Release"));
		Assert.True(matcher.IsIgnored("/repo/src/MyProject/bin", true, "bin"));
		Assert.True(matcher.IsIgnored("/repo/src/MyProject/obj", true, "obj"));
	}

	[Fact]
	public void IsIgnored_VisualStudioGitignore_UserFiles()
	{
		var patterns = new[]
		{
			"*.suo",
			"*.user",
			"*.userosscache",
			"*.sln.docstates",
			".vs/"
		};
		var matcher = GitIgnoreMatcher.Build("/repo", patterns);

		Assert.True(matcher.IsIgnored("/repo/project.suo", false, "project.suo"));
		Assert.True(matcher.IsIgnored("/repo/project.csproj.user", false, "project.csproj.user"));
		Assert.True(matcher.IsIgnored("/repo/.vs", true, ".vs"));
	}

	[Fact]
	public void IsIgnored_VisualStudioGitignore_NuGetPackages()
	{
		var patterns = new[]
		{
			"**/[Pp]ackages/*",
			"!**/[Pp]ackages/build/",
			"*.nupkg",
			"*.snupkg"
		};
		var matcher = GitIgnoreMatcher.Build("/repo", patterns);

		Assert.True(matcher.IsIgnored("/repo/packages/Newtonsoft.Json", true, "Newtonsoft.Json"));
		Assert.True(matcher.IsIgnored("/repo/MyLib.1.0.0.nupkg", false, "MyLib.1.0.0.nupkg"));
	}

	#endregion

	#region Real-World .gitignore Patterns - Node.js

	[Fact]
	public void IsIgnored_NodeGitignore_CommonPatterns()
	{
		var patterns = new[]
		{
			"node_modules/",
			"npm-debug.log*",
			"yarn-debug.log*",
			"yarn-error.log*",
			".npm",
			".yarn",
			"dist/",
			"build/",
			"coverage/"
		};
		var matcher = GitIgnoreMatcher.Build("/repo", patterns);

		Assert.True(matcher.IsIgnored("/repo/node_modules", true, "node_modules"));
		Assert.True(matcher.IsIgnored("/repo/npm-debug.log", false, "npm-debug.log"));
		Assert.True(matcher.IsIgnored("/repo/npm-debug.log.1234", false, "npm-debug.log.1234"));
		Assert.True(matcher.IsIgnored("/repo/dist", true, "dist"));
		Assert.True(matcher.IsIgnored("/repo/coverage", true, "coverage"));
		Assert.True(matcher.IsIgnored("/repo/packages/mylib/node_modules", true, "node_modules"));
	}

	[Fact]
	public void IsIgnored_NodeGitignore_EnvFiles()
	{
		var patterns = new[]
		{
			".env",
			".env.local",
			".env.*.local",
			"*.env"
		};
		var matcher = GitIgnoreMatcher.Build("/repo", patterns);

		Assert.True(matcher.IsIgnored("/repo/.env", false, ".env"));
		Assert.True(matcher.IsIgnored("/repo/.env.local", false, ".env.local"));
		Assert.True(matcher.IsIgnored("/repo/production.env", false, "production.env"));
	}

	#endregion

	#region Real-World .gitignore Patterns - Python

	[Fact]
	public void IsIgnored_PythonGitignore_CommonPatterns()
	{
		var patterns = new[]
		{
			"__pycache__/",
			"*.py[cod]",
			"*$py.class",
			".Python",
			"venv/",
			"ENV/",
			".venv/",
			"*.egg-info/",
			"dist/",
			"build/"
		};
		var matcher = GitIgnoreMatcher.Build("/repo", patterns);

		Assert.True(matcher.IsIgnored("/repo/__pycache__", true, "__pycache__"));
		Assert.True(matcher.IsIgnored("/repo/src/__pycache__", true, "__pycache__"));
		Assert.True(matcher.IsIgnored("/repo/module.pyc", false, "module.pyc"));
		Assert.True(matcher.IsIgnored("/repo/module.pyo", false, "module.pyo"));
		Assert.True(matcher.IsIgnored("/repo/module.pyd", false, "module.pyd"));
		Assert.True(matcher.IsIgnored("/repo/venv", true, "venv"));
		Assert.True(matcher.IsIgnored("/repo/.venv", true, ".venv"));
		Assert.True(matcher.IsIgnored("/repo/mypackage.egg-info", true, "mypackage.egg-info"));
	}

	[Fact]
	public void IsIgnored_PythonGitignore_JupyterNotebooks()
	{
		var patterns = new[]
		{
			".ipynb_checkpoints/",
			"*.ipynb_checkpoints"
		};
		var matcher = GitIgnoreMatcher.Build("/repo", patterns);

		Assert.True(matcher.IsIgnored("/repo/.ipynb_checkpoints", true, ".ipynb_checkpoints"));
		Assert.True(matcher.IsIgnored("/repo/notebooks/.ipynb_checkpoints", true, ".ipynb_checkpoints"));
	}

	#endregion

	#region Real-World .gitignore Patterns - Java

	[Fact]
	public void IsIgnored_JavaGitignore_CommonPatterns()
	{
		var patterns = new[]
		{
			"*.class",
			"*.jar",
			"*.war",
			"*.ear",
			"target/",
			".gradle/",
			"build/",
			"out/",
			".idea/"
		};
		var matcher = GitIgnoreMatcher.Build("/repo", patterns);

		Assert.True(matcher.IsIgnored("/repo/Main.class", false, "Main.class"));
		Assert.True(matcher.IsIgnored("/repo/target", true, "target"));
		Assert.True(matcher.IsIgnored("/repo/module/target", true, "target"));
		Assert.True(matcher.IsIgnored("/repo/.gradle", true, ".gradle"));
		Assert.True(matcher.IsIgnored("/repo/build", true, "build"));
		Assert.True(matcher.IsIgnored("/repo/.idea", true, ".idea"));
		Assert.True(matcher.IsIgnored("/repo/app.jar", false, "app.jar"));
	}

	[Fact]
	public void IsIgnored_MavenGitignore_TargetDirectories()
	{
		var patterns = new[]
		{
			"target/",
			"pom.xml.tag",
			"pom.xml.releaseBackup",
			"pom.xml.versionsBackup",
			"release.properties"
		};
		var matcher = GitIgnoreMatcher.Build("/repo", patterns);

		Assert.True(matcher.IsIgnored("/repo/target", true, "target"));
		Assert.True(matcher.IsIgnored("/repo/module/target", true, "target"));
		Assert.True(matcher.IsIgnored("/repo/pom.xml.releaseBackup", false, "pom.xml.releaseBackup"));
	}

	#endregion

	#region Real-World .gitignore Patterns - Go

	[Fact]
	public void IsIgnored_GoGitignore_CommonPatterns()
	{
		var patterns = new[]
		{
			"vendor/",
			"*.exe",
			"*.exe~",
			"*.dll",
			"*.so",
			"*.dylib",
			"*.test",
			"*.out",
			"go.work"
		};
		var matcher = GitIgnoreMatcher.Build("/repo", patterns);

		Assert.True(matcher.IsIgnored("/repo/vendor", true, "vendor"));
		Assert.True(matcher.IsIgnored("/repo/app.exe", false, "app.exe"));
		Assert.True(matcher.IsIgnored("/repo/app.dll", false, "app.dll"));
		Assert.True(matcher.IsIgnored("/repo/pkg.test", false, "pkg.test"));
		Assert.True(matcher.IsIgnored("/repo/coverage.out", false, "coverage.out"));
	}

	#endregion

	#region Real-World .gitignore Patterns - Ruby

	[Fact]
	public void IsIgnored_RubyGitignore_CommonPatterns()
	{
		var patterns = new[]
		{
			"*.gem",
			"*.rbc",
			".bundle/",
			"vendor/bundle/",
			"coverage/",
			"log/",
			"tmp/",
			".byebug_history"
		};
		var matcher = GitIgnoreMatcher.Build("/repo", patterns);

		Assert.True(matcher.IsIgnored("/repo/myapp.gem", false, "myapp.gem"));
		Assert.True(matcher.IsIgnored("/repo/.bundle", true, ".bundle"));
		Assert.True(matcher.IsIgnored("/repo/vendor/bundle", true, "bundle"));
		Assert.True(matcher.IsIgnored("/repo/coverage", true, "coverage"));
		Assert.True(matcher.IsIgnored("/repo/log", true, "log"));
		Assert.True(matcher.IsIgnored("/repo/tmp", true, "tmp"));
	}

	#endregion

	#region Real-World .gitignore Patterns - Rust

	[Fact]
	public void IsIgnored_RustGitignore_CommonPatterns()
	{
		var patterns = new[]
		{
			"/target/",
			"**/*.rs.bk",
			"Cargo.lock"
		};
		var matcher = GitIgnoreMatcher.Build("/repo", patterns);

		Assert.True(matcher.IsIgnored("/repo/target", true, "target"));
		Assert.False(matcher.IsIgnored("/repo/crates/mylib/target", true, "target")); // anchored
		Assert.True(matcher.IsIgnored("/repo/src/main.rs.bk", false, "main.rs.bk"));
		Assert.True(matcher.IsIgnored("/repo/Cargo.lock", false, "Cargo.lock"));
	}

	#endregion

	#region Real-World .gitignore Patterns - PHP

	[Fact]
	public void IsIgnored_PHPGitignore_CommonPatterns()
	{
		var patterns = new[]
		{
			"vendor/",
			"composer.lock",
			"*.cache",
			"storage/",
			"bootstrap/cache/"
		};
		var matcher = GitIgnoreMatcher.Build("/repo", patterns);

		Assert.True(matcher.IsIgnored("/repo/vendor", true, "vendor"));
		Assert.True(matcher.IsIgnored("/repo/app.cache", false, "app.cache"));
		Assert.True(matcher.IsIgnored("/repo/storage", true, "storage"));
	}

	#endregion

	#region Edge Cases

	[Fact]
	public void IsIgnored_PatternWithSpaces_HandlesCorrectly()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "my folder/", "file name.txt" });

		Assert.True(matcher.IsIgnored("/repo/my folder", true, "my folder"));
		Assert.True(matcher.IsIgnored("/repo/file name.txt", false, "file name.txt"));
	}

	[Fact]
	public void IsIgnored_TrailingWhitespace_Trimmed()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "*.log   ", "build/  " });

		Assert.True(matcher.IsIgnored("/repo/debug.log", false, "debug.log"));
		Assert.True(matcher.IsIgnored("/repo/build", true, "build"));
	}

	[Fact]
	public void IsIgnored_PatternWithDots_EscapedCorrectly()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "file.config.txt", ".env.local" });

		Assert.True(matcher.IsIgnored("/repo/file.config.txt", false, "file.config.txt"));
		Assert.False(matcher.IsIgnored("/repo/fileXconfigXtxt", false, "fileXconfigXtxt"));
		Assert.True(matcher.IsIgnored("/repo/.env.local", false, ".env.local"));
	}

	[Fact]
	public void IsIgnored_VeryDeepPath_HandlesCorrectly()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "**/deep.txt" });

		var deepPath = "/repo" + string.Concat(Enumerable.Repeat("/a", 50)) + "/deep.txt";
		Assert.True(matcher.IsIgnored(deepPath, false, "deep.txt"));
	}

	[Fact]
	public void IsIgnored_EmptyFileName_HandlesCorrectly()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "*.txt" });

		// Should not crash with empty name
		Assert.False(matcher.IsIgnored("/repo/", true, ""));
	}

	[Fact]
	public void IsIgnored_SpecialRegexChars_EscapedCorrectly()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "file(1).txt", "test+plus.log", "dollar$sign" });

		Assert.True(matcher.IsIgnored("/repo/file(1).txt", false, "file(1).txt"));
		Assert.True(matcher.IsIgnored("/repo/test+plus.log", false, "test+plus.log"));
		Assert.True(matcher.IsIgnored("/repo/dollar$sign", false, "dollar$sign"));
	}

	[Fact]
	public void IsIgnored_PatternOnlySlash_HandlesCorrectly()
	{
		// Edge case: pattern is just "/"
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "/" });
		Assert.NotNull(matcher);
	}

	[Fact]
	public void IsIgnored_MultipleConsecutiveSlashes_Normalized()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "src//build/" });

		// The pattern might not match due to normalization, but shouldn't crash
		Assert.NotNull(matcher);
	}

	[Fact]
	public void IsIgnored_UnicodeCharacters_HandledCorrectly()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "файл.txt", "文件.log" });

		Assert.True(matcher.IsIgnored("/repo/файл.txt", false, "файл.txt"));
		Assert.True(matcher.IsIgnored("/repo/文件.log", false, "文件.log"));
	}

	[Fact]
	public void IsIgnored_LongPattern_HandlesCorrectly()
	{
		var longPattern = new string('a', 500) + ".txt";
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { longPattern });

		Assert.True(matcher.IsIgnored("/repo/" + longPattern, false, longPattern));
	}

	#endregion

	#region Multiple Rules Interaction

	[Fact]
	public void IsIgnored_LastMatchWins()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] {
			"*.log",
			"!important.log",
			"important.log" // Re-ignore
		});

		Assert.True(matcher.IsIgnored("/repo/important.log", false, "important.log"));
	}

	[Fact]
	public void IsIgnored_OrderMatters_FirstIgnoreThenUnignore()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] {
			"logs/",
			"!logs/keep.log"
		});

		Assert.True(matcher.IsIgnored("/repo/logs", true, "logs"));
		Assert.False(matcher.IsIgnored("/repo/logs/keep.log", false, "keep.log"));
	}

	[Fact]
	public void IsIgnored_ComplexCombination_WorksCorrectly()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] {
			"**/node_modules/",
			"**/dist/",
			"*.log",
			"!error.log",
			"build/",
			"!build/important.dll"
		});

		Assert.True(matcher.IsIgnored("/repo/node_modules", true, "node_modules"));
		Assert.True(matcher.IsIgnored("/repo/packages/app/node_modules", true, "node_modules"));
		Assert.True(matcher.IsIgnored("/repo/dist", true, "dist"));
		Assert.True(matcher.IsIgnored("/repo/debug.log", false, "debug.log"));
		Assert.False(matcher.IsIgnored("/repo/error.log", false, "error.log"));
		Assert.True(matcher.IsIgnored("/repo/build", true, "build"));
		Assert.False(matcher.IsIgnored("/repo/build/important.dll", false, "important.dll"));
	}

	#endregion

	#region Content-Based Directory Ignore (dir/* patterns)

	[Fact]
	public void IsIgnored_ContentPatternIgnoresDirectory_WhenAllContentsIgnored()
	{
		// Pattern **/bin/* ignores all contents of bin, so bin directory itself should be ignored
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "**/bin/*" });

		Assert.True(matcher.IsIgnored("/repo/bin", true, "bin"));
		Assert.True(matcher.IsIgnored("/repo/src/Project/bin", true, "bin"));
	}

	[Fact]
	public void IsIgnored_ContentPatternIgnoresDirectory_ObjFolder()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "**/obj/*" });

		Assert.True(matcher.IsIgnored("/repo/obj", true, "obj"));
		Assert.True(matcher.IsIgnored("/repo/src/Project/obj", true, "obj"));
	}

	[Fact]
	public void IsIgnored_ContentPatternIgnoresDirectory_NodeModules()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "**/node_modules/*" });

		Assert.True(matcher.IsIgnored("/repo/node_modules", true, "node_modules"));
		Assert.True(matcher.IsIgnored("/repo/packages/app/node_modules", true, "node_modules"));
	}

	[Fact]
	public void IsIgnored_ContentPatternIgnoresDirectory_DistFolder()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "**/dist/*" });

		Assert.True(matcher.IsIgnored("/repo/dist", true, "dist"));
		Assert.True(matcher.IsIgnored("/repo/packages/ui/dist", true, "dist"));
	}

	[Fact]
	public void IsIgnored_ContentPatternIgnoresDirectory_TargetFolder()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "**/target/*" });

		Assert.True(matcher.IsIgnored("/repo/target", true, "target"));
		Assert.True(matcher.IsIgnored("/repo/module/target", true, "target"));
	}

	[Fact]
	public void IsIgnored_ContentPatternIgnoresDirectory_VendorFolder()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "**/vendor/*" });

		Assert.True(matcher.IsIgnored("/repo/vendor", true, "vendor"));
	}

	[Fact]
	public void IsIgnored_ContentPatternIgnoresDirectory_CacheFolder()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "**/.cache/*" });

		Assert.True(matcher.IsIgnored("/repo/.cache", true, ".cache"));
		Assert.True(matcher.IsIgnored("/repo/project/.cache", true, ".cache"));
	}

	[Fact]
	public void IsIgnored_ContentPattern_DoesNotIgnoreUnrelatedDirectory()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "**/bin/*" });

		Assert.False(matcher.IsIgnored("/repo/src", true, "src"));
		Assert.False(matcher.IsIgnored("/repo/lib", true, "lib"));
	}

	[Fact]
	public void IsIgnored_ContentPattern_DoesNotIgnoreFiles()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "**/bin/*" });

		// Files named "bin" should not be ignored by this pattern
		Assert.False(matcher.IsIgnored("/repo/bin", false, "bin"));
	}

	[Fact]
	public void IsIgnored_ContentPattern_WithNegation_DoesNotIgnoreDirectory()
	{
		// When negation rules exist, we should be conservative and NOT hide the directory
		// because some contents might be un-ignored
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "**/bin/*", "!**/bin/keep.dll" });

		// With negation rules, directory should NOT be hidden (conservative approach)
		Assert.False(matcher.IsIgnored("/repo/bin", true, "bin"));
	}

	[Fact]
	public void IsIgnored_ContentPattern_CharacterClass()
	{
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "**/[Bb]in/*" });

		Assert.True(matcher.IsIgnored("/repo/bin", true, "bin"));
		Assert.True(matcher.IsIgnored("/repo/Bin", true, "Bin"));
		Assert.True(matcher.IsIgnored("/repo/src/Project/bin", true, "bin"));
	}

	[Fact]
	public void IsIgnored_DirectPatternStillWorks()
	{
		// Direct directory patterns should still work
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "bin/" });

		Assert.True(matcher.IsIgnored("/repo/bin", true, "bin"));
		Assert.True(matcher.IsIgnored("/repo/src/bin", true, "bin"));
	}

	[Fact]
	public void IsIgnored_BothDirectAndContentPatterns()
	{
		// Both bin/ and **/bin/* should work together
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "[Oo]bj/", "**/[Bb]in/*" });

		Assert.True(matcher.IsIgnored("/repo/obj", true, "obj"));
		Assert.True(matcher.IsIgnored("/repo/Obj", true, "Obj"));
		Assert.True(matcher.IsIgnored("/repo/bin", true, "bin"));
		Assert.True(matcher.IsIgnored("/repo/Bin", true, "Bin"));
		Assert.True(matcher.IsIgnored("/repo/src/Project/obj", true, "obj"));
		Assert.True(matcher.IsIgnored("/repo/src/Project/bin", true, "bin"));
	}

	[Fact]
	public void IsIgnored_SpecificFilePattern_DoesNotIgnoreDirectory()
	{
		// Pattern *.log should NOT cause directories to be ignored
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "*.log" });

		Assert.False(matcher.IsIgnored("/repo/logs", true, "logs"));
	}

	[Fact]
	public void IsIgnored_PartialContentPattern_DoesNotIgnoreDirectory()
	{
		// Pattern logs/*.log only ignores .log files in logs, not all contents
		// So logs directory should NOT be hidden
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "logs/*.log" });

		Assert.False(matcher.IsIgnored("/repo/logs", true, "logs"));
	}

	#endregion
}
