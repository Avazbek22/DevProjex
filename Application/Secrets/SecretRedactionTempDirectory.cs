using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DevProjex.Application.Secrets;

internal sealed class SecretRedactionTempDirectory : IDisposable
{
	internal const string DirectoryPrefix = "DevProjex-SecretRedaction-";
	internal const string LeaseFileName = ".lease";
	internal const string OwnerFileName = ".owner";
	// The marker is one SHA-256 hex digest. A small allowance rejects attacker-controlled temp
	// files before ReadAllText can allocate from an arbitrary length.
	private const long MaximumOwnerMarkerBytes = 128;
	// Cleanup is startup best effort. Bounding one pass keeps a polluted shared temp root from
	// delaying application work; later starts continue where the previous pass stopped.
	private const int MaximumDirectoriesPerScavenge = 256;
	private const int MaximumDirectoryCreationAttempts = 16;
	// A full day avoids touching output from a suspended process while still reclaiming crash
	// residue automatically on a later application start.
	internal static readonly TimeSpan MinimumScavengeAge = TimeSpan.FromHours(24);

	private static readonly string OwnerIdentity = CreateOwnerIdentity();
	private FileStream? _lease;
	private int _disposed;

	private SecretRedactionTempDirectory(string path, FileStream lease)
	{
		Path = path;
		_lease = lease;
	}

	public string Path { get; }

	public static SecretRedactionTempDirectory Create()
		=> Create(System.IO.Path.GetTempPath());

	internal static SecretRedactionTempDirectory Create(
		string tempRoot,
		Func<Guid>? guidFactory = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(tempRoot);
		Directory.CreateDirectory(tempRoot);
		guidFactory ??= Guid.NewGuid;
		for (var attempt = 0; attempt < MaximumDirectoryCreationAttempts; attempt++)
		{
			var directory = System.IO.Path.Combine(
				tempRoot,
				$"{DirectoryPrefix}{guidFactory():N}");
			if (Directory.Exists(directory))
				continue;
			try
			{
				Directory.CreateDirectory(directory);
				return Initialize(directory);
			}
			catch (IOException) when (Directory.Exists(directory))
			{
				// A concurrent creator won this cryptographically unlikely name collision.
			}
		}

		throw new IOException("A unique secret-redaction temporary directory could not be created.");
	}

