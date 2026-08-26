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
			RepositoryUrlUtility.GetComparisonKey(right));
	}

	[Theory]
	[InlineData("https://GITHUB.com/Owner/Repo.git", "https://github.com/owner/repo")]
	[InlineData("https://gitlab.com/Group/Repo.git", "git@gitlab.com:group/repo.git")]
	[InlineData("https://BITBUCKET.org/Team/Repo.git", "https://bitbucket.org/team/repo")]
	public void KnownRepositoryHostsUseCaseInsensitivePaths(string left, string right)
	{
		Assert.True(RepositoryUrlUtility.AreEquivalent(left, right));
		Assert.Equal(
			RepositoryUrlUtility.GetComparisonKey(left),
			RepositoryUrlUtility.GetComparisonKey(right));
	}

	[Fact]
	public void SelfHostedRepositoryUsesCaseInsensitiveHostAndCaseSensitivePath()
	{
		const string canonical = "https://Git.Example.test/Owner/Repo.git";

		Assert.True(RepositoryUrlUtility.AreEquivalent(
			canonical,
			"https://git.example.test/Owner/Repo"));
		Assert.False(RepositoryUrlUtility.AreEquivalent(
			canonical,
			"https://git.example.test/owner/repo"));
		Assert.False(RepositoryUrlUtility.AreEquivalent(
			canonical,
			"https://git.example.test/Owner/Repo.GIT"));
	}

	[Fact]
	public void ComparisonIdentityCarriesItsVersion()
	{
		Assert.StartsWith(
			"v2:",
			RepositoryUrlUtility.GetComparisonKey("https://example.com/Owner/Repo.git"),
			StringComparison.Ordinal);
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

	[Theory]
	[InlineData("https://user:super-secret@[invalid/repo")]
	[InlineData("ssh://user:super-secret@")]
	[InlineData("user:super-secret@example.com:repo")]
	public void SafeDisplayRejectsMalformedSourcesInsteadOfReturningCredentials(string source)
	{
		var display = RepositoryUrlUtility.ToSafeDisplay(source);

		Assert.Empty(display);
		Assert.DoesNotContain("super-secret", display, StringComparison.Ordinal);
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

	[Fact]
	public void LocalRepositoryIdentityUsesPlatformPathCaseSemantics()
	{
		var upperPath = Path.Combine(Path.GetTempPath(), "DevProjex", "CaseIdentity", "Repo.git");
		var lowerPath = Path.Combine(Path.GetTempPath(), "DevProjex", "CaseIdentity", "repo.git");
		var upperUri = new Uri(upperPath).AbsoluteUri;
		var lowerUri = new Uri(lowerPath).AbsoluteUri;

		Assert.Equal(
			OperatingSystem.IsWindows(),
			RepositoryUrlUtility.AreEquivalent(upperUri, lowerUri));
		Assert.Equal(
			OperatingSystem.IsWindows(),
			RepositoryUrlUtility.AreEquivalent(upperPath, lowerPath));
	}

	[Fact]
	public void LocalRepositoryIdentityStillIgnoresGitSuffix()
	{
		var repositoryPath = Path.Combine(Path.GetTempPath(), "DevProjex", "LocalIdentity", "repo");

		Assert.True(RepositoryUrlUtility.AreEquivalent(
			new Uri(repositoryPath).AbsoluteUri,
			new Uri(repositoryPath + ".git").AbsoluteUri));
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
