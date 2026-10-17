namespace DevProjex.Kernel;

public sealed record FlatpakEnvironmentSnapshot(bool IsActive, string? AppId)
{
	public string? ExternalLaunchCommand => IsActive && AppId is not null
		? "flatpak run " + FlatpakEnvironment.QuoteArgument(AppId)
		: null;
}

public static class FlatpakEnvironment
{
	private static readonly AsyncLocal<FlatpakEnvironmentSnapshot?> Override = new();
	public static FlatpakEnvironmentSnapshot Current => Override.Value ?? Detect(
		OperatingSystem.IsLinux(), Environment.GetEnvironmentVariable, File.Exists);

	public static FlatpakEnvironmentSnapshot Detect(
		bool isLinux,
		Func<string, string?> environment,
		Func<string, bool> fileExists)
	{
		ArgumentNullException.ThrowIfNull(environment);
		ArgumentNullException.ThrowIfNull(fileExists);
		if (!isLinux)
			return new(false, null);
		var appId = environment("FLATPAK_ID");
		return !string.IsNullOrWhiteSpace(appId) && fileExists("/.flatpak-info")
			? new(true, appId)
			: new(false, null);
	}

	public static string QuoteArgument(string value)
	{
		ArgumentNullException.ThrowIfNull(value);
		return value.Length > 0 && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/')
			? value
			: "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
	}

	public static string OverrideCommand(string directory) =>
		"flatpak override --user " + QuoteArgument("--filesystem=" + directory) + " " + QuoteArgument(Current.AppId!);

	internal static IDisposable UseForTesting(FlatpakEnvironmentSnapshot snapshot)
	{
		var previous = Override.Value;
		Override.Value = snapshot;
		return new Scope(() => Override.Value = previous);
	}

	private sealed class Scope(Action restore) : IDisposable
	{
		public void Dispose() => restore();
	}
}
