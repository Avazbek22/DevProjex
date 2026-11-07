using System.Text.RegularExpressions;

namespace DevProjex.Application.Services;

public sealed record McpServerLaunch(string Command, IReadOnlyList<string> PrefixArguments);

public static partial class McpServerLaunchResolver
{
	public const string NpmExecutableVariable = "DEVPROJEX_NPM_EXECUTABLE";

	public static McpServerLaunch Resolve(string executablePath) => Resolve(
		executablePath,
		Environment.GetEnvironmentVariable(NpmExecutableVariable),
		Environment.GetEnvironmentVariable("NUGET_PACKAGES"),
		File.Exists,
		ReferToSameEntry);

	internal static McpServerLaunch Resolve(
		string executablePath,
		string? npmExecutable,
		string? nugetPackages,
		Func<string, bool> fileExists,
		Func<string, string, bool>? sameEntry = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
		if (FlatpakEnvironment.Current.IsActive)
			return new("flatpak", ["run", FlatpakEnvironment.Current.AppId!]);

		var windowsPath = IsWindowsPath(executablePath);
		var comparison = windowsPath ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		var path = Normalize(executablePath);
		var parts = path.Split('/');
		if (parts.Any(static part => part is "." or ".."))
			return new(executablePath, []);

		if (parts.Length >= 5 &&
			parts[^5].Equals("node_modules", comparison) &&
			parts[^4].Equals("@devprojex", comparison) &&
			parts[^2].Equals("bin", comparison) &&
			IsNpmBinary(parts[^3], parts[^1], comparison) &&
			(parts.Length >= 7 && parts[^7].Equals("_npx", comparison) && parts[^6].Length > 0 ||
			 PathsMatch(path, npmExecutable, comparison, sameEntry)))
		{
			return new("npx", ["-y", "devprojex"]);
		}

		if (parts.Length >= 7 &&
			parts[^4].Equals("tools", comparison) && parts[^3].Equals("any", comparison) &&
			IsNuGetBinary(parts[^6], parts[^2], parts[^1], comparison) &&
			PackageVersion().IsMatch(parts[^5]))
		{
			var packageRoot = string.Join('/', parts[..^6]);
			// Global tools have stable shims and their own .store; only the NuGet cache needs dnx.
			if (!parts.Any(part => part.Equals(".store", comparison)) &&
				(packageRoot.EndsWith("/.nuget/packages", comparison) ||
				 PathsMatch(packageRoot, nugetPackages is null ? null : Normalize(nugetPackages).TrimEnd('/'), comparison, sameEntry) ||
				 fileExists(string.Join('/', parts[..^4]) + "/.nupkg.metadata")))
			{
				return new("dnx", ["devprojex"]);
			}
		}
		return new(executablePath, []);
	}

	private static bool IsNpmBinary(string package, string binary, StringComparison comparison)
	{
		foreach (var platform in new[] { "win32-x64", "win32-arm64", "linux-x64", "linux-arm64", "darwin-x64", "darwin-arm64" })
		{
			if (package.Equals("cli-" + platform, comparison))
				return binary.Equals(platform.StartsWith("win32", StringComparison.Ordinal) ? "devprojex.exe" : "devprojex", comparison);
		}
		return false;
	}

	private static bool IsNuGetBinary(string package, string rid, string binary, StringComparison comparison) =>
		new[] { "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64" }.Contains(rid, comparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) &&
		package.Equals("devprojex." + rid, comparison) &&
		binary.Equals(rid.StartsWith("win-", comparison) ? "devprojex.exe" : "devprojex", comparison);

	private static bool PathsMatch(string path, string? candidate, StringComparison comparison,
		Func<string, string, bool>? sameEntry) =>
		!string.IsNullOrWhiteSpace(candidate) &&
		(path.Equals(Normalize(candidate), comparison) || sameEntry?.Invoke(path, candidate) == true);

	private static bool ReferToSameEntry(string left, string right) =>
		FileSystemPathIdentity.TryRead(left, out var leftIdentity) &&
		FileSystemPathIdentity.TryRead(right, out var rightIdentity) && leftIdentity == rightIdentity;

	private static string Normalize(string path) => IsWindowsPath(path) ? path.Replace('\\', '/') : path;

	private static bool IsWindowsPath(string path) =>
		path.StartsWith("\\\\", StringComparison.Ordinal) ||
		path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '/' or '\\';

	[GeneratedRegex(@"\A[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
	private static partial Regex PackageVersion();
}
