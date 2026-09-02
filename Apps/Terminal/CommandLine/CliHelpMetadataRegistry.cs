using System.CommandLine;
using System.Runtime.CompilerServices;

namespace DevProjex.Terminal.CommandLine;

internal static class CliHelpMetadataRegistry
{
	private static readonly ConditionalWeakTable<Option, DefaultDisplay> Defaults = new();
	private static readonly ConditionalWeakTable<Option, RequiredDisplay> Required = new();

	public static void SuppressParserDefault(Option option)
	{
		ArgumentNullException.ThrowIfNull(option);
		Defaults.AddOrUpdate(option, new DefaultDisplay(null));
	}

	public static void SetDefaultDisplay(Option option, string value)
	{
		ArgumentNullException.ThrowIfNull(option);
		ArgumentException.ThrowIfNullOrWhiteSpace(value);
		Defaults.AddOrUpdate(option, new DefaultDisplay(value));
	}

	public static bool TryGetDefaultDisplay(
		Option option,
		out string? value)
	{
		if (Defaults.TryGetValue(option, out var display))
		{
			value = display.Value;
			return true;
		}

		value = null;
		return false;
	}

	public static void MarkRequired(Option option)
	{
		ArgumentNullException.ThrowIfNull(option);
		Required.AddOrUpdate(option, new RequiredDisplay());
	}

	public static bool IsRequired(Option option) => Required.TryGetValue(option, out _);

	private sealed record DefaultDisplay(string? Value);
	private sealed record RequiredDisplay;
}
