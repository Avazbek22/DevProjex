using DevProjex.Application.Services;

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

