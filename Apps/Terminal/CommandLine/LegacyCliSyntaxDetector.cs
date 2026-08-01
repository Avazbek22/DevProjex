namespace DevProjex.Terminal.CommandLine;

public sealed record LegacyCliMigration(
	IReadOnlyList<string> ReplacementArguments);

public static class LegacyCliSyntaxDetector
{
	private const string ReportAction = "--report";
	private const string ExportAction = "--export";
	private const string CopyAction = "--copy";

	public static bool TryDetect(
		IReadOnlyList<string> arguments,
		out LegacyCliMigration migration)
	{
		migration = null!;
		if (arguments.Contains("--", StringComparer.Ordinal) ||
		    !TryParse(arguments, out var invocation) ||
		    !TryBuildReplacement(invocation, out var replacement))
		{
			return false;
		}

		migration = new LegacyCliMigration(replacement);
		return true;
	}

	private static bool TryParse(
		IReadOnlyList<string> arguments,
		out LegacyInvocation invocation)
	{
		invocation = new LegacyInvocation();
		for (var index = 0; index < arguments.Count; index++)
		{
			var token = arguments[index];
			if (!token.StartsWith("-", StringComparison.Ordinal))
			{
				if (invocation.PositionalProject is not null)
					return false;
				invocation.PositionalProject = token;
				continue;
			}

			var equalsIndex = token.IndexOf('=');
			var name = equalsIndex >= 0 ? token[..equalsIndex] : token;
			if (!RequiresValue(name))
				return false;

			string value;
			if (equalsIndex >= 0)
			{
				value = token[(equalsIndex + 1)..];
			}
			else
			{
				if (++index >= arguments.Count)
					return false;
				value = arguments[index];
			}

			if (value.Length == 0 || !invocation.TryAdd(name, value))
				return false;
		}

		return invocation.Action is not null &&
		       !(invocation.ProjectOption is not null &&
		         invocation.PositionalProject is not null);
	}

	private static bool TryBuildReplacement(
		LegacyInvocation invocation,
		out IReadOnlyList<string> replacement)
	{
		replacement = [];
		var project = invocation.ProjectOption ??
		              invocation.PositionalProject ??
		              ".";
		if (project.Length == 0 ||
		    !TryMapIgnores(invocation.Ignores, out var selectionArguments) ||
		    !TryNormalizeLanguage(invocation.Language, out var language))
		{
			return false;
		}

		List<string> arguments;
		switch (invocation.Action!.Value.Name.ToLowerInvariant())
		{
			case ReportAction:
				if (invocation.Output is not null ||
				    invocation.Format is not null)
				{
					return false;
				}
				arguments =
				[
					"devprojex",
					"analyze",
					project,
					"--format",
					"json",
					"-o",
					invocation.Action.Value.Value
				];
				break;
			case ExportAction:
				if (!CliChoiceSets.ContextView.TryParse(
					    invocation.Action.Value.Value,
					    out var view) ||
				    !TryNormalizeContextFormat(invocation.Format, out var contextFormat))
				{
					return false;
				}
				arguments =
				[
					"devprojex",
					"export",
					"context",
					project,
					"--view",
					CliChoiceSets.ContextView.ToToken(view),
					"--format",
					contextFormat,
					"-o",
					invocation.Output ?? "-"
				];
				break;
			case CopyAction:
				if (invocation.Format is not null ||
				    invocation.Output is null ||
				    !CliChoiceSets.ProjectExportFormat.TryParse(
					    invocation.Action.Value.Value,
					    out var outputKind))
				{
					return false;
				}
				arguments =
				[
					"devprojex",
					"export",
					"project",
					project,
					"--as",
					CliChoiceSets.ProjectExportFormat.ToToken(outputKind),
					"-o",
					invocation.Output
				];
				break;
			default:
				return false;
		}

		arguments.AddRange(selectionArguments);
		if (language is not null)
		{
			arguments.Add("--language");
			arguments.Add(language);
		}
		replacement = arguments;
		return true;
	}

