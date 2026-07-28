using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

internal static partial class TerminalScreenSnapshot
{
	private const string UpdateVariable = "DEVPROJEX_UPDATE_TUI_SNAPSHOTS";

	public static void Verify(
		string name,
		string screen,
		params (string Value, string Replacement)[] replacements)
	{
		var normalized = Normalize(screen, replacements);
		var path = Path.Combine(
			PublishedApplicationLocator.FindRepositoryRoot(),
			"Tests",
			"DevProjex.Tests.Terminal",
			"Snapshots",
			$"{name}.snap.txt");

		if (string.Equals(
			    Environment.GetEnvironmentVariable(UpdateVariable),
			    "1",
			    StringComparison.Ordinal))
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, normalized + Environment.NewLine, new UTF8Encoding(false));
			return;
		}

		Assert.True(
			File.Exists(path),
			$"Snapshot does not exist: {path}. Set {UpdateVariable}=1 to create it.");
		var expected = Normalize(File.ReadAllText(path), []);
		Assert.Equal(expected, normalized);
	}

	private static string Normalize(
		string value,
		IReadOnlyList<(string Value, string Replacement)> replacements)
	{
		var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal);
		foreach (var (source, replacement) in replacements)
		{
			if (!string.IsNullOrEmpty(source))
				normalized = normalized.Replace(source, replacement, StringComparison.OrdinalIgnoreCase);
		}

		normalized = VersionPattern().Replace(normalized, "v<VERSION>");
		normalized = IdentifierPattern().Replace(normalized, "<ID>");
		normalized = TruncatedProjectIdentifierPattern().Replace(normalized, "<PROJECT>");
		normalized = TestProjectIdentifierPattern().Replace(normalized, "<PROJECT>");
		normalized = IdentifierFragmentPattern().Replace(normalized, "<ID-PART>");
		normalized = string.Join(
			'\n',
			normalized
				.Split('\n')
				.Select(static line => line.TrimEnd()));
		return normalized.TrimEnd();
	}

	[GeneratedRegex(@"\bv\d+(?:\.\d+){1,3}(?:[-+][0-9A-Za-z.-]+)?\b")]
	private static partial Regex VersionPattern();

	[GeneratedRegex(@"\b[0-9a-f]{32}\b", RegexOptions.IgnoreCase)]
	private static partial Regex IdentifierPattern();

	[GeneratedRegex(@"(?<=<TEMP_ROOT>[\\/])[0-9a-f]{1,32}", RegexOptions.IgnoreCase)]
	private static partial Regex TruncatedProjectIdentifierPattern();

	[GeneratedRegex(@"(?<=Tests\.Terminal[\\/])[0-9a-f]{1,32}", RegexOptions.IgnoreCase)]
	private static partial Regex TestProjectIdentifierPattern();

	[GeneratedRegex(@"\b[0-9a-f]{8,31}\b", RegexOptions.IgnoreCase)]
	private static partial Regex IdentifierFragmentPattern();
}
