using System.Collections.ObjectModel;
using Avalonia.Threading;
using DevProjex.Avalonia.ViewModels;

namespace DevProjex.Avalonia.Services;

public sealed class ToastService : IToastService
{
	private const int MaxToasts = 3;
	private static readonly TimeSpan DisplayDuration = TimeSpan.FromSeconds(3);
	private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(200);
	private static readonly TimeSpan UiAnimationDelay = TimeSpan.FromMilliseconds(10);

	private readonly Dictionary<ToastMessageViewModel, CancellationTokenSource> _dismissTokens = new();

	public ObservableCollection<ToastMessageViewModel> Items { get; } = new();

	public void Show(string message)
	{
		if (string.IsNullOrWhiteSpace(message))
			return;

		Dispatcher.UIThread.Post(() =>
		{
			var toast = new ToastMessageViewModel(message);
			AddToast(toast);
			ScheduleDismiss(toast, DisplayDuration);
		});
	}

	private void AddToast(ToastMessageViewModel toast)
	{
		if (Items.Count >= MaxToasts)
			RemoveToast(Items[0]);

		Items.Add(toast);

		Dispatcher.UIThread.Post(async () =>
		{
			await Task.Delay(UiAnimationDelay);
			toast.Opacity = 1;
			toast.OffsetY = 0;
		});
	}

	private void ScheduleDismiss(ToastMessageViewModel toast, TimeSpan duration)
	{
		var cts = new CancellationTokenSource();
		_dismissTokens[toast] = cts;

		_ = DismissAsync(toast, duration, cts.Token);
	}

	private async Task DismissAsync(ToastMessageViewModel toast, TimeSpan duration, CancellationToken token)
	{
		try
		{
			await Task.Delay(duration, token);
		}
		catch (OperationCanceledException)
		{
			return;
		}

		await Dispatcher.UIThread.InvokeAsync(async () =>
		{
			toast.Opacity = 0;
			toast.OffsetY = 12;
			await Task.Delay(FadeDuration);
			RemoveToast(toast);
		});
	}

	private void RemoveToast(ToastMessageViewModel toast)
	{
		if (_dismissTokens.Remove(toast, out var cts))
		{
			cts.Cancel();
			cts.Dispose();
		}

		Items.Remove(toast);
	}
}
