namespace DevProjex.Infrastructure.Git;

/// <summary>
/// Isolated reads skip the user's global and system configuration, and Git for Windows keeps
/// <c>core.autocrlf</c> in its system configuration. Without it a CRLF checkout compares as modified
/// whenever Git rechecks file content, so a working-tree comparison would report nearly every file
/// of a fresh clone. This reads the two line-ending settings, and nothing else, from every scope
/// in Git's own order, so the comparison matches what the user's Git reports.
/// </summary>
internal static class GitLineEndingSettings
{
	public static Task<IReadOnlyList<string>> ReadAsync(string repositoryRoot, CancellationToken cancellationToken) =>
		ReadAsync(repositoryRoot, DefaultUserConfigurationFiles(), ReadsSystemConfiguration(), cancellationToken);

	/// <summary>
	/// Returns validated <c>-c</c> values. An unreadable repository scope returns none, which keeps
	/// the comparison exactly as it was without these settings.
	/// </summary>
	internal static async Task<IReadOnlyList<string>> ReadAsync(
		string repositoryRoot,
		IReadOnlyList<string> userConfigurationFiles,
		bool readSystemConfiguration,
		CancellationToken cancellationToken)
	{
		// Lowest precedence first, as Git reads them: system, then the user's files, then the repository.
		var reads = new List<Task<GitInspectionProcessResult?>>();
		if (readSystemConfiguration)
		{
			reads.Add(GitRepositorySafetyInspector.RunAsync(
				repositoryRoot,
				GitProcessOperation.ReadConfigValue(GitConfigReadKind.SystemLineEndings),
				cancellationToken));
		}
		foreach (var file in userConfigurationFiles.Where(File.Exists))
		{
			reads.Add(GitRepositorySafetyInspector.RunAsync(
				repositoryRoot,
				GitProcessOperation.ReadLineEndingsFromFile(file),
				cancellationToken));
		}
		var repositoryRead = GitRepositorySafetyInspector.RunAsync(
			repositoryRoot,
			GitProcessOperation.ReadConfigValue(GitConfigReadKind.RepositoryLineEndings),
			cancellationToken);
		reads.Add(repositoryRead);
		var results = await Task.WhenAll(reads).ConfigureAwait(false);
		if (!IsQueryResult(await repositoryRead.ConfigureAwait(false)))
			return [];

		var values = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var result in results)
		{
			if (!IsQueryResult(result))
				continue;
			// With -z each entry is "key\nvalue" followed by NUL, so a line break inside a value stays
			// part of that value. A key written without "= value" is a boolean true and has no
			// separator at all.
			foreach (var entry in result!.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
			{
				var separator = entry.IndexOf('\n');
				var key = (separator < 0 ? entry : entry[..separator]).ToLowerInvariant();
				values[key] = separator < 0 ? "true" : entry[(separator + 1)..];
			}
		}
		return Normalize(values);
	}

	internal static IReadOnlyList<string> Normalize(IReadOnlyDictionary<string, string> values)
	{
		var settings = new List<string>(2);
		if (values.TryGetValue("core.autocrlf", out var autocrlf) &&
			NormalizeAutoCrlf(autocrlf) is { } normalizedAutoCrlf)
			settings.Add("core.autocrlf=" + normalizedAutoCrlf);
		if (values.TryGetValue("core.eol", out var eol) &&
			eol.ToLowerInvariant() is "lf" or "crlf" or "native")
			settings.Add("core.eol=" + eol.ToLowerInvariant());
		return settings;
	}

	// Git accepts any boolean spelling for core.autocrlf, plus "input".
	private static string? NormalizeAutoCrlf(string value) => value.ToLowerInvariant() switch
	{
		"input" => "input",
		"true" or "yes" or "on" or "1" => "true",
		"false" or "no" or "off" or "0" or "" => "false",
		_ => NormalizeNumericBoolean(value)
	};

	// Git booleans also accept base-0 integers with an optional binary k/m/g multiplier.
	private static string? NormalizeNumericBoolean(string value)
	{
		var digits = value.AsSpan();
		if (digits.IsEmpty)
			return null;
		if (digits[0] is '+' or '-')
			digits = digits[1..];
		if (digits.IsEmpty)
			return null;
		var multiplier = char.ToLowerInvariant(digits[^1]) switch
		{
			'k' => 1024,
			'm' => 1024 * 1024,
			'g' => 1024 * 1024 * 1024,
			_ => 1
		};
		if (multiplier != 1)
			digits = digits[..^1];
		var radix = 10;
		if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			radix = 16;
			digits = digits[2..];
		}
		else if (digits.Length > 1 && digits[0] == '0')
		{
			radix = 8;
		}
		if (digits.IsEmpty)
			return null;
		var limit = int.MaxValue / multiplier;
		var magnitude = 0;
		foreach (var character in digits)
		{
			var digit = character switch
			{
				>= '0' and <= '9' => character - '0',
				>= 'a' and <= 'f' => character - 'a' + 10,
				>= 'A' and <= 'F' => character - 'A' + 10,
				_ => -1
			};
			if (digit < 0 || digit >= radix || digit > limit || magnitude > (limit - digit) / radix)
				return null;
			magnitude = magnitude * radix + digit;
		}
		return magnitude == 0 ? "false" : "true";
	}

	// Exit code 1 with no output means the scope sets neither key.
	private static bool IsQueryResult(GitInspectionProcessResult? result) =>
		result is { ExceededOutputLimit: false } &&
		(result.ExitCode == 0 || result.ExitCode == 1 && string.IsNullOrEmpty(result.Output));

	private static bool ReadsSystemConfiguration() =>
		ReadsSystemConfiguration(Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM"));

	internal static bool ReadsSystemConfiguration(string? noSystem) =>
		NormalizeAutoCrlf(noSystem?.Trim() ?? string.Empty) is not "true";

	// Git reads the XDG file and then ~/.gitconfig, unless GIT_CONFIG_GLOBAL names the only one.
	private static IReadOnlyList<string> DefaultUserConfigurationFiles()
	{
		var global = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
		if (!string.IsNullOrWhiteSpace(global))
			return Path.IsPathFullyQualified(global) ? [global] : [];
		var home = Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } homeVariable &&
				   Path.IsPathFullyQualified(homeVariable)
			? homeVariable
			: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (string.IsNullOrEmpty(home))
			return [];
		var xdgHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg &&
					  Path.IsPathFullyQualified(xdg)
			? xdg
			: Path.Combine(home, ".config");
		return [Path.Combine(xdgHome, "git", "config"), Path.Combine(home, ".gitconfig")];
	}
}
