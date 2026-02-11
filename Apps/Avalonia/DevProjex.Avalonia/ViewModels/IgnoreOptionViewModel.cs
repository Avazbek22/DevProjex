using DevProjex.Kernel.Models;

namespace DevProjex.Avalonia.ViewModels;

public sealed class IgnoreOptionViewModel : ViewModelBase
{
    private bool _isChecked;
    private string _label;

    public IgnoreOptionViewModel(IgnoreOptionId id, string label, bool isChecked)
    {
        Id = id;
        _label = label;
        _isChecked = isChecked;
    }

    public IgnoreOptionId Id { get; }

    public bool IsGitIgnoreOption => Id == IgnoreOptionId.UseGitIgnore;

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

    public event EventHandler? CheckedChanged;
}
