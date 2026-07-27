using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.Tui;

public sealed record TerminalWorkspacePresentation(
	bool UseMonochromeScheme,
	string? SchemeName);

public static class TerminalWorkspacePresentationPolicy
{
	public const string MonochromeSchemeName = "DevProjexMonochrome";

	public static TerminalWorkspacePresentation Resolve(
		TerminalColorMode requestedColor,
		bool plain,
		ITerminalEnvironment environment)
	{
		var useMonochrome = plain ||
		                    requestedColor == TerminalColorMode.Never ||
		                    requestedColor == TerminalColorMode.Auto && environment.IsNoColor;
		return new TerminalWorkspacePresentation(
			useMonochrome,
			useMonochrome ? MonochromeSchemeName : null);
	}
}
