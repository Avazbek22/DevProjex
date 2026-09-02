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
	private bool _isWarningStatus;

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
            RaisePropertyChanged(nameof(DisplayName));
            RaisePropertyChanged(nameof(CounterText));
            RaisePropertyChanged(nameof(HasCounter));
        }
    }

    /// <summary>
    /// Labels travel through profiles and selection snapshots as a single string, so the
    /// trailing match counter is recovered here rather than threaded through every
    /// persistence path. The split lets the view trim a long name with an ellipsis while
    /// the counter stays visible.
    /// </summary>
    public string DisplayName
    {
        get
        {
            var counterStart = FindCounterStart(_label);
            return counterStart < 0 ? _label : _label[..counterStart];
        }
    }

    public string CounterText
    {
        get
        {
            var counterStart = FindCounterStart(_label);
            return counterStart < 0 ? string.Empty : _label[(counterStart + 1)..];
        }
    }

    public bool HasCounter => FindCounterStart(_label) >= 0;

    // Only a trailing " (digits)" or " (digits/digits)" group counts; parentheses that are
    // part of a translated name never match because they contain non-digit characters.
    private static int FindCounterStart(string label)
    {
        if (label.Length < 4 || label[^1] != ')')
            return -1;
        var open = label.LastIndexOf(" (", StringComparison.Ordinal);
        if (open < 1)
            return -1;
        var digitsSeen = false;
        var slashSeen = false;
        for (var index = open + 2; index < label.Length - 1; index++)
        {
            var character = label[index];
            if (character is >= '0' and <= '9')
            {
                digitsSeen = true;
                continue;
            }

            if (character == '/' && digitsSeen && !slashSeen)
            {
                slashSeen = true;
                digitsSeen = false;
                continue;
            }

            return -1;
        }

        return digitsSeen ? open : -1;
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
			RaisePropertyChanged(nameof(IsInformationStatus));
        }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);

	public bool IsWarningStatus
	{
		get => _isWarningStatus;
		internal set
		{
			if (_isWarningStatus == value) return;
			_isWarningStatus = value;
			RaisePropertyChanged();
			RaisePropertyChanged(nameof(IsInformationStatus));
		}
	}

	public bool IsInformationStatus => HasStatus && !IsWarningStatus;

    public event EventHandler? CheckedChanged;
}
