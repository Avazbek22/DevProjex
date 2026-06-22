using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class TerminalCommandPromptDismissalTests
{
	[Theory]
	[InlineData((int)TerminalCommandDialogAction.None, false, false)]
	[InlineData((int)TerminalCommandDialogAction.None, true, true)]
	[InlineData((int)TerminalCommandDialogAction.DismissPrompt, false, true)]
	[InlineData((int)TerminalCommandDialogAction.DismissPrompt, true, true)]
	[InlineData((int)TerminalCommandDialogAction.InstallOrRepair, false, false)]
	[InlineData((int)TerminalCommandDialogAction.InstallOrRepair, true, false)]
	public void ShouldPersistTerminalCommandPromptDismissal_OnlyPersistsRealDismissals(
		int actionValue,
		bool dontShowAgain,
		bool expected)
	{
		var action = (TerminalCommandDialogAction)actionValue;
		var result = new TerminalCommandDialogResult(action, dontShowAgain);

		Assert.Equal(expected, MainWindow.ShouldPersistTerminalCommandPromptDismissal(result));
	}
}
