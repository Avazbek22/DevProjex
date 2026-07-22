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
	public StartupBenchmarkOptions Benchmark { get; init; } = StartupBenchmarkOptions.Disabled;
	public StartupUiBenchmarkOptions UiBenchmark { get; init; } = StartupUiBenchmarkOptions.Disabled;
	public StartupSessionMetricsOptions SessionMetrics { get; init; } = StartupSessionMetricsOptions.Disabled;
	public StartupUiBenchmarkScriptOptions UiBenchmarkScript { get; init; } = StartupUiBenchmarkScriptOptions.Disabled;
	public StartupExportOptions Export { get; init; } = StartupExportOptions.Disabled;
	public StartupUiOptions Ui { get; init; } = StartupUiOptions.Default;
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
		var benchmark = StartupBenchmarkOptions.Disabled;
		var uiBenchmark = StartupUiBenchmarkOptions.Disabled;
		var sessionMetrics = StartupSessionMetricsOptions.Disabled;
		var uiBenchmarkScript = StartupUiBenchmarkScriptOptions.Disabled;
		var export = StartupExportOptions.Disabled;
		var ui = StartupUiOptions.Default;
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
				if (TryRejectUnexpectedInlineValue(arg, inlineValue, errors))
					continue;

				showHelp = true;
				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.Version, StringComparison.OrdinalIgnoreCase))
			{
				if (TryRejectUnexpectedInlineValue(arg, inlineValue, errors))
					continue;

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
				if (TryRejectUnexpectedInlineValue(arg, inlineValue, errors))
					continue;

				elevationAttempted = true;
				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.NoUi, StringComparison.OrdinalIgnoreCase) ||
			    arg.Equals(CommandLineOptionTokens.Silent, StringComparison.OrdinalIgnoreCase))
			{
				if (TryRejectUnexpectedInlineValue(arg, inlineValue, errors))
					continue;

				noUi = true;
				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.Strict, StringComparison.OrdinalIgnoreCase))
			{
				if (TryRejectUnexpectedInlineValue(arg, inlineValue, errors))
					continue;

				strict = true;
				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.Last, StringComparison.OrdinalIgnoreCase))
			{
				if (TryRejectUnexpectedInlineValue(arg, inlineValue, errors))
					continue;

				ui = ui with { OpenLastProject = true };
				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.Preview, StringComparison.OrdinalIgnoreCase))
			{
				if (TryRejectUnexpectedInlineValue(arg, inlineValue, errors))
					continue;

				ui = ui with { OpenPreview = true };
				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.PreviewMode, StringComparison.OrdinalIgnoreCase))
			{
				if (!TryReadRequiredValue(args, ref i, arg, inlineValue, errors, out var value))
					continue;

				if (!TryParseStartupPreviewMode(value, out var previewMode))
				{
					errors.Add(new CommandLineParseError("invalid-preview-mode", $"Unsupported preview mode '{value}'.", arg));
					continue;
				}

				ui = ui with { OpenPreview = true, PreviewMode = previewMode };
				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.TreeFormat, StringComparison.OrdinalIgnoreCase))
			{
				if (!TryReadRequiredValue(args, ref i, arg, inlineValue, errors, out var value))
					continue;

				if (!TryParseTreeTextFormat(value, out var format))
				{
					errors.Add(new CommandLineParseError("invalid-tree-format", $"Unsupported tree format '{value}'.", arg));
					continue;
				}

				ui = ui with { TreeFormat = format };
				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.TreeFilter, StringComparison.OrdinalIgnoreCase))
			{
				if (!TryReadRequiredValue(args, ref i, arg, inlineValue, errors, out var value))
					continue;

				ui = ui with { TreeFilter = value };
				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.PreviewSearch, StringComparison.OrdinalIgnoreCase))
			{
				if (!TryReadRequiredValue(args, ref i, arg, inlineValue, errors, out var value))
					continue;

				ui = ui with { OpenPreview = true, PreviewSearch = value };
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

			if (arg.Equals(CommandLineOptionTokens.Benchmark, StringComparison.OrdinalIgnoreCase))
			{
				if (!TryReadRequiredValue(args, ref i, arg, inlineValue, errors, out var value))
					continue;

				benchmark = benchmark with { Enabled = true, Path = value };
				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.BenchmarkUi, StringComparison.OrdinalIgnoreCase))
			{
				if (!TryReadRequiredValue(args, ref i, arg, inlineValue, errors, out var value))
					continue;

				uiBenchmark = uiBenchmark with { Enabled = true, Path = value };
				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.BenchmarkOutput, StringComparison.OrdinalIgnoreCase))
			{
				if (!TryReadRequiredValue(args, ref i, arg, inlineValue, errors, out var value))
					continue;

				benchmark = benchmark with { OutputPath = value };
				uiBenchmark = uiBenchmark with { OutputPath = value };
				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.SessionMetrics, StringComparison.OrdinalIgnoreCase))
			{
				if (!TryReadRequiredValue(args, ref i, arg, inlineValue, errors, out var value))
					continue;

				sessionMetrics = sessionMetrics with { Enabled = true, Path = value };
				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.SessionMetricsOutput, StringComparison.OrdinalIgnoreCase))
			{
				if (!TryReadRequiredValue(args, ref i, arg, inlineValue, errors, out var value))
					continue;

				sessionMetrics = sessionMetrics with { OutputPath = value };
				continue;
			}

			if (arg.Equals(CommandLineOptionTokens.UiBenchmarkScript, StringComparison.OrdinalIgnoreCase))
			{
				if (!TryReadRequiredValue(args, ref i, arg, inlineValue, errors, out var value))
					continue;

				if (!TryParseUiBenchmarkScript(value, out var script))
				{
					errors.Add(new CommandLineParseError("invalid-ui-benchmark-script", $"Unsupported UI benchmark script '{value}'.", arg));
					continue;
				}

				uiBenchmarkScript = uiBenchmarkScript with { Enabled = true, Script = script };
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

				if (!TryParseTreeTextFormat(value, out var format))
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
				errors.Add(new CommandLineParseError("unknown-option", BuildUnknownOptionMessage(arg), arg));
				if (!hasInlineValue && i + 1 < args.Length && !IsOptionToken(args[i + 1]))
					i++;
				continue;
			}

			if (hasPositionalPath || !string.IsNullOrWhiteSpace(path))
			{
				errors.Add(new CommandLineParseError("unexpected-argument", $"Unexpected positional argument '{arg}'.", arg));
				continue;
			}

			if (TryResolveMissingOptionPrefix(arg, out var suggestedOption))
			{
				errors.Add(new CommandLineParseError(
					"missing-option-prefix",
					$"Unknown command or path-like argument '{arg}'. Did you mean '{suggestedOption}'? Use --path {Quote(arg)} if this is a folder name.",
					arg));
				continue;
			}

			path = arg;
			hasPositionalPath = true;
		}

		ValidateStartupUiOptions(path, ui, sessionMetrics, errors);
		ValidateBenchmarkOptions(path, noUi, strict, report, benchmark, uiBenchmark, export, ui, includeRootFolders, includeExtensions, ignoreOptionsSpecified, errors);
		ValidateUiBenchmarkOptions(path, noUi, strict, report, benchmark, uiBenchmark, sessionMetrics, uiBenchmarkScript, export, ui, includeRootFolders, includeExtensions, ignoreOptionsSpecified, errors);
		ValidateSessionMetricsOptions(path, noUi, strict, report, benchmark, uiBenchmark, sessionMetrics, uiBenchmarkScript, export, includeRootFolders, includeExtensions, ignoreOptionsSpecified, errors);

		var options = new CommandLineOptions(path, lang, elevationAttempted)
		{
			NoUi = noUi,
			ShowHelp = showHelp,
			ShowVersion = showVersion,
			Report = report,
			Benchmark = benchmark,
			UiBenchmark = uiBenchmark,
			SessionMetrics = sessionMetrics,
			UiBenchmarkScript = uiBenchmarkScript,
			Export = export,
			Ui = ui,
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

		if (Benchmark.Enabled)
		{
			parts.Add(CommandLineOptionTokens.Benchmark);
			parts.Add(Quote(Benchmark.Path!));
		}

		if (UiBenchmark.Enabled)
		{
			parts.Add(CommandLineOptionTokens.BenchmarkUi);
			parts.Add(Quote(UiBenchmark.Path!));
		}

		var benchmarkOutputPath = Benchmark.Enabled ? Benchmark.OutputPath : UiBenchmark.OutputPath;
		if (!string.IsNullOrWhiteSpace(benchmarkOutputPath))
		{
			parts.Add(CommandLineOptionTokens.BenchmarkOutput);
			parts.Add(Quote(benchmarkOutputPath!));
		}

		if (SessionMetrics.Enabled)
		{
			parts.Add(CommandLineOptionTokens.SessionMetrics);
			parts.Add(Quote(SessionMetrics.Path!));
		}

		if (!string.IsNullOrWhiteSpace(SessionMetrics.OutputPath))
		{
			parts.Add(CommandLineOptionTokens.SessionMetricsOutput);
			parts.Add(Quote(SessionMetrics.OutputPath!));
		}

		if (UiBenchmarkScript.Enabled)
		{
			parts.Add(CommandLineOptionTokens.UiBenchmarkScript);
			parts.Add(FormatUiBenchmarkScript(UiBenchmarkScript.Script));
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
			parts.Add(FormatTreeTextFormat(Export.Format));
		}

		if (Ui.OpenLastProject)
			parts.Add(CommandLineOptionTokens.Last);

		if (Ui.OpenPreview && Ui.PreviewMode is null && string.IsNullOrWhiteSpace(Ui.PreviewSearch))
			parts.Add(CommandLineOptionTokens.Preview);

		if (Ui.PreviewMode is { } previewMode)
		{
			parts.Add(CommandLineOptionTokens.PreviewMode);
			parts.Add(FormatStartupPreviewMode(previewMode));
		}

		if (Ui.TreeFormat is { } treeFormat)
		{
			parts.Add(CommandLineOptionTokens.TreeFormat);
			parts.Add(FormatTreeTextFormat(treeFormat));
		}

		if (!string.IsNullOrWhiteSpace(Ui.TreeFilter))
		{
			parts.Add(CommandLineOptionTokens.TreeFilter);
			parts.Add(Quote(Ui.TreeFilter!));
		}

		if (!string.IsNullOrWhiteSpace(Ui.PreviewSearch))
		{
			parts.Add(CommandLineOptionTokens.PreviewSearch);
			parts.Add(Quote(Ui.PreviewSearch!));
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
			"es" or "es-es" => AppLanguage.Es,
			"pt" or "pt-br" => AppLanguage.Pt,
			"pt-pt" or "pt-ao" or "pt-mz" or "pt-cv" or "pt-gw" or "pt-st" or "pt-gq" or "pt-tl" or "pt-mo" => AppLanguage.PtPt,
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
		AppLanguage.Es => "es",
		AppLanguage.Pt => "pt",
		AppLanguage.PtPt => "pt-pt",
		_ => "en"
	};

	public static AppLanguage DetectSystemLanguage()
	{
		var cultureName = CultureInfo.CurrentUICulture.Name.ToLowerInvariant();
		if (cultureName is "pt-pt" or "pt-ao" or "pt-mz" or "pt-cv" or "pt-gw" or "pt-st" or "pt-gq" or "pt-tl" or "pt-mo")
			return AppLanguage.PtPt;

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
			"es" => AppLanguage.Es,
			"pt" => AppLanguage.Pt,
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

	private static bool TryRejectUnexpectedInlineValue(
		string optionName,
		string? inlineValue,
		List<CommandLineParseError> errors)
	{
		if (inlineValue is null)
			return false;

		errors.Add(new CommandLineParseError(
			"unexpected-value",
			$"Option '{optionName}' does not accept a value.",
			optionName));
		return true;
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

	private static string BuildUnknownOptionMessage(string option)
	{
		if (TrySuggestLongOption(option, allowFuzzy: true, out var suggestion))
			return $"Unknown option '{option}'. Did you mean '{suggestion}'?";

		return $"Unknown option '{option}'.";
	}

	private static bool TryResolveMissingOptionPrefix(string value, out string suggestedOption)
	{
		suggestedOption = string.Empty;
		if (string.IsNullOrWhiteSpace(value) ||
		    value.StartsWith(".", StringComparison.Ordinal) ||
		    value.StartsWith("~", StringComparison.Ordinal))
		{
			return false;
		}

		var looksLikeSlashOption =
			value.Length > 1 &&
			value[0] == '/' &&
			value[1] != '/' &&
			value.IndexOf('/', 1) < 0 &&
			value.IndexOf('\\', 1) < 0;
		if (looksLikeSlashOption &&
		    TrySuggestLongOption(value, value.Contains('-') || value.Contains('_'), out suggestedOption))
		{
			return true;
		}

		if (global::System.IO.Path.IsPathRooted(value))
			return false;

		var allowFuzzy = value.Contains('-') || value.Contains('_');
		return TrySuggestLongOption(value, allowFuzzy, out suggestedOption);
	}

	private static bool TrySuggestLongOption(string value, bool allowFuzzy, out string suggestedOption)
	{
		suggestedOption = string.Empty;
		var normalizedValue = NormalizeOptionCandidate(value);
		if (normalizedValue.Length == 0)
			return false;

		var compactValue = RemoveOptionSeparators(normalizedValue);
		string? nearestOption = null;
		var nearestDistance = int.MaxValue;

		foreach (var option in CommandLineOptionTokens.PublicHelpTokens)
		{
			if (!option.StartsWith("--", StringComparison.Ordinal))
				continue;

			var normalizedOption = NormalizeOptionCandidate(option);
			if (normalizedValue.Equals(normalizedOption, StringComparison.Ordinal) ||
			    compactValue.Equals(RemoveOptionSeparators(normalizedOption), StringComparison.Ordinal))
			{
				suggestedOption = option;
				return true;
			}

			if (!allowFuzzy)
				continue;

			var distance = CalculateBoundedEditDistance(normalizedValue, normalizedOption, maxDistance: 2);
			if (distance < nearestDistance)
			{
				nearestDistance = distance;
				nearestOption = option;
			}
		}

		if (nearestOption is null || nearestDistance > 2)
			return false;

		suggestedOption = nearestOption;
		return true;
	}

	private static string NormalizeOptionCandidate(string value)
	{
		var trimmed = value.Trim();
		while (trimmed.StartsWith("--", StringComparison.Ordinal))
			trimmed = trimmed[2..];
		while (trimmed.StartsWith("-", StringComparison.Ordinal))
			trimmed = trimmed[1..];
		while (trimmed.StartsWith("/", StringComparison.Ordinal))
			trimmed = trimmed[1..];

		return NormalizeOptionName(trimmed);
	}

	private static string RemoveOptionSeparators(string value) =>
		value.Replace("-", string.Empty, StringComparison.Ordinal);

	private static int CalculateBoundedEditDistance(string left, string right, int maxDistance)
	{
		if (Math.Abs(left.Length - right.Length) > maxDistance)
			return maxDistance + 1;

		var previous = new int[right.Length + 1];
		var current = new int[right.Length + 1];
		for (var j = 0; j <= right.Length; j++)
			previous[j] = j;

		for (var i = 1; i <= left.Length; i++)
		{
			current[0] = i;
			var bestInRow = current[0];
			for (var j = 1; j <= right.Length; j++)
			{
				var substitutionCost = left[i - 1] == right[j - 1] ? 0 : 1;
				current[j] = Math.Min(
					Math.Min(current[j - 1] + 1, previous[j] + 1),
					previous[j - 1] + substitutionCost);
				bestInRow = Math.Min(bestInRow, current[j]);
			}

			if (bestInRow > maxDistance)
				return maxDistance + 1;

			(previous, current) = (current, previous);
		}

		return previous[right.Length];
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

	private static bool TryParseStartupPreviewMode(string value, out StartupPreviewMode mode)
	{
		switch (NormalizeOptionName(value))
		{
			case "tree":
				mode = StartupPreviewMode.Tree;
				return true;
			case "content":
				mode = StartupPreviewMode.Content;
				return true;
			case "tree-content":
			case "tree-and-content":
			case "all":
				mode = StartupPreviewMode.TreeContent;
				return true;
			default:
				mode = default;
				return false;
		}
	}

	private static bool TryParseTreeTextFormat(string value, out TreeTextFormat format)
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
			case "xml":
				format = TreeTextFormat.Xml;
				return true;
			case "md":
			case "markdown":
				format = TreeTextFormat.Markdown;
				return true;
			default:
				format = TreeTextFormat.Ascii;
				return false;
		}
	}

	private static void ValidateStartupUiOptions(
		string? path,
		StartupUiOptions ui,
		StartupSessionMetricsOptions sessionMetrics,
		List<CommandLineParseError> errors)
	{
		if (ui.OpenLastProject && !string.IsNullOrWhiteSpace(path))
		{
			errors.Add(new CommandLineParseError(
				"conflicting-startup-target",
				"Use either --last or --path/positional folder, not both.",
				CommandLineOptionTokens.Last));
		}

		if (ui.OpenLastProject && sessionMetrics.Enabled)
		{
			errors.Add(new CommandLineParseError(
				"conflicting-session-metrics-last",
				"Use either --session-metrics <folder> or --last, not both.",
				CommandLineOptionTokens.SessionMetrics));
		}

		if (ui.HasStartupActions &&
		    !ui.OpenLastProject &&
		    string.IsNullOrWhiteSpace(path) &&
		    !sessionMetrics.Enabled)
		{
			errors.Add(new CommandLineParseError(
				"ui-startup-requires-project",
				"UI startup options require --path, a positional folder, --last, or --session-metrics.",
				null));
		}

		if (!string.IsNullOrWhiteSpace(ui.TreeFilter) &&
		    !string.IsNullOrWhiteSpace(ui.PreviewSearch))
		{
			errors.Add(new CommandLineParseError(
				"conflicting-search-and-filter",
				"--tree-filter and --preview-search cannot be used together because the desktop UI shows only one tree text tool at a time.",
				CommandLineOptionTokens.PreviewSearch));
		}
	}

	private static bool TryParseUiBenchmarkScript(string value, out StartupUiBenchmarkScript script)
	{
		switch (NormalizeOptionName(value))
		{
			case "standard":
			case "standard-ui":
				script = StartupUiBenchmarkScript.Standard;
				return true;
			default:
				script = default;
				return false;
		}
	}

	private static void ValidateBenchmarkOptions(
		string? path,
		bool noUi,
		bool strict,
		StartupReportOptions report,
		StartupBenchmarkOptions benchmark,
		StartupUiBenchmarkOptions uiBenchmark,
		StartupExportOptions export,
		StartupUiOptions ui,
		IReadOnlyList<string> includeRootFolders,
		IReadOnlyList<string> includeExtensions,
		bool ignoreOptionsSpecified,
		List<CommandLineParseError> errors)
	{
		if (!benchmark.Enabled)
		{
			if (!uiBenchmark.Enabled && !string.IsNullOrWhiteSpace(benchmark.OutputPath))
			{
				errors.Add(new CommandLineParseError(
					"benchmark-output-requires-benchmark",
					"--benchmark-output requires --benchmark or --benchmark-ui.",
					CommandLineOptionTokens.BenchmarkOutput));
			}

			return;
		}

		if (!string.IsNullOrWhiteSpace(path))
		{
			errors.Add(new CommandLineParseError(
				"conflicting-benchmark-path",
				"Use --benchmark <folder> without --path or a positional folder.",
				CommandLineOptionTokens.Benchmark));
		}

		if (noUi || strict || report.Enabled || export.Enabled || export.HasOutputPath || export.FormatSpecified ||
		    uiBenchmark.Enabled || ui.HasStartupActions || includeRootFolders.Count > 0 || includeExtensions.Count > 0 || ignoreOptionsSpecified)
		{
			errors.Add(new CommandLineParseError(
				"conflicting-benchmark-options",
				"--benchmark runs the standard project report benchmark and cannot be combined with UI benchmark, report, export, UI startup, selection, strict, --no-ui, or --silent options.",
				CommandLineOptionTokens.Benchmark));
		}
	}

	private static void ValidateUiBenchmarkOptions(
		string? path,
		bool noUi,
		bool strict,
		StartupReportOptions report,
		StartupBenchmarkOptions benchmark,
		StartupUiBenchmarkOptions uiBenchmark,
		StartupSessionMetricsOptions sessionMetrics,
		StartupUiBenchmarkScriptOptions uiBenchmarkScript,
		StartupExportOptions export,
		StartupUiOptions ui,
		IReadOnlyList<string> includeRootFolders,
		IReadOnlyList<string> includeExtensions,
		bool ignoreOptionsSpecified,
		List<CommandLineParseError> errors)
	{
		if (!uiBenchmark.Enabled)
			return;

		if (!string.IsNullOrWhiteSpace(path))
		{
			errors.Add(new CommandLineParseError(
				"conflicting-ui-benchmark-path",
				"Use --benchmark-ui <folder> without --path or a positional folder.",
				CommandLineOptionTokens.BenchmarkUi));
		}

		if (noUi || strict || report.Enabled || benchmark.Enabled || sessionMetrics.Enabled || uiBenchmarkScript.Enabled ||
		    export.Enabled || export.HasOutputPath || export.FormatSpecified || ui.HasStartupActions ||
		    includeRootFolders.Count > 0 || includeExtensions.Count > 0 || ignoreOptionsSpecified)
		{
			errors.Add(new CommandLineParseError(
				"conflicting-ui-benchmark-options",
				"--benchmark-ui runs the standard desktop UI benchmark and cannot be combined with report, export, benchmark, session metrics, UI startup, selection, strict, --no-ui, or --silent options.",
				CommandLineOptionTokens.BenchmarkUi));
		}
	}

	private static void ValidateSessionMetricsOptions(
		string? path,
		bool noUi,
		bool strict,
		StartupReportOptions report,
		StartupBenchmarkOptions benchmark,
		StartupUiBenchmarkOptions uiBenchmark,
		StartupSessionMetricsOptions sessionMetrics,
		StartupUiBenchmarkScriptOptions uiBenchmarkScript,
		StartupExportOptions export,
		IReadOnlyList<string> includeRootFolders,
		IReadOnlyList<string> includeExtensions,
		bool ignoreOptionsSpecified,
		List<CommandLineParseError> errors)
	{
		if (!sessionMetrics.Enabled)
		{
			if (!string.IsNullOrWhiteSpace(sessionMetrics.OutputPath))
			{
				errors.Add(new CommandLineParseError(
					"session-metrics-output-requires-session-metrics",
					"--session-metrics-output requires --session-metrics.",
					CommandLineOptionTokens.SessionMetricsOutput));
			}

			if (uiBenchmarkScript.Enabled)
			{
				errors.Add(new CommandLineParseError(
					"ui-benchmark-script-requires-session-metrics",
					"--ui-benchmark-script is an internal option and requires --session-metrics.",
					CommandLineOptionTokens.UiBenchmarkScript));
			}

			return;
		}

		if (string.Equals(sessionMetrics.OutputPath?.Trim(), CommandLineOptionTokens.StandardOutputReportPath, StringComparison.Ordinal))
		{
			errors.Add(new CommandLineParseError(
				"session-metrics-output-requires-file",
				"--session-metrics-output must point to a JSON file path, not stdout.",
				CommandLineOptionTokens.SessionMetricsOutput));
		}

		if (!string.IsNullOrWhiteSpace(path))
		{
			errors.Add(new CommandLineParseError(
				"conflicting-session-metrics-path",
				"Use --session-metrics <folder> without --path or a positional folder.",
				CommandLineOptionTokens.SessionMetrics));
		}

		if (string.IsNullOrWhiteSpace(sessionMetrics.Path))
		{
			errors.Add(new CommandLineParseError(
				"session-metrics-requires-path",
				"--session-metrics requires a folder path.",
				CommandLineOptionTokens.SessionMetrics));
		}

		if (noUi || strict || report.Enabled || benchmark.Enabled || uiBenchmark.Enabled || export.Enabled || export.HasOutputPath || export.FormatSpecified ||
		    includeRootFolders.Count > 0 || includeExtensions.Count > 0 || ignoreOptionsSpecified)
		{
			errors.Add(new CommandLineParseError(
				"conflicting-session-metrics-options",
				"--session-metrics opens the desktop app and cannot be combined with report, export, benchmark, UI benchmark, selection, strict, --no-ui, or --silent options.",
				CommandLineOptionTokens.SessionMetrics));
		}
	}

	private static string FormatExportMode(StartupExportMode mode) => mode switch
	{
		StartupExportMode.Tree => "tree",
		StartupExportMode.Content => "content",
		StartupExportMode.TreeContent => "tree-content",
		_ => mode.ToString().ToLowerInvariant()
	};

	private static string FormatStartupPreviewMode(StartupPreviewMode mode) => mode switch
	{
		StartupPreviewMode.Tree => "tree",
		StartupPreviewMode.Content => "content",
		StartupPreviewMode.TreeContent => "tree-content",
		_ => mode.ToString().ToLowerInvariant()
	};

	private static string FormatTreeTextFormat(TreeTextFormat format) => format switch
	{
		TreeTextFormat.Ascii => "ascii",
		TreeTextFormat.Json => "json",
		TreeTextFormat.Xml => "xml",
		TreeTextFormat.Markdown => "md",
		_ => format.ToString().ToLowerInvariant()
	};

	private static string FormatUiBenchmarkScript(StartupUiBenchmarkScript script) => script switch
	{
		StartupUiBenchmarkScript.Standard => "standard",
		_ => script.ToString().ToLowerInvariant()
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

public sealed record StartupBenchmarkOptions(
	bool Enabled,
	string? Path,
	string? OutputPath)
{
	public static StartupBenchmarkOptions Disabled { get; } = new(false, null, null);
}

public sealed record StartupUiBenchmarkOptions(
	bool Enabled,
	string? Path,
	string? OutputPath)
{
	public static StartupUiBenchmarkOptions Disabled { get; } = new(false, null, null);
}

public sealed record StartupSessionMetricsOptions(
	bool Enabled,
	string? Path,
	string? OutputPath)
{
	public static StartupSessionMetricsOptions Disabled { get; } = new(false, null, null);
}

public sealed record StartupUiBenchmarkScriptOptions(
	bool Enabled,
	StartupUiBenchmarkScript Script)
{
	public static StartupUiBenchmarkScriptOptions Disabled { get; } = new(false, StartupUiBenchmarkScript.Standard);
}

public enum StartupUiBenchmarkScript
{
	Standard
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

public sealed record StartupUiOptions(
	bool OpenLastProject,
	bool OpenPreview,
	StartupPreviewMode? PreviewMode,
	TreeTextFormat? TreeFormat,
	string? TreeFilter,
	string? PreviewSearch)
{
	public static StartupUiOptions Default { get; } = new(
		OpenLastProject: false,
		OpenPreview: false,
		PreviewMode: null,
		TreeFormat: null,
		TreeFilter: null,
		PreviewSearch: null);

	public bool HasStartupActions =>
		OpenLastProject ||
		OpenPreview ||
		PreviewMode is not null ||
		TreeFormat is not null ||
		!string.IsNullOrWhiteSpace(TreeFilter) ||
		!string.IsNullOrWhiteSpace(PreviewSearch);
}

public enum StartupPreviewMode
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
