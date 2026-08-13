using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Infrastructure.Secrets;

public sealed class PersistentSecretIdentityProvider : IPersistentSecretIdentityProvider, IDisposable
{
	private const int KeyByteLength = 32;
	private const int EnvelopeVersion = 1;
	private const int EnvelopeHeaderLength = 13;
	private const int EnvelopeChecksumLength = 32;
	private const int MaximumKeyFileBytes = 4 * 1024;
	private const string FolderName = "DevProjex";
	private const string FileName = "secret-mark-hmac.key";
	private const uint CryptProtectUiForbidden = 0x1;
	private static readonly byte[] EnvelopeMagic = "DPXSHK01"u8.ToArray();
	private static readonly TimeSpan DefaultTransientRetryCooldown = TimeSpan.FromMilliseconds(250);

	private readonly Func<string> _appDataPathProvider;
	private readonly object _sync = new();
	private readonly TimeSpan? _lockTimeout;
	private readonly TimeSpan _transientRetryCooldown;
	private readonly TimeProvider _timeProvider;
	private byte[]? _key;
	private Task<PersistentSecretIdentityAvailability>? _initializationTask;
	private DateTimeOffset _retryNotBeforeUtc;
	private PersistentSecretIdentityProviderState _state;
	private int _initializationAttemptCount;

	public PersistentSecretIdentityProvider(Func<string>? appDataPathProvider = null)
		: this(
			appDataPathProvider ?? UserDataPathResolver.GetConfigurationRoot,
			lockTimeout: null,
			DefaultTransientRetryCooldown,
			TimeProvider.System)
	{
	}

	internal PersistentSecretIdentityProvider(
		Func<string> appDataPathProvider,
		TimeSpan? lockTimeout,
		TimeSpan transientRetryCooldown,
		TimeProvider? timeProvider = null)
	{
		_appDataPathProvider = appDataPathProvider ?? throw new ArgumentNullException(nameof(appDataPathProvider));
		ArgumentOutOfRangeException.ThrowIfLessThan(transientRetryCooldown, TimeSpan.Zero);
		_lockTimeout = lockTimeout;
		_transientRetryCooldown = transientRetryCooldown;
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	public bool IsAvailable
	{
		get
		{
			lock (_sync)
				return _state == PersistentSecretIdentityProviderState.Ready;
		}
	}

	internal PersistentSecretIdentityProviderState State
	{
		get
		{
			lock (_sync)
				return _state;
		}
	}

	internal int InitializationAttemptCount => Volatile.Read(ref _initializationAttemptCount);

	public ValueTask<PersistentSecretIdentityAvailability> EnsureAvailableAsync(
		CancellationToken cancellationToken = default)
	{
		Task<PersistentSecretIdentityAvailability> initialization;
		lock (_sync)
		{
			if (_state == PersistentSecretIdentityProviderState.Disposed)
				return ValueTask.FromException<PersistentSecretIdentityAvailability>(
					new ObjectDisposedException(nameof(PersistentSecretIdentityProvider)));
			if (_state == PersistentSecretIdentityProviderState.Ready)
				return ValueTask.FromResult(PersistentSecretIdentityAvailability.Ready);
			if (_state == PersistentSecretIdentityProviderState.PermanentFault)
				return ValueTask.FromResult(PersistentSecretIdentityAvailability.PermanentlyUnavailable);
			if (_state == PersistentSecretIdentityProviderState.TransientFault &&
			    _timeProvider.GetUtcNow() < _retryNotBeforeUtc)
			{
				return ValueTask.FromResult(PersistentSecretIdentityAvailability.TemporarilyUnavailable);
			}

			if (_state == PersistentSecretIdentityProviderState.Initializing)
			{
				initialization = _initializationTask!;
			}
			else
			{
				_state = PersistentSecretIdentityProviderState.Initializing;
				Interlocked.Increment(ref _initializationAttemptCount);
				initialization = Task.Run(InitializeAndPublish);
				_initializationTask = initialization;
			}
		}

		return new ValueTask<PersistentSecretIdentityAvailability>(
			initialization.WaitAsync(cancellationToken));
	}

	public bool TryComputeDigest(ReadOnlySpan<char> normalizedValue, Span<byte> destination)
	{
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(
				_state == PersistentSecretIdentityProviderState.Disposed,
				this);
			if (_state != PersistentSecretIdentityProviderState.Ready ||
			    _key is not { Length: KeyByteLength } key ||
			    destination.Length < PersistentSecretIdentity.V2DigestByteLength)
			{
				return false;
			}

			var maximumByteCount = Encoding.UTF8.GetMaxByteCount(normalizedValue.Length);
			byte[]? rented = null;
			Span<byte> utf8 = maximumByteCount <= 2 * 1024
				? stackalloc byte[maximumByteCount]
				: (rented = ArrayPool<byte>.Shared.Rent(maximumByteCount));
			try
			{
				var byteCount = Encoding.UTF8.GetBytes(normalizedValue, utf8);
				HMACSHA256.HashData(key, utf8[..byteCount], destination);
				CryptographicOperations.ZeroMemory(utf8[..byteCount]);
				return true;
			}
			finally
			{
				if (rented is not null)
					ArrayPool<byte>.Shared.Return(rented, clearArray: true);
			}
		}
	}

