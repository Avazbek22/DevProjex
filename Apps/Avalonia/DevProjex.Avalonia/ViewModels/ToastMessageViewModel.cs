using System;

namespace DevProjex.Avalonia.ViewModels;

public sealed class ToastMessageViewModel : ViewModelBase
{
	private string _message;
	private double _opacity;
	private double _offsetY;

	public ToastMessageViewModel(string message)
	{
		_message = message;
		_opacity = 0;
		_offsetY = 12;
	}

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
}
