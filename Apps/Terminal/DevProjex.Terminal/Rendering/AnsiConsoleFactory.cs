using Spectre.Console;

namespace DevProjex.Terminal.Rendering;

internal static class AnsiConsoleFactory
{
	public static IAnsiConsole Create(TextWriter writer, TerminalCapabilities capabilities)
	{
		var settings = new AnsiConsoleSettings
		{
			Ansi = capabilities.UseAnsi ? AnsiSupport.Yes : AnsiSupport.No,
			ColorSystem = capabilities.UseAnsi ? ColorSystemSupport.Detect : ColorSystemSupport.NoColors,
			Interactive = capabilities.UseInteractiveProgress
				? InteractionSupport.Yes
				: InteractionSupport.No,
			Out = new AnsiConsoleOutput(writer)
		};
		var console = AnsiConsole.Create(settings);
		console.Profile.Width = capabilities.Width;
		return console;
	}
}
