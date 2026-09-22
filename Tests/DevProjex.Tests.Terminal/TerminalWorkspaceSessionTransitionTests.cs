namespace DevProjex.Tests.Terminal;

public sealed class TerminalWorkspaceSessionTransitionTests
{
	[Fact]
	public async Task LatestSettingsRefreshBarrierWaitsAcrossTheDebounceGapAndSupersedingDraft()
	{
		var firstRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var secondRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var debounceTransition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var supersedingTransition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var finalTransition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		Task? pendingRefresh = null;
		var hasDraft = true;
		Task stateChanged = debounceTransition.Task;
		var requestId = 0L;
		var outcome = TerminalWorkspaceSession.SettingsRefreshOutcome.None;

		var barrier = TerminalWorkspaceSession.AwaitLatestSettingsRefreshAsync(
			() => (pendingRefresh, hasDraft, stateChanged, requestId, outcome),
			TestContext.Current.CancellationToken);

		await Task.Yield();
		Assert.False(barrier.IsCompleted);

		pendingRefresh = firstRefresh.Task;
		stateChanged = supersedingTransition.Task;
		requestId = 1;
		outcome = TerminalWorkspaceSession.SettingsRefreshOutcome.Pending;
		debounceTransition.SetResult();
		await Task.Yield();
		Assert.False(barrier.IsCompleted);

		pendingRefresh = secondRefresh.Task;
		stateChanged = finalTransition.Task;
		requestId = 2;
		supersedingTransition.SetResult();

		await Task.Yield();
		Assert.False(barrier.IsCompleted);

		hasDraft = false;
		pendingRefresh = null;
		outcome = TerminalWorkspaceSession.SettingsRefreshOutcome.Applied;
		secondRefresh.SetResult();
		finalTransition.SetResult();
		await barrier;
		firstRefresh.SetResult();
	}

	[Fact]
	public async Task FailedSettingsRefreshPreventsCopyPayloadConstruction()
	{
		const string secret = "ghp_a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL";
		var backgroundRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var stateChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var hasDraft = true;
		Task? pendingRefresh = backgroundRefresh.Task;
		var outcome = TerminalWorkspaceSession.SettingsRefreshOutcome.Pending;
		var payloadBuilderCalls = 0;
		var clipboard = "unchanged";

		async Task CopyAfterRefreshAsync()
		{
			await TerminalWorkspaceSession.AwaitLatestSettingsRefreshAsync(
				() => (pendingRefresh, hasDraft, stateChanged.Task, 1, outcome),
				TestContext.Current.CancellationToken);
			payloadBuilderCalls++;
			clipboard = secret;
		}

		var copy = CopyAfterRefreshAsync();
		await Task.Yield();
		Assert.False(copy.IsCompleted);
		hasDraft = false;
		backgroundRefresh.SetException(new InvalidOperationException("background refresh failed"));
		pendingRefresh = null;
		outcome = TerminalWorkspaceSession.SettingsRefreshOutcome.Failed;
		stateChanged.SetResult();

		await Assert.ThrowsAnyAsync<Exception>(() => copy);
		Assert.Equal(0, payloadBuilderCalls);
		Assert.Equal("unchanged", clipboard);
		_ = backgroundRefresh.Task.Exception;
	}

	[Fact]
	public void PortableProfileSaveWaitsForTheSettingsBarrierBeforeReadingThePlan()
	{
		var source = File.ReadAllText(Path.Combine(
			PublishedApplicationLocator.FindRepositoryRoot(),
			"Apps",
			"Terminal",
			"Tui",
			"TerminalWorkspaceSession.cs"));
		var methodStart = source.IndexOf(
			"private void SaveProfile(",
			StringComparison.Ordinal);
		var methodEnd = source.IndexOf(
			"private async Task RunOperationAsync(",
			methodStart,
			StringComparison.Ordinal);
		Assert.InRange(methodStart, 0, source.Length - 1);
		Assert.InRange(methodEnd, methodStart + 1, source.Length);
		var method = source[methodStart..methodEnd];
		var barrier = method.IndexOf("AwaitLatestSettingsRefreshAsync", StringComparison.Ordinal);
		var save = method.IndexOf("SavePortableProfileAsync", StringComparison.Ordinal);

		Assert.InRange(barrier, 0, method.Length - 1);
		Assert.InRange(save, barrier + 1, method.Length);
	}

