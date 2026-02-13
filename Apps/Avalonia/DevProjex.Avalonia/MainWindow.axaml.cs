using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DevProjex.Application;
using DevProjex.Application.Services;
using DevProjex.Application.UseCases;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.Services;
using DevProjex.Avalonia.Views;
using DevProjex.Avalonia.ViewModels;
using ThemePresetStore = DevProjex.Infrastructure.ThemePresets.ThemePresetStore;
using ThemePresetDb = DevProjex.Infrastructure.ThemePresets.ThemePresetDb;
using ThemePreset = DevProjex.Infrastructure.ThemePresets.ThemePreset;
using ThemePresetVariant = DevProjex.Infrastructure.ThemePresets.ThemeVariant;
using ThemePresetEffect = DevProjex.Infrastructure.ThemePresets.ThemeEffectMode;
using DevProjex.Kernel.Abstractions;
using DevProjex.Kernel;
using DevProjex.Kernel.Contracts;
using DevProjex.Kernel.Models;
using DevProjex.Infrastructure.Git;

namespace DevProjex.Avalonia;

public partial class MainWindow : Window
{
    public MainWindow()
        : this(CommandLineOptions.Empty, AvaloniaCompositionRoot.CreateDefault(CommandLineOptions.Empty))
    {
    }

    private readonly CommandLineOptions _startupOptions;
    private readonly LocalizationService _localization;
    private readonly ScanOptionsUseCase _scanOptions;
    private readonly BuildTreeUseCase _buildTree;
    private readonly IgnoreOptionsService _ignoreOptionsService;
    private readonly IgnoreRulesService _ignoreRulesService;
    private readonly FilterOptionSelectionService _filterSelectionService;
    private readonly TreeExportService _treeExport;
    private readonly SelectedContentExportService _contentExport;
    private readonly TreeAndContentExportService _treeAndContentExport;
    private readonly IToastService _toastService;
    private readonly IconCache _iconCache;
    private readonly IElevationService _elevation;
    private readonly ThemePresetStore _themePresetStore;
    private readonly IGitRepositoryService _gitService;
    private readonly IRepoCacheService _repoCacheService;
    private readonly IZipDownloadService _zipDownloadService;
    private readonly IFileContentAnalyzer _fileContentAnalyzer;

    private readonly MainWindowViewModel _viewModel;
    private readonly TreeSearchCoordinator _searchCoordinator;
    private readonly NameFilterCoordinator _filterCoordinator;
    private readonly ThemeBrushCoordinator _themeBrushCoordinator;
    private readonly SelectionSyncCoordinator _selectionCoordinator;

    private BuildTreeResult? _currentTree;
    private string? _currentPath;
    private string? _currentProjectDisplayName;
    private string? _currentRepositoryUrl;
    private bool _elevationAttempted;
    private bool _wasThemePopoverOpen;
    private ThemePresetDb _themePresetDb = new();
    private ThemePresetVariant _currentThemeVariant = ThemePresetVariant.Dark;
    private ThemePresetEffect _currentEffectMode = ThemePresetEffect.Transparent;

    private TreeView? _treeView;
    private TopMenuBarView? _topMenuBar;
    private SearchBarView? _searchBar;
    private FilterBarView? _filterBar;
    private HashSet<string>? _filterExpansionSnapshot;
    private int _filterApplyVersion;
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _gitCloneCts;
    private GitCloneWindow? _gitCloneWindow;
    private string? _currentCachedRepoPath;
    private Viewbox? _dropZoneIcon;
    private TranslateTransform? _dropZoneIconTransform;
    private global::Avalonia.Threading.DispatcherTimer? _dropZoneFloatTimer;
    private readonly Stopwatch _dropZoneFloatClock = new();

    // Settings panel animation
    private Border? _settingsContainer;
    private Border? _settingsIsland;
    private TranslateTransform? _settingsTransform;
    private bool _settingsAnimating;
    private const double SettingsPanelWidth = 328.0; // 320 content + 8 margin

    // Real-time metrics calculation
    private readonly object _metricsLock = new();
    private CancellationTokenSource? _metricsCalculationCts;
    private global::Avalonia.Threading.DispatcherTimer? _metricsDebounceTimer;
    private readonly Dictionary<string, FileMetricsData> _fileMetricsCache = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _isBackgroundMetricsActive;

    // Event handler delegates for proper unsubscription
    private EventHandler? _languageChangedHandler;
    private EventHandler? _themeChangedHandler;
    private PropertyChangedEventHandler? _viewModelPropertyChangedHandler;

