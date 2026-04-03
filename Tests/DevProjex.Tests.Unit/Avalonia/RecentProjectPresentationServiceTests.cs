using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class RecentProjectPresentationServiceTests
{
	[Fact]
	public void CreateFolderDisplayText_UsesParentAndLeaf()
	{
		var path = Path.Combine("C:", "Work", "Repo");

		var text = RecentProjectPresentationService.CreateFolderDisplayText(path);

		Assert.Equal("Work / Repo", text);
	}

	[Fact]
	public void CreateFolderToolTip_ReturnsNormalizedPath()
	{
		var path = Path.Combine("C:", "Work", "Repo");

		var tooltip = RecentProjectPresentationService.CreateFolderToolTip(path);

		Assert.EndsWith(Path.Combine("Work", "Repo"), tooltip, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void CreateRepositoryDisplayText_TrimsGitSuffixAndShowsOwnerRepo()
	{
		var text = RecentProjectPresentationService.CreateRepositoryDisplayText("https://github.com/user/repo.git");

		Assert.Equal("user / repo", text);
	}

	[Fact]
	public void CreateRepositoryToolTip_ReturnsNormalizedUrl()
	{
		var tooltip = RecentProjectPresentationService.CreateRepositoryToolTip("https://github.com/user/repo.git?ref=main");

		Assert.Equal("https://github.com/user/repo.git", tooltip);
	}

	[Fact]
	public void CreateFolderDisplayText_ReturnsRootPath_WhenFolderHasNoParentSegment()
	{
		var rootPath = Path.GetPathRoot(Environment.CurrentDirectory)!;

		var text = RecentProjectPresentationService.CreateFolderDisplayText(rootPath);

		Assert.Equal(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), text.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
	}

	[Fact]
	public void CreateRepositoryDisplayText_HandlesScpLikeGitUrl()
	{
		var text = RecentProjectPresentationService.CreateRepositoryDisplayText("git@github.com:user/repo.git");

		Assert.Equal("git@github.com:user/repo.git", text);
	}

	[Fact]
	public void CreateRepositoryToolTip_LeavesScpLikeGitUrlUntouched()
	{
		var tooltip = RecentProjectPresentationService.CreateRepositoryToolTip("git@github.com:user/repo.git");

		Assert.Equal("git@github.com:user/repo.git", tooltip);
	}
}
