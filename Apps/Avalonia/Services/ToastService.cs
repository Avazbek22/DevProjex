namespace DevProjex.Avalonia.Services;

public sealed class ToastService : IToastService, IDisposable
{
	private const int MaxToasts = 3;
	private static readonly TimeSpan DisplayDuration = UiTimingProfile.Scale(TimeSpan.FromSeconds(2));
	private static readonly TimeSpan FadeDuration = UiTimingProfile.Scale(TimeSpan.FromMilliseconds(200));
	private static readonly TimeSpan UiAnimationDelay = UiTimingProfile.Scale(TimeSpan.FromMilliseconds(10));

	private readonly Dictionary<ToastMessageViewModel, CancellationTokenSource> _dismissTokens = new();
	private int _disposed;

	public ObservableCollection<ToastMessageViewModel> Items { get; } = [];

	public void Show(string message) => Show(message, DisplayDuration);

	public void Show(string message, TimeSpan duration)
	{
		if (Volatile.Read(ref _disposed) != 0)
			return;
		if (string.IsNullOrWhiteSpace(message))
			return;
		if (duration <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(duration));

		Dispatcher.UIThread.Post(() =>
		{
			if (Volatile.Read(ref _disposed) != 0)
				return;

			var toast = new ToastMessageViewModel(message);
			AddToast(toast);
			ScheduleDismiss(toast, duration);
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
			if (Volatile.Read(ref _disposed) != 0 || !Items.Contains(toast))
				return;

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

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;

		foreach (var cts in _dismissTokens.Values)
		{
			cts.Cancel();
			cts.Dispose();
		}

		_dismissTokens.Clear();
		Items.Clear();
	}
}
