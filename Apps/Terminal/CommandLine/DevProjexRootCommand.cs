using System.CommandLine;
using System.CommandLine.Parsing;

namespace DevProjex.Terminal.CommandLine;

public sealed class DevProjexRootCommand(string description) : RootCommand(description)
{
	private static readonly HashSet<string> ToggleOptions = new(StringComparer.Ordinal)
	{
		"--hide-secrets",
		"--hide-private-data",
		"--compress-code",
		"--strip-comments",
		"--strip-blank-lines"
	};

	public new ParseResult Parse(
		IReadOnlyList<string> arguments,
		ParserConfiguration? configuration = null) =>
		CommandLineParser.Parse(this, NormalizeToggleValues(arguments), configuration);

	private static IReadOnlyList<string> NormalizeToggleValues(IReadOnlyList<string> arguments)
	{
		string[]? normalized = null;
		for (var index = 0; index < arguments.Count; index++)
		{
			var token = arguments[index];
			if (token == "--")
				break;

			if (ToggleOptions.Contains(token) && index + 1 < arguments.Count &&
			    TryNormalize(arguments[index + 1], out var value))
			{
				normalized ??= arguments.ToArray();
				normalized[index + 1] = value;
				index++;
				continue;
			}

			var equalsIndex = token.IndexOf('=');
			if (equalsIndex <= 0 || !ToggleOptions.Contains(token[..equalsIndex]) ||
			    !TryNormalize(token[(equalsIndex + 1)..], out value))
			{
				continue;
			}

			normalized ??= arguments.ToArray();
			normalized[index] = $"{token[..(equalsIndex + 1)]}{value}";
		}

		return normalized ?? arguments;
	}

	private static bool TryNormalize(string value, out string normalized)
	{
		if (value.Equals("on", StringComparison.OrdinalIgnoreCase))
		{
			normalized = bool.TrueString;
			return true;
		}
		if (value.Equals("off", StringComparison.OrdinalIgnoreCase))
		{
			normalized = bool.FalseString;
			return true;
		}

		normalized = string.Empty;
		return false;
	}
}
