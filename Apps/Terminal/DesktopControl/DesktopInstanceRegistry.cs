using System.Diagnostics;
using System.Text.Json;
using DevProjex.Kernel.IO;

namespace DevProjex.Terminal.DesktopControl;

public sealed record DesktopRegistrySnapshot(
	IReadOnlyList<DesktopInstanceRegistration> Instances,
	int StaleEntryCount);

public sealed class DesktopInstanceRegistry
{
	private static readonly TimeSpan StaleTemporaryFileAge = TimeSpan.FromHours(24);
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true
	};
	private readonly DesktopControlPaths _paths;
	private readonly Action? _afterRegistrationReadOpened;

	public DesktopInstanceRegistry(DesktopControlPaths? paths = null)
		: this(paths, afterRegistrationReadOpened: null)
	{
	}

	internal DesktopInstanceRegistry(
		DesktopControlPaths? paths,
		Action? afterRegistrationReadOpened)
	{
		_paths = paths ?? new DesktopControlPaths();
		_afterRegistrationReadOpened = afterRegistrationReadOpened;
	}

	internal string RegistryDirectory => _paths.RegistryDirectory;

	public async Task RegisterAsync(
		DesktopInstanceRegistration registration,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(registration);
		if (!HasValidFields(registration))
			throw new ArgumentException("Desktop registration fields are invalid.", nameof(registration));

		EnsurePrivateDirectory(_paths.RegistryDirectory);
		var target = _paths.GetRegistrationPath(registration.InstanceId);
		var temp = target + $".{Guid.NewGuid():N}.tmp";
		try
		{
			var json = JsonSerializer.Serialize(registration, JsonOptions);
			await File.WriteAllTextAsync(
				temp,
				json,
				new UTF8Encoding(false),
				cancellationToken).ConfigureAwait(false);
			SetPrivateFileMode(temp);
			CommitRegistration(temp, target);
			SetPrivateFileMode(target);
		}
		finally
		{
			TryDelete(temp);
		}
	}

	public Task UnregisterAsync(string instanceId)
	{
		if (IsValidInstanceId(instanceId))
			TryDelete(_paths.GetRegistrationPath(instanceId));
		return Task.CompletedTask;
	}

	public async Task<IReadOnlyList<DesktopInstanceRegistration>> ListAsync(
		CancellationToken cancellationToken = default) =>
		(await ProbeAsync(removeStale: true, cancellationToken).ConfigureAwait(false)).Instances;

	public async Task<DesktopRegistrySnapshot> ProbeAsync(
		bool removeStale,
		CancellationToken cancellationToken = default)
	{
		if (!Directory.Exists(_paths.RegistryDirectory))
			return new DesktopRegistrySnapshot([], 0);

		if (removeStale)
			RemoveStaleTemporaryFiles(cancellationToken);

		var registrations = new List<DesktopInstanceRegistration>();
		var staleEntryCount = 0;
		foreach (var path in Directory.EnumerateFiles(_paths.RegistryDirectory, "*.json"))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var registration = await TryReadAsync(path, cancellationToken).ConfigureAwait(false);
			if (registration is null || !IsLiveProcess(registration))
			{
				if (removeStale)
				{
					TryDelete(path);
					if (registration is not null && IsOwnedUnixEndpoint(registration))
						TryDelete(registration.Endpoint);
				}
				staleEntryCount++;
				continue;
			}

			registrations.Add(registration);
		}

		return new DesktopRegistrySnapshot(
			registrations
				.OrderByDescending(static registration => registration.LastActiveUtc)
				.ThenBy(static registration => registration.InstanceId, StringComparer.Ordinal)
				.ToArray(),
			staleEntryCount);
	}

	private bool IsOwnedUnixEndpoint(DesktopInstanceRegistration registration)
	{
		if (!string.Equals(registration.Transport, "unix", StringComparison.Ordinal))
			return false;
		try
		{
			return PathComparer.Default.Equals(
				Path.GetFullPath(registration.Endpoint),
				Path.GetFullPath(_paths.GetSocketPath(registration.InstanceId)));
		}
		catch (Exception exception) when (
			exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}
	}

	private async Task<DesktopInstanceRegistration?> TryReadAsync(
		string path,
		CancellationToken cancellationToken)
	{
		try
		{
			await using var source = new FileStream(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete,
				bufferSize: 4 * 1024,
				FileOptions.Asynchronous | FileOptions.SequentialScan);
			_afterRegistrationReadOpened?.Invoke();
			await using var stream = new MaximumLengthReadStream(
				source,
				DesktopProtocol.MaximumMessageBytes,
				static () => new IOException("Desktop registration exceeds the protocol limit."));
			var registration = await JsonSerializer
				.DeserializeAsync<DesktopInstanceRegistration>(
					stream,
					JsonOptions,
					cancellationToken)
				.ConfigureAwait(false);
			return registration is not null && IsValidRegistrationFile(registration, path)
				? registration
				: null;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			return null;
		}
	}

	private static bool IsValidRegistrationFile(
		DesktopInstanceRegistration registration,
		string path) =>
		HasValidFields(registration) &&
		string.Equals(
			Path.GetFileNameWithoutExtension(path),
			registration.InstanceId,
			StringComparison.Ordinal);

	private static bool HasValidFields(DesktopInstanceRegistration registration) =>
		registration.ProtocolVersion == DesktopProtocol.CurrentVersion &&
		IsValidInstanceId(registration.InstanceId) &&
		registration.ProcessId > 0 &&
		registration.ProcessStartTimeUtcTicks > 0 &&
		registration.Transport is "pipe" or "unix" &&
		!string.IsNullOrWhiteSpace(registration.Endpoint) &&
		!registration.Endpoint.Any(char.IsControl);

	private static bool IsValidInstanceId(string? instanceId) =>
		!string.IsNullOrEmpty(instanceId) &&
		instanceId.Length <= 128 &&
		instanceId.All(static character =>
			char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

	private void RemoveStaleTemporaryFiles(CancellationToken cancellationToken)
	{
		var cutoff = DateTime.UtcNow - StaleTemporaryFileAge;
		foreach (var path in Directory.EnumerateFiles(_paths.RegistryDirectory, "*.tmp"))
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				if (!IsOwnedRegistrationTemporaryFile(path) ||
				    File.GetLastWriteTimeUtc(path) > cutoff)
				{
					continue;
				}

				TryDelete(path);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
			}
		}
	}

	private static bool IsOwnedRegistrationTemporaryFile(string path)
	{
		var fileName = Path.GetFileName(path);
		if (!fileName.EndsWith(".tmp", StringComparison.Ordinal))
			return false;

		var withoutSuffix = fileName[..^4];
		var nonceSeparator = withoutSuffix.LastIndexOf('.');
		if (nonceSeparator <= 0)
			return false;

		var nonce = withoutSuffix.AsSpan(nonceSeparator + 1);
		if (nonce.Length != 32 || !ContainsOnlyAsciiHexDigits(nonce))
			return false;

		var registrationFileName = withoutSuffix[..nonceSeparator];
		if (!registrationFileName.EndsWith(".json", StringComparison.Ordinal))
			return false;

		return IsValidInstanceId(Path.GetFileNameWithoutExtension(registrationFileName));
	}

	private static bool ContainsOnlyAsciiHexDigits(ReadOnlySpan<char> value)
	{
		foreach (var character in value)
		{
			if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
				return false;
		}

		return true;
	}

	private static void CommitRegistration(string temporaryPath, string targetPath)
	{
		if (!File.Exists(targetPath))
		{
			File.Move(temporaryPath, targetPath);
			return;
		}

		try
		{
			File.Replace(temporaryPath, targetPath, destinationBackupFileName: null);
		}
		catch (FileNotFoundException) when (!File.Exists(targetPath))
		{
			File.Move(temporaryPath, targetPath);
		}
		catch (NotSupportedException)
		{
			File.Move(temporaryPath, targetPath, overwrite: true);
		}
	}

	private static bool IsLiveProcess(DesktopInstanceRegistration registration)
	{
		try
		{
			using var process = Process.GetProcessById(registration.ProcessId);
			return !process.HasExited &&
			       Math.Abs(process.StartTime.ToUniversalTime().Ticks - registration.ProcessStartTimeUtcTicks) <
			       TimeSpan.FromSeconds(2).Ticks;
		}
		catch
		{
			return false;
		}
	}

	internal static void EnsurePrivateDirectory(string path)
	{
		Directory.CreateDirectory(path);
		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(
				path,
				UnixFileMode.UserRead |
				UnixFileMode.UserWrite |
				UnixFileMode.UserExecute);
		}
	}

	internal static void SetPrivateFileMode(string path)
	{
		if (!OperatingSystem.IsWindows())
			File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
	}

	internal static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch
		{
			// Cleanup is best-effort because another process may still own the entry.
		}
	}
}