	public void Dispose()
	{
		lock (_sync)
		{
			if (_state == PersistentSecretIdentityProviderState.Disposed)
				return;
			_state = PersistentSecretIdentityProviderState.Disposed;
			if (_key is not null)
				CryptographicOperations.ZeroMemory(_key);
			_key = null;
		}
	}

	private PersistentSecretIdentityAvailability InitializeAndPublish()
	{
		var result = TryLoadOrCreateKey();
		lock (_sync)
		{
			_initializationTask = null;
			if (_state == PersistentSecretIdentityProviderState.Disposed)
			{
				result.DisposeKey();
				return PersistentSecretIdentityAvailability.PermanentlyUnavailable;
			}

			switch (result.Status)
			{
				case KeyInitializationStatus.Ready:
					_key = result.Key;
					_state = PersistentSecretIdentityProviderState.Ready;
					return PersistentSecretIdentityAvailability.Ready;
				case KeyInitializationStatus.TransientFault:
					_state = PersistentSecretIdentityProviderState.TransientFault;
					_retryNotBeforeUtc = _timeProvider.GetUtcNow() + _transientRetryCooldown;
					return PersistentSecretIdentityAvailability.TemporarilyUnavailable;
				default:
					_state = PersistentSecretIdentityProviderState.PermanentFault;
					return PersistentSecretIdentityAvailability.PermanentlyUnavailable;
			}
		}
	}

	private KeyInitializationResult TryLoadOrCreateKey()
	{
		try
		{
			var fileSet = JsonStoreFileSet.Create(_appDataPathProvider, FolderName, FileName);
			if (!TryAcquireLock(fileSet, out var heldLock))
				return KeyInitializationResult.Transient();
			using var _ = heldLock;
			return LoadRecoverOrCreate(fileSet);
		}
		catch (Exception exception) when (exception is
			IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			return KeyInitializationResult.Transient();
		}
		catch (CryptographicException)
		{
			return KeyInitializationResult.Permanent();
		}
	}

	private bool TryAcquireLock(JsonStoreFileSet fileSet, out IDisposable? heldLock) =>
		_lockTimeout is { } timeout
			? CrossProcessFileLock.TryAcquire(fileSet, timeout, out heldLock)
			: CrossProcessFileLock.TryAcquire(fileSet, out heldLock);

