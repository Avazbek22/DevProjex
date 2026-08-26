using Avalonia.Threading;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.UI;

public sealed class ToastServiceLifecycleUiTests
{
	[AvaloniaFact]
	public async Task ShowQueuedBeforeDispose_DoesNotPublishAfterDisposal()
	{
		var service = new ToastService();

		await Task.Run(() => service.Show("late toast", TimeSpan.FromMinutes(1)));
		service.Dispose();
		Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);

		Assert.Empty(service.Items);
	}
}
