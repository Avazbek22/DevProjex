using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DevProjex.Mcp;

internal sealed class McpRootJailFileStreamOpener(McpRootRegistry roots)
{
	private const int DarwinGetPath = 50;
	private const int DarwinPathBufferLength = 1024;

	public FileStream OpenRead(
		string path,
		int bufferSize,
		FileShare fileShare,
		bool asynchronous)
	{
		UnixFileTypeInspector.EnsureRegularFile(path);
		var lexicalRoot = roots.FindLexicalRoot(path);
		var stream = new FileStream(
			path,
			FileMode.Open,
			FileAccess.Read,
			fileShare,
			bufferSize,
			FileOptions.SequentialScan |
			(asynchronous ? FileOptions.Asynchronous : FileOptions.None));
		try
		{
			if (lexicalRoot is not null)
			{
				var openedPath = ResolveOpenedPath(stream.SafeFileHandle);
				roots.EnsureOpenedPathIsWithin(lexicalRoot, path, openedPath);
			}
			return stream;
		}
		catch
		{
			stream.Dispose();
			throw;
		}
	}

	internal static string ResolveOpenedPath(SafeFileHandle handle)
	{
		ArgumentNullException.ThrowIfNull(handle);
		if (handle.IsInvalid || handle.IsClosed)
			throw new IOException("The opened project file handle is unavailable.");
		if (OperatingSystem.IsWindows())
			return ResolveWindowsPath(handle);
		if (OperatingSystem.IsLinux())
			return ResolveLinuxPath(handle);
		if (OperatingSystem.IsMacOS())
			return ResolveDarwinPath(handle);
		throw new PlatformNotSupportedException("MCP root-jail file reads require Windows, Linux, or macOS.");
	}

	private static string ResolveWindowsPath(SafeFileHandle handle)
	{
		var capacity = 512;
		while (capacity <= 32_768)
		{
			var path = new StringBuilder(capacity);
			var length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
			if (length == 0)
				throw NativeFailure("The final Windows path of an opened project file could not be resolved.");
			if (length < path.Capacity)
				return NormalizeWindowsPath(path.ToString());
			capacity = checked((int)length + 1);
		}
		throw new IOException("The final Windows path of an opened project file is too long.");
	}

	private static string NormalizeWindowsPath(string path)
	{
		const string uncPrefix = @"\\?\UNC\";
		const string extendedPrefix = @"\\?\";
		if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
			path = @"\\" + path[uncPrefix.Length..];
		else if (path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase))
			path = path[extendedPrefix.Length..];
		return Path.GetFullPath(path);
	}

	private static string ResolveLinuxPath(SafeFileHandle handle)
	{
		var descriptor = handle.DangerousGetHandle().ToInt32();
		var descriptorPath = $"/proc/self/fd/{descriptor}";
		for (var capacity = 1024; capacity <= 32_768; capacity *= 2)
		{
			var bytes = new byte[capacity];
			var length = ReadLink(descriptorPath, bytes, (nuint)bytes.Length);
			if (length < 0)
				throw NativeFailure("The final Linux path of an opened project file could not be resolved.");
			if (length < bytes.Length)
				return Path.GetFullPath(Encoding.UTF8.GetString(bytes, 0, checked((int)length)));
		}
		throw new IOException("The final Linux path of an opened project file is too long.");
	}

	private static string ResolveDarwinPath(SafeFileHandle handle)
	{
		var descriptor = handle.DangerousGetHandle().ToInt32();
		var pathBuffer = new byte[DarwinPathBufferLength];
		var pinned = GCHandle.Alloc(pathBuffer, GCHandleType.Pinned);
		try
		{
			var result = ReadDarwinPath(descriptor, pinned.AddrOfPinnedObject());
			if (result != 0)
				throw NativeFailure("The final macOS path of an opened project file could not be resolved.");
		}
		finally
		{
			pinned.Free();
		}

		var terminator = Array.IndexOf(pathBuffer, (byte)0);
		if (terminator <= 0)
			throw new IOException("The final macOS path of an opened project file is invalid.");
		return Path.GetFullPath(Encoding.UTF8.GetString(pathBuffer, 0, terminator));
	}

	private static int ReadDarwinPath(int descriptor, IntPtr pathBuffer)
	{
		if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
		{
			// Darwin ARM64 passes fcntl's variadic path pointer in its ABI stack slot.
			return DarwinFcntlArm64(
				descriptor,
				DarwinGetPath,
				0,
				0,
				0,
				0,
				0,
				0,
				pathBuffer);
		}
		return DarwinFcntl(descriptor, DarwinGetPath, pathBuffer);
	}

	private static IOException NativeFailure(string message) =>
		new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern uint GetFinalPathNameByHandle(
		SafeFileHandle handle,
		StringBuilder path,
		uint pathLength,
		uint flags);

	[DllImport("libc", EntryPoint = "readlink", SetLastError = true)]
	private static extern nint ReadLink(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string path,
		byte[] buffer,
		nuint bufferSize);

	[DllImport("libSystem.B.dylib", EntryPoint = "fcntl", SetLastError = true)]
	private static extern int DarwinFcntl(int descriptor, int command, IntPtr pathBuffer);

	[DllImport("libSystem.B.dylib", EntryPoint = "fcntl", SetLastError = true)]
	private static extern int DarwinFcntlArm64(
		int descriptor,
		int command,
		nint register2,
		nint register3,
		nint register4,
		nint register5,
		nint register6,
		nint register7,
		IntPtr pathBuffer);
}
