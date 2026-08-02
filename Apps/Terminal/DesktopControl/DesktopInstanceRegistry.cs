using System.Diagnostics;
using System.Text.Json;

namespace DevProjex.Terminal.DesktopControl;

public sealed record DesktopRegistrySnapshot(
	IReadOnlyList<DesktopInstanceRegistration> Instances,
	int StaleEntryCount);

public sealed class DesktopInstanceRegistry(DesktopControlPaths? paths = null)
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true
	};
	private readonly DesktopControlPaths _paths = paths ?? new DesktopControlPaths();

	internal string RegistryDirectory => _paths.RegistryDirectory;

	public async Task RegisterAsync(
		DesktopInstanceRegistration registration,
		CancellationToken cancellationToken = default)
	{
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
			File.Move(temp, target, overwrite: true);
			SetPrivateFileMode(target);
		}
		finally
		{
			TryDelete(temp);
		}
	}

	public Task UnregisterAsync(string instanceId)
	{
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
					if (registration?.Transport == "unix")
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

	private static async Task<DesktopInstanceRegistration?> TryReadAsync(
		string path,
		CancellationToken cancellationToken)
	{
		try
		{
			var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
			var registration = JsonSerializer.Deserialize<DesktopInstanceRegistration>(json, JsonOptions);
			return registration?.ProtocolVersion == DesktopProtocol.CurrentVersion
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
			if (File.Exists(path))
				File.Delete(path);
		}
		catch
		{
			// Stale entries are retried by the next registry probe.
		}
	}
}
