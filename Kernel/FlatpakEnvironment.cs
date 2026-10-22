namespace DevProjex.Kernel;

public sealed record FlatpakEnvironmentSnapshot(bool IsActive, string? AppId)
{
	public string? ExternalLaunchCommand => IsActive && AppId is not null
		? "flatpak run " + FlatpakEnvironment.QuoteArgument(AppId)
		: null;
}

public static class FlatpakEnvironment
{
	private const string ApplicationPrefix = "/app/";
	private static readonly AsyncLocal<FlatpakEnvironmentSnapshot?> Override = new();
	// The markers and the executable location cannot change during a process lifetime.
	private static readonly Lazy<FlatpakEnvironmentSnapshot> Detected = new(static () => Detect(
		OperatingSystem.IsLinux(), Environment.GetEnvironmentVariable, File.Exists, Environment.ProcessPath));

	public static FlatpakEnvironmentSnapshot Current => Override.Value ?? Detected.Value;

	/// <summary>
	/// Detects the DevProjex Flatpak itself. Another application's sandbox, such as an editor terminal
	/// that runs a DevProjex binary from npm or a release archive, sets the same markers but never
	/// runs that binary from the application prefix.
	/// </summary>
	public static FlatpakEnvironmentSnapshot Detect(
		bool isLinux,
		Func<string, string?> environment,
		Func<string, bool> fileExists,
		string? processPath)
	{
		var sandbox = Detect(isLinux, environment, fileExists);
		return sandbox.IsActive &&
			   processPath is not null &&
			   processPath.StartsWith(ApplicationPrefix, StringComparison.Ordinal) &&
			   !processPath.Contains("/../", StringComparison.Ordinal)
			? sandbox
			: new(false, null);
	}

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
