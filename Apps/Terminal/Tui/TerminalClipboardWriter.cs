using Terminal.Gui.App;

namespace DevProjex.Terminal.Tui;

internal enum TerminalClipboardWriteStatus
{
	Native,
	Osc52,
	Unavailable,
	PayloadTooLarge
}

internal readonly record struct TerminalClipboardWriteResult(TerminalClipboardWriteStatus Status)
{
	public bool IsSuccess => Status is TerminalClipboardWriteStatus.Native or TerminalClipboardWriteStatus.Osc52;
}

internal sealed class TerminalClipboardWriter(
	Func<IClipboard?> resolveClipboard,
	Func<string, bool> writeRaw,
	Func<bool> canUseOsc52)
{
	internal const int MaximumOsc52SequenceLength = 100_000;

	public TerminalClipboardWriteResult Write(string payload)
	{
		ArgumentNullException.ThrowIfNull(payload);
		try
		{
			var clipboard = resolveClipboard();
			if (clipboard?.IsSupported == true && clipboard.TrySetClipboardData(payload))
				return new TerminalClipboardWriteResult(TerminalClipboardWriteStatus.Native);
		}
		catch
		{
			// OSC 52 remains available when a platform clipboard provider fails at runtime.
		}

		if (!canUseOsc52())
			return new TerminalClipboardWriteResult(TerminalClipboardWriteStatus.Unavailable);

		var sequence = EncodeOsc52(payload);
		if (sequence.Length > MaximumOsc52SequenceLength)
			return new TerminalClipboardWriteResult(TerminalClipboardWriteStatus.PayloadTooLarge);

		try
		{
			return writeRaw(sequence)
				? new TerminalClipboardWriteResult(TerminalClipboardWriteStatus.Osc52)
				: new TerminalClipboardWriteResult(TerminalClipboardWriteStatus.Unavailable);
		}
		catch
		{
			return new TerminalClipboardWriteResult(TerminalClipboardWriteStatus.Unavailable);
		}
	}

	internal static string EncodeOsc52(string payload) =>
		$"\u001b]52;c;{Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))}\a";
}
