using DevProjex.Application.Compression;

namespace DevProjex.Terminal.CommandLine;

public enum DetailForOptionFailure
{
	TooManyValues,
	InvalidFormat,
	InvalidLevel,
	InvalidPattern
}

/// <summary>
/// A rejected <c>--detail-for</c> value. The reason is carried so the caller can present it with its
/// own wording and exit code; <see cref="Failure"/> and <see cref="Value"/> let it localize the
/// reason instead of repeating the English message.
/// </summary>
public sealed class DetailForOptionException(
	string reason,
	DetailForOptionFailure failure,
	string value) : Exception(reason)
{
	public DetailForOptionFailure Failure { get; } = failure;

	public string Value { get; } = value;
}

/// <summary>
/// Parses repeatable <c>--detail-for "&lt;glob&gt;=&lt;full|compact|signatures&gt;"</c> values into the
/// same ordered override list the MCP parameter produces. Entries apply in order and the last match
/// wins.
/// </summary>
public static class DetailForOption
{
	public const int MaximumEntries = 16;

	public static IReadOnlyList<ContentDetailOverride>? Parse(IReadOnlyList<string>? values)
	{
		if (values is null || values.Count == 0)
			return null;
		if (values.Count > MaximumEntries)
		{
			throw new DetailForOptionException(
				$"at most {MaximumEntries} values are allowed",
				DetailForOptionFailure.TooManyValues,
				MaximumEntries.ToString(System.Globalization.CultureInfo.InvariantCulture));
		}

		var overrides = new List<ContentDetailOverride>(values.Count);
		foreach (var value in values)
		{
			// Split on the LAST separator: a glob may legitimately contain one, while a level never
			// does, so the tail is unambiguous and the head keeps whatever the caller wrote.
			var separator = value.LastIndexOf('=');
			if (separator <= 0 || separator == value.Length - 1)
			{
				throw new DetailForOptionException(
					$"'{value}' is not '<glob>=<full|compact|signatures>'",
					DetailForOptionFailure.InvalidFormat,
					value);
			}

			var pattern = value[..separator];
			var kinds = value[(separator + 1)..] switch
			{
				"full" => CodeTransformKinds.None,
				"compact" => CodeTransformKinds.Comments | CodeTransformKinds.BlankLines,
				"signatures" => CodeTransformKinds.Bodies |
				                CodeTransformKinds.Comments |
				                CodeTransformKinds.BlankLines,
				var level => throw new DetailForOptionException(
					$"'{level}' is not a detail level; valid values are full, compact, signatures",
					DetailForOptionFailure.InvalidLevel,
					level)
			};
			try
			{
				overrides.Add(new ContentDetailOverride(ContentDetailPatternSet.Create([pattern]), kinds));
			}
			catch (ProjectRelativeGlobException failure)
			{
				throw new DetailForOptionException(
					$"'{pattern}': {failure.Reason}",
					DetailForOptionFailure.InvalidPattern,
					pattern);
			}
		}
		return overrides;
	}
}
