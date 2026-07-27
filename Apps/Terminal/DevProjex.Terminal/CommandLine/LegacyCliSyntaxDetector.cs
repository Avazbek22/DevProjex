namespace DevProjex.Terminal.CommandLine;

public sealed record LegacyCliMigration(string Replacement, string Message);

public static class LegacyCliSyntaxDetector
{
	private static readonly HashSet<string> LegacyActions = new(StringComparer.OrdinalIgnoreCase)
	{
		"--no-ui",
		"--silent",
		"--report",
		"--export",
		"--copy",
		"--benchmark",
		"--benchmark-ui",
		"--session-metrics"
	};
	private static readonly HashSet<string> LegacyOptionsWithValues = new(StringComparer.OrdinalIgnoreCase)
	{
		"--path",
		"--language",
		"--report",
		"--report-path",
		"--report-format",
		"--export",
		"--copy",
		"--output",
		"-o",
		"--format",
		"--export-format",
		"--include-root",
		"--roots",
		"--include-extension",
		"--ext",
		"--ignore",
		"--benchmark",
		"--benchmark-ui",
		"--benchmark-output",
		"--session-metrics",
		"--session-metrics-output",
		"--ui-benchmark-script",
		"--preview-mode",
		"--tree-format",
		"--tree-filter",
		"--preview-search"
	};

	public static bool TryDetect(IReadOnlyList<string> args, out LegacyCliMigration migration)
	{
		migration = null!;
		if (!args.Any(LegacyActions.Contains))
			return false;

		var project = ReadValue(args, "--path") ?? FindLegacyPositionalProject(args) ?? ".";
		var output = ReadValue(args, "--output") ?? ReadValue(args, "-o");
		string replacement;
		if (TryReadValue(args, "--copy", out var copyMode))
		{
			var format = copyMode.Equals("zip", StringComparison.OrdinalIgnoreCase) ? "zip" : "folder";
			var destination = output ?? (format == "zip" ? "./project-export.zip" : "./project-export");
			replacement = $"devprojex export project {Quote(project)} --as {format} -o {Quote(destination)}";
		}
		else if (TryReadValue(args, "--export", out var exportMode))
		{
			var view = exportMode.ToLowerInvariant() switch
			{
				"tree" => "tree",
				"content" => "content",
				_ => "tree-content"
			};
			var format = ReadValue(args, "--format") ?? "text";
			var destination = output ?? "-";
			replacement = $"devprojex export context {Quote(project)} --view {view} --format {format} -o {Quote(destination)}";
		}
		else if (args.Contains("--benchmark-ui", StringComparer.OrdinalIgnoreCase))
		{
			replacement = $"devprojex dev benchmark ui {Quote(project)}";
		}
		else if (args.Contains("--benchmark", StringComparer.OrdinalIgnoreCase))
		{
			replacement = $"devprojex dev benchmark analysis {Quote(project)}";
		}
		else if (args.Contains("--session-metrics", StringComparer.OrdinalIgnoreCase))
		{
			replacement = $"devprojex dev session {Quote(project)}";
		}
		else
		{
			var reportPath = ReadValue(args, "--report") ?? "-";
			replacement = $"devprojex analyze {Quote(project)} --format json -o {Quote(reportPath)}";
		}

		replacement += BuildSelectionMigration(args);
		migration = new LegacyCliMigration(
			replacement,
			"The experimental flat CLI has been replaced by hierarchical commands.");
		return true;
	}

	private static string BuildSelectionMigration(IReadOnlyList<string> args)
	{
		var output = new StringBuilder();
		for (var index = 0; index < args.Count - 1; index++)
		{
			if (!args[index].Equals("--ignore", StringComparison.OrdinalIgnoreCase))
				continue;

			switch (args[index + 1].ToLowerInvariant())
			{
				case "git-ignore":
					output.Append(" --git-mode gitignore");
					break;
				case "git-tracked-only":
					output.Append(" --git-mode tracked");
					break;
				case "smart-ignore":
					output.Append(" --exclude smart-ignore");
					break;
			}
		}

		return output.ToString();
	}

	private static bool TryReadValue(
		IReadOnlyList<string> args,
		string option,
		out string value)
	{
		value = ReadValue(args, option) ?? string.Empty;
		return value.Length > 0;
	}

	private static string? ReadValue(IReadOnlyList<string> args, string option)
	{
		for (var index = 0; index < args.Count; index++)
		{
			var argument = args[index];
			if (argument.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase))
				return argument[(option.Length + 1)..];
			if (argument.Equals(option, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
				return args[index + 1];
		}

		return null;
	}

	private static string? FindLegacyPositionalProject(IReadOnlyList<string> args)
	{
		for (var index = 0; index < args.Count; index++)
		{
			var argument = args[index];
			if (argument.StartsWith('-'))
			{
				var optionName = argument.Split('=', 2)[0];
				if (!argument.Contains('=') &&
				    LegacyOptionsWithValues.Contains(optionName) &&
				    index + 1 < args.Count)
				{
					index++;
				}
				continue;
			}

			return argument;
		}

		return null;
	}

	private static string Quote(string value) =>
		value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
}
