using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.Rendering;

public sealed record TerminalCapabilities(
	bool UseAnsi,
	bool UseUnicode,
	bool UseInteractiveProgress,
	int Width,
	int Height)
{
	public static TerminalCapabilities Resolve(
		ITerminalEnvironment environment,
		TerminalOutputOptions options,
		bool forStandardError)
	{
		var streamInteractive = forStandardError
			? environment.IsErrorInteractive
			: environment.IsOutputInteractive;
		var useAnsi = !options.Plain &&
		              options.Color != TerminalColorMode.Never &&
		              !environment.IsNoColor &&
		              !environment.IsTermDumb &&
		              (options.Color == TerminalColorMode.Always || streamInteractive);
		var useProgress = !options.Plain &&
		                  options.Progress != TerminalProgressMode.Never &&
		                  !environment.IsTermDumb &&
		                  (options.Progress == TerminalProgressMode.Always ||
		                   (environment.IsErrorInteractive && !environment.IsCi));

		return new TerminalCapabilities(
			UseAnsi: useAnsi,
			UseUnicode: environment.SupportsUnicode,
			UseInteractiveProgress: useProgress,
			Width: Math.Max(40, environment.Width),
			Height: Math.Max(10, environment.Height));
	}
}