	[Fact]
	public void JournalResetReloadsCompleteHistoryBeforeRebuildingTheTrace()
	{
		var source = File.ReadAllText(Path.Combine(
			PublishedApplicationLocator.FindRepositoryRoot(),
			"Apps",
			"Terminal",
			"Tui",
			"TerminalWorkspaceSession.LiveContext.cs"));
		var reset = source.IndexOf("RequiresReset: true", StringComparison.Ordinal);
		var reload = source.IndexOf("ReadCallsAsync", reset, StringComparison.Ordinal);
		var snapshot = source.IndexOf("TerminalAgentJournalSnapshot.Create", reload, StringComparison.Ordinal);

		Assert.InRange(reset, 0, source.Length - 1);
		Assert.InRange(reload, reset + 1, source.Length);
		Assert.InRange(snapshot, reload + 1, source.Length);
	}

	[Fact]
	public void PreviewSearchUsesOneClearTransition()
	{
		var method = typeof(TerminalWorkspaceSession).GetMethod(
			"ClearPreviewSearch",
			System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

		Assert.NotNull(method);
	}

	[Fact]
	public void WelcomeRecentPaletteIdTracksTheConcreteProjectAcrossReordering()
	{
		var method = typeof(TerminalWorkspaceSession).GetMethod(
			"BuildWelcomePaletteItemId",
			System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
		Assert.NotNull(method);
		var first = new TerminalWelcomeAction(
			TerminalWelcomeActionKind.RecentProject,
			"first",
			"recent",
			@"C:\projects\first",
			Number: 1);
		var reordered = first with { Number = 2 };
		var second = first with { Title = "second", Value = @"C:\projects\second" };

		var firstId = Assert.IsType<string>(method.Invoke(null, [first]));
		var reorderedId = Assert.IsType<string>(method.Invoke(null, [reordered]));
		var secondId = Assert.IsType<string>(method.Invoke(null, [second]));

		Assert.Equal(firstId, reorderedId);
		Assert.NotEqual(firstId, secondId);
	}

	[Fact]
	public async Task SuccessfulCheckoutUsesANonCancelableConsistencyRefresh()
	{
		using var cancellation = new CancellationTokenSource();
		var inconsistent = false;
		var refreshed = false;

		var switched = await TerminalWorkspaceSession.RunPostCheckoutRefreshAsync(
			_ =>
			{
				cancellation.Cancel();
				return Task.FromResult(true);
			},
			token =>
			{
				Assert.False(token.CanBeCanceled);
				refreshed = true;
				return Task.CompletedTask;
			},
			value => inconsistent = value,
			cancellation.Token);

		Assert.True(switched);
		Assert.True(refreshed);
		Assert.False(inconsistent);
	}

	[Fact]
	public async Task FailedPostCheckoutRefreshLeavesExportsBlocked()
	{
		using var cancellation = new CancellationTokenSource();
		var inconsistent = false;

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			TerminalWorkspaceSession.RunPostCheckoutRefreshAsync(
				_ =>
				{
					cancellation.Cancel();
					return Task.FromResult(true);
				},
				token =>
				{
					Assert.False(token.CanBeCanceled);
					return Task.FromException(new InvalidOperationException("refresh failed"));
				},
				value => inconsistent = value,
				cancellation.Token));

		Assert.True(inconsistent);
		Assert.False(TerminalWorkspaceSession.IsRepositoryExportAllowed(inconsistent));
	}
}
