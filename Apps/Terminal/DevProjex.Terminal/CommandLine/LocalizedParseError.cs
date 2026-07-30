namespace DevProjex.Terminal.CommandLine;

internal static class LocalizedParseError
{
	internal const string Prefix = "\u001fdevprojex-localized:";

	public static string Create(string message) => Prefix + message;

	public static string Resolve(string message, LocalizationService localization) =>
		message.StartsWith(Prefix, StringComparison.Ordinal)
			? message[Prefix.Length..]
			: localization["Terminal.Error.ParserRejected"];
}
