using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DevProjex.Application.Services;

internal static class UnixFileTypeInspector
{
	private const uint FileTypeMask = 0xF000;
	private const uint RegularFileType = 0x8000;

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

		var mode = OperatingSystem.IsMacOS()
			? buffer.MacOsMode
			: RuntimeInformation.ProcessArchitecture == Architecture.Arm64
				? buffer.LinuxArm64Mode
				: buffer.LinuxX64Mode;
		return (mode & FileTypeMask) == RegularFileType;
	}

	public static void EnsureRegularFile(string path)
	{
		if (!IsRegularFile(path))
			throw new IOException("The source entry is not a regular file.");
	}

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

	[StructLayout(LayoutKind.Explicit, Size = 256)]
	private struct NativeStatBuffer
	{
		[FieldOffset(4)] public ushort MacOsMode;
		[FieldOffset(16)] public uint LinuxArm64Mode;
		[FieldOffset(24)] public uint LinuxX64Mode;
	}
}
