using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Execution;
using System.Text;

namespace DevProjex.TerminalHost;

internal static class Program
{
	public static int Main(string[] args)
	{
		if (OperatingSystem.IsWindows())
			ConfigureUtf8StandardStreams();

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

	private static void ConfigureUtf8StandardStreams()
	{
		var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
		Console.InputEncoding = utf8;
		Console.OutputEncoding = utf8;
		Console.SetIn(new StreamReader(Console.OpenStandardInput(), utf8, detectEncodingFromByteOrderMarks: true));
		Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
		Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
	}
}
