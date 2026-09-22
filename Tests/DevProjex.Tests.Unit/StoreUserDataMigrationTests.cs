using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Tests.Unit;

public sealed class StoreUserDataMigrationTests
{
	private const string PackageFamily = "StarkIndustriesDev.DevProjex_test";

	[Fact]
	public void EmptySharedStateMigratesVirtualizedStoreDataAndKeepsBackup()
	{
		using var workspace = new TemporaryDirectory();
		var configuration = workspace.CreateFolder("roaming");
		var local = workspace.CreateFolder("local");
		var source = CreateSource(local);
		File.WriteAllText(Path.Combine(source, "project-profiles.json"), "profiles");
		Directory.CreateDirectory(Path.Combine(source, "nested"));
		File.WriteAllText(Path.Combine(source, "nested", "settings.json"), "settings");

		var status = StoreUserDataMigration.TryMigrate(configuration, local, PackageFamily);

		Assert.Equal(StoreUserDataMigrationStatus.Migrated, status);
		Assert.Equal("profiles", File.ReadAllText(Path.Combine(configuration, "DevProjex", "project-profiles.json")));
		Assert.Equal("settings", File.ReadAllText(Path.Combine(configuration, "DevProjex", "nested", "settings.json")));
		Assert.Equal("profiles", File.ReadAllText(Path.Combine(
			configuration,
			"DevProjex.v5.1-store-backup",
			"project-profiles.json")));
	}

	[Fact]
	public void ExistingSharedStateIsNeverOverwrittenByVirtualizedData()
	{
		using var workspace = new TemporaryDirectory();
		var configuration = workspace.CreateFolder("roaming");
		var local = workspace.CreateFolder("local");
		var source = CreateSource(local);
		File.WriteAllText(Path.Combine(source, "project-profiles.json"), "legacy");
		var destination = Directory.CreateDirectory(Path.Combine(configuration, "DevProjex")).FullName;
		File.WriteAllText(Path.Combine(destination, "project-profiles.json"), "current");

		var status = StoreUserDataMigration.TryMigrate(configuration, local, PackageFamily);

		Assert.Equal(StoreUserDataMigrationStatus.AlreadyInitialized, status);
		Assert.Equal("current", File.ReadAllText(Path.Combine(destination, "project-profiles.json")));
		Assert.False(Directory.Exists(Path.Combine(configuration, "DevProjex.v5.1-store-backup")));
	}

	[Fact]
	public void StoreLockCreatedByTerminalDoesNotBlockLaterPackageMigration()
	{
		using var workspace = new TemporaryDirectory();
		var configuration = workspace.CreateFolder("roaming");
		var local = workspace.CreateFolder("local");
		var source = CreateSource(local);
		File.WriteAllText(Path.Combine(source, "project-profiles.json"), "legacy");
		var destination = Directory.CreateDirectory(Path.Combine(configuration, "DevProjex")).FullName;
		File.WriteAllText(Path.Combine(destination, "terminal-settings.json.lock"), string.Empty);

		var first = StoreUserDataMigration.TryMigrate(configuration, local, PackageFamily);
		var second = StoreUserDataMigration.TryMigrate(configuration, local, PackageFamily);

		Assert.Equal(StoreUserDataMigrationStatus.Migrated, first);
		Assert.Equal(StoreUserDataMigrationStatus.AlreadyInitialized, second);
		Assert.False(File.Exists(Path.Combine(destination, "terminal-settings.json.lock")));
		Assert.Equal(
			"legacy",
			File.ReadAllText(Path.Combine(destination, "project-profiles.json")));
		Assert.True(File.Exists(Path.Combine(configuration, ".devprojex-store-migration.completed")));
	}

	[Fact]
	public void MigrationDoesNotDeleteForeignTemporaryOrLockFiles()
	{
		using var workspace = new TemporaryDirectory();
		var configuration = workspace.CreateFolder("roaming");
		var local = workspace.CreateFolder("local");
		var source = CreateSource(local);
		File.WriteAllText(Path.Combine(source, "project-profiles.json"), "legacy");
		var destination = Directory.CreateDirectory(Path.Combine(configuration, "DevProjex")).FullName;
		var foreignTemporary = Path.Combine(destination, "owner-document.tmp");
		var foreignLock = Path.Combine(destination, "owner-document.lock");
		File.WriteAllText(foreignTemporary, "keep-temp");
		File.WriteAllText(foreignLock, "keep-lock");

		var status = StoreUserDataMigration.TryMigrate(configuration, local, PackageFamily);

		Assert.Equal(StoreUserDataMigrationStatus.AlreadyInitialized, status);
		Assert.Equal("keep-temp", File.ReadAllText(foreignTemporary));
		Assert.Equal("keep-lock", File.ReadAllText(foreignLock));
		Assert.False(File.Exists(Path.Combine(destination, "project-profiles.json")));
	}

