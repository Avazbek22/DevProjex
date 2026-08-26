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

	[Fact]
	public void GhostUpdatesSynchronouslyWhenCursorMoves()
	{
		using var view = new TerminalWorkspaceCommandLineView(
			null!,
			static (_, _) => new TerminalWorkspaceCommandCompletion([], "y", null),
			static key => key,
			new TerminalCommandHistory(),
			plain: false,
			useUnicode: true)
		{
			Frame = new Rectangle(0, 0, 40, 1)
		};

		view.Open("cop");
		var input = GetField<TerminalTransparentTextEditor>(view, "_input");
		var ghost = GetField<TerminalLiteralLabel>(view, "_ghost");
		Assert.True(ghost.Visible);

		input.InsertionPoint = 0;

		Assert.False(ghost.Visible);
	}

	private static string GetResultText(TerminalWorkspaceCommandLineView view)
	{
		return GetField<TerminalLiteralLabel>(view, "_result").Text?.ToString() ?? string.Empty;
	}

	private static T GetField<T>(TerminalWorkspaceCommandLineView view, string name) where T : class =>
		Assert.IsType<T>(typeof(TerminalWorkspaceCommandLineView)
			.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?
			.GetValue(view));
}
