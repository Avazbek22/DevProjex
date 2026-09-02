using DevProjex.Infrastructure.Git;

namespace DevProjex.Tests.Unit;

public sealed class GitRemoteDiffRangeResolverTests
{
	[Theory]
	[InlineData("main", true, "main")]
	[InlineData("origin/main", true, "main")]
	[InlineData("refs/remotes/origin/main", true, "refs/heads/main")]
	[InlineData("origin/--upload-pack=helper", false, "--upload-pack=helper")]
	[InlineData("origin/-c", false, "-c")]
	[InlineData("main:refs/heads/injected", false, "main:refs/heads/injected")]
	[InlineData(":refs/heads/injected", false, ":refs/heads/injected")]
	[InlineData("main?", false, "main?")]
	[InlineData("main*", false, "main*")]
	[InlineData("refs/tags/v1", true, "refs/tags/v1")]
	[InlineData("0123456789abcdef0123456789abcdef01234567", true, "0123456789abcdef0123456789abcdef01234567")]
	public void RemoteReferenceNormalizationCannotCreateGitOptions(
		string reference,
		bool expected,
		string normalized)
	{
		Assert.Equal(
			expected,
			GitRemoteDiffRangeResolver.TryNormalizeRemoteReference(reference, out var actual));
		Assert.Equal(normalized, actual);
	}
}
