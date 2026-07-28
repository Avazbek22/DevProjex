using System.Runtime.InteropServices;

namespace DevProjex.Terminal.CommandLine;

public static class WindowsConsoleBridge
{
	private const uint AttachParentProcess = 0xFFFFFFFF;
	private const int ErrorAccessDenied = 5;

	public static bool EnsureAttached()
	{
		if (!OperatingSystem.IsWindows())
			return HasUsableConsole();

		try
		{
			if (GetConsoleWindow() != IntPtr.Zero)
			{
				ResetStandardStreams();
				return true;
			}

			var inheritedHandles = CaptureRedirectedStandardHandles();
			if (!AttachConsole(AttachParentProcess))
			{
				var error = Marshal.GetLastWin32Error();
				if (error != ErrorAccessDenied)
				{
					if (!HasRedirectedStandardStream() || !HasUsableStandardHandle())
						return false;

					ResetStandardStreams();
					return true;
				}
			}
			else
			{
				// A Windows-subsystem executable needs AttachConsole for inherited
				// console pseudo-handles to become interactive. Windows may replace
				// real file or pipe redirections during attach, so restore only those.
				RestoreRedirectedStandardHandles(inheritedHandles);
			}

			ResetStandardStreams();
			return GetConsoleWindow() != IntPtr.Zero || HasUsableStandardHandle();
		}
		catch
		{
			return false;
		}
	}

	private static IReadOnlyList<StandardHandleSnapshot> CaptureRedirectedStandardHandles()
	{
		var handles = new List<StandardHandleSnapshot>(3);
		foreach (var id in new[] { StandardInputHandle, StandardOutputHandle, StandardErrorHandle })
		{
			var handle = GetStdHandle(id);
			if (!IsUsableHandle(handle) || GetFileType(handle) == FileTypeChar)
				continue;
			handles.Add(new StandardHandleSnapshot(id, handle));
		}
		return handles;
	}

	private static void RestoreRedirectedStandardHandles(
		IReadOnlyList<StandardHandleSnapshot> handles)
	{
		foreach (var handle in handles)
			_ = SetStdHandle(handle.Id, handle.Handle);
	}

	private static bool HasRedirectedStandardStream()
	{
		try
		{
			return Console.IsInputRedirected ||
			       Console.IsOutputRedirected ||
			       Console.IsErrorRedirected;
		}
		catch
		{
			return false;
		}
	}

	private static bool HasUsableStandardHandle()
	{
		foreach (var id in new[] { StandardInputHandle, StandardOutputHandle, StandardErrorHandle })
		{
			var handle = GetStdHandle(id);
			if (IsUsableHandle(handle))
				return true;
		}
		return false;
	}

	private static bool IsUsableHandle(IntPtr handle) =>
		handle != IntPtr.Zero && handle != new IntPtr(-1);

	private static bool HasUsableConsole()
	{
		try
		{
			return !Console.IsInputRedirected ||
			       !Console.IsOutputRedirected ||
			       !Console.IsErrorRedirected;
		}
		catch
		{
			return false;
		}
	}

	private static void ResetStandardStreams()
	{
		var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
		Console.InputEncoding = utf8;
		Console.OutputEncoding = utf8;
		Console.SetIn(new StreamReader(Console.OpenStandardInput(), utf8, detectEncodingFromByteOrderMarks: true));
		Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
		Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool AttachConsole(uint processId);

	[DllImport("kernel32.dll")]
	private static extern IntPtr GetConsoleWindow();

	private const int StandardInputHandle = -10;
	private const int StandardOutputHandle = -11;
	private const int StandardErrorHandle = -12;
	private const uint FileTypeChar = 0x0002;

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr GetStdHandle(int standardHandle);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetStdHandle(int standardHandle, IntPtr handle);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern uint GetFileType(IntPtr handle);

	private readonly record struct StandardHandleSnapshot(int Id, IntPtr Handle);
}
