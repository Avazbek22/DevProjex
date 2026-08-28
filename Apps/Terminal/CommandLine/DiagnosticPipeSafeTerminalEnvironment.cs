namespace DevProjex.Terminal.CommandLine;

internal sealed class DiagnosticPipeSafeTerminalEnvironment(
	ITerminalEnvironment inner) : ITerminalEnvironment
{
	private readonly TextWriter _error = new BrokenPipeSafeTextWriter(inner.Error);

	public TextReader Input => inner.Input;
	public Stream? RawInput => inner.RawInput;
	public TextWriter Output => inner.Output;
	public TextWriter Error => _error;
	public bool IsInputInteractive => inner.IsInputInteractive;
	public bool IsOutputInteractive => inner.IsOutputInteractive;
	public bool IsErrorInteractive => inner.IsErrorInteractive;
	public bool HasAttachedConsole => inner.HasAttachedConsole;
	public bool IsTerminalHost => inner.IsTerminalHost;
	public bool IsCi => inner.IsCi;
	public bool IsTermDumb => inner.IsTermDumb;
	public bool IsNoColor => inner.IsNoColor;
	public bool SupportsUnicode => inner.SupportsUnicode;
	public int Width => inner.Width;
	public int Height => inner.Height;
	public IReadOnlyDictionary<string, string?> Variables => inner.Variables;

	private sealed class BrokenPipeSafeTextWriter(TextWriter innerWriter) : TextWriter
	{
		public override Encoding Encoding => innerWriter.Encoding;
		public override IFormatProvider FormatProvider => innerWriter.FormatProvider;

		public override void Flush() => Execute(innerWriter.Flush);
		public override Task FlushAsync() => ExecuteAsync(innerWriter.FlushAsync);
		public override Task FlushAsync(CancellationToken cancellationToken) =>
			ExecuteAsync(() => innerWriter.FlushAsync(cancellationToken));
		public override void Write(char value) => Execute(() => innerWriter.Write(value));
		public override void Write(char[] buffer, int index, int count) =>
			Execute(() => innerWriter.Write(buffer, index, count));
		public override void Write(string? value) => Execute(() => innerWriter.Write(value));
		public override void Write(ReadOnlySpan<char> buffer)
		{
			try
			{
				innerWriter.Write(buffer);
			}
			catch (TerminalBrokenPipeException)
			{
			}
		}

		public override Task WriteAsync(char value) =>
			ExecuteAsync(() => innerWriter.WriteAsync(value));
		public override Task WriteAsync(string? value) =>
			ExecuteAsync(() => innerWriter.WriteAsync(value));
		public override Task WriteAsync(char[] buffer, int index, int count) =>
			ExecuteAsync(() => innerWriter.WriteAsync(buffer, index, count));
		public override Task WriteAsync(
			ReadOnlyMemory<char> buffer,
			CancellationToken cancellationToken = default) =>
			ExecuteAsync(() => innerWriter.WriteAsync(buffer, cancellationToken));

		private static void Execute(Action action)
		{
			try
			{
				action();
			}
			catch (TerminalBrokenPipeException)
			{
			}
		}

		private static async Task ExecuteAsync(Func<Task> action)
		{
			try
			{
				await action().ConfigureAwait(false);
			}
			catch (TerminalBrokenPipeException)
			{
			}
		}
	}
}
