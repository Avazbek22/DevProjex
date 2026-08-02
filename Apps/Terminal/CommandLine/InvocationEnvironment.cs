using System.Runtime.InteropServices;

namespace DevProjex.Terminal.CommandLine;

public interface ITerminalEnvironment
{
	TextReader Input { get; }
	TextWriter Output { get; }
	TextWriter Error { get; }
	bool IsInputInteractive { get; }
	bool IsOutputInteractive { get; }
	bool IsErrorInteractive { get; }
	bool HasAttachedConsole { get; }
	bool IsTerminalHost { get; }
	bool IsCi { get; }
	bool IsTermDumb { get; }
	bool IsNoColor { get; }
	bool SupportsUnicode { get; }
	int Width { get; }
	int Height { get; }
	IReadOnlyDictionary<string, string?> Variables { get; }
}

public sealed class InvocationEnvironment : ITerminalEnvironment
{
	private const int DefaultTerminalWidth = 80;
	private const int DefaultTerminalHeight = 24;
	public const string TerminalHostVariable = "DEVPROJEX_TERMINAL_HOST";
	public const string DesktopRequestVariable = "DEVPROJEX_DESKTOP_REQUEST_FILE";
	internal const string InternalDataRootVariable = "DEVPROJEX_INTERNAL_DATA_ROOT";
	private readonly TextWriter _output;

	public InvocationEnvironment(bool hasAttachedConsole)
	{
		_output = new TerminalOutputWriter(Console.Out);
		HasAttachedConsole = hasAttachedConsole;
		IsInputInteractive = hasAttachedConsole && !ReadRedirected(static () => Console.IsInputRedirected);
		IsOutputInteractive = hasAttachedConsole && !ReadRedirected(static () => Console.IsOutputRedirected);
		IsErrorInteractive = hasAttachedConsole && !ReadRedirected(static () => Console.IsErrorRedirected);
		IsTerminalHost = string.Equals(
			Environment.GetEnvironmentVariable(TerminalHostVariable),
			"1",
			StringComparison.Ordinal);
		IsCi = IsTruthy(Environment.GetEnvironmentVariable("CI"));
		IsTermDumb = string.Equals(
			Environment.GetEnvironmentVariable("TERM"),
			"dumb",
			StringComparison.OrdinalIgnoreCase);
		IsNoColor = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
		SupportsUnicode = DetectUnicodeCapability();
		(Width, Height) = ReadTerminalSize();
		Variables = CaptureVariables();
	}

	public TextReader Input => Console.In;
	public TextWriter Output => _output;
	public TextWriter Error => Console.Error;
	public bool IsInputInteractive { get; }
	public bool IsOutputInteractive { get; }
	public bool IsErrorInteractive { get; }
	public bool HasAttachedConsole { get; }
	public bool IsTerminalHost { get; }
	public bool IsCi { get; }
	public bool IsTermDumb { get; }
	public bool IsNoColor { get; }
	public bool SupportsUnicode { get; }
	public int Width { get; }
	public int Height { get; }
	public IReadOnlyDictionary<string, string?> Variables { get; }

	public bool CanRunTui =>
		IsInputInteractive &&
		IsOutputInteractive &&
		!IsTermDumb;

	private static bool ReadRedirected(Func<bool> reader)
	{
		try
		{
			return reader();
		}
		catch
		{
			return true;
		}
	}

	private static (int Width, int Height) ReadTerminalSize()
	{
		if (!OperatingSystem.IsWindows() && TryReadUnixTerminalSize(out var unixSize))
			return unixSize;

		try
		{
			var width = Console.WindowWidth;
			var height = Console.WindowHeight;
			return (
				width > 0 ? width : DefaultTerminalWidth,
				height > 0 ? height : DefaultTerminalHeight);
		}
		catch
		{
			return (DefaultTerminalWidth, DefaultTerminalHeight);
		}
	}

	private static bool TryReadUnixTerminalSize(out (int Width, int Height) size)
	{
		// Console.Window* can report the allocated inline region instead of the PTY dimensions.
		var request = OperatingSystem.IsMacOS() ? 0x40087468u : 0x5413u;
		foreach (var descriptor in new[] { 1, 0, 2 })
		{
			if (ReadUnixWindowSize(descriptor, request, out var windowSize) == 0 &&
			    windowSize.Columns > 0 &&
			    windowSize.Rows > 0)
			{
				size = (windowSize.Columns, windowSize.Rows);
				return true;
			}
		}

		size = default;
		return false;
	}

