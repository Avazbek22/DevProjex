using System.Security.Cryptography;
using DevProjex.Application.Services;

namespace DevProjex.Infrastructure.TerminalCommands;

internal enum McpExistingEntryKind
{
	/// <summary>DevProjex started through npx, dnx, or flatpak run.</summary>
	Launcher,

	/// <summary>An entry named devprojex that DevProjex does not recognize.</summary>
	Foreign
}

/// <summary>
/// A client entry named devprojex that is not the native "&lt;executable&gt; mcp" form DevProjex writes itself.
/// </summary>
internal sealed record McpExistingEntry(
	McpExistingEntryKind Kind,
	string Command,
	IReadOnlyList<string> Arguments,
	IReadOnlyDictionary<string, string> Environment,
	string? RawConfiguration,
	string? Launcher,
	string? ProjectRoot,
	McpConnectionMode Mode,
	IReadOnlyList<string> UnsupportedArguments)
{
	public bool IsDevProjexLauncher => Kind == McpExistingEntryKind.Launcher;

	public string DisplayCommand => McpExistingEntryParser.FormatCommandLine(Command, Arguments);

	/// <summary>Identifies the entry the user saw when confirming, so a changed entry is never replaced.</summary>
	public string Fingerprint
	{
		get
		{
			var canonical = new StringBuilder(Command).Append('\0');
			foreach (var argument in Arguments)
				canonical.Append(argument).Append('\0');
			foreach (var pair in Environment.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
				canonical.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
			return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
		}
	}
}

/// <summary>
/// Recognizes the launch commands that DevProjex documentation, its npm and NuGet packages, the MCP Registry
/// and Flathub produce, so an entry created that way is treated as DevProjex instead of an unknown server.
/// </summary>
internal static class McpExistingEntryParser
{
	private const string PackageName = "devprojex";
	private const string ServerCommand = "mcp";

	private static readonly HashSet<string> NpxFlags = new(StringComparer.Ordinal)
	{
		"-y", "--yes", "-q", "--quiet", "--prefer-offline", "--prefer-online"
	};

	private static readonly HashSet<string> DnxFlags = new(StringComparer.Ordinal)
	{
		"-y", "--yes", "--prerelease", "--interactive", "--allow-roll-forward"
	};

	private static readonly HashSet<string> DnxValueOptions = new(StringComparer.Ordinal)
	{
		"--version", "--source", "--add-source", "--configfile"
	};

	public static McpExistingEntry Parse(
		string command,
		IReadOnlyList<string> arguments,
		IReadOnlyDictionary<string, string> environment,
		string? rawConfiguration = null)
	{
		ArgumentNullException.ThrowIfNull(command);
		ArgumentNullException.ThrowIfNull(arguments);
		ArgumentNullException.ThrowIfNull(environment);
		if (TryFindServerArguments(command, arguments, 0, allowShellWrapper: true, out var launcher, out var serverStart) &&
			serverStart < arguments.Count &&
			arguments[serverStart] == ServerCommand)
		{
			var (projectRoot, mode, unsupported) = ParseServerArguments(arguments, serverStart + 1);
			return new McpExistingEntry(
				McpExistingEntryKind.Launcher,
				command,
				arguments,
				environment,
				rawConfiguration,
				launcher,
				projectRoot,
				mode,
				unsupported);
		}

		return new McpExistingEntry(
			McpExistingEntryKind.Foreign,
			command,
			arguments,
			environment,
			rawConfiguration,
			Launcher: null,
			ProjectRoot: null,
			McpConnectionMode.Standard,
			UnsupportedArguments: []);
	}

	/// <summary>
	/// A launcher entry is equivalent when it already serves the requested project in the requested mode.
	/// Live mode also needs the server to read the selection this application writes, which a server started
	/// outside a Store or Flatpak sandbox cannot do for an application inside one.
	/// </summary>
	public static bool IsEquivalent(
		McpExistingEntry entry,
		McpConnectionRequest request,
		bool launcherSharesApplicationData)
	{
		ArgumentNullException.ThrowIfNull(entry);
		ArgumentNullException.ThrowIfNull(request);
		return entry.IsDevProjexLauncher &&
			   entry.ProjectRoot is not null &&
			   PathsEqual(entry.ProjectRoot, request.ProjectRoot) &&
			   entry.Mode == request.Mode &&
			   entry.UnsupportedArguments.Count == 0 &&
			   entry.Environment.Count == 0 &&
			   (request.Mode == McpConnectionMode.Standard || launcherSharesApplicationData);
	}

	public static string FormatCommandLine(string command, IEnumerable<string> arguments) =>
		string.Join(' ', new[] { command }.Concat(arguments).Select(QuoteForDisplay));

	private static bool TryFindServerArguments(
		string command,
		IReadOnlyList<string> arguments,
		int start,
		bool allowShellWrapper,
		out string launcher,
		out int serverStart)
	{
		launcher = string.Empty;
		serverStart = -1;
		switch (NormalizeCommandName(command))
		{
			case "cmd" when allowShellWrapper:
				// Claude Code on Windows needs `cmd /c npx ...` to start an npm launcher.
				var index = start;
				while (index < arguments.Count && arguments[index] is "/d" or "/D" or "/s" or "/S" or "/q" or "/Q")
					index++;
				return index + 1 < arguments.Count &&
					   arguments[index] is "/c" or "/C" &&
					   TryFindServerArguments(arguments[index + 1], arguments, index + 2, false, out launcher, out serverStart);
			case "npx":
				launcher = "npx";
				return TryFindNpxServer(arguments, start, out serverStart);
			case "dnx":
				launcher = "dnx";
				return TryFindDnxServer(arguments, start, out serverStart);
			case "dotnet":
				launcher = "dnx";
				return start < arguments.Count &&
					   arguments[start] == "dnx" &&
					   TryFindDnxServer(arguments, start + 1, out serverStart);
			case "flatpak":
				launcher = "flatpak";
				return TryFindFlatpakServer(arguments, start, out serverStart);
			default:
				return false;
		}
	}

	private static bool TryFindNpxServer(IReadOnlyList<string> arguments, int start, out int serverStart)
	{
		var index = start;
		while (index < arguments.Count && NpxFlags.Contains(arguments[index]))
			index++;
		if (index >= arguments.Count || !IsPackage(arguments[index]))
		{
			serverStart = -1;
			return false;
		}
		index++;
		if (index < arguments.Count && arguments[index] == "--")
			index++;
		serverStart = index;
		return true;
	}

	private static bool TryFindDnxServer(IReadOnlyList<string> arguments, int start, out int serverStart)
	{
		var index = start;
		var hasPackage = false;
		while (index < arguments.Count)
		{
			var argument = arguments[index];
			if (argument == "--")
			{
				index++;
				break;
			}
			if (DnxFlags.Contains(argument))
			{
				index++;
				continue;
			}
			if (DnxValueOptions.Contains(argument))
			{
				index += 2;
				continue;
			}
			if (!hasPackage && IsPackage(argument))
			{
				hasPackage = true;
				index++;
				continue;
			}
			break;
		}
		serverStart = hasPackage ? index : -1;
		return hasPackage;
	}

	private static bool TryFindFlatpakServer(IReadOnlyList<string> arguments, int start, out int serverStart)
	{
		serverStart = -1;
		if (start >= arguments.Count || arguments[start] != "run")
			return false;
		var index = start + 1;
		// Options of flatpak run use the --name=value form, so a bare word is the application.
		while (index < arguments.Count && arguments[index].StartsWith("--", StringComparison.Ordinal))
			index++;
		if (index >= arguments.Count ||
			!string.Equals(arguments[index], FlatpakEnvironment.DevProjexAppId, StringComparison.Ordinal))
			return false;
		serverStart = index + 1;
		return true;
	}

	private static (string? ProjectRoot, McpConnectionMode Mode, IReadOnlyList<string> Unsupported)
		ParseServerArguments(IReadOnlyList<string> arguments, int start)
	{
		string? projectRoot = null;
		var hasRoot = false;
		var hasLive = false;
		var unsupported = new List<string>();
		for (var index = start; index < arguments.Count; index++)
		{
			var argument = arguments[index];
			if (argument == "--root" && index + 1 < arguments.Count && !hasRoot)
			{
				hasRoot = true;
				var value = arguments[++index];
				// A relative root depends on the directory the client starts in, so it cannot be compared.
				if (Path.IsPathFullyQualified(value))
					projectRoot = Path.GetFullPath(value);
				else
					unsupported.AddRange(["--root", value]);
				continue;
			}
			if (argument == "--live" && !hasLive)
			{
				hasLive = true;
				continue;
			}
			unsupported.Add(argument);
		}
		return (projectRoot, hasLive ? McpConnectionMode.Live : McpConnectionMode.Standard, unsupported);
	}

	private static bool IsPackage(string argument) =>
		string.Equals(argument, PackageName, StringComparison.OrdinalIgnoreCase) ||
		argument.Length > PackageName.Length + 1 &&
		argument.StartsWith(PackageName + "@", StringComparison.OrdinalIgnoreCase);

	private static string NormalizeCommandName(string command)
	{
		var name = Path.GetFileName(command.Replace('\\', '/'));
		foreach (var extension in new[] { ".exe", ".cmd", ".bat" })
		{
			if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
				return name[..^extension.Length].ToLowerInvariant();
		}
		return name.ToLowerInvariant();
	}

	private static string QuoteForDisplay(string token) =>
		token.Length > 0 && !token.Any(static character => char.IsWhiteSpace(character) || character is '"' or '\'')
			? token
			: "\"" + token.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

	private static bool PathsEqual(string left, string right) =>
		string.Equals(
			Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
