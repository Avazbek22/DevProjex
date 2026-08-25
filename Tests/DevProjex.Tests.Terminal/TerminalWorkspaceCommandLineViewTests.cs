using System.Reflection;
using System.Drawing;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalWorkspaceCommandLineViewTests
{
	[Fact]
	public void ResultRecoversFullTextAfterTerminalExpands()
	{
		using var view = new TerminalWorkspaceCommandLineView(
			null!,
			static (_, _) => TerminalWorkspaceCommandCompletion.Empty,
			static key => key,
			new TerminalCommandHistory(),
			plain: false,
			useUnicode: true)
		{
			Frame = new Rectangle(0, 0, 12, 1)
		};
		const string message = "Characters: 1200 · Approximate tokens: 300";

		view.ShowResult(message, success: true);
		Assert.DoesNotContain("tokens", GetResultText(view), StringComparison.Ordinal);

		view.Frame = new Rectangle(0, 0, 80, 1);
		view.RefreshLayout();

		Assert.Contains(message, GetResultText(view), StringComparison.Ordinal);
	}

	private static string GetResultText(TerminalWorkspaceCommandLineView view)
	{
		var field = typeof(TerminalWorkspaceCommandLineView).GetField(
			"_result",
			BindingFlags.Instance | BindingFlags.NonPublic);
		return Assert.IsType<TerminalLiteralLabel>(field?.GetValue(view)).Text?.ToString() ?? string.Empty;
	}
}
