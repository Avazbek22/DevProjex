using System.Reflection;
using System.Drawing;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalWorkspaceCommandLineViewTests
{
	[Fact]
	public void CompletionProviderSupportsAsynchronousCancellation()
	{
		var asynchronousProvider = typeof(Func<
			string,
			int,
			CancellationToken,
			ValueTask<TerminalWorkspaceCommandCompletion>>);

		Assert.Contains(
			typeof(TerminalWorkspaceCommandLineView).GetConstructors(),
			constructor => constructor.GetParameters().Length > 1 &&
				constructor.GetParameters()[1].ParameterType == asynchronousProvider);
	}

	[Fact]
	public async Task StaleAsynchronousCompletionCannotReplaceTheLatestGhost()
	{
		var firstStarted = new TaskCompletionSource<bool>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseFirst = new TaskCompletionSource<bool>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		CancellationToken firstToken = default;
		async ValueTask<TerminalWorkspaceCommandCompletion> CompleteAsync(
			string text,
			int cursor,
			CancellationToken cancellationToken)
		{
			if (text == "a")
			{
				firstToken = cancellationToken;
				firstStarted.TrySetResult(true);
				await releaseFirst.Task.ConfigureAwait(false);
				return Completion("a-old", "-old");
			}
			return Completion(text + "-new", "-new");
		}

		using var view = new TerminalWorkspaceCommandLineView(
			null!,
			CompleteAsync,
			static (_, _) => TerminalWorkspaceCommandGhostCompletion.Empty,
			static key => key,
			new TerminalCommandHistory(),
			plain: false,
			useUnicode: true)
		{
			Frame = new Rectangle(0, 0, 40, 1)
		};

		view.Open("a");
		await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
		var firstCompletionTask = Assert.IsAssignableFrom<Task>(view.ActiveCompletionTask);
		var input = GetField<TerminalTransparentTextEditor>(view, "_input");
		input.Value = "ab";
		input.MoveEnd();
		var latestCompletionTask = Assert.IsAssignableFrom<Task>(view.ActiveCompletionTask);
		Assert.NotSame(firstCompletionTask, latestCompletionTask);
		await latestCompletionTask.WaitAsync(TestContext.Current.CancellationToken);
		var ghost = GetField<TerminalLiteralLabel>(view, "_ghost");
		Assert.Contains("-new", ghost.Text?.ToString() ?? string.Empty, StringComparison.Ordinal);

		releaseFirst.TrySetResult(true);
		await firstCompletionTask.WaitAsync(TestContext.Current.CancellationToken);

		Assert.True(firstToken.IsCancellationRequested);
		Assert.Contains("-new", ghost.Text?.ToString() ?? string.Empty, StringComparison.Ordinal);
		Assert.DoesNotContain("-old", ghost.Text?.ToString() ?? string.Empty, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AsynchronousCompletionCacheIsBounded()
	{
		using var view = new TerminalWorkspaceCommandLineView(
			null!,
			static (text, _, _) => ValueTask.FromResult(Completion(text, "-cached")),
			static (_, _) => TerminalWorkspaceCommandGhostCompletion.Empty,
			static key => key,
			new TerminalCommandHistory(),
			plain: false,
			useUnicode: true)
		{
			Frame = new Rectangle(0, 0, 40, 1)
		};
		view.Open("value-0");
		if (view.ActiveCompletionTask is { } initialTask)
			await initialTask.WaitAsync(TestContext.Current.CancellationToken);
		Assert.Equal(1, view.CompletionCacheCount);
		var input = GetField<TerminalTransparentTextEditor>(view, "_input");
		for (var index = 1; index < 40; index++)
		{
			input.Value = $"value-{index}";
			input.MoveEnd();
			if (view.ActiveCompletionTask is { } activeTask)
				await activeTask.WaitAsync(TestContext.Current.CancellationToken);
			Assert.Equal(Math.Min(index + 1, 32), view.CompletionCacheCount);
		}

		Assert.Equal(32, view.CompletionCacheCount);
	}

	[Fact]
	public async Task FailedAsynchronousCompletionCanBeRetriedForTheSameInput()
	{
		var firstStarted = new TaskCompletionSource<bool>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseFirst = new TaskCompletionSource<bool>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var secondStarted = new TaskCompletionSource<bool>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseSecond = new TaskCompletionSource<bool>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var attempts = 0;
		async ValueTask<TerminalWorkspaceCommandCompletion> CompleteAsync(
			string text,
			int cursor,
			CancellationToken cancellationToken)
		{
			var attempt = Interlocked.Increment(ref attempts);
			if (attempt == 1)
			{
				firstStarted.TrySetResult(true);
				await releaseFirst.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
				throw new IOException("controlled completion failure");
			}

			secondStarted.TrySetResult(true);
			await releaseSecond.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
			return Completion(text + "-ready", "-ready");
		}

		using var view = new TerminalWorkspaceCommandLineView(
			null!,
			CompleteAsync,
			static (_, _) => TerminalWorkspaceCommandGhostCompletion.Empty,
			static key => key,
			new TerminalCommandHistory(),
			plain: false,
			useUnicode: true)
		{
			Frame = new Rectangle(0, 0, 40, 1)
		};

		view.Open("value");
		await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
		var failedTask = Assert.IsAssignableFrom<Task>(view.ActiveCompletionTask);
		releaseFirst.TrySetResult(true);
		await failedTask.WaitAsync(TestContext.Current.CancellationToken);
		Assert.Null(view.ActiveCompletionTask);

		Invoke(view, "CycleCompletion");
		await secondStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
		var retriedTask = Assert.IsAssignableFrom<Task>(view.ActiveCompletionTask);
		releaseSecond.TrySetResult(true);
		await retriedTask.WaitAsync(TestContext.Current.CancellationToken);

		Assert.Equal(2, attempts);
		Assert.Equal("value-ready", view.InputText);
	}

	[Fact]
	public void SessionWiresTheCancellableCompletionProvider()
	{
		var source = File.ReadAllText(Path.Combine(
			PublishedApplicationLocator.FindRepositoryRoot(),
			"Apps",
			"Terminal",
			"Tui",
			"TerminalWorkspaceSession.cs"));

		Assert.Equal(
			2,
			source.Split("GetCompletionAsync", StringSplitOptions.None).Length - 1);
		Assert.Equal(
			2,
			source.Split(
				"BuildCommandParseContext(includeKnownProjectPaths: false)",
				StringSplitOptions.None).Length - 1);
	}

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

	private static TerminalWorkspaceCommandCompletion Completion(string completedText, string ghostSuffix) =>
		new(
			[new TerminalWorkspaceCommandCompletionCandidate(completedText, completedText, completedText.Length)],
			ghostSuffix,
			null);

}
