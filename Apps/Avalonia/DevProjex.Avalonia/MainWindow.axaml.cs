using System;
using System.ComponentModel;
using System.Diagnostics;
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
    private readonly IconCache _iconCache;
    private readonly IElevationService _elevation;
    private readonly ThemePresetStore _themePresetStore;
    private readonly IGitRepositoryService _gitService;
    private readonly IRepoCacheService _repoCacheService;
    private readonly IZipDownloadService _zipDownloadService;

    private readonly MainWindowViewModel _viewModel;
    private readonly TreeSearchCoordinator _searchCoordinator;
    private readonly NameFilterCoordinator _filterCoordinator;
    private readonly ThemeBrushCoordinator _themeBrushCoordinator;
    private readonly SelectionSyncCoordinator _selectionCoordinator;

    private BuildTreeResult? _currentTree;
    private string? _currentPath;
    private string? _currentProjectDisplayName;
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
        _iconCache = new IconCache(services.IconStore);
        _elevation = services.Elevation;
        _themePresetStore = services.ThemePresetStore;
        _gitService = services.GitRepositoryService;
        _repoCacheService = services.RepoCacheService;
        _zipDownloadService = services.ZipDownloadService;

        _viewModel = new MainWindowViewModel(_localization, services.HelpContentProvider);
        DataContext = _viewModel;

        InitializeComponent();

        InitializeThemePresets();

        _viewModel.UpdateHelpPopoverMaxSize(Bounds.Size);
        PropertyChanged += OnWindowPropertyChanged;

        _treeView = this.FindControl<TreeView>("ProjectTree");
        _topMenuBar = this.FindControl<TopMenuBarView>("TopMenuBar");
        _searchBar = this.FindControl<SearchBarView>("SearchBar");
        _filterBar = this.FindControl<FilterBarView>("FilterBar");

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
                _searchCoordinator.UpdateSearchMatches();
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

        // Dispose coordinators
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
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

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
        UpdateTitle();

        if (_currentPath is not null)
        {
            _selectionCoordinator.PopulateIgnoreOptionsForRootSelection(_selectionCoordinator.GetSelectedRootFolders());
        }
    }

    private async Task ShowErrorAsync(string message) =>
        await MessageDialog.ShowAsync(this, _localization["Msg.ErrorTitle"], message);

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
        try
        {
            await ReloadProjectAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private async void OnCopyFullTree(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!EnsureTreeReady()) return;

            var content = _treeExport.BuildFullTree(_currentPath!, _currentTree!.Root);
            await SetClipboardTextAsync(content);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    private async void OnCopySelectedTree(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!EnsureTreeReady()) return;

            var selected = GetCheckedPaths();
            var content = _treeExport.BuildSelectedTree(_currentPath!, _currentTree!.Root, selected);
            if (string.IsNullOrWhiteSpace(content))
            {
                await ShowInfoAsync(_localization["Msg.NoCheckedTree"]);
                return;
            }

            await SetClipboardTextAsync(content);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    private async void OnCopySelectedContent(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!EnsureTreeReady()) return;

            var selected = GetCheckedPaths();
            var files = selected.Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (files.Count == 0)
            {
                await ShowInfoAsync(_localization["Msg.NoCheckedFiles"]);
                return;
            }

            // Run file reading off UI thread
            var content = await Task.Run(() => _contentExport.BuildAsync(files, CancellationToken.None));
            if (string.IsNullOrWhiteSpace(content))
            {
                await ShowInfoAsync(_localization["Msg.NoTextContent"]);
                return;
            }

            await SetClipboardTextAsync(content);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    private async void OnCopyTreeAndContent(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!EnsureTreeReady()) return;

            var selected = GetCheckedPaths();
            // Run file reading off UI thread
            var content = await Task.Run(() => _treeAndContentExport.BuildAsync(_currentPath!, _currentTree!.Root, selected, CancellationToken.None));
            await SetClipboardTextAsync(content);
        }
        catch (Exception ex)
        {
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
        _viewModel.SettingsVisible = !_viewModel.SettingsVisible;
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
        _viewModel.GitClonePopoverOpen = true;
        e.Handled = true;
    }

    private void OnGitCloneClose(object? sender, RoutedEventArgs e)
    {
        CancelGitCloneOperation();
        _viewModel.GitClonePopoverOpen = false;
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

        _gitCloneCts?.Cancel();
        _gitCloneCts = new CancellationTokenSource();
        var cancellationToken = _gitCloneCts.Token;

        _viewModel.GitCloneInProgress = true;
        _viewModel.GitCloneStatus = _viewModel.GitCloneProgressCheckingGit;

        string? targetPath = null;

        try
        {
            targetPath = _repoCacheService.CreateRepositoryDirectory(url);
            var progress = new Progress<string>(status =>
            {
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _viewModel.GitCloneStatus = status;
                });
            });

            GitCloneResult result;

            // Check if Git is available
            var gitAvailable = await _gitService.IsGitAvailableAsync(cancellationToken);

            if (gitAvailable)
            {
                _viewModel.GitCloneStatus = _viewModel.GitCloneProgressCloning;
                result = await _gitService.CloneAsync(url, targetPath, progress, cancellationToken);
            }
            else
            {
                // Fallback to ZIP download
                _viewModel.GitCloneStatus = _viewModel.GitErrorGitNotFound;
                await Task.Delay(1500, cancellationToken);

                _viewModel.GitCloneStatus = _viewModel.GitCloneProgressDownloading;
                result = await _zipDownloadService.DownloadAndExtractAsync(url, targetPath, progress, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!result.Success)
            {
                _repoCacheService.DeleteRepositoryDirectory(targetPath);
                _viewModel.GitClonePopoverOpen = false;
                _viewModel.GitCloneInProgress = false;
                await ShowErrorAsync(_localization.Format("Git.Error.CloneFailed", result.ErrorMessage ?? "Unknown error"));
                return;
            }

            // Successfully cloned - open the project
            _viewModel.GitClonePopoverOpen = false;
            _viewModel.GitCloneInProgress = false;
            _viewModel.ProjectSourceType = result.SourceType;
            _viewModel.CurrentBranch = result.DefaultBranch ?? "main";

            // Save repository name for display
            _currentProjectDisplayName = result.RepositoryName;

            await TryOpenFolderAsync(result.LocalPath, fromDialog: false);

            // Load branches if Git mode
            if (result.SourceType == ProjectSourceType.GitClone)
                await RefreshGitBranchesAsync(result.LocalPath);
        }
        catch (OperationCanceledException)
        {
            if (targetPath is not null)
                _repoCacheService.DeleteRepositoryDirectory(targetPath);
        }
        catch (Exception ex)
        {
            if (targetPath is not null)
                _repoCacheService.DeleteRepositoryDirectory(targetPath);

            _viewModel.GitClonePopoverOpen = false;
            await ShowErrorAsync(_localization.Format("Git.Error.CloneFailed", ex.Message));
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
            _viewModel.GitClonePopoverOpen = false;
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

            var progress = new Progress<string>(_ => { });
            var success = await _gitService.PullUpdatesAsync(_currentPath, progress);

            if (!success)
            {
                await ShowErrorAsync(_localization.Format("Git.Error.UpdateFailed", "Pull failed"));
                return;
            }

            // Refresh branches and tree
            await RefreshGitBranchesAsync(_currentPath);
            await ReloadProjectAsync();
        }
        catch (Exception ex)
        {
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

            var progress = new Progress<string>(_ => { });
            var success = await _gitService.SwitchBranchAsync(_currentPath, branchName, progress);

            if (!success)
            {
                await ShowErrorAsync(_localization.Format("Git.Error.BranchSwitchFailed", branchName));
                return;
            }

            _viewModel.CurrentBranch = branchName;

            // Refresh branches and tree
            await RefreshGitBranchesAsync(_currentPath);
            await ReloadProjectAsync();
        }
        catch (Exception ex)
        {
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

    private void OnSearchNext(object? sender, RoutedEventArgs e) => _searchCoordinator.Navigate(1);

    private void OnSearchPrev(object? sender, RoutedEventArgs e) => _searchCoordinator.Navigate(-1);

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

            await RefreshTreeAsync();

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

        // Ctrl+O (всегда доступно)
        if (mods == KeyModifiers.Control && e.Key == Key.O)
        {
            OnOpenFolder(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Ctrl+F (только при загруженном проекте — как WinForms miSearch.Enabled)
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

        // Esc закрывает help popover
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

        // Esc закрывает поиск
        if (e.Key == Key.Escape && _viewModel.SearchVisible)
        {
            CloseSearch();
            e.Handled = true;
            return;
        }

        // F5 refresh (как WinForms)
        if (e.Key == Key.F5)
        {
            if (_viewModel.IsProjectLoaded)
                OnRefresh(this, new RoutedEventArgs());

            e.Handled = true;
            return;
        }

        // Zoom горячие клавиши (в WinForms работают даже без проекта)
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

        // Copy hotkeys (как WinForms)
        if (mods == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.C)
        {
            OnCopyFullTree(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (mods == (KeyModifiers.Control | KeyModifiers.Alt) && e.Key == Key.C)
        {
            OnCopySelectedTree(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (mods == (KeyModifiers.Control | KeyModifiers.Alt) && e.Key == Key.V)
        {
            OnCopySelectedContent(this, new RoutedEventArgs());
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
            // Font family — как WinForms: применяется только по Apply
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
        }

        UpdateTitle();

        await ReloadProjectAsync();
    }

    private bool TryElevateAndRestart(string path)
    {
        // In Store builds, show a localized hint instead of attempting elevation.
        // Note: fire-and-forget here is acceptable as this is a terminal state (window closing or showing info)
        if (!BuildFlags.AllowElevation)
        {
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

        // Clear icon cache to release bitmaps
        _iconCache.Clear();

        // Force GC to collect released objects immediately
        // This is intentional - user expects memory to drop when switching projects
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private async Task RefreshTreeAsync()
    {
        if (string.IsNullOrEmpty(_currentPath)) return;

        using var _ = PerformanceMetrics.Measure("RefreshTreeAsync");

        // Cancel any previous refresh operation to avoid race conditions
        _refreshCts?.Cancel();
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
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

        Cursor = new Cursor(StandardCursorType.Wait);
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

            _searchCoordinator.UpdateSearchMatches();

            // Suggest GC to collect old tree objects after building new one
            // Using non-blocking mode to avoid UI freeze
            GC.Collect(1, GCCollectionMode.Optimized, blocking: false);
        }
        finally
        {
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

        // Use repository name for Git clones, otherwise use full path
        var displayPath = !string.IsNullOrEmpty(_currentProjectDisplayName)
            ? _currentProjectDisplayName
            : _currentPath;

        _viewModel.Title = $"{MainWindowViewModel.BaseTitle} - {displayPath}";
    }

    private IgnoreRules BuildIgnoreRules(string rootPath)
    {
        var selected = _selectionCoordinator.GetSelectedIgnoreOptionIds();
        return _ignoreRulesService.Build(rootPath, selected);
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
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in _viewModel.TreeNodes)
            CollectChecked(node, selected);
        return selected;
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
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void RestoreExpandedNodes(HashSet<string> expandedPaths)
    {
        foreach (var node in _viewModel.TreeNodes.SelectMany(item => item.Flatten()))
            node.IsExpanded = expandedPaths.Contains(node.FullPath);

        if (_viewModel.TreeNodes.FirstOrDefault() is { } root && !root.IsExpanded)
            root.IsExpanded = true;
    }

}
