using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DevProjex.Mcp;

public sealed class McpPackRegistry : IDisposable
{
	private static readonly TimeSpan StaleAge = TimeSpan.FromHours(24);
	private static readonly ConcurrentDictionary<string, byte> ActiveSessions = new(PathComparer.Default);
	private readonly Dictionary<string, PackEntry> _packs = new(StringComparer.Ordinal);
	private readonly string _sessionDirectory;
	private readonly FileStream _sessionLease;
	private readonly object _sync = new();
	private bool _disposed;

	public McpPackRegistry(string? tempRoot = null, TimeProvider? timeProvider = null)
	{
		TimeProvider = timeProvider ?? TimeProvider.System;
		var baseDirectory = Path.Combine(
			tempRoot ?? Path.GetTempPath(),
			"DevProjex",
			"mcp");
		Directory.CreateDirectory(baseDirectory);
		SetPrivateDirectoryMode(baseDirectory);
		Scavenge(baseDirectory, TimeProvider.GetUtcNow());
		_sessionDirectory = Path.Combine(baseDirectory, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
		Directory.CreateDirectory(_sessionDirectory);
		SetPrivateDirectoryMode(_sessionDirectory);
		_sessionLease = new FileStream(
			Path.Combine(_sessionDirectory, ".session.lock"),
			FileMode.CreateNew,
			FileAccess.ReadWrite,
			FileShare.None);
		SetPrivateFileMode(_sessionLease.Name);
		ActiveSessions.TryAdd(_sessionDirectory, 0);
	}

	internal TimeProvider TimeProvider { get; }
	internal string SessionDirectory => _sessionDirectory;

	public async Task<string> StoreAsync(string content, CancellationToken cancellationToken)
	{
		var document = await CreateAsync(
			async (stream, token) =>
			{
				await using var writer = new StreamWriter(
					stream,
					new UTF8Encoding(false),
					bufferSize: 16 * 1024,
					leaveOpen: true);
				await writer.WriteAsync(content.AsMemory(), token).ConfigureAwait(false);
			},
			cancellationToken).ConfigureAwait(false);
		return document.Id;
	}

	public async Task<McpPackDocument> CreateAsync(
		Func<Stream, CancellationToken, Task> writer,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(writer);
		ObjectDisposedException.ThrowIf(_disposed, this);
		var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
		var path = Path.Combine(_sessionDirectory, id + ".pack");
		try
		{
			await using (var stream = new FileStream(
				             path,
				             FileMode.CreateNew,
				             FileAccess.Write,
				             FileShare.None,
				             64 * 1024,
				             FileOptions.Asynchronous | FileOptions.SequentialScan))
			{
				SetPrivateFileMode(path);
				await writer(stream, cancellationToken).ConfigureAwait(false);
			}

			var (characters, lines) = await MeasureAsync(path, cancellationToken).ConfigureAwait(false);
			var document = new McpPackDocument(id, path, lines, characters);
			lock (_sync)
				_packs.Add(id, new PackEntry(document, TimeProvider.GetUtcNow()));
			return document;
		}
		catch
		{
			try
			{
				File.Delete(path);
			}
			catch (IOException)
			{
			}
			throw;
		}
	}

	public void Remove(string packId)
	{
		PackEntry? entry;
		lock (_sync)
		{
			if (!_packs.Remove(packId, out entry))
				return;
		}
		try
		{
			File.Delete(entry.Document.Path);
		}
		catch (IOException)
		{
		}
	}

	public string Resolve(string packId) => ResolveDocument(packId).Path;

	internal McpPackDocument ResolveDocument(string packId)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (string.IsNullOrWhiteSpace(packId))
			throw Expired();
		PackEntry? entry;
		lock (_sync)
			_packs.TryGetValue(packId, out entry);
		if (entry is null || !File.Exists(entry.Document.Path))
			throw Expired();
		return entry.Document;
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		try
		{
			_sessionLease.Dispose();
			try
			{
				if (Directory.Exists(_sessionDirectory))
					Directory.Delete(_sessionDirectory, recursive: true);
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
		finally
		{
			ActiveSessions.TryRemove(_sessionDirectory, out _);
		}
	}

	private static void Scavenge(string baseDirectory, DateTimeOffset now)
	{
		if (!Directory.Exists(baseDirectory))
			return;
		foreach (var directory in Directory.EnumerateDirectories(baseDirectory))
		{
			try
			{
				if (!IsOwnedSessionDirectory(directory))
					continue;
				if (ActiveSessions.ContainsKey(directory))
					continue;
				if (now - Directory.GetLastWriteTimeUtc(directory) <= StaleAge)
					continue;
				var leasePath = Path.Combine(directory, ".session.lock");
				using var lease = new FileStream(
					leasePath,
					FileMode.Open,
					FileAccess.ReadWrite,
					FileShare.Delete);
				Directory.Delete(directory, recursive: true);
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
	}

	private static bool IsOwnedSessionDirectory(string directory)
	{
		var name = Path.GetFileName(directory);
		if (name.Length != 32 || !name.All(IsLowerHexDigit))
			return false;

		var directoryAttributes = File.GetAttributes(directory);
		if (!directoryAttributes.HasFlag(FileAttributes.Directory) ||
		    directoryAttributes.HasFlag(FileAttributes.ReparsePoint))
		{
			return false;
		}

		var leasePath = Path.Combine(directory, ".session.lock");
		if (!File.Exists(leasePath))
			return false;
		var leaseAttributes = File.GetAttributes(leasePath);
		return !leaseAttributes.HasFlag(FileAttributes.Directory) &&
		       !leaseAttributes.HasFlag(FileAttributes.ReparsePoint);
	}

	private static bool IsLowerHexDigit(char value) =>
		value is >= '0' and <= '9' or >= 'a' and <= 'f';

	private static void SetPrivateDirectoryMode(string path)
	{
		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(
				path,
				UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
	}

	private static void SetPrivateFileMode(string path)
	{
		if (!OperatingSystem.IsWindows())
			File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
	}

	private static McpToolException Expired() =>
		new(
			McpErrorCodes.PackExpired,
			$"{McpErrorCodes.PackExpired}: pack expired or belongs to another server session; call pack_context again.");

	private sealed record PackEntry(McpPackDocument Document, DateTimeOffset CreatedUtc);

	private static async Task<(long Characters, int Lines)> MeasureAsync(
		string path,
		CancellationToken cancellationToken)
	{
		using var reader = new StreamReader(
			path,
			new UTF8Encoding(false, true),
			detectEncodingFromByteOrderMarks: false,
			bufferSize: 16 * 1024);
		var buffer = new char[16 * 1024];
		long characters = 0;
		var lineBreaks = 0;
		var previousCarriageReturn = false;
		while (true)
		{
			var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
			if (read == 0)
				break;
			characters += read;
			for (var index = 0; index < read; index++)
			{
				var character = buffer[index];
				if (character == '\n')
				{
					if (!previousCarriageReturn)
						lineBreaks++;
					previousCarriageReturn = false;
				}
				else
				{
					if (character == '\r')
						lineBreaks++;
					previousCarriageReturn = character == '\r';
				}
			}
		}
		return (characters, characters == 0 ? 0 : checked(lineBreaks + 1));
	}
}

public sealed record McpPackDocument(string Id, string Path, int Lines, long Characters);
