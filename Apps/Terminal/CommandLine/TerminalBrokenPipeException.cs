namespace DevProjex.Terminal.CommandLine;

public sealed class TerminalBrokenPipeException(
	Exception? innerException = null)
	: IOException("The output consumer closed the pipe.", innerException);
