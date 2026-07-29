using DevProjex.Infrastructure.ThemePresets;
using DevProjex.Avalonia.Services;
using AvaloniaThemeVariant = Avalonia.Styling.ThemeVariant;
using ThemePreset = DevProjex.Infrastructure.ThemePresets.ThemePreset;
using ThemePresetEffect = DevProjex.Infrastructure.ThemePresets.ThemeEffectMode;
using ThemePresetVariant = DevProjex.Infrastructure.ThemePresets.ThemeVariant;

namespace DevProjex.Avalonia.Coordinators;

internal sealed class AppearanceSettingsController(
    Window window,
    MainWindowViewModel viewModel,
    LocalizationService localization,
    UserSettingsStore userSettingsStore,
    ThemeSettingsStore themeSettingsStore,
    ThemeBrushCoordinator themeBrushes,
    WorkspacePresentationController workspace,
    Action refreshActiveQueryHighlights,
    bool isMicaSupported,
    AppLanguage? commandLineLanguage)
{
    private static readonly TimeSpan StartupStoreLockTimeout =
        TimeSpan.FromMilliseconds(100);

    private UserSettingsDb _userSettings = new();
    private ThemeSettingsDocument _themeSettings = new();
    private ThemePresetSession? _themeSession;
    private ThemePresetVariant _currentTheme = ThemePresetVariant.Dark;
    private ThemePresetEffect _currentEffect = ThemePresetEffect.Transparent;
    private bool _wasThemePopoverOpen;
    private int _applyingPresetDepth;

    public AppViewSettings ViewSettings =>
        _userSettings.ViewSettings ?? new AppViewSettings();

    public bool IsApplyingPreset =>
        Volatile.Read(ref _applyingPresetDepth) != 0;

    public void Initialize()
    {
        _userSettings =
            userSettingsStore.LoadForStartup(StartupStoreLockTimeout);
        ApplySavedLanguagePreference(ViewSettings);

        _themeSettings =
            themeSettingsStore.LoadForStartup(StartupStoreLockTimeout);
        _themeSession =
            new ThemePresetSession(themeSettingsStore, _themeSettings);

        _currentTheme = _themeSession.CurrentTheme;
        _currentEffect = ThemeEffectPlatformSupport.Normalize(
            _themeSession.CurrentEffect,
            isMicaSupported);

        viewModel.IsDarkTheme =
            _currentTheme == ThemePresetVariant.Dark;
        ApplyEffectMode(_currentEffect);
        ApplyPresetValues(themeSettingsStore.GetPreset(
            _themeSettings,
            _currentTheme,
            _currentEffect));
        ApplyViewSettings(ViewSettings);
        _wasThemePopoverOpen = viewModel.ThemePopoverOpen;
    }

    public void ApplyStartupPreset()
    {
        if (global::Avalonia.Application.Current is not { } application)
            return;

        application.RequestedThemeVariant =
            _currentTheme == ThemePresetVariant.Dark
                ? AvaloniaThemeVariant.Dark
                : AvaloniaThemeVariant.Light;
        viewModel.IsDarkTheme =
            _currentTheme == ThemePresetVariant.Dark;
        ApplyEffectMode(_currentEffect);
        ApplyPresetValues(themeSettingsStore.GetPreset(
            _themeSettings,
            _currentTheme,
            _currentEffect));
        themeBrushes.UpdateTransparencyEffect();
    }

    public void MarkPresetDirty()
    {
        if (!IsApplyingPreset)
            _themeSession?.MarkDirty();
    }

    public void HandleThemePopoverStateChange()
    {
        if (_wasThemePopoverOpen && !viewModel.ThemePopoverOpen)
            PersistCurrentThemePreset();

        _wasThemePopoverOpen = viewModel.ThemePopoverOpen;
    }

    public void SetTheme(ThemePresetVariant theme)
    {
        if (global::Avalonia.Application.Current is not { } application)
            return;

        application.RequestedThemeVariant =
            theme == ThemePresetVariant.Dark
                ? AvaloniaThemeVariant.Dark
                : AvaloniaThemeVariant.Light;
        viewModel.IsDarkTheme = theme == ThemePresetVariant.Dark;
        ApplyPresetForSelection(theme, GetSelectedEffectMode());
        refreshActiveQueryHighlights();
        themeBrushes.UpdateDynamicThemeBrushes();
    }

    public void ToggleCompactMode()
    {
        if (!viewModel.CanToggleCompactMode)
            return;

        viewModel.IsCompactMode = !viewModel.IsCompactMode;
        workspace.UpdateCompactModeVisualState();
        SaveCurrentViewSettings();
    }

    public void ToggleTreeAnimation()
    {
        viewModel.IsTreeAnimationEnabled =
            !viewModel.IsTreeAnimationEnabled;
        ApplyTreeAnimationClass();
        SaveCurrentViewSettings();
    }

    public void ToggleThemePopover()
        => viewModel.ThemePopoverOpen = !viewModel.ThemePopoverOpen;

    public void ToggleTransparentEffect()
    {
        viewModel.ToggleTransparent();
        ApplySelectedEffect();
    }

    public void ToggleMicaEffect()
    {
        viewModel.ToggleMica();
        ApplySelectedEffect();
    }

    public void ToggleAcrylicEffect()
    {
        viewModel.ToggleAcrylic();
        ApplySelectedEffect();
    }

    public void SetLanguage(AppLanguage language)
    {
        SetLanguageForCurrentSession(language);
        var current = ViewSettings;
        _userSettings.ViewSettings = current with
        {
            PreferredLanguage = localization.CurrentLanguage
        };
        userSettingsStore.TryPersistViewSettings(_userSettings);
    }

    public void SetLanguageForCurrentSession(AppLanguage language)
        => localization.SetLanguage(language);

    public void MarkTerminalCommandPromptDismissed()
    {
        _userSettings.ViewSettings = ViewSettings with
        {
            IsTerminalCommandPromptDismissed = true
        };
        userSettingsStore.TryPersistViewSettings(_userSettings);
    }

    public void ResetThemeSettings()
    {
        var resetDocument = themeSettingsStore.ResetToDefaults();
        var resetSession =
            new ThemePresetSession(themeSettingsStore, resetDocument);
        var theme = resetSession.CurrentTheme;
        var effect = ThemeEffectPlatformSupport.Normalize(
            resetSession.CurrentEffect,
            isMicaSupported);

        if (global::Avalonia.Application.Current is { } application)
        {
            application.RequestedThemeVariant =
                theme == ThemePresetVariant.Dark
                    ? AvaloniaThemeVariant.Dark
                    : AvaloniaThemeVariant.Light;
        }

        _currentTheme = theme;
        _currentEffect = effect;
        viewModel.IsDarkTheme = theme == ThemePresetVariant.Dark;
        ApplyEffectMode(effect);
        ApplyPresetValues(themeSettingsStore.GetPreset(
            resetDocument,
            theme,
            effect));
        _themeSettings = resetDocument;
        _themeSession = resetSession;
        themeBrushes.UpdateTransparencyEffect();
        themeBrushes.UpdateDynamicThemeBrushes();
    }

    public void PersistPendingChanges()
    {
        PersistCurrentThemePreset();
        SaveCurrentViewSettings();
    }

    public void SyncThemeWithSystem()
    {
        if (global::Avalonia.Application.Current is not { } application)
            return;

        viewModel.IsDarkTheme =
            application.ActualThemeVariant == AvaloniaThemeVariant.Dark;
    }

    internal static AppLanguage ResolveStartupLanguage(
        AppLanguage currentLanguage,
        AppLanguage? commandLineLanguage,
        AppLanguage? preferredLanguage)
        => commandLineLanguage ?? preferredLanguage ?? currentLanguage;

    private void ApplySavedLanguagePreference(AppViewSettings settings)
    {
        var startupLanguage = ResolveStartupLanguage(
            localization.CurrentLanguage,
            commandLineLanguage,
            settings.PreferredLanguage);
        if (startupLanguage == localization.CurrentLanguage)
            return;

        localization.SetLanguage(startupLanguage);
        viewModel.UpdateLocalization();
    }

    private void ApplyEffectMode(ThemePresetEffect effect)
    {
        switch (effect)
        {
            case ThemePresetEffect.Solid:
                viewModel.SetThemeEffects(
                    transparent: false,
                    mica: false,
                    acrylic: false);
                break;
            case ThemePresetEffect.Mica:
                viewModel.SetThemeEffects(
                    transparent: false,
                    mica: true,
                    acrylic: false);
                break;
            case ThemePresetEffect.Acrylic:
                viewModel.SetThemeEffects(
                    transparent: false,
                    mica: false,
                    acrylic: true);
                break;
            default:
                viewModel.SetThemeEffects(
                    transparent: true,
                    mica: false,
                    acrylic: false);
                break;
        }
    }

    private void ApplyPresetValues(ThemePreset preset)
    {
        Interlocked.Increment(ref _applyingPresetDepth);
        try
        {
            viewModel.BackgroundTransparency =
                preset.BackgroundTransparency;
            viewModel.PanelContrast = preset.PanelContrast;
            viewModel.MenuTransparency = preset.MenuTransparency;
            viewModel.BorderVisibility = preset.BorderVisibility;
        }
        finally
        {
            Interlocked.Decrement(ref _applyingPresetDepth);
        }
    }

    private void ApplyPresetForSelection(
        ThemePresetVariant theme,
        ThemePresetEffect effect)
    {
        if (_themeSession is null)
            return;

        var preset = _themeSession.Select(
            theme,
            effect,
            CreateCurrentThemePreset());
        _currentTheme = theme;
        _currentEffect = effect;
        ApplyPresetValues(preset);
    }

    private void ApplyViewSettings(AppViewSettings settings)
    {
        viewModel.IsCompactMode = settings.IsCompactMode;
        viewModel.IsTreeAnimationEnabled =
            settings.IsTreeAnimationEnabled;
        workspace.UpdateCompactModeVisualState();
        ApplyTreeAnimationClass();
    }

    private void ApplyTreeAnimationClass()
    {
        if (viewModel.IsTreeAnimationEnabled)
            window.Classes.Add("tree-animation");
        else
            window.Classes.Remove("tree-animation");
    }

    private void ApplySelectedEffect()
    {
        ApplyPresetForSelection(
            GetSelectedThemeVariant(),
            GetSelectedEffectMode());
        themeBrushes.UpdateTransparencyEffect();
    }

    private ThemePreset CreateCurrentThemePreset()
        => new()
        {
            BackgroundTransparency =
                viewModel.BackgroundTransparency,
            PanelContrast = viewModel.PanelContrast,
            MenuTransparency = viewModel.MenuTransparency,
            BorderVisibility = viewModel.BorderVisibility
        };

    private void PersistCurrentThemePreset()
    {
        if (_themeSession is { IsDirty: true } session)
            session.Persist(CreateCurrentThemePreset());
    }

    private void SaveCurrentViewSettings()
    {
        var current = ViewSettings;
        _userSettings.ViewSettings = new AppViewSettings
        {
            IsCompactMode = viewModel.IsCompactMode,
            IsTreeAnimationEnabled = viewModel.IsTreeAnimationEnabled,
            IsTerminalCommandPromptDismissed =
                current.IsTerminalCommandPromptDismissed,
            PreferredLanguage = current.PreferredLanguage
        };
        userSettingsStore.TryPersistViewSettings(_userSettings);
    }

    private ThemePresetVariant GetSelectedThemeVariant()
        => viewModel.IsDarkTheme
            ? ThemePresetVariant.Dark
            : ThemePresetVariant.Light;

    private ThemePresetEffect GetSelectedEffectMode()
    {
        if (viewModel.IsMicaEnabled)
            return ThemePresetEffect.Mica;
        if (viewModel.IsAcrylicEnabled)
            return ThemePresetEffect.Acrylic;
        return viewModel.IsTransparentEnabled
            ? ThemePresetEffect.Transparent
            : ThemePresetEffect.Solid;
    }
}
