using Avalonia.Threading;
using Avalonia.VisualTree;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class ToastServiceLifecycleUiTests(UiWorkspaceFixture workspace)
{
	[Fact]
	public void DefaultDuration_UsesMessageLengthAndClampsShortAndLongText()
	{
		Assert.Equal(
			UiTimingProfile.Scale(TimeSpan.FromSeconds(2.5)),
			ToastService.CalculateDefaultDuration("OK"));
		Assert.Equal(
			UiTimingProfile.Scale(TimeSpan.FromSeconds(10)),
			ToastService.CalculateDefaultDuration(new string('x', 500)));
	}

	[AvaloniaFact]
	public async Task ShowQueuedBeforeDispose_DoesNotPublishAfterDisposal()
	{
		var service = new ToastService();

		await Task.Run(() => service.Show("late toast", TimeSpan.FromMinutes(1)));
		service.Dispose();
		Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);

		Assert.Empty(service.Items);
	}

	[AvaloniaFact]
	public async Task ExplicitDuration_IsPassedToDismissalTimerWithoutDefaultScaling()
	{
		var scheduler = new ControlledDismissScheduler();
		using var service = new ToastService(scheduler.DelayAsync);
		var explicitDuration = TimeSpan.FromSeconds(37);

		service.Show("Explicit duration", explicitDuration);
		Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);

		Assert.Equal(explicitDuration, Assert.Single(scheduler.Requests).Duration);
	}

	[AvaloniaFact]
	public async Task PointerHover_PausesAndResumesDismissal_AndClickDismissesImmediately()
	{
		var scheduler = new ControlledDismissScheduler();
		using var service = new ToastService(scheduler.DelayAsync);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			workspace.Project,
			configureServices: services => services with { ToastService = service });

		try
		{
			service.Show("Pause while reading", TimeSpan.FromMinutes(1));
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => service.Items.Count == 1 && scheduler.Requests.Count == 1,
				"the first toast dismissal timer");

			var toast = Assert.Single(service.Items);
			var border = GetToastBorder(window, toast);
			window.MouseMove(UiTestDriver.GetControlCenter(border, window), RawInputModifiers.None);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => scheduler.Requests[0].WaitTask.IsCanceled,
				"the hovered toast timer to pause");
			Assert.Contains(toast, service.Items);

			window.MouseMove(new Point(1, 1), RawInputModifiers.None);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => scheduler.Requests.Count == 2,
				"the toast timer to resume after pointer exit");
			scheduler.Requests[1].Complete();
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => service.Items.Count == 0,
				"the resumed toast to dismiss");

			service.Show("Click to dismiss", TimeSpan.FromMinutes(1));
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => service.Items.Count == 1 && scheduler.Requests.Count == 3,
				"the clickable toast");
			var clickable = Assert.Single(service.Items);
			await UiTestDriver.ClickAsync(window, GetToastBorder(window, clickable));

			Assert.Empty(service.Items);
			Assert.True(scheduler.Requests[2].WaitTask.IsCanceled);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	private static Border GetToastBorder(MainWindow window, ToastMessageViewModel toast) =>
		Assert.Single(
			window.GetVisualDescendants().OfType<Border>(),
			control => ReferenceEquals(control.DataContext, toast));

	private sealed class ControlledDismissScheduler
	{
		public List<DelayRequest> Requests { get; } = [];

		public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
		{
			var request = new DelayRequest(duration, cancellationToken);
			Requests.Add(request);
			return request.WaitTask;
		}
	}

	private sealed class DelayRequest
	{
		private readonly TaskCompletionSource _completion = new(
			TaskCreationOptions.RunContinuationsAsynchronously);

		public DelayRequest(TimeSpan duration, CancellationToken cancellationToken)
		{
			Duration = duration;
			WaitTask = _completion.Task.WaitAsync(cancellationToken);
		}

		public TimeSpan Duration { get; }
		public Task WaitTask { get; }

		public void Complete() => _completion.TrySetResult();
	}
}