	private static SecretRedactionTempDirectory Initialize(string directory)
	{
		FileStream? lease = null;
		try
		{
			TryRestrictDirectory(directory);
			var leasePath = System.IO.Path.Combine(directory, LeaseFileName);
			lease = new FileStream(
				leasePath,
				FileMode.CreateNew,
				FileAccess.ReadWrite,
				FileShare.None,
				bufferSize: 1,
				FileOptions.None);
			TryRestrictFile(leasePath);
			var ownerPath = System.IO.Path.Combine(directory, OwnerFileName);
			File.WriteAllText(ownerPath, OwnerIdentity, new UTF8Encoding(false, true));
			TryRestrictFile(ownerPath);
			var ownedLease = lease;
			lease = null;
			return new SecretRedactionTempDirectory(directory, ownedLease);
		}
		catch
		{
			if (lease is not null)
			{
				lease.Dispose();
				TryDelete(directory);
			}
			throw;
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;
		Interlocked.Exchange(ref _lease, null)?.Dispose();
		TryDelete(Path);
	}

	internal void AbandonForTest() => Interlocked.Exchange(ref _lease, null)?.Dispose();

	internal static int Scavenge(
		string tempRoot,
		DateTime utcNow,
		TimeSpan? minimumAge = null)
	{
		var age = minimumAge ?? MinimumScavengeAge;
		ArgumentOutOfRangeException.ThrowIfLessThan(age, TimeSpan.Zero);
		var removed = 0;
		try
		{
			if (!Directory.Exists(tempRoot))
				return 0;
			var inspected = 0;
			foreach (var directory in Directory.EnumerateDirectories(
				         tempRoot,
				         $"{DirectoryPrefix}*",
				         SearchOption.TopDirectoryOnly))
			{
				if (inspected++ >= MaximumDirectoriesPerScavenge)
					break;
				if (!IsOwnedStaleDirectory(directory, utcNow, age) ||
				    !TryAcquireLease(directory, out var lease))
				{
					continue;
				}
				using (lease)
					TryDelete(directory);
				if (!Directory.Exists(directory))
					removed++;
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Temp cleanup is best effort and never invalidates a user operation.
		}
		return removed;
	}

	private static bool IsOwnedStaleDirectory(string directory, DateTime utcNow, TimeSpan minimumAge)
	{
		try
		{
			var ownerPath = System.IO.Path.Combine(directory, OwnerFileName);
			if (!HasExpectedDirectoryName(directory) ||
			    IsReparsePoint(directory) ||
			    IsReparsePoint(ownerPath) ||
			    !File.Exists(ownerPath) ||
			    new FileInfo(ownerPath).Length > MaximumOwnerMarkerBytes ||
			    !File.ReadAllText(ownerPath, Encoding.UTF8).Equals(OwnerIdentity, StringComparison.Ordinal) ||
			    !IsOwnedByCurrentUser(directory))
			{
				return false;
			}
			if (HasSharedUnixPermissions(directory))
				return false;
			return utcNow - File.GetLastWriteTimeUtc(ownerPath) >= minimumAge;
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			return false;
		}
	}

	internal static bool HasExpectedDirectoryName(string directory)
	{
		var name = System.IO.Path.GetFileName(directory);
		if (!name.StartsWith(DirectoryPrefix, StringComparison.Ordinal))
			return false;
		var suffix = name.AsSpan(DirectoryPrefix.Length);
		return suffix.Length == 32 && ContainsOnlyAsciiHexDigits(suffix);
	}

	private static bool IsOwnedByCurrentUser(string directory)
	{
		if (!OperatingSystem.IsWindows())
			return true;
		try
		{
			using var identity = WindowsIdentity.GetCurrent();
			var security = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Owner);
			var directoryOwner = security.GetOwner(typeof(SecurityIdentifier));
			return identity.User?.Equals(directoryOwner) == true ||
			       identity.Owner?.Equals(directoryOwner) == true;
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or NotSupportedException or
				IdentityNotMappedException)
		{
			return false;
		}
	}

	private static bool TryAcquireLease(string directory, out FileStream? lease)
	{
		lease = null;
		try
		{
			var leasePath = System.IO.Path.Combine(directory, LeaseFileName);
			var leaseMode = File.Exists(leasePath) ? FileMode.Open : FileMode.CreateNew;
			if (leaseMode == FileMode.Open && IsReparsePoint(leasePath))
				return false;
			lease = new FileStream(
				leasePath,
				leaseMode,
				FileAccess.ReadWrite,
				FileShare.Delete,
				bufferSize: 1,
				FileOptions.None);
			return true;
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or FileNotFoundException)
		{
			lease?.Dispose();
			lease = null;
			return false;
		}
	}

	private static bool HasSharedUnixPermissions(string directory)
	{
		if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD())
			return false;
		var mode = File.GetUnixFileMode(directory);
		const UnixFileMode shared =
			UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
			UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
		return (mode & shared) != 0;
	}

	private static void TryRestrictFile(string path)
	{
		if (OperatingSystem.IsWindows())
			return;
		File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
	}

	private static void TryRestrictDirectory(string path)
	{
		if (OperatingSystem.IsWindows())
			return;
		File.SetUnixFileMode(
			path,
			UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
	}

	private static bool IsReparsePoint(string path)
	{
		try
		{
			return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			return true;
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (Directory.Exists(path))
				Directory.Delete(path, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Prepared output cleanup is best effort; the startup scavenger retries abandoned paths.
		}
	}

	private static string CreateOwnerIdentity()
	{
		var source = $"{Environment.UserDomainName}\n{Environment.UserName}\n" +
		             Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
	}

	private static bool ContainsOnlyAsciiHexDigits(ReadOnlySpan<char> value)
	{
		foreach (var character in value)
		{
			if (!char.IsAsciiHexDigit(character))
				return false;
		}
		return true;
	}
}

internal static class SecretRedactionTempDirectoryScavenger
{
	private static int _started;

	public static void StartOnce()
	{
		if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
			return;
		_ = Task.Run(static () =>
			SecretRedactionTempDirectory.Scavenge(Path.GetTempPath(), DateTime.UtcNow));
	}
}
