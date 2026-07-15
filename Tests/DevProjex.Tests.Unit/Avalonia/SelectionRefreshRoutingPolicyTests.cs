namespace DevProjex.Tests.Unit.Avalonia;

public sealed class SelectionRefreshRoutingPolicyTests
{
    [Theory]
    [InlineData(IgnoreOptionId.HiddenFiles)]
    [InlineData(IgnoreOptionId.DotFiles)]
    [InlineData(IgnoreOptionId.EmptyFiles)]
    [InlineData(IgnoreOptionId.ExtensionlessFiles)]
    public void CanUseLiveOptionsRefresh_FileVisibilityOptions_ReturnsTrue(IgnoreOptionId optionId)
    {
        var result = SelectionRefreshRoutingPolicy.CanUseLiveOptionsRefresh(optionId);

        Assert.True(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(IgnoreOptionId.HiddenFolders)]
    [InlineData(IgnoreOptionId.DotFolders)]
    [InlineData(IgnoreOptionId.EmptyFolders)]
    [InlineData(IgnoreOptionId.UseGitIgnore)]
    [InlineData(IgnoreOptionId.SmartIgnore)]
    public void CanUseLiveOptionsRefresh_RootStructureOrUnknownOptions_ReturnsFalse(IgnoreOptionId? optionId)
    {
        var result = SelectionRefreshRoutingPolicy.CanUseLiveOptionsRefresh(optionId);

        Assert.False(result);
    }
}
