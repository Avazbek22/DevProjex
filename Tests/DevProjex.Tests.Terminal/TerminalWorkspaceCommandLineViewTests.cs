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
			static (_, _) => TerminalWorkspaceCommandGhostCompletion.Empty,
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
			static (_, _) => TerminalWorkspaceCommandCompletion.Empty,
			static (_, _) => new TerminalWorkspaceCommandGhostCompletion("y", null),
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

	[Fact]
	public void GhostRenderingAvoidsFullCandidatesUntilTabCyclesCompletion()
	{
		var fullCompletionCalls = 0;
		var ghostCompletionCalls = 0;
		using var view = new TerminalWorkspaceCommandLineView(
			null!,
			(_, _) =>
			{
				fullCompletionCalls++;
				return new TerminalWorkspaceCommandCompletion(
					[new TerminalWorkspaceCommandCompletionCandidate("copy", "copy", 4)],
					"y",
					null);
			},
			(_, _) =>
			{
				ghostCompletionCalls++;
				return new TerminalWorkspaceCommandGhostCompletion("y", null);
			},
			static key => key,
			new TerminalCommandHistory(),
			plain: false,
			useUnicode: true)
		{
			Frame = new Rectangle(0, 0, 40, 1)
		};

		view.Open("cop");

		Assert.Equal(0, fullCompletionCalls);
		Assert.True(ghostCompletionCalls > 0);

		Invoke(view, "CycleCompletion");

		Assert.Equal(1, fullCompletionCalls);
		Assert.Equal("copy", view.InputText);
	}

	private static string GetResultText(TerminalWorkspaceCommandLineView view)
	{
		return GetField<TerminalLiteralLabel>(view, "_result").Text?.ToString() ?? string.Empty;
	}

	private static T GetField<T>(TerminalWorkspaceCommandLineView view, string name) where T : class =>
		Assert.IsType<T>(typeof(TerminalWorkspaceCommandLineView)
			.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?
			.GetValue(view));

	private static void Invoke(TerminalWorkspaceCommandLineView view, string methodName)
	{
		var method = typeof(TerminalWorkspaceCommandLineView).GetMethod(
			methodName,
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		method.Invoke(view, null);
	}
}
