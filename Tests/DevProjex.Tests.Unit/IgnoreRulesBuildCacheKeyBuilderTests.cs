namespace DevProjex.Tests.Unit;

public sealed class IgnoreRulesBuildCacheKeyBuilderTests
{
	public static IEnumerable<object[]> EquivalentSelectionCases()
	{
		yield return
		[
			new[] { IgnoreOptionId.DotFolders, IgnoreOptionId.UseGitIgnore },
			new[] { IgnoreOptionId.UseGitIgnore, IgnoreOptionId.DotFolders, IgnoreOptionId.UseGitIgnore },
			new[] { "src", "docs" },
			new[] { "docs", "src", "src" }
		];
		yield return
		[
			new[] { IgnoreOptionId.ExtensionlessFiles, IgnoreOptionId.EmptyFiles, IgnoreOptionId.DotFiles },
			new[] { IgnoreOptionId.DotFiles, IgnoreOptionId.ExtensionlessFiles, IgnoreOptionId.EmptyFiles },
			new[] { " api ", "web", "" },
			new[] { "web", " api ", "" }
		];
	}

	[Theory]
	[MemberData(nameof(EquivalentSelectionCases))]
	public void Build_EquivalentOptionAndRootSets_ProduceSameKey(
		IgnoreOptionId[] firstOptions,
		IgnoreOptionId[] secondOptions,
		string[] firstRoots,
		string[] secondRoots)
	{
		var first = IgnoreRulesBuildCacheKeyBuilder.Build(
			@"C:\Workspace\Project",
			firstOptions,
			firstRoots);
		var second = IgnoreRulesBuildCacheKeyBuilder.Build(
			@"C:\Workspace\Project",
			secondOptions,
			secondRoots);

		Assert.Equal(first, second);
	}

	[Fact]
	public void Build_NullEmptyAndWhitespaceRootSelections_HaveDistinctMeaning()
	{
		var nullRoots = IgnoreRulesBuildCacheKeyBuilder.Build("project", [], selectedRootFolders: null);
		var emptyRoots = IgnoreRulesBuildCacheKeyBuilder.Build("project", [], []);
		var whitespaceRoots = IgnoreRulesBuildCacheKeyBuilder.Build("project", [], ["", " "]);

		Assert.NotEqual(nullRoots, emptyRoots);
		Assert.NotEqual(emptyRoots, whitespaceRoots);
	}

	[Fact]
	public void Build_DelimiterAndSentinelLikeRootNames_NeverCollide()
	{
		var firstDelimitedSet = IgnoreRulesBuildCacheKeyBuilder.Build(
			"project",
			[IgnoreOptionId.UseGitIgnore],
			["a", "b|c"]);
		var secondDelimitedSet = IgnoreRulesBuildCacheKeyBuilder.Build(
			"project",
			[IgnoreOptionId.UseGitIgnore],
			["a|b", "c"]);
		var sentinelLikeName = IgnoreRulesBuildCacheKeyBuilder.Build(
			"project",
			[IgnoreOptionId.UseGitIgnore],
			["<null>"]);
		var nullSelection = IgnoreRulesBuildCacheKeyBuilder.Build(
			"project",
			[IgnoreOptionId.UseGitIgnore],
			selectedRootFolders: null);

		Assert.NotEqual(firstDelimitedSet, secondDelimitedSet);
		Assert.NotEqual(sentinelLikeName, nullSelection);
	}

	[Fact]
	public void Build_LeadingAndTrailingWhitespaceRemainPartOfRootIdentity()
	{
		var exact = IgnoreRulesBuildCacheKeyBuilder.Build(
			"project",
			[IgnoreOptionId.DotFolders],
			[" source "]);
		var trimmed = IgnoreRulesBuildCacheKeyBuilder.Build(
			"project",
			[IgnoreOptionId.DotFolders],
			["source"]);

		Assert.NotEqual(exact, trimmed);
	}

	[Fact]
	public void Build_OptionSetChange_ChangesKeyEvenWhenPathAndRootsAreSame()
	{
		var dotOnly = IgnoreRulesBuildCacheKeyBuilder.Build(
			"project",
			[IgnoreOptionId.DotFolders],
			["src"]);
		var dotAndGit = IgnoreRulesBuildCacheKeyBuilder.Build(
			"project",
			[IgnoreOptionId.DotFolders, IgnoreOptionId.UseGitIgnore],
			["src"]);

		Assert.NotEqual(dotOnly, dotAndGit);
	}

	[Fact]
	public void Build_RootSetChange_ChangesKeyEvenWhenPathAndOptionsAreSame()
	{
		var srcOnly = IgnoreRulesBuildCacheKeyBuilder.Build(
			"project",
			[IgnoreOptionId.SmartIgnore],
			["src"]);
		var srcAndDocs = IgnoreRulesBuildCacheKeyBuilder.Build(
			"project",
			[IgnoreOptionId.SmartIgnore],
			["src", "docs"]);

		Assert.NotEqual(srcOnly, srcAndDocs);
	}

	[Fact]
	public void Build_RootSelectionCaseSensitivity_FollowsPlatformComparer()
	{
		var lower = IgnoreRulesBuildCacheKeyBuilder.Build(
			@"C:\Workspace\Project",
			[IgnoreOptionId.DotFolders],
			["src"]);
		var upper = IgnoreRulesBuildCacheKeyBuilder.Build(
			@"C:\Workspace\Project",
			[IgnoreOptionId.DotFolders],
			["SRC"]);

		Assert.Equal(OperatingSystem.IsWindows(), string.Equals(lower, upper, StringComparison.Ordinal));
	}

	[Fact]
	public void Build_RelativePathSegments_NormalizeBeforeBuildingKey()
	{
		var first = IgnoreRulesBuildCacheKeyBuilder.Build(
			Path.Combine("project", "..", "project"),
			[IgnoreOptionId.EmptyFiles, IgnoreOptionId.DotFiles],
			["src", "src"]);
		var second = IgnoreRulesBuildCacheKeyBuilder.Build(
			"project",
			[IgnoreOptionId.DotFiles, IgnoreOptionId.EmptyFiles],
			["src"]);

		Assert.Equal(first, second);
	}
}
