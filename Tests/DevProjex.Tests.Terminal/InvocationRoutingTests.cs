namespace DevProjex.Tests.Terminal;

public sealed class InvocationRoutingTests
{
	[Fact]
	public void DesktopLaunchWithoutConsoleUsesDesktop()
	{
		var environment = new TestTerminalEnvironment();

		var result = ProcessInvocationRouter.Resolve([], environment, hasPendingDesktopRequest: false);

		Assert.Equal(ProcessInvocationMode.Desktop, result);
	}

	[Fact]
	public void InteractiveConsoleWithoutArgumentsUsesTerminal()
	{
		var environment = new TestTerminalEnvironment
		{
			HasAttachedConsole = true,
			IsInputInteractive = true,
			IsOutputInteractive = true
		};

		var result = ProcessInvocationRouter.Resolve([], environment, hasPendingDesktopRequest: false);

		Assert.Equal(ProcessInvocationMode.Terminal, result);
	}

	[Fact]
	public void RedirectedAttachedConsoleWithoutArgumentsUsesTerminalHelpPath()
	{
		var environment = new TestTerminalEnvironment { HasAttachedConsole = true };

		var result = ProcessInvocationRouter.Resolve([], environment, hasPendingDesktopRequest: false);

		Assert.Equal(ProcessInvocationMode.Terminal, result);
	}

	[Fact]
	public void TerminalLauncherMarkerUsesTerminalWithoutInteractiveStreams()
	{
		var environment = new TestTerminalEnvironment { IsTerminalHost = true };

		var result = ProcessInvocationRouter.Resolve([], environment, hasPendingDesktopRequest: false);

		Assert.Equal(ProcessInvocationMode.Terminal, result);
	}

	[Fact]
	public void CiWithoutArgumentsUsesTerminalHelpEvenWithoutAnAttachedConsole()
	{
		var environment = new TestTerminalEnvironment { IsCi = true };

		var result = ProcessInvocationRouter.Resolve([], environment, hasPendingDesktopRequest: false);

		Assert.Equal(ProcessInvocationMode.Terminal, result);
	}

	[Fact]
	public void PendingDesktopRequestAlwaysWins()
	{
		var environment = new TestTerminalEnvironment
		{
			HasAttachedConsole = true,
			IsTerminalHost = true,
			IsInputInteractive = true,
			IsOutputInteractive = true
		};

		var result = ProcessInvocationRouter.Resolve(["analyze"], environment, hasPendingDesktopRequest: true);

		Assert.Equal(ProcessInvocationMode.Desktop, result);
	}

	[Fact]
	public async Task RedirectedRootInvocationPrintsHelpAndDoesNotStartTui()
	{
		var environment = new TestTerminalEnvironment { HasAttachedConsole = true };

		var exitCode = await new TerminalApplication(environment)
			.RunAsync([], TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Contains("devprojex analyze", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(environment.StandardError);
	}

	[Theory]
	[InlineData(false, true, false)]
	[InlineData(true, false, false)]
	[InlineData(true, true, true)]
	public async Task ExplicitTuiRequiresBothInteractiveStreams(
		bool inputInteractive,
		bool outputInteractive,
		bool succeedsRoutingGate)
	{
		var environment = new TestTerminalEnvironment
		{
			HasAttachedConsole = true,
			IsInputInteractive = inputInteractive,
			IsOutputInteractive = outputInteractive,
			Width = succeedsRoutingGate ? 10 : 120,
			Height = succeedsRoutingGate ? 10 : 30
		};

		var exitCode = await new TerminalApplication(environment)
			.RunAsync(["tui", "."], TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Contains(
			succeedsRoutingGate ? "DPX-TUI-TERMINAL-TOO-SMALL" : "DPX-TUI-NOT-INTERACTIVE",
			environment.StandardError,
			StringComparison.Ordinal);
	}
}
