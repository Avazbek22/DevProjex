namespace DevProjex.Tests.Unit;

public sealed class RepositoryWebPathPresentationServiceTests
{
	[Theory]
	[InlineData("https://github.com/user/repo.git", "https://github.com/user/repo")]
	[InlineData("https://github.com/user/repo.git/", "https://github.com/user/repo")]
	[InlineData("https://github.com/user/repo?tab=readme#top", "https://github.com/user/repo")]
	[InlineData("https:" + "//user:token@github.com/user/repo.git?tab=readme#top", "https://github.com/user/repo")]
	[InlineData("github.com/user/repo.git/", "github.com/user/repo")]
	public void NormalizeForDisplay_ReturnsCleanRepositoryUrl(string repositoryUrl, string expected)
	{
		var normalized = RepositoryWebPathPresentationService.NormalizeForDisplay(repositoryUrl);

		Assert.Equal(expected, normalized);
	}

	[Fact]
	public void NormalizeForDisplay_ReturnsEmpty_ForNullOrWhitespace()
	{
		Assert.Equal(string.Empty, RepositoryWebPathPresentationService.NormalizeForDisplay(null!));
		Assert.Equal(string.Empty, RepositoryWebPathPresentationService.NormalizeForDisplay(""));
		Assert.Equal(string.Empty, RepositoryWebPathPresentationService.NormalizeForDisplay("   "));
	}

	[Fact]
	public void NormalizeForDisplay_RejectsMalformedCredentialBearingUrl()
	{
		var normalized = RepositoryWebPathPresentationService.NormalizeForDisplay(
			"https://user:super-secret@[invalid/repo");

		Assert.Empty(normalized);
		Assert.DoesNotContain("super-secret", normalized, StringComparison.Ordinal);
	}

	[Fact]
	public void TryCreate_ReturnsNull_ForInvalidInputs()
	{
		var service = new RepositoryWebPathPresentationService();
		var repoRoot = BuildAbsolutePath("repo");

		Assert.Null(service.TryCreate("", "https://github.com/user/repo"));
		Assert.Null(service.TryCreate(repoRoot, ""));
		Assert.Null(service.TryCreate(repoRoot, "not-a-url"));
		Assert.Null(service.TryCreate(repoRoot, "ftp://github.com/user/repo"));
	}

	[Fact]
	public void TryCreate_BuildsCleanRootUrl_WithoutCredentialsQueryFragmentAndDotGit()
	{
		var service = new RepositoryWebPathPresentationService();
		var repoRoot = BuildAbsolutePath("work", "repo");
		var presentation = service.TryCreate(
			repoRoot,
			"https:" + "//user:token@github.com/Avazbek22/DevProjex.git?tab=readme#top");

		Assert.NotNull(presentation);
		Assert.Equal("https://github.com/Avazbek22/DevProjex", presentation!.DisplayRootPath);
		Assert.Equal("DevProjex", presentation.DisplayRootName);
	}

	[Fact]
	public void TryCreate_MapsNestedFilePath_ToCleanWebPath()
	{
		var service = new RepositoryWebPathPresentationService();
		var repoRoot = BuildAbsolutePath("work", "repo");
		var presentation = service.TryCreate(
			repoRoot,
			"https://github.com/Avazbek22/DevProjex.git");

		Assert.NotNull(presentation);
		var mapped = presentation!.MapFilePath(Path.Combine(repoRoot, "src", "MainWindow.axaml.cs"));

		Assert.Equal("https://github.com/Avazbek22/DevProjex/src/MainWindow.axaml.cs", mapped);
	}

	[Fact]
	public void TryCreate_DotDotPrefixedDirectoryRemainsInsideRepository()
	{
		var service = new RepositoryWebPathPresentationService();
		var repoRoot = BuildAbsolutePath("work", "repo");
		var presentation = service.TryCreate(
			repoRoot,
			"https://github.com/Avazbek22/DevProjex.git");

		Assert.NotNull(presentation);
		var mapped = presentation!.MapFilePath(Path.Combine(repoRoot, "..cache", "state.json"));

		Assert.Equal("https://github.com/Avazbek22/DevProjex/..cache/state.json", mapped);
	}

