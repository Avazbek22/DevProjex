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
	public const string TerminalHostVariable = "DEVPROJEX_TERMINAL_HOST";
	public const string DesktopRequestVariable = "DEVPROJEX_DESKTOP_REQUEST_FILE";

	public InvocationEnvironment(bool hasAttachedConsole)
	{
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
		IsNoColor = Environment.GetEnvironmentVariable("NO_COLOR") is not null;
		SupportsUnicode = DetectUnicodeCapability();
		(Width, Height) = ReadTerminalSize();
		Variables = CaptureVariables();
	}

	public TextReader Input => Console.In;
	public TextWriter Output => Console.Out;
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
		try
		{
			return (Math.Max(1, Console.WindowWidth), Math.Max(1, Console.WindowHeight));
		}
		catch
		{
			return (80, 24);
		}
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
			[TerminalHostVariable] = Environment.GetEnvironmentVariable(TerminalHostVariable)
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
