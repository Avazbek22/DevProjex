using System;
using DevProjex.Kernel.Models;
using Xunit;

namespace DevProjex.Tests.Unit;

public sealed class GitIgnoreMatcherTests
{
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

	[Fact]
	public void IsIgnored_CharacterClassInPattern_MatchesBothCases()
	{
		// Pattern [Oo]bj/ should match both "obj" and "Obj"
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "[Oo]bj/" });

		Assert.True(matcher.IsIgnored("/repo/obj", isDirectory: true, "obj"));
		Assert.True(matcher.IsIgnored("/repo/Obj", isDirectory: true, "Obj"));
		Assert.False(matcher.IsIgnored("/repo/xbj", isDirectory: true, "xbj"));
	}

	[Fact]
	public void IsIgnored_CharacterClassWithDoubleAsterisk_MatchesBinDirectories()
	{
		// Pattern **/[Bb]in/* should match bin and Bin anywhere in the tree
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "**/[Bb]in/*" });

		Assert.True(matcher.IsIgnored("/repo/bin/Debug", isDirectory: true, "Debug"));
		Assert.True(matcher.IsIgnored("/repo/src/MyProject/bin/Release", isDirectory: true, "Release"));
		Assert.True(matcher.IsIgnored("/repo/Bin/Debug", isDirectory: true, "Debug"));
		Assert.False(matcher.IsIgnored("/repo/xbin/Debug", isDirectory: true, "Debug"));
	}

	[Fact]
	public void IsIgnored_StandardGitIgnoreObjPattern_MatchesObjDirectories()
	{
		// Standard .gitignore pattern from VisualStudio.gitignore
		var matcher = GitIgnoreMatcher.Build("/repo", new[] { "[Oo]bj/" });

		Assert.True(matcher.IsIgnored("/repo/obj", isDirectory: true, "obj"));
		Assert.True(matcher.IsIgnored("/repo/src/MyProject/obj", isDirectory: true, "obj"));
		Assert.True(matcher.IsIgnored("/repo/Obj", isDirectory: true, "Obj"));
	}
}
