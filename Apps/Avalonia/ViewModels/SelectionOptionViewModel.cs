namespace DevProjex.Avalonia.ViewModels;

public sealed class SelectionOptionViewModel(string name, bool isChecked) : ViewModelBase
{
    private bool _isChecked = isChecked;

    public string Name { get; } = name;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            RaisePropertyChanged();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? CheckedChanged;
}
