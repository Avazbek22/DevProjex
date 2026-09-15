using DevProjex.Infrastructure.Git;

namespace DevProjex.Tests.Unit;

public sealed class GitNetworkPolicyTests
{
	[Theory]
	[InlineData("https://example.test/team/repository.git")]
	[InlineData("ssh://git@example.test/team/repository.git")]
	[InlineData("git@example.test:team/repository.git")]
	[InlineData("example.test:team/repository.git")]
	public void SupportedRemoteUrlsAreReturnedUnchanged(string url) =>
		Assert.Equal(url, GitNetworkPolicy.ValidateUrl(url));

	[Theory]
	[InlineData("http://example.test/team/repository.git")]
	[InlineData("git://example.test/team/repository.git")]
	[InlineData("ext::sh -c whoami")]
	public void UnsupportedRemoteUrlsAreRejected(string url) =>
		Assert.Throws<ArgumentException>(() => GitNetworkPolicy.ValidateUrl(url));

	[Theory]
	[InlineData("file:///C:/repository.git")]
	[InlineData("file://server/share/repository.git")]
	[InlineData("file:/C:/repository.git")]
	[InlineData("file:C:/repository.git")]
	[InlineData(" C:/repository.git")]
	public void FileUrlsAreRejectedWithoutTheLocalTransport(string url)
	{
		Assert.Throws<ArgumentException>(() => GitNetworkPolicy.ValidateUrl(url));
		Assert.Throws<ArgumentException>(() => GitNetworkPolicy.ValidateUrl(url, allowFileTransport: false));
	}

	/// <summary>
	/// The invariant that matters for a shipped build: whatever survives validation without the
	/// local transport must not select the file protocol for the Git invocation.
	/// </summary>
	[Theory]
	[InlineData("https://example.test/team/repository.git", "https")]
	[InlineData("ssh://git@example.test/team/repository.git", "ssh")]
	[InlineData("git@example.test:team/repository.git", "ssh")]
	[InlineData("example.test:team/repository.git", "ssh")]
	[InlineData("file:repository.git", "ssh")]
	public void AcceptedUrlsNeverSelectTheFileProtocolWithoutTheLocalTransport(
		string url,
		string expectedProtocol)
	{
		Assert.Equal(url, GitNetworkPolicy.ValidateUrl(url));
		Assert.Equal(expectedProtocol, GitNetworkPolicy.GetAllowedProtocols(url));
	}

	[Theory]
	[InlineData("file:///C:/repository.git")]
	[InlineData("file:/C:/repository.git")]
	[InlineData("file:C:/repository.git")]
	public void FileUrlsAreReturnedWithTheLocalTransport(string url) =>
		Assert.Equal(url, GitNetworkPolicy.ValidateUrl(url, allowFileTransport: true));

	[Theory]
	[InlineData("C:/repository.git")]
	[InlineData("C:\\repository.git")]
	[InlineData("/home/user/repository.git")]
	[InlineData("//server/share/repository.git")]
	[InlineData("\\\\server\\share\\repository.git")]
	public void LocalFilesystemPathsAreNotRemoteUrls(string url) =>
		Assert.Throws<ArgumentException>(() => GitNetworkPolicy.ValidateUrl(url));
}
