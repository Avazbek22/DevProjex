namespace DevProjex.Terminal.CommandLine;

internal static class CompletionCommandLineTransport
{
	private static readonly UTF8Encoding StrictUtf8 =
		new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

	public static string EncodeBase64(string value)
	{
		ArgumentNullException.ThrowIfNull(value);
		return Convert.ToBase64String(StrictUtf8.GetBytes(value));
	}

	public static bool TryDecodeBase64(string encoded, out string commandLine)
	{
		ArgumentNullException.ThrowIfNull(encoded);
		try
		{
			commandLine = StrictUtf8.GetString(Convert.FromBase64String(encoded));
			return true;
		}
		catch (FormatException)
		{
			commandLine = string.Empty;
			return false;
		}
		catch (DecoderFallbackException)
		{
			commandLine = string.Empty;
			return false;
		}
	}
}
