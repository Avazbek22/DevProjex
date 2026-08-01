using System.CommandLine;
using System.Runtime.CompilerServices;

namespace DevProjex.Terminal.CommandLine;

internal static class CliHelpMetadataRegistry
{
	private static readonly ConditionalWeakTable<Option, DefaultDisplay> Defaults = new();

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

	private sealed record DefaultDisplay(string? Value);
}
