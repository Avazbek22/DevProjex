namespace DevProjex.Avalonia.Services;

public interface IToastService
{
	ObservableCollection<ToastMessageViewModel> Items { get; }
	void Show(string message);
}
