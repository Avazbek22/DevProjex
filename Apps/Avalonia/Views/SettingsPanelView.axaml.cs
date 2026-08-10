using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia.Views;

public partial class SettingsPanelView : UserControl
{
    private const double DefaultHeaderMinimumGap = 3.0;
    private const double MinimumWidthSafetyPadding = 2.0;

    private Border? _panelRoot;
    private Grid? _ignoreHeaderGrid;
    private TextBlock? _ignoreHeaderText;
    private CheckBox? _ignoreAllCheckBox;
    private Grid? _extensionsHeaderGrid;
    private TextBlock? _extensionsHeaderText;
    private CheckBox? _extensionsAllCheckBox;
    private double _lastReportedMinimumWidth;
    private bool _minimumWidthRefreshQueued;
    private bool _pendingForcedMinimumWidthRefresh;
    private bool _minimumWidthSizeSubscriptionsAttached;

    public event EventHandler<RoutedEventArgs>? ApplySettingsRequested;
    public event EventHandler<RoutedEventArgs>? IgnoreAllChanged;
    public event EventHandler<RoutedEventArgs>? ExtensionsAllChanged;
    public event EventHandler<SettingsPanelMinimumWidthChangedEventArgs>? MinimumWidthChanged;
	public event EventHandler? SecretScanRetryRequested;

    public SettingsPanelView()
    {
        InitializeComponent();

        _panelRoot = this.FindControl<Border>("PanelRoot");
        _ignoreHeaderGrid = this.FindControl<Grid>("IgnoreHeaderGrid");
        _ignoreHeaderText = this.FindControl<TextBlock>("IgnoreHeaderText");
        _ignoreAllCheckBox = this.FindControl<CheckBox>("IgnoreAllCheckBox");
        _extensionsHeaderGrid = this.FindControl<Grid>("ExtensionsHeaderGrid");
        _extensionsHeaderText = this.FindControl<TextBlock>("ExtensionsHeaderText");
        _extensionsAllCheckBox = this.FindControl<CheckBox>("ExtensionsAllCheckBox");

        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnApplySettings(object? sender, RoutedEventArgs e)
        => ApplySettingsRequested?.Invoke(sender, e);

    private void OnIgnoreAllChanged(object? sender, RoutedEventArgs e)
        => IgnoreAllChanged?.Invoke(sender, e);

    private void OnExtensionsAllChanged(object? sender, RoutedEventArgs e)
        => ExtensionsAllChanged?.Invoke(sender, e);

	private void OnContentProcessingToolTipLoaded(object? sender, RoutedEventArgs e)
	{
		if (sender is not ToolTip toolTip || DataContext is not MainWindowViewModel viewModel)
			return;

		PopupBackdropConfigurator.TryApply(
			toolTip,
			TopLevel.GetTopLevel(this),
			viewModel.ActiveThemeEffect,
			PopupBackdropTransparencyFallback.Transparent);
	}

	private void OnContentProcessingStatusIndicatorPointerPressed(
		object? sender,
		PointerPressedEventArgs e)
	{
		e.Handled = true;
	}

	private void OnContentProcessingStatusIndicatorPointerReleased(
		object? sender,
		PointerReleasedEventArgs e)
	{
		// The warning triangle on Hide Secrets doubles as the retry control: a failure there is
		// usually transient, and the status text invites this click. Informational indicators
		// keep their tooltip-on-click behavior.
		if (sender is Control
		    {
			    DataContext: IgnoreOptionViewModel { Id: IgnoreOptionId.HideSecrets, IsWarningStatus: true }
		    })
		{
			SecretScanRetryRequested?.Invoke(this, EventArgs.Empty);
			e.Handled = true;
			return;
		}

		if (sender is Control indicator)
			ToolTip.SetIsOpen(indicator, true);

		e.Handled = true;
	}

    public void RequestMinimumWidthRefresh()
        => QueueMinimumWidthRefresh(force: true);

    public double GetRequiredMinimumWidth()
        => CalculateRequiredMinimumWidth();

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        AttachMinimumWidthSubscriptions();
        QueueMinimumWidthRefresh(force: true);
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        DetachMinimumWidthSubscriptions();
        _minimumWidthRefreshQueued = false;
        _pendingForcedMinimumWidthRefresh = false;
    }

    private void ReportMinimumWidthIfChanged(bool force)
    {
        var minimumWidth = CalculateRequiredMinimumWidth();
        if (!force && Math.Abs(minimumWidth - _lastReportedMinimumWidth) < 0.5)
            return;

        _lastReportedMinimumWidth = minimumWidth;
        MinimumWidthChanged?.Invoke(this, new SettingsPanelMinimumWidthChangedEventArgs(minimumWidth));
    }

