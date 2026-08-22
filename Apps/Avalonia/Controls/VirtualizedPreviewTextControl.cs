using DevProjex.Avalonia.Services;
using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Avalonia.Controls;

public sealed class PreviewRedactionToggleRequestedEventArgs(
	string occurrenceId,
	IReadOnlyCollection<string>? restoreOccurrenceIds = null) : EventArgs
{
	public string OccurrenceId { get; } = occurrenceId;
	public IReadOnlyCollection<string>? RestoreOccurrenceIds { get; } = restoreOccurrenceIds;
}

public sealed class PreviewBulkRedactionToggleRequestedEventArgs(
	IReadOnlyCollection<string> occurrenceIds,
	bool keep) : EventArgs
{
	public IReadOnlyCollection<string> OccurrenceIds { get; } = occurrenceIds;
	public bool Keep { get; } = keep;
}

public sealed class PreviewManualSecretMarkRequestedEventArgs(
	MarkedSecretValue value,
	PreviewSelectionRange selection,
	ManualRedactionClass classification,
	bool persistent) : EventArgs
{
	public PreviewManualSecretMarkRequestedEventArgs(
		MarkedSecretValue value,
		PreviewSelectionRange selection,
		bool persistent)
		: this(value, selection, ManualRedactionClass.Secret, persistent)
	{
	}

	public MarkedSecretValue Value { get; } = value;
	public PreviewSelectionRange Selection { get; } = selection;
	public ManualRedactionClass Class { get; } = classification;
	public bool Persistent { get; } = persistent;
}

public sealed class PreviewManualSecretUnmarkRequestedEventArgs(
	string? persistentMarkHash,
	int persistentMarkLength,
	string? sessionMarkId,
	bool alsoDetected,
	PersistentSecretMarkId? persistentMarkId = null) : EventArgs
{
	public string? PersistentMarkHash { get; } = persistentMarkHash;
	public int PersistentMarkLength { get; } = persistentMarkLength;
	public string? SessionMarkId { get; } = sessionMarkId;
	public bool AlsoDetected { get; } = alsoDetected;
	public PersistentSecretMarkId? PersistentMarkId { get; } = persistentMarkId;
}

internal sealed class PreviewManualSecretMarkRejectedEventArgs(string message) : EventArgs
{
	public string Message { get; } = message;
}

/// <summary>
/// Draws only visible preview text lines for large payloads.
/// Rendering stays virtualized while the underlying document can be either in-memory
/// or file-backed, which keeps preview RAM bounded for huge repositories.
/// </summary>
public sealed class VirtualizedPreviewTextControl : Control
{
    public event EventHandler<CancelEventArgs>? CopyingToClipboard;
    public event EventHandler? CopiedToClipboard;
    public event EventHandler? PreviewSelectionChanged;
	public event EventHandler<PreviewRedactionToggleRequestedEventArgs>? RedactionToggleRequested;
	public event EventHandler<PreviewBulkRedactionToggleRequestedEventArgs>? BulkRedactionToggleRequested;
	public event EventHandler<PreviewManualSecretMarkRequestedEventArgs>? ManualSecretMarkRequested;
	public event EventHandler<PreviewManualSecretUnmarkRequestedEventArgs>? ManualSecretUnmarkRequested;
	internal event EventHandler<PreviewManualSecretMarkRejectedEventArgs>? ManualSecretMarkRejected;
	internal event EventHandler? SearchDocumentChanged;
	internal event EventHandler<PreviewMarkersChangedEventArgs>? PreviewMarkersChanged;

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<VirtualizedPreviewTextControl, string>(nameof(Text), string.Empty);

    public static readonly StyledProperty<IPreviewTextDocument?> DocumentProperty =
        AvaloniaProperty.Register<VirtualizedPreviewTextControl, IPreviewTextDocument?>(nameof(Document));

    public static readonly StyledProperty<double> VerticalOffsetProperty =
        AvaloniaProperty.Register<VirtualizedPreviewTextControl, double>(nameof(VerticalOffset));

    public static readonly StyledProperty<double> HorizontalOffsetProperty =
        AvaloniaProperty.Register<VirtualizedPreviewTextControl, double>(nameof(HorizontalOffset));

    public static readonly StyledProperty<double> ViewportHeightProperty =
        AvaloniaProperty.Register<VirtualizedPreviewTextControl, double>(nameof(ViewportHeight));

    public static readonly StyledProperty<double> ViewportWidthProperty =
        AvaloniaProperty.Register<VirtualizedPreviewTextControl, double>(nameof(ViewportWidth));

    public static readonly StyledProperty<double> TopPaddingProperty =
        AvaloniaProperty.Register<VirtualizedPreviewTextControl, double>(nameof(TopPadding), 10.0);

    public static readonly StyledProperty<double> BottomPaddingProperty =
        AvaloniaProperty.Register<VirtualizedPreviewTextControl, double>(nameof(BottomPadding), 10.0);

    public static readonly StyledProperty<double> LeftPaddingProperty =
        AvaloniaProperty.Register<VirtualizedPreviewTextControl, double>(nameof(LeftPadding), 10.0);

    public static readonly StyledProperty<double> RightPaddingProperty =
        AvaloniaProperty.Register<VirtualizedPreviewTextControl, double>(nameof(RightPadding), 16.0);

    public static readonly StyledProperty<FontFamily?> TextFontFamilyProperty =
        AvaloniaProperty.Register<VirtualizedPreviewTextControl, FontFamily?>(
            nameof(TextFontFamily),
            FontFamily.Default);

    public static readonly StyledProperty<double> TextFontSizeProperty =
        AvaloniaProperty.Register<VirtualizedPreviewTextControl, double>(nameof(TextFontSize), 15.0);

    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<VirtualizedPreviewTextControl, IBrush?>(nameof(TextBrush));

    public static readonly StyledProperty<IBrush?> SectionDividerBrushProperty =
        AvaloniaProperty.Register<VirtualizedPreviewTextControl, IBrush?>(nameof(SectionDividerBrush));

    public static readonly StyledProperty<string> StickyHeaderTextProperty =
        AvaloniaProperty.Register<VirtualizedPreviewTextControl, string>(nameof(StickyHeaderText), string.Empty);

    public static readonly StyledProperty<bool> StickyHeaderVisibleProperty =
        AvaloniaProperty.Register<VirtualizedPreviewTextControl, bool>(nameof(StickyHeaderVisible));

    public static readonly StyledProperty<bool> StickyHeaderReservedProperty =
        AvaloniaProperty.Register<VirtualizedPreviewTextControl, bool>(nameof(StickyHeaderReserved));

    public static readonly StyledProperty<double> TopOverlayClipHeightProperty =
        AvaloniaProperty.Register<VirtualizedPreviewTextControl, double>(nameof(TopOverlayClipHeight));

    public static readonly StyledProperty<IBrush?> StickyHeaderBackgroundBrushProperty =
        AvaloniaProperty.Register<VirtualizedPreviewTextControl, IBrush?>(nameof(StickyHeaderBackgroundBrush));

    public static readonly StyledProperty<IBrush?> StickyHeaderBorderBrushProperty =
        AvaloniaProperty.Register<VirtualizedPreviewTextControl, IBrush?>(nameof(StickyHeaderBorderBrush));

    public static readonly StyledProperty<string> CopyMenuHeaderProperty =
        AvaloniaProperty.Register<VirtualizedPreviewTextControl, string>(nameof(CopyMenuHeader), "Copy");

    public static readonly StyledProperty<string> SelectAllMenuHeaderProperty =
        AvaloniaProperty.Register<VirtualizedPreviewTextControl, string>(nameof(SelectAllMenuHeader), "Select All");

	public static readonly StyledProperty<string> RedactedSecretToolTipFormatProperty =
		AvaloniaProperty.Register<VirtualizedPreviewTextControl, string>(
			nameof(RedactedSecretToolTipFormat),
			DesktopShortcutTextFormatter.Format(
				"Detected {0}.\nClick to keep the original value.\n{alt}↑ / {alt}↓ navigates findings.",
				DesktopPlatformResolver.Resolve()));

	public static readonly StyledProperty<string> KeptSecretToolTipFormatProperty =
		AvaloniaProperty.Register<VirtualizedPreviewTextControl, string>(
			nameof(KeptSecretToolTipFormat),
			"Detected {0}.\nThe original value is kept.\nClick to redact it again.");

	public static readonly StyledProperty<string> AlwaysHideSecretFormatProperty =
		AvaloniaProperty.Register<VirtualizedPreviewTextControl, string>(
			nameof(AlwaysHideSecretFormat),
			"Always hide secret \"{0}\"");

	public static readonly StyledProperty<string> HideSecretHereFormatProperty =
		AvaloniaProperty.Register<VirtualizedPreviewTextControl, string>(
			nameof(HideSecretHereFormat),
			"Hide \"{0}\" here");

	public static readonly StyledProperty<string> PrivateDataAlwaysHideFormatProperty =
		AvaloniaProperty.Register<VirtualizedPreviewTextControl, string>(
			nameof(PrivateDataAlwaysHideFormat),
			"Hide \"{0}\" as private data");

	public static readonly StyledProperty<string> HideHereSecretToolTipProperty =
		AvaloniaProperty.Register<VirtualizedPreviewTextControl, string>(
			nameof(HideHereSecretToolTip),
			"Only this occurrence");

	public static readonly StyledProperty<string> AlwaysHideValueToolTipProperty =
		AvaloniaProperty.Register<VirtualizedPreviewTextControl, string>(
			nameof(AlwaysHideValueToolTip),
			"All occurrences of this value");

	public static readonly StyledProperty<string> PrivateDataAlwaysHideToolTipProperty =
		AvaloniaProperty.Register<VirtualizedPreviewTextControl, string>(
			nameof(PrivateDataAlwaysHideToolTip),
			"All occurrences; controlled by Hide private data");

	public static readonly StyledProperty<string> RemoveSecretMarkHeaderProperty =
		AvaloniaProperty.Register<VirtualizedPreviewTextControl, string>(
			nameof(RemoveSecretMarkHeader),
			"Remove mark");

	public static readonly StyledProperty<string> KeepAllRuleOccurrencesFormatProperty =
		AvaloniaProperty.Register<VirtualizedPreviewTextControl, string>(
			nameof(KeepAllRuleOccurrencesFormat),
			"Keep all occurrences \"{0}\" ({1})");

	public static readonly StyledProperty<string> HideAllRuleOccurrencesFormatProperty =
		AvaloniaProperty.Register<VirtualizedPreviewTextControl, string>(
			nameof(HideAllRuleOccurrencesFormat),
			"Hide all occurrences \"{0}\" ({1})");

	public static readonly StyledProperty<string> KeepAllFileOccurrencesFormatProperty =
		AvaloniaProperty.Register<VirtualizedPreviewTextControl, string>(
			nameof(KeepAllFileOccurrencesFormat),
			"Keep all occurrences in \"{0}\" ({1})");

	public static readonly StyledProperty<string> HideAllFileOccurrencesFormatProperty =
		AvaloniaProperty.Register<VirtualizedPreviewTextControl, string>(
			nameof(HideAllFileOccurrencesFormat),
			"Hide all occurrences in \"{0}\" ({1})");

	public static readonly StyledProperty<string> SecretSelectionTooShortProperty =
		AvaloniaProperty.Register<VirtualizedPreviewTextControl, string>(
			nameof(SecretSelectionTooShort),
			"Select at least 8 characters.");

	public static readonly StyledProperty<string> SecretSelectionTooLongProperty =
		AvaloniaProperty.Register<VirtualizedPreviewTextControl, string>(
			nameof(SecretSelectionTooLong),
			"Select no more than 512 characters.");

	public static readonly StyledProperty<string> SecretSelectionMultilineProperty =
		AvaloniaProperty.Register<VirtualizedPreviewTextControl, string>(
			nameof(SecretSelectionMultiline),
			"Multiline values are not supported.");

	public static readonly StyledProperty<string> SecretSelectionContentOnlyProperty =
		AvaloniaProperty.Register<VirtualizedPreviewTextControl, string>(
			nameof(SecretSelectionContentOnly),
			"Select a value inside file content.");

    private const int RenderBufferLines = 3;
    private const int MaxFallbackVisibleLines = 120;
    private const int MaxRenderedVisibleLines = 512;
    private const int MaxRetainedLineMetadataCapacity = 4096;
    private const int MaxCachedFormattedLines =
        (MaxRenderedVisibleLines + (RenderBufferLines * 2)) * 2;
    private const double AutoScrollEdgeThreshold = 28.0;
    private static readonly TimeSpan AutoScrollTickInterval = TimeSpan.FromMilliseconds(16);
    private readonly List<int> _lineStarts = [0];
    private readonly PreviewFontMetricsCache _fontMetricsCache = new();
    private DispatcherTimer? _selectionAutoScrollTimer;
    private VisibleTextWindow? _cachedVisibleWindow;
    private IPreviewTextDocument? _cachedVisibleWindowDocument;
    private int _cachedVisibleWindowFirstLine;
    private int _cachedVisibleWindowLastLine;
    private readonly Dictionary<int, FormattedLineCacheEntry> _formattedLineCache = [];
    private readonly Queue<int> _formattedLineCacheOrder = [];
	private Dictionary<int, PreviewRedactionSpan[]> _redactionsByLine = [];
	private PreviewRedactionSpan[] _redactionOccurrences = [];
	private PreviewSearchMatch[] _searchMatches = [];
	private int _activeSearchMatchIndex = -1;
	private bool _selectionOwnedBySearch;
	private string? _hoveredRedactionOccurrenceId;
	private RedactionNavigationTarget? _activeRedactionTarget;
    private string? _formattedLineCacheFontFamilyName;
    private string? _formattedLineCacheCultureName;
    private double _formattedLineCacheFontSize = double.NaN;
    private IBrush? _formattedLineCacheBrush;
    private ScrollViewer? _ownerScrollViewer;
    private int _lineCount = 1;
    private int _maxLineLength;
    private SelectionPosition? _selectionAnchor;
    private SelectionPosition? _selectionActive;
    private bool _isSelecting;
    private Point _selectionPointerViewportPoint;
    private ThemeVariant? _cachedSelectionTheme;
    private IBrush? _cachedSelectionBackground;
    private IBrush? _cachedSelectionForeground;
	private ThemeVariant? _cachedSearchTheme;
	private IBrush? _cachedSearchHighlightBrush;
	private IBrush? _cachedSearchCurrentBrush;
	private IBrush? _cachedSearchHighlightTextBrush;
    private StickyHeaderTrimCacheKey? _cachedStickyHeaderTrimKey;
    private string? _cachedStickyHeaderTrimText;
	private MenuFlyout? _contextFlyout;
	private MenuItem? _secretHideHereMenuItem;
	private MenuItem? _secretAlwaysHideMenuItem;
	private MenuItem? _privateDataAlwaysHideMenuItem;
	private MenuItem? _removeSecretMarkMenuItem;
	private Separator? _manualRedactionSeparator;
	private Separator? _bulkRedactionSeparator;
	private MenuItem? _bulkRuleRedactionMenuItem;
	private MenuItem? _bulkFileRedactionMenuItem;
    private MenuItem? _copyMenuItem;
    private MenuItem? _selectAllMenuItem;
	private ToolTip? _redactionToolTip;
	private TextBlock? _redactionToolTipText;
	private MarkedSecretValue? _contextMarkedSecret;
	private PreviewSelectionRange _contextSelectionRange;
	private string? _contextSecretMarkRejectionMessage;
	private PreviewRedactionSpan? _contextManualRedaction;
	private PreviewRedactionSpan? _contextDetectorRedaction;
	private IReadOnlyCollection<string> _contextRuleOccurrenceIds = [];
	private IReadOnlyCollection<string> _contextFileOccurrenceIds = [];
	private bool _contextBulkKeep;
    private static Cursor? _previewTextCursor;
    private static Cursor? _previewMenuCursor;
	private static Cursor? _previewActionCursor;

