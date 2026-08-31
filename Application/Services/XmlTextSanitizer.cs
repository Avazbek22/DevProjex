using System.Xml;

namespace DevProjex.Application.Services;

internal static class XmlTextSanitizer
{
	public static string Sanitize(string value) =>
		TrySanitize(value.AsSpan(), out var sanitized)
			? sanitized
			: value;

	public static bool TrySanitize(ReadOnlySpan<char> value, out string sanitized)
	{
		StringBuilder? builder = null;
		for (var index = 0; index < value.Length; index++)
		{
			var character = value[index];
			if (char.IsHighSurrogate(character) &&
			    index + 1 < value.Length &&
			    char.IsLowSurrogate(value[index + 1]))
			{
				if (builder is not null)
				{
					builder.Append(character);
					builder.Append(value[++index]);
				}
				else
				{
					index++;
				}
				continue;
			}

			if (XmlConvert.IsXmlChar(character))
			{
				builder?.Append(character);
				continue;
			}

			builder ??= new StringBuilder(value.Length)
				.Append(value[..index]);
			builder.Append('\uFFFD');
		}

		sanitized = builder?.ToString() ?? string.Empty;
		return builder is not null;
	}
}