	private static KeyInitializationResult LoadRecoverOrCreate(JsonStoreFileSet fileSet)
	{
		var primary = ReadKeyFile(fileSet.PrimaryPath);
		if (primary.Status == KeyFileStatus.TransientFault)
			return KeyInitializationResult.Transient();
		var backup = ReadKeyFile(fileSet.BackupPath);
		if (backup.Status == KeyFileStatus.TransientFault)
		{
			primary.DisposeKey();
			return KeyInitializationResult.Transient();
		}

		try
		{
			if (primary.IsReady)
			{
				if (backup.IsReady && !KeysEqual(primary.Key!, backup.Key!))
				{
					return KeyInitializationResult.Permanent();
				}
				var envelope = primary.IsEnvelope
					? primary.SerializedEnvelope!
					: CreateEnvelope(ProtectForStorage(primary.Key!));
				if (!backup.IsReady || !backup.IsEnvelope || primary.IsLegacy)
				{
					if (!WriteAtomic(fileSet.BackupPath, envelope))
						return KeyInitializationResult.Transient();
				}
				if (primary.IsLegacy && !WriteAtomic(fileSet.PrimaryPath, envelope))
					return KeyInitializationResult.Transient();
				return KeyInitializationResult.Ready(primary.TakeKey());
			}

			if (backup.IsReady)
			{
				var envelope = backup.IsEnvelope
					? backup.SerializedEnvelope!
					: CreateEnvelope(ProtectForStorage(backup.Key!));
				if (!WriteAtomic(fileSet.PrimaryPath, envelope) ||
				    (!backup.IsEnvelope && !WriteAtomic(fileSet.BackupPath, envelope)))
				{
					return KeyInitializationResult.Transient();
				}
				return KeyInitializationResult.Ready(backup.TakeKey());
			}

			if (primary.Status != KeyFileStatus.Missing || backup.Status != KeyFileStatus.Missing)
				return KeyInitializationResult.Permanent();

			var key = RandomNumberGenerator.GetBytes(KeyByteLength);
			try
			{
				var envelope = CreateEnvelope(ProtectForStorage(key));
				if (!WriteAtomic(fileSet.BackupPath, envelope) ||
				    !WriteAtomic(fileSet.PrimaryPath, envelope))
				{
					CryptographicOperations.ZeroMemory(key);
					return KeyInitializationResult.Transient();
				}
				return KeyInitializationResult.Ready(key);
			}
			catch
			{
				CryptographicOperations.ZeroMemory(key);
				throw;
			}
		}
		finally
		{
			primary.DisposeKey();
			backup.DisposeKey();
		}
	}

	private static KeyFileReadResult ReadKeyFile(string path)
	{
		if (!File.Exists(path))
			return KeyFileReadResult.Missing();
		try
		{
			if (!OperatingSystem.IsWindows() && !HasOwnerOnlyPermissions(path))
				return KeyFileReadResult.Permanent();
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			if (stream.Length is <= 0 or > MaximumKeyFileBytes)
				return KeyFileReadResult.Permanent();
			var bytes = new byte[checked((int)stream.Length)];
			stream.ReadExactly(bytes);
			try
			{
				if (bytes.AsSpan().StartsWith(EnvelopeMagic))
				{
					if (!TryParseEnvelope(bytes, out var payload))
						return KeyFileReadResult.Permanent();
					try
					{
						var key = UnprotectFromStorage(payload);
						return key is { Length: KeyByteLength }
							? KeyFileReadResult.Envelope(key, bytes.ToArray())
							: KeyFileReadResult.Permanent();
					}
					finally
					{
						CryptographicOperations.ZeroMemory(payload);
					}
				}

				var legacyKey = OperatingSystem.IsWindows()
					? UnprotectWindows(bytes)
					: bytes.Length == KeyByteLength ? bytes.ToArray() : null;
				return legacyKey is { Length: KeyByteLength }
					? KeyFileReadResult.Legacy(legacyKey)
					: KeyFileReadResult.Permanent();
			}
			finally
			{
				CryptographicOperations.ZeroMemory(bytes);
			}
		}
		catch (CryptographicException)
		{
			return KeyFileReadResult.Permanent();
		}
		catch (Exception exception) when (exception is
			IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			return KeyFileReadResult.Transient();
		}
	}

	private static byte[] CreateEnvelope(byte[] payload)
	{
		try
		{
			var envelope = new byte[EnvelopeHeaderLength + payload.Length + EnvelopeChecksumLength];
			EnvelopeMagic.CopyTo(envelope, 0);
			envelope[EnvelopeMagic.Length] = EnvelopeVersion;
			BinaryPrimitives.WriteInt32LittleEndian(
				envelope.AsSpan(EnvelopeMagic.Length + 1, sizeof(int)),
				payload.Length);
			payload.CopyTo(envelope, EnvelopeHeaderLength);
			SHA256.HashData(
				payload,
				envelope.AsSpan(EnvelopeHeaderLength + payload.Length, EnvelopeChecksumLength));
			return envelope;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(payload);
		}
	}

