using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

/// <summary>
/// The classifier is a pure string function, so these cases settle what it answers without any
/// path existing, any host being reachable, or any call waiting on one.
/// </summary>
public sealed class McpRemoteProviderPathTests
{
	[Theory]
	[InlineData("\\\\server\\share")]
	[InlineData("\\\\server\\share\\repository")]
	[InlineData("//server/share")]
	[InlineData("//server/share/repository")]
	[InlineData("\\\\server")]
	[InlineData("//server")]
	[InlineData("\\\\?\\UNC\\server\\share")]
	[InlineData("\\\\.\\UNC\\server\\share")]
	[InlineData("\\\\?\\C:\\temp")]
	[InlineData("\\\\.\\pipe\\name")]
	[InlineData("\\/server/share")]
	[InlineData("/\\server\\share")]
	[InlineData("\\\\\\\\server\\share")]
	[InlineData("  \\\\server\\share")]
	[InlineData("//")]
	public void RemoteAndDeviceFormsAreRecognised(string path) =>
		Assert.True(McpRemoteProviderPath.ReachesRemoteProvider(path));

	[Theory]
	[InlineData("C:\\repository")]
	[InlineData("C:/repository")]
	[InlineData("/home/user/repository")]
	[InlineData("/")]
	[InlineData("repository")]
	[InlineData("./repository")]
	[InlineData("../repository")]
	[InlineData(".")]
	[InlineData("src/models")]
	[InlineData("src\\models")]
	[InlineData("https://example.test/team/repository.git")]
	[InlineData("ssh://git@example.test/team/repository.git")]
	[InlineData("git@example.test:team/repository.git")]
	[InlineData("example.test:team/repository.git")]
	[InlineData("")]
	[InlineData("   ")]
	public void OrdinaryPathsAndUrlsAreNotRemoteForms(string path) =>
		Assert.False(McpRemoteProviderPath.ReachesRemoteProvider(path));

	[Fact]
	public void AMissingValueIsNotARemoteForm() =>
		Assert.False(McpRemoteProviderPath.ReachesRemoteProvider(null));

	/// <summary>
	/// A separator pair anywhere other than the start is ordinary text: the scheme separator in a
	/// url and a doubled separator inside a path both have to stay accepted.
	/// </summary>
	[Theory]
	[InlineData("C:\\repository\\\\nested")]
	[InlineData("/home/user//nested")]
	[InlineData("http://example.test/x")]
	public void ASeparatorPairAwayFromTheStartIsNotARemoteForm(string path) =>
		Assert.False(McpRemoteProviderPath.ReachesRemoteProvider(path));
}
