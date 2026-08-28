using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DevProjex.Mcp;

public sealed class McpPackRegistry : IDisposable
{
	private const int LineCheckpointInterval = 256;
	internal const long MaximumPackBytes = 200L * 1024 * 1024;
	internal const long MaximumSessionBytes = 1024L * 1024 * 1024;
	private const UnixFileMode PrivateDirectoryMode =
		UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
	private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
	private static readonly TimeSpan StaleAge = TimeSpan.FromHours(24);
	private static readonly ConcurrentDictionary<string, byte> ActiveSessions = new(PathComparer.Default);
	private readonly Dictionary<string, PackEntry> _packs = new(StringComparer.Ordinal);
	private readonly string _sessionDirectory;
	private readonly FileStream _sessionLease;
	private readonly long _maximumPackBytes;
	private readonly long _maximumSessionBytes;
	private readonly object _sync = new();
	private long _allocatedBytes;
	private int _activeCreates;
	private bool _disposed;

	public McpPackRegistry(string? tempRoot = null, TimeProvider? timeProvider = null)
		: this(tempRoot, timeProvider, MaximumPackBytes, MaximumSessionBytes)
	{
	}

	internal McpPackRegistry(
		string? tempRoot,
		TimeProvider? timeProvider,
		long maximumPackBytes,
		long maximumSessionBytes,
		Action<string>? onSessionDirectoryCreated = null)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPackBytes);
		ArgumentOutOfRangeException.ThrowIfLessThan(maximumSessionBytes, maximumPackBytes);
		_maximumPackBytes = maximumPackBytes;
		_maximumSessionBytes = maximumSessionBytes;
		TimeProvider = timeProvider ?? TimeProvider.System;
		var productDirectory = Path.Combine(tempRoot ?? Path.GetTempPath(), "DevProjex");
		EnsurePrivateDirectory(productDirectory);
		var baseDirectory = Path.Combine(productDirectory, "mcp");
		EnsurePrivateDirectory(baseDirectory);
		Scavenge(baseDirectory, TimeProvider.GetUtcNow());
		_sessionDirectory = Path.Combine(baseDirectory, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
		var sessionDirectoryExisted = Directory.Exists(_sessionDirectory);
		try
		{
			EnsurePrivateDirectory(_sessionDirectory);
			onSessionDirectoryCreated?.Invoke(_sessionDirectory);
			_sessionLease = OpenPrivateFile(
				Path.Combine(_sessionDirectory, ".session.lock"),
				FileMode.CreateNew,
				FileAccess.ReadWrite,
				FileShare.None,
				bufferSize: 4096,
				FileOptions.None);
		}
		catch
		{
			if (!sessionDirectoryExisted)
				TryDeleteSessionDirectory(_sessionDirectory);
			throw;
		}
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
		cancellationToken.ThrowIfCancellationRequested();
		BeginCreate();
		try
		{
			var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
			var path = Path.Combine(_sessionDirectory, id + ".pack");
			var reservation = new PackReservation(this);
			try
			{
				PackTextMetrics metrics;
				await using (var stream = OpenPrivateFile(
					             path,
					             FileMode.CreateNew,
					             FileAccess.Write,
					             FileShare.None,
					             64 * 1024,
					             FileOptions.Asynchronous | FileOptions.SequentialScan))
				{
					await using var bounded = new QuotaWriteStream(stream, reservation);
					await writer(bounded, cancellationToken).ConfigureAwait(false);
					metrics = bounded.CompleteMetrics();
				}

				var document = new McpPackDocument(
					id,
					path,
					metrics.Lines,
					metrics.Characters,
					reservation.Bytes)
				{
					LineCheckpoints = metrics.LineCheckpoints
				};
				lock (_sync)
				{
					ObjectDisposedException.ThrowIf(_disposed, this);
					cancellationToken.ThrowIfCancellationRequested();
					_packs.Add(id, new PackEntry(document, TimeProvider.GetUtcNow()));
				}
				reservation.Commit();
				return document;
			}
			catch
			{
				reservation.Dispose();
				TryDeletePackFile(path);
				throw;
			}
		}
		finally
		{
			EndCreate();
		}
	}

	public void Remove(string packId)
	{
		PackEntry? entry;
		lock (_sync)
		{
			if (!_packs.Remove(packId, out entry))
				return;
			_allocatedBytes -= entry.Document.Bytes;
		}
		TryDeletePackFile(entry.Document.Path);
	}

	public string Resolve(string packId) => ResolveDocument(packId).Path;

	internal McpPackDocument ResolveDocument(string packId)
	{
		if (string.IsNullOrWhiteSpace(packId))
			throw Expired();
		PackEntry? entry;
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			_packs.TryGetValue(packId, out entry);
		}
		if (entry is null || !File.Exists(entry.Document.Path))
			throw Expired();
		return entry.Document;
	}

	public void Dispose()
	{
		bool cleanupNow;
		lock (_sync)
		{
			if (_disposed)
				return;
			_disposed = true;
			cleanupNow = _activeCreates == 0;
		}

		if (cleanupNow)
			CleanupSessionDirectory();
	}

	private void BeginCreate()
	{
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			_activeCreates++;
		}
	}

	private void EndCreate()
	{
		bool cleanupNow;
		lock (_sync)
		{
			_activeCreates--;
			cleanupNow = _disposed && _activeCreates == 0;
		}
		if (cleanupNow)
			CleanupSessionDirectory();
	}

	private void CleanupSessionDirectory()
	{
		try
		{
			_sessionLease.Dispose();
			TryDeleteSessionDirectory(_sessionDirectory);
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

	private static void TryDeletePackFile(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private static void TryDeleteSessionDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path))
				Directory.Delete(path, recursive: true);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private static void SetPrivateDirectoryMode(string path)
	{
		if (!OperatingSystem.IsWindows())
			File.SetUnixFileMode(path, PrivateDirectoryMode);
	}

	private static void EnsurePrivateDirectory(string path)
	{
		if (OperatingSystem.IsWindows())
			Directory.CreateDirectory(path);
		else
			Directory.CreateDirectory(path, PrivateDirectoryMode);
		RejectLinkedDirectory(path);
		SetPrivateDirectoryMode(path);
		RejectLinkedDirectory(path);
	}

	private static void RejectLinkedDirectory(string path)
	{
		if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
			throw new IOException("MCP temporary storage cannot use a symbolic link or reparse point.");
	}

	private static FileStream OpenPrivateFile(
		string path,
		FileMode mode,
		FileAccess access,
		FileShare share,
		int bufferSize,
		FileOptions options)
	{
		var streamOptions = new FileStreamOptions
		{
			Mode = mode,
			Access = access,
			Share = share,
			BufferSize = bufferSize,
			Options = options
		};
		if (!OperatingSystem.IsWindows())
			streamOptions.UnixCreateMode = PrivateFileMode;
		var stream = new FileStream(path, streamOptions);
		try
		{
			if (!OperatingSystem.IsWindows())
				File.SetUnixFileMode(path, PrivateFileMode);
			return stream;
		}
		catch
		{
			stream.Dispose();
			throw;
		}
	}

	private static McpToolException Expired() =>
		new(
			McpErrorCodes.PackExpired,
			$"{McpErrorCodes.PackExpired}: pack expired or belongs to another server session; call pack_context again.");

	private static McpToolException TooLarge() =>
		new(
			McpErrorCodes.PackTooLarge,
			$"{McpErrorCodes.PackTooLarge}: pack storage limit exceeded. Narrow paths or include/exclude patterns, " +
			"use a lower detail level, or enable tracked_only, then call pack_context again.");

	private void Reserve(PackReservation reservation, int count)
	{
		if (count <= 0)
			return;
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (count > _maximumPackBytes - reservation.Bytes ||
			    count > _maximumSessionBytes - _allocatedBytes)
			{
				throw TooLarge();
			}
			reservation.Add(count);
			_allocatedBytes += count;
		}
	}

	private void Release(long bytes)
	{
		lock (_sync)
			_allocatedBytes -= bytes;
	}

	private sealed record PackEntry(McpPackDocument Document, DateTimeOffset CreatedUtc);

	private sealed class PackReservation(McpPackRegistry owner) : IDisposable
	{
		private bool _committed;
		private bool _disposed;

		public long Bytes { get; private set; }

		public void Reserve(int count) => owner.Reserve(this, count);

		public void Add(int count) => Bytes += count;

		public void Commit() => _committed = true;

		public void Dispose()
		{
			if (_disposed)
				return;
			_disposed = true;
			if (!_committed)
				owner.Release(Bytes);
		}
	}

	private sealed class QuotaWriteStream(Stream inner, PackReservation reservation) : Stream
	{
		private static readonly SearchValues<byte> LineEndingBytes = SearchValues.Create("\r\n"u8);
		private readonly Decoder _decoder = new UTF8Encoding(false, true).GetDecoder();
		private readonly char[] _characterBuffer = new char[16 * 1024];
		private long _characters;
		private int _lineBreaks;
		private bool _previousCarriageReturn;
		private bool _metricsCompleted;
		private readonly List<McpPackLineCheckpoint> _lineCheckpoints = [new(1, 0)];
		private long _indexedBytes;
		private long _pendingCarriageReturnLineOffset = -1;
		private int _indexedLineNumber = 1;

		public override bool CanRead => false;
		public override bool CanSeek => false;
		public override bool CanWrite => true;
		public override long Length => inner.Length;
		public override long Position
		{
			get => inner.Position;
			set => throw new NotSupportedException();
		}

		public override void Flush() => inner.Flush();

		public override Task FlushAsync(CancellationToken cancellationToken) =>
			inner.FlushAsync(cancellationToken);

		public override void Write(byte[] buffer, int offset, int count)
		{
			reservation.Reserve(count);
			inner.Write(buffer, offset, count);
			AppendMetrics(buffer.AsSpan(offset, count));
		}

		public override void Write(ReadOnlySpan<byte> buffer)
		{
			reservation.Reserve(buffer.Length);
			inner.Write(buffer);
			AppendMetrics(buffer);
		}

		public override async ValueTask WriteAsync(
			ReadOnlyMemory<byte> buffer,
			CancellationToken cancellationToken = default)
		{
			reservation.Reserve(buffer.Length);
			await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
			AppendMetrics(buffer.Span);
		}

		public override async Task WriteAsync(
			byte[] buffer,
			int offset,
			int count,
			CancellationToken cancellationToken)
		{
			reservation.Reserve(count);
			await inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
			AppendMetrics(buffer.AsSpan(offset, count));
		}

		public PackTextMetrics CompleteMetrics()
		{
			if (_metricsCompleted)
				throw new InvalidOperationException("Pack text metrics have already been completed.");
			_metricsCompleted = true;
			CompletePendingCarriageReturn();
			Decode([], flush: true);
			return new PackTextMetrics(
				_characters,
				_characters == 0 ? 0 : checked(_lineBreaks + 1),
				_lineCheckpoints.ToArray());
		}

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (disposing)
				inner.Dispose();
			base.Dispose(disposing);
		}

		public override async ValueTask DisposeAsync()
		{
			await inner.DisposeAsync().ConfigureAwait(false);
			GC.SuppressFinalize(this);
		}

		private void AppendMetrics(ReadOnlySpan<byte> bytes)
		{
			if (_metricsCompleted)
				throw new InvalidOperationException("Cannot write after pack text metrics are completed.");
			AppendLineCheckpoints(bytes);
			Decode(bytes, flush: false);
		}

		private void AppendLineCheckpoints(ReadOnlySpan<byte> bytes)
		{
			var index = 0;
			if (_pendingCarriageReturnLineOffset >= 0 && !bytes.IsEmpty)
			{
				if (bytes[0] == (byte)'\n')
				{
					RecordLineStart(_indexedBytes + 1);
					_pendingCarriageReturnLineOffset = -1;
					index = 1;
				}
				else
					CompletePendingCarriageReturn();
			}

			while (index < bytes.Length)
			{
				var relativeOffset = bytes[index..].IndexOfAny(LineEndingBytes);
				if (relativeOffset < 0)
					break;
				index += relativeOffset;
				var value = bytes[index];
				var absoluteOffset = _indexedBytes + index;
				if (value == (byte)'\r')
				{
					if (index + 1 < bytes.Length)
					{
						if (bytes[index + 1] == (byte)'\n')
						{
							RecordLineStart(absoluteOffset + 2);
							index += 2;
							continue;
						}
						RecordLineStart(absoluteOffset + 1);
					}
					else
						_pendingCarriageReturnLineOffset = absoluteOffset + 1;
				}
				else
					RecordLineStart(absoluteOffset + 1);
				index++;
			}
			_indexedBytes += bytes.Length;
		}

		private void CompletePendingCarriageReturn()
		{
			if (_pendingCarriageReturnLineOffset < 0)
				return;
			RecordLineStart(_pendingCarriageReturnLineOffset);
			_pendingCarriageReturnLineOffset = -1;
		}

		private void RecordLineStart(long byteOffset)
		{
			_indexedLineNumber++;
			if ((_indexedLineNumber - 1) % LineCheckpointInterval == 0)
				_lineCheckpoints.Add(new McpPackLineCheckpoint(_indexedLineNumber, byteOffset));
		}

		private void Decode(ReadOnlySpan<byte> bytes, bool flush)
		{
			do
			{
				_decoder.Convert(
					bytes,
					_characterBuffer,
					flush,
					out var bytesUsed,
					out var charactersUsed,
					out var completed);
				bytes = bytes[bytesUsed..];
				AppendCharacters(_characterBuffer.AsSpan(0, charactersUsed));
				if (completed)
					break;
			} while (!bytes.IsEmpty || flush);
		}

		private void AppendCharacters(ReadOnlySpan<char> characters)
		{
			_characters += characters.Length;
			foreach (var character in characters)
			{
				if (character == '\n')
				{
					if (!_previousCarriageReturn)
						_lineBreaks++;
					_previousCarriageReturn = false;
				}
				else
				{
					if (character == '\r')
						_lineBreaks++;
					_previousCarriageReturn = character == '\r';
				}
			}
		}
	}

	private readonly record struct PackTextMetrics(
		long Characters,
		int Lines,
		McpPackLineCheckpoint[] LineCheckpoints);
}

internal readonly record struct McpPackLineCheckpoint(int LineNumber, long ByteOffset);

public sealed record McpPackDocument(string Id, string Path, int Lines, long Characters, long Bytes)
{
	internal IReadOnlyList<McpPackLineCheckpoint> LineCheckpoints { get; init; } =
		[new McpPackLineCheckpoint(1, 0)];

	internal McpPackLineCheckpoint ResolveLineCheckpoint(int lineNumber)
	{
		var low = 0;
		var high = LineCheckpoints.Count - 1;
		while (low <= high)
		{
			var middle = low + ((high - low) / 2);
			var checkpoint = LineCheckpoints[middle];
			if (checkpoint.LineNumber == lineNumber)
				return checkpoint;
			if (checkpoint.LineNumber < lineNumber)
				low = middle + 1;
			else
				high = middle - 1;
		}

		return LineCheckpoints[Math.Max(0, high)];
	}
}
