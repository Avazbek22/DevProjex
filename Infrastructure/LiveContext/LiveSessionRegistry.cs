using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text.Json;
using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Infrastructure.LiveContext;

public sealed record LiveSessionRecord(
	int Pid,
	DateTimeOffset ProcessStartUtc,
	string? ClientName,
	string? ClientVersion,
	IReadOnlyList<string> Roots,
	DateTimeOffset HeartbeatUtc,
	AgentJournalMode Mode = AgentJournalMode.Live);

public sealed class LiveSessionRegistry(
	Func<string>? stateRootProvider = null,
	TimeProvider? timeProvider = null,
	Func<int, DateTimeOffset?>? processStartProvider = null)
{
	public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
	public static readonly TimeSpan StaleHeartbeatAge = TimeSpan.FromSeconds(15);
	private const UnixFileMode PrivateDirectoryMode =
		UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
	private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
	private const int MaximumRecordBytes = 64 * 1024;
	private const int MaximumRegistryEntries = 1_024;
	private const int MaximumClientNameCharacters = 256;
	private const int MaximumClientVersionCharacters = 128;
	private const int MaximumRootCharacters = 4_096;
	private readonly Func<string> stateRoot = stateRootProvider ?? UserDataPathResolver.GetStateRoot;
	private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
	private readonly Func<int, DateTimeOffset?> processStart = processStartProvider ?? TryGetProcessStartUtc;

	public string DirectoryPath
	{
		get
		{
			try
			{
				var directory = UserDataPathResolver.EnsurePhysicalServiceDirectory(
					stateRoot(), "live-sessions");
				if (!OperatingSystem.IsWindows())
					File.SetUnixFileMode(directory, PrivateDirectoryMode);
				return directory;
			}
			catch (Exception exception) when (exception is NotSupportedException or SecurityException)
			{
				throw new IOException("Live context session directory cannot be protected.", exception);
			}
		}
	}

	public LiveSessionWriter Start(
		IReadOnlyList<string> roots,
		AgentJournalMode mode = AgentJournalMode.Live) =>
		new(
			this,
			Environment.ProcessId,
			GetCurrentProcessStartUtc(),
			roots,
			mode);

	internal LiveSessionWriter Start(
		int pid,
		DateTimeOffset processStartUtc,
		IReadOnlyList<string> roots,
		AgentJournalMode mode = AgentJournalMode.Live) =>
		new(this, pid, processStartUtc, roots, mode);

	public IReadOnlyList<LiveSessionRecord> ReadActive(string? projectRoot = null)
	{
		string[] paths;
		try
		{
			var directory = DirectoryPath;
			if (!Directory.Exists(directory))
				return [];
			paths = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
				.Take(MaximumRegistryEntries)
				.ToArray();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
		{
			return [];
		}

		var now = clock.GetUtcNow();
		var normalizedProjectRoot = TryNormalize(projectRoot);
		var records = new List<LiveSessionRecord>();
		foreach (var path in paths)
		{
			var record = TryRead(path, out var invalid);
			if (record is null)
			{
				if (invalid)
					TryDelete(path);
				continue;
			}
			if (!IsAlive(record, now))
			{
				TryDelete(path);
				continue;
			}
			if (normalizedProjectRoot is null || record.Roots.Any(root => PathComparer.Default.Equals(
				TryNormalize(root),
				normalizedProjectRoot)))
			{
				records.Add(record);
			}
		}
		return records
			.OrderBy(static record => record.ProcessStartUtc)
			.ThenBy(static record => record.Pid)
			.ToArray();
	}

	public static string FormatClientName(string? clientName) =>
		clientName?.Trim().ToLowerInvariant() switch
		{
			"claude-code" => "Claude Code",
			"codex" => "Codex",
			_ => string.IsNullOrWhiteSpace(clientName) ? "MCP client" : clientName.Trim()
		};

	internal DateTimeOffset UtcNow => clock.GetUtcNow();
	internal string GetPath(int pid) => Path.Combine(DirectoryPath, $"{pid}.json");

	internal void Write(LiveSessionRecord record)
	{
		var directory = DirectoryPath;
		var path = Path.Combine(directory, $"{record.Pid}.json");
		var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			var options = new FileStreamOptions
			{
				Mode = FileMode.CreateNew,
				Access = FileAccess.Write,
				Share = FileShare.None
			};
			if (!OperatingSystem.IsWindows())
				options.UnixCreateMode = PrivateFileMode;
			using (var stream = new FileStream(temporary, options))
			{
				JsonSerializer.Serialize(
					stream,
					record,
					InfrastructureJsonSerializerContext.Default.LiveSessionRecord);
			}
			File.Move(temporary, path, overwrite: true);
		}
		finally
		{
			TryDelete(temporary);
		}
	}

	internal bool TryWrite(LiveSessionRecord record)
	{
		try
		{
			Write(record);
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException)
		{
			Trace.TraceWarning(
				"Live context session could not be written: {0}",
				exception.GetType().Name);
			return false;
		}
	}

	internal void Delete(int pid)
	{
		try
		{
			TryDelete(GetPath(pid));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
		{
		}
	}

	private LiveSessionRecord? TryRead(string path, out bool invalid)
	{
		invalid = false;
		try
		{
			var attributes = File.GetAttributes(path);
			if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 ||
				!TryParsePidFileName(path, out var filePid))
			{
				invalid = true;
				return null;
			}
			using var stream = new FileStream(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete,
				bufferSize: 4096,
				FileOptions.SequentialScan);
			EnsurePrivateFileMode(stream);
			if (stream.Length is <= 0 or > MaximumRecordBytes)
			{
				invalid = true;
				return null;
			}

			var bytes = new byte[(int)stream.Length];
			stream.ReadExactly(bytes);
			if (stream.ReadByte() >= 0)
			{
				invalid = true;
				return null;
			}

			var record = JsonSerializer.Deserialize(
				bytes,
				InfrastructureJsonSerializerContext.Default.LiveSessionRecord);
			if (record is { Roots: not null } &&
				record.Pid == filePid &&
				record.Roots.Count is > 0 and <= 256 &&
				record.Roots.All(static root => !string.IsNullOrWhiteSpace(root) && root.Length <= MaximumRootCharacters) &&
				(record.ClientName?.Length ?? 0) <= MaximumClientNameCharacters &&
				(record.ClientVersion?.Length ?? 0) <= MaximumClientVersionCharacters)
				return record;

			invalid = true;
			return null;
		}
		catch (Exception exception) when (exception is JsonException or NotSupportedException)
		{
			invalid = true;
			return null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
		{
			return null;
		}
	}

	private static void EnsurePrivateFileMode(FileStream stream)
	{
		if (OperatingSystem.IsWindows())
			return;
		try
		{
			var handle = stream.SafeFileHandle;
			if (File.GetUnixFileMode(handle) != PrivateFileMode)
				File.SetUnixFileMode(handle, PrivateFileMode);
		}
		catch (NotSupportedException exception)
		{
			throw new IOException("Live context session file cannot be protected.", exception);
		}
	}

	private static bool TryParsePidFileName(string path, out int pid)
	{
		var fileName = Path.GetFileName(path);
		var name = Path.GetFileNameWithoutExtension(fileName);
		return int.TryParse(
			name,
			System.Globalization.NumberStyles.None,
			System.Globalization.CultureInfo.InvariantCulture,
			out pid) &&
			pid > 0 &&
			StringComparer.Ordinal.Equals(
				fileName,
				pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json");
	}

	private static string? TryNormalize(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return null;
		try
		{
			return PathUtility.Normalize(path);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or SecurityException)
		{
			return null;
		}
	}

	private bool IsAlive(LiveSessionRecord record, DateTimeOffset now)
	{
		if (record.Pid <= 0 ||
			record.HeartbeatUtc < record.ProcessStartUtc ||
			record.HeartbeatUtc > now + HeartbeatInterval ||
			now - record.HeartbeatUtc > StaleHeartbeatAge)
			return false;
		DateTimeOffset? observedStart;
		try
		{
			observedStart = processStart(record.Pid);
		}
		catch (Exception exception) when (exception is
			   ArgumentException or InvalidOperationException or NotSupportedException or
			   UnauthorizedAccessException or SecurityException or Win32Exception)
		{
			return false;
		}
		return observedStart is not null &&
			   Math.Abs((observedStart.Value - record.ProcessStartUtc).TotalSeconds) < 1;
	}

	private static DateTimeOffset GetCurrentProcessStartUtc()
	{
		using var process = Process.GetCurrentProcess();
		return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
	}

	private static DateTimeOffset? TryGetProcessStartUtc(int pid)
	{
		try
		{
			using var process = Process.GetProcessById(pid);
			return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
		}
		catch (Exception exception) when (exception is
			   ArgumentException or InvalidOperationException or NotSupportedException or
			   UnauthorizedAccessException or SecurityException or Win32Exception)
		{
			return null;
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
		{
		}
	}
}

public sealed class LiveSessionWriter : IAsyncDisposable, IDisposable
{
	private readonly LiveSessionRegistry registry;
	private readonly CancellationTokenSource cancellation = new();
	private readonly Task heartbeat;
	private readonly object sync = new();
	private LiveSessionRecord record;
	private int disposed;

	internal LiveSessionWriter(
		LiveSessionRegistry registry,
		int pid,
		DateTimeOffset processStartUtc,
		IReadOnlyList<string> roots,
		AgentJournalMode mode)
	{
		this.registry = registry;
		record = new LiveSessionRecord(
			pid,
			processStartUtc,
			ClientName: null,
			ClientVersion: null,
			roots.Select(PathUtility.Normalize).Distinct(PathComparer.Default).ToArray(),
			registry.UtcNow,
			mode);
		registry.TryWrite(record);
		heartbeat = RunHeartbeatAsync();
	}

	public string Path => registry.GetPath(record.Pid);

	public void UpdateClient(string? name, string? version)
	{
		lock (sync)
		{
			ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
			if (StringComparer.Ordinal.Equals(record.ClientName, name) &&
				StringComparer.Ordinal.Equals(record.ClientVersion, version))
			{
				return;
			}
			record = record with
			{
				ClientName = name,
				ClientVersion = version,
				HeartbeatUtc = registry.UtcNow
			};
			registry.TryWrite(record);
		}
	}

	internal void WriteHeartbeat()
	{
		lock (sync)
		{
			if (Volatile.Read(ref disposed) != 0)
				return;
			record = record with { HeartbeatUtc = registry.UtcNow };
			registry.TryWrite(record);
		}
	}

	private async Task RunHeartbeatAsync()
	{
		try
		{
			using var timer = new PeriodicTimer(LiveSessionRegistry.HeartbeatInterval);
			while (await timer.WaitForNextTickAsync(cancellation.Token).ConfigureAwait(false))
			{
				try
				{
					WriteHeartbeat();
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					Trace.TraceWarning(
						"Live context heartbeat could not be written: {0}",
						exception.GetType().Name);
				}
			}
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
		{
		}
	}

	public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref disposed, 1) != 0)
			return;
		cancellation.Cancel();
		try
		{
			await heartbeat.ConfigureAwait(false);
		}
		finally
		{
			registry.Delete(record.Pid);
			cancellation.Dispose();
		}
	}
}
