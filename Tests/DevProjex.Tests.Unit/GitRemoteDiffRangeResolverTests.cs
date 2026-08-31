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
