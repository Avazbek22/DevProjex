using System.Globalization;

namespace DevProjex.Kernel.Models;

public sealed record CommandLineOptions(
	string? Path,
	AppLanguage? Language,
	bool ElevationAttempted)
{
	public static CommandLineOptions Empty { get; } = new(null, null, false);

	public bool NoUi { get; init; }
	public bool ShowHelp { get; init; }
	public bool ShowVersion { get; init; }
	public StartupReportOptions Report { get; init; } = StartupReportOptions.Disabled;
	public IReadOnlyList<string> IncludeRootFolders { get; init; } = [];
	public IReadOnlyList<string> IncludeExtensions { get; init; } = [];
	public IReadOnlyList<IgnoreOptionId> IgnoreOptions { get; init; } = [];
	public bool IgnoreOptionsSpecified { get; init; }

	public bool HasRootFolderOverrides => IncludeRootFolders.Count > 0;
	public bool HasExtensionOverrides => IncludeExtensions.Count > 0;
	public bool HasIgnoreOverrides => IgnoreOptionsSpecified;
	public bool HasSelectionOverrides => HasRootFolderOverrides || HasExtensionOverrides || HasIgnoreOverrides;

	public static CommandLineParseResult Parse(string[] args)
	{
		if (args.Length == 0)
			return CommandLineParseResult.Valid(Empty);

		string? path = null;
		AppLanguage? lang = null;
		bool elevationAttempted = false;
		bool noUi = false;
		bool showHelp = false;
		bool showVersion = false;
		var report = StartupReportOptions.Disabled;
		var includeRootFolders = new List<string>();
		var includeExtensions = new List<string>();
		var ignoreOptions = new List<IgnoreOptionId>();
		var ignoreOptionsSpecified = false;
		var errors = new List<CommandLineParseError>();
		var hasPositionalPath = false;

		for (int i = 0; i < args.Length; i++)
		{
			var arg = args[i];

			if (IsHelpToken(arg))
			{
				showHelp = true;
				continue;
			}

			if (arg.Equals("--version", StringComparison.OrdinalIgnoreCase))
			{
				showVersion = true;
				continue;
			}

			if (arg.Equals("--path", StringComparison.OrdinalIgnoreCase))
			{
				if (!TryReadRequiredValue(args, ref i, arg, errors, out var value))
					continue;

				path = value;
				continue;
			}

			if (arg.Equals("--lang", StringComparison.OrdinalIgnoreCase))
			{
				if (!TryReadRequiredValue(args, ref i, arg, errors, out var value))
					continue;

				lang = ParseLanguage(value);
				if (lang is null)
					errors.Add(new CommandLineParseError("invalid-language", $"Unsupported language code '{value}'.", arg));
				continue;
			}

			if (arg.Equals("--elevation-attempted", StringComparison.OrdinalIgnoreCase) ||
			    arg.Equals("--elevationAttempted", StringComparison.OrdinalIgnoreCase))
			{
				elevationAttempted = true;
				continue;
			}

			if (arg.Equals("--no-ui", StringComparison.OrdinalIgnoreCase) ||
			    arg.Equals("--silent", StringComparison.OrdinalIgnoreCase))
			{
				noUi = true;
				continue;
			}

			if (arg.Equals("--report", StringComparison.OrdinalIgnoreCase))
			{
				report = report with { Enabled = true };
				if (TryReadOptionalValue(args, ref i, out var value))
					report = report with { Path = value };

				continue;
			}

			if (arg.Equals("--report-path", StringComparison.OrdinalIgnoreCase))
			{
				if (!TryReadRequiredValue(args, ref i, arg, errors, out var value))
					continue;

				report = report with { Enabled = true, Path = value };
				continue;
			}

			if (arg.Equals("--report-format", StringComparison.OrdinalIgnoreCase))
			{
				if (!TryReadRequiredValue(args, ref i, arg, errors, out var value))
					continue;

				report = report with { Enabled = true };
				if (!TryParseReportFormat(value, out var format))
				{
					errors.Add(new CommandLineParseError("invalid-report-format", $"Unsupported report format '{value}'.", arg));
					continue;
				}

				report = report with { Format = format };
				continue;
			}

			if (arg.Equals("--include-root", StringComparison.OrdinalIgnoreCase))
			{
				if (TryReadRequiredValue(args, ref i, arg, errors, out var value))
					includeRootFolders.Add(value);

				continue;
			}

			if (arg.Equals("--include-extension", StringComparison.OrdinalIgnoreCase))
			{
				if (TryReadRequiredValue(args, ref i, arg, errors, out var value))
					includeExtensions.Add(NormalizeExtension(value));

				continue;
			}

			if (arg.Equals("--ignore", StringComparison.OrdinalIgnoreCase))
			{
				if (!TryReadRequiredValue(args, ref i, arg, errors, out var value))
					continue;

				ignoreOptionsSpecified = true;
				if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
				{
					ignoreOptions.Clear();
					continue;
				}

				if (!TryParseIgnoreOption(value, out var optionId))
				{
					errors.Add(new CommandLineParseError("invalid-ignore-option", $"Unsupported ignore option '{value}'.", arg));
					continue;
				}

				if (!ignoreOptions.Contains(optionId))
					ignoreOptions.Add(optionId);
				continue;
			}

			if (IsOptionToken(arg))
			{
				errors.Add(new CommandLineParseError("unknown-option", $"Unknown option '{arg}'.", arg));
				if (i + 1 < args.Length && !IsOptionToken(args[i + 1]))
					i++;
				continue;
			}

			if (hasPositionalPath || !string.IsNullOrWhiteSpace(path))
			{
				errors.Add(new CommandLineParseError("unexpected-argument", $"Unexpected positional argument '{arg}'.", arg));
				continue;
			}

			path = arg;
			hasPositionalPath = true;
		}

		var options = new CommandLineOptions(path, lang, elevationAttempted)
		{
			NoUi = noUi,
			ShowHelp = showHelp,
			ShowVersion = showVersion,
			Report = report,
			IncludeRootFolders = includeRootFolders.ToArray(),
			IncludeExtensions = includeExtensions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
			IgnoreOptions = ignoreOptions.ToArray(),
			IgnoreOptionsSpecified = ignoreOptionsSpecified
		};

		return new CommandLineParseResult(options, errors.ToArray());
	}

	public CommandLineOptions WithElevationAttempted() => this with { ElevationAttempted = true };

	public string ToArguments()
	{
		var parts = new List<string>();

		if (!string.IsNullOrWhiteSpace(Path))
		{
			parts.Add("--path");
			parts.Add(Quote(Path!));
		}

		if (Language is not null)
		{
			parts.Add("--lang");
			parts.Add(LanguageToCode(Language.Value));
		}

		if (ElevationAttempted)
			parts.Add("--elevation-attempted");

		if (NoUi)
			parts.Add("--no-ui");

		if (Report.Enabled)
		{
			parts.Add("--report");
			if (!string.IsNullOrWhiteSpace(Report.Path))
				parts.Add(Quote(Report.Path!));

			if (Report.Format != StartupReportFormat.Json)
			{
				parts.Add("--report-format");
				parts.Add(Report.Format.ToString().ToLowerInvariant());
			}
		}

		foreach (var root in IncludeRootFolders)
		{
			parts.Add("--include-root");
			parts.Add(Quote(root));
		}

		foreach (var extension in IncludeExtensions)
		{
			parts.Add("--include-extension");
			parts.Add(Quote(extension));
		}

		if (IgnoreOptionsSpecified && IgnoreOptions.Count == 0)
		{
			parts.Add("--ignore");
			parts.Add("none");
		}

		foreach (var ignoreOption in IgnoreOptions)
		{
			parts.Add("--ignore");
			parts.Add(FormatIgnoreOption(ignoreOption));
		}

		return string.Join(" ", parts);
	}

	public static AppLanguage? ParseLanguage(string code)
	{
		if (string.IsNullOrWhiteSpace(code)) return null;

		return code.Trim().ToLowerInvariant() switch
		{
			"ru" => AppLanguage.Ru,
			"en" => AppLanguage.En,
			"uz" => AppLanguage.Uz,
			"tg" => AppLanguage.Tg,
			"kk" => AppLanguage.Kk,
			"fr" => AppLanguage.Fr,
			"de" => AppLanguage.De,
			"it" => AppLanguage.It,
			_ => null
		};
	}

	public static string LanguageToCode(AppLanguage language) => language switch
	{
		AppLanguage.Ru => "ru",
		AppLanguage.En => "en",
		AppLanguage.Uz => "uz",
		AppLanguage.Tg => "tg",
		AppLanguage.Kk => "kk",
		AppLanguage.Fr => "fr",
		AppLanguage.De => "de",
		AppLanguage.It => "it",
		_ => "en"
	};

	public static AppLanguage DetectSystemLanguage()
	{
		var code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
		return code switch
		{
			"ru" => AppLanguage.Ru,
			"uz" => AppLanguage.Uz,
			"tg" => AppLanguage.Tg,
			"kk" => AppLanguage.Kk,
			"fr" => AppLanguage.Fr,
			"de" => AppLanguage.De,
			"it" => AppLanguage.It,
			_ => AppLanguage.En
		};
	}

	private static string Quote(string value)
	{
		if (string.IsNullOrEmpty(value)) return "\"\"";
		bool needsQuotes = value.Any(ch => char.IsWhiteSpace(ch) || ch == '"');

		if (!needsQuotes) return value;

		return "\"" + value.Replace("\"", "\\\"") + "\"";
	}

	private static bool TryReadRequiredValue(
		string[] args,
		ref int index,
		string optionName,
		List<CommandLineParseError> errors,
		out string value)
	{
		value = string.Empty;
		if (index + 1 >= args.Length || IsOptionToken(args[index + 1]))
		{
			errors.Add(new CommandLineParseError("missing-value", $"Option '{optionName}' requires a value.", optionName));
			return false;
		}

		value = args[++index];
		return true;
	}

	private static bool TryReadOptionalValue(string[] args, ref int index, out string value)
	{
		value = string.Empty;
		if (index + 1 >= args.Length || IsOptionToken(args[index + 1]))
			return false;

		value = args[++index];
		return true;
	}

	private static bool IsHelpToken(string value) =>
		value.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
		value.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
		value.Equals("/?", StringComparison.OrdinalIgnoreCase);

	private static bool IsOptionToken(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return false;

		return value.StartsWith("--", StringComparison.Ordinal) ||
		       (value.Length == 2 && value[0] == '-' && char.IsLetter(value[1])) ||
		       value.Equals("/?", StringComparison.Ordinal);
	}

	private static bool TryParseReportFormat(string value, out StartupReportFormat format)
	{
		if (value.Equals("json", StringComparison.OrdinalIgnoreCase))
		{
			format = StartupReportFormat.Json;
			return true;
		}

		format = StartupReportFormat.Json;
		return false;
	}

	private static string NormalizeExtension(string value)
	{
		var trimmed = value.Trim();
		if (trimmed.Length == 0 || trimmed == ".")
			return trimmed;

		return trimmed[0] == '.' ? trimmed : "." + trimmed;
	}

	private static bool TryParseIgnoreOption(string value, out IgnoreOptionId optionId)
	{
		switch (NormalizeOptionName(value))
		{
			case "smart":
			case "smart-ignore":
				optionId = IgnoreOptionId.SmartIgnore;
				return true;
			case "gitignore":
			case "git-ignore":
			case "use-gitignore":
			case "use-git-ignore":
				optionId = IgnoreOptionId.UseGitIgnore;
				return true;
			case "hidden-folders":
				optionId = IgnoreOptionId.HiddenFolders;
				return true;
			case "hidden-files":
				optionId = IgnoreOptionId.HiddenFiles;
				return true;
			case "dot-folders":
				optionId = IgnoreOptionId.DotFolders;
				return true;
			case "dot-files":
				optionId = IgnoreOptionId.DotFiles;
				return true;
			case "empty-folders":
				optionId = IgnoreOptionId.EmptyFolders;
				return true;
			case "empty-files":
				optionId = IgnoreOptionId.EmptyFiles;
				return true;
			case "extensionless-files":
			case "files-without-extension":
				optionId = IgnoreOptionId.ExtensionlessFiles;
				return true;
			default:
				optionId = default;
				return false;
		}
	}

	private static string FormatIgnoreOption(IgnoreOptionId optionId) => optionId switch
	{
		IgnoreOptionId.SmartIgnore => "smart-ignore",
		IgnoreOptionId.UseGitIgnore => "git-ignore",
		IgnoreOptionId.HiddenFolders => "hidden-folders",
		IgnoreOptionId.HiddenFiles => "hidden-files",
		IgnoreOptionId.DotFolders => "dot-folders",
		IgnoreOptionId.DotFiles => "dot-files",
		IgnoreOptionId.EmptyFolders => "empty-folders",
		IgnoreOptionId.EmptyFiles => "empty-files",
		IgnoreOptionId.ExtensionlessFiles => "extensionless-files",
		_ => optionId.ToString().ToLowerInvariant()
	};

	private static string NormalizeOptionName(string value) =>
		value.Trim().Replace('_', '-').ToLowerInvariant();
}

public sealed record StartupReportOptions(
	bool Enabled,
	string? Path,
	StartupReportFormat Format)
{
	public static StartupReportOptions Disabled { get; } = new(false, null, StartupReportFormat.Json);
}

public enum StartupReportFormat
{
	Json
}

public sealed record CommandLineParseResult(
	CommandLineOptions Options,
	IReadOnlyList<CommandLineParseError> Errors)
{
	public bool Success => Errors.Count == 0;

	public static CommandLineParseResult Valid(CommandLineOptions options) => new(options, []);
}

public sealed record CommandLineParseError(
	string Code,
	string Message,
	string? Token);
