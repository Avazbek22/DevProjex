namespace DevProjex.Avalonia.ViewModels;

public sealed class ToastMessageViewModel(string message) : ViewModelBase
{
	private string _message = message;
	private double _opacity = 0;
	private double _offsetY = 12;
	private Action<ToastMessageViewModel>? _pauseDismissal;
	private Action<ToastMessageViewModel>? _resumeDismissal;
	private Action<ToastMessageViewModel>? _dismiss;

	public string Message
	{
		get => _message;
		set
		{
			if (_message == value) return;
			_message = value;
			RaisePropertyChanged();
		}
	}

	public double Opacity
	{
		get => _opacity;
		set
		{
			if (Math.Abs(_opacity - value) < 0.001) return;
			_opacity = value;
			RaisePropertyChanged();
		}
	}

	public double OffsetY
	{
		get => _offsetY;
		set
		{
			if (Math.Abs(_offsetY - value) < 0.001) return;
			_offsetY = value;
			RaisePropertyChanged();
		}
	}

	internal void SetInteractionHandlers(
		Action<ToastMessageViewModel> pauseDismissal,
		Action<ToastMessageViewModel> resumeDismissal,
		Action<ToastMessageViewModel> dismiss)
	{
		_pauseDismissal = pauseDismissal;
		_resumeDismissal = resumeDismissal;
		_dismiss = dismiss;
	}

	internal void PauseDismissal() => _pauseDismissal?.Invoke(this);

	internal void ResumeDismissal() => _resumeDismissal?.Invoke(this);

	internal void Dismiss() => _dismiss?.Invoke(this);

	internal void ClearInteractionHandlers()
	{
		_pauseDismissal = null;
		_resumeDismissal = null;
		_dismiss = null;
	}
}
