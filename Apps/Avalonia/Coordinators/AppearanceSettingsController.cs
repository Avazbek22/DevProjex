using DevProjex.Infrastructure.ThemePresets;
using DevProjex.Avalonia.Services;
using AvaloniaThemeVariant = Avalonia.Styling.ThemeVariant;
using ThemePreset = DevProjex.Infrastructure.ThemePresets.ThemePreset;
using ThemePresetEffect = DevProjex.Infrastructure.ThemePresets.ThemeEffectMode;
using ThemePresetSelectionMode = DevProjex.Infrastructure.ThemePresets.ThemeSelectionMode;
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
    private ThemePresetSelectionMode _currentThemeMode = ThemePresetSelectionMode.System;
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
            new ThemePresetSession(
                themeSettingsStore,
                _themeSettings,
                ResolveSystemTheme());
        NormalizeSessionEffectForPlatform(_themeSession.CurrentPreset);

        _currentThemeMode = _themeSession.CurrentMode;
        _currentTheme = _themeSession.CurrentTheme;
        _currentEffect = _themeSession.CurrentEffect;

        viewModel.IsDarkTheme =
            _currentTheme == ThemePresetVariant.Dark;
        viewModel.SelectedThemeMode = _currentThemeMode;
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

        application.RequestedThemeVariant = ResolveRequestedThemeVariant();
        viewModel.IsDarkTheme =
            _currentTheme == ThemePresetVariant.Dark;
        viewModel.SelectedThemeMode = _currentThemeMode;
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

    public void SetTheme(ThemePresetSelectionMode mode)
    {
        if (global::Avalonia.Application.Current is not { } application)
            return;

        var systemTheme = mode == ThemePresetSelectionMode.System
            ? ResolveSystemTheme()
            : null;
        var session = _themeSession;
        if (session is null)
            return;

        session.SelectMode(
            mode,
            systemTheme,
            CreateCurrentThemePreset());
        application.RequestedThemeVariant = ToRequestedThemeVariant(
            mode,
            session.CurrentTheme);
        var preset = NormalizeSessionEffectForPlatform(session.CurrentPreset);
        SynchronizeSelectionFromSession();
        ApplyEffectMode(_currentEffect);
        ApplyPresetValues(preset);
        themeBrushes.UpdateTransparencyEffect();
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

    public void ToggleTreeExpansionAnimation()
    {
        viewModel.IsTreeExpansionAnimationEnabled =
            !viewModel.IsTreeExpansionAnimationEnabled;
        SaveCurrentViewSettings();
    }

    public void ToggleStatusMetricsAnimation()
    {
        viewModel.IsStatusMetricsAnimationEnabled =
            !viewModel.IsStatusMetricsAnimationEnabled;
        SaveCurrentViewSettings();
    }

    public void ToggleToolAnimation()
    {
        viewModel.IsToolAnimationEnabled =
            !viewModel.IsToolAnimationEnabled;
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
            new ThemePresetSession(
                themeSettingsStore,
                resetDocument,
                ResolveSystemTheme());
        NormalizeSessionEffectForPlatform(resetSession, resetSession.CurrentPreset);

        _themeSettings = resetDocument;
        _themeSession = resetSession;
        SynchronizeSelectionFromSession();

        if (global::Avalonia.Application.Current is { } application)
        {
            application.RequestedThemeVariant = ToRequestedThemeVariant(
                resetSession.CurrentMode,
                resetSession.CurrentTheme);
        }

        var preset = NormalizeSessionEffectForPlatform(resetSession.CurrentPreset);
        SynchronizeSelectionFromSession();
        ApplyEffectMode(_currentEffect);
        ApplyPresetValues(preset);
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
        if (_themeSession is not { CurrentMode: ThemePresetSelectionMode.System } session ||
            global::Avalonia.Application.Current is not { } application)
        {
            return;
        }

        var systemTheme = ResolveSystemTheme();
        var previousTheme = session.CurrentTheme;
        var preset = session.SynchronizeSystemTheme(
            systemTheme,
            CreateCurrentThemePreset());
        if (session.CurrentTheme == previousTheme)
            return;

        preset = NormalizeSessionEffectForPlatform(preset);
        SynchronizeSelectionFromSession();
        ApplyEffectMode(_currentEffect);
        ApplyPresetValues(preset);
        themeBrushes.UpdateTransparencyEffect();
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

    private void ApplyPresetForSelectedEffect(ThemePresetEffect effect)
    {
        if (_themeSession is null)
            return;

        var preset = _themeSession.SelectEffect(
            effect,
            CreateCurrentThemePreset());
        SynchronizeSelectionFromSession();
        ApplyPresetValues(preset);
    }

    private void ApplyViewSettings(AppViewSettings settings)
    {
        viewModel.IsCompactMode = settings.IsCompactMode;
        viewModel.IsTreeExpansionAnimationEnabled =
            settings.IsTreeExpansionAnimationEnabled;
        viewModel.IsStatusMetricsAnimationEnabled =
            settings.IsStatusMetricsAnimationEnabled;
        viewModel.IsToolAnimationEnabled =
            settings.IsToolAnimationEnabled;
        workspace.UpdateCompactModeVisualState();
    }

    private void ApplySelectedEffect()
    {
        ApplyPresetForSelectedEffect(GetSelectedEffectMode());
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
            IsTreeExpansionAnimationEnabled =
                viewModel.IsTreeExpansionAnimationEnabled,
            IsStatusMetricsAnimationEnabled =
                viewModel.IsStatusMetricsAnimationEnabled,
            IsToolAnimationEnabled =
                viewModel.IsToolAnimationEnabled,
            IsTerminalCommandPromptDismissed =
                current.IsTerminalCommandPromptDismissed,
            PreferredLanguage = current.PreferredLanguage
        };
        userSettingsStore.TryPersistViewSettings(_userSettings);
    }

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

    private void SynchronizeSelectionFromSession()
    {
        if (_themeSession is null)
            return;

        _currentThemeMode = _themeSession.CurrentMode;
        _currentTheme = _themeSession.CurrentTheme;
        _currentEffect = _themeSession.CurrentEffect;
        viewModel.SelectedThemeMode = _currentThemeMode;
        viewModel.IsDarkTheme = _currentTheme == ThemePresetVariant.Dark;
    }

    private AvaloniaThemeVariant ResolveRequestedThemeVariant()
        => ToRequestedThemeVariant(_currentThemeMode, _currentTheme);

    private static AvaloniaThemeVariant ToRequestedThemeVariant(
        ThemePresetSelectionMode mode,
        ThemePresetVariant effectiveTheme) => mode switch
    {
        ThemePresetSelectionMode.System => AvaloniaThemeVariant.Default,
        _ => effectiveTheme == ThemePresetVariant.Dark
            ? AvaloniaThemeVariant.Dark
            : AvaloniaThemeVariant.Light
    };

    private ThemePresetVariant? ResolveSystemTheme()
    {
        var application = global::Avalonia.Application.Current;
        return SystemThemeResolver.Resolve(
            window.GetPlatformSettings()?.GetColorValues().ThemeVariant,
            application?.ActualThemeVariant,
            application?.RequestedThemeVariant);
    }

    private ThemePreset NormalizeSessionEffectForPlatform(ThemePreset currentPreset)
    {
        if (_themeSession is null)
            return currentPreset;

        return NormalizeSessionEffectForPlatform(_themeSession, currentPreset);
    }

    private ThemePreset NormalizeSessionEffectForPlatform(
        ThemePresetSession session,
        ThemePreset currentPreset)
    {
        var normalizedEffect = ThemeEffectPlatformSupport.Normalize(
            session.CurrentEffect,
            isMicaSupported);
        return normalizedEffect == session.CurrentEffect
            ? currentPreset
            : session.SelectEffect(normalizedEffect, currentPreset);
    }
}
