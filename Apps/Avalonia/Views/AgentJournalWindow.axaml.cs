using Avalonia.Platform.Storage;
using DevProjex.Avalonia.Services;
using DevProjex.Avalonia.ViewModels;
using DevProjex.Infrastructure.AgentJournal;
using DevProjex.Kernel.Abstractions;
using DevProjex.Kernel.Models;

namespace DevProjex.Avalonia.Views;

internal partial class AgentJournalWindow : Window
{
    internal const double MinimumWindowWidth = 900;
    internal const double MinimumWindowHeight = 560;
    private const double OwnerSizeRatio = 0.7;
    private static readonly TimeSpan SessionRefreshInterval = TimeSpan.FromSeconds(5);
    private readonly IAgentJournalReader _reader;
    private readonly IAgentJournalReceiptFormatter _formatter;
    private readonly LocalizationService _localization;
    private readonly Window? _owner;
    private readonly string? _currentProjectRoot;
    private readonly AgentJournalWindowViewModel _viewModel;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _watchSession;
    private readonly DispatcherTimer _refreshTimer;
    private bool _loaded;

    public AgentJournalWindow()
        : this(
            null,
            new AgentJournalStore(),
            new AgentJournalReceiptFormatter(),
            new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En),
            currentProjectRoot: null)
    {
    }

    public AgentJournalWindow(
        IAgentJournalReader reader,
        IAgentJournalReceiptFormatter formatter,
        LocalizationService localization,
        string? currentProjectRoot)
        : this(null, reader, formatter, localization, currentProjectRoot)
    {
    }

    public AgentJournalWindow(
        Window? owner,
        IAgentJournalReader reader,
        IAgentJournalReceiptFormatter formatter,
        LocalizationService localization,
        string? currentProjectRoot)
    {
        _owner = owner;
        _reader = reader;
        _formatter = formatter;
        _localization = localization;
        _currentProjectRoot = string.IsNullOrWhiteSpace(currentProjectRoot)
            ? null
            : Path.GetFullPath(currentProjectRoot);
        _viewModel = new AgentJournalWindowViewModel(localization, _currentProjectRoot is not null);
        DataContext = _viewModel;
        InitializeComponent();
        ApplyInitialSize();
        ApplyDialogSurface();

        _refreshTimer = new DispatcherTimer { Interval = SessionRefreshInterval };
        _refreshTimer.Tick += OnRefreshTimerTick;
        Opened += OnOpened;
        Closed += OnClosed;
        _localization.LanguageChanged += OnLanguageChanged;
        if (_owner is not null)
            _owner.ActualThemeVariantChanged += OnActualThemeVariantChanged;
        else if (global::Avalonia.Application.Current is { } application)
            application.ActualThemeVariantChanged += OnActualThemeVariantChanged;
    }

    internal AgentJournalWindowViewModel ViewModel => _viewModel;

    internal async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        _viewModel.IsLoading = true;
        try
        {
            var root = _viewModel.CurrentProjectOnly ? _currentProjectRoot : null;
            var sessions = await _reader.ListSessionsAsync(root, cancellationToken: cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var rows = sessions
                .OrderByDescending(static session => session.StartedUtc)
                .Select(session => CreateSessionRow(session, now))
                .ToArray();
            _viewModel.ReplaceSessions(rows);
            await LoadSelectedSessionAsync(cancellationToken);
        }
        finally
        {
            _viewModel.IsLoading = false;
        }
    }

    internal async Task ExportSelectedToPathAsync(
        string path,
        bool json,
        CancellationToken cancellationToken = default)
    {
        var session = _viewModel.SelectedSession?.Session;
        if (session is null)
            return;
        var receipt = await _reader.ReadReceiptAsync(session.Id, cancellationToken);
        if (receipt is null)
            return;
        var text = json ? _formatter.FormatJson(receipt) : _formatter.FormatMarkdown(receipt);
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
    }

    internal async Task<int> ClearCurrentScopeAsync(CancellationToken cancellationToken = default)
    {
        var root = _viewModel.CurrentProjectOnly ? _currentProjectRoot : null;
        var removed = await _reader.ClearAsync(root, cancellationToken);
        await RefreshAsync(cancellationToken);
        return removed;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_loaded)
            return;
        _loaded = true;
        ApplyDialogSurface();
        _refreshTimer.Start();
        await RefreshSafelyAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTimerTick;
        _watchSession?.Cancel();
        _watchSession?.Dispose();
        _watchSession = null;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _localization.LanguageChanged -= OnLanguageChanged;
        if (_owner is not null)
            _owner.ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        else if (global::Avalonia.Application.Current is { } application)
            application.ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        Opened -= OnOpened;
        Closed -= OnClosed;
    }

    private async void OnRefreshTimerTick(object? sender, EventArgs e) => await RefreshSafelyAsync();

    private async void OnProjectFilterChanged(object? sender, RoutedEventArgs e)
    {
        if (!_loaded)
            return;
        await RefreshSafelyAsync();
    }

    private async void OnSessionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_loaded)
            return;
        await LoadSelectedSessionSafelyAsync();
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedSession is null)
            return;
        var markdown = new FilePickerFileType("Markdown") { Patterns = ["*.md"] };
        var json = new FilePickerFileType("JSON") { Patterns = ["*.json"] };
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = _localization["AgentJournal.Export"],
            SuggestedFileName = $"devprojex-journal-{_viewModel.SelectedSession.Session.Id}",
            DefaultExtension = "md",
            FileTypeChoices = [markdown, json]
        });
        if (file is null)
            return;

        var receipt = await _reader.ReadReceiptAsync(
            _viewModel.SelectedSession.Session.Id,
            _lifetime.Token);
        if (receipt is null)
            return;
        var exportJson = string.Equals(Path.GetExtension(file.Name), ".json", StringComparison.OrdinalIgnoreCase);
        var text = exportJson ? _formatter.FormatJson(receipt) : _formatter.FormatMarkdown(receipt);
        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(text.AsMemory(), _lifetime.Token);
    }

    private async void OnClear(object? sender, RoutedEventArgs e)
    {
        var confirmed = await MessageDialog.ShowConfirmationAsync(
            this,
            _localization["AgentJournal.Clear.Title"],
            _viewModel.CurrentProjectOnly
                ? _localization["AgentJournal.Clear.ProjectMessage"]
                : _localization["AgentJournal.Clear.AllMessage"],
            _localization["AgentJournal.Clear"],
            _localization["Dialog.Cancel"],
            width: 430,
            height: 170);
        if (confirmed)
            await ClearCurrentScopeAsync(_lifetime.Token);
    }

    private async Task RefreshSafelyAsync()
    {
        try
        {
            await RefreshAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Agent journal refresh failed: {0}", exception.GetType().Name);
        }
    }

    private async Task LoadSelectedSessionSafelyAsync()
    {
        try
        {
            await LoadSelectedSessionAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Agent journal calls could not be read: {0}", exception.GetType().Name);
        }
    }

    private async Task LoadSelectedSessionAsync(CancellationToken cancellationToken)
    {
        _watchSession?.Cancel();
        _watchSession?.Dispose();
        _watchSession = null;

        var session = _viewModel.SelectedSession?.Session;
        if (session is null)
        {
            _viewModel.ReplaceCalls([]);
            _viewModel.FooterText = string.Empty;
            return;
        }

        var calls = await _reader.ReadCallsAsync(session.Id, cancellationToken);
        _viewModel.ReplaceCalls(calls.Select(CreateCallRow).ToArray());
        _viewModel.FooterText = FormatFooter(session);

        if (!session.IsLive || session.Mode != AgentJournalMode.Live)
            return;
        _watchSession = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = WatchSelectedSessionAsync(session.Id, _watchSession.Token);
    }

    private async Task WatchSelectedSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var change in _reader.WatchChangesAsync(sessionId, cancellationToken))
            {
                if (!string.Equals(change.SessionId, sessionId, StringComparison.Ordinal))
                    continue;
                Task refresh = Task.CompletedTask;
                await Dispatcher.UIThread.InvokeAsync(
                    () => refresh = RefreshSafelyAsync(),
                    DispatcherPriority.Background);
                await refresh;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Agent journal change stream ended: {0}", exception.GetType().Name);
        }
    }

    private AgentJournalSessionViewModel CreateSessionRow(AgentJournalSession session, DateTimeOffset now)
    {
        var mode = _localization[session.Mode == AgentJournalMode.Live
            ? "AgentJournal.Mode.Live"
            : "AgentJournal.Mode.Standard"];
        return new AgentJournalSessionViewModel(
            session,
            AgentJournalPresentation.FormatProject(session),
            mode,
            AgentJournalPresentation.FormatSessionDuration(session, now),
            _localization.Format(
                "AgentJournal.Masked",
                AgentJournalPresentation.FormatNumber(session.Totals.SecretsMasked),
                AgentJournalPresentation.FormatNumber(session.Totals.PrivateDataMasked)),
            _localization.Format(
                "AgentJournal.Masked.Short",
                AgentJournalPresentation.FormatNumber(session.Totals.SecretsMasked),
                AgentJournalPresentation.FormatNumber(session.Totals.PrivateDataMasked)));
    }

    private AgentJournalCallViewModel CreateCallRow(AgentJournalCall call) => new(
        call,
        _localization.Format(
            "AgentJournal.Masked",
            AgentJournalPresentation.FormatNumber(call.SecretsMasked),
            AgentJournalPresentation.FormatNumber(call.PrivateDataMasked)),
        _localization.Format(
            "AgentJournal.Masked.Short",
            AgentJournalPresentation.FormatNumber(call.SecretsMasked),
            AgentJournalPresentation.FormatNumber(call.PrivateDataMasked)),
        string.Join(", ", call.Notices.Select(FormatNotice)));

    private string FormatNotice(string notice) => notice switch
    {
        AgentJournalNoticeCodes.OutsideSelection => _localization["AgentJournal.Notice.OutsideSelection"],
        AgentJournalNoticeCodes.StalePack => _localization["AgentJournal.Notice.StalePack"],
        AgentJournalNoticeCodes.SearchPartial => _localization["AgentJournal.Notice.SearchPartial"],
        AgentJournalNoticeCodes.MatchesOmitted => _localization["AgentJournal.Notice.MatchesOmitted"],
        AgentJournalNoticeCodes.BudgetSkipped => _localization["AgentJournal.Notice.BudgetSkipped"],
        AgentJournalNoticeCodes.MissingPath => _localization["AgentJournal.Notice.MissingPath"],
        AgentJournalNoticeCodes.Unavailable => _localization["AgentJournal.Notice.Unavailable"],
        _ => notice
    };

    private string FormatFooter(AgentJournalSession session) => _localization.Format(
        "AgentJournal.Footer",
        AgentJournalPresentation.FormatNumber(session.Totals.Calls),
        AgentJournalPresentation.FormatNumber(session.Totals.ResultCharacters),
        AgentJournalPresentation.FormatNumber(session.Totals.EstimatedTokens),
        AgentJournalPresentation.FormatNumber(session.Totals.FilesDelivered),
        AgentJournalPresentation.FormatNumber(session.Totals.SecretsMasked + session.Totals.PrivateDataMasked),
        AgentJournalPresentation.FormatNumber(session.Totals.Errors));

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        _viewModel.UpdateLocalization();
        _ = RefreshSafelyAsync();
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        ApplyDialogSurface();
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!_lifetime.IsCancellationRequested)
                    ApplyDialogSurface();
            },
            DispatcherPriority.Render);
    }

    internal static Size ResolveInitialSize(Size ownerSize)
    {
        return new Size(
            ResolveInitialDimension(ownerSize.Width, MinimumWindowWidth),
            ResolveInitialDimension(ownerSize.Height, MinimumWindowHeight));
    }

    private static double ResolveInitialDimension(double ownerDimension, double minimum)
    {
        if (!double.IsFinite(ownerDimension) || ownerDimension <= 0)
            return minimum;
        return Math.Min(ownerDimension, Math.Max(minimum, ownerDimension * OwnerSizeRatio));
    }

    private void ApplyInitialSize()
    {
        var ownerSize = _owner?.ClientSize ?? default;
        if (ownerSize.Width <= 0 || ownerSize.Height <= 0)
            ownerSize = _owner?.Bounds.Size ?? default;
        var initialSize = ResolveInitialSize(ownerSize);
        Width = initialSize.Width;
        Height = initialSize.Height;
        MinWidth = MinimumWindowWidth;
        MinHeight = MinimumWindowHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }

    private void ApplyDialogSurface()
    {
        var theme = DialogSurfaceFactory.ResolveThemeVariant(_owner);
        var brushes = DialogSurfaceFactory.ResolveBrushes(_owner, theme);
        DialogSurfaceFactory.ApplyWindowSurface(this, theme, brushes);
        var background = Background;
        var panel = EnsureOpaqueSurface(brushes.Panel, background);
        var border = brushes.Border ?? panel;
        var header = EnsureOpaqueSurface(brushes.Header, panel);

        JournalSurface.Background = background;
        ApplyCardSurface(JournalSessionsSurface, panel, border);
        JournalSessionsHeader.Background = header;
        JournalSessionsList.Background = panel;
        JournalSessionsList.BorderBrush = border;
        ApplyCardSurface(JournalCallsSurface, panel, border);
        JournalCallsHeader.Background = header;
        JournalCallsList.Background = panel;
        JournalCallsList.BorderBrush = border;
        JournalFooterSurface.Background = panel;
        JournalFooterSurface.BorderBrush = border;
        ApplyCardSurface(JournalEmptySurface, panel, border);
    }

    private static void ApplyCardSurface(Border surface, IBrush? background, IBrush? border)
    {
        surface.Background = background;
        surface.BorderBrush = border;
    }

    private static IBrush? EnsureOpaqueSurface(IBrush? brush, IBrush? fallback)
    {
        if (brush is not ISolidColorBrush solid)
            return fallback;
        var color = solid.Color;
        return color.A == byte.MaxValue
            ? brush
            : new SolidColorBrush(Color.FromArgb(byte.MaxValue, color.R, color.G, color.B));
    }
}