	private static bool TryParseEnvelope(byte[] envelope, out byte[] payload)
	{
		payload = [];
		if (envelope.Length < EnvelopeHeaderLength + EnvelopeChecksumLength ||
		    !envelope.AsSpan().StartsWith(EnvelopeMagic) ||
		    envelope[EnvelopeMagic.Length] != EnvelopeVersion)
		{
			return false;
		}
		var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(
			envelope.AsSpan(EnvelopeMagic.Length + 1, sizeof(int)));
		if (payloadLength <= 0 ||
		    payloadLength > MaximumKeyFileBytes - EnvelopeHeaderLength - EnvelopeChecksumLength ||
		    envelope.Length != EnvelopeHeaderLength + payloadLength + EnvelopeChecksumLength)
		{
			return false;
		}

		var payloadSpan = envelope.AsSpan(EnvelopeHeaderLength, payloadLength);
		Span<byte> checksum = stackalloc byte[EnvelopeChecksumLength];
		SHA256.HashData(payloadSpan, checksum);
		if (!CryptographicOperations.FixedTimeEquals(
			    checksum,
			    envelope.AsSpan(EnvelopeHeaderLength + payloadLength, EnvelopeChecksumLength)))
		{
			return false;
		}
		payload = payloadSpan.ToArray();
		return true;
	}

	private static bool WriteAtomic(string path, byte[] envelope)
	{
		var directory = Path.GetDirectoryName(path);
		if (string.IsNullOrWhiteSpace(directory))
			return false;
		Directory.CreateDirectory(directory);
		var tempPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
		try
		{
			var options = new FileStreamOptions
			{
				Mode = FileMode.CreateNew,
				Access = FileAccess.Write,
				Share = FileShare.None,
				Options = FileOptions.WriteThrough
			};
			if (!OperatingSystem.IsWindows())
				options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
			using (var stream = new FileStream(tempPath, options))
			{
				stream.Write(envelope);
				stream.Flush(flushToDisk: true);
			}
			var persisted = File.ReadAllBytes(tempPath);
			try
			{
				if (!persisted.AsSpan().SequenceEqual(envelope) ||
				    !TryParseEnvelope(persisted, out var validationPayload))
				{
					return false;
				}
				CryptographicOperations.ZeroMemory(validationPayload);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(persisted);
			}
			File.Move(tempPath, path, overwrite: true);
			if (!OperatingSystem.IsWindows())
				File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			return true;
		}
		finally
		{
			try
			{
				File.Delete(tempPath);
			}
			catch
			{
				// The committed file is authoritative; an abandoned temp is ignored on the next load.
			}
		}
	}

