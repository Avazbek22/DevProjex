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
		var snapshotDirectory = Path.Combine(
			PublishedApplicationLocator.FindRepositoryRoot(),
			"Tests",
			"DevProjex.Tests.Terminal",
			"Snapshots");
		var commonPath = Path.Combine(snapshotDirectory, $"{name}.snap.txt");
		var platformPath = GetPlatformSnapshotPath(snapshotDirectory, name);

		if (string.Equals(
			    Environment.GetEnvironmentVariable(UpdateVariable),
			    "1",
			    StringComparison.Ordinal))
		{
			Directory.CreateDirectory(snapshotDirectory);
			var updatePath = platformPath is not null &&
			                 File.Exists(commonPath) &&
			                 !string.Equals(
				                 Normalize(File.ReadAllText(commonPath), []),
				                 normalized,
				                 StringComparison.Ordinal)
				? platformPath
				: commonPath;
			File.WriteAllText(
				updatePath,
				normalized + Environment.NewLine,
				new UTF8Encoding(false));
			return;
		}

		var path = platformPath is not null && File.Exists(platformPath)
			? platformPath
			: commonPath;
		Assert.True(
			File.Exists(path),
			$"Snapshot does not exist: {path}. Set {UpdateVariable}=1 to create it.");
		var expected = Normalize(File.ReadAllText(path), []);
		Assert.Equal(expected, normalized);
	}

	private static string? GetPlatformSnapshotPath(string directory, string name)
	{
		var platform = OperatingSystem.IsLinux()
			? "linux"
			: OperatingSystem.IsMacOS()
				? "macos"
				: null;
		return platform is null
			? null
			: Path.Combine(directory, $"{name}.{platform}.snap.txt");
	}

	private static string Normalize(
		string value,
		IReadOnlyList<(string Value, string Replacement)> replacements)
	{
		var normalized = value
			.Replace("\r\n", "\n", StringComparison.Ordinal)
			.Replace('\u00A0', ' ');
		normalized = GroupedNumberSeparatorPattern().Replace(normalized, " ");
		normalized = ByteSizeDecimalSeparatorPattern().Replace(normalized, ".");
		normalized = EstimatedTokenCountPattern().Replace(normalized, "<TOKENS>");
		foreach (var (source, replacement) in replacements)
		{
			if (!string.IsNullOrEmpty(source))
				normalized = ReplacePathVariants(normalized, source, replacement);
		}

		normalized = VersionPattern().Replace(normalized, "v<VERSION>");
		normalized = IdentifierPattern().Replace(normalized, "<ID>");
		normalized = TruncatedProjectIdentifierPattern().Replace(normalized, "<PROJECT>");
		normalized = TestProjectIdentifierPattern().Replace(normalized, "<PROJECT>");
		normalized = ClippedTestProjectIdentifierPattern().Replace(normalized, "<ID-PART>");
		normalized = IdentifierFragmentPattern().Replace(normalized, "<ID-PART>");
		normalized = ClippedTemporaryProjectPathPattern().Replace(
			normalized,
			"<PROJECT_ROOT>");
		normalized = ElapsedPattern().Replace(
			normalized,
			static match => $"{match.Groups["label"].Value}: <ELAPSED>");
		normalized = RecentOpenedPattern().Replace(
			normalized,
			static match => $"{match.Groups["label"].Value}: <TIMESTAMP>");
		normalized = SpinnerFramePattern().Replace(
			normalized,
			static match =>
				$"{match.Groups["prefix"].Value}<SPINNER>{match.Groups["suffix"].Value}");
		normalized = FileUriPlaceholderPrefixPattern().Replace(normalized, "file:///");
		normalized = NormalizePathPlaceholderSeparators(normalized);
		normalized = PreviewColumnCountPattern().Replace(
			normalized,
			static match => $"{match.Groups["prefix"].Value}<COLUMNS>");
		normalized = string.Join(
			'\n',
			normalized
				.Split('\n')
				.Select(static line => line.TrimEnd()));
		return normalized.TrimEnd();
	}

	private static string ReplacePathVariants(
		string value,
		string source,
		string replacement)
	{
		if (Path.IsPathFullyQualified(source))
		{
			try
			{
				var fileUri = new Uri(source).AbsoluteUri.TrimEnd('/');
				value = value.Replace(
					fileUri,
					"file:///" + replacement,
					StringComparison.OrdinalIgnoreCase);
				for (var start = 1; start < fileUri.Length; start++)
				{
					var suffix = fileUri[start..];
					if (suffix.Length < 24 || suffix.Count(static character => character == '/') < 2)
						continue;
					value = value.Replace(
						"..." + suffix,
						replacement,
						StringComparison.OrdinalIgnoreCase);
				}
			}
			catch
			{
				// Continue with path variants when the platform cannot construct a file URI.
			}
		}

		var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			source,
			source.Replace('\\', '/'),
			source.Replace('/', '\\')
		};
		foreach (var variant in variants)
		{
			value = value.Replace(variant, replacement, StringComparison.OrdinalIgnoreCase);
			for (var start = 1; start < variant.Length; start++)
			{
				var suffix = variant[start..];
				if (suffix.Length < 24 ||
				    suffix.Count(static character => character is '/' or '\\') < 2)
				{
					continue;
				}
				value = value.Replace(
					"..." + suffix,
					replacement,
					StringComparison.OrdinalIgnoreCase);
			}

			if (!Path.IsPathFullyQualified(source))
				continue;
			const int minimumUniqueSuffixLength = 24;
			for (var start = 1;
			     start <= variant.Length - minimumUniqueSuffixLength;
			     start++)
			{
				var suffix = variant[start..];
				if (suffix.Count(static character => character is '/' or '\\') < 2)
					continue;
				value = ReplaceBoundedPathFragment(
					value,
					suffix,
					replacement);
			}

			for (var length = variant.Length - 1; length >= 8; length--)
			{
				value = ReplaceBoundedPathFragment(
					value,
					variant[..length],
					replacement);
			}
		}
		return value;
	}

	private static string ReplaceBoundedPathFragment(
		string value,
		string fragment,
		string replacement)
	{
		var pattern =
			$@"(?<leading>(?:^|[\u2500-\u257F])[ \t]*){Regex.Escape(fragment)}" +
			@"(?=[ \t]*(?:[\u2500-\u257F]|$))";
		return Regex.Replace(
			value,
			pattern,
			match => match.Groups["leading"].Value + replacement,
			RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
	}

	private static string NormalizePathPlaceholderSeparators(string value)
	{
		var pathPlaceholders = new[]
		{
			"<PROJECT_ROOT>",
			"<PROJECTS_ROOT>",
			"<TEMP_ROOT>",
			"<ORIGIN_ROOT>",
			"<WELCOME_ROOT>"
		};
		var normalizedLines = string.Join(
			'\n',
			value.Split('\n').Select(line =>
				pathPlaceholders.Any(line.Contains)
					? line.Replace('\\', '/')
					: line));
		normalizedLines = normalizedLines
			.Replace(
				"<TEMP_ROOT>/<PROJECT>",
				"<PROJECT_ROOT>",
				StringComparison.Ordinal)
			.Replace(
				"<PROJECT_ROOT><PROJECT_ROOT>",
				"<PROJECT_ROOT>",
				StringComparison.Ordinal);
		return PlaceholderCellPaddingPattern().Replace(
			normalizedLines,
			static match => match.Groups["content"].Value.TrimEnd() + " ");
	}

	[GeneratedRegex(@"\bv\d+(?:\.\d+){1,3}(?:[-+][0-9A-Za-z.-]+)?\b")]
	private static partial Regex VersionPattern();

	[GeneratedRegex(@"\b[0-9a-f]{32}\b", RegexOptions.IgnoreCase)]
	private static partial Regex IdentifierPattern();

	[GeneratedRegex(@"(?<=<TEMP_ROOT>[\\/])[0-9a-f]{1,32}", RegexOptions.IgnoreCase)]
	private static partial Regex TruncatedProjectIdentifierPattern();

	[GeneratedRegex(@"(?<=Tests\.Terminal[\\/])[0-9a-f]{1,32}", RegexOptions.IgnoreCase)]
	private static partial Regex TestProjectIdentifierPattern();

	[GeneratedRegex(@"(?<=\.Terminal[\\/])[0-9a-f]{1,32}", RegexOptions.IgnoreCase)]
	private static partial Regex ClippedTestProjectIdentifierPattern();

	[GeneratedRegex(@"\b[0-9a-f]{8,31}\b", RegexOptions.IgnoreCase)]
	private static partial Regex IdentifierFragmentPattern();

	[GeneratedRegex(@"(?:\.{3})?[^\s\n│]*?\.Terminal[\\/]<ID>[\\/]<PROJECT>")]
	private static partial Regex ClippedTemporaryProjectPathPattern();

	[GeneratedRegex(
		@"(?<label>Elapsed|Прошло|Vergangen|Transcurrido|Écoulé|Trascorso|Өткен уақыт|Decorrido|Вақти гузашта|O‘tgan vaqt):\s*\d{1,2}:\d{2}(?::\d{2})?")]
	private static partial Regex ElapsedPattern();

	[GeneratedRegex(
		@"(?<label>Last opened|Zuletzt geöffnet|Última apertura|Dernière ouverture|Ultima apertura|Соңғы ашылуы|Última abertura|Последнее открытие|Кушодани охирин|So‘nggi ochilish):[^\n│]*")]
	private static partial Regex RecentOpenedPattern();

	[GeneratedRegex(@"(?m)(?<prefix>(?:^|│)\s{2})[|/\\-](?<suffix>\s{3,})")]
	private static partial Regex SpinnerFramePattern();

	[GeneratedRegex(@"(?<=\d)[, \u202F](?=\d{3}(?:\D|$))")]
	private static partial Regex GroupedNumberSeparatorPattern();

	[GeneratedRegex(@"(?<=\d)[,.](?=\d{2}\s(?:B|KB|MB|GB|TB)\b)")]
	private static partial Regex ByteSizeDecimalSeparatorPattern();

	[GeneratedRegex(@"(?<=~)\d[\d ,.]*?(?=\s+\p{L})")]
	private static partial Regex EstimatedTokenCountPattern();

	[GeneratedRegex(@"file:/{2,3}(?=<)", RegexOptions.IgnoreCase)]
	private static partial Regex FileUriPlaceholderPrefixPattern();

	[GeneratedRegex(@"(?<prefix>\bColumns\s+\d+-\d+/)\d+")]
	private static partial Regex PreviewColumnCountPattern();

	[GeneratedRegex(
		@"(?<content><(?:PROJECT_ROOT|PROJECTS_ROOT|TEMP_ROOT|ORIGIN_ROOT|WELCOME_ROOT)>[^│\n]*?)\s{2,}(?=│)")]
	private static partial Regex PlaceholderCellPaddingPattern();
}