	private static bool TryNormalizeContextFormat(
		string? value,
		out string format)
	{
		if (value is null)
		{
			format = "text";
			return true;
		}

		if (CliChoiceSets.ContextDocumentFormat.TryParse(value, out var parsed))
		{
			format = CliChoiceSets.ContextDocumentFormat.ToToken(parsed);
			return true;
		}

		format = string.Empty;
		return false;
	}

	private static bool TryNormalizeLanguage(
		string? value,
		out string? language)
	{
		if (value is null)
		{
			language = null;
			return true;
		}

		if (CliChoiceSets.Language.TryParse(value, out var parsed))
		{
			language = CliChoiceSets.Language.ToToken(parsed);
			return true;
		}

		language = null;
		return false;
	}

	private static bool TryMapIgnores(
		IReadOnlyList<string> values,
		out IReadOnlyList<string> arguments)
	{
		var mapped = new List<string>(values.Count * 2);
		foreach (var value in values)
		{
			switch (value.ToLowerInvariant())
			{
				case "git-ignore":
					mapped.Add("--git-mode");
					mapped.Add("gitignore");
					break;
				case "git-tracked-only":
					mapped.Add("--git-mode");
					mapped.Add("tracked");
					break;
				case "smart-ignore":
					mapped.Add("--exclude");
					mapped.Add("smart-ignore");
					break;
				default:
					arguments = [];
					return false;
			}
		}

		arguments = mapped;
		return true;
	}

	private static bool RequiresValue(string name) =>
		name.Equals(ReportAction, StringComparison.OrdinalIgnoreCase) ||
		name.Equals(ExportAction, StringComparison.OrdinalIgnoreCase) ||
		name.Equals(CopyAction, StringComparison.OrdinalIgnoreCase) ||
		name.Equals("--path", StringComparison.OrdinalIgnoreCase) ||
		name.Equals("--output", StringComparison.OrdinalIgnoreCase) ||
		name.Equals("-o", StringComparison.OrdinalIgnoreCase) ||
		name.Equals("--format", StringComparison.OrdinalIgnoreCase) ||
		name.Equals("--ignore", StringComparison.OrdinalIgnoreCase) ||
		name.Equals("--language", StringComparison.OrdinalIgnoreCase);

	private sealed class LegacyInvocation
	{
		public (string Name, string Value)? Action { get; private set; }
		public string? ProjectOption { get; private set; }
		public string? PositionalProject { get; set; }
		public string? Output { get; private set; }
		public string? Format { get; private set; }
		public string? Language { get; private set; }
		public List<string> Ignores { get; } = [];

		public bool TryAdd(string name, string value)
		{
			if (IsAction(name))
			{
				if (Action is not null)
					return false;
				Action = (name, value);
				return true;
			}
			if (name.Equals("--path", StringComparison.OrdinalIgnoreCase))
			{
				if (ProjectOption is not null)
					return false;
				ProjectOption = value;
				return true;
			}
			if (name.Equals("--output", StringComparison.OrdinalIgnoreCase) ||
			    name.Equals("-o", StringComparison.OrdinalIgnoreCase))
			{
				if (Output is not null)
					return false;
				Output = value;
				return true;
			}
			if (name.Equals("--format", StringComparison.OrdinalIgnoreCase))
			{
				if (Format is not null)
					return false;
				Format = value;
				return true;
			}
			if (name.Equals("--language", StringComparison.OrdinalIgnoreCase))
			{
				if (Language is not null)
					return false;
				Language = value;
				return true;
			}
			if (name.Equals("--ignore", StringComparison.OrdinalIgnoreCase))
			{
				Ignores.Add(value);
				return true;
			}

			return false;
		}

		private static bool IsAction(string name) =>
			name.Equals(ReportAction, StringComparison.OrdinalIgnoreCase) ||
			name.Equals(ExportAction, StringComparison.OrdinalIgnoreCase) ||
			name.Equals(CopyAction, StringComparison.OrdinalIgnoreCase);

	}
}
