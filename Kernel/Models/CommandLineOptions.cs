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
	public StartupExportOptions Export { get; init; } = StartupExportOptions.Disabled;
	public IReadOnlyList<string> IncludeRootFolders { get; init; } = [];
	public IReadOnlyList<string> IncludeExtensions { get; init; } = [];
	public IReadOnlyList<IgnoreOptionId> IgnoreOptions { get; init; } = [];
	public bool IgnoreOptionsSpecified { get; init; }
	public bool Strict { get; init; }

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
		var export = StartupExportOptions.Disabled;
		var includeRootFolders = new List<string>();
		var includeExtensions = new List<string>();
		var ignoreOptions = new List<IgnoreOptionId>();
		var ignoreOptionsSpecified = false;
		var strict = false;
		var errors = new List<CommandLineParseError>();
		var hasPositionalPath = false;

		for (int i = 0; i < args.Length; i++)
		{
			var rawArg = args[i];
			var arg = rawArg;
			string? inlineValue = null;
			var hasInlineValue = TrySplitOptionAssignment(rawArg, out var optionName, out var assignedValue);
			if (hasInlineValue)
			{
				arg = optionName;
				inlineValue = assignedValue;
			}

			if (IsHelpToken(arg))
			{
				showHelp = true;
				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.Version, StringComparison.OrdinalIgnoreCase))
			{
				showVersion = true;
				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.Path, StringComparison.OrdinalIgnoreCase))
			{
				if (!TryReadRequiredValue(args, ref i, arg, inlineValue, errors, out var value))
					continue;

				path = value;
				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.Language, StringComparison.OrdinalIgnoreCase))
			{
				if (!TryReadRequiredValue(args, ref i, arg, inlineValue, errors, out var value))
					continue;

				lang = ParseLanguage(value);
				if (lang is null)
					errors.Add(new CommandLineParseError("invalid-language", $"Unsupported language code '{value}'.", arg));
				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.ElevationAttempted, StringComparison.OrdinalIgnoreCase) ||
			    arg.Equals(CommandLineOptionTokens.LegacyElevationAttempted, StringComparison.OrdinalIgnoreCase))
			{
				elevationAttempted = true;
				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.NoUi, StringComparison.OrdinalIgnoreCase) ||
			    arg.Equals(CommandLineOptionTokens.Silent, StringComparison.OrdinalIgnoreCase))
			{
				noUi = true;
				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.Strict, StringComparison.OrdinalIgnoreCase))
			{
				strict = true;
				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.Report, StringComparison.OrdinalIgnoreCase))
			{
				report = report with { Enabled = true };
				if (TryReadOptionalValue(args, ref i, inlineValue, out var value))
					report = report with { Path = value };

				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.ReportPath, StringComparison.OrdinalIgnoreCase))
			{
				if (!TryReadRequiredValue(args, ref i, arg, inlineValue, errors, out var value))
					continue;

				report = report with { Enabled = true, Path = value };
				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.ReportFormat, StringComparison.OrdinalIgnoreCase))
			{
				if (!TryReadRequiredValue(args, ref i, arg, inlineValue, errors, out var value))
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

			if (arg.Equals(CommandLineOptionTokens.Export, StringComparison.OrdinalIgnoreCase))
			{
				if (!TryReadRequiredValue(args, ref i, arg, inlineValue, errors, out var value))
					continue;

				if (!TryParseExportMode(value, out var mode))
				{
					errors.Add(new CommandLineParseError("invalid-export-mode", $"Unsupported export mode '{value}'.", arg));
					continue;
				}

				export = export with { Enabled = true, Mode = mode };
				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.Output, StringComparison.OrdinalIgnoreCase) ||
			    arg.Equals(CommandLineOptionTokens.ShortOutput, StringComparison.OrdinalIgnoreCase))
			{
				if (!TryReadRequiredValue(args, ref i, arg, inlineValue, errors, out var value))
					continue;

				export = export with { Path = value };
				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.ExportFormat, StringComparison.OrdinalIgnoreCase) ||
			    arg.Equals(CommandLineOptionTokens.Format, StringComparison.OrdinalIgnoreCase))
			{
				if (!TryReadRequiredValue(args, ref i, arg, inlineValue, errors, out var value))
					continue;

				if (!TryParseExportFormat(value, out var format))
				{
					errors.Add(new CommandLineParseError("invalid-export-format", $"Unsupported export format '{value}'.", arg));
					continue;
				}

				export = export with { Format = format, FormatSpecified = true };
				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.IncludeRoot, StringComparison.OrdinalIgnoreCase) ||
			    arg.Equals(CommandLineOptionTokens.Roots, StringComparison.OrdinalIgnoreCase))
			{
				if (TryReadRequiredValue(args, ref i, arg, inlineValue, errors, out var value))
					includeRootFolders.Add(value);

				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.IncludeExtension, StringComparison.OrdinalIgnoreCase) ||
			    arg.Equals(CommandLineOptionTokens.Extensions, StringComparison.OrdinalIgnoreCase))
			{
				if (TryReadRequiredValue(args, ref i, arg, inlineValue, errors, out var value))
					includeExtensions.Add(NormalizeExtension(value));

				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.Ignore, StringComparison.OrdinalIgnoreCase))
			{
				if (!TryReadRequiredValue(args, ref i, arg, inlineValue, errors, out var value))
					continue;

				ignoreOptionsSpecified = true;
				if (value.Equals(CommandLineOptionTokens.IgnoreNone, StringComparison.OrdinalIgnoreCase))
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
				if (!hasInlineValue && i + 1 < args.Length && !IsOptionToken(args[i + 1]))
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
			Export = export,
			IncludeRootFolders = includeRootFolders.ToArray(),
			IncludeExtensions = includeExtensions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
			IgnoreOptions = ignoreOptions.ToArray(),
			IgnoreOptionsSpecified = ignoreOptionsSpecified,
			Strict = strict
		};

		return new CommandLineParseResult(options, errors.ToArray());
	}

	public CommandLineOptions WithElevationAttempted() => this with { ElevationAttempted = true };

	public string ToArguments()
	{
		var parts = new List<string>();

		if (!string.IsNullOrWhiteSpace(Path))
		{
			parts.Add(CommandLineOptionTokens.Path);
			parts.Add(Quote(Path!));
		}

		if (Language is not null)
		{
			parts.Add(CommandLineOptionTokens.Language);
			parts.Add(LanguageToCode(Language.Value));
		}

		if (ElevationAttempted)
			parts.Add(CommandLineOptionTokens.ElevationAttempted);

		if (NoUi)
			parts.Add(CommandLineOptionTokens.NoUi);

		if (Strict)
			parts.Add(CommandLineOptionTokens.Strict);

		if (Report.Enabled)
		{
			parts.Add(CommandLineOptionTokens.Report);
			if (!string.IsNullOrWhiteSpace(Report.Path))
				parts.Add(Quote(Report.Path!));

			if (Report.Format != StartupReportFormat.Json)
			{
				parts.Add(CommandLineOptionTokens.ReportFormat);
				parts.Add(Report.Format.ToString().ToLowerInvariant());
			}
		}

		if (Export.Enabled)
		{
			parts.Add(CommandLineOptionTokens.Export);
			parts.Add(FormatExportMode(Export.Mode));
		}

		if (!string.IsNullOrWhiteSpace(Export.Path))
		{
			parts.Add(CommandLineOptionTokens.Output);
			parts.Add(Quote(Export.Path!));
		}

		if (Export.FormatSpecified || Export.Format != TreeTextFormat.Ascii)
		{
			parts.Add(CommandLineOptionTokens.ExportFormat);
			parts.Add(Export.Format.ToString().ToLowerInvariant());
		}

		foreach (var root in IncludeRootFolders)
		{
			parts.Add(CommandLineOptionTokens.IncludeRoot);
			parts.Add(Quote(root));
		}

		foreach (var extension in IncludeExtensions)
		{
			parts.Add(CommandLineOptionTokens.IncludeExtension);
			parts.Add(Quote(extension));
		}

		if (IgnoreOptionsSpecified && IgnoreOptions.Count == 0)
		{
			parts.Add(CommandLineOptionTokens.Ignore);
			parts.Add(CommandLineOptionTokens.IgnoreNone);
		}

		foreach (var ignoreOption in IgnoreOptions)
		{
			parts.Add(CommandLineOptionTokens.Ignore);
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
		string? inlineValue,
		List<CommandLineParseError> errors,
		out string value)
	{
		value = string.Empty;
		if (inlineValue is not null)
		{
			if (inlineValue.Length == 0)
			{
				errors.Add(new CommandLineParseError("missing-value", $"Option '{optionName}' requires a value.", optionName));
				return false;
			}

			value = inlineValue;
			return true;
		}

		if (index + 1 >= args.Length || IsOptionToken(args[index + 1]))
		{
			errors.Add(new CommandLineParseError("missing-value", $"Option '{optionName}' requires a value.", optionName));
			return false;
		}

		value = args[++index];
		return true;
	}

	private static bool TryReadOptionalValue(string[] args, ref int index, string? inlineValue, out string value)
	{
		value = string.Empty;
		if (inlineValue is not null)
		{
			if (inlineValue.Length == 0)
				return false;

			value = inlineValue;
			return true;
		}

		if (index + 1 >= args.Length || IsOptionToken(args[index + 1]))
			return false;

		value = args[++index];
		return true;
	}

	private static bool TrySplitOptionAssignment(string arg, out string optionName, out string value)
	{
		optionName = arg;
		value = string.Empty;

		var separatorIndex = arg.IndexOf('=');
		if (separatorIndex < 0)
			return false;

		if (arg.StartsWith("--", StringComparison.Ordinal))
		{
			if (separatorIndex <= 2)
				return false;

			optionName = arg[..separatorIndex];
			value = arg[(separatorIndex + 1)..];
			return true;
		}

		if (arg.Length >= 3 && arg[0] == '-' && char.IsLetter(arg[1]) && separatorIndex == 2)
		{
			optionName = arg[..separatorIndex];
			value = arg[(separatorIndex + 1)..];
			return true;
		}

		return false;
	}

	private static bool IsHelpToken(string value) =>
		value.Equals(CommandLineOptionTokens.Help, StringComparison.OrdinalIgnoreCase) ||
		value.Equals(CommandLineOptionTokens.ShortHelp, StringComparison.OrdinalIgnoreCase) ||
		value.Equals(CommandLineOptionTokens.WindowsHelp, StringComparison.OrdinalIgnoreCase);

	private static bool IsOptionToken(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return false;

		return value.StartsWith("--", StringComparison.Ordinal) ||
		       (value.Length == 2 && value[0] == '-' && char.IsLetter(value[1])) ||
		       value.Equals(CommandLineOptionTokens.WindowsHelp, StringComparison.Ordinal);
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

	private static bool TryParseExportMode(string value, out StartupExportMode mode)
	{
		switch (NormalizeOptionName(value))
		{
			case "tree":
				mode = StartupExportMode.Tree;
				return true;
			case "content":
				mode = StartupExportMode.Content;
				return true;
			case "tree-content":
			case "tree-and-content":
			case "all":
				mode = StartupExportMode.TreeContent;
				return true;
			default:
				mode = default;
				return false;
		}
	}

	private static bool TryParseExportFormat(string value, out TreeTextFormat format)
	{
		switch (NormalizeOptionName(value))
		{
			case "ascii":
			case "text":
				format = TreeTextFormat.Ascii;
				return true;
			case "json":
				format = TreeTextFormat.Json;
				return true;
			default:
				format = TreeTextFormat.Ascii;
				return false;
		}
	}

	private static string FormatExportMode(StartupExportMode mode) => mode switch
	{
		StartupExportMode.Tree => "tree",
		StartupExportMode.Content => "content",
		StartupExportMode.TreeContent => "tree-content",
		_ => mode.ToString().ToLowerInvariant()
	};

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
			case CommandLineOptionTokens.IgnoreSmartIgnore:
				optionId = IgnoreOptionId.SmartIgnore;
				return true;
			case "gitignore":
			case CommandLineOptionTokens.IgnoreGitIgnore:
			case "use-gitignore":
			case "use-git-ignore":
				optionId = IgnoreOptionId.UseGitIgnore;
				return true;
			case CommandLineOptionTokens.IgnoreHiddenFolders:
				optionId = IgnoreOptionId.HiddenFolders;
				return true;
			case CommandLineOptionTokens.IgnoreHiddenFiles:
				optionId = IgnoreOptionId.HiddenFiles;
				return true;
			case CommandLineOptionTokens.IgnoreDotFolders:
				optionId = IgnoreOptionId.DotFolders;
				return true;
			case CommandLineOptionTokens.IgnoreDotFiles:
				optionId = IgnoreOptionId.DotFiles;
				return true;
			case CommandLineOptionTokens.IgnoreEmptyFolders:
				optionId = IgnoreOptionId.EmptyFolders;
				return true;
			case CommandLineOptionTokens.IgnoreEmptyFiles:
				optionId = IgnoreOptionId.EmptyFiles;
				return true;
			case CommandLineOptionTokens.IgnoreExtensionlessFiles:
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
		IgnoreOptionId.SmartIgnore => CommandLineOptionTokens.IgnoreSmartIgnore,
		IgnoreOptionId.UseGitIgnore => CommandLineOptionTokens.IgnoreGitIgnore,
		IgnoreOptionId.HiddenFolders => CommandLineOptionTokens.IgnoreHiddenFolders,
		IgnoreOptionId.HiddenFiles => CommandLineOptionTokens.IgnoreHiddenFiles,
		IgnoreOptionId.DotFolders => CommandLineOptionTokens.IgnoreDotFolders,
		IgnoreOptionId.DotFiles => CommandLineOptionTokens.IgnoreDotFiles,
		IgnoreOptionId.EmptyFolders => CommandLineOptionTokens.IgnoreEmptyFolders,
		IgnoreOptionId.EmptyFiles => CommandLineOptionTokens.IgnoreEmptyFiles,
		IgnoreOptionId.ExtensionlessFiles => CommandLineOptionTokens.IgnoreExtensionlessFiles,
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

	public bool WriteToStandardOutput =>
		string.Equals(Path?.Trim(), CommandLineOptionTokens.StandardOutputReportPath, StringComparison.Ordinal);
}

public enum StartupReportFormat
{
	Json
}

public sealed record StartupExportOptions(
	bool Enabled,
	StartupExportMode Mode,
	string? Path,
	TreeTextFormat Format)
{
	public static StartupExportOptions Disabled { get; } = new(false, StartupExportMode.TreeContent, null, TreeTextFormat.Ascii);

	public bool FormatSpecified { get; init; }

	public bool HasOutputPath => !string.IsNullOrWhiteSpace(Path);

	public bool WriteToStandardOutput =>
		Enabled &&
		(string.IsNullOrWhiteSpace(Path) ||
		 string.Equals(Path.Trim(), CommandLineOptionTokens.StandardOutputReportPath, StringComparison.Ordinal));
}

public enum StartupExportMode
{
	Tree,
	Content,
	TreeContent
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
