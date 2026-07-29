using System.ComponentModel;

namespace DevProjex.Terminal.CommandLine;

internal static class TerminalBrokenPipeDetector
{
	private const int UnixBrokenPipe = 32;
	private const int WindowsBrokenPipe = 109;
	private const int WindowsNoData = 232;

	public static bool IsBrokenPipe(IOException exception) =>
		IsBrokenPipe(exception, OperatingSystem.IsWindows());

	internal static bool IsBrokenPipe(
		IOException exception,
		bool isWindows)
	{
		ArgumentNullException.ThrowIfNull(exception);
		for (Exception? current = exception; current is not null; current = current.InnerException)
		{
			var nativeCode = current switch
			{
				Win32Exception win32Exception => win32Exception.NativeErrorCode,
				IOException ioException => ioException.HResult & 0xFFFF,
				_ => -1
			};
			if (isWindows
				    ? nativeCode is WindowsBrokenPipe or WindowsNoData
				    : nativeCode == UnixBrokenPipe)
			{
				return true;
			}
		}

		return false;
	}
}

internal sealed class TerminalOutputWriter(
	TextWriter inner,
	bool? isWindows = null) : TextWriter
{
	private readonly bool _isWindows = isWindows ?? OperatingSystem.IsWindows();

	public override Encoding Encoding => inner.Encoding;
	public override IFormatProvider FormatProvider => inner.FormatProvider;

	public override void Flush() =>
		Execute(inner.Flush);

	public override Task FlushAsync() =>
		ExecuteAsync(inner.FlushAsync);

	public override Task FlushAsync(CancellationToken cancellationToken) =>
		ExecuteAsync(() => inner.FlushAsync(cancellationToken));

	public override void Write(char value) =>
		Execute(() => inner.Write(value));

	public override void Write(char[] buffer, int index, int count) =>
		Execute(() => inner.Write(buffer, index, count));

	public override void Write(string? value) =>
		Execute(() => inner.Write(value));

	public override void Write(ReadOnlySpan<char> buffer)
	{
		try
		{
			inner.Write(buffer);
		}
		catch (IOException exception) when (IsBrokenPipe(exception))
		{
			throw new TerminalBrokenPipeException(exception);
		}
	}

	public override Task WriteAsync(char value) =>
		ExecuteAsync(() => inner.WriteAsync(value));

	public override Task WriteAsync(string? value) =>
		ExecuteAsync(() => inner.WriteAsync(value));

	public override Task WriteAsync(
		char[] buffer,
		int index,
		int count) =>
		ExecuteAsync(() => inner.WriteAsync(buffer, index, count));

	public override Task WriteAsync(
		ReadOnlyMemory<char> buffer,
		CancellationToken cancellationToken = default) =>
		ExecuteAsync(() => inner.WriteAsync(buffer, cancellationToken));

	private void Execute(Action action)
	{
		try
		{
			action();
		}
		catch (IOException exception) when (IsBrokenPipe(exception))
		{
			throw new TerminalBrokenPipeException(exception);
		}
	}

	private async Task ExecuteAsync(Func<Task> action)
	{
		try
		{
			await action().ConfigureAwait(false);
		}
		catch (IOException exception) when (IsBrokenPipe(exception))
		{
			throw new TerminalBrokenPipeException(exception);
		}
	}

	private bool IsBrokenPipe(IOException exception) =>
		TerminalBrokenPipeDetector.IsBrokenPipe(exception, _isWindows);
}