	private static int ReadUnixWindowSize(
		int descriptor,
		nuint request,
		out UnixWindowSize windowSize)
	{
		if (RequiresDarwinArm64VarArgIoctl(
			    OperatingSystem.IsMacOS(),
			    RuntimeInformation.OSArchitecture))
		{
			// Darwin ARM64 passes variadic arguments on the stack. Fill x2-x7 so the
			// output pointer reaches the ABI-defined vararg position instead of corrupting memory.
			return ioctlDarwinArm64(
				descriptor,
				request,
				0,
				0,
				0,
				0,
				0,
				0,
				out windowSize);
		}

		return ioctl(descriptor, request, out windowSize);
	}

	internal static bool RequiresDarwinArm64VarArgIoctl(
		bool isMacOs,
		Architecture architecture) =>
		isMacOs && architecture == Architecture.Arm64;

	[DllImport("libc", SetLastError = true)]
	private static extern int ioctl(
		int descriptor,
		nuint request,
		out UnixWindowSize windowSize);

	[DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
	private static extern int ioctlDarwinArm64(
		int descriptor,
		nuint request,
		nint register2,
		nint register3,
		nint register4,
		nint register5,
		nint register6,
		nint register7,
		out UnixWindowSize windowSize);

	[StructLayout(LayoutKind.Sequential)]
	private readonly struct UnixWindowSize
	{
		public readonly ushort Rows;
		public readonly ushort Columns;
		private readonly ushort _pixelWidth;
		private readonly ushort _pixelHeight;
	}

	private static bool DetectUnicodeCapability()
	{
		if (OperatingSystem.IsWindows())
			return true;

		var locale = Environment.GetEnvironmentVariable("LC_ALL") ??
		             Environment.GetEnvironmentVariable("LC_CTYPE") ??
		             Environment.GetEnvironmentVariable("LANG");
		return locale is null ||
		       locale.Contains("UTF-8", StringComparison.OrdinalIgnoreCase) ||
		       locale.Contains("UTF8", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsTruthy(string? value) =>
		value is not null &&
		(value == "1" ||
		 value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
		 value.Equals("yes", StringComparison.OrdinalIgnoreCase));

	private static IReadOnlyDictionary<string, string?> CaptureVariables() =>
		new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
		{
			["TERM"] = Environment.GetEnvironmentVariable("TERM"),
			["NO_COLOR"] = Environment.GetEnvironmentVariable("NO_COLOR"),
			["CI"] = Environment.GetEnvironmentVariable("CI"),
			["TMUX"] = Environment.GetEnvironmentVariable("TMUX"),
			["ZELLIJ"] = Environment.GetEnvironmentVariable("ZELLIJ"),
			["WT_SESSION"] = Environment.GetEnvironmentVariable("WT_SESSION"),
			[TerminalHostVariable] = Environment.GetEnvironmentVariable(TerminalHostVariable),
			[InternalDataRootVariable] = Environment.GetEnvironmentVariable(InternalDataRootVariable)
		};
}

public sealed record TerminalEnvironmentSnapshot(
	bool IsInputInteractive,
	bool IsOutputInteractive,
	bool IsErrorInteractive,
	bool HasAttachedConsole,
	bool IsTerminalHost,
	bool IsCi,
	bool IsTermDumb,
	bool IsNoColor,
	bool SupportsUnicode,
	int Width,
	int Height)
{
	public bool CanRunTui => IsInputInteractive && IsOutputInteractive && !IsTermDumb;

	public static TerminalEnvironmentSnapshot From(ITerminalEnvironment environment) =>
		new(
			environment.IsInputInteractive,
			environment.IsOutputInteractive,
			environment.IsErrorInteractive,
			environment.HasAttachedConsole,
			environment.IsTerminalHost,
			environment.IsCi,
			environment.IsTermDumb,
			environment.IsNoColor,
			environment.SupportsUnicode,
			environment.Width,
			environment.Height);
}
