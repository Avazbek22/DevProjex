namespace DevProjex.Infrastructure.Persistence;

public static class StoreUserDataMigrationAdmission
{
	private const int MaximumAttempts = 3;

	public static StoreUserDataMigrationStatus Run(
		Func<StoreUserDataMigrationStatus>? migrationProbe = null,
		Action<TimeSpan>? wait = null)
	{
		migrationProbe ??= StoreUserDataMigration.TryMigrateCurrentWindowsPackage;
		wait ??= Thread.Sleep;

		var status = StoreUserDataMigrationStatus.Failed;
		for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
		{
			try
			{
				status = migrationProbe();
			}
			catch (Exception exception) when (exception is
					   IOException or UnauthorizedAccessException or
					   System.Security.SecurityException or ArgumentException or NotSupportedException)
			{
				status = StoreUserDataMigrationStatus.Failed;
			}
			if (IsReady(status))
				return status;
			if (attempt < MaximumAttempts)
				wait(TimeSpan.FromMilliseconds(75 * attempt));
		}

		return status;
	}

	public static bool IsReady(StoreUserDataMigrationStatus status) => status is
		StoreUserDataMigrationStatus.NotApplicable or
		StoreUserDataMigrationStatus.AlreadyInitialized or
		StoreUserDataMigrationStatus.Migrated;
}

public sealed class StoreUserDataMigrationUnavailableException(
	StoreUserDataMigrationStatus status)
	: IOException("Existing Store data could not be prepared safely.")
{
	public StoreUserDataMigrationStatus Status { get; } = status;
}
