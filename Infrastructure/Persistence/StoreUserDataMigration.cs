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
	private static readonly HashSet<string> ManagedStoreFileNames = new(StringComparer.OrdinalIgnoreCase)
	{
		"project-profiles.json",
		"project-secret-marks.json",
		"recent-projects.json",
		"secret-mark-hmac.key",
		"terminal-settings.json",
		"theme-settings.json",
		"user-settings.json"
	};

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
			var normalizedConfigurationRoot = Path.GetFullPath(configurationRoot);
			var destination = Path.Combine(normalizedConfigurationRoot, ProductFolderName);
			var source = Path.Combine(
				Path.GetFullPath(localDataRoot),
				"Packages",
				packageFamilyName,
				"LocalCache",
				"Roaming",
				ProductFolderName);
			if (!Directory.Exists(source))
				return StoreUserDataMigrationStatus.NotApplicable;
			UserDataPathResolver.EnsurePhysicalDirectory(source, createIfMissing: false);
			if (!HasInitializedData(source))
				return StoreUserDataMigrationStatus.NotApplicable;

			UserDataPathResolver.EnsurePhysicalDirectory(normalizedConfigurationRoot, createIfMissing: true);
			var lockPath = Path.Combine(normalizedConfigurationRoot, LockFileName);
			using var migrationLock = TryAcquireLock(lockPath);
			if (migrationLock is null)
				return StoreUserDataMigrationStatus.TemporarilyUnavailable;
			var completionMarker = Path.Combine(normalizedConfigurationRoot, CompletionMarkerFileName);
			if (File.Exists(completionMarker))
				return StoreUserDataMigrationStatus.AlreadyInitialized;
			if (Directory.Exists(destination))
			{
				UserDataPathResolver.EnsurePhysicalDirectory(destination, createIfMissing: false);
				if (HasInitializedData(destination))
				{
					WriteCompletionMarker(completionMarker);
					return StoreUserDataMigrationStatus.AlreadyInitialized;
				}
			}

			var backup = Path.Combine(normalizedConfigurationRoot, BackupFolderName);
			RefreshBackup(source, backup);

			var staging = destination + ".migration-" + Guid.NewGuid().ToString("N");
			try
			{
				CopyDirectory(backup, staging);
				if (Directory.Exists(destination))
				{
					UserDataPathResolver.EnsurePhysicalDirectory(destination, createIfMissing: false);
					RemoveManagedInitializationArtifacts(destination);
					Directory.Delete(destination, recursive: false);
				}
				Directory.Move(staging, destination);
			}
			finally
			{
				if (Directory.Exists(staging) && FileSystemRootEntryPolicy.IsPhysicalDirectory(staging))
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
			.Any(static file => !IsManagedInitializationArtifact(file));
	}

	private static void RemoveManagedInitializationArtifacts(string destination)
	{
		foreach (var file in Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories))
		{
			if (IsManagedInitializationArtifact(file))
				File.Delete(file);
		}

		foreach (var directory in Directory.EnumerateDirectories(destination, "*", SearchOption.AllDirectories)
					 .OrderByDescending(static path => path.Length))
		{
			if (!Directory.EnumerateFileSystemEntries(directory).Any())
				Directory.Delete(directory, recursive: false);
		}
	}

	private static bool IsManagedInitializationArtifact(string path)
	{
		var fileName = Path.GetFileName(path);
		if (fileName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
			return ManagedStoreFileNames.Contains(fileName[..^".lock".Length]);
		if (!fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
			return false;

		var withoutSuffix = fileName[..^".tmp".Length];
		var separator = withoutSuffix.LastIndexOf('.');
		if (separator < 0 ||
			!Guid.TryParseExact(withoutSuffix[(separator + 1)..], "N", out _))
		{
			return false;
		}

		var owner = withoutSuffix[..separator].TrimStart('.');
		return ManagedStoreFileNames.Contains(owner);
	}

	private static void WriteCompletionMarker(string path) =>
		File.WriteAllText(path, "completed");

	private static void RefreshBackup(string source, string backup)
	{
		if (Directory.Exists(backup))
			ValidatePhysicalTree(backup);
		var staging = backup + ".migration-" + Guid.NewGuid().ToString("N");
		try
		{
			CopyDirectory(source, staging);
			if (Directory.Exists(backup))
				Directory.Delete(backup, recursive: true);
			Directory.Move(staging, backup);
		}
		finally
		{
			if (Directory.Exists(staging) && FileSystemRootEntryPolicy.IsPhysicalDirectory(staging))
				Directory.Delete(staging, recursive: true);
		}
	}

	private static FileStream? TryAcquireLock(string path)
	{
		try
		{
			if (FileSystemRootEntryPolicy.IsReparsePoint(path))
				return null;
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
		ValidatePhysicalTree(source);
		Directory.CreateDirectory(destination);
		UserDataPathResolver.EnsurePhysicalDirectory(destination, createIfMissing: false);
		var pending = new Stack<(string Source, string Destination)>();
		pending.Push((source, destination));
		while (pending.TryPop(out var current))
		{
			foreach (var entry in Directory.EnumerateFileSystemEntries(
						 current.Source,
						 "*",
						 SearchOption.TopDirectoryOnly))
			{
				var attributes = File.GetAttributes(entry);
				if ((attributes & FileAttributes.ReparsePoint) != 0)
					throw new IOException("Store migration does not follow symbolic links or junctions.");
				var target = Path.Combine(current.Destination, Path.GetFileName(entry));
				if ((attributes & FileAttributes.Directory) != 0)
				{
					Directory.CreateDirectory(target);
					pending.Push((entry, target));
					continue;
				}
				File.Copy(entry, target, overwrite: false);
			}
		}
	}

	private static void ValidatePhysicalTree(string root)
	{
		UserDataPathResolver.EnsurePhysicalDirectory(root, createIfMissing: false);
		var pending = new Stack<string>();
		pending.Push(root);
		while (pending.TryPop(out var directory))
		{
			foreach (var entry in Directory.EnumerateFileSystemEntries(
						 directory,
						 "*",
						 SearchOption.TopDirectoryOnly))
			{
				var attributes = File.GetAttributes(entry);
				if ((attributes & FileAttributes.ReparsePoint) != 0)
					throw new IOException("Store migration does not follow symbolic links or junctions.");
				if ((attributes & FileAttributes.Directory) != 0)
					pending.Push(entry);
			}
		}
	}
}