	[Fact]
	public void MigrationRejectsASymbolicLinkProductDirectory()
	{
		using var workspace = new TemporaryDirectory();
		using var outside = new TemporaryDirectory();
		var configuration = workspace.CreateFolder("roaming");
		var local = workspace.CreateFolder("local");
		var source = CreateSource(local);
		File.WriteAllText(Path.Combine(source, "project-profiles.json"), "legacy");
		var protectedFile = Path.Combine(outside.Path, "keep.txt");
		File.WriteAllText(protectedFile, "keep");
		try
		{
			Directory.CreateSymbolicLink(Path.Combine(configuration, "DevProjex"), outside.Path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Assert.Skip("Creating directory symbolic links is unavailable in this environment.");
			return;
		}

		var status = StoreUserDataMigration.TryMigrate(configuration, local, PackageFamily);

		Assert.Equal(StoreUserDataMigrationStatus.Failed, status);
		Assert.Equal("keep", File.ReadAllText(protectedFile));
	}

	[Fact]
	public void MigrationRejectsASymbolicLinkInsideTheStoreSource()
	{
		using var workspace = new TemporaryDirectory();
		using var outside = new TemporaryDirectory();
		var configuration = workspace.CreateFolder("roaming");
		var local = workspace.CreateFolder("local");
		var source = CreateSource(local);
		File.WriteAllText(Path.Combine(source, "project-profiles.json"), "legacy");
		var protectedFile = Path.Combine(outside.Path, "keep.txt");
		File.WriteAllText(protectedFile, "keep");
		try
		{
			Directory.CreateSymbolicLink(Path.Combine(source, "linked"), outside.Path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Assert.Skip("Creating directory symbolic links is unavailable in this environment.");
			return;
		}

		var status = StoreUserDataMigration.TryMigrate(configuration, local, PackageFamily);

		Assert.Equal(StoreUserDataMigrationStatus.Failed, status);
		Assert.Equal("keep", File.ReadAllText(protectedFile));
		Assert.False(Directory.Exists(Path.Combine(configuration, "DevProjex")));
	}

	[Fact]
	public void ConcurrentMigrationLockLeavesBothTreesUntouched()
	{
		using var workspace = new TemporaryDirectory();
		var configuration = workspace.CreateFolder("roaming");
		var local = workspace.CreateFolder("local");
		var source = CreateSource(local);
		File.WriteAllText(Path.Combine(source, "settings.json"), "legacy");
		using var heldLock = new FileStream(
			Path.Combine(configuration, ".devprojex-store-migration.lock"),
			FileMode.OpenOrCreate,
			FileAccess.ReadWrite,
			FileShare.None);

		var status = StoreUserDataMigration.TryMigrate(configuration, local, PackageFamily);

		Assert.Equal(StoreUserDataMigrationStatus.TemporarilyUnavailable, status);
		Assert.Equal("legacy", File.ReadAllText(Path.Combine(source, "settings.json")));
		Assert.False(Directory.Exists(Path.Combine(configuration, "DevProjex")));
	}

	[Fact]
	public void EmptyVirtualizedStoreDoesNotFinalizeMigrationBeforeDataAppears()
	{
		using var workspace = new TemporaryDirectory();
		var configuration = workspace.CreateFolder("roaming");
		var local = workspace.CreateFolder("local");
		var source = CreateSource(local);

		var empty = StoreUserDataMigration.TryMigrate(configuration, local, PackageFamily);
		File.WriteAllText(Path.Combine(source, "project-profiles.json"), "legacy");
		var populated = StoreUserDataMigration.TryMigrate(configuration, local, PackageFamily);

		Assert.Equal(StoreUserDataMigrationStatus.NotApplicable, empty);
		Assert.Equal(StoreUserDataMigrationStatus.Migrated, populated);
		Assert.Equal(
			"legacy",
			File.ReadAllText(Path.Combine(configuration, "DevProjex", "project-profiles.json")));
	}

	[Fact]
	public void IncompleteBackupIsRebuiltFromTheVirtualizedStore()
	{
		using var workspace = new TemporaryDirectory();
		var configuration = workspace.CreateFolder("roaming");
		var local = workspace.CreateFolder("local");
		var source = CreateSource(local);
		File.WriteAllText(Path.Combine(source, "project-profiles.json"), "profiles");
		File.WriteAllText(Path.Combine(source, "terminal-settings.json"), "settings");
		var backup = Directory.CreateDirectory(
			Path.Combine(configuration, "DevProjex.v5.1-store-backup")).FullName;
		File.WriteAllText(Path.Combine(backup, "project-profiles.json"), "partial");

		var status = StoreUserDataMigration.TryMigrate(configuration, local, PackageFamily);

		Assert.Equal(StoreUserDataMigrationStatus.Migrated, status);
		Assert.Equal("profiles", File.ReadAllText(Path.Combine(backup, "project-profiles.json")));
		Assert.Equal("settings", File.ReadAllText(Path.Combine(backup, "terminal-settings.json")));
		Assert.Equal(
			"settings",
			File.ReadAllText(Path.Combine(configuration, "DevProjex", "terminal-settings.json")));
	}

	private static string CreateSource(string localRoot) =>
		Directory.CreateDirectory(Path.Combine(
			localRoot,
			"Packages",
			PackageFamily,
			"LocalCache",
			"Roaming",
			"DevProjex")).FullName;
}
