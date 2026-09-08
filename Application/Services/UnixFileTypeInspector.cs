using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DevProjex.Application.Services;

public static class UnixFileTypeInspector
{
	private const uint FileTypeMask = 0xF000;
	private const uint RegularFileType = 0x8000;
	private const int ReadOnly = 0;
	private const int GetFileStatusFlags = 3;
	private const int SetFileStatusFlags = 4;

	public static bool IsPhysicalDirectoryOrRegularFile(string path, FileAttributes attributes) =>
		!attributes.HasFlag(FileAttributes.ReparsePoint) &&
		(attributes.HasFlag(FileAttributes.Directory) || IsRegularFile(path));

	public static bool IsRegularFile(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		if (OperatingSystem.IsWindows())
			return true;

		NativeStatBuffer buffer;
		var result = OperatingSystem.IsMacOS()
			? RuntimeInformation.ProcessArchitecture == Architecture.X64
				? MacOsX64LStat(path, out buffer)
				: MacOsLStat(path, out buffer)
			: LinuxLStat(path, out buffer);
		if (result != 0)
			ThrowForLastError(path);

		return (GetMode(buffer) & FileTypeMask) == RegularFileType;
	}

	public static void EnsureRegularFile(string path)
	{
		if (!IsRegularFile(path))
			throw new IOException("The source entry is not a regular file.");
	}

	internal static FileStream OpenRegularFileForSequentialRead(
		string path,
		int bufferSize,
		FileShare fileShare,
		bool asynchronous,
		Action? beforeOpen = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
		beforeOpen?.Invoke();
		if (OperatingSystem.IsWindows())
		{
			return new FileStream(
				path,
				FileMode.Open,
				FileAccess.Read,
				fileShare,
				bufferSize,
				FileOptions.SequentialScan |
				(asynchronous ? FileOptions.Asynchronous : FileOptions.None));
		}

		var nonBlocking = OperatingSystem.IsMacOS() ? 0x4 : 0x800;
		var closeOnExec = OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;
		var noFollow = OperatingSystem.IsMacOS() ? 0x100 : 0x20000;
		var descriptor = OperatingSystem.IsMacOS()
			? MacOsOpen(path, ReadOnly | nonBlocking | closeOnExec | noFollow)
			: LinuxOpen(path, ReadOnly | nonBlocking | closeOnExec | noFollow);
		if (descriptor < 0)
			ThrowForLastError(path);

		var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
		try
		{
			NativeStatBuffer buffer;
			var statusResult = OperatingSystem.IsMacOS()
				? RuntimeInformation.ProcessArchitecture == Architecture.X64
					? MacOsX64FStat(descriptor, out buffer)
					: MacOsFStat(descriptor, out buffer)
				: LinuxFStat(descriptor, out buffer);
			if (statusResult != 0)
				ThrowForLastError(path);
			if ((GetMode(buffer) & FileTypeMask) != RegularFileType)
				throw new IOException("The source entry is not a regular file.");

			var currentFlags = OperatingSystem.IsMacOS()
				? MacOsFcntl(descriptor, GetFileStatusFlags, 0)
				: LinuxFcntl(descriptor, GetFileStatusFlags, 0);
			if (currentFlags < 0)
				ThrowForLastError(path);
			var blockingResult = OperatingSystem.IsMacOS()
				? MacOsFcntl(descriptor, SetFileStatusFlags, currentFlags & ~nonBlocking)
				: LinuxFcntl(descriptor, SetFileStatusFlags, currentFlags & ~nonBlocking);
			if (blockingResult != 0)
				ThrowForLastError(path);

			var stream = new FileStream(handle, FileAccess.Read, bufferSize, asynchronous);
			handle = null!;
			return stream;
		}
		finally
		{
			handle?.Dispose();
		}
	}

	private static uint GetMode(NativeStatBuffer buffer) =>
		OperatingSystem.IsMacOS()
			? buffer.MacOsMode
			: RuntimeInformation.ProcessArchitecture == Architecture.Arm64
				? buffer.LinuxArm64Mode
				: buffer.LinuxX64Mode;

	private static void ThrowForLastError(string path)
	{
		var error = Marshal.GetLastPInvokeError();
		throw error switch
		{
			2 => new FileNotFoundException("The source file was not found.", path),
			13 => new UnauthorizedAccessException("Access to the source file was denied."),
			_ => new IOException("The source file type could not be inspected.", new Win32Exception(error))
		};
	}

	[DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
	private static extern int LinuxLStat(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string path,
		out NativeStatBuffer buffer);

	[DllImport("libSystem.B.dylib", EntryPoint = "lstat", SetLastError = true)]
	private static extern int MacOsLStat(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string path,
		out NativeStatBuffer buffer);

	[DllImport("libSystem.B.dylib", EntryPoint = "lstat$INODE64", SetLastError = true)]
	private static extern int MacOsX64LStat(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string path,
		out NativeStatBuffer buffer);

	[DllImport("libc", EntryPoint = "open", SetLastError = true)]
	private static extern int LinuxOpen(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string path,
		int flags);

	[DllImport("libSystem.B.dylib", EntryPoint = "open", SetLastError = true)]
	private static extern int MacOsOpen(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string path,
		int flags);

	[DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
	private static extern int LinuxFStat(int descriptor, out NativeStatBuffer buffer);

	[DllImport("libSystem.B.dylib", EntryPoint = "fstat", SetLastError = true)]
	private static extern int MacOsFStat(int descriptor, out NativeStatBuffer buffer);

	[DllImport("libSystem.B.dylib", EntryPoint = "fstat$INODE64", SetLastError = true)]
	private static extern int MacOsX64FStat(int descriptor, out NativeStatBuffer buffer);

	[DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
	private static extern int LinuxFcntl(int descriptor, int command, int argument);

	[DllImport("libSystem.B.dylib", EntryPoint = "fcntl", SetLastError = true)]
	private static extern int MacOsFcntl(int descriptor, int command, int argument);

	[StructLayout(LayoutKind.Explicit, Size = 256)]
	private struct NativeStatBuffer
	{
		[FieldOffset(4)] public ushort MacOsMode;
		[FieldOffset(16)] public uint LinuxArm64Mode;
		[FieldOffset(24)] public uint LinuxX64Mode;
	}
}
