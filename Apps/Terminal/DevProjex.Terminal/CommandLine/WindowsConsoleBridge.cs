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

			if (!AttachConsole(AttachParentProcess))
			{
				var error = Marshal.GetLastWin32Error();
				if (error != ErrorAccessDenied)
					return HasRedirectedStandardStream() && HasUsableStandardHandle();
			}

			ResetStandardStreams();
			return GetConsoleWindow() != IntPtr.Zero || HasUsableStandardHandle();
		}
		catch
		{
			return false;
		}
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
			if (handle != IntPtr.Zero && handle != new IntPtr(-1))
				return true;
		}
		return false;
	}

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

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr GetStdHandle(int standardHandle);
}