	[Fact]
	public void TryCreate_WhitespaceOnlyFileNameRemainsAFilePath()
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip("Windows normalizes this file name through ordinary path APIs.");
			return;
		}

		var service = new RepositoryWebPathPresentationService();
		var repoRoot = BuildAbsolutePath("work", "repo");
		var presentation = service.TryCreate(
			repoRoot,
			"https://github.com/Avazbek22/DevProjex.git");

		Assert.NotNull(presentation);
		var mapped = presentation!.MapFilePath(Path.Combine(repoRoot, " "));

		Assert.Equal("https://github.com/Avazbek22/DevProjex/%20", mapped);
	}

	[Theory]
	[InlineData("https://github.com/user/repo", "repo")]
	[InlineData("https://github.com/user/repo/", "repo")]
	[InlineData("https://github.com/user/repo.git", "repo")]
	[InlineData("https://gitlab.com/group/subgroup/project", "project")]
	[InlineData("https://bitbucket.org/team/project-name", "project-name")]
	[InlineData("https://example.com/scm/repositories/Alpha_123", "Alpha_123")]
	[InlineData("https://example.com/a/b/c/d", "d")]
	[InlineData("https://example.com/a/My.Repo", "My.Repo")]
	[InlineData("https://example.com/a/repo~name", "repo~name")]
	[InlineData("https://example.com/a/repo-name.git", "repo-name")]
	[InlineData("https://example.com/a/repo_name.git", "repo_name")]
	[InlineData("https://example.com/a/repo%20name", "repo name")]
	[InlineData("https://example.com/a/%D0%A2%D0%B5%D1%81%D1%82", "Тест")]
	[InlineData("https://example.com/a/repo?tab=readme", "repo")]
	[InlineData("https://example.com/a/repo#top", "repo")]
	public void TryCreate_ExtractsDisplayRootName_FromRepositoryUrl(string repositoryUrl, string expectedName)
	{
		var service = new RepositoryWebPathPresentationService();
		var presentation = service.TryCreate(BuildAbsolutePath("work", "repo"), repositoryUrl);

		Assert.NotNull(presentation);
		Assert.Equal(expectedName, presentation!.DisplayRootName);
	}

	[Fact]
	public void TryCreate_LeavesDisplayRootNameNull_WhenRepositoryPathHasNoSegments()
	{
		var service = new RepositoryWebPathPresentationService();
		var presentation = service.TryCreate(BuildAbsolutePath("work", "repo"), "https://example.com");

		Assert.NotNull(presentation);
		Assert.Null(presentation!.DisplayRootName);
	}

	[Fact]
	public void TryCreate_MapsRootPathToRepositoryRootUrl()
	{
		var service = new RepositoryWebPathPresentationService();
		var repoRoot = BuildAbsolutePath("work", "repo");
		var presentation = service.TryCreate(
			repoRoot,
			"https://github.com/Avazbek22/DevProjex");

		Assert.NotNull(presentation);
		var mapped = presentation!.MapFilePath(repoRoot);

		Assert.Equal("https://github.com/Avazbek22/DevProjex", mapped);
	}

	[Fact]
	public void TryCreate_EncodesUnsafeSegmentsInFilePath()
	{
		var service = new RepositoryWebPathPresentationService();
		var repoRoot = BuildAbsolutePath("work", "repo");
		var presentation = service.TryCreate(
			repoRoot,
			"https://github.com/Avazbek22/DevProjex");

		Assert.NotNull(presentation);
		var mapped = presentation!.MapFilePath(Path.Combine(repoRoot, "Docs", "Мой файл #1.txt"));

		Assert.Contains("/Docs/", mapped, StringComparison.Ordinal);
		Assert.Contains("%D0%9C%D0%BE%D0%B9%20%D1%84%D0%B0%D0%B9%D0%BB%20%231.txt", mapped, StringComparison.Ordinal);
	}

	[Fact]
	public void TryCreate_PathOutsideRoot_ReturnsOriginalPath()
	{
		var service = new RepositoryWebPathPresentationService();
		var repoRoot = BuildAbsolutePath("work", "repo");
		var presentation = service.TryCreate(
			repoRoot,
			"https://github.com/Avazbek22/DevProjex");

		Assert.NotNull(presentation);
		var external = BuildAbsolutePath("other", "external.txt");
		var mapped = presentation!.MapFilePath(external);

		Assert.Equal(external, mapped);
	}

	private static string BuildAbsolutePath(params string[] segments)
		=> Path.Combine([Path.GetTempPath(), "DevProjexTests", "RepositoryWebPathPresentationService", .. segments]);
}
