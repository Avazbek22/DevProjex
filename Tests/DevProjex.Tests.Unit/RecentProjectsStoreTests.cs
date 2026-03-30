using DevProjex.Infrastructure.RecentProjects;

namespace DevProjex.Tests.Unit;

public sealed class RecentProjectsStoreTests
{
	[Fact]
	public void AddFolder_MovesDuplicateToFront_AndPersists()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var db = store.Load();
		var folderA = Path.Combine(temp.Path, "FolderA");
		var folderB = Path.Combine(temp.Path, "FolderB");

		db = store.AddFolder(db, folderA);
		db = store.AddFolder(db, folderB);
		db = store.AddFolder(db, folderA);

		Assert.Equal(2, db.RecentFolders.Count);
		Assert.Equal(Path.GetFullPath(folderA).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), db.RecentFolders[0].Path);
		Assert.Equal(Path.GetFullPath(folderB).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), db.RecentFolders[1].Path);
		Assert.True(File.Exists(store.GetPath()));
	}

	[Fact]
	public void AddFolder_ClampsToTenItems()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var db = store.Load();

		for (var i = 0; i < 12; i++)
			db = store.AddFolder(db, Path.Combine(temp.Path, $"Folder{i}"));

		Assert.Equal(10, db.RecentFolders.Count);
		Assert.Contains(db.RecentFolders, entry => entry.Path.EndsWith("Folder11", StringComparison.Ordinal));
		Assert.DoesNotContain(db.RecentFolders, entry => entry.Path.EndsWith("Folder0", StringComparison.Ordinal));
	}

	[Fact]
	public void AddRepository_NormalizesUrl_AndDeduplicates()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var db = store.Load();

		db = store.AddRepository(db, "https://github.com/user/repo/");
		db = store.AddRepository(db, "https://github.com/user/repo?ref=main");

		Assert.Single(db.RecentRepositories);
		Assert.Equal("https://github.com/user/repo", db.RecentRepositories[0].Url);
	}

	[Fact]
	public void AddRepository_ClampsToSevenItems()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var db = store.Load();

		for (var i = 0; i < 9; i++)
			db = store.AddRepository(db, $"https://example.com/user/repo{i}");

		Assert.Equal(7, db.RecentRepositories.Count);
		Assert.Contains(db.RecentRepositories, entry => entry.Url.EndsWith("repo8", StringComparison.Ordinal));
		Assert.DoesNotContain(db.RecentRepositories, entry => entry.Url.EndsWith("repo0", StringComparison.Ordinal));
	}
}
