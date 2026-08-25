namespace DevProjex.Terminal.CommandLine;

internal static class LocalizedParseError
{
	internal const string Prefix = "\u001fdevprojex-localized:";
	private const char CodeSeparator = '\u001e';

	public static string Create(string message) => Prefix + message;
	public static string Create(string code, string message) =>
		Prefix + code + CodeSeparator + message;

	public static string Resolve(string message, LocalizationService localization) =>
		TryResolve(message, out _, out var localized)
			? localized
			: localization["Terminal.Error.ParserRejected"];

	public static string? ResolveCode(string message) =>
		TryResolve(message, out var code, out _)
			? code
			: null;

	private static bool TryResolve(string message, out string? code, out string localized)
	{
		code = null;
		localized = string.Empty;
		if (!message.StartsWith(Prefix, StringComparison.Ordinal))
			return false;

		var payload = message.AsSpan(Prefix.Length);
		var separator = payload.IndexOf(CodeSeparator);
		if (separator > 0)
		{
			code = payload[..separator].ToString();
			localized = payload[(separator + 1)..].ToString();
		}
		else
		{
			localized = payload.ToString();
		}
		return true;
	}
}
