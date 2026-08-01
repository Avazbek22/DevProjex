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
		bool hasPendingDesktopRequest,
		bool isFrameworkDependentLaunch)
	{
		if (hasPendingDesktopRequest)
			return ProcessInvocationMode.Desktop;
		if (arguments.Count > 0)
			return ProcessInvocationMode.Terminal;
		if (environment.IsTerminalHost)
			return ProcessInvocationMode.Terminal;
		if (environment.IsCi)
			return ProcessInvocationMode.Terminal;

		if (environment.IsInputInteractive && environment.IsOutputInteractive)
			return ProcessInvocationMode.Terminal;

		// IDE run consoles expose valid redirected handles, but they are not an
		// intentional terminal invocation. The generated launcher and CI have
		// explicit signals above, while a real shell remains interactive.
		if (isFrameworkDependentLaunch)
			return ProcessInvocationMode.Desktop;

		return environment.HasAttachedConsole
			? ProcessInvocationMode.Terminal
			: ProcessInvocationMode.Desktop;
	}
}
