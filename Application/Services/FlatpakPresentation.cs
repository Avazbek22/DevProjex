namespace DevProjex.Application.Services;

public sealed record FlatpakUnavailablePathNotice(string Message, string? Command);

public static class FlatpakPresentation
{
	public static string UnavailablePath(LocalizationService localization, string path) =>
		IsPortalDocumentPath(path)
			? localization["Flatpak.PortalPathUnavailable"]
			: localization.Format("Flatpak.PathUnavailable", FlatpakEnvironment.OverrideCommand(path));

	// Dialogs show the override command in its own copyable block, so the sentence keeps only its lead-in.
	public static FlatpakUnavailablePathNotice UnavailablePathNotice(LocalizationService localization, string path) =>
		IsPortalDocumentPath(path)
			? new(localization["Flatpak.PortalPathUnavailable"], null)
			: new(localization.Format("Flatpak.PathUnavailable", string.Empty).TrimEnd(),
				FlatpakEnvironment.OverrideCommand(path));

	public static string EnvironmentLine(LocalizationService localization, string gitVersion, string dataPath) =>
		FlatpakEnvironment.Current.IsActive
			? localization.Format("Flatpak.Environment", gitVersion, dataPath,
				FlatpakEnvironment.Current.ExternalLaunchCommand + " mcp")
			: string.Empty;

	public static bool IsPortalDocumentPath(string path) =>
		path.StartsWith("/run/user/", StringComparison.Ordinal) &&
		(path.Contains("/doc/", StringComparison.Ordinal) || path.EndsWith("/doc", StringComparison.Ordinal));
}
