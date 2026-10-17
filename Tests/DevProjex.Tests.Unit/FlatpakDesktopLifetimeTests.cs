using System.Diagnostics;
using DevProjex.Terminal.Execution;

namespace DevProjex.Tests.Unit;

public sealed class FlatpakDesktopLifetimeTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task OnlyTheSandboxCliWaitsForItsOwnDesktopChild(bool sandbox)
	{
		var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "sh")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		foreach (var argument in OperatingSystem.IsWindows()
			? new[] { "/d", "/c", "echo ready&set /p token=" }
			: new[] { "-c", "printf 'ready\\n'; read token" })
			start.ArgumentList.Add(argument);
		using var process = Process.Start(start)!;
		try
		{
			Assert.Equal("ready", await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken));
			var waiting = DesktopCommandHandler.WaitForSandboxDesktopAsync(process.Id, sandbox, TestContext.Current.CancellationToken);
			Assert.Equal(!sandbox, waiting.IsCompleted);
			await process.StandardInput.WriteLineAsync("done".AsMemory(), TestContext.Current.CancellationToken);
			process.StandardInput.Close();
			await waiting.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
			await process.WaitForExitAsync(TestContext.Current.CancellationToken);
		}
		finally
		{
			if (!process.HasExited) process.Kill(entireProcessTree: true);
		}
	}
}