	private static bool HasOwnerOnlyPermissions(string path)
	{
		if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD())
			return false;
		var mode = File.GetUnixFileMode(path);
		var ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
		return (mode & ~ownerOnly) == 0;
	}

	private static bool KeysEqual(byte[] first, byte[] second) =>
		first.Length == second.Length && CryptographicOperations.FixedTimeEquals(first, second);

	private static byte[] ProtectForStorage(byte[] key) =>
		OperatingSystem.IsWindows() ? ProtectWindows(key)! : key.ToArray();

	private static byte[]? UnprotectFromStorage(byte[] payload) =>
		OperatingSystem.IsWindows() ? UnprotectWindows(payload) : payload.ToArray();

	private static byte[]? ProtectWindows(byte[] value) => TransformWindows(value, protect: true);
	private static byte[]? UnprotectWindows(byte[] value) => TransformWindows(value, protect: false);

	private static byte[]? TransformWindows(byte[] value, bool protect)
	{
		if (!OperatingSystem.IsWindows())
			throw new PlatformNotSupportedException();
		var inputPointer = Marshal.AllocHGlobal(value.Length);
		try
		{
			Marshal.Copy(value, 0, inputPointer, value.Length);
			var input = new DataBlob(value.Length, inputPointer);
			var succeeded = protect
				? CryptProtectData(
					ref input,
					null,
					IntPtr.Zero,
					IntPtr.Zero,
					IntPtr.Zero,
					CryptProtectUiForbidden,
					out var output)
				: CryptUnprotectData(
					ref input,
					IntPtr.Zero,
					IntPtr.Zero,
					IntPtr.Zero,
					IntPtr.Zero,
					CryptProtectUiForbidden,
					out output);
			if (!succeeded)
				throw new CryptographicException(Marshal.GetLastWin32Error());
			try
			{
				var result = new byte[output.Size];
				Marshal.Copy(output.Data, result, 0, output.Size);
				return result;
			}
			finally
			{
				if (output.Data != IntPtr.Zero)
					LocalFree(output.Data);
			}
		}
		finally
		{
			Marshal.Copy(new byte[value.Length], 0, inputPointer, value.Length);
			Marshal.FreeHGlobal(inputPointer);
		}
	}

	private sealed class KeyFileReadResult(
		KeyFileStatus status,
		byte[]? key = null,
		byte[]? serializedEnvelope = null)
	{
		public KeyFileStatus Status { get; } = status;
		public byte[]? Key { get; private set; } = key;
		public byte[]? SerializedEnvelope { get; } = serializedEnvelope;
		public bool IsReady => Status is KeyFileStatus.Envelope or KeyFileStatus.Legacy;
		public bool IsEnvelope => Status == KeyFileStatus.Envelope;
		public bool IsLegacy => Status == KeyFileStatus.Legacy;

		public byte[] TakeKey()
		{
			var value = Key!;
			Key = null;
			return value;
		}

		public void DisposeKey()
		{
			if (Key is not null)
				CryptographicOperations.ZeroMemory(Key);
			Key = null;
		}

		public static KeyFileReadResult Missing() => new(KeyFileStatus.Missing);
		public static KeyFileReadResult Transient() => new(KeyFileStatus.TransientFault);
		public static KeyFileReadResult Permanent() => new(KeyFileStatus.PermanentFault);
		public static KeyFileReadResult Legacy(byte[] key) => new(KeyFileStatus.Legacy, key);
		public static KeyFileReadResult Envelope(byte[] key, byte[] serialized) =>
			new(KeyFileStatus.Envelope, key, serialized);
	}

	private sealed class KeyInitializationResult(KeyInitializationStatus status, byte[]? key = null)
	{
		public KeyInitializationStatus Status { get; } = status;
		public byte[]? Key { get; } = key;

		public void DisposeKey()
		{
			if (Key is not null)
				CryptographicOperations.ZeroMemory(Key);
		}

		public static KeyInitializationResult Ready(byte[] key) => new(KeyInitializationStatus.Ready, key);
		public static KeyInitializationResult Transient() => new(KeyInitializationStatus.TransientFault);
		public static KeyInitializationResult Permanent() => new(KeyInitializationStatus.PermanentFault);
	}

	private enum KeyFileStatus
	{
		Missing,
		Envelope,
		Legacy,
		TransientFault,
		PermanentFault
	}

	private enum KeyInitializationStatus
	{
		Ready,
		TransientFault,
		PermanentFault
	}

	[StructLayout(LayoutKind.Sequential)]
	private readonly struct DataBlob(int size, IntPtr data)
	{
		public readonly int Size = size;
		public readonly IntPtr Data = data;
	}

	[DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CryptProtectData(
		ref DataBlob input,
		string? description,
		IntPtr optionalEntropy,
		IntPtr reserved,
		IntPtr prompt,
		uint flags,
		out DataBlob output);

	[DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CryptUnprotectData(
		ref DataBlob input,
		IntPtr description,
		IntPtr optionalEntropy,
		IntPtr reserved,
		IntPtr prompt,
		uint flags,
		out DataBlob output);

	[DllImport("kernel32.dll")]
	private static extern IntPtr LocalFree(IntPtr memory);
}

internal enum PersistentSecretIdentityProviderState
{
	Uninitialized,
	Initializing,
	Ready,
	TransientFault,
	PermanentFault,
	Disposed
}
