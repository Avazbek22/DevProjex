namespace DevProjex.Avalonia.ViewModels;

public sealed class IgnoreOptionViewModel(
    IgnoreOptionId id,
    string label,
    bool isChecked,
    bool isControllerGroupEnd = false) : ViewModelBase
{
    private bool _isChecked = isChecked;
    private string _label = label;
    private string _statusText = string.Empty;

    public IgnoreOptionId Id { get; } = id;

    public bool IsGitIgnoreOption => Id == IgnoreOptionId.UseGitIgnore;

    public bool IsControllerGroupEnd { get; } = isControllerGroupEnd;

    public string Label
    {
        get => _label;
        set
        {
            if (_label == value) return;
            _label = value;
            RaisePropertyChanged();
        }
    }

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

    public string StatusText
    {
        get => _statusText;
        internal set
        {
            if (_statusText == value) return;
            _statusText = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);

    public event EventHandler? CheckedChanged;
}