    private double CalculateRequiredMinimumWidth()
    {
        // The window owns the visual settings-island width. Header text is allowed to
        // compress inside that frame so the island can stay aligned with the top toolbar.
        if (_panelRoot?.MinWidth is > 0)
            return Math.Ceiling(_panelRoot.MinWidth);

        var contentWidth = Math.Max(
            MeasureHeaderWidth(_ignoreHeaderGrid, _ignoreHeaderText, _ignoreAllCheckBox),
            MeasureHeaderWidth(_extensionsHeaderGrid, _extensionsHeaderText, _extensionsAllCheckBox));

        var panelPadding = _panelRoot?.Padding ?? default;
        var borderThickness = _panelRoot?.BorderThickness ?? default;

        var totalWidth = contentWidth
                         + panelPadding.Left
                         + panelPadding.Right
                         + borderThickness.Left
                         + borderThickness.Right
                         + MinimumWidthSafetyPadding;

        return Math.Ceiling(Math.Max(240.0, totalWidth));
    }

    // Measure against infinite width so the computed minimum reflects the real content width,
    // not the current constrained layout width.
    private static double MeasureHeaderWidth(Grid? headerGrid, Control? title, CheckBox? allCheckBox)
    {
        var titleWidth = MeasureControlWidth(title);
        var checkBoxWidth = MeasureControlWidth(allCheckBox);
        if (titleWidth <= 0 || checkBoxWidth <= 0)
            return titleWidth + checkBoxWidth;

        return titleWidth + GetHeaderGap(headerGrid) + checkBoxWidth;
    }

    private static double MeasureControlWidth(Control? control)
        => SettingsPanelMeasurementHelper.MeasureControlWidth(control);

    private void QueueMinimumWidthRefresh(bool force)
    {
        _pendingForcedMinimumWidthRefresh |= force;
        if (_minimumWidthRefreshQueued)
            return;

        _minimumWidthRefreshQueued = true;
        // Post to Render so all header text/check box measurements are stable before we
        // report a new minimum width to the window layout.
        Dispatcher.Post(
            FlushPendingMinimumWidthRefresh,
            DispatcherPriority.Render);
    }

    private static double GetHeaderGap(Grid? headerGrid)
    {
        if (headerGrid?.ColumnDefinitions.Count > 1)
        {
            var gapColumn = headerGrid.ColumnDefinitions[1].Width;
            if (gapColumn.IsAbsolute)
                return gapColumn.Value;
        }

        return DefaultHeaderMinimumGap;
    }

    private void FlushPendingMinimumWidthRefresh()
    {
        _minimumWidthRefreshQueued = false;
        var force = _pendingForcedMinimumWidthRefresh;
        _pendingForcedMinimumWidthRefresh = false;
        ReportMinimumWidthIfChanged(force);
    }

    private void AttachMinimumWidthSubscriptions()
    {
        if (_minimumWidthSizeSubscriptionsAttached ||
            _panelRoot?.MinWidth is > 0)
        {
            return;
        }

        ToggleMinimumWidthAffectingSizeChanges(_ignoreHeaderText, subscribe: true);
        ToggleMinimumWidthAffectingSizeChanges(_ignoreAllCheckBox, subscribe: true);
        ToggleMinimumWidthAffectingSizeChanges(_extensionsHeaderText, subscribe: true);
        ToggleMinimumWidthAffectingSizeChanges(_extensionsAllCheckBox, subscribe: true);
        _minimumWidthSizeSubscriptionsAttached = true;
    }

    private void DetachMinimumWidthSubscriptions()
    {
        if (!_minimumWidthSizeSubscriptionsAttached)
            return;

        ToggleMinimumWidthAffectingSizeChanges(_ignoreHeaderText, subscribe: false);
        ToggleMinimumWidthAffectingSizeChanges(_ignoreAllCheckBox, subscribe: false);
        ToggleMinimumWidthAffectingSizeChanges(_extensionsHeaderText, subscribe: false);
        ToggleMinimumWidthAffectingSizeChanges(_extensionsAllCheckBox, subscribe: false);
        _minimumWidthSizeSubscriptionsAttached = false;
    }

    private void ToggleMinimumWidthAffectingSizeChanges(Control? control, bool subscribe)
    {
        if (control is null)
            return;

        if (subscribe)
            control.SizeChanged += OnMinimumWidthAffectingSizeChanged;
        else
            control.SizeChanged -= OnMinimumWidthAffectingSizeChanged;
    }

    private void OnMinimumWidthAffectingSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 0.5
            && Math.Abs(e.NewSize.Height - e.PreviousSize.Height) < 0.5)
        {
            return;
        }

        QueueMinimumWidthRefresh(force: false);
    }
}

public sealed class SettingsPanelMinimumWidthChangedEventArgs(double minimumWidth) : EventArgs
{
    public double MinimumWidth { get; } = minimumWidth;
}