    // Cursor construction resolves a platform service. Keep it out of the type
    // initializer so geometry helpers and detached controls remain usable before
    // Avalonia finishes bootstrapping its desktop or headless backend.
    private static Cursor PreviewTextCursor =>
        _previewTextCursor ??= new Cursor(StandardCursorType.Ibeam);

    private static Cursor PreviewMenuCursor =>
        _previewMenuCursor ??= new Cursor(StandardCursorType.Arrow);

	private static Cursor PreviewActionCursor =>
		_previewActionCursor ??= new Cursor(StandardCursorType.Hand);

    static VirtualizedPreviewTextControl()
    {
        AffectsRender<VirtualizedPreviewTextControl>(
            TextProperty,
            DocumentProperty,
            HorizontalOffsetProperty,
            VerticalOffsetProperty,
            ViewportHeightProperty,
            ViewportWidthProperty,
            TopPaddingProperty,
            BottomPaddingProperty,
            LeftPaddingProperty,
            RightPaddingProperty,
            TextFontFamilyProperty,
            TextFontSizeProperty,
            TextBrushProperty,
            SectionDividerBrushProperty,
            StickyHeaderTextProperty,
            StickyHeaderVisibleProperty,
            StickyHeaderReservedProperty,
            TopOverlayClipHeightProperty,
            StickyHeaderBackgroundBrushProperty,
            StickyHeaderBorderBrushProperty,
            CopyMenuHeaderProperty,
            SelectAllMenuHeaderProperty,
			RedactedSecretToolTipFormatProperty,
			KeptSecretToolTipFormatProperty,
			AlwaysHideSecretFormatProperty,
			HideSecretHereFormatProperty,
			PrivateDataAlwaysHideFormatProperty,
			HideHereSecretToolTipProperty,
			AlwaysHideValueToolTipProperty,
			PrivateDataAlwaysHideToolTipProperty,
			RemoveSecretMarkHeaderProperty,
			KeepAllRuleOccurrencesFormatProperty,
			HideAllRuleOccurrencesFormatProperty,
			KeepAllFileOccurrencesFormatProperty,
			HideAllFileOccurrencesFormatProperty,
			SecretSelectionTooShortProperty,
			SecretSelectionTooLongProperty,
			SecretSelectionMultilineProperty,
			SecretSelectionContentOnlyProperty);

        AffectsMeasure<VirtualizedPreviewTextControl>(
            TextProperty,
            DocumentProperty,
            ViewportWidthProperty,
            TopPaddingProperty,
            BottomPaddingProperty,
            LeftPaddingProperty,
            RightPaddingProperty,
            TextFontFamilyProperty,
            TextFontSizeProperty);

        TextProperty.Changed.AddClassHandler<VirtualizedPreviewTextControl>((control, _) =>
        {
            control.RebuildTextLayoutMetadata();
        });

        DocumentProperty.Changed.AddClassHandler<VirtualizedPreviewTextControl>((control, _) =>
        {
			control.ClearSearchMatches(publishMarkers: false);
            control.RebuildRedactionIndex();
            control.RebuildTextLayoutMetadata();
			control.SearchDocumentChanged?.Invoke(control, EventArgs.Empty);
        });

        CopyMenuHeaderProperty.Changed.AddClassHandler<VirtualizedPreviewTextControl>((control, _) =>
        {
            control.UpdateContextMenuHeaders();
        });

        SelectAllMenuHeaderProperty.Changed.AddClassHandler<VirtualizedPreviewTextControl>((control, _) =>
        {
            control.UpdateContextMenuHeaders();
        });

		AlwaysHideSecretFormatProperty.Changed.AddClassHandler<VirtualizedPreviewTextControl>((control, _) =>
		{
			control.UpdateContextMenuHeaders();
		});

		HideSecretHereFormatProperty.Changed.AddClassHandler<VirtualizedPreviewTextControl>((control, _) =>
		{
			control.UpdateContextMenuHeaders();
		});

		PrivateDataAlwaysHideFormatProperty.Changed.AddClassHandler<VirtualizedPreviewTextControl>((control, _) =>
		{
			control.UpdateContextMenuHeaders();
		});

		HideHereSecretToolTipProperty.Changed.AddClassHandler<VirtualizedPreviewTextControl>((control, _) =>
		{
			control.UpdateContextMenuHeaders();
		});

		AlwaysHideValueToolTipProperty.Changed.AddClassHandler<VirtualizedPreviewTextControl>((control, _) =>
		{
			control.UpdateContextMenuHeaders();
		});

		PrivateDataAlwaysHideToolTipProperty.Changed.AddClassHandler<VirtualizedPreviewTextControl>((control, _) =>
		{
			control.UpdateContextMenuHeaders();
		});

		RemoveSecretMarkHeaderProperty.Changed.AddClassHandler<VirtualizedPreviewTextControl>((control, _) =>
		{
			control.UpdateContextMenuHeaders();
		});

		KeepAllRuleOccurrencesFormatProperty.Changed.AddClassHandler<VirtualizedPreviewTextControl>((control, _) =>
		{
			control.UpdateContextMenuHeaders();
		});

		HideAllRuleOccurrencesFormatProperty.Changed.AddClassHandler<VirtualizedPreviewTextControl>((control, _) =>
		{
			control.UpdateContextMenuHeaders();
		});

		KeepAllFileOccurrencesFormatProperty.Changed.AddClassHandler<VirtualizedPreviewTextControl>((control, _) =>
		{
			control.UpdateContextMenuHeaders();
		});

		HideAllFileOccurrencesFormatProperty.Changed.AddClassHandler<VirtualizedPreviewTextControl>((control, _) =>
		{
			control.UpdateContextMenuHeaders();
		});
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IPreviewTextDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public double VerticalOffset
    {
        get => GetValue(VerticalOffsetProperty);
        set => SetValue(VerticalOffsetProperty, value);
    }

    public double HorizontalOffset
    {
        get => GetValue(HorizontalOffsetProperty);
        set => SetValue(HorizontalOffsetProperty, value);
    }

    public double ViewportHeight
    {
        get => GetValue(ViewportHeightProperty);
        set => SetValue(ViewportHeightProperty, value);
    }

    public double ViewportWidth
    {
        get => GetValue(ViewportWidthProperty);
        set => SetValue(ViewportWidthProperty, value);
    }

    public double TopPadding
    {
        get => GetValue(TopPaddingProperty);
        set => SetValue(TopPaddingProperty, value);
    }

    public double BottomPadding
    {
        get => GetValue(BottomPaddingProperty);
        set => SetValue(BottomPaddingProperty, value);
    }

    public double LeftPadding
    {
        get => GetValue(LeftPaddingProperty);
        set => SetValue(LeftPaddingProperty, value);
    }

    public double RightPadding
    {
        get => GetValue(RightPaddingProperty);
        set => SetValue(RightPaddingProperty, value);
    }

    public FontFamily? TextFontFamily
    {
        get => GetValue(TextFontFamilyProperty);
        set => SetValue(TextFontFamilyProperty, value);
    }

    public double TextFontSize
    {
        get => GetValue(TextFontSizeProperty);
        set => SetValue(TextFontSizeProperty, value);
    }

    public IBrush? TextBrush
    {
        get => GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public IBrush? SectionDividerBrush
    {
        get => GetValue(SectionDividerBrushProperty);
        set => SetValue(SectionDividerBrushProperty, value);
    }

    public string StickyHeaderText
    {
        get => GetValue(StickyHeaderTextProperty);
        set => SetValue(StickyHeaderTextProperty, value);
    }

    public bool StickyHeaderVisible
    {
        get => GetValue(StickyHeaderVisibleProperty);
        set => SetValue(StickyHeaderVisibleProperty, value);
    }

    public bool StickyHeaderReserved
    {
        get => GetValue(StickyHeaderReservedProperty);
        set => SetValue(StickyHeaderReservedProperty, value);
    }

    public double TopOverlayClipHeight
    {
        get => GetValue(TopOverlayClipHeightProperty);
        set => SetValue(TopOverlayClipHeightProperty, value);
    }

    public IBrush? StickyHeaderBackgroundBrush
    {
        get => GetValue(StickyHeaderBackgroundBrushProperty);
        set => SetValue(StickyHeaderBackgroundBrushProperty, value);
    }

    public IBrush? StickyHeaderBorderBrush
    {
        get => GetValue(StickyHeaderBorderBrushProperty);
        set => SetValue(StickyHeaderBorderBrushProperty, value);
    }

    public string CopyMenuHeader
    {
        get => GetValue(CopyMenuHeaderProperty);
        set => SetValue(CopyMenuHeaderProperty, value);
    }

    public string SelectAllMenuHeader
    {
        get => GetValue(SelectAllMenuHeaderProperty);
        set => SetValue(SelectAllMenuHeaderProperty, value);
    }

	public string RedactedSecretToolTipFormat
	{
		get => GetValue(RedactedSecretToolTipFormatProperty);
		set => SetValue(RedactedSecretToolTipFormatProperty, value);
	}

	public string KeptSecretToolTipFormat
	{
		get => GetValue(KeptSecretToolTipFormatProperty);
		set => SetValue(KeptSecretToolTipFormatProperty, value);
	}

	public string AlwaysHideSecretFormat
	{
		get => GetValue(AlwaysHideSecretFormatProperty);
		set => SetValue(AlwaysHideSecretFormatProperty, value);
	}

	public string HideSecretHereFormat
	{
		get => GetValue(HideSecretHereFormatProperty);
		set => SetValue(HideSecretHereFormatProperty, value);
	}

	public string PrivateDataAlwaysHideFormat
	{
		get => GetValue(PrivateDataAlwaysHideFormatProperty);
		set => SetValue(PrivateDataAlwaysHideFormatProperty, value);
	}

	public string HideHereSecretToolTip
	{
		get => GetValue(HideHereSecretToolTipProperty);
		set => SetValue(HideHereSecretToolTipProperty, value);
	}

	public string AlwaysHideValueToolTip
	{
		get => GetValue(AlwaysHideValueToolTipProperty);
		set => SetValue(AlwaysHideValueToolTipProperty, value);
	}

	public string PrivateDataAlwaysHideToolTip
	{
		get => GetValue(PrivateDataAlwaysHideToolTipProperty);
		set => SetValue(PrivateDataAlwaysHideToolTipProperty, value);
	}

	public string RemoveSecretMarkHeader
	{
		get => GetValue(RemoveSecretMarkHeaderProperty);
		set => SetValue(RemoveSecretMarkHeaderProperty, value);
	}

	public string KeepAllRuleOccurrencesFormat
	{
		get => GetValue(KeepAllRuleOccurrencesFormatProperty);
		set => SetValue(KeepAllRuleOccurrencesFormatProperty, value);
	}

	public string HideAllRuleOccurrencesFormat
	{
		get => GetValue(HideAllRuleOccurrencesFormatProperty);
		set => SetValue(HideAllRuleOccurrencesFormatProperty, value);
	}

	public string KeepAllFileOccurrencesFormat
	{
		get => GetValue(KeepAllFileOccurrencesFormatProperty);
		set => SetValue(KeepAllFileOccurrencesFormatProperty, value);
	}

	public string HideAllFileOccurrencesFormat
	{
		get => GetValue(HideAllFileOccurrencesFormatProperty);
		set => SetValue(HideAllFileOccurrencesFormatProperty, value);
	}

	public string SecretSelectionTooShort
	{
		get => GetValue(SecretSelectionTooShortProperty);
		set => SetValue(SecretSelectionTooShortProperty, value);
	}

	public string SecretSelectionTooLong
	{
		get => GetValue(SecretSelectionTooLongProperty);
		set => SetValue(SecretSelectionTooLongProperty, value);
	}

	public string SecretSelectionMultiline
	{
		get => GetValue(SecretSelectionMultilineProperty);
		set => SetValue(SecretSelectionMultilineProperty, value);
	}

	public string SecretSelectionContentOnly
	{
		get => GetValue(SecretSelectionContentOnlyProperty);
		set => SetValue(SecretSelectionContentOnlyProperty, value);
	}

    public bool HasSelection => TryGetNormalizedSelection(out _, out _);

    public VirtualizedPreviewTextControl()
    {
        // Keep the resting preview frame pixel-aligned without rounding logical scroll offsets.
        // Selection and hit-testing share those offsets, while Avalonia performs the final
        // device-pixel baseline snap for the active monitor's render scaling.
        TextOptions.SetTextHintingMode(this, TextHintingMode.Strong);
        TextOptions.SetBaselinePixelAlignment(this, BaselinePixelAlignment.Aligned);
        Focusable = true;
        RebuildTextLayoutMetadata();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Cursor = PreviewTextCursor;
    }

    public void ClearSelection()
    {
        _isSelecting = false;
        StopSelectionAutoScroll();
        ClearSelectionState(invalidateVisual: true);
    }

    public string GetSelectedText() => BuildSelectedText(normalizeForClipboard: false);

    public bool TryGetSelectionRange(out PreviewSelectionRange selectionRange)
    {
        if (!TryGetNormalizedSelection(out var start, out var end))
        {
            selectionRange = default;
            return false;
        }

        selectionRange = new PreviewSelectionRange(start.Line, start.Column, end.Line, end.Column);
        return true;
    }

	internal int SearchMatchCount => _searchMatches.Length;

	internal int ActiveSearchMatchIndex => _activeSearchMatchIndex;
	internal PreviewMarkerSnapshot MarkerSnapshot { get; private set; } = PreviewMarkerSnapshot.Empty;

	internal int SetSearchMatches(
		PreviewSearchMatch[] matches,
		bool activateNearestToViewport,
		bool scrollIntoView)
	{
		ArgumentNullException.ThrowIfNull(matches);
		_searchMatches = matches;
		PublishPreviewMarkers();
		if (matches.Length == 0)
		{
			_activeSearchMatchIndex = -1;
			ClearSearchSelection();
			InvalidateVisual();
			return 0;
		}

		var nextIndex = activateNearestToViewport
			? ResolveNearestSearchMatchIndexFromViewportTop()
			: Math.Clamp(_activeSearchMatchIndex, 0, matches.Length - 1);
		ActivateSearchMatch(nextIndex, scrollIntoView);
		return nextIndex + 1;
	}

	internal int NavigateSearchMatch(int step)
	{
		if (_searchMatches.Length == 0 || step == 0)
			return _activeSearchMatchIndex + 1;

		var currentIndex = _activeSearchMatchIndex;
		if (currentIndex < 0 || currentIndex >= _searchMatches.Length)
			currentIndex = ResolveNearestSearchMatchIndexFromViewportTop();
		var nextIndex = step > 0
			? (currentIndex + 1) % _searchMatches.Length
			: (currentIndex - 1 + _searchMatches.Length) % _searchMatches.Length;
		ActivateSearchMatch(nextIndex, scrollIntoView: true);
		return nextIndex + 1;
	}

	internal void NavigateRedaction(bool forward) => MoveToRedaction(forward);

	internal void NavigateToMarker(PreviewMarkerTarget target)
	{
		if (target.Category == PreviewMarkerCategory.Redaction)
		{
			var index = FindNearestRedactedOccurrenceIndex(target.LineNumber);
			if (index >= 0)
				ActivateRedaction(_redactionOccurrences[index], centerInViewport: true);
		}
		else
		{
			var index = FindNearestSearchMatchIndex(target.LineNumber);
			if (index >= 0)
				ActivateSearchMatch(index, scrollIntoView: true, centerInViewport: true);
		}

		Focus();
	}

	internal void ClearSearchMatches(bool publishMarkers = true)
	{
		var hadMatches = _searchMatches.Length > 0 || _activeSearchMatchIndex >= 0;
		_searchMatches = [];
		_activeSearchMatchIndex = -1;
		ClearSearchSelection();
		if (hadMatches)
		{
			if (publishMarkers)
				PublishPreviewMarkers();
			InvalidateVisual();
		}
	}

    public bool TryHandleViewportSelectionStart(IPointer pointer, Point viewportPoint, KeyModifiers keyModifiers)
    {
        Focus();

        var scrollViewer = GetOwnerScrollViewer();
        var documentPoint = scrollViewer is not null
            ? new Point(scrollViewer.Offset.X + viewportPoint.X, scrollViewer.Offset.Y + viewportPoint.Y)
            : viewportPoint;

        _selectionPointerViewportPoint = viewportPoint;
        return TryStartSelection(pointer, documentPoint, keyModifiers);
    }

    public int GetLineNumberAtVerticalOffset(double verticalOffset)
    {
        var lineHeight = ResolveLineHeight();
        if (lineHeight <= 0)
            return 1;

        return ResolveLineNumberAtOffset(verticalOffset, TopPadding, lineHeight, ResolveLineCount());
    }

    public double GetVerticalOffsetForLine(int lineNumber)
    {
        var lineCount = ResolveLineCount();
        var lineHeight = ResolveLineHeight();
        if (lineCount <= 0 || lineHeight <= 0)
            return 0;

        var normalizedLineNumber = Math.Clamp(lineNumber, 1, lineCount);
        return TopPadding + ((normalizedLineNumber - 1) * lineHeight);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var lineHeight = ResolveLineHeight();
        var width = Math.Max(CalculateRequiredWidth(), Math.Ceiling(Math.Max(0, ViewportWidth)));
        var height = Math.Ceiling(ResolveContentTopPadding() + BottomPadding + (ResolveLineCount() * lineHeight));

        return new Size(Math.Max(1, width), Math.Max(1, height));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var lineCount = ResolveLineCount();
        if (lineCount <= 0)
            return;

        var typeface = ResolveTypeface();
        var lineHeight = ResolveLineHeight();
        if (lineHeight <= 0)
            return;

        var viewportTop = Math.Max(0, VerticalOffset);
        var viewportHeight = ResolveViewportHeightForRendering(lineHeight);
        var contentTopPadding = ResolveContentTopPadding();
        var firstVisibleLine = ResolveLineNumberAtOffset(viewportTop, contentTopPadding, lineHeight, lineCount);
        var visibleLineCount = double.IsFinite(viewportHeight)
            ? (int)Math.Clamp(Math.Ceiling(viewportHeight / lineHeight), 1, MaxRenderedVisibleLines)
            : MaxRenderedVisibleLines;
        var lastVisibleLine = Math.Min(lineCount, firstVisibleLine + visibleLineCount - 1);

        firstVisibleLine = Math.Max(1, firstVisibleLine - RenderBufferLines);
        lastVisibleLine = Math.Min(lineCount, lastVisibleLine + RenderBufferLines);
        if (lastVisibleLine < firstVisibleLine)
            return;

        var visibleWindow = BuildVisibleTextWindow(firstVisibleLine, lastVisibleLine);
        if (visibleWindow.Text.Length == 0)
            return;

        var origin = new Point(
            LeftPadding,
            CalculateViewportRelativeLineOriginY(
                firstVisibleLine,
                contentTopPadding,
                lineHeight,
                viewportTop));

        // ScrollViewer translates this oversized child by -viewportTop. Cancel that
        // transform locally and draw the small visible window around Y=0. Sending
        // billion-pixel glyph coordinates to the composition backend loses precision,
        // even though only a few dozen lines are actually visible.
        using (context.PushTransform(Matrix.CreateTranslation(0, viewportTop)))
        using (PushTopOverlayClip(context, viewportHeight))
        {
            DrawVisibleSectionDividers(context, firstVisibleLine, lastVisibleLine, lineHeight, viewportTop);
			DrawVisibleRedactionHighlights(
				context,
				visibleWindow,
				origin,
				typeface,
				lineHeight);

            if (!TryGetVisibleSelectionRange(visibleWindow, out _, out _))
            {
				DrawVisibleSearchHighlights(
					context,
					visibleWindow,
					origin,
					typeface,
					lineHeight);
                DrawVisibleTextLines(
                    context,
                    visibleWindow,
                    origin,
                    typeface,
                    lineHeight);
            }
            else
            {
                var (selectionBackground, selectionForeground) = ResolveSelectionBrushes();
                DrawSelectionBackgrounds(
                    context,
                    visibleWindow,
                    selectionBackground,
                    typeface,
                    lineHeight,
                    viewportTop);
				DrawVisibleSearchHighlights(
					context,
					visibleWindow,
					origin,
					typeface,
					lineHeight);
                DrawVisibleTextLinesWithSelection(context, visibleWindow, origin, typeface, lineHeight, selectionForeground);
            }

			DrawVisibleSearchHighlightText(
				context,
				visibleWindow,
				origin,
				typeface,
				lineHeight);
        }

        DrawStickyHeader(context, typeface);
    }

    private IDisposable? PushTopOverlayClip(DrawingContext context, double viewportHeight)
    {
        var clipHeight = Math.Max(0, TopOverlayClipHeight);
        if (clipHeight <= 0)
            return null;

        var clipTop = clipHeight;
        var clipWidth = Math.Max(Bounds.Width, HorizontalOffset + Math.Max(ViewportWidth, 1));
        var clipBottom = Math.Max(clipTop, Math.Max(viewportHeight, 1));
        var clipRectHeight = Math.Max(0, clipBottom - clipTop);
        return clipRectHeight > 0
            ? context.PushClip(new Rect(0, clipTop, clipWidth, clipRectHeight))
            : null;
    }

    private void DrawVisibleTextLines(
        DrawingContext context,
        VisibleTextWindow visibleWindow,
        Point origin,
        Typeface typeface,
        double lineHeight)
    {
        EnsureFormattedLineCacheStyle();
        for (var lineIndex = 0; lineIndex < visibleWindow.LineCount; lineIndex++)
        {
            var lineOrigin = new Point(origin.X, origin.Y + (lineIndex * lineHeight));
            var lineNumber = visibleWindow.FirstLine + lineIndex;
            var lineText = visibleWindow.GetLineSpan(lineIndex);
            context.DrawText(
                GetOrCreateFormattedLine(lineNumber, lineText, typeface),
                lineOrigin);
        }
    }

	private void DrawVisibleRedactionHighlights(
		DrawingContext context,
		VisibleTextWindow visibleWindow,
		Point origin,
		Typeface typeface,
		double lineHeight)
	{
		if (_redactionsByLine.Count == 0)
			return;

		var firstLine = visibleWindow.FirstLine;
		var lastLine = firstLine + visibleWindow.LineCount - 1;
		for (var lineNumber = firstLine; lineNumber <= lastLine; lineNumber++)
		{
			if (!_redactionsByLine.TryGetValue(lineNumber, out var lineRedactions))
				continue;

			var lineText = GetLineText(lineNumber);
			foreach (var span in lineRedactions)
			{
				if (span.Length <= 0)
					continue;
				var startColumn = Math.Clamp(span.StartColumn, 0, lineText.Length);
				var endColumn = Math.Clamp(span.StartColumn + span.Length, startColumn, lineText.Length);
				if (endColumn <= startColumn)
					continue;

				var x = origin.X + ResolveDistanceFromColumn(lineText, startColumn, typeface);
				var width = Math.Max(
					1,
					ResolveDistanceFromColumn(lineText, endColumn, typeface) -
					ResolveDistanceFromColumn(lineText, startColumn, typeface));
				var y = origin.Y + ((lineNumber - firstLine) * lineHeight);
				var isInteractive = span.OccurrenceId == _hoveredRedactionOccurrenceId ||
				                    IsNavigationTarget(span, _activeRedactionTarget);
				var (background, border) = ResolveRedactionBrushes(span.State, isInteractive);
				context.DrawRectangle(
					background,
					new Pen(border, isInteractive ? 1.6 : 1.15),
					new RoundedRect(new Rect(x, y + 1, width, Math.Max(1, lineHeight - 2)), 3));
			}
		}
	}

	private void DrawVisibleSearchHighlightText(
		DrawingContext context,
		VisibleTextWindow visibleWindow,
		Point origin,
		Typeface typeface,
		double lineHeight)
	{
		if (_searchMatches.Length == 0)
			return;

		var highlightTextBrush = ResolveSearchHighlightTextBrush();
		var firstMatchIndex = FindFirstSearchMatchOnOrAfterLine(visibleWindow.FirstLine);
		var currentLineNumber = -1;
		var lineText = string.Empty;
		FormattedText? formattedLine = null;
		for (var matchIndex = firstMatchIndex;
		     matchIndex < _searchMatches.Length;
		     matchIndex++)
		{
			var match = _searchMatches[matchIndex];
			if (match.LineNumber > visibleWindow.LastLine)
				break;
			if (match.LineNumber < visibleWindow.FirstLine || match.Length <= 0)
				continue;

			if (currentLineNumber != match.LineNumber)
			{
				currentLineNumber = match.LineNumber;
				lineText = GetLineText(match.LineNumber);
				formattedLine = BuildFormattedText(lineText, typeface);
				formattedLine.SetForegroundBrush(highlightTextBrush);
			}

			var startColumn = Math.Clamp(match.StartColumn, 0, lineText.Length);
			var endColumn = Math.Clamp(match.StartColumn + match.Length, startColumn, lineText.Length);
			if (endColumn <= startColumn || formattedLine is null)
				continue;

			var left = origin.X + ResolveDistanceFromColumn(lineText, startColumn, typeface);
			var right = origin.X + ResolveDistanceFromColumn(lineText, endColumn, typeface);
			var top = origin.Y + ((match.LineNumber - visibleWindow.FirstLine) * lineHeight);
			using (context.PushClip(new Rect(left, top, Math.Max(1, right - left), lineHeight)))
			{
				context.DrawText(
					formattedLine,
					new Point(origin.X, top));
			}
		}
	}

	private void DrawVisibleSearchHighlights(
		DrawingContext context,
		VisibleTextWindow visibleWindow,
		Point origin,
		Typeface typeface,
		double lineHeight)
	{
		if (_searchMatches.Length == 0)
			return;

		var firstMatchIndex = FindFirstSearchMatchOnOrAfterLine(visibleWindow.FirstLine);
		var currentLineNumber = -1;
		var currentLineText = string.Empty;
		for (var matchIndex = firstMatchIndex;
		     matchIndex < _searchMatches.Length;
		     matchIndex++)
		{
			var match = _searchMatches[matchIndex];
			if (match.LineNumber > visibleWindow.LastLine)
				break;
			if (match.LineNumber < visibleWindow.FirstLine || match.Length <= 0)
				continue;

			if (currentLineNumber != match.LineNumber)
			{
				currentLineNumber = match.LineNumber;
				currentLineText = GetLineText(match.LineNumber);
			}

			var lineText = currentLineText;
			var startColumn = Math.Clamp(match.StartColumn, 0, lineText.Length);
			var endColumn = Math.Clamp(match.StartColumn + match.Length, startColumn, lineText.Length);
			if (endColumn <= startColumn)
				continue;

			var left = origin.X + ResolveDistanceFromColumn(lineText, startColumn, typeface);
			var right = origin.X + ResolveDistanceFromColumn(lineText, endColumn, typeface);
			var top = origin.Y + ((match.LineNumber - visibleWindow.FirstLine) * lineHeight);
			context.FillRectangle(
				ResolveSearchBrush(matchIndex == _activeSearchMatchIndex),
				new Rect(left, top + 1, Math.Max(1, right - left), Math.Max(1, lineHeight - 2)));
		}
	}

	private int FindFirstSearchMatchOnOrAfterLine(int lineNumber)
	{
		var low = 0;
		var high = _searchMatches.Length;
		while (low < high)
		{
			var middle = low + ((high - low) / 2);
			if (_searchMatches[middle].LineNumber < lineNumber)
				low = middle + 1;
			else
				high = middle;
		}

		return low;
	}

	private IBrush ResolveSearchBrush(bool current)
	{
		var application = global::Avalonia.Application.Current;
		var theme = application?.ActualThemeVariant ?? ThemeVariant.Light;
		if (_cachedSearchTheme != theme ||
		    _cachedSearchHighlightBrush is null ||
		    _cachedSearchCurrentBrush is null ||
		    _cachedSearchHighlightTextBrush is null)
		{
			_cachedSearchTheme = theme;
			_cachedSearchHighlightBrush = ResolveSearchBrushResource(
				application,
				theme,
				"TreeSearchHighlightBrush",
				"#FFEB3B");
			_cachedSearchCurrentBrush = ResolveSearchBrushResource(
				application,
				theme,
				"TreeSearchCurrentBrush",
				"#F9A825");
			_cachedSearchHighlightTextBrush = ResolveSearchBrushResource(
				application,
				theme,
				"TreeSearchHighlightTextBrush",
				"#000000");
		}

		return current ? _cachedSearchCurrentBrush : _cachedSearchHighlightBrush;
	}

	private IBrush ResolveSearchHighlightTextBrush()
	{
		_ = ResolveSearchBrush(current: false);
		return _cachedSearchHighlightTextBrush!;
	}

	private static IBrush ResolveSearchBrushResource(
		global::Avalonia.Application? application,
		ThemeVariant theme,
		string resourceKey,
		string fallbackColor)
	{
		return application?.TryFindResource(resourceKey, theme, out var resource) == true &&
		       resource is IBrush brush
			? brush
			: new SolidColorBrush(Color.Parse(fallbackColor));
	}

	private static (IBrush Background, IBrush Border) ResolveRedactionBrushes(
		SecretPreviewSpanState state,
		bool isInteractive)
	{
		var application = global::Avalonia.Application.Current;
		var theme = application?.ActualThemeVariant ?? ThemeVariant.Light;
		var accent = application?.Resources.TryGetResource("AppAccentBrush", theme, out var resource) == true &&
		             resource is ISolidColorBrush solid
			? solid.Color
			: Color.Parse("#6D5DFB");
		var backgroundAlpha = state == SecretPreviewSpanState.Redacted
			? (isInteractive ? (byte)82 : (byte)54)
			: (isInteractive ? (byte)38 : (byte)14);
		var borderAlpha = isInteractive
			? (byte)240
			: state == SecretPreviewSpanState.Redacted ? (byte)180 : (byte)190;
		return (
			new SolidColorBrush(Color.FromArgb(backgroundAlpha, accent.R, accent.G, accent.B)),
			new SolidColorBrush(Color.FromArgb(borderAlpha, accent.R, accent.G, accent.B)));
	}

    private void DrawVisibleTextLinesWithSelection(
        DrawingContext context,
        VisibleTextWindow visibleWindow,
        Point origin,
        Typeface typeface,
        double lineHeight,
        IBrush? selectionForeground)
    {
        if (!TryGetNormalizedSelection(out var selectionStart, out var selectionEnd))
        {
            DrawVisibleTextLines(
                context,
                visibleWindow,
                origin,
                typeface,
                lineHeight);
            return;
        }

        for (var lineIndex = 0; lineIndex < visibleWindow.LineCount; lineIndex++)
        {
            var lineNumber = visibleWindow.FirstLine + lineIndex;
            var lineText = visibleWindow.GetLineSpan(lineIndex).ToString();
            // Keep selected text on the same per-line baseline model as normal preview rendering.
            var formattedText = BuildFormattedText(lineText, typeface);
            if (selectionForeground is not null &&
                TryGetSelectedTextColumns(lineNumber, lineText.Length, selectionStart, selectionEnd, out var startColumn, out var endColumn))
            {
                formattedText.SetForegroundBrush(selectionForeground, startColumn, endColumn - startColumn);
            }

            var lineOrigin = new Point(origin.X, origin.Y + (lineIndex * lineHeight));
            context.DrawText(formattedText, lineOrigin);
        }
    }

    private FormattedText GetOrCreateFormattedLine(
        int lineNumber,
        ReadOnlySpan<char> lineText,
        Typeface typeface)
    {
        if (_formattedLineCache.TryGetValue(lineNumber, out var cachedLine) &&
            lineText.SequenceEqual(cachedLine.Text.AsSpan()))
        {
            return cachedLine.FormattedText;
        }

        var text = lineText.ToString();
        var formattedText = BuildFormattedText(text, typeface);
        if (cachedLine is null)
            _formattedLineCacheOrder.Enqueue(lineNumber);

        _formattedLineCache[lineNumber] =
            new FormattedLineCacheEntry(text, formattedText);
        TrimFormattedLineCache();
        return formattedText;
    }

    private void EnsureFormattedLineCacheStyle()
    {
        var fontFamilyName = (TextFontFamily ?? FontFamily.Default).Name;
        var cultureName = CultureInfo.CurrentUICulture.Name;
        var brush = TextBrush ?? Brushes.White;
        if (string.Equals(
                _formattedLineCacheFontFamilyName,
                fontFamilyName,
                StringComparison.Ordinal) &&
            string.Equals(
                _formattedLineCacheCultureName,
                cultureName,
                StringComparison.Ordinal) &&
            _formattedLineCacheFontSize.Equals(TextFontSize) &&
            ReferenceEquals(_formattedLineCacheBrush, brush))
        {
            return;
        }

        ClearFormattedLineCache();
        _formattedLineCacheFontFamilyName = fontFamilyName;
        _formattedLineCacheCultureName = cultureName;
        _formattedLineCacheFontSize = TextFontSize;
        _formattedLineCacheBrush = brush;
    }

    private void TrimFormattedLineCache()
    {
        while (_formattedLineCache.Count > MaxCachedFormattedLines &&
               _formattedLineCacheOrder.TryDequeue(out var lineNumber))
        {
            _formattedLineCache.Remove(lineNumber);
        }
    }

    private void ClearFormattedLineCache()
    {
        _formattedLineCache.Clear();
        _formattedLineCacheOrder.Clear();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var properties = e.GetCurrentPoint(this).Properties;
		var isContextGesture = properties.IsRightButtonPressed ||
			properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed ||
			(DesktopShortcutModifiers.Current.Platform == DesktopPlatform.MacOS &&
			 (properties.IsLeftButtonPressed ||
			  properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed) &&
			 DesktopShortcutModifiers.Current.IsMacOSSecondaryClickModifier(e.KeyModifiers));
		if (isContextGesture)
        {
            Focus();
			PrepareContextSelection(e.GetPosition(this));
			OpenContextMenu();
            e.Handled = true;
            return;
        }

        if (!properties.IsLeftButtonPressed)
            return;

        Focus();
		var hitPosition = HitTestSelectionPosition(e.GetPosition(this));
		if (TryGetRedactionAt(hitPosition, out var redaction))
		{
			_activeRedactionTarget = CreateNavigationTarget(redaction);
			InvalidateVisual();
			RaiseRedactionToggleRequested(redaction);
			e.Handled = true;
			return;
		}

		if (_activeRedactionTarget is not null)
		{
			_activeRedactionTarget = null;
			InvalidateVisual();
		}
        CaptureSelectionPointer(e);
        TryStartSelection(e.Pointer, e.GetPosition(this), e.KeyModifiers);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

		if (!_isSelecting || e.Pointer.Captured != this)
		{
			var position = HitTestSelectionPosition(e.GetPosition(this));
			UpdateHoveredRedaction(TryGetRedactionAt(position, out var hovered) ? hovered : null);
			return;
		}

		UpdateHoveredRedaction(null);
        CaptureSelectionPointer(e);
        UpdateSelectionActivePosition(HitTestSelectionPosition(e.GetPosition(this)));
        UpdateSelectionAutoScrollState();
        Cursor = PreviewTextCursor;
        e.Handled = true;
    }

	protected override void OnPointerExited(PointerEventArgs e)
	{
		base.OnPointerExited(e);
		if (!_isSelecting)
			UpdateHoveredRedaction(null);
	}

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!_isSelecting)
            return;

        CaptureSelectionPointer(e);
        UpdateSelectionActivePosition(HitTestSelectionPosition(e.GetPosition(this)));
        EndSelectionCapture(e.Pointer);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _isSelecting = false;
        StopSelectionAutoScroll();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled)
            return;

