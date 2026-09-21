namespace DevProjex.Infrastructure.Persistence;

using DevProjex.Infrastructure.TerminalCommands;

public enum StoreUserDataMigrationStatus
{
	NotApplicable = 0,
	AlreadyInitialized = 1,
	Migrated = 2,
	TemporarilyUnavailable = 3,
	Failed = 4
}

public static class StoreUserDataMigration
{
	private const string ProductFolderName = "DevProjex";
	private const string BackupFolderName = "DevProjex.v5.1-store-backup";
	private const string LockFileName = ".devprojex-store-migration.lock";
	private const string CompletionMarkerFileName = ".devprojex-store-migration.completed";

	public static StoreUserDataMigrationStatus TryMigrateCurrentWindowsPackage()
	{
		return WindowsPackageIdentityProbe.TryGetPackageFamilyName(out var packageFamilyName)
			? TryMigrate(
				UserDataPathResolver.GetConfigurationRoot(),
				UserDataPathResolver.GetLocalDataRoot(),
				packageFamilyName)
			: StoreUserDataMigrationStatus.NotApplicable;
	}

	public static StoreUserDataMigrationStatus TryMigrate(
		string configurationRoot,
		string localDataRoot,
		string packageFamilyName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(configurationRoot);
		ArgumentException.ThrowIfNullOrWhiteSpace(localDataRoot);
		ArgumentException.ThrowIfNullOrWhiteSpace(packageFamilyName);

		try
		{
			var destination = Path.Combine(Path.GetFullPath(configurationRoot), ProductFolderName);
			var source = Path.Combine(
				Path.GetFullPath(localDataRoot),
				"Packages",
				packageFamilyName,
				"LocalCache",
				"Roaming",
				ProductFolderName);
			if (!Directory.Exists(source))
				return StoreUserDataMigrationStatus.NotApplicable;

			Directory.CreateDirectory(configurationRoot);
			var lockPath = Path.Combine(configurationRoot, LockFileName);
			using var migrationLock = TryAcquireLock(lockPath);
			if (migrationLock is null)
				return StoreUserDataMigrationStatus.TemporarilyUnavailable;
			var completionMarker = Path.Combine(configurationRoot, CompletionMarkerFileName);
			if (File.Exists(completionMarker))
				return StoreUserDataMigrationStatus.AlreadyInitialized;
			if (HasInitializedData(destination))
			{
				WriteCompletionMarker(completionMarker);
				return StoreUserDataMigrationStatus.AlreadyInitialized;
			}

			var backup = Path.Combine(configurationRoot, BackupFolderName);
			if (!Directory.Exists(backup))
				CopyDirectory(source, backup);

			var staging = destination + ".migration-" + Guid.NewGuid().ToString("N");
			try
			{
				CopyDirectory(backup, staging);
				if (Directory.Exists(destination))
				{
					RemoveTransientInitializationArtifacts(destination);
					Directory.Delete(destination, recursive: false);
				}
				Directory.Move(staging, destination);
			}
			finally
			{
				if (Directory.Exists(staging))
					Directory.Delete(staging, recursive: true);
			}

			WriteCompletionMarker(completionMarker);
			return StoreUserDataMigrationStatus.Migrated;
		}
		catch (Exception exception) when (exception is
			   IOException or
			   UnauthorizedAccessException or
			   System.Security.SecurityException or
			   ArgumentException or
			   NotSupportedException)
		{
			return StoreUserDataMigrationStatus.Failed;
		}
	}

	private static bool HasInitializedData(string destination)
	{
		if (!Directory.Exists(destination))
			return false;

		return Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
			.Any(static path => !IsTransientInitializationArtifact(path));
	}

	private static void RemoveTransientInitializationArtifacts(string destination)
	{
		foreach (var file in Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories))
		{
			if (IsTransientInitializationArtifact(file))
				File.Delete(file);
		}

		foreach (var directory in Directory.EnumerateDirectories(destination, "*", SearchOption.AllDirectories)
					 .OrderByDescending(static path => path.Length))
		{
			if (!Directory.EnumerateFileSystemEntries(directory).Any())
				Directory.Delete(directory, recursive: false);
		}
	}

	private static bool IsTransientInitializationArtifact(string path) =>
		path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ||
		path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

	private static void WriteCompletionMarker(string path) =>
		File.WriteAllText(path, "completed");

	private static FileStream? TryAcquireLock(string path)
	{
		try
		{
			return new FileStream(
				path,
				FileMode.OpenOrCreate,
				FileAccess.ReadWrite,
				FileShare.None,
				bufferSize: 1,
				FileOptions.WriteThrough);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static void CopyDirectory(string source, string destination)
	{
		Directory.CreateDirectory(destination);
		foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
		{
			var relative = Path.GetRelativePath(source, directory);
			Directory.CreateDirectory(Path.Combine(destination, relative));
		}

		foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
		{
			var relative = Path.GetRelativePath(source, file);
			var target = Path.Combine(destination, relative);
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			File.Copy(file, target, overwrite: false);
		}
	}
}
