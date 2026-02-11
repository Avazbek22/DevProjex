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
}
