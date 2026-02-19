namespace DevProjex.Tests.Unit;

public sealed class RepositoryWebPathPresentationServiceTests
{
	[Fact]
	public void TryCreate_ReturnsNull_ForInvalidInputs()
	{
		var service = new RepositoryWebPathPresentationService();

		Assert.Null(service.TryCreate("", "https://github.com/user/repo"));
		Assert.Null(service.TryCreate("C:\\repo", ""));
		Assert.Null(service.TryCreate("C:\\repo", "not-a-url"));
		Assert.Null(service.TryCreate("C:\\repo", "ftp://github.com/user/repo"));
	}

	[Fact]
	public void TryCreate_BuildsCleanRootUrl_WithoutCredentialsQueryFragmentAndDotGit()
	{
		var service = new RepositoryWebPathPresentationService();
		var presentation = service.TryCreate(
			@"C:\work\repo",
			"https://user:token@github.com/Avazbek22/DevProjex.git?tab=readme#top");

		Assert.NotNull(presentation);
		Assert.Equal("https://github.com/Avazbek22/DevProjex", presentation!.DisplayRootPath);
		Assert.Equal("DevProjex", presentation.DisplayRootName);
	}

	[Fact]
	public void TryCreate_MapsNestedFilePath_ToCleanWebPath()
	{
		var service = new RepositoryWebPathPresentationService();
		var presentation = service.TryCreate(
			@"C:\work\repo",
			"https://github.com/Avazbek22/DevProjex.git");

		Assert.NotNull(presentation);
		var mapped = presentation!.MapFilePath(@"C:\work\repo\src\MainWindow.axaml.cs");

		Assert.Equal("https://github.com/Avazbek22/DevProjex/src/MainWindow.axaml.cs", mapped);
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
		var presentation = service.TryCreate(@"C:\work\repo", repositoryUrl);

		Assert.NotNull(presentation);
		Assert.Equal(expectedName, presentation!.DisplayRootName);
	}

	[Fact]
	public void TryCreate_LeavesDisplayRootNameNull_WhenRepositoryPathHasNoSegments()
	{
		var service = new RepositoryWebPathPresentationService();
		var presentation = service.TryCreate(@"C:\work\repo", "https://example.com");

		Assert.NotNull(presentation);
		Assert.Null(presentation!.DisplayRootName);
	}

	[Fact]
	public void TryCreate_MapsRootPathToRepositoryRootUrl()
	{
		var service = new RepositoryWebPathPresentationService();
		var presentation = service.TryCreate(
			@"C:\work\repo",
			"https://github.com/Avazbek22/DevProjex");

		Assert.NotNull(presentation);
		var mapped = presentation!.MapFilePath(@"C:\work\repo");

		Assert.Equal("https://github.com/Avazbek22/DevProjex", mapped);
	}

	[Fact]
	public void TryCreate_EncodesUnsafeSegmentsInFilePath()
	{
		var service = new RepositoryWebPathPresentationService();
		var presentation = service.TryCreate(
			@"C:\work\repo",
			"https://github.com/Avazbek22/DevProjex");

		Assert.NotNull(presentation);
		var mapped = presentation!.MapFilePath(@"C:\work\repo\Docs\Мой файл #1.txt");

		Assert.Contains("/Docs/", mapped, StringComparison.Ordinal);
		Assert.Contains("%D0%9C%D0%BE%D0%B9%20%D1%84%D0%B0%D0%B9%D0%BB%20%231.txt", mapped, StringComparison.Ordinal);
	}

	[Fact]
	public void TryCreate_PathOutsideRoot_ReturnsOriginalPath()
	{
		var service = new RepositoryWebPathPresentationService();
		var presentation = service.TryCreate(
			@"C:\work\repo",
			"https://github.com/Avazbek22/DevProjex");

		Assert.NotNull(presentation);
		var external = @"C:\other\external.txt";
		var mapped = presentation!.MapFilePath(external);

		Assert.Equal(external, mapped);
	}
}
