namespace DevProjex.Tests.Unit;

public sealed class RepositoryUrlUtilityTests
{
	[Theory]
	[InlineData("https://github.com/owner/DevProjex.git", "DevProjex")]
	[InlineData("git@github.com:owner/DevProjex.git", "DevProjex")]
	[InlineData("ssh://git@github.com/owner/%D0%9F%D1%80%D0%BE%D0%B5%D0%BA%D1%82.git", "Проект")]
	public void RepositoryNameIsTransportIndependent(string url, string expected)
	{
		Assert.Equal(expected, RepositoryUrlUtility.GetRepositoryName(url));
	}

	[Theory]
	[InlineData(
		"https://github.com/owner/DevProjex.git",
		"git@github.com:owner/DevProjex.git")]
	[InlineData(
		"ssh://git@github.com/owner/DevProjex",
		"https://github.com/owner/DevProjex")]
	[InlineData(
		"https://GITHUB.com/owner/DevProjex?token=secret",
		"https://github.com/owner/DevProjex#fragment")]
	public void EquivalentRepositoryFormsShareOneComparisonIdentity(
		string left,
		string right)
	{
		Assert.True(RepositoryUrlUtility.AreEquivalent(left, right));
		Assert.Equal(
			RepositoryUrlUtility.GetComparisonKey(left),
			RepositoryUrlUtility.GetComparisonKey(right),
			ignoreCase: true);
	}

	[Fact]
	public void SafeDisplayRemovesCredentialsQueryAndFragment()
	{
		var display = RepositoryUrlUtility.ToSafeDisplay(
			"https:" + "//user:super-secret@example.com/owner/repo.git?access_token=hidden#fragment");

		Assert.Equal("https://example.com/owner/repo.git", display);
		Assert.DoesNotContain("super-secret", display, StringComparison.Ordinal);
		Assert.DoesNotContain("access_token", display, StringComparison.Ordinal);
	}

	[Fact]
	public void SafeDisplayPreservesFileUriIdentityAcrossPlatforms()
	{
		using var temporary = new TemporaryDirectory();
		var repositoryUrl = new Uri(temporary.Path).AbsoluteUri;

		var display = RepositoryUrlUtility.ToSafeDisplay(repositoryUrl);

		Assert.StartsWith("file://", display, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(repositoryUrl.TrimEnd('/'), display);
	}

	[Theory]
	[InlineData("")]
	[InlineData("-uploader")]
	[InlineData("https://example.com/owner/repo.git\" --upload-pack=evil")]
	[InlineData("https://example.com/owner/repo.git\nAuthorization: secret")]
	[InlineData("ftp://example.com/repo.git")]
	public void UnsafeOrUnsupportedCloneSourcesAreRejected(string source)
	{
		Assert.False(RepositoryUrlUtility.IsSupportedCloneSource(source));
	}

	[Theory]
	[InlineData("https://example.com/owner/repo.git")]
	[InlineData("ssh://git@example.com/owner/repo.git")]
	[InlineData("git@example.com:owner/repo.git")]
	[InlineData("git://example.com/owner/repo.git")]
	public void SupportedRemoteCloneSourcesAreAccepted(string source)
	{
		Assert.True(RepositoryUrlUtility.IsSupportedCloneSource(source));
	}

	[Fact]
	public void ExistingAbsoluteFolderIsAcceptedAsLocalCloneSource()
	{
		using var temporary = new TemporaryDirectory();

		Assert.True(RepositoryUrlUtility.IsSupportedCloneSource(temporary.Path));
	}
}
