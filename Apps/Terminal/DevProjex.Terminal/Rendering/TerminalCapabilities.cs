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
		              !environment.IsTermDumb &&
		              options.Color switch
		              {
			              TerminalColorMode.Always => true,
			              TerminalColorMode.Never => false,
			              _ => !environment.IsNoColor && streamInteractive
		              };
		var useProgress = !options.Plain &&
		                  options.Verbosity is not (
			                  TerminalVerbosity.Quiet or
			                  TerminalVerbosity.Minimal) &&
		                  options.Progress != TerminalProgressMode.Never &&
		                  !environment.IsTermDumb &&
		                  environment.IsErrorInteractive &&
		                  (options.Progress == TerminalProgressMode.Always ||
		                   !environment.IsCi);

		return new TerminalCapabilities(
			UseAnsi: useAnsi,
			UseUnicode: !options.Plain && !environment.IsTermDumb && environment.SupportsUnicode,
			UseInteractiveProgress: useProgress,
			Width: Math.Max(40, environment.Width),
			Height: Math.Max(10, environment.Height));
	}
}
