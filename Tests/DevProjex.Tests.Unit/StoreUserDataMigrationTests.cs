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

	private static string CreateSource(string localRoot) =>
		Directory.CreateDirectory(Path.Combine(
			localRoot,
			"Packages",
			PackageFamily,
			"LocalCache",
			"Roaming",
			"DevProjex")).FullName;
}
