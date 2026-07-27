namespace DevProjex.Terminal.CommandLine;

public enum ProcessInvocationMode
{
	Desktop,
	Terminal
}

public static class ProcessInvocationRouter
{
	public static ProcessInvocationMode Resolve(
		IReadOnlyList<string> arguments,
		ITerminalEnvironment environment,
		bool hasPendingDesktopRequest)
	{
		if (hasPendingDesktopRequest)
			return ProcessInvocationMode.Desktop;
		if (arguments.Count > 0)
			return ProcessInvocationMode.Terminal;
		if (environment.IsTerminalHost)
			return ProcessInvocationMode.Terminal;
		if (environment.IsCi)
			return ProcessInvocationMode.Terminal;
		return environment.HasAttachedConsole
			? ProcessInvocationMode.Terminal
			: ProcessInvocationMode.Desktop;
	}
}