		if (DesktopShortcutModifiers.Current.IsPrimary(e.KeyModifiers) && e.Key == Key.A)
        {
            SelectAll();
            e.Handled = true;
            return;
        }

		if (DesktopShortcutModifiers.Current.IsPrimary(e.KeyModifiers) && e.Key == Key.C && HasSelection)
        {
            _ = CopySelectionToClipboardAsync();
            e.Handled = true;
            return;
        }

		if (e.Key == Key.Escape &&
		    (_selectionAnchor is not null ||
		     _selectionActive is not null ||
		     _activeRedactionTarget is not null))
        {
            ClearSelection();
			_activeRedactionTarget = null;
			InvalidateVisual();
            e.Handled = true;
			return;
        }

		if (e.Key is Key.Enter or Key.Space &&
		    TryGetActiveRedaction(out var redaction))
		{
			RaiseRedactionToggleRequested(redaction);
			e.Handled = true;
			return;
		}

		if (e.Key is Key.Up or Key.Down && e.KeyModifiers == KeyModifiers.Alt)
		{
			MoveToRedaction(forward: e.Key == Key.Down);
			e.Handled = true;
		}
    }

	private bool TryGetActiveRedaction(out PreviewRedactionSpan redaction)
	{
		if (_activeRedactionTarget is { } target)
		{
			redaction = FindRedaction(target)!;
			if (redaction is not null)
				return true;
		}

		if (_selectionActive is { } active)
			return TryGetRedactionAt(active, out redaction);

		redaction = null!;
		return false;
	}

	private bool TryGetRedactionAt(
		SelectionPosition position,
		out PreviewRedactionSpan redaction)
	{
		redaction = null!;
		if (!_redactionsByLine.TryGetValue(position.Line, out var redactions))
			return false;

		foreach (var candidate in redactions)
		{
			if (candidate.LineNumber == position.Line &&
			    position.Column >= candidate.StartColumn &&
			    position.Column < candidate.StartColumn + candidate.Length)
			{
				redaction = candidate;
				return true;
			}
		}

		return false;
	}

	private void RebuildRedactionIndex()
	{
		_contextDetectorRedaction = null;
		_contextRuleOccurrenceIds = [];
		_contextFileOccurrenceIds = [];
		_contextFlyout?.Hide();
		var redactions = Document?.Redactions ?? Array.Empty<PreviewRedactionSpan>();
		_redactionsByLine = redactions
			.GroupBy(static span => span.LineNumber)
			.ToDictionary(
				static group => group.Key,
				static group => group.OrderBy(static span => span.StartColumn).ToArray());
		_redactionOccurrences = BuildRedactionNavigationStops(redactions);
		if (_activeRedactionTarget is { } active && FindRedaction(active) is null)
			_activeRedactionTarget = null;
		UpdateHoveredRedaction(null);
		PublishPreviewMarkers();
	}

	private void PublishPreviewMarkers()
	{
		var document = Document;
		if (document is null)
		{
			MarkerSnapshot = PreviewMarkerSnapshot.Empty;
			PreviewMarkersChanged?.Invoke(this, new PreviewMarkersChangedEventArgs(MarkerSnapshot));
			return;
		}

		List<PreviewMarkerSource>? markers = null;
		foreach (var (lineNumber, redactions) in _redactionsByLine)
		{
			if (!redactions.Any(static span =>
				span.State == SecretPreviewSpanState.Redacted &&
				span.Source != SecretFindingSource.GeneratedPath))
				continue;

			markers ??= [];
			markers.Add(new PreviewMarkerSource(lineNumber, PreviewMarkerCategory.Redaction));
		}

		var previousSearchLine = -1;
		foreach (var match in _searchMatches)
		{
			if (match.LineNumber == previousSearchLine)
				continue;

			markers ??= [];
			markers.Add(new PreviewMarkerSource(match.LineNumber, PreviewMarkerCategory.Search));
			previousSearchLine = match.LineNumber;
		}

		MarkerSnapshot = new PreviewMarkerSnapshot(
			Math.Max(1, document.LineCount),
			markers?.ToArray() ?? []);
		PreviewMarkersChanged?.Invoke(this, new PreviewMarkersChangedEventArgs(MarkerSnapshot));
	}

	private void RaiseRedactionToggleRequested(PreviewRedactionSpan redaction)
	{
		var restoreOccurrenceIds = redaction.State == SecretPreviewSpanState.KeptAsIs
			? redaction.CascadedOccurrenceIds
			: null;
		RedactionToggleRequested?.Invoke(
			this,
			new PreviewRedactionToggleRequestedEventArgs(
				redaction.OccurrenceId,
				restoreOccurrenceIds));
	}

	private static PreviewRedactionSpan[] BuildRedactionNavigationStops(
		IReadOnlyList<PreviewRedactionSpan> redactions)
	{
		var stops = redactions.ToArray();
		Array.Sort(stops, CompareRedactionPositions);
		return stops;
	}

	private PreviewRedactionSpan? FindRedaction(RedactionNavigationTarget target)
	{
		if (!_redactionsByLine.TryGetValue(target.LineNumber, out var lineRedactions))
			return null;

		return lineRedactions.FirstOrDefault(span => IsNavigationTarget(span, target));
	}

	private static int CompareRedactionPositions(PreviewRedactionSpan left, PreviewRedactionSpan right)
	{
		var lineComparison = left.LineNumber.CompareTo(right.LineNumber);
		return lineComparison != 0
			? lineComparison
			: left.StartColumn.CompareTo(right.StartColumn);
	}

	private static RedactionNavigationTarget CreateNavigationTarget(PreviewRedactionSpan span) =>
		new(span.OccurrenceId, span.LineNumber, span.StartColumn);

	private static bool IsNavigationTarget(
		PreviewRedactionSpan span,
		RedactionNavigationTarget? target) =>
		target is { } value &&
		string.Equals(span.OccurrenceId, value.OccurrenceId, StringComparison.Ordinal) &&
		span.LineNumber == value.LineNumber &&
		span.StartColumn == value.StartColumn;

	private void UpdateHoveredRedaction(PreviewRedactionSpan? redaction)
	{
		var occurrenceId = redaction?.OccurrenceId;
		var changed = !string.Equals(
			_hoveredRedactionOccurrenceId,
			occurrenceId,
			StringComparison.Ordinal);
		_hoveredRedactionOccurrenceId = occurrenceId;
		Cursor = redaction is null ? PreviewTextCursor : PreviewActionCursor;
		if (redaction is null)
		{
			ToolTip.SetTip(this, null);
		}
		else
		{
			var toolTip = EnsureRedactionToolTip();
			_redactionToolTipText!.Text = string.Format(
				CultureInfo.CurrentCulture,
				redaction.State == SecretPreviewSpanState.Redacted
					? RedactedSecretToolTipFormat
					: KeptSecretToolTipFormat,
				redaction.RuleId);
			ToolTip.SetTip(this, toolTip);
		}
		if (changed)
			InvalidateVisual();
	}

	private ToolTip EnsureRedactionToolTip()
	{
		if (_redactionToolTip is not null)
			return _redactionToolTip;

		_redactionToolTipText = new TextBlock
		{
			TextWrapping = TextWrapping.Wrap
		};
		_redactionToolTip = new ToolTip
		{
			Content = _redactionToolTipText,
			MaxWidth = 420
		};
		return _redactionToolTip;
	}

	private void MoveToRedaction(bool forward)
	{
		if (_redactionOccurrences.Length == 0)
			return;

		var nextIndex = ResolveRedactionNavigationIndex(forward);
		ActivateRedaction(_redactionOccurrences[nextIndex], centerInViewport: false);
	}

	private void ActivateRedaction(
		PreviewRedactionSpan redaction,
		bool centerInViewport)
	{
		_activeRedactionTarget = CreateNavigationTarget(redaction);
		_selectionOwnedBySearch = false;
		_selectionAnchor = new SelectionPosition(redaction.LineNumber, redaction.StartColumn);
		_selectionActive = _selectionAnchor;
		UpdateHoveredRedaction(null);
		ScrollRedactionIntoView(redaction, centerInViewport);
		InvalidateVisual();
		PreviewSelectionChanged?.Invoke(this, EventArgs.Empty);
	}

	private int FindNearestRedactedOccurrenceIndex(int targetLine)
	{
		var nearestIndex = -1;
		var nearestDistance = int.MaxValue;
		for (var index = 0; index < _redactionOccurrences.Length; index++)
		{
			var occurrence = _redactionOccurrences[index];
			if (occurrence.State != SecretPreviewSpanState.Redacted)
				continue;

			var distance = Math.Abs(occurrence.LineNumber - targetLine);
			if (distance < nearestDistance)
			{
				nearestIndex = index;
				nearestDistance = distance;
			}
			else if (occurrence.LineNumber > targetLine && distance > nearestDistance)
			{
				break;
			}
		}

		return nearestIndex;
	}

	private int ResolveRedactionNavigationIndex(bool forward)
	{
		var currentIndex = _activeRedactionTarget is null
			? -1
			: Array.FindIndex(
				_redactionOccurrences,
				span => IsNavigationTarget(span, _activeRedactionTarget));
		var scrollViewer = GetOwnerScrollViewer();
		if (currentIndex >= 0 &&
		    (scrollViewer is null || IsRedactionVisible(_redactionOccurrences[currentIndex], scrollViewer)))
		{
			return forward
				? (currentIndex + 1) % _redactionOccurrences.Length
				: (currentIndex - 1 + _redactionOccurrences.Length) % _redactionOccurrences.Length;
		}

		if (scrollViewer is null || scrollViewer.Viewport.Height <= 0)
			return forward ? 0 : _redactionOccurrences.Length - 1;

		var lineHeight = ResolveLineHeight();
		var viewportTop = scrollViewer.Offset.Y;
		var viewportBottom = viewportTop + scrollViewer.Viewport.Height;
		if (forward)
		{
			var index = Array.FindIndex(
				_redactionOccurrences,
				span => ResolveRedactionLineBottom(span, lineHeight) > viewportTop);
			return index >= 0 ? index : 0;
		}

		var previousIndex = Array.FindLastIndex(
			_redactionOccurrences,
			span => ResolveRedactionLineTop(span, lineHeight) < viewportBottom);
		return previousIndex >= 0 ? previousIndex : _redactionOccurrences.Length - 1;
	}

	private bool IsRedactionVisible(PreviewRedactionSpan redaction, ScrollViewer scrollViewer)
	{
		if (scrollViewer.Viewport.Height <= 0)
			return true;

		var lineHeight = ResolveLineHeight();
		var viewportTop = scrollViewer.Offset.Y;
		var viewportBottom = viewportTop + scrollViewer.Viewport.Height;
		return ResolveRedactionLineBottom(redaction, lineHeight) > viewportTop &&
		       ResolveRedactionLineTop(redaction, lineHeight) < viewportBottom;
	}

	private double ResolveRedactionLineTop(PreviewRedactionSpan redaction, double lineHeight)
		=> ResolveContentTopPadding() + ((redaction.LineNumber - 1) * lineHeight);

	private double ResolveRedactionLineBottom(PreviewRedactionSpan redaction, double lineHeight)
		=> ResolveRedactionLineTop(redaction, lineHeight) + lineHeight;

	private void ScrollRedactionIntoView(
		PreviewRedactionSpan redaction,
		bool centerInViewport = false)
	{
		var scrollViewer = GetOwnerScrollViewer();
		if (scrollViewer is null)
			return;

		var lineHeight = ResolveLineHeight();
		var lineTop = ResolveRedactionLineTop(redaction, lineHeight);
		var lineBottom = ResolveRedactionLineBottom(redaction, lineHeight);
		var offset = scrollViewer.Offset;
		var targetY = ResolveTargetVerticalOffset(
			lineTop,
			lineBottom,
			offset.Y,
			scrollViewer.Viewport.Height,
			centerInViewport);

		var lineText = GetLineText(redaction.LineNumber);
		var typeface = ResolveTypeface();
		var spanLeft = LeftPadding + ResolveDistanceFromColumn(lineText, redaction.StartColumn, typeface);
		var spanRight = LeftPadding + ResolveDistanceFromColumn(
			lineText,
			redaction.StartColumn + redaction.Length,
			typeface);
		var targetX = offset.X;
		if (spanLeft < offset.X)
			targetX = spanLeft - LeftPadding;
		else if (spanRight > offset.X + scrollViewer.Viewport.Width)
			targetX = spanRight - scrollViewer.Viewport.Width + RightPadding;

		var maximumX = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
		var maximumY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
		scrollViewer.Offset = new Vector(
			Math.Clamp(targetX, 0, maximumX),
			Math.Clamp(targetY, 0, maximumY));
	}

	private int ResolveNearestSearchMatchIndexFromViewportTop()
	{
		if (_searchMatches.Length == 0)
			return -1;

		var scrollViewer = GetOwnerScrollViewer();
		if (scrollViewer is null)
			return 0;

		var topLine = GetLineNumberAtVerticalOffset(scrollViewer.Offset.Y);
		var index = FindFirstSearchMatchOnOrAfterLine(topLine);
		return index < _searchMatches.Length ? index : 0;
	}

	private int FindNearestSearchMatchIndex(int targetLine)
	{
		if (_searchMatches.Length == 0)
			return -1;

		var nextIndex = FindFirstSearchMatchOnOrAfterLine(targetLine);
		if (nextIndex == 0)
			return 0;
		if (nextIndex == _searchMatches.Length)
			return nextIndex - 1;

		return targetLine - _searchMatches[nextIndex - 1].LineNumber <=
		       _searchMatches[nextIndex].LineNumber - targetLine
			? nextIndex - 1
			: nextIndex;
	}

	private void ActivateSearchMatch(
		int index,
		bool scrollIntoView,
		bool centerInViewport = false)
	{
		if (index < 0 || index >= _searchMatches.Length)
			return;

		_activeSearchMatchIndex = index;
		var match = _searchMatches[index];
		_selectionAnchor = new SelectionPosition(match.LineNumber, match.StartColumn);
		_selectionActive = new SelectionPosition(
			match.LineNumber,
			match.StartColumn + match.Length);
		_selectionOwnedBySearch = true;
		_activeRedactionTarget = null;
		UpdateHoveredRedaction(null);
		if (scrollIntoView)
			ScrollSearchMatchIntoView(match, centerInViewport);

		InvalidateVisual();
		PreviewSelectionChanged?.Invoke(this, EventArgs.Empty);
	}

	private void ScrollSearchMatchIntoView(
		PreviewSearchMatch match,
		bool centerInViewport = false)
	{
		var scrollViewer = GetOwnerScrollViewer();
		if (scrollViewer is null)
			return;

		var lineHeight = ResolveLineHeight();
		var lineTop = ResolveContentTopPadding() + ((match.LineNumber - 1) * lineHeight);
		var lineBottom = lineTop + lineHeight;
		var offset = scrollViewer.Offset;
		var targetY = ResolveTargetVerticalOffset(
			lineTop,
			lineBottom,
			offset.Y,
			scrollViewer.Viewport.Height,
			centerInViewport);

		var lineText = GetLineText(match.LineNumber);
		var typeface = ResolveTypeface();
		var spanLeft = LeftPadding + ResolveDistanceFromColumn(lineText, match.StartColumn, typeface);
		var spanRight = LeftPadding + ResolveDistanceFromColumn(
			lineText,
			match.StartColumn + match.Length,
			typeface);
		var targetX = offset.X;
		if (spanLeft < offset.X)
			targetX = spanLeft - LeftPadding;
		else if (spanRight > offset.X + scrollViewer.Viewport.Width)
			targetX = spanRight - scrollViewer.Viewport.Width + RightPadding;

		var maximumX = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
		var maximumY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
		scrollViewer.Offset = new Vector(
			Math.Clamp(targetX, 0, maximumX),
			Math.Clamp(targetY, 0, maximumY));
	}

	private static double ResolveTargetVerticalOffset(
		double lineTop,
		double lineBottom,
		double viewportTop,
		double viewportHeight,
		bool centerInViewport)
	{
		if (centerInViewport)
			return ((lineTop + lineBottom) / 2) - (viewportHeight / 2);

		return lineTop < viewportTop || lineBottom > viewportTop + viewportHeight
			? lineTop - (viewportHeight * 0.35)
			: viewportTop;
	}

	private void ClearSearchSelection()
	{
		if (!_selectionOwnedBySearch)
			return;

		ClearSelectionState(invalidateVisual: false);
	}

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isSelecting = false;
        ReleaseSelectionAutoScrollTimer();
        _ownerScrollViewer = null;
		_activeRedactionTarget = null;
		UpdateHoveredRedaction(null);
		ToolTip.SetTip(this, null);
        CloseContextMenu();
        ResetVisibleWindowCache();
    }

    private void RebuildTextLayoutMetadata()
    {
        ResetVisibleWindowCache();
        ClearSelectionState(invalidateVisual: false);
        StopSelectionAutoScroll();
        _cachedSelectionTheme = null;
        _cachedSelectionBackground = null;
        _cachedSelectionForeground = null;
		_cachedSearchTheme = null;
		_cachedSearchHighlightBrush = null;
		_cachedSearchCurrentBrush = null;
		_cachedSearchHighlightTextBrush = null;

        if (Document is { } document)
        {
            _lineStarts.Clear();
            _lineStarts.Add(0);
            ReleaseOversizedLineMetadataCapacity();
            _lineCount = Math.Max(1, document.LineCount);
            _maxLineLength = Math.Max(0, document.MaxLineLength);
            InvalidateMeasure();
            InvalidateVisual();
            return;
        }

        RebuildStringMetadata();
    }

    private void RebuildStringMetadata()
    {
        _lineStarts.Clear();
        _lineStarts.Add(0);
        _lineCount = 1;
        _maxLineLength = 0;

        var text = Text ?? string.Empty;
        if (text.Length == 0)
        {
            ReleaseOversizedLineMetadataCapacity();
            InvalidateMeasure();
            InvalidateVisual();
            return;
        }

        var currentLineLength = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\n')
            {
                if (currentLineLength > _maxLineLength)
                    _maxLineLength = currentLineLength;

                currentLineLength = 0;
                _lineStarts.Add(i + 1);
                continue;
            }

            if (ch != '\r')
                currentLineLength++;
        }

        if (currentLineLength > _maxLineLength)
            _maxLineLength = currentLineLength;

        _lineCount = Math.Max(1, _lineStarts.Count);
        InvalidateMeasure();
        InvalidateVisual();
    }

    private VisibleTextWindow BuildVisibleTextWindow(int firstVisibleLine, int lastVisibleLine)
    {
        if (_cachedVisibleWindow is not null &&
            ReferenceEquals(_cachedVisibleWindowDocument, Document) &&
            _cachedVisibleWindowFirstLine == firstVisibleLine &&
            _cachedVisibleWindowLastLine == lastVisibleLine)
        {
            return _cachedVisibleWindow;
        }

        var text = BuildVisibleLinesText(firstVisibleLine, lastVisibleLine);
        _cachedVisibleWindow = new VisibleTextWindow(firstVisibleLine, lastVisibleLine, text);
        _cachedVisibleWindowDocument = Document;
        _cachedVisibleWindowFirstLine = firstVisibleLine;
        _cachedVisibleWindowLastLine = lastVisibleLine;
        return _cachedVisibleWindow;
    }

    private double ResolveViewportHeightForRendering(double lineHeight)
    {
        if (ViewportHeight > 0)
            return ViewportHeight;

        // Before the owner ScrollViewer reports its viewport, Bounds.Height can be the
        // full document extent instead of the visible surface. Keep the bootstrap render
        // bounded so the control stays virtualized from the first frame onward.
        var boundedFallbackHeight = Math.Max(lineHeight, MaxFallbackVisibleLines * lineHeight);
        return Bounds.Height > 0
            ? Math.Min(Bounds.Height, boundedFallbackHeight)
            : lineHeight;
    }

    private string BuildVisibleLinesText(int firstVisibleLine, int lastVisibleLine)
    {
        if (Document is { } document)
            return document.GetLineRangeText(firstVisibleLine, lastVisibleLine);

        var text = Text ?? string.Empty;
        if (text.Length == 0)
            return string.Empty;

        var linesCount = Math.Max(0, lastVisibleLine - firstVisibleLine + 1);
        var estimatedLineLength = Math.Max(12, Math.Min(_maxLineLength, 256));
        var builder = new StringBuilder(linesCount * (estimatedLineLength + 1));

        for (var lineIndex = firstVisibleLine - 1; lineIndex <= lastVisibleLine - 1; lineIndex++)
        {
            var line = GetStringLineSpan(lineIndex);
            if (!line.IsEmpty)
                builder.Append(line);

            if (lineIndex < lastVisibleLine - 1)
                builder.Append('\n');
        }

        return builder.ToString();
    }

    private bool TryGetVisibleSelectionRange(VisibleTextWindow visibleWindow, out int selectionStart, out int selectionLength)
    {
        selectionStart = 0;
        selectionLength = 0;

        if (!TryGetNormalizedSelection(out var selectionRangeStart, out var selectionRangeEnd))
            return false;

        var windowStart = new SelectionPosition(visibleWindow.FirstLine, 0);
        var windowEnd = new SelectionPosition(visibleWindow.LastLine, visibleWindow.GetLineLength(visibleWindow.LastLine));

        if (ComparePositions(selectionRangeEnd, windowStart) <= 0 ||
            ComparePositions(selectionRangeStart, windowEnd) >= 0)
        {
            return false;
        }

        var clampedStart = ComparePositions(selectionRangeStart, windowStart) < 0
            ? windowStart
            : visibleWindow.Clamp(selectionRangeStart);
        var clampedEnd = ComparePositions(selectionRangeEnd, windowEnd) > 0
            ? windowEnd
            : visibleWindow.Clamp(selectionRangeEnd);

        var localStart = visibleWindow.GetLocalTextIndex(clampedStart.Line, clampedStart.Column);
        var localEnd = visibleWindow.GetLocalTextIndex(clampedEnd.Line, clampedEnd.Column);
        if (localEnd <= localStart)
            return false;

        selectionStart = localStart;
        selectionLength = localEnd - localStart;
        return true;
    }

    private (IBrush SelectionBackground, IBrush? SelectionForeground) ResolveSelectionBrushes()
    {
        var app = global::Avalonia.Application.Current;
        var theme = app?.ActualThemeVariant ?? ThemeVariant.Light;

        if (_cachedSelectionTheme == theme && _cachedSelectionBackground is not null)
            return (_cachedSelectionBackground, _cachedSelectionForeground);

        _cachedSelectionTheme = theme;
        _cachedSelectionBackground = theme == ThemeVariant.Dark
            ? new SolidColorBrush(Color.Parse("#254861"))
            : new SolidColorBrush(Color.Parse("#DCEEFF"));
        _cachedSelectionForeground = TextBrush ?? Brushes.White;

        if (app?.Resources.TryGetResource("PreviewSelectionBrush", theme, out var selectionBackground) == true &&
            selectionBackground is IBrush selectionBackgroundBrush)
        {
            _cachedSelectionBackground = EnsureOpaqueSelectionBrush(selectionBackgroundBrush);
        }

        if (app?.Resources.TryGetResource("PreviewSelectionTextBrush", theme, out var selectionForeground) == true &&
            selectionForeground is IBrush selectionForegroundBrush)
        {
            _cachedSelectionForeground = selectionForegroundBrush;
        }

        return (_cachedSelectionBackground, _cachedSelectionForeground);
    }

    private void DrawSelectionBackgrounds(
        DrawingContext context,
        VisibleTextWindow visibleWindow,
        IBrush selectionBackground,
        Typeface typeface,
        double lineHeight,
        double viewportTop)
    {
        if (!TryGetNormalizedSelection(out var selectionStart, out var selectionEnd))
            return;

        var firstSelectedLine = Math.Max(selectionStart.Line, visibleWindow.FirstLine);
        var lastSelectedLine = Math.Min(selectionEnd.Line, visibleWindow.LastLine);
        if (lastSelectedLine < firstSelectedLine)
            return;

        var minimumSelectionWidth = ResolveMinimumSelectionWidth();

        for (var lineNumber = firstSelectedLine; lineNumber <= lastSelectedLine; lineNumber++)
        {
            var lineText = GetLineText(lineNumber);
            var lineLength = lineText.Length;
            var startColumn = lineNumber == selectionStart.Line
                ? Math.Clamp(selectionStart.Column, 0, lineLength)
                : 0;
            var endColumn = lineNumber == selectionEnd.Line
                ? Math.Clamp(selectionEnd.Column, startColumn, lineLength)
                : lineLength;

            if (startColumn == endColumn && lineNumber == selectionEnd.Line)
                continue;

            var left = LeftPadding + ResolveDistanceFromColumn(lineText, startColumn, typeface);
            var right = LeftPadding + ResolveDistanceFromColumn(lineText, endColumn, typeface);
            var width = Math.Max(0, right - left);

            if (width < minimumSelectionWidth && lineNumber < selectionEnd.Line)
                width = minimumSelectionWidth;

            if (width <= 0)
                continue;

            var top = ResolveContentTopPadding() + ((lineNumber - 1) * lineHeight) - viewportTop;
            context.FillRectangle(selectionBackground, new Rect(left, top, width, lineHeight));
        }
    }

    private static bool TryGetSelectedTextColumns(
        int lineNumber,
        int lineLength,
        SelectionPosition selectionStart,
        SelectionPosition selectionEnd,
        out int startColumn,
        out int endColumn)
    {
        startColumn = 0;
        endColumn = 0;

        if (lineNumber < selectionStart.Line || lineNumber > selectionEnd.Line)
            return false;

        startColumn = lineNumber == selectionStart.Line
            ? Math.Clamp(selectionStart.Column, 0, lineLength)
            : 0;
        endColumn = lineNumber == selectionEnd.Line
            ? Math.Clamp(selectionEnd.Column, startColumn, lineLength)
            : lineLength;

        return endColumn > startColumn;
    }

    private SelectionPosition HitTestSelectionPosition(Point point)
        => HitTestSelection(point).Position;

    private SelectionHitResult HitTestSelection(Point point)
    {
        var stickyHeaderHeight = ResolveStickyHeaderHeight();
        if (stickyHeaderHeight > 0)
        {
            var stickyHeaderTop = Math.Max(0, VerticalOffset);
            if (point.Y >= stickyHeaderTop && point.Y < stickyHeaderTop + stickyHeaderHeight)
            {
                var headerLine = GetLineNumberAtVerticalOffset(stickyHeaderTop);
                return new SelectionHitResult(new SelectionPosition(headerLine, 0), SelectionHitKind.Empty);
            }
        }

        var typeface = ResolveTypeface();
        var lineHeight = ResolveLineHeight();
        if (lineHeight <= 0)
            return new SelectionHitResult(new SelectionPosition(1, 0), SelectionHitKind.Empty);

        var lineCount = ResolveLineCount();
        var relativeY = point.Y - ResolveContentTopPadding();
        if (relativeY < 0)
            return new SelectionHitResult(new SelectionPosition(1, 0), SelectionHitKind.Empty);

        var rawLineNumber = (int)Math.Floor(relativeY / lineHeight) + 1;
        if (rawLineNumber > lineCount)
        {
            var lastLine = Math.Max(1, lineCount);
            return new SelectionHitResult(
                new SelectionPosition(lastLine, GetLineText(lastLine).Length),
                SelectionHitKind.Empty);
        }

        var lineNumber = Math.Clamp(rawLineNumber, 1, lineCount);

        var x = Math.Max(0, point.X - LeftPadding);
        var lineText = GetLineText(lineNumber);
        var lineWidth = ResolveDistanceFromColumn(lineText, lineText.Length, typeface);
        var column = ResolveColumnFromDistance(lineText, x, typeface);
        return new SelectionHitResult(
            new SelectionPosition(lineNumber, column),
            x > lineWidth + 1.0
                ? SelectionHitKind.TrailingArea
                : SelectionHitKind.Text);
    }

    private int ResolveColumnFromDistance(string lineText, double distance, Typeface typeface)
    {
        if (string.IsNullOrEmpty(lineText) || distance <= 0)
            return 0;

        var fullWidth = ResolveDistanceFromColumn(lineText, lineText.Length, typeface);
        if (distance >= fullWidth)
            return lineText.Length;

        var low = 0;
        var high = lineText.Length;
        while (low < high)
        {
            var mid = (low + high) / 2;
            var midpoint = ResolveCharacterMidpoint(lineText, mid, typeface);
            if (distance < midpoint)
                high = mid;
            else
                low = mid + 1;
        }

        return low;
    }

    private double ResolveDistanceFromColumn(string lineText, int column, Typeface typeface)
    {
        if (string.IsNullOrEmpty(lineText) || column <= 0)
            return 0;

        var clampedColumn = Math.Clamp(column, 0, lineText.Length);
        // Hit-testing must use the same text geometry as DrawText, including trailing code whitespace.
        return BuildFormattedText(lineText[..clampedColumn], typeface).WidthIncludingTrailingWhitespace;
    }

    private double ResolveCharacterMidpoint(string lineText, int column, Typeface typeface)
    {
        var left = ResolveDistanceFromColumn(lineText, column, typeface);
        var right = ResolveDistanceFromColumn(lineText, column + 1, typeface);
        return left + ((right - left) / 2.0);
    }

    private double ResolveMinimumSelectionWidth()
    {
        var spaceWidth = _fontMetricsCache.GetMetrics(TextFontFamily, TextFontSize).SpaceWidth;
        return Math.Max(4.0, Math.Ceiling(spaceWidth));
    }

    private void UpdateSelectionActivePosition(SelectionPosition position)
    {
        if (_selectionActive == position)
            return;

        _selectionActive = position;
        InvalidateVisual();
        PreviewSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearSelectionState(bool invalidateVisual)
    {
        var hadState = _selectionAnchor is not null || _selectionActive is not null;
        _selectionAnchor = null;
        _selectionActive = null;
		_selectionOwnedBySearch = false;

        if (invalidateVisual && hadState)
            InvalidateVisual();

        if (hadState)
            PreviewSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool TryGetNormalizedSelection(out SelectionPosition start, out SelectionPosition end)
    {
        if (_selectionAnchor is not { } anchor || _selectionActive is not { } active)
        {
            start = default;
            end = default;
            return false;
        }

        if (anchor == active)
        {
            start = default;
            end = default;
            return false;
        }

        if (ComparePositions(anchor, active) <= 0)
        {
            start = anchor;
            end = active;
        }
        else
        {
            start = active;
            end = anchor;
        }

        return true;
    }

    private static int ComparePositions(SelectionPosition left, SelectionPosition right)
    {
        var lineComparison = left.Line.CompareTo(right.Line);
        return lineComparison != 0
            ? lineComparison
            : left.Column.CompareTo(right.Column);
    }

    private async Task CopySelectionToClipboardAsync()
    {
        var selectedText = BuildSelectedText(normalizeForClipboard: true);
        if (string.IsNullOrEmpty(selectedText))
            return;

        var copying = new CancelEventArgs();
        CopyingToClipboard?.Invoke(this, copying);
        if (copying.Cancel)
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;

        await clipboard.SetTextAsync(selectedText);
        CopiedToClipboard?.Invoke(this, EventArgs.Empty);
    }

    public void SelectAll()
    {
        var lineCount = ResolveLineCount();
        if (lineCount <= 0)
        {
            ClearSelection();
            return;
        }

        var lastLine = Math.Max(1, lineCount);
        var lastLineLength = GetLineText(lastLine).Length;
        if (lastLine == 1 && lastLineLength == 0)
        {
            ClearSelection();
            return;
        }

        _selectionAnchor = new SelectionPosition(1, 0);
        _selectionActive = new SelectionPosition(lastLine, lastLineLength);
		_selectionOwnedBySearch = false;
        InvalidateVisual();
        PreviewSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private string BuildSelectedText(bool normalizeForClipboard)
    {
        if (!TryGetNormalizedSelection(out var start, out var end))
            return string.Empty;

        string selectedText;
        if (start.Line == end.Line)
        {
            var lineText = GetLineText(start.Line);
            var startColumn = Math.Clamp(start.Column, 0, lineText.Length);
            var endColumn = Math.Clamp(end.Column, startColumn, lineText.Length);
            selectedText = endColumn > startColumn
                ? lineText[startColumn..endColumn]
                : string.Empty;

			return normalizeForClipboard
				? PreviewClipboardPayloadBuilder.BuildSelectionPayload(
					Document,
					start.Line,
					startColumn,
					end.Line,
					endColumn,
					selectedText)
				: selectedText;
        }

        var estimatedLineLength = Math.Max(12, Math.Min(Document?.MaxLineLength ?? _maxLineLength, 256));
        var builder = new StringBuilder((end.Line - start.Line + 1) * (estimatedLineLength + 1));

        for (var lineNumber = start.Line; lineNumber <= end.Line; lineNumber++)
        {
            var lineText = GetLineText(lineNumber);
            var segmentStart = lineNumber == start.Line
                ? Math.Clamp(start.Column, 0, lineText.Length)
                : 0;
            var segmentEnd = lineNumber == end.Line
                ? Math.Clamp(end.Column, segmentStart, lineText.Length)
                : lineText.Length;

            if (segmentEnd > segmentStart)
                builder.Append(lineText.AsSpan(segmentStart, segmentEnd - segmentStart));

            if (lineNumber < end.Line)
                builder.Append('\n');
        }

        selectedText = builder.ToString();
		return normalizeForClipboard
			? PreviewClipboardPayloadBuilder.BuildSelectionPayload(
				Document,
				start.Line,
				start.Column,
				end.Line,
				end.Column,
				selectedText)
			: selectedText;
    }

    private string GetLineText(int lineNumber)
    {
        if (Document is { } document)
            return document.GetLineText(lineNumber);

        var normalizedIndex = Math.Clamp(lineNumber, 1, _lineCount) - 1;
        return GetStringLineSpan(normalizedIndex).ToString();
    }

    private ReadOnlySpan<char> GetStringLineSpan(int lineIndex)
    {
        var text = Text ?? string.Empty;
        if (text.Length == 0 || lineIndex < 0 || lineIndex >= _lineStarts.Count)
            return ReadOnlySpan<char>.Empty;

        var start = _lineStarts[lineIndex];
        var nextStart = lineIndex + 1 < _lineStarts.Count
            ? _lineStarts[lineIndex + 1]
            : text.Length;

        var endExclusive = lineIndex + 1 < _lineStarts.Count
            ? Math.Max(start, nextStart - 1)
            : nextStart;

        if (endExclusive > start && text[endExclusive - 1] == '\r')
            endExclusive--;

        return endExclusive > start
            ? text.AsSpan(start, endExclusive - start)
            : ReadOnlySpan<char>.Empty;
    }

    private int ResolveLineCount() => Document?.LineCount ?? _lineCount;

    private void DrawVisibleSectionDividers(
        DrawingContext context,
        int firstVisibleLine,
        int lastVisibleLine,
        double lineHeight,
        double viewportTop)
    {
        if (SectionDividerBrush is null || Document?.Sections is not { Count: > 0 } sections)
            return;

        var firstSectionIndex = PreviewDocumentSectionLookup.FindFirstIntersectingSectionIndex(sections, firstVisibleLine);
        if (firstSectionIndex < 0)
            return;

        var right = Math.Max(LeftPadding + 24.0, Bounds.Width - RightPadding);
        if (right <= LeftPadding)
            return;

        var dividerPen = new Pen(SectionDividerBrush, 1.25);
        var dividerOffset = Math.Max(2.0, Math.Floor(lineHeight * 0.35));

        for (var i = firstSectionIndex; i < sections.Count; i++)
        {
            var section = sections[i];
            if (section.StartLine > lastVisibleLine)
                break;

            if (section.StartLine <= 1)
                continue;

            var y = ResolveContentTopPadding() +
                    ((section.HeaderLine - 1) * lineHeight) -
                    viewportTop -
                    dividerOffset;
            context.DrawLine(dividerPen, new Point(LeftPadding, y), new Point(right, y));
        }
    }

    private static int ResolveLineNumberAtOffset(
        double verticalOffset,
        double contentTopPadding,
        double lineHeight,
        int lineCount)
    {
        if (!double.IsFinite(verticalOffset) || lineHeight <= 0 || lineCount <= 1)
            return 1;

        var rawLineNumber = Math.Floor((Math.Max(0, verticalOffset) - contentTopPadding) / lineHeight) + 1;
        if (rawLineNumber <= 1)
            return 1;

        if (rawLineNumber >= lineCount)
            return lineCount;

        return (int)rawLineNumber;
    }

    internal static double CalculateViewportRelativeLineOriginY(
        int firstVisibleLine,
        double contentTopPadding,
        double lineHeight,
        double viewportTop) =>
        contentTopPadding + ((Math.Max(1, firstVisibleLine) - 1) * lineHeight) - viewportTop;

    private void DrawStickyHeader(DrawingContext context, Typeface typeface)
    {
        var headerHeight = ResolveStickyHeaderHeight();
        if (headerHeight <= 0)
            return;

        var left = Math.Max(0, HorizontalOffset);
        var top = Math.Floor(Math.Max(0, VerticalOffset));
        var headerWidth = ViewportWidth > 0 ? ViewportWidth : Bounds.Width;
        var headerBounds = new Rect(left, top, headerWidth, headerHeight);
        if (headerBounds.Width <= 0 || headerBounds.Height <= 0)
            return;

        if (StickyHeaderBackgroundBrush is not null)
            context.FillRectangle(StickyHeaderBackgroundBrush, headerBounds);

        if (StickyHeaderBorderBrush is not null)
        {
            var borderPen = new Pen(StickyHeaderBorderBrush, 1);
            var borderY = top + headerHeight - 0.5;
            context.DrawLine(borderPen, new Point(left, borderY), new Point(left + headerWidth, borderY));
        }

        var textWidth = Math.Max(0, headerWidth - LeftPadding - RightPadding);
        if (!StickyHeaderVisible || textWidth <= 1 || string.IsNullOrWhiteSpace(StickyHeaderText))
            return;

        var headerText = TrimStickyHeaderText(StickyHeaderText, textWidth, typeface);
        var formattedText = BuildFormattedText(headerText, typeface);
        var textY = top + Math.Max(3.0, Math.Floor((headerHeight - formattedText.Height) / 2.0));
        context.DrawText(formattedText, new Point(left + LeftPadding, textY));
    }

    private double ResolveStickyHeaderHeight()
    {
        if (!StickyHeaderReserved)
            return 0;

        return Math.Max(24.0, Math.Ceiling(TextFontSize + 12.0));
    }

    private double ResolveContentTopPadding() => TopPadding + ResolveStickyHeaderHeight();

    private string TrimStickyHeaderText(string text, double availableWidth, Typeface typeface)
    {
        if (string.IsNullOrEmpty(text) || availableWidth <= 0)
            return string.Empty;

        var cacheKey = StickyHeaderTrimCacheKey.Create(text, availableWidth, TextFontFamily, TextFontSize);
        if (_cachedStickyHeaderTrimKey == cacheKey && _cachedStickyHeaderTrimText is not null)
            return _cachedStickyHeaderTrimText;

        if (BuildFormattedText(text, typeface).Width <= availableWidth)
        {
            CacheStickyHeaderTrim(cacheKey, text);
            return text;
        }

        const string ellipsis = "...";
        if (BuildFormattedText(ellipsis, typeface).Width > availableWidth)
        {
            CacheStickyHeaderTrim(cacheKey, string.Empty);
            return string.Empty;
        }

        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            var candidate = text[..mid] + ellipsis;
            if (BuildFormattedText(candidate, typeface).Width <= availableWidth)
                low = mid;
            else
                high = mid - 1;
        }

        var trimmedText = low <= 0 ? ellipsis : text[..low] + ellipsis;
        CacheStickyHeaderTrim(cacheKey, trimmedText);
        return trimmedText;
    }

    private void CacheStickyHeaderTrim(StickyHeaderTrimCacheKey key, string text)
    {
        _cachedStickyHeaderTrimKey = key;
        _cachedStickyHeaderTrimText = text;
    }

    private Typeface ResolveTypeface() =>
        new(TextFontFamily ?? FontFamily.Default, FontStyle.Normal, FontWeight.Normal);

    private double CalculateRequiredWidth()
    {
        var glyphWidth = _fontMetricsCache.GetMetrics(TextFontFamily, TextFontSize).WideGlyphWidth;
        var contentWidth = (Document?.MaxLineLength ?? _maxLineLength) * glyphWidth;
        return Math.Ceiling(LeftPadding + contentWidth + RightPadding);
    }

    private double ResolveLineHeight()
    {
        return _fontMetricsCache.GetMetrics(TextFontFamily, TextFontSize).LineHeight;
    }

    private FormattedText BuildFormattedText(string text, Typeface typeface)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            TextFontSize,
            TextBrush ?? Brushes.White);
    }

    private void EnsureContextMenu()
    {
		if (_contextFlyout is not null)
            return;

		_secretHideHereMenuItem = CreateContextMenuItem(OnSecretHideHereMenuItemClick);
		_secretAlwaysHideMenuItem = CreateContextMenuItem(OnSecretAlwaysHideMenuItemClick);
		_privateDataAlwaysHideMenuItem = CreateContextMenuItem(OnPrivateDataAlwaysHideMenuItemClick);
		_removeSecretMarkMenuItem = CreateContextMenuItem(OnRemoveSecretMarkMenuItemClick);
		_bulkRuleRedactionMenuItem = CreateContextMenuItem(OnBulkRuleRedactionMenuItemClick);
		_bulkFileRedactionMenuItem = CreateContextMenuItem(OnBulkFileRedactionMenuItemClick);
		_manualRedactionSeparator = new Separator { Cursor = PreviewMenuCursor };
		_bulkRedactionSeparator = new Separator { Cursor = PreviewMenuCursor };
		ToolTip.SetShowOnDisabled(_secretHideHereMenuItem, true);
		ToolTip.SetShowOnDisabled(_secretAlwaysHideMenuItem, true);
		ToolTip.SetShowOnDisabled(_privateDataAlwaysHideMenuItem, true);

		_copyMenuItem = CreateContextMenuItem(OnCopyMenuItemClick);

		_selectAllMenuItem = CreateContextMenuItem(OnSelectAllMenuItemClick);

		_contextFlyout = new MenuFlyout();
		_contextFlyout.Items.Add(_copyMenuItem);
		_contextFlyout.Items.Add(_selectAllMenuItem);
		_contextFlyout.Items.Add(_manualRedactionSeparator);
		_contextFlyout.Items.Add(_secretHideHereMenuItem);
		_contextFlyout.Items.Add(_secretAlwaysHideMenuItem);
		_contextFlyout.Items.Add(_privateDataAlwaysHideMenuItem);
		_contextFlyout.Items.Add(_removeSecretMarkMenuItem);
		_contextFlyout.Items.Add(_bulkRedactionSeparator);
		_contextFlyout.Items.Add(_bulkRuleRedactionMenuItem);
		_contextFlyout.Items.Add(_bulkFileRedactionMenuItem);
		_contextFlyout.Opening += OnContextMenuOpening;
		_contextFlyout.Opened += OnContextMenuOpened;
		ContextFlyout = _contextFlyout;

        UpdateContextMenuHeaders();
    }

	private static MenuItem CreateContextMenuItem(EventHandler<RoutedEventArgs> clickHandler)
	{
		var item = new MenuItem { Cursor = PreviewMenuCursor };
		item.Click += clickHandler;
		return item;
	}

    private void UpdateContextMenuHeaders()
    {
        if (_copyMenuItem is not null)
            _copyMenuItem.Header = CopyMenuHeader;

        if (_selectAllMenuItem is not null)
            _selectAllMenuItem.Header = SelectAllMenuHeader;

		if (_removeSecretMarkMenuItem is not null)
			_removeSecretMarkMenuItem.Header = RemoveSecretMarkHeader;

		PrepareBulkSecretMenuItems();
    }

    private void OpenContextMenu()
    {
        EnsureContextMenu();
		_contextFlyout?.ShowAt(this, showAtPointer: true);
    }

    private void CloseContextMenu()
    {
		_contextFlyout?.Hide();
    }

	private void OnContextMenuOpening(object? sender, EventArgs e)
	{
		PrepareManualSecretMenuItems();
		PrepareBulkSecretMenuItems();

        if (_copyMenuItem is not null)
            _copyMenuItem.IsEnabled = HasSelection;

		if (_selectAllMenuItem is not null)
			_selectAllMenuItem.IsEnabled = ResolveLineCount() > 0 && (ResolveLineCount() > 1 || GetLineText(1).Length > 0);

		UpdateContextMenuSeparatorVisibility();
	}

	private void OnContextMenuOpened(object? sender, EventArgs e)
	{
		if (DataContext is not MainWindowViewModel viewModel)
			return;

		PopupBackdropConfigurator.TryApply(
			_copyMenuItem,
			TopLevel.GetTopLevel(this),
			viewModel.ActiveThemeEffect,
			PopupBackdropTransparencyFallback.Transparent);
	}

    private void OnCopyMenuItemClick(object? sender, RoutedEventArgs e)
    {
        _ = CopySelectionToClipboardAsync();
    }

    private void OnSelectAllMenuItemClick(object? sender, RoutedEventArgs e)
    {
        SelectAll();
    }

	private void OnSecretHideHereMenuItemClick(object? sender, RoutedEventArgs e) =>
		RaiseManualSecretMarkRequested(ManualRedactionClass.Secret, persistent: false);

	private void OnSecretAlwaysHideMenuItemClick(object? sender, RoutedEventArgs e) =>
		RaiseManualSecretMarkRequested(ManualRedactionClass.Secret, persistent: true);

	private void OnPrivateDataAlwaysHideMenuItemClick(object? sender, RoutedEventArgs e) =>
		RaiseManualSecretMarkRequested(ManualRedactionClass.PrivateData, persistent: true);

	private void OnRemoveSecretMarkMenuItemClick(object? sender, RoutedEventArgs e)
	{
		if (_contextManualRedaction is not { } redaction || !HasManualMarkIdentity(redaction))
			return;
		ManualSecretUnmarkRequested?.Invoke(
			this,
			new PreviewManualSecretUnmarkRequestedEventArgs(
				redaction.PersistentMarkHash,
				redaction.SourceLength,
				redaction.SessionMarkId,
				redaction.Source.HasFlag(SecretFindingSource.Detector),
				redaction.PersistentMarkId));
	}

	private void OnBulkRuleRedactionMenuItemClick(object? sender, RoutedEventArgs e) =>
		RaiseBulkRedactionToggleRequested(_contextRuleOccurrenceIds);

	private void OnBulkFileRedactionMenuItemClick(object? sender, RoutedEventArgs e) =>
		RaiseBulkRedactionToggleRequested(_contextFileOccurrenceIds);

	private void RaiseBulkRedactionToggleRequested(IReadOnlyCollection<string> occurrenceIds)
	{
		if (occurrenceIds.Count == 0)
			return;

		BulkRedactionToggleRequested?.Invoke(
			this,
			new PreviewBulkRedactionToggleRequestedEventArgs(occurrenceIds, _contextBulkKeep));
	}

	private void RaiseManualSecretMarkRequested(
		ManualRedactionClass classification,
		bool persistent)
	{
		if (_contextMarkedSecret is null || _contextSelectionRange.IsCollapsed)
		{
			ManualSecretMarkRejected?.Invoke(
				this,
				new PreviewManualSecretMarkRejectedEventArgs(
					_contextSecretMarkRejectionMessage ?? SecretSelectionContentOnly));
			return;
		}
		ManualSecretMarkRequested?.Invoke(
			this,
			new PreviewManualSecretMarkRequestedEventArgs(
				_contextMarkedSecret,
				_contextSelectionRange,
				classification,
				persistent));
	}

	private void PrepareContextSelection(Point point)
	{
		_contextManualRedaction = null;
		_contextDetectorRedaction = null;
		var hit = HitTestSelectionPosition(point);
		if (TryGetRedactionAt(hit, out var redaction))
		{
			if (redaction.Source.HasFlag(SecretFindingSource.Detector))
				_contextDetectorRedaction = redaction;
			if (HasManualMarkIdentity(redaction))
			{
				_contextManualRedaction = redaction;
			}
			else
			{
				ClearSelection();
			}
			return;
		}

		if (TryGetNormalizedSelection(out var selectionStart, out var selectionEnd) &&
		    IsWithinSelection(hit, selectionStart, selectionEnd))
		{
			return;
		}

		SelectTokenAt(hit);
	}

	private void PrepareBulkSecretMenuItems()
	{
		if (_bulkRedactionSeparator is null ||
		    _bulkRuleRedactionMenuItem is null ||
		    _bulkFileRedactionMenuItem is null)
		{
			return;
		}

		_contextRuleOccurrenceIds = [];
		_contextFileOccurrenceIds = [];
		var visible = _contextDetectorRedaction is not null && Document is not null;
		_bulkRuleRedactionMenuItem.IsVisible = visible;
		_bulkFileRedactionMenuItem.IsVisible = visible;
		if (!visible)
			return;

		var target = _contextDetectorRedaction!;
		(_contextRuleOccurrenceIds, _contextFileOccurrenceIds) = CollectBulkOccurrenceIds(target);
		_contextBulkKeep = target.State == SecretPreviewSpanState.Redacted;
		_bulkRuleRedactionMenuItem.Header = string.Format(
			CultureInfo.CurrentCulture,
			_contextBulkKeep ? KeepAllRuleOccurrencesFormat : HideAllRuleOccurrencesFormat,
			target.RuleId,
			_contextRuleOccurrenceIds.Count);
		_bulkFileRedactionMenuItem.Header = string.Format(
			CultureInfo.CurrentCulture,
			_contextBulkKeep ? KeepAllFileOccurrencesFormat : HideAllFileOccurrencesFormat,
			GetRelativeFileName(target.RelativePath),
			_contextFileOccurrenceIds.Count);
	}

	private (IReadOnlyCollection<string> Rule, IReadOnlyCollection<string> File)
		CollectBulkOccurrenceIds(PreviewRedactionSpan target)
	{
		if (Document is not { } document)
			return ([], []);

		var ruleOccurrenceIds = new HashSet<string>(StringComparer.Ordinal);
		var fileOccurrenceIds = new HashSet<string>(StringComparer.Ordinal);
		foreach (var span in document.Redactions)
		{
			if (!span.Source.HasFlag(SecretFindingSource.Detector))
				continue;

			if (string.Equals(span.RuleId, target.RuleId, StringComparison.Ordinal))
				ruleOccurrenceIds.Add(span.OccurrenceId);
			if (string.Equals(span.RelativePath, target.RelativePath, StringComparison.Ordinal))
				fileOccurrenceIds.Add(span.OccurrenceId);
		}

		return (ruleOccurrenceIds, fileOccurrenceIds);
	}

	private static string GetRelativeFileName(string relativePath)
	{
		var separatorIndex = relativePath.LastIndexOfAny('/', '\\');
		return separatorIndex >= 0 && separatorIndex < relativePath.Length - 1
			? relativePath[(separatorIndex + 1)..]
			: relativePath;
	}

	private void PrepareManualSecretMenuItems()
	{
		if (_secretHideHereMenuItem is null ||
		    _secretAlwaysHideMenuItem is null ||
		    _privateDataAlwaysHideMenuItem is null ||
		    _removeSecretMarkMenuItem is null)
		{
			return;
		}

		var removeVisible = _contextManualRedaction is { } redaction && HasManualMarkIdentity(redaction);
		_removeSecretMarkMenuItem.IsVisible = removeVisible;
		_secretHideHereMenuItem.IsVisible = false;
		_secretAlwaysHideMenuItem.IsVisible = false;
		_privateDataAlwaysHideMenuItem.IsVisible = false;
		_contextMarkedSecret = null;
		_contextSelectionRange = default;
		_contextSecretMarkRejectionMessage = null;
		if (removeVisible || !HasSelection)
			return;

		var selectedText = GetSelectedText();
		var hasRange = TryGetSelectionRange(out _contextSelectionRange);
		var isContentSelection = hasRange && IsFileContentSelection(_contextSelectionRange);
		if (!isContentSelection)
			return;
		_secretHideHereMenuItem.IsVisible = true;
		_secretAlwaysHideMenuItem.IsVisible = true;
		_privateDataAlwaysHideMenuItem.IsVisible = true;
		var isValid = MarkedSecretValueNormalizer.TryCreate(
			selectedText,
			out var candidate,
			out var validationError);
		if (isValid)
			_contextMarkedSecret = candidate;

		var displayValue = (isValid ? candidate.NormalizedValue : selectedText.Trim())
			.Replace('\r', ' ')
			.Replace('\n', ' ');
		var masked = MaskSecretValue(displayValue);
		_secretHideHereMenuItem.Header = string.Format(
			CultureInfo.CurrentCulture,
			HideSecretHereFormat,
			masked);
		_secretAlwaysHideMenuItem.Header = string.Format(
			CultureInfo.CurrentCulture,
			AlwaysHideSecretFormat,
			masked);
		_privateDataAlwaysHideMenuItem.Header = string.Format(
			CultureInfo.CurrentCulture,
			PrivateDataAlwaysHideFormat,
			masked);

		var enabled = isValid;
		_secretHideHereMenuItem.IsEnabled = enabled;
		_secretAlwaysHideMenuItem.IsEnabled = enabled;
		_privateDataAlwaysHideMenuItem.IsEnabled = enabled;
		var reason = GetValidationMessage(validationError);
		_contextSecretMarkRejectionMessage = enabled ? null : reason;
		ToolTip.SetTip(_secretHideHereMenuItem, enabled ? HideHereSecretToolTip : reason);
		ToolTip.SetTip(_secretAlwaysHideMenuItem, enabled ? AlwaysHideValueToolTip : reason);
		ToolTip.SetTip(_privateDataAlwaysHideMenuItem, enabled ? PrivateDataAlwaysHideToolTip : reason);
	}

	private void UpdateContextMenuSeparatorVisibility()
	{
		if (_contextFlyout is null)
			return;

		var hasVisibleItemInGroup = false;
		for (var index = _contextFlyout.Items.Count - 1; index >= 0; index--)
		{
			switch (_contextFlyout.Items[index])
			{
				case MenuItem menuItem:
					hasVisibleItemInGroup |= menuItem.IsVisible;
					break;
				case Separator separator:
					separator.IsVisible = hasVisibleItemInGroup;
					hasVisibleItemInGroup = false;
					break;
			}
		}
	}

	private static bool HasManualMarkIdentity(PreviewRedactionSpan redaction) =>
		redaction.PersistentMarkHash is { Length: > 0 } ||
		redaction.SessionMarkId is { Length: > 0 };

	private bool IsFileContentSelection(PreviewSelectionRange selection)
	{
		if (Document is not { } document)
			return false;
		var section = PreviewDocumentSectionLookup.FindContainingSection(
			document.Sections,
			selection.StartLine);
		return section is not null &&
		       selection.StartLine >= section.ContentStartLine &&
		       selection.EndLine >= section.ContentStartLine &&
		       selection.EndLine <= section.EndLine;
	}

	private string GetValidationMessage(MarkedSecretValidationError error) => error switch
	{
		MarkedSecretValidationError.TooLong => SecretSelectionTooLong,
		MarkedSecretValidationError.Multiline => SecretSelectionMultiline,
		_ => SecretSelectionTooShort
	};

	private void SelectTokenAt(SelectionPosition position)
	{
		var line = GetLineText(position.Line);
		if (line.Length == 0)
		{
			ClearSelection();
			return;
		}

		var column = Math.Clamp(position.Column, 0, line.Length - 1);
		if (!IsSecretSelectionCharacter(line[column]) &&
		    column > 0 &&
		    IsSecretSelectionCharacter(line[column - 1]))
		{
			column--;
		}
		if (!IsSecretSelectionCharacter(line[column]))
		{
			ClearSelection();
			return;
		}

		var start = column;
		while (start > 0 && IsSecretSelectionCharacter(line[start - 1]))
			start--;
		var end = column + 1;
		while (end < line.Length && IsSecretSelectionCharacter(line[end]))
			end++;

		_selectionAnchor = new SelectionPosition(position.Line, start);
		_selectionActive = new SelectionPosition(position.Line, end);
		_selectionOwnedBySearch = false;
		InvalidateVisual();
		PreviewSelectionChanged?.Invoke(this, EventArgs.Empty);
	}

	private static bool IsWithinSelection(
		SelectionPosition position,
		SelectionPosition start,
		SelectionPosition end) =>
		ComparePositions(position, start) >= 0 && ComparePositions(position, end) < 0;

	private static bool IsSecretSelectionCharacter(char character) =>
		!char.IsWhiteSpace(character) && character is not ('\'' or '"' or '`' or '=' or ',' or ';' or
			'(' or ')' or '[' or ']' or '{' or '}' or '<' or '>');

	private static string MaskSecretValue(string value)
	{
		var elements = StringInfo.GetTextElementEnumerator(value);
		var graphemes = new List<string>();
		while (elements.MoveNext())
			graphemes.Add(elements.GetTextElement());
		if (graphemes.Count == 0)
			return string.Empty;

		var leadingCount = Math.Min(8, Math.Max(2, graphemes.Count - 6));
		var trailingCount = Math.Min(4, Math.Max(2, graphemes.Count - leadingCount - 2));
		if (leadingCount + trailingCount >= graphemes.Count)
		{
			leadingCount = Math.Max(1, graphemes.Count / 2 - 1);
			trailingCount = Math.Max(1, graphemes.Count - leadingCount - 2);
		}
		return string.Concat(graphemes.Take(leadingCount)) + "…" +
		       string.Concat(graphemes.Skip(graphemes.Count - trailingCount));
	}

    private static IBrush EnsureOpaqueSelectionBrush(IBrush brush)
    {
        if (brush is not ISolidColorBrush solidBrush)
            return brush;

        var color = solidBrush.Color;
        return new SolidColorBrush(
            Color.FromArgb(byte.MaxValue, color.R, color.G, color.B),
            1.0);
    }

    private void ResetVisibleWindowCache()
    {
        _cachedVisibleWindow = null;
        _cachedVisibleWindowDocument = null;
        _cachedVisibleWindowFirstLine = 0;
        _cachedVisibleWindowLastLine = 0;
        ClearFormattedLineCache();
        _formattedLineCacheFontFamilyName = null;
        _formattedLineCacheCultureName = null;
        _formattedLineCacheFontSize = double.NaN;
        _formattedLineCacheBrush = null;
    }

    private ScrollViewer? GetOwnerScrollViewer()
    {
        if (_ownerScrollViewer is not null)
            return _ownerScrollViewer;

        foreach (var visual in this.GetVisualAncestors())
        {
            if (visual is not ScrollViewer scrollViewer)
                continue;

            _ownerScrollViewer = scrollViewer;
            break;
        }

        return _ownerScrollViewer;
    }

    private void CaptureSelectionPointer(PointerEventArgs e)
    {
        var scrollViewer = GetOwnerScrollViewer();
        _selectionPointerViewportPoint = scrollViewer is not null
            ? e.GetPosition(scrollViewer)
            : e.GetPosition(this);
    }

    private bool TryStartSelection(IPointer pointer, Point documentPoint, KeyModifiers keyModifiers)
    {
        var previousAnchor = _selectionAnchor;
        var previousActive = _selectionActive;
		_selectionOwnedBySearch = false;
        var hit = HitTestSelection(documentPoint);
        if (!keyModifiers.HasFlag(KeyModifiers.Shift) &&
            (hit.Kind == SelectionHitKind.Empty ||
             (hit.Kind == SelectionHitKind.TrailingArea && HasSelection)))
        {
            if (HasSelection)
                ClearSelection();

            return false;
        }

        var selectionPosition = hit.Position;
        if (!keyModifiers.HasFlag(KeyModifiers.Shift) || _selectionAnchor is null)
        {
            _selectionAnchor = selectionPosition;
        }

        UpdateSelectionActivePosition(selectionPosition);
        _isSelecting = true;
        pointer.Capture(this);
        UpdateSelectionAutoScrollState();

        if (previousAnchor != _selectionAnchor && previousActive == _selectionActive)
            PreviewSelectionChanged?.Invoke(this, EventArgs.Empty);

        return true;
    }

    private void UpdateSelectionAutoScrollState()
    {
        if (!_isSelecting)
        {
            StopSelectionAutoScroll();
            return;
        }

        var scrollViewer = GetOwnerScrollViewer();
        if (scrollViewer is null || scrollViewer.Extent.Height <= scrollViewer.Viewport.Height)
        {
            StopSelectionAutoScroll();
            return;
        }

        var viewportHeight = scrollViewer.Viewport.Height;
        if (viewportHeight <= 0)
        {
            StopSelectionAutoScroll();
            return;
        }

        var pointerY = _selectionPointerViewportPoint.Y;
        var shouldScrollUp = pointerY < AutoScrollEdgeThreshold;
        var shouldScrollDown = pointerY > viewportHeight - AutoScrollEdgeThreshold;
        if (!shouldScrollUp && !shouldScrollDown)
        {
            StopSelectionAutoScroll();
            return;
        }

        if (_selectionAutoScrollTimer is null)
        {
            _selectionAutoScrollTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = AutoScrollTickInterval
            };
            _selectionAutoScrollTimer.Tick += OnSelectionAutoScrollTick;
        }

        if (!_selectionAutoScrollTimer.IsEnabled)
            _selectionAutoScrollTimer.Start();
    }

    private void StopSelectionAutoScroll()
    {
        _selectionAutoScrollTimer?.Stop();
    }

    private void ReleaseSelectionAutoScrollTimer()
    {
        var timer = _selectionAutoScrollTimer;
        if (timer is null)
            return;

        timer.Stop();
        timer.Tick -= OnSelectionAutoScrollTick;
        _selectionAutoScrollTimer = null;
    }

    private void ReleaseOversizedLineMetadataCapacity()
    {
        // String previews need one offset per line, but file-backed documents carry their own
        // index. Do not retain a large List<int> backing array after switching or clearing.
        if (_lineStarts.Capacity > MaxRetainedLineMetadataCapacity)
            _lineStarts.TrimExcess();
    }

    private void OnSelectionAutoScrollTick(object? sender, EventArgs e)
    {
        if (!_isSelecting)
        {
            StopSelectionAutoScroll();
            return;
        }

        var scrollViewer = GetOwnerScrollViewer();
        if (scrollViewer is null)
        {
            StopSelectionAutoScroll();
            return;
        }

        var viewportHeight = scrollViewer.Viewport.Height;
        if (viewportHeight <= 0)
        {
            StopSelectionAutoScroll();
            return;
        }

        var deltaY = 0.0;
        if (_selectionPointerViewportPoint.Y < AutoScrollEdgeThreshold)
        {
            deltaY = -CalculateAutoScrollDelta(AutoScrollEdgeThreshold - _selectionPointerViewportPoint.Y);
        }
        else if (_selectionPointerViewportPoint.Y > viewportHeight - AutoScrollEdgeThreshold)
        {
            deltaY = CalculateAutoScrollDelta(_selectionPointerViewportPoint.Y - (viewportHeight - AutoScrollEdgeThreshold));
        }

        if (Math.Abs(deltaY) < 0.1)
        {
            StopSelectionAutoScroll();
            return;
        }

        var maxVerticalOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var nextVerticalOffset = Math.Clamp(scrollViewer.Offset.Y + deltaY, 0, maxVerticalOffset);
        if (Math.Abs(nextVerticalOffset - scrollViewer.Offset.Y) < 0.1)
        {
            StopSelectionAutoScroll();
            return;
        }

        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, nextVerticalOffset);
        VerticalOffset = nextVerticalOffset;
        var documentPoint = new Point(
            scrollViewer.Offset.X + _selectionPointerViewportPoint.X,
            nextVerticalOffset + _selectionPointerViewportPoint.Y);
        UpdateSelectionActivePosition(HitTestSelectionPosition(documentPoint));
    }

    private static double CalculateAutoScrollDelta(double overshoot)
    {
        var normalizedOvershoot = Math.Clamp(overshoot, 0, AutoScrollEdgeThreshold);
        return Math.Max(4.0, normalizedOvershoot * 0.65);
    }

    private void EndSelectionCapture(IPointer pointer)
    {
        _isSelecting = false;
        StopSelectionAutoScroll();
        if (pointer.Captured == this)
            pointer.Capture(null);
    }

    private readonly record struct SelectionHitResult(SelectionPosition Position, SelectionHitKind Kind);
	private readonly record struct RedactionNavigationTarget(
		string OccurrenceId,
		int LineNumber,
		int StartColumn);

    private readonly record struct SelectionPosition(int Line, int Column);
    private sealed record FormattedLineCacheEntry(
        string Text,
        FormattedText FormattedText);
    private readonly record struct StickyHeaderTrimCacheKey(
        string Text,
        double AvailableWidth,
        string FontFamilyName,
        double FontSize,
        string CultureName)
    {
        public static StickyHeaderTrimCacheKey Create(
            string text,
            double availableWidth,
            FontFamily? fontFamily,
            double fontSize)
        {
            var resolvedFamily = fontFamily ?? FontFamily.Default;
            return new StickyHeaderTrimCacheKey(
                text,
                availableWidth,
                resolvedFamily.Name,
                fontSize,
                CultureInfo.CurrentUICulture.Name);
        }
    }

    private enum SelectionHitKind
    {
        Empty = 0,
        Text = 1,
        TrailingArea = 2
    }

    private sealed class VisibleTextWindow(int firstLine, int lastLine, string text)
    {
        private readonly int[] _lineStarts = BuildLineStarts(firstLine, lastLine, text);

        public int FirstLine { get; } = firstLine;

        public int LastLine { get; } = lastLine;

        public string Text { get; } = text;

        public int LineCount => _lineStarts.Length;

        public ReadOnlySpan<char> GetLineSpan(int lineIndex)
        {
            var normalizedLineIndex = Math.Clamp(
                lineIndex,
                0,
                _lineStarts.Length - 1);
            var lineStart = _lineStarts[normalizedLineIndex];
            var lineEnd = normalizedLineIndex + 1 < _lineStarts.Length
                ? Math.Max(
                    lineStart,
                    _lineStarts[normalizedLineIndex + 1] - 1)
                : Text.Length;
            if (lineEnd > lineStart && Text[lineEnd - 1] == '\r')
                lineEnd--;

            return Text.AsSpan(lineStart, Math.Max(0, lineEnd - lineStart));
        }

        public SelectionPosition Clamp(SelectionPosition position)
        {
            var clampedLine = Math.Clamp(position.Line, FirstLine, LastLine);
            var clampedColumn = Math.Clamp(position.Column, 0, GetLineLength(clampedLine));
            return new SelectionPosition(clampedLine, clampedColumn);
        }

        public int GetLineLength(int lineNumber)
        {
            var lineIndex = Math.Clamp(lineNumber - FirstLine, 0, _lineStarts.Length - 1);
            return GetLineSpan(lineIndex).Length;
        }

        public int GetLocalTextIndex(int lineNumber, int column)
        {
            var lineIndex = Math.Clamp(lineNumber - FirstLine, 0, _lineStarts.Length - 1);
            var lineStart = _lineStarts[lineIndex];
            var clampedColumn = Math.Clamp(column, 0, GetLineLength(lineNumber));
            return lineStart + clampedColumn;
        }

        private static int[] BuildLineStarts(int firstLine, int lastLine, string text)
        {
            var lineCount = Math.Max(1, lastLine - firstLine + 1);
            var lineStarts = new int[lineCount];
            var currentLine = 0;

            for (var i = 0; i < text.Length && currentLine + 1 < lineStarts.Length; i++)
            {
                if (text[i] != '\n')
                    continue;

                currentLine++;
                lineStarts[currentLine] = i + 1;
            }

            return lineStarts;
        }
    }
}
