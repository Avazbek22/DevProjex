namespace DevProjex.Avalonia.Services;

public sealed class ToastService : IToastService, IDisposable
{
	private const int MaxToasts = 3;
	private const int MillisecondsPerCharacter = 60;
	private static readonly TimeSpan BaseDisplayDuration = TimeSpan.FromSeconds(2);
	private static readonly TimeSpan MinimumDisplayDuration = TimeSpan.FromSeconds(2.5);
	private static readonly TimeSpan MaximumDisplayDuration = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan FadeDuration = UiTimingProfile.Scale(TimeSpan.FromMilliseconds(200));
	private static readonly TimeSpan UiAnimationDelay = UiTimingProfile.Scale(TimeSpan.FromMilliseconds(10));

	private readonly Func<TimeSpan, CancellationToken, Task> _dismissDelayAsync;
	private readonly Dictionary<ToastMessageViewModel, DismissalState> _dismissalStates = new();
	private int _disposed;

	public ToastService()
		: this(static (duration, cancellationToken) => Task.Delay(duration, cancellationToken))
	{
	}

	internal ToastService(Func<TimeSpan, CancellationToken, Task> dismissDelayAsync)
	{
		ArgumentNullException.ThrowIfNull(dismissDelayAsync);
		_dismissDelayAsync = dismissDelayAsync;
	}

	public ObservableCollection<ToastMessageViewModel> Items { get; } = [];

	public void Show(string message)
	{
		if (string.IsNullOrWhiteSpace(message))
			return;

		Show(message, CalculateDefaultDuration(message));
	}

	internal static TimeSpan CalculateDefaultDuration(string message)
	{
		ArgumentNullException.ThrowIfNull(message);
		var unscaled = BaseDisplayDuration + TimeSpan.FromMilliseconds(
			(long)message.Length * MillisecondsPerCharacter);
		return UiTimingProfile.Scale(TimeSpan.FromMilliseconds(Math.Clamp(
			unscaled.TotalMilliseconds,
			MinimumDisplayDuration.TotalMilliseconds,
			MaximumDisplayDuration.TotalMilliseconds)));
	}

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
			toast.SetInteractionHandlers(PauseDismissal, ResumeDismissal, RemoveToast);
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
		var state = new DismissalState(duration);
		_dismissalStates[toast] = state;
		StartDismissal(toast, state);
	}

	private void StartDismissal(ToastMessageViewModel toast, DismissalState state)
	{
		state.IsPaused = false;
		state.StartedTimestamp = Stopwatch.GetTimestamp();
		var cancellation = new CancellationTokenSource();
		state.Cancellation = cancellation;
		_ = DismissAsync(toast, state, cancellation);
	}

	private async Task DismissAsync(
		ToastMessageViewModel toast,
		DismissalState state,
		CancellationTokenSource cancellation)
	{
		try
		{
			await _dismissDelayAsync(state.Remaining, cancellation.Token);
		}
		catch (OperationCanceledException)
		{
			return;
		}

		await Dispatcher.UIThread.InvokeAsync(async () =>
		{
			if (!_dismissalStates.TryGetValue(toast, out var current) ||
				!ReferenceEquals(current, state) ||
				!ReferenceEquals(state.Cancellation, cancellation) ||
				state.IsPaused)
			{
				return;
			}

			state.Cancellation = null;
			cancellation.Dispose();
			toast.Opacity = 0;
			toast.OffsetY = 12;
			await Task.Delay(FadeDuration);
			if (_dismissalStates.TryGetValue(toast, out current) && ReferenceEquals(current, state))
				RemoveToast(toast);
		});
	}

	private void PauseDismissal(ToastMessageViewModel toast)
	{
		if (!_dismissalStates.TryGetValue(toast, out var state) || state.IsPaused)
			return;

		var elapsed = Stopwatch.GetElapsedTime(state.StartedTimestamp);
		state.Remaining = elapsed < state.Remaining
			? state.Remaining - elapsed
			: TimeSpan.Zero;
		state.IsPaused = true;
		CancelDismissal(state);
	}

	private void ResumeDismissal(ToastMessageViewModel toast)
	{
		if (!_dismissalStates.TryGetValue(toast, out var state) || !state.IsPaused)
			return;

		if (state.Remaining <= TimeSpan.Zero)
		{
			RemoveToast(toast);
			return;
		}

		StartDismissal(toast, state);
	}

	private void RemoveToast(ToastMessageViewModel toast)
	{
		if (_dismissalStates.Remove(toast, out var state))
			CancelDismissal(state);

		toast.ClearInteractionHandlers();
		Items.Remove(toast);
	}

	private static void CancelDismissal(DismissalState state)
	{
		var cancellation = state.Cancellation;
		state.Cancellation = null;
		if (cancellation is null)
			return;

		cancellation.Cancel();
		cancellation.Dispose();
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;

		foreach (var state in _dismissalStates.Values)
			CancelDismissal(state);
		foreach (var toast in Items)
			toast.ClearInteractionHandlers();

		_dismissalStates.Clear();
		Items.Clear();
	}

	private sealed class DismissalState(TimeSpan duration)
	{
		public TimeSpan Remaining { get; set; } = duration;
		public long StartedTimestamp { get; set; }
		public CancellationTokenSource? Cancellation { get; set; }
		public bool IsPaused { get; set; }
	}
}
