using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalTestProcessTests
{
	[Fact]
	public void RunDrainsStandardOutputAndErrorConcurrently()
	{
		var result = TerminalTestProcess.Run(new ProcessStartInfo(
			PublishedApplicationLocator.FindProgressCheckpointHostExecutable(),
			"--pipe-flood")
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		});

		Assert.Equal(0, result.ExitCode);
		Assert.Equal("completed", result.StandardOutput);
		Assert.Equal(1024 * 1024, result.StandardError.Length);
	}
}