    public MainWindow(CommandLineOptions startupOptions, AvaloniaAppServices services)
    {
        _startupOptions = startupOptions;
        _localization = services.Localization;
        _scanOptions = services.ScanOptionsUseCase;
        _buildTree = services.BuildTreeUseCase;
        _ignoreOptionsService = services.IgnoreOptionsService;
        _ignoreRulesService = services.IgnoreRulesService;
        _filterSelectionService = services.FilterOptionSelectionService;
        _treeExport = services.TreeExportService;
        _contentExport = services.ContentExportService;
        _treeAndContentExport = services.TreeAndContentExportService;
        _toastService = services.ToastService;
        _iconCache = new IconCache(services.IconStore);
        _elevation = services.Elevation;
        _themePresetStore = services.ThemePresetStore;
        _gitService = services.GitRepositoryService;
        _repoCacheService = services.RepoCacheService;
        _zipDownloadService = services.ZipDownloadService;
        _fileContentAnalyzer = services.FileContentAnalyzer;

        _viewModel = new MainWindowViewModel(_localization, services.HelpContentProvider);
        _viewModel.SetToastItems(_toastService.Items);
        DataContext = _viewModel;
        SubscribeToMetricsUpdates();

        InitializeComponent();

        // Setup drag & drop for the drop zone
        var dropZone = this.FindControl<Border>("DropZoneContainer");
        if (dropZone is not null)
        {
            dropZone.AddHandler(DragDrop.DragEnterEvent, OnDropZoneDragEnter);
            dropZone.AddHandler(DragDrop.DragLeaveEvent, OnDropZoneDragLeave);
            dropZone.AddHandler(DragDrop.DropEvent, OnDropZoneDrop);
        }

        InitializeThemePresets();

        _viewModel.UpdateHelpPopoverMaxSize(Bounds.Size);
        PropertyChanged += OnWindowPropertyChanged;

        _treeView = this.FindControl<TreeView>("ProjectTree");
        _topMenuBar = this.FindControl<TopMenuBarView>("TopMenuBar");
        _searchBar = this.FindControl<SearchBarView>("SearchBar");
        _filterBar = this.FindControl<FilterBarView>("FilterBar");
        _dropZoneIcon = this.FindControl<Viewbox>("DropZoneIcon");
        _settingsContainer = this.FindControl<Border>("SettingsContainer");
        _settingsIsland = this.FindControl<Border>("SettingsIsland");

        if (_dropZoneIcon is not null)
        {
            _dropZoneIconTransform = new TranslateTransform();
            _dropZoneIcon.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
            _dropZoneIcon.RenderTransform = _dropZoneIconTransform;
            EnsureDropZoneFloatAnimationStarted();
        }

        if (_settingsIsland is not null && _settingsContainer is not null)
        {
            _settingsTransform = new TranslateTransform();
            _settingsIsland.RenderTransform = _settingsTransform;
            // Start hidden (collapsed width, off-screen to the right)
            _settingsContainer.Width = 0;
            _settingsTransform.X = SettingsPanelWidth;
            _settingsIsland.Opacity = 0;
        }

        if (_treeView is not null)
        {
            _treeView.PointerEntered += OnTreePointerEntered;
        }
        AddHandler(PointerWheelChangedEvent, OnWindowPointerWheelChanged, RoutingStrategies.Tunnel, true);

        _searchCoordinator = new TreeSearchCoordinator(_viewModel, _treeView ?? throw new InvalidOperationException());
        _filterCoordinator = new NameFilterCoordinator(ApplyFilterRealtimeWithToken);
        _themeBrushCoordinator = new ThemeBrushCoordinator(this, _viewModel, () => _topMenuBar?.MainMenuControl);
        _selectionCoordinator = new SelectionSyncCoordinator(
            _viewModel,
            _scanOptions,
            _filterSelectionService,
            _ignoreOptionsService,
            BuildIgnoreRules,
            TryElevateAndRestart,
            () => _currentPath);

        Closed += OnWindowClosed;
        Deactivated += OnDeactivated;

        _elevationAttempted = startupOptions.ElevationAttempted;

        // Store event handlers for proper unsubscription
        _languageChangedHandler = (_, _) => ApplyLocalization();
        _localization.LanguageChanged += _languageChangedHandler;

        var app = global::Avalonia.Application.Current;
        if (app is not null)
        {
            _themeChangedHandler = OnThemeChanged;
            app.ActualThemeVariantChanged += _themeChangedHandler;
        }

        InitializeFonts();
        _selectionCoordinator.HookOptionListeners(_viewModel.RootFolders);
        _selectionCoordinator.HookOptionListeners(_viewModel.Extensions);
        _selectionCoordinator.HookIgnoreListeners(_viewModel.IgnoreOptions);

        _viewModelPropertyChangedHandler = (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.SearchQuery))
                _searchCoordinator.OnSearchQueryChanged();
            else if (args.PropertyName == nameof(MainWindowViewModel.NameFilter))
                _filterCoordinator.OnNameFilterChanged();
            else if (args.PropertyName is nameof(MainWindowViewModel.MaterialIntensity)
                     or nameof(MainWindowViewModel.PanelContrast)
                     or nameof(MainWindowViewModel.BorderStrength)
                     or nameof(MainWindowViewModel.MenuChildIntensity))
                _themeBrushCoordinator.UpdateDynamicThemeBrushes();
            else if (args.PropertyName == nameof(MainWindowViewModel.BlurRadius))
                _themeBrushCoordinator.UpdateTransparencyEffect();
            else if (args.PropertyName == nameof(MainWindowViewModel.ThemePopoverOpen))
                HandleThemePopoverStateChange();
            else if (args.PropertyName == nameof(MainWindowViewModel.IsProjectLoaded))
                UpdateDropZoneFloatAnimationState();
            else if (args.PropertyName == nameof(MainWindowViewModel.SelectedExportFormat))
                RecalculateMetricsAsync(); // Update tree metrics when format changes (ASCII vs JSON)
        };
        _viewModel.PropertyChanged += _viewModelPropertyChangedHandler;

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        Opened += OnOpened;

        // Hook menu item submenu opening to apply brushes directly
        AddHandler(MenuItem.SubmenuOpenedEvent, _themeBrushCoordinator.HandleSubmenuOpened, RoutingStrategies.Bubble);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        // Unsubscribe from window events
        PropertyChanged -= OnWindowPropertyChanged;

        // Unsubscribe from localization service
        if (_languageChangedHandler is not null)
            _localization.LanguageChanged -= _languageChangedHandler;

        // Unsubscribe from application theme changes
        var app = global::Avalonia.Application.Current;
        if (app is not null && _themeChangedHandler is not null)
            app.ActualThemeVariantChanged -= _themeChangedHandler;

        // Unsubscribe from ViewModel
        if (_viewModelPropertyChangedHandler is not null)
            _viewModel.PropertyChanged -= _viewModelPropertyChangedHandler;

        // Unsubscribe from tree checkbox changes for metrics
        UnsubscribeFromMetricsUpdates();

        // Cancel metrics calculation
        _metricsCalculationCts?.Cancel();
        _metricsCalculationCts?.Dispose();
        _metricsDebounceTimer?.Stop();

        // Dispose coordinators
        _searchCoordinator.Dispose();
        _filterCoordinator.Dispose();

        // Cancel and dispose refresh token
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();

        // Cancel and dispose git clone token
        _gitCloneCts?.Cancel();
        _gitCloneCts?.Dispose();

        // Clear icon cache to release memory
        _iconCache.Clear();

        // Clear tree references
        _currentTree = null;
        _filterExpansionSnapshot = null;

        // Clean up repository cache on exit
        _repoCacheService.ClearAllCache();

        // Dispose ZipDownloadService
        if (_zipDownloadService is IDisposable disposable)
            disposable.Dispose();

        if (_dropZoneFloatTimer is not null)
        {
            _dropZoneFloatTimer.Stop();
            _dropZoneFloatTimer.Tick -= OnDropZoneFloatTick;
        }
    }

    private void EnsureDropZoneFloatAnimationStarted()
    {
        if (_dropZoneIcon is null || _dropZoneIconTransform is null)
            return;

        _dropZoneFloatTimer ??= new global::Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _dropZoneFloatTimer.Tick -= OnDropZoneFloatTick;
        _dropZoneFloatTimer.Tick += OnDropZoneFloatTick;

        _dropZoneFloatClock.Restart();
        _dropZoneFloatTimer.Start();
    }

    private void UpdateDropZoneFloatAnimationState()
    {
        if (_viewModel.IsProjectLoaded)
        {
            _dropZoneFloatTimer?.Stop();
            if (_dropZoneIconTransform is not null)
                _dropZoneIconTransform.Y = 0;
            return;
        }

        EnsureDropZoneFloatAnimationStarted();
    }

    private void OnDropZoneFloatTick(object? sender, EventArgs e)
    {
        if (_dropZoneIconTransform is null)
            return;

        //Folder's animaion parameters
        const double periodSeconds = 2.0; // Animation speed
        const double amplitudePx = 4.0; // Vertical distance
        var phase = _dropZoneFloatClock.Elapsed.TotalSeconds / periodSeconds * 2 * Math.PI;

        // Symmetric sine motion makes the floating clearly visible.
        _dropZoneIconTransform.Y = Math.Sin(phase) * amplitudePx;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        // Defer update to let theme resources settle first
        global::Avalonia.Threading.Dispatcher.UIThread.Post(
            () => _searchCoordinator.RefreshThemeHighlights(),
            global::Avalonia.Threading.DispatcherPriority.Background);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != BoundsProperty)
            return;

        if (e.NewValue is Rect rect)
            _viewModel.UpdateHelpPopoverMaxSize(rect.Size);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_viewModel.HelpPopoverOpen)
            _viewModel.HelpPopoverOpen = false;
        if (_viewModel.HelpDocsPopoverOpen)
            _viewModel.HelpDocsPopoverOpen = false;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            ApplyStartupThemePreset();

            if (!string.IsNullOrWhiteSpace(_startupOptions.Path))
                await TryOpenFolderAsync(_startupOptions.Path!, fromDialog: false);

            // Clean up stale cache from previous sessions (non-blocking background task)
            _ = Task.Run(() =>
            {
                try
                {
                    _repoCacheService.CleanupStaleCacheOnStartup();
                }
                catch
                {
                    // Best effort - ignore errors
                }
            });
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    #region Drop Zone Handlers

    private void OnDropZoneClick(object? sender, PointerPressedEventArgs e)
    {
        // Ignore if clicked on the button (button has its own handler)
        if (e.Source is Button) return;

        OnOpenFolder(sender, new RoutedEventArgs());
    }

    private void OnDropZoneDragEnter(object? sender, DragEventArgs e)
    {
        var hasFolder = e.Data.GetDataFormats().Any(f =>
            f == DataFormats.Files);

        e.DragEffects = hasFolder ? DragDropEffects.Copy : DragDropEffects.None;

        // Add visual feedback class
        if (sender is Border border)
        {
            border.Classes.Add("drag-over");
        }
    }

    private void OnDropZoneDragLeave(object? sender, DragEventArgs e)
    {
        // Remove visual feedback class
        if (sender is Border border)
        {
            border.Classes.Remove("drag-over");
        }
    }

    private async void OnDropZoneDrop(object? sender, DragEventArgs e)
    {
        // Remove visual feedback class
        if (sender is Border border)
        {
            border.Classes.Remove("drag-over");
        }

        try
        {
            var files = e.Data.GetFiles();
            if (files is null) return;

            var folder = files
                .Select(f => f.TryGetLocalPath())
                .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(folder))
            {
                // Maybe it's a file - try to get its directory
                var file = files
                    .Select(f => f.TryGetLocalPath())
                    .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                    .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(file))
                {
                    folder = Path.GetDirectoryName(file);
                }
            }

            if (!string.IsNullOrWhiteSpace(folder))
            {
                await TryOpenFolderAsync(folder, fromDialog: true);
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    #endregion

    private void ApplyStartupThemePreset()
    {
        var app = global::Avalonia.Application.Current;
        if (app is null) return;

        app.RequestedThemeVariant = _currentThemeVariant == ThemePresetVariant.Dark
            ? ThemeVariant.Dark
            : ThemeVariant.Light;

        _viewModel.IsDarkTheme = _currentThemeVariant == ThemePresetVariant.Dark;
        ApplyEffectMode(_currentEffectMode);
        ApplyPresetValues(_themePresetStore.GetPreset(_themePresetDb, _currentThemeVariant, _currentEffectMode));
        _themeBrushCoordinator.UpdateTransparencyEffect();
    }

    private void InitializeThemePresets()
    {
        _themePresetDb = _themePresetStore.Load();

        if (!_themePresetStore.TryParseKey(_themePresetDb.LastSelected, out var theme, out var effect))
        {
            theme = ThemePresetVariant.Dark;
            effect = ThemePresetEffect.Transparent;
        }

        _currentThemeVariant = theme;
        _currentEffectMode = effect;
        _viewModel.IsDarkTheme = theme == ThemePresetVariant.Dark;
        ApplyEffectMode(effect);
        ApplyPresetValues(_themePresetStore.GetPreset(_themePresetDb, theme, effect));
        _wasThemePopoverOpen = _viewModel.ThemePopoverOpen;
    }

    private void ApplyEffectMode(ThemePresetEffect effect)
    {
        switch (effect)
        {
            case ThemePresetEffect.Mica:
                _viewModel.IsMicaEnabled = true;
                break;
            case ThemePresetEffect.Acrylic:
                _viewModel.IsAcrylicEnabled = true;
                break;
            default:
                _viewModel.IsTransparentEnabled = true;
                break;
        }
    }

    private void ApplyPresetValues(ThemePreset preset)
    {
        _viewModel.MaterialIntensity = preset.MaterialIntensity;
        _viewModel.BlurRadius = preset.BlurRadius;
        _viewModel.PanelContrast = preset.PanelContrast;
        _viewModel.MenuChildIntensity = preset.MenuChildIntensity;
        _viewModel.BorderStrength = preset.BorderStrength;
    }

    private void ApplyPresetForSelection(ThemePresetVariant theme, ThemePresetEffect effect)
    {
        _currentThemeVariant = theme;
        _currentEffectMode = effect;
        ApplyPresetValues(_themePresetStore.GetPreset(_themePresetDb, theme, effect));
    }

    private void HandleThemePopoverStateChange()
    {
        if (_wasThemePopoverOpen && !_viewModel.ThemePopoverOpen)
            SaveCurrentThemePreset();

        _wasThemePopoverOpen = _viewModel.ThemePopoverOpen;
    }

    private void SaveCurrentThemePreset()
    {
        var theme = GetSelectedThemeVariant();
        var effect = GetEffectModeForSave();

        _currentThemeVariant = theme;
        _currentEffectMode = effect;

        var preset = new ThemePreset
        {
            Theme = theme,
            Effect = effect,
            MaterialIntensity = _viewModel.MaterialIntensity,
            BlurRadius = _viewModel.BlurRadius,
            PanelContrast = _viewModel.PanelContrast,
            MenuChildIntensity = _viewModel.MenuChildIntensity,
            BorderStrength = _viewModel.BorderStrength
        };

        _themePresetStore.SetPreset(_themePresetDb, theme, effect, preset);
        _themePresetDb.LastSelected = $"{theme}.{effect}";
        _themePresetStore.Save(_themePresetDb);
    }

    private ThemePresetVariant GetSelectedThemeVariant()
        => _viewModel.IsDarkTheme ? ThemePresetVariant.Dark : ThemePresetVariant.Light;

    private ThemePresetEffect GetSelectedEffectMode()
    {
        if (_viewModel.IsMicaEnabled)
            return ThemePresetEffect.Mica;
        if (_viewModel.IsAcrylicEnabled)
            return ThemePresetEffect.Acrylic;
        return ThemePresetEffect.Transparent;
    }

    private ThemePresetEffect GetEffectModeForSave()
    {
        if (_viewModel.HasAnyEffect)
            return GetSelectedEffectMode();

        return _currentEffectMode;
    }

    private void InitializeFonts()
    {
        // Only use predefined fonts like WinForms
        var predefinedFonts = new[]
            { "Consolas", "Courier New", "Fira Code", "Lucida Console", "Cascadia Code", "JetBrains Mono" };

        var systemFonts = FontManager.Current?.SystemFonts?
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);

        _viewModel.FontFamilies.Add(FontFamily.Default);

        // Add only predefined fonts that exist on system
        foreach (var fontName in predefinedFonts)
        {
            if (systemFonts.TryGetValue(fontName, out var font))
                _viewModel.FontFamilies.Add(font);
        }

        if (_viewModel.FontFamilies.Count == 1)
        {
            foreach (var font in systemFonts.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                _viewModel.FontFamilies.Add(font);
        }

        var selected = _viewModel.FontFamilies.FirstOrDefault();
        _viewModel.SelectedFontFamily = selected;
        _viewModel.PendingFontFamily = selected;
    }

    private void SyncThemeWithSystem()
    {
        var app = global::Avalonia.Application.Current;
        if (app is null) return;

        var isDark = app.ActualThemeVariant == ThemeVariant.Dark;
        _viewModel.IsDarkTheme = isDark;
    }

    private void ApplyLocalization()
    {
        _viewModel.UpdateLocalization();
        RecalculateMetricsAsync(); // Update metrics text with new localization
        UpdateTitle();

        if (_currentPath is not null)
        {
            _selectionCoordinator.PopulateIgnoreOptionsForRootSelection(_selectionCoordinator.GetSelectedRootFolders(), _currentPath);
        }
    }

    private async Task ShowErrorAsync(string message)
    {
        // Show error relative to Git Clone window if it's open, otherwise relative to main window
        var owner = _gitCloneWindow ?? (Window)this;
        await MessageDialog.ShowAsync(owner, _localization["Msg.ErrorTitle"], message);
    }

    private async Task ShowInfoAsync(string message) =>
        await MessageDialog.ShowAsync(this, _localization["Msg.InfoTitle"], message);

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var options = new FolderPickerOpenOptions
            {
                AllowMultiple = false,
                Title = _viewModel.MenuFileOpen
            };

            var folders = await StorageProvider.OpenFolderPickerAsync(options);
            var folder = folders.FirstOrDefault();
            var path = folder?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
                return;

            await TryOpenFolderAsync(path, fromDialog: true);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    private async void OnRefresh(object? sender, RoutedEventArgs e)
    {
        BeginStatusOperation(_viewModel.StatusOperationRefreshingProject, indeterminate: true);
        try
        {
            await ReloadProjectAsync();
            CompleteStatusOperation();
            _toastService.Show(_localization["Toast.Refresh.Success"]);
        }
        catch (Exception ex)
        {
            CompleteStatusOperation();
            await ShowErrorAsync(ex.Message);
        }
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private async void OnCopyTree(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!EnsureTreeReady()) return;

            var selected = GetCheckedPaths();
            string content;
            if (selected.Count > 0)
            {
                content = _treeExport.BuildSelectedTree(_currentPath!, _currentTree!.Root, selected);
                if (string.IsNullOrWhiteSpace(content))
                    content = _treeExport.BuildFullTree(_currentPath!, _currentTree!.Root);
            }
            else
            {
                content = _treeExport.BuildFullTree(_currentPath!, _currentTree!.Root);
            }

            await SetClipboardTextAsync(content);
            CompleteStatusOperation();
            _toastService.Show(_localization["Toast.Copy.Tree"]);
        }
        catch (Exception ex)
        {
            CompleteStatusOperation();
            await ShowErrorAsync(ex.Message);
        }
    }

    private async void OnCopyContent(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!EnsureTreeReady()) return;

            // Cancel background metrics calculation - user wants immediate action
            CancelBackgroundMetricsCalculation();

            var selected = GetCheckedPaths();
            var files = (selected.Count > 0 ? selected.Where(File.Exists) : EnumerateFilePaths(_currentTree!.Root))
                .Distinct(PathComparer.Default)
                .OrderBy(path => path, PathComparer.Default)
                .ToList();

            if (files.Count == 0)
            {
                if (selected.Count > 0)
                    await ShowInfoAsync(_localization["Msg.NoCheckedFiles"]);
                else
                    await ShowInfoAsync(_localization["Msg.NoTextContent"]);
                return;
            }

            // Run file reading off UI thread
            BeginStatusOperation("Preparing content...", indeterminate: true);
            var content = await Task.Run(() => _contentExport.BuildAsync(files, CancellationToken.None));
            if (string.IsNullOrWhiteSpace(content))
            {
                CompleteStatusOperation();
                await ShowInfoAsync(_localization["Msg.NoTextContent"]);
                return;
            }

            await SetClipboardTextAsync(content);
            CompleteStatusOperation();
            _toastService.Show(_localization["Toast.Copy.Content"]);
        }
        catch (Exception ex)
        {
            CompleteStatusOperation();
            await ShowErrorAsync(ex.Message);
        }
    }

    private async void OnCopyTreeAndContent(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!EnsureTreeReady()) return;

            // Cancel background metrics calculation - user wants immediate action
            CancelBackgroundMetricsCalculation();

            var selected = GetCheckedPaths();
            // Run file reading off UI thread
            BeginStatusOperation("Building export...", indeterminate: true);
            var content = await Task.Run(() => _treeAndContentExport.BuildAsync(_currentPath!, _currentTree!.Root, selected, CancellationToken.None));
            await SetClipboardTextAsync(content);
            CompleteStatusOperation();
            _toastService.Show(_localization["Toast.Copy.TreeAndContent"]);
        }
        catch (Exception ex)
        {
            CompleteStatusOperation();
            await ShowErrorAsync(ex.Message);
        }
    }

    private void OnExpandAll(object? sender, RoutedEventArgs e) => ExpandCollapseTree(expand: true);

    private void OnCollapseAll(object? sender, RoutedEventArgs e) => ExpandCollapseTree(expand: false);

    private void ExpandCollapseTree(bool expand)
    {
        foreach (var node in _viewModel.TreeNodes)
        {
            node.SetExpandedRecursive(expand);
            if (!expand)
                node.IsExpanded = true;
        }
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e) => AdjustTreeFontSize(1);

    private void OnZoomOut(object? sender, RoutedEventArgs e) => AdjustTreeFontSize(-1);

    private void OnZoomReset(object? sender, RoutedEventArgs e) => _viewModel.TreeFontSize = 12;

    private void AdjustTreeFontSize(double delta)
    {
        const double min = 6;
        const double max = 28;
        var next = Math.Clamp(_viewModel.TreeFontSize + delta, min, max);
        _viewModel.TreeFontSize = next;
    }

    private void OnToggleSettings(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsProjectLoaded) return;
        if (_settingsAnimating) return;

        var newVisible = !_viewModel.SettingsVisible;
        _viewModel.SettingsVisible = newVisible;
        AnimateSettingsPanel(newVisible);
    }

    private async void AnimateSettingsPanel(bool show)
    {
        if (_settingsIsland is null || _settingsTransform is null || _settingsContainer is null) return;
        if (_settingsAnimating) return;

        _settingsAnimating = true;

        const double durationMs = 300.0;

        // Get current values
        var startWidth = _settingsContainer.Width;
        if (double.IsNaN(startWidth)) startWidth = show ? 0 : SettingsPanelWidth;

        var startX = _settingsTransform.X;
        var startOpacity = _settingsIsland.Opacity;

        // Target values
        var endWidth = show ? SettingsPanelWidth : 0.0;
        var endX = show ? 0.0 : SettingsPanelWidth;
        var endOpacity = show ? 1.0 : 0.0;

        var clock = Stopwatch.StartNew();

        while (clock.Elapsed.TotalMilliseconds < durationMs)
        {
            var t = Math.Min(1.0, clock.Elapsed.TotalMilliseconds / durationMs);
            // Cubic ease out: 1 - (1-t)^3
            var eased = 1.0 - Math.Pow(1.0 - t, 3);

            // Animate container width (this makes tree expand/contract)
            _settingsContainer.Width = startWidth + (endWidth - startWidth) * eased;

            // Animate inner panel slide and fade
            _settingsTransform.X = startX + (endX - startX) * eased;
            _settingsIsland.Opacity = startOpacity + (endOpacity - startOpacity) * eased;

            await Task.Delay(8); // ~120 FPS for smooth animation
        }

        // Ensure final values
        _settingsContainer.Width = endWidth;
        _settingsTransform.X = endX;
        _settingsIsland.Opacity = endOpacity;
        _settingsAnimating = false;
    }

    private void OnSetLightTheme(object? sender, RoutedEventArgs e)
    {
        var app = global::Avalonia.Application.Current;
        if (app is null) return;

        app.RequestedThemeVariant = ThemeVariant.Light;
        _viewModel.IsDarkTheme = false;
        ApplyPresetForSelection(ThemePresetVariant.Light, GetSelectedEffectMode());
        _searchCoordinator.UpdateHighlights(_viewModel.SearchQuery);
        _searchCoordinator.UpdateHighlights(_viewModel.NameFilter);
        _themeBrushCoordinator.UpdateDynamicThemeBrushes();
    }

    private void OnSetDarkTheme(object? sender, RoutedEventArgs e)
    {
        var app = global::Avalonia.Application.Current;
        if (app is null) return;

        app.RequestedThemeVariant = ThemeVariant.Dark;
        _viewModel.IsDarkTheme = true;
        ApplyPresetForSelection(ThemePresetVariant.Dark, GetSelectedEffectMode());
        _searchCoordinator.UpdateHighlights(_viewModel.SearchQuery);
        _searchCoordinator.UpdateHighlights(_viewModel.NameFilter);
        _themeBrushCoordinator.UpdateDynamicThemeBrushes();
    }

    private void OnToggleMica(object? sender, RoutedEventArgs e)
    {
        _viewModel.IsMicaEnabled = !_viewModel.IsMicaEnabled;
        _themeBrushCoordinator.UpdateTransparencyEffect();
    }

    private void OnToggleAcrylic(object? sender, RoutedEventArgs e)
    {
        _viewModel.IsAcrylicEnabled = !_viewModel.IsAcrylicEnabled;
        _themeBrushCoordinator.UpdateTransparencyEffect();
    }

    private void OnToggleCompactMode(object? sender, RoutedEventArgs e)
    {
        _viewModel.IsCompactMode = !_viewModel.IsCompactMode;

        if (_viewModel.IsCompactMode)
            Classes.Add("compact-mode");
        else
            Classes.Remove("compact-mode");
    }

    private void OnThemeMenuClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.ThemePopoverOpen = !_viewModel.ThemePopoverOpen;
        e.Handled = true;
    }

    private void OnSetLightThemeCheckbox(object? sender, RoutedEventArgs e)
    {
        // Always set light theme when clicked (even if already light - just refresh)
        OnSetLightTheme(sender, e);
        e.Handled = true;
    }

    private void OnSetDarkThemeCheckbox(object? sender, RoutedEventArgs e)
    {
        // Always set dark theme when clicked
        OnSetDarkTheme(sender, e);
        e.Handled = true;
    }

    private void OnSetTransparentMode(object? sender, RoutedEventArgs e)
    {
        _viewModel.ToggleTransparent();
        _themeBrushCoordinator.UpdateTransparencyEffect();
        if (_viewModel.IsTransparentEnabled)
            ApplyPresetForSelection(GetSelectedThemeVariant(), ThemePresetEffect.Transparent);
        e.Handled = true;
    }

    private void OnSetMicaMode(object? sender, RoutedEventArgs e)
    {
        _viewModel.ToggleMica();
        _themeBrushCoordinator.UpdateTransparencyEffect();
        if (_viewModel.IsMicaEnabled)
            ApplyPresetForSelection(GetSelectedThemeVariant(), ThemePresetEffect.Mica);
        e.Handled = true;
    }

    private void OnSetAcrylicMode(object? sender, RoutedEventArgs e)
    {
        _viewModel.ToggleAcrylic();
        _themeBrushCoordinator.UpdateTransparencyEffect();
        if (_viewModel.IsAcrylicEnabled)
            ApplyPresetForSelection(GetSelectedThemeVariant(), ThemePresetEffect.Acrylic);
        e.Handled = true;
    }


    private void OnLangRu(object? sender, RoutedEventArgs e) => _localization.SetLanguage(AppLanguage.Ru);
    private void OnLangEn(object? sender, RoutedEventArgs e) => _localization.SetLanguage(AppLanguage.En);
    private void OnLangUz(object? sender, RoutedEventArgs e) => _localization.SetLanguage(AppLanguage.Uz);
    private void OnLangTg(object? sender, RoutedEventArgs e) => _localization.SetLanguage(AppLanguage.Tg);
    private void OnLangKk(object? sender, RoutedEventArgs e) => _localization.SetLanguage(AppLanguage.Kk);
    private void OnLangFr(object? sender, RoutedEventArgs e) => _localization.SetLanguage(AppLanguage.Fr);
    private void OnLangDe(object? sender, RoutedEventArgs e) => _localization.SetLanguage(AppLanguage.De);
    private void OnLangIt(object? sender, RoutedEventArgs e) => _localization.SetLanguage(AppLanguage.It);

    private void OnAbout(object? sender, RoutedEventArgs e)
    {
        _viewModel.HelpPopoverOpen = true;
        _viewModel.HelpDocsPopoverOpen = false;
        _viewModel.ThemePopoverOpen = false;
        e.Handled = true;
    }

    private void OnAboutClose(object? sender, RoutedEventArgs e)
    {
        _viewModel.HelpPopoverOpen = false;
        e.Handled = true;
    }

    private void OnHelp(object? sender, RoutedEventArgs e)
    {
        _viewModel.HelpDocsPopoverOpen = true;
        _viewModel.HelpPopoverOpen = false;
        _viewModel.ThemePopoverOpen = false;
        e.Handled = true;
    }

    private void OnHelpClose(object? sender, RoutedEventArgs e)
    {
        _viewModel.HelpDocsPopoverOpen = false;
        e.Handled = true;
    }

    private void OnResetSettings(object? sender, RoutedEventArgs e)
    {
        ResetThemeSettings();
        _toastService.Show(_localization["Toast.Settings.Reset"]);
        e.Handled = true;
    }

    /// <summary>
    /// Resets all theme presets to factory defaults and reapplies current selection.
    /// </summary>
    private void ResetThemeSettings()
    {
        _themePresetDb = _themePresetStore.ResetToDefaults();

        // Reparse last selected to get current theme variant and effect
        if (!_themePresetStore.TryParseKey(_themePresetDb.LastSelected, out var theme, out var effect))
        {
            theme = ThemePresetVariant.Dark;
            effect = ThemePresetEffect.Transparent;
        }

        _currentThemeVariant = theme;
        _currentEffectMode = effect;

        // Apply default preset values to ViewModel
        ApplyPresetValues(_themePresetStore.GetPreset(_themePresetDb, theme, effect));

        // Refresh visual effects
        _themeBrushCoordinator.UpdateTransparencyEffect();
        _themeBrushCoordinator.UpdateDynamicThemeBrushes();
    }

    #region Git Operations

    private void OnGitClone(object? sender, RoutedEventArgs e)
    {
        _viewModel.GitCloneUrl = string.Empty;
        _viewModel.GitCloneStatus = string.Empty;
        _viewModel.GitCloneInProgress = false;

        // Create and show Git Clone window
        _gitCloneWindow = new GitCloneWindow
        {
            DataContext = _viewModel
        };

        _gitCloneWindow.StartCloneRequested += OnGitCloneStart;
        _gitCloneWindow.CancelRequested += OnGitCloneCancel;

        _gitCloneWindow.ShowDialog(this);
        e.Handled = true;
    }

    private void OnGitCloneClose(object? sender, RoutedEventArgs e)
    {
        CancelGitCloneOperation();
        _gitCloneWindow?.Close();
        _gitCloneWindow = null;
        e.Handled = true;
    }

    private async void OnGitCloneStart(object? sender, RoutedEventArgs e)
    {
        var url = _viewModel.GitCloneUrl?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            await ShowErrorAsync(_viewModel.GitErrorInvalidUrl);
            return;
        }

        // Validate URL format before attempting to clone
        if (!IsValidGitRepositoryUrl(url))
        {
            await ShowErrorAsync(_viewModel.GitErrorInvalidUrl);
            return;
        }

        _gitCloneCts?.Cancel();
        _gitCloneCts = new CancellationTokenSource();
        var cancellationToken = _gitCloneCts.Token;

        _viewModel.GitCloneInProgress = true;
        _viewModel.GitCloneStatus = _viewModel.GitCloneProgressCheckingGit;

        string? targetPath = null;

        try
        {
            // Check internet connection before starting
            var hasInternet = await CheckInternetConnectionAsync(cancellationToken);
            if (!hasInternet)
            {
                _viewModel.GitCloneInProgress = false;
                _gitCloneWindow?.Close();
                _gitCloneWindow = null;
                await ShowErrorAsync(_viewModel.GitErrorNoInternetConnection);
                return;
            }

            // Clean up previous cached repository before cloning a new one
            if (_currentCachedRepoPath is not null)
            {
                _repoCacheService.DeleteRepositoryDirectory(_currentCachedRepoPath);
                _currentCachedRepoPath = null;
            }

            targetPath = _repoCacheService.CreateRepositoryDirectory(url);

            // Track current operation for progress reporting
            string currentOperation = string.Empty;

            var progress = new Progress<string>(status =>
            {
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    // Handle phase transition markers
                    if (status == "::EXTRACTING::")
                    {
                        currentOperation = _viewModel.GitCloneProgressExtracting;
                        _viewModel.GitCloneStatus = currentOperation;
                        return;
                    }

                    // If status is just a percentage, append it to current operation message
                    if (status.EndsWith('%') && status.Length <= 4 && !string.IsNullOrEmpty(currentOperation))
                    {
                        _viewModel.GitCloneStatus = $"{currentOperation} {status}";
                    }
                    else
                    {
                        // Git output or other dynamic message (contains progress info with %)
                        _viewModel.GitCloneStatus = status;
                    }
                });
            });

            GitCloneResult result;

            // Check if Git is available
            var gitAvailable = await _gitService.IsGitAvailableAsync(cancellationToken);

            if (gitAvailable)
            {
                currentOperation = _viewModel.GitCloneProgressCloning;
                _viewModel.GitCloneStatus = currentOperation;
                result = await _gitService.CloneAsync(url, targetPath, progress, cancellationToken);
            }
            else
            {
                // Fallback to ZIP download
                _viewModel.GitCloneStatus = _viewModel.GitErrorGitNotFound;
                await Task.Delay(1500, cancellationToken);

                currentOperation = _viewModel.GitCloneProgressDownloading;
                _viewModel.GitCloneStatus = currentOperation;
                result = await _zipDownloadService.DownloadAndExtractAsync(url, targetPath, progress, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!result.Success)
            {
                _repoCacheService.DeleteRepositoryDirectory(targetPath);
                _gitCloneWindow?.Close();
                _gitCloneWindow = null;
                _viewModel.GitCloneInProgress = false;
                await ShowErrorAsync(_localization.Format("Git.Error.CloneFailed", result.ErrorMessage ?? "Unknown error"));
                _toastService.Show(_localization["Toast.Git.CloneError"]);
                return;
            }

            // Successfully cloned - open the project
            _gitCloneWindow?.Close();
            _gitCloneWindow = null;
            _viewModel.GitCloneInProgress = false;
            _viewModel.ProjectSourceType = result.SourceType;
            _viewModel.CurrentBranch = result.DefaultBranch ?? "main";

            // Save repository name and URL for display
            _currentProjectDisplayName = result.RepositoryName;
            _currentRepositoryUrl = result.RepositoryUrl;

            // Save cache path for cleanup when project is closed or replaced
            _currentCachedRepoPath = targetPath;

            await TryOpenFolderAsync(result.LocalPath, fromDialog: false);

            // Load branches if Git mode
            if (result.SourceType == ProjectSourceType.GitClone)
                await RefreshGitBranchesAsync(result.LocalPath);

            if (_currentPath == result.LocalPath)
            {
                _toastService.Show(_localization["Toast.Git.CloneSuccess"]);
            }
        }
        catch (OperationCanceledException)
        {
            if (targetPath is not null)
            {
                // Use default cancellation token since operation was cancelled
                _repoCacheService.DeleteRepositoryDirectory(targetPath);
            }
        }
        catch (Exception ex)
        {
            if (targetPath is not null)
            {
                _repoCacheService.DeleteRepositoryDirectory(targetPath);
            }

            _gitCloneWindow?.Close();
            _gitCloneWindow = null;
            await ShowErrorAsync(_localization.Format("Git.Error.CloneFailed", ex.Message));
            _toastService.Show(_localization["Toast.Git.CloneError"]);
        }
        finally
        {
            _viewModel.GitCloneInProgress = false;
        }

        e.Handled = true;
    }

    private void OnGitCloneCancel(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.GitCloneInProgress)
        {
            CancelGitCloneOperation();
        }
        else
        {
            _gitCloneWindow?.Close();
            _gitCloneWindow = null;
        }
        e.Handled = true;
    }

    private void CancelGitCloneOperation()
    {
        _gitCloneCts?.Cancel();
        _viewModel.GitCloneInProgress = false;
    }

    private async void OnGitGetUpdates(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsGitMode || string.IsNullOrEmpty(_currentPath))
            return;

        try
        {
            Cursor = new Cursor(StandardCursorType.Wait);
            var statusText = string.IsNullOrWhiteSpace(_viewModel.CurrentBranch)
                ? _viewModel.StatusOperationGettingUpdates
                : _localization.Format("Status.Operation.GettingUpdatesBranch", _viewModel.CurrentBranch);
            BeginStatusOperation(statusText, indeterminate: true);

            var progress = new Progress<string>(status =>
            {
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (TryParseTrailingPercent(status, out var percent))
                        UpdateStatusOperationProgress(percent, statusText);
                    else
                        UpdateStatusOperationText(statusText);
                });
            });
            var beforeHash = await _gitService.GetHeadCommitAsync(_currentPath);
            var success = await _gitService.PullUpdatesAsync(_currentPath, progress);

            if (!success)
            {
                CompleteStatusOperation();
                await ShowErrorAsync(_localization.Format("Git.Error.UpdateFailed", "Pull failed"));
                return;
            }

            // Refresh branches and tree
            await RefreshGitBranchesAsync(_currentPath);
            await ReloadProjectAsync();

            var afterHash = await _gitService.GetHeadCommitAsync(_currentPath);
            if (!string.IsNullOrWhiteSpace(beforeHash) && !string.IsNullOrWhiteSpace(afterHash) && beforeHash == afterHash)
            {
                _toastService.Show(_localization["Toast.Git.NoUpdates"]);
                CompleteStatusOperation();
            }
            else
            {
                _toastService.Show(_localization["Toast.Git.UpdatesApplied"]);
                CompleteStatusOperation();
            }
        }
        catch (Exception ex)
        {
            CompleteStatusOperation();
            await ShowErrorAsync(_localization.Format("Git.Error.UpdateFailed", ex.Message));
        }
        finally
        {
            Cursor = new Cursor(StandardCursorType.Arrow);
        }

        e.Handled = true;
    }

    private async void OnGitBranchSwitch(object? sender, string branchName)
    {
        if (!_viewModel.IsGitMode || string.IsNullOrEmpty(_currentPath))
            return;

        try
        {
            Cursor = new Cursor(StandardCursorType.Wait);
            var statusText = _localization.Format("Status.Operation.SwitchingBranch", branchName);
            BeginStatusOperation(statusText, indeterminate: true);

            var progress = new Progress<string>(status =>
            {
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (TryParseTrailingPercent(status, out var percent))
                        UpdateStatusOperationProgress(percent, statusText);
                    else
                        UpdateStatusOperationText(statusText);
                });
            });
            var success = await _gitService.SwitchBranchAsync(_currentPath, branchName, progress);

            if (!success)
            {
                CompleteStatusOperation();
                await ShowErrorAsync(_localization.Format("Git.Error.BranchSwitchFailed", branchName));
                return;
            }

            _viewModel.CurrentBranch = branchName;
            UpdateTitle();

            // Refresh branches and tree
            await RefreshGitBranchesAsync(_currentPath);
            await ReloadProjectAsync();
            CompleteStatusOperation();
            _toastService.Show(_localization.Format("Toast.Git.BranchSwitched", branchName));
        }
        catch (Exception ex)
        {
            CompleteStatusOperation();
            await ShowErrorAsync(_localization.Format("Git.Error.BranchSwitchFailed", ex.Message));
        }
        finally
        {
            Cursor = new Cursor(StandardCursorType.Arrow);
        }
    }

    private async Task RefreshGitBranchesAsync(string repositoryPath)
    {
        try
        {
            var branches = await _gitService.GetBranchesAsync(repositoryPath);

            _viewModel.GitBranches.Clear();
            foreach (var branch in branches)
                _viewModel.GitBranches.Add(branch);

            // Update branch menu
            UpdateBranchMenu();
        }
        catch
        {
            // Ignore branch loading errors
        }
    }

    private void UpdateBranchMenu()
    {
        var branchMenuItem = _topMenuBar?.GitBranchMenuItemControl;
        if (branchMenuItem is null)
            return;

        branchMenuItem.Items.Clear();

        foreach (var branch in _viewModel.GitBranches)
        {
            var item = new MenuItem
            {
                Header = branch.IsActive ? $"✓ {branch.Name}" : $"   {branch.Name}",
                Tag = branch.Name
            };

            item.Click += (_, _) =>
            {
                if (item.Tag is string name)
                    _topMenuBar?.OnGitBranchSwitch(name);
            };

            branchMenuItem.Items.Add(item);
        }
    }

    #endregion

    private void OnAboutOpenLink(object? sender, RoutedEventArgs e)
    {
        OpenRepositoryLink();
        e.Handled = true;
    }

    private async void OnAboutCopyLink(object? sender, RoutedEventArgs e)
    {
        try
        {
            await SetClipboardTextAsync(ProjectLinks.RepositoryUrl);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
        e.Handled = true;
    }

    private void OnSearchNext(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_viewModel.SearchQuery))
            return;

        if (!_searchCoordinator.HasMatches)
        {
            _toastService.Show(_localization["Toast.NoMatches"]);
            return;
        }

        _searchCoordinator.Navigate(1);
    }

    private void OnSearchPrev(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_viewModel.SearchQuery))
            return;

        if (!_searchCoordinator.HasMatches)
        {
            _toastService.Show(_localization["Toast.NoMatches"]);
            return;
        }

        _searchCoordinator.Navigate(-1);
    }

    private void OnToggleSearch(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsProjectLoaded) return;

        if (_viewModel.SearchVisible)
        {
            CloseSearch();
            return;
        }

        ShowSearch();
    }

    private void OnSearchClose(object? sender, RoutedEventArgs e) => CloseSearch();

    private void OnToggleFilter(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsProjectLoaded) return;

        if (_viewModel.FilterVisible)
        {
            CloseFilter();
            return;
        }

        ShowFilter();
    }

    private void OnFilterClose(object? sender, RoutedEventArgs e) => CloseFilter();

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseFilter();
            e.Handled = true;
        }
    }

    private void ShowFilter()
    {
        if (!_viewModel.IsProjectLoaded) return;

        _viewModel.FilterVisible = true;
        _filterBar?.FilterBoxControl?.Focus();
        _filterBar?.FilterBoxControl?.SelectAll();
    }

    private void CloseFilter()
    {
        if (!_viewModel.FilterVisible) return;

        _viewModel.FilterVisible = false;
        _viewModel.NameFilter = string.Empty;
        ApplyFilterRealtime();
        _treeView?.Focus();
    }

    private void ApplyFilterRealtimeWithToken(CancellationToken cancellationToken)
    {
        // Fire-and-forget with cancellation support
        _ = ApplyFilterRealtimeAsync(cancellationToken);
    }

    private async Task ApplyFilterRealtimeAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrEmpty(_currentPath)) return;

            var query = _viewModel.NameFilter?.Trim();
            bool hasQuery = !string.IsNullOrWhiteSpace(query);
            var version = Interlocked.Increment(ref _filterApplyVersion);

            if (hasQuery && _filterExpansionSnapshot is null)
                _filterExpansionSnapshot = CaptureExpandedNodes();

            cancellationToken.ThrowIfCancellationRequested();

            await RefreshTreeAsync(interactiveFilter: true);

            cancellationToken.ThrowIfCancellationRequested();

            if (version != _filterApplyVersion)
                return;
            _searchCoordinator.UpdateHighlights(query);

            if (hasQuery)
            {
                TreeSearchEngine.ApplySmartExpandForFilter(
                    _viewModel.TreeNodes,
                    query!,
                    node => node.DisplayName,
                    node => node.Children,
                    (node, expanded) => node.IsExpanded = expanded);
            }
            else if (_filterExpansionSnapshot is not null)
            {
                RestoreExpandedNodes(_filterExpansionSnapshot);
                _filterExpansionSnapshot = null;
            }
        }
        catch (OperationCanceledException)
        {
            // Filter was superseded by a newer request - expected behavior
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    private void ApplyFilterRealtime()
    {
        _ = ApplyFilterRealtimeAsync(CancellationToken.None);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseSearch();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            _searchCoordinator.UpdateSearchMatches();
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                _searchCoordinator.Navigate(-1);
            else
                _searchCoordinator.Navigate(1);

            e.Handled = true;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;

        // Ctrl+O (always available)
        if (mods == KeyModifiers.Control && e.Key == Key.O)
        {
            OnOpenFolder(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Ctrl+F (available only when a project is loaded, same as WinForms miSearch.Enabled)
        if (mods == KeyModifiers.Control && e.Key == Key.F)
        {
            OnToggleSearch(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+N - Filter by name
        if (mods == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.N)
        {
            if (_viewModel.IsProjectLoaded)
                OnToggleFilter(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Esc closes the help popover
        if (e.Key == Key.Escape && _viewModel.HelpPopoverOpen)
        {
            _viewModel.HelpPopoverOpen = false;
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _viewModel.HelpDocsPopoverOpen)
        {
            _viewModel.HelpDocsPopoverOpen = false;
            e.Handled = true;
            return;
        }

        // Esc closes search
        if (e.Key == Key.Escape && _viewModel.SearchVisible)
        {
            CloseSearch();
            e.Handled = true;
            return;
        }

        // F5 refresh (same as WinForms)
        if (e.Key == Key.F5)
        {
            if (_viewModel.IsProjectLoaded)
                OnRefresh(this, new RoutedEventArgs());

            e.Handled = true;
            return;
        }

        // Zoom hotkeys (in WinForms they work even without a loaded project)
        if (mods == KeyModifiers.Control && (e.Key == Key.OemPlus || e.Key == Key.Add))
        {
            AdjustTreeFontSize(1);
            e.Handled = true;
            return;
        }

        if (mods == KeyModifiers.Control && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
        {
            AdjustTreeFontSize(-1);
            e.Handled = true;
            return;
        }

        if (mods == KeyModifiers.Control && (e.Key == Key.D0 || e.Key == Key.NumPad0))
        {
            OnZoomReset(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (!_viewModel.IsProjectLoaded)
            return;

        // Ctrl+P Options panel toggle
        if (mods == KeyModifiers.Control && e.Key == Key.P)
        {
            OnToggleSettings(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Ctrl+E Expand All
        if (mods == KeyModifiers.Control && e.Key == Key.E)
        {
            ExpandCollapseTree(expand: true);
            e.Handled = true;
            return;
        }

        // Ctrl+W Collapse All
        if (mods == KeyModifiers.Control && e.Key == Key.W)
        {
            ExpandCollapseTree(expand: false);
            e.Handled = true;
            return;
        }

        // Copy hotkeys (same as WinForms)
        if (mods == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.C)
        {
            OnCopyTree(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (mods == (KeyModifiers.Control | KeyModifiers.Alt) && e.Key == Key.C)
        {
            OnCopyTree(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (mods == (KeyModifiers.Control | KeyModifiers.Alt) && e.Key == Key.V)
        {
            OnCopyContent(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (mods == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.V)
        {
            OnCopyTreeAndContent(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
    }

    private void OnTreePointerEntered(object? sender, PointerEventArgs e)
    {
        _treeView?.Focus();
    }

    private void OnWindowPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!TreeZoomWheelHandler.TryGetZoomStep(e.KeyModifiers, e.Delta, IsPointerOverTree(e.Source), out var step))
            return;

        AdjustTreeFontSize(step);
        e.Handled = true;
    }

    private bool IsPointerOverTree(object? source)
    {
        if (_treeView is null)
            return false;

        if (ReferenceEquals(source, _treeView))
            return true;

        return source is Visual visual && visual.GetVisualAncestors().Contains(_treeView);
    }

    private void ShowSearch()
    {
        if (!_viewModel.IsProjectLoaded) return;

        _viewModel.SearchVisible = true;
        _searchBar?.SearchBoxControl?.Focus();
        _searchBar?.SearchBoxControl?.SelectAll();
    }

    private void CloseSearch()
    {
        if (!_viewModel.SearchVisible) return;

        _viewModel.SearchVisible = false;
        _viewModel.SearchQuery = string.Empty;
        _searchCoordinator.ClearSearchState();
        _treeView?.Focus();
    }

    private void OnRootAllChanged(object? sender, RoutedEventArgs e)
    {
        // Get value directly from control - event fires BEFORE binding updates ViewModel
        var check = (sender as CheckBox)?.IsChecked == true;
        _selectionCoordinator.HandleRootAllChanged(check, _currentPath);
    }

    private void OnExtensionsAllChanged(object? sender, RoutedEventArgs e)
    {
        // Get value directly from control - event fires BEFORE binding updates ViewModel
        var check = (sender as CheckBox)?.IsChecked == true;
        _selectionCoordinator.HandleExtensionsAllChanged(check);
    }

    private void OnIgnoreAllChanged(object? sender, RoutedEventArgs e)
    {
        // Get value directly from control - event fires BEFORE binding updates ViewModel
        var check = (sender as CheckBox)?.IsChecked == true;
        _selectionCoordinator.HandleIgnoreAllChanged(check, _currentPath);
    }

    private async void OnApplySettings(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Font family follows WinForms behavior: applied only on Apply
            var pending = _viewModel.PendingFontFamily;
            if (pending is not null &&
                !string.Equals(_viewModel.SelectedFontFamily?.Name, pending.Name, StringComparison.OrdinalIgnoreCase))
            {
                _viewModel.SelectedFontFamily = pending;
            }

            await RefreshTreeAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    private async Task TryOpenFolderAsync(string path, bool fromDialog)
    {
        if (!Directory.Exists(path))
        {
            await ShowErrorAsync(_localization.Format("Msg.PathNotFound", path));
            return;
        }

        if (!_scanOptions.CanReadRoot(path))
        {
            if (TryElevateAndRestart(path))
                return;

            if (BuildFlags.AllowElevation)
                await ShowErrorAsync(_localization["Msg.AccessDeniedRoot"]);
            return;
        }

        _viewModel.StatusMetricsVisible = false;
        BeginStatusOperation(_viewModel.StatusOperationLoadingProject, indeterminate: true);
        try
        {
            _currentPath = path;
            _viewModel.IsProjectLoaded = true;
            _viewModel.SettingsVisible = true;
            _viewModel.SearchVisible = false;

            // Set project source type based on how it was opened
            // If opened from dialog (File → Open), it's LocalFolder
            // If opened from Git clone, the source type is already set
            if (fromDialog)
            {
                _viewModel.ProjectSourceType = ProjectSourceType.LocalFolder;
                _viewModel.CurrentBranch = string.Empty;
                _viewModel.GitBranches.Clear();
                _currentProjectDisplayName = null;
                _currentRepositoryUrl = null;

                // Clear cached repo path when opening from dialog (local folder)
                // This ensures the previous Git clone cache gets cleaned up
                if (_currentCachedRepoPath is not null)
                {
                    _repoCacheService.DeleteRepositoryDirectory(_currentCachedRepoPath);
                    _currentCachedRepoPath = null;
                }
            }

            UpdateTitle();

            await ReloadProjectAsync();
            CompleteStatusOperation();
        }
        catch
        {
            CompleteStatusOperation();
            throw;
        }
    }

    private bool TryElevateAndRestart(string path)
    {
        if (!BuildFlags.AllowElevation)
        {
            // Store builds: never attempt elevation, just show a clear message.
            _ = ShowErrorAsync(_localization["Msg.AccessDeniedElevationRequired"]);
            return false;
        }

        if (_elevation.IsAdministrator) return false;
        if (_elevationAttempted) return false;

        _elevationAttempted = true;

        var opts = new CommandLineOptions(
            Path: path,
            Language: _localization.CurrentLanguage,
            ElevationAttempted: true);

        bool started = _elevation.TryRelaunchAsAdministrator(opts);
        if (started)
        {
            Close();
            return true;
        }

        _ = ShowInfoAsync(_localization["Msg.ElevationCanceled"]);
        return false;
    }

    private async Task ReloadProjectAsync()
    {
        if (string.IsNullOrEmpty(_currentPath)) return;

        // Clear old state before loading new project to release memory
        ClearPreviousProjectState();

        // Keep root/extension scans sequenced to avoid inconsistent UI states.
        await _selectionCoordinator.RefreshRootAndDependentsAsync(_currentPath);
        await RefreshTreeAsync();
    }

    /// <summary>
    /// Clears state from previous project to release memory before loading a new one.
    /// </summary>
    private void ClearPreviousProjectState()
    {
        // Clear search state first (holds references to TreeNodeViewModel)
        _searchCoordinator.ClearSearchState();

        // Clear filter state
        _filterExpansionSnapshot = null;
        _filterCoordinator.CancelPending();

        // Clear TreeView selection and temporarily disconnect ItemsSource
        // to force Avalonia to release all TreeViewItem containers
        if (_treeView is not null)
        {
            _treeView.SelectedItem = null;
            _treeView.ItemsSource = null;
        }

        // Recursively clear all tree nodes to break circular references and release memory
        foreach (var node in _viewModel.TreeNodes)
            node.ClearRecursive();
        _viewModel.TreeNodes.Clear();

        // Reconnect ItemsSource
        if (_treeView is not null)
            _treeView.ItemsSource = _viewModel.TreeNodes;

        // Clear current tree descriptor reference (this is the second copy of the tree)
        _currentTree = null;
        _viewModel.StatusMetricsVisible = false;
        _viewModel.StatusTreeStatsText = string.Empty;
        _viewModel.StatusContentStatsText = string.Empty;

        // Clear icon cache to release bitmaps
        _iconCache.Clear();

        // Force GC to collect released objects immediately
        // This is intentional - user expects memory to drop when switching projects
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private async Task RefreshTreeAsync(bool interactiveFilter = false)
    {
        if (string.IsNullOrEmpty(_currentPath)) return;

        using var _ = PerformanceMetrics.Measure("RefreshTreeAsync");

        // Cancel any previous refresh operation to avoid race conditions
        var cts = new CancellationTokenSource();
        var previousRefreshCts = Interlocked.Exchange(ref _refreshCts, cts);
        previousRefreshCts?.Cancel();
        previousRefreshCts?.Dispose();
        var cancellationToken = cts.Token;

        var allowedExt = new HashSet<string>(_viewModel.Extensions.Where(o => o.IsChecked).Select(o => o.Name),
            StringComparer.OrdinalIgnoreCase);
        var allowedRoot = new HashSet<string>(_viewModel.RootFolders.Where(o => o.IsChecked).Select(o => o.Name),
            StringComparer.OrdinalIgnoreCase);

        var ignoreRules = BuildIgnoreRules(_currentPath);

        var nameFilter = string.IsNullOrWhiteSpace(_viewModel.NameFilter) ? null : _viewModel.NameFilter.Trim();

        var options = new TreeFilterOptions(
            AllowedExtensions: allowedExt,
            AllowedRootFolders: allowedRoot,
            IgnoreRules: ignoreRules,
            NameFilter: nameFilter);

        var waitCursorActive = false;
        if (!interactiveFilter)
        {
            _viewModel.StatusMetricsVisible = false;
            Cursor = new Cursor(StandardCursorType.Wait);
            waitCursorActive = true;
        }
        try
        {
            BuildTreeResult result;

            // Build the tree off the UI thread to keep the window responsive on large folders.
            using (PerformanceMetrics.Measure("BuildTree"))
            {
                result = await Task.Run(() => _buildTree.Execute(new BuildTreeRequest(_currentPath, options)), cancellationToken);
            }

            // Check if this operation was superseded by a newer one
            cancellationToken.ThrowIfCancellationRequested();

            // Clear references to old tree BEFORE replacing to allow GC
            _searchCoordinator.ClearSearchState();
            if (_treeView is not null)
                _treeView.SelectedItem = null;

            // Recursively clear old tree nodes to release memory (DisplayInlines, Children, Icons)
            foreach (var node in _viewModel.TreeNodes)
                node.ClearRecursive();
            _viewModel.TreeNodes.Clear();

            _currentTree = result;

            if (result.RootAccessDenied && TryElevateAndRestart(_currentPath))
                return;

            var root = BuildTreeViewModel(result.Root, null);

            // Set root display name: use repository name for git clones, folder name for local
            try
            {
                root.DisplayName = !string.IsNullOrEmpty(_currentProjectDisplayName)
                    ? _currentProjectDisplayName
                    : new DirectoryInfo(_currentPath!).Name;
            }
            catch
            {
                // ignore
            }

            _viewModel.TreeNodes.Add(root);
            root.IsExpanded = true;

            if (!interactiveFilter && !string.IsNullOrWhiteSpace(nameFilter) && root.Children.Count == 0)
                _toastService.Show(_localization["Toast.NoMatches"]);

            _searchCoordinator.UpdateSearchMatches();

            // Initialize file metrics cache in background for real-time status bar updates
            // Only do full scan on initial load, not on interactive filter changes
            if (!interactiveFilter)
            {
                if (waitCursorActive)
                {
                    Cursor = new Cursor(StandardCursorType.Arrow);
                    waitCursorActive = false;
                }

                // Animate settings panel BEFORE metrics calculation starts
                // so user sees the panel immediately after tree renders
                if (_viewModel.SettingsVisible && !_settingsAnimating)
                    AnimateSettingsPanel(true);

                UpdateStatusOperationText(_viewModel.StatusOperationCalculatingData);
                await InitializeFileMetricsCacheAsync(cancellationToken);
            }
            else
            {
                // For filter changes, just recalculate from existing cache
                RecalculateMetricsAsync();
            }

            // Suggest GC to collect old tree objects after building new one
            // Using non-blocking mode to avoid UI freeze
            if (!interactiveFilter)
                GC.Collect(1, GCCollectionMode.Optimized, blocking: false);
        }
        finally
        {
            if (!interactiveFilter && waitCursorActive)
                Cursor = new Cursor(StandardCursorType.Arrow);
        }
    }

    private TreeNodeViewModel BuildTreeViewModel(TreeNodeDescriptor descriptor, TreeNodeViewModel? parent)
    {
        var icon = _iconCache.GetIcon(descriptor.IconKey);
        var node = new TreeNodeViewModel(descriptor, parent, icon);

        foreach (var child in descriptor.Children)
        {
            var childViewModel = BuildTreeViewModel(child, node);
            node.Children.Add(childViewModel);
        }

        return node;
    }

    private void UpdateTitle()
    {
        if (string.IsNullOrWhiteSpace(_currentPath))
        {
            _viewModel.Title = MainWindowViewModel.BaseTitleWithAuthor;
            return;
        }

        // For Git clones: show full URL + branch in square brackets
        // For local folders: show full path
        if (_viewModel.IsGitMode && !string.IsNullOrEmpty(_currentRepositoryUrl))
        {
            var branchDisplay = !string.IsNullOrEmpty(_viewModel.CurrentBranch)
                ? $" [{_viewModel.CurrentBranch}]"
                : string.Empty;
            _viewModel.Title = $"{MainWindowViewModel.BaseTitle} - {_currentRepositoryUrl}{branchDisplay}";
        }
        else
        {
            var displayPath = !string.IsNullOrEmpty(_currentProjectDisplayName)
                ? _currentProjectDisplayName
                : _currentPath;
            _viewModel.Title = $"{MainWindowViewModel.BaseTitle} - {displayPath}";
        }
    }

    private IgnoreRules BuildIgnoreRules(string rootPath)
    {
        var selected = _selectionCoordinator.GetSelectedIgnoreOptionIds();
        return _ignoreRulesService.Build(rootPath, selected);
    }

    private void BeginStatusOperation(string text, bool indeterminate = true)
    {
        _viewModel.StatusOperationText = text;
        _viewModel.StatusBusy = true;
        _viewModel.StatusProgressIsIndeterminate = indeterminate;
        if (indeterminate)
            _viewModel.StatusProgressValue = 0;
    }

    private void UpdateStatusOperationText(string text)
    {
        _viewModel.StatusOperationText = text;
    }

    private void UpdateStatusOperationProgress(double percent, string? text = null)
    {
        if (!string.IsNullOrWhiteSpace(text))
            _viewModel.StatusOperationText = text;

        _viewModel.StatusBusy = true;
        _viewModel.StatusProgressIsIndeterminate = false;
        _viewModel.StatusProgressValue = Math.Clamp(percent, 0, 100);
    }

    private void CompleteStatusOperation()
    {
        // If background metrics calculation is still active, don't fully hide progress
        if (_isBackgroundMetricsActive)
        {
            UpdateStatusOperationText(_viewModel.StatusOperationCalculatingData);
            return;
        }

        _viewModel.StatusOperationText = string.Empty;
        _viewModel.StatusBusy = false;
        _viewModel.StatusProgressIsIndeterminate = true;
        _viewModel.StatusProgressValue = 0;
    }

    /// <summary>
    /// Cancels any active background metrics calculation.
    /// Call this before starting user-initiated operations that need the status bar.
    /// </summary>
    private void CancelBackgroundMetricsCalculation()
    {
        _isBackgroundMetricsActive = false;
        _metricsCalculationCts?.Cancel();
    }


    private static bool TryParseTrailingPercent(string status, out double percent)
    {
        percent = 0;
        if (string.IsNullOrWhiteSpace(status))
            return false;

        var trimmed = status.Trim();
        if (!trimmed.EndsWith('%'))
            return false;

        var lastSpace = trimmed.LastIndexOf(' ');
        var token = lastSpace >= 0 ? trimmed[(lastSpace + 1)..] : trimmed;
        token = token.TrimEnd('%');

        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out percent) ||
               double.TryParse(token, NumberStyles.Float, CultureInfo.CurrentCulture, out percent);
    }

    private async Task SetClipboardTextAsync(string content)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;

        if (clipboard != null)
            await clipboard.SetTextAsync(content);
    }

    private static void OpenRepositoryLink()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = ProjectLinks.RepositoryUrl,
            UseShellExecute = true
        });
    }

    private bool EnsureTreeReady() => _currentTree is not null && !string.IsNullOrWhiteSpace(_currentPath);

    private HashSet<string> GetCheckedPaths()
    {
        var selected = new HashSet<string>(PathComparer.Default);
        foreach (var node in _viewModel.TreeNodes)
            CollectChecked(node, selected);
        return selected;
    }

    private static IEnumerable<string> EnumerateFilePaths(TreeNodeDescriptor node)
    {
        if (!node.IsDirectory)
        {
            yield return node.FullPath;
            yield break;
        }

        foreach (var child in node.Children)
        {
            foreach (var path in EnumerateFilePaths(child))
                yield return path;
        }
    }

    private static void CollectChecked(TreeNodeViewModel node, HashSet<string> selected)
    {
        if (node.IsChecked == true)
            selected.Add(node.FullPath);

        foreach (var child in node.Children)
            CollectChecked(child, selected);
    }

    private HashSet<string> CaptureExpandedNodes()
    {
        return _viewModel.TreeNodes
            .SelectMany(node => node.Flatten())
            .Where(node => node.IsExpanded)
            .Select(node => node.FullPath)
            .ToHashSet(PathComparer.Default);
    }

    private void RestoreExpandedNodes(HashSet<string> expandedPaths)
    {
        foreach (var node in _viewModel.TreeNodes.SelectMany(item => item.Flatten()))
            node.IsExpanded = expandedPaths.Contains(node.FullPath);

        if (_viewModel.TreeNodes.FirstOrDefault() is { } root && !root.IsExpanded)
            root.IsExpanded = true;
    }

    /// <summary>
    /// Validates that URL looks like a valid Git repository URL.
    /// Accepts URLs from common Git hosting services (GitHub, GitLab, Bitbucket, etc.)
    /// or any URL ending with .git
    /// </summary>
    private static bool IsValidGitRepositoryUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        try
        {
            // Try to parse as URI
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;

            // Must be HTTP or HTTPS
            if (uri.Scheme != "http" && uri.Scheme != "https")
                return false;

            var host = uri.Host.ToLowerInvariant();
            var path = uri.AbsolutePath.ToLowerInvariant();

            // Check for common Git hosting services
            var validHosts = new[]
            {
                "github.com",
                "gitlab.com",
                "bitbucket.org",
                "gitea.com",
                "codeberg.org",
                "sourceforge.net",
                "git.sr.ht"
            };

            // Allow subdomains (e.g., gitlab.mycompany.com)
            var isKnownHost = validHosts.Any(h => host == h || host.EndsWith("." + h));

            // Or URL ends with .git extension
            var hasGitExtension = path.EndsWith(".git");

            // Or contains /git/ in path (common for self-hosted instances)
            var hasGitInPath = path.Contains("/git/");

            return isKnownHost || hasGitExtension || hasGitInPath;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if internet connection is available by attempting to connect to reliable hosts.
    /// Returns true if connection successful, false otherwise.
    /// This is a simple check - we try to resolve DNS and connect to well-known hosts.
    /// </summary>
    private static async Task<bool> CheckInternetConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Try to connect to multiple reliable hosts to avoid false negatives
            // Use different providers to increase reliability
            var hosts = new[]
            {
                "https://www.github.com",
                "https://www.google.com",
                "https://www.cloudflare.com"
            };

            using var httpClient = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(5)
            };

            // Try each host - if any succeeds, we have internet
            foreach (var host in hosts)
            {
                try
                {
                    using var response = await httpClient.GetAsync(host, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    // If we get any response (even error status codes), it means we have connectivity
                    return true;
                }
                catch
                {
                    // Try next host
                    continue;
                }
            }

            // All hosts failed
            return false;
        }
        catch
        {
            // If exception occurs, assume no internet
            return false;
        }
    }

    #region Real-time Status Metrics

    /// <summary>
    /// Cached file metrics for efficient real-time updates.
    /// </summary>
    private sealed record FileMetricsData(long Size, int LineCount, int CharCount)
    {
        public int EstimatedTokens => (int)Math.Ceiling(CharCount / 4.0);
    }

    /// <summary>
    /// Subscribe to checkbox change events for real-time metrics updates.
    /// </summary>
    private void SubscribeToMetricsUpdates()
    {
        TreeNodeViewModel.GlobalCheckedChanged += OnTreeNodeCheckedChanged;
    }

    /// <summary>
    /// Unsubscribe from checkbox change events.
    /// </summary>
    private void UnsubscribeFromMetricsUpdates()
    {
        TreeNodeViewModel.GlobalCheckedChanged -= OnTreeNodeCheckedChanged;
    }

    /// <summary>
    /// Handle checkbox change with debouncing to avoid excessive recalculations.
    /// </summary>
    private void OnTreeNodeCheckedChanged(object? sender, EventArgs e)
    {
        // Debounce rapid checkbox changes (e.g., when selecting parent node)
        _metricsDebounceTimer?.Stop();
        _metricsDebounceTimer = new global::Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _metricsDebounceTimer.Tick += (_, _) =>
        {
            _metricsDebounceTimer.Stop();
            RecalculateMetricsAsync();
        };
        _metricsDebounceTimer.Start();
    }

    /// <summary>
    /// Initialize file metrics cache after tree is built.
    /// Scans all files in parallel using IFileContentAnalyzer as single source of truth.
    /// Binary files are skipped via extension check (fast) or null-byte detection.
    /// </summary>
    private async Task InitializeFileMetricsCacheAsync(CancellationToken cancellationToken)
    {
        // Cancel any previous calculation
        _metricsCalculationCts?.Cancel();
        _metricsCalculationCts = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _metricsCalculationCts.Token);

        _isBackgroundMetricsActive = true;
        try
        {
            UpdateStatusOperationText(_viewModel.StatusOperationCalculatingData);
            _viewModel.StatusBusy = true;
            _viewModel.StatusProgressIsIndeterminate = false;
            _viewModel.StatusProgressValue = 0;

            // Collect all file paths from tree
            var filePaths = new List<string>();
            foreach (var node in _viewModel.TreeNodes.SelectMany(n => n.Flatten()))
            {
                if (!node.Descriptor.IsDirectory && !node.Descriptor.IsAccessDenied)
                    filePaths.Add(node.FullPath);
            }

            // Clear cache before scanning
            lock (_metricsLock)
            {
                _fileMetricsCache.Clear();
            }

            var totalFiles = filePaths.Count;
            if (totalFiles == 0)
            {
                _isBackgroundMetricsActive = false;
                RecalculateMetricsAsync();
                _viewModel.StatusMetricsVisible = true;
                CompleteStatusOperation();
                return;
            }

            var processedCount = 0;
            var lastProgressPercent = 0;

            // Process files in parallel for better performance
            // Limit parallelism to avoid overwhelming the disk I/O
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
                CancellationToken = linkedCts.Token
            };

            await Parallel.ForEachAsync(filePaths, parallelOptions, async (filePath, ct) =>
            {
                try
                {
                    // GetTextFileMetricsAsync uses streaming - no full content in memory:
                    // 1. Known binary extensions - instant skip (no I/O)
                    // 2. Null-byte detection in first 512 bytes (fast binary check)
                    // 3. Streams through file counting lines/chars without storing content
                    // 4. Large files (>10MB) get estimated metrics
                    var metrics = await _fileContentAnalyzer.GetTextFileMetricsAsync(filePath, ct)
                        .ConfigureAwait(false);

                    // Skip binary files - they won't be exported
                    if (metrics is not null)
                    {
                        lock (_metricsLock)
                        {
                            _fileMetricsCache[filePath] = new FileMetricsData(
                                metrics.SizeBytes,
                                metrics.LineCount,
                                metrics.CharCount);
                        }
                    }

                    // Update progress periodically (every 2%)
                    var current = Interlocked.Increment(ref processedCount);
                    var progressPercent = (int)(current * 100.0 / totalFiles);
                    if (progressPercent >= lastProgressPercent + 2)
                    {
                        Interlocked.Exchange(ref lastProgressPercent, progressPercent);
                        await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (_isBackgroundMetricsActive)
                                _viewModel.StatusProgressValue = progressPercent;
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Skip files that can't be read
                    Interlocked.Increment(ref processedCount);
                }
            });

            // Calculation completed successfully
            _isBackgroundMetricsActive = false;
            _viewModel.StatusProgressValue = 100;
            RecalculateMetricsAsync();
            _viewModel.StatusMetricsVisible = true;
            CompleteStatusOperation();
        }
        catch (OperationCanceledException)
        {
            // Cancelled by user action - show partial results if available
            _isBackgroundMetricsActive = false;
            if (_fileMetricsCache.Count > 0)
            {
                RecalculateMetricsAsync();
                _viewModel.StatusMetricsVisible = true;
            }
            CompleteStatusOperation();
        }
    }

    /// <summary>
    /// Recalculate both tree and content metrics based on current selection.
    /// </summary>
    private void RecalculateMetricsAsync()
    {
        if (!_viewModel.IsProjectLoaded || _viewModel.TreeNodes.Count == 0)
        {
            UpdateStatusBarMetrics(0, 0, 0, 0, 0, 0);
            return;
        }

        var treeRoot = _viewModel.TreeNodes.FirstOrDefault();
        if (treeRoot == null)
            return;

        // Determine which nodes are selected (if any explicitly checked, use those; otherwise use all)
        var hasAnyChecked = HasAnyCheckedNodes(treeRoot);

        // Calculate tree metrics (ASCII tree structure)
        var treeMetrics = CalculateTreeMetrics(treeRoot, hasAnyChecked);

        // Calculate content metrics (file contents)
        var contentMetrics = CalculateContentMetrics(treeRoot, hasAnyChecked);

        // Update UI on dispatcher thread
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            UpdateStatusBarMetrics(
                treeMetrics.Lines, treeMetrics.Chars, treeMetrics.Tokens,
                contentMetrics.Lines, contentMetrics.Chars, contentMetrics.Tokens);
        });
    }

    /// <summary>
    /// Check if any node in the tree is explicitly checked.
    /// </summary>
    private static bool HasAnyCheckedNodes(TreeNodeViewModel root)
    {
        if (root.IsChecked == true)
            return true;

        foreach (var child in root.Children)
        {
            if (HasAnyCheckedNodes(child))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Calculate metrics for tree output based on selected format (ASCII or JSON).
    /// Estimates lines, characters, and tokens for the tree structure text.
    /// </summary>
    private (int Lines, int Chars, int Tokens) CalculateTreeMetrics(TreeNodeViewModel root, bool hasSelection)
    {
        int lineCount = 0;
        int charCount = 0;

        var isJson = _viewModel.SelectedExportFormat == ExportFormat.Json;

        // Count nodes that will be included in tree output
        CountTreeNodes(root, hasSelection, ref lineCount, ref charCount, 0, true, isJson);

        if (isJson)
        {
            // JSON overhead: opening/closing braces, array brackets, etc.
            // Estimate: ~20 chars for root structure + ~10 per node for JSON syntax
            int nodeCount = lineCount;
            charCount += 20 + (nodeCount * 10);
            // JSON typically has more lines due to formatting
            lineCount += nodeCount / 2;
        }

        // Token estimate: ~4 chars per token
        int tokens = (int)Math.Ceiling(charCount / 4.0);

        return (lineCount, charCount, tokens);
    }

    /// <summary>
    /// Recursively count tree nodes and estimate character count.
    /// </summary>
    private void CountTreeNodes(TreeNodeViewModel node, bool hasSelection, ref int lineCount, ref int charCount, int depth, bool isRoot, bool isJson)
    {
        // If there's a selection, only count nodes that are checked or have checked descendants
        if (hasSelection && node.IsChecked == false)
            return;

        // Each node is one line
        lineCount++;

        if (isJson)
        {
            // JSON format: {"name": "filename", "type": "file", "children": [...]}
            // Estimate: indent (depth * 2) + "name" key + value + type + structure chars
            int indent = depth * 2;
            int nameLen = node.DisplayName.Length;
            // ~30 chars for JSON syntax per node + name length + indent
            charCount += indent + nameLen + 30;
        }
        else
        {
            // ASCII tree format: "├── filename" or "└── filename"
            // Estimate chars per line: prefix (depth * 4) + name + newline
            int prefixLen = isRoot ? 0 : depth * 4;
            charCount += prefixLen + node.DisplayName.Length + 1; // +1 for newline
        }

        // Recursively count children
        foreach (var child in node.Children)
        {
            CountTreeNodes(child, hasSelection, ref lineCount, ref charCount, depth + 1, false, isJson);
        }
    }

    /// <summary>
    /// Calculate metrics for file content output.
    /// Sums up metrics from cached file data for selected files.
    /// Only counts files that are actually text (present in cache) - binary files are excluded.
    /// </summary>
    private (int Lines, int Chars, int Tokens) CalculateContentMetrics(TreeNodeViewModel root, bool hasSelection)
    {
        int totalLines = 0;
        int totalChars = 0;
        int textFileCount = 0;

        // Collect file paths based on selection state
        var filePaths = new List<string>();
        CollectFilePaths(root, hasSelection, filePaths);

        // Sum up metrics from cache - only text files are in cache (binary files are excluded)
        lock (_metricsLock)
        {
            foreach (var path in filePaths)
            {
                if (_fileMetricsCache.TryGetValue(path, out var metrics))
                {
                    totalLines += metrics.LineCount;
                    totalChars += metrics.CharCount;
                    textFileCount++;
                }
                // Binary files are not in cache, so they are automatically excluded
            }
        }

        // Add overhead for file path headers in output (e.g., "// path/to/file.cs" per file)
        // Each file adds: separator line + path comment + blank line = ~3 lines, ~80 chars average
        // Only count text files that will actually be exported
        int headerOverhead = textFileCount * 3;
        int headerCharOverhead = textFileCount * 80;
        totalLines += headerOverhead;
        totalChars += headerCharOverhead;

        int tokens = (int)Math.Ceiling(totalChars / 4.0);

        return (totalLines, totalChars, tokens);
    }

    /// <summary>
    /// Collect file paths from tree based on selection state.
    /// </summary>
    private static void CollectFilePaths(TreeNodeViewModel node, bool hasSelection, List<string> filePaths)
    {
        // If there's a selection, only include checked files
        if (hasSelection)
        {
            if (node.IsChecked == true && !node.Descriptor.IsDirectory)
            {
                filePaths.Add(node.FullPath);
            }
            else if (node.IsChecked != false) // null or true - has some checked descendants
            {
                foreach (var child in node.Children)
                    CollectFilePaths(child, hasSelection, filePaths);
            }
        }
        else
        {
            // No selection - include all files
            if (!node.Descriptor.IsDirectory && !node.Descriptor.IsAccessDenied)
            {
                filePaths.Add(node.FullPath);
            }

            foreach (var child in node.Children)
                CollectFilePaths(child, hasSelection, filePaths);
        }
    }

    /// <summary>
    /// Update status bar with calculated metrics.
    /// </summary>
    private void UpdateStatusBarMetrics(
        int treeLines, int treeChars, int treeTokens,
        int contentLines, int contentChars, int contentTokens)
    {
        // Format: [Lines: X | Chars: X | ~Tokens: X]
        var linesLabel = _localization.Format("Status.Metric.Lines", "{0}");
        var charsLabel = _localization.Format("Status.Metric.Chars", "{0}");
        var tokensLabel = _localization.Format("Status.Metric.Tokens", "{0}");

        // Extract format pattern (e.g., "Lines: {0}" -> "Lines:")
        var linesPrefix = linesLabel.Replace("{0}", "").Trim();
        var charsPrefix = charsLabel.Replace("{0}", "").Trim();
        var tokensPrefix = tokensLabel.Replace("{0}", "").Trim();

        _viewModel.StatusTreeStatsText = $"[{linesPrefix} {FormatNumber(treeLines)} | {charsPrefix} {FormatNumber(treeChars)} | {tokensPrefix} {FormatNumber(treeTokens)}]";
        _viewModel.StatusContentStatsText = $"[{linesPrefix} {FormatNumber(contentLines)} | {charsPrefix} {FormatNumber(contentChars)} | {tokensPrefix} {FormatNumber(contentTokens)}]";
    }

    /// <summary>
    /// Format large numbers with K/M suffixes for readability.
    /// </summary>
    private static string FormatNumber(int value)
    {
        return value switch
        {
            >= 1_000_000 => $"{value / 1_000_000.0:F1}M",
            >= 10_000 => $"{value / 1_000.0:F1}K",
            _ => value.ToString("N0")
        };
    }

    private void OnStatusOperationCancelRequested(object? sender, RoutedEventArgs e)
    {
        // Reserved for future cancellation support.
    }

    #endregion

}
