namespace DevProjex.Terminal.CommandLine;

public enum TerminalColorMode
{
	Auto,
	Always,
	Never
}

public enum TerminalProgressMode
{
	Auto,
	Always,
	Never
}

public enum TerminalVerbosity
{
	Quiet,
	Minimal,
	Normal,
	Detailed,
	Diagnostic
}

public enum TerminalScreenMode
{
	Auto,
	Alternate,
	Inline
}

public sealed record TerminalOutputOptions(
	TerminalColorMode Color = TerminalColorMode.Auto,
	TerminalProgressMode Progress = TerminalProgressMode.Auto,
	TerminalVerbosity Verbosity = TerminalVerbosity.Normal,
	bool Plain = false);
