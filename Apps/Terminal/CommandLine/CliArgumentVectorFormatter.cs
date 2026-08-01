using System.Text.Encodings.Web;
using System.Text.Json;

namespace DevProjex.Terminal.CommandLine;

internal static class CliArgumentVectorFormatter
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	public static string Format(IReadOnlyList<string> arguments)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		var representation = new StringBuilder();
		for (var index = 0; index < arguments.Count; index++)
		{
			if (index > 0)
				representation.AppendLine();
			representation
				.Append("argv[")
				.Append(index)
				.Append("] = ")
				.Append(JsonSerializer.Serialize(arguments[index], JsonOptions));
		}

		return representation.ToString();
	}
}
