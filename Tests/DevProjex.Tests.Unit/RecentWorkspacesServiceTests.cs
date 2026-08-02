using DevProjex.Application.Workspaces;

namespace DevProjex.Tests.Unit;

public sealed class RecentWorkspacesServiceTests
{
	private readonly RecentWorkspacesService _service = new();

	[Fact]
	public void Project_MergesFoldersAndRepositoriesByMostRecentUse()
	{
		var now = DateTimeOffset.UtcNow;
		var folder = Path.GetFullPath(Path.Combine("missing", "Workspace"));

		var result = _service.Project(
		[
			new RecentWorkspaceSource(RecentWorkspaceKind.Folder, folder, now.AddMinutes(-2)),
			new RecentWorkspaceSource(
				RecentWorkspaceKind.Repository,
				"https://github.com/Avazbek22/DevProjex.git",
				now)
		]);

		Assert.Collection(
			result,
			repository =>
			{
				Assert.Equal(RecentWorkspaceKind.Repository, repository.Kind);
				Assert.Equal("DevProjex", repository.DisplayName);
				Assert.Equal(
					"https://github.com/Avazbek22/DevProjex.git",
					repository.DisplaySource);
			},
			local =>
			{
				Assert.Equal(RecentWorkspaceKind.Folder, local.Kind);
				Assert.Equal("Workspace", local.DisplayName);
				Assert.Equal(folder, local.DisplaySource, PathComparer.Default);
			});
	}

	[Fact]
	public void Project_DeduplicatesEquivalentRepositoryUrlsAndKeepsLatestSource()
	{
		var earlier = DateTimeOffset.UtcNow.AddMinutes(-1);
		var latest = DateTimeOffset.UtcNow;

		var result = _service.Project(
		[
			new RecentWorkspaceSource(
				RecentWorkspaceKind.Repository,
				"git@github.com:Avazbek22/DevProjex.git",
				earlier),
			new RecentWorkspaceSource(
				RecentWorkspaceKind.Repository,
				"https://github.com/avazbek22/devprojex",
				latest)
		]);

		var workspace = Assert.Single(result);
		Assert.Equal(latest, workspace.OpenedUtc);
		Assert.Equal("https://github.com/avazbek22/devprojex", workspace.Source);
	}

	[Fact]
	public void Project_DoesNotProbeWhetherFolderExists()
	{
		var missingPath = Path.Combine(
			Path.GetTempPath(),
			"DevProjex",
			"never-created",
			Guid.NewGuid().ToString("N"));

		var workspace = Assert.Single(_service.Project(
		[
			new RecentWorkspaceSource(
				RecentWorkspaceKind.Folder,
				missingPath,
				DateTimeOffset.UtcNow)
		]));

		Assert.Equal(Path.GetFullPath(missingPath), workspace.Source, PathComparer.Default);
		Assert.False(Directory.Exists(missingPath));
	}

	[Fact]
	public void Project_RemovesCredentialsAndQueryFromRepositoryDisplay()
	{
		var workspace = Assert.Single(_service.Project(
		[
			new RecentWorkspaceSource(
				RecentWorkspaceKind.Repository,
				"https://user:secret@example.com/owner/repository.git?token=private",
				DateTimeOffset.UtcNow)
		]));

		Assert.Equal(
			"https://example.com/owner/repository.git",
			workspace.DisplaySource);
		Assert.DoesNotContain("secret", workspace.DisplaySource, StringComparison.Ordinal);
		Assert.DoesNotContain("token", workspace.DisplaySource, StringComparison.Ordinal);
	}

	[Fact]
	public void Project_SkipsMalformedEntriesWithoutBreakingTheList()
	{
		var validPath = Path.GetFullPath("valid-workspace");

		var result = _service.Project(
		[
			new RecentWorkspaceSource(
				RecentWorkspaceKind.Repository,
				"https://example.com/repository\u0001",
				DateTimeOffset.UtcNow),
			new RecentWorkspaceSource(
				RecentWorkspaceKind.Folder,
				"\0",
				DateTimeOffset.UtcNow),
			new RecentWorkspaceSource(
				RecentWorkspaceKind.Folder,
				validPath,
				DateTimeOffset.UtcNow)
		]);

		Assert.Equal(validPath, Assert.Single(result).Source, PathComparer.Default);
	}
}
