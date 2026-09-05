using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Execution;

namespace DevProjex.TerminalHost;

internal static class Program
{
	public static int Main(string[] args)
	{
		using var cancellation = TerminalCancellationCoordinator.Register();
		return new TerminalApplication(
				new InvocationEnvironment(hasAttachedConsole: true),
				new TerminalServiceFactory(
					hostCapabilities: TerminalHostCapabilities.Headless),
				developerCommandRunner: null)
			.RunAsync(args, cancellation.Token)
			.GetAwaiter()
			.GetResult();
	}
}
