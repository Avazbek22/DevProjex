using DevProjex.Terminal.DesktopControl;

namespace DevProjex.Tests.Terminal;

public sealed class DesktopUiListTimeoutTests
{
	[Fact]
	public async Task RegistryScanTimeoutUsesDesktopErrorAndCancelsTheScan()
	{
		var environment = new TestTerminalEnvironment();
		var pendingScan = new TaskCompletionSource<IReadOnlyList<DesktopInstanceRegistration>>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var scanToken = CancellationToken.None;
		try
		{
			var exitCode = await CommandExecution.RunAsync(
				environment,
				new TerminalOutputOptions(),
				() => new DesktopCommandHandler(environment).ListAsync(
					json: false,
					new TerminalOutputOptions(),
					TimeSpan.FromMilliseconds(30),
					cancellationToken =>
					{
						scanToken = cancellationToken;
						return pendingScan.Task;
					},
					TestContext.Current.CancellationToken));

			Assert.Equal(CommandLineExitCodes.DesktopUnavailable, exitCode);
			Assert.Contains("DPX-DESKTOP-TIMEOUT", environment.StandardError, StringComparison.Ordinal);
			Assert.DoesNotContain("DPX-CLI-UNEXPECTED", environment.StandardError, StringComparison.Ordinal);
			Assert.Empty(environment.StandardOutput);
			Assert.True(scanToken.IsCancellationRequested);
		}
		finally
		{
			pendingScan.TrySetResult([]);
		}
	}

	[Fact]
	public async Task CallerCancellationRemainsACanceledCommand()
	{
		var environment = new TestTerminalEnvironment();
		var pendingScan = new TaskCompletionSource<IReadOnlyList<DesktopInstanceRegistration>>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);
		cancellation.CancelAfter(TimeSpan.FromMilliseconds(30));
		try
		{
			var exitCode = await CommandExecution.RunAsync(
				environment,
				new TerminalOutputOptions(),
				() => new DesktopCommandHandler(environment).ListAsync(
					json: false,
					new TerminalOutputOptions(),
					TimeSpan.FromSeconds(5),
					_ => pendingScan.Task,
					cancellation.Token));

			Assert.Equal(CommandLineExitCodes.Canceled, exitCode);
			Assert.Contains("DPX-CLI-CANCELED", environment.StandardError, StringComparison.Ordinal);
			Assert.DoesNotContain("DPX-DESKTOP-TIMEOUT", environment.StandardError, StringComparison.Ordinal);
		}
		finally
		{
			pendingScan.TrySetResult([]);
		}
	}
}
