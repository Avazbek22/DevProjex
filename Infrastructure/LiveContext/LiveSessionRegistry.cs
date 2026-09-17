using System.Diagnostics;
using System.Text.Json;
using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Infrastructure.LiveContext;

public sealed record LiveSessionRecord(
	int Pid,
	DateTimeOffset ProcessStartUtc,
	string? ClientName,
	string? ClientVersion,
	IReadOnlyList<string> Roots,
	DateTimeOffset HeartbeatUtc);

public sealed class LiveSessionRegistry(
	Func<string>? stateRootProvider = null,
	TimeProvider? timeProvider = null,
	Func<int, DateTimeOffset?>? processStartProvider = null)
{
	public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
	public static readonly TimeSpan StaleHeartbeatAge = TimeSpan.FromSeconds(15);
	private const int MaximumRecordBytes = 64 * 1024;
	private readonly Func<string> stateRoot = stateRootProvider ?? UserDataPathResolver.GetStateRoot;
	private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
	private readonly Func<int, DateTimeOffset?> processStart = processStartProvider ?? TryGetProcessStartUtc;

	public string DirectoryPath => Path.Combine(stateRoot(), "live-sessions");

	public LiveSessionWriter Start(IReadOnlyList<string> roots) =>
		new(
			this,
			Environment.ProcessId,
			GetCurrentProcessStartUtc(),
			roots);

	internal LiveSessionWriter Start(
		int pid,
		DateTimeOffset processStartUtc,
		IReadOnlyList<string> roots) =>
		new(this, pid, processStartUtc, roots);

	public IReadOnlyList<LiveSessionRecord> ReadActive(string? projectRoot = null)
	{
		var directory = DirectoryPath;
		string[] paths;
		try
		{
			if (!Directory.Exists(directory))
				return [];
			paths = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return [];
		}

		var now = clock.GetUtcNow();
		var normalizedProjectRoot = TryNormalize(projectRoot);
		var records = new List<LiveSessionRecord>();
		foreach (var path in paths)
		{
			var record = TryRead(path);
			if (record is null || !IsAlive(record, now))
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
		Directory.CreateDirectory(directory);
		var path = GetPath(record.Pid);
		var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			var json = JsonSerializer.Serialize(
				record,
				InfrastructureJsonSerializerContext.Default.LiveSessionRecord);
			File.WriteAllText(temporary, json, new UTF8Encoding(false));
			File.Move(temporary, path, overwrite: true);
		}
		finally
		{
			TryDelete(temporary);
		}
	}

	internal void Delete(int pid) => TryDelete(GetPath(pid));

	private LiveSessionRecord? TryRead(string path)
	{
		try
		{
			var info = new FileInfo(path);
			if (!info.Exists || info.Length is <= 0 or > MaximumRecordBytes)
				return null;
			var record = JsonSerializer.Deserialize(
				File.ReadAllText(path),
				InfrastructureJsonSerializerContext.Default.LiveSessionRecord);
			return record is { Roots: not null } && record.Roots.Count <= 256
				? record
				: null;
		}
		catch (Exception exception) when (exception is
			   IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
		{
			return null;
		}
	}

	private static string? TryNormalize(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return null;
		try
		{
			return PathUtility.Normalize(path);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
		{
			return null;
		}
	}

	private bool IsAlive(LiveSessionRecord record, DateTimeOffset now)
	{
		if (record.Pid <= 0 || now - record.HeartbeatUtc > StaleHeartbeatAge)
			return false;
		var observedStart = processStart(record.Pid);
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
			   ArgumentException or InvalidOperationException or NotSupportedException)
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
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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
		IReadOnlyList<string> roots)
	{
		this.registry = registry;
		record = new LiveSessionRecord(
			pid,
			processStartUtc,
			ClientName: null,
			ClientVersion: null,
			roots.Select(PathUtility.Normalize).Distinct(PathComparer.Default).ToArray(),
			registry.UtcNow);
		registry.Write(record);
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
			registry.Write(record);
		}
	}

	internal void WriteHeartbeat()
	{
		lock (sync)
		{
			if (Volatile.Read(ref disposed) != 0)
				return;
			record = record with { HeartbeatUtc = registry.UtcNow };
			registry.Write(record);
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
