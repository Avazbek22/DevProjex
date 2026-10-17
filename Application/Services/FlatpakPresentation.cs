namespace DevProjex.Application.Services;

public static class FlatpakPresentation
{
	public static string UnavailablePath(LocalizationService localization, string path) =>
		path.StartsWith("/run/user/", StringComparison.Ordinal) && path.Contains("/doc/", StringComparison.Ordinal)
			? localization["Flatpak.PortalPathUnavailable"]
			: localization.Format("Flatpak.PathUnavailable", FlatpakEnvironment.OverrideCommand(path));

	public static string EnvironmentLine(LocalizationService localization, string gitVersion, string dataPath) =>
		FlatpakEnvironment.Current.IsActive
			? localization.Format("Flatpak.Environment", gitVersion, dataPath,
				FlatpakEnvironment.Current.ExternalLaunchCommand + " mcp")
			: string.Empty;
}
