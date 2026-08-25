using System.Text.Json;

namespace DevProjex.Terminal.DesktopControl;

internal static class DesktopProtocolMessageReader
{
	private static readonly UTF8Encoding StrictUtf8 = new(false, true);

	public static async Task<string> ReadAsync(
		Stream stream,
		Func<DesktopControlException> oversizedMessageFactory,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(stream);
		ArgumentNullException.ThrowIfNull(oversizedMessageFactory);
		var buffer = new byte[4096];
		using var message = new MemoryStream();
		while (true)
		{
			var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
			if (read == 0)
				throw new EndOfStreamException();

			var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
			var count = newline >= 0 ? newline : read;
			if (message.Length + count > DesktopProtocol.MaximumMessageBytes)
				throw oversizedMessageFactory();

			message.Write(buffer, 0, count);
			if (newline < 0)
				continue;

			try
			{
				return StrictUtf8.GetString(
					message.GetBuffer(),
					0,
					checked((int)message.Length));
			}
			catch (DecoderFallbackException exception)
			{
				throw new JsonException(
					"The desktop protocol message is not valid UTF-8.",
					exception);
			}
		}
	}
}
