using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.Rendering;

internal static class DryRunRenderer
{
	public static void WritePlan(
		ITerminalEnvironment environment,
		LocalizationService localization,
		string destination)
	{
		environment.Error.WriteLine(
			localization.Format("Terminal.DryRun.Ready", destination));
	}
}
