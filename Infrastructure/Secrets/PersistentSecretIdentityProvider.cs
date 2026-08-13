using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Infrastructure.Secrets;

public sealed class PersistentSecretIdentityProvider : IPersistentSecretIdentityProvider, IDisposable
{
	private const int KeyByteLength = 32;
	// DPAPI payloads are small. A four-KiB cap prevents a corrupted local key file from becoming
	// an unbounded allocation while leaving ample room for platform protection metadata.
	private const int MaximumKeyFileBytes = 4 * 1024;
	private const string FolderName = "DevProjex";
	private const string FileName = "secret-mark-hmac.key";
	private const uint CryptProtectUiForbidden = 0x1;
	private readonly Func<string> _appDataPathProvider;
	private readonly object _sync = new();
	private byte[]? _key;
	private bool _initialized;
	private bool _disposed;

	public PersistentSecretIdentityProvider(Func<string>? appDataPathProvider = null)
	{
		_appDataPathProvider = appDataPathProvider ?? UserDataPathResolver.GetConfigurationRoot;
	}

	public bool IsAvailable
	{
		get
		{
			lock (_sync)
				return !_disposed && GetOrCreateKeyLocked() is { Length: KeyByteLength };
		}
	}

	public bool TryComputeDigest(ReadOnlySpan<char> normalizedValue, Span<byte> destination)
	{
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			var key = GetOrCreateKeyLocked();
			if (key is null || destination.Length < PersistentSecretIdentity.V2DigestByteLength)
				return false;

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
			if (_disposed)
				return;
			_disposed = true;
			if (_key is not null)
				CryptographicOperations.ZeroMemory(_key);
			_key = null;
		}
	}

	private byte[]? GetOrCreateKeyLocked()
	{
		if (_initialized)
			return _key;
		_initialized = true;
		_key = TryLoadOrCreateKey(_appDataPathProvider);
		return _key;
	}

	private static byte[]? TryLoadOrCreateKey(Func<string> appDataPathProvider)
	{
		try
		{
			var fileSet = JsonStoreFileSet.Create(appDataPathProvider, FolderName, FileName);
			if (!CrossProcessFileLock.TryAcquire(fileSet, out var heldLock))
				return null;
			using var _ = heldLock;
			if (File.Exists(fileSet.PrimaryPath))
				return TryReadKey(fileSet.PrimaryPath);

			var key = RandomNumberGenerator.GetBytes(KeyByteLength);
			try
			{
				var payload = OperatingSystem.IsWindows()
					? ProtectWindows(key)
					: key.ToArray();
				if (payload is null || !TryWriteKey(fileSet.PrimaryPath, payload))
					return null;
				return key;
			}
			catch
			{
				CryptographicOperations.ZeroMemory(key);
				throw;
			}
		}
		catch (Exception exception) when (exception is
			IOException or UnauthorizedAccessException or CryptographicException or
			ArgumentException or NotSupportedException)
		{
			return null;
		}
	}

	private static byte[]? TryReadKey(string path)
	{
		if (!OperatingSystem.IsWindows())
		{
			var mode = File.GetUnixFileMode(path);
			var ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
			if ((mode & ~ownerOnly) != 0)
				return null;
		}

		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		if (stream.Length is <= 0 or > MaximumKeyFileBytes)
			return null;
		var payload = new byte[checked((int)stream.Length)];
		stream.ReadExactly(payload);
		try
		{
			var key = OperatingSystem.IsWindows() ? UnprotectWindows(payload) : payload.ToArray();
			if (key is { Length: KeyByteLength })
				return key;
			if (key is not null)
				CryptographicOperations.ZeroMemory(key);
			return null;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(payload);
		}
	}

	private static bool TryWriteKey(string path, byte[] payload)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var options = new FileStreamOptions
		{
			Mode = FileMode.CreateNew,
			Access = FileAccess.Write,
			Share = FileShare.None,
			Options = FileOptions.WriteThrough
		};
		if (!OperatingSystem.IsWindows())
			options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
		using var stream = new FileStream(path, options);
		stream.Write(payload);
		stream.Flush(flushToDisk: true);
		return true;
	}

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
