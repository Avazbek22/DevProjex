namespace DevProjex.Tests.Unit;

public sealed class IgnoreRuleSemanticsTests
{
	[Fact]
	public void ShouldIgnoreHiddenDirectory_DotFolderWithDotRuleEnabled_DoesNotDoubleOwnHiddenOverlap()
	{
		var ignored = IgnoreRuleSemantics.ShouldIgnoreHiddenDirectory(
			ignoreHiddenFolders: true,
			isHidden: true,
			isDot: true,
			ignoreDotFolders: true);

		Assert.False(ignored);
	}

	[Fact]
	public void ShouldIgnoreHiddenDirectory_NonDotHiddenFolder_AppliesHiddenRule()
	{
		var ignored = IgnoreRuleSemantics.ShouldIgnoreHiddenDirectory(
			ignoreHiddenFolders: true,
			isHidden: true,
			isDot: false,
			ignoreDotFolders: false);

		Assert.True(ignored);
	}

	[Fact]
	public void ShouldIgnoreHiddenDirectory_DotFolderWithDotRuleDisabled_UsesPlatformHiddenSemantics()
	{
		var ignored = IgnoreRuleSemantics.ShouldIgnoreHiddenDirectory(
			ignoreHiddenFolders: true,
			isHidden: true,
			isDot: true,
			ignoreDotFolders: false);

		Assert.Equal(OperatingSystem.IsWindows(), ignored);
	}

	[Fact]
	public void ShouldIgnoreHiddenFile_DotFileWithDotRuleDisabled_UsesPlatformHiddenSemantics()
	{
		var ignored = IgnoreRuleSemantics.ShouldIgnoreHiddenFile(
			ignoreHiddenFiles: true,
			isHidden: true,
			isDot: true,
			ignoreDotFiles: false);

		Assert.Equal(OperatingSystem.IsWindows(), ignored);
	}
}
