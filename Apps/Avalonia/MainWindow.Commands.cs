using DevProjex.Application;
using DevProjex.Application.Updates;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.Services;
using DevProjex.Avalonia.Views;
using ThemeSelectionMode = DevProjex.Infrastructure.ThemePresets.ThemeSelectionMode;

namespace DevProjex.Avalonia;

public partial class MainWindow
{
    private void OnSetSystemTheme(object? sender, RoutedEventArgs e)
        => _appearanceSettings.SetTheme(ThemeSelectionMode.System);

    private void OnSetLightTheme(object? sender, RoutedEventArgs e)
        => _appearanceSettings.SetTheme(ThemeSelectionMode.Light);

    private void OnSetDarkTheme(object? sender, RoutedEventArgs e)
        => _appearanceSettings.SetTheme(ThemeSelectionMode.Dark);

    private void OnToggleCompactMode(object? sender, RoutedEventArgs e)
        => _appearanceSettings.ToggleCompactMode();

    private void OnToggleTreeExpansionAnimation(
        object? sender,
        RoutedEventArgs e)
        => _appearanceSettings.ToggleTreeExpansionAnimation();

    private void OnToggleStatusMetricsAnimation(
        object? sender,
        RoutedEventArgs e)
        => _appearanceSettings.ToggleStatusMetricsAnimation();

    private void OnToggleToolAnimation(object? sender, RoutedEventArgs e)
    {
        _appearanceSettings.ToggleToolAnimation();
        if (!_viewModel.IsToolAnimationEnabled)
            _topMenuBar?.CompleteProjectToolsReveal();
    }

    private void OnThemeMenuClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.UpdatePopoverOpen = false;
        _appearanceSettings.ToggleThemePopover();
        e.Handled = true;
    }

    private void OnSetSystemThemeCheckbox(object? sender, RoutedEventArgs e)
    {
        OnSetSystemTheme(sender, e);
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
        _appearanceSettings.ToggleTransparentEffect();
        e.Handled = true;
    }

    private void OnSetMicaMode(object? sender, RoutedEventArgs e)
    {
        _appearanceSettings.ToggleMicaEffect();
        e.Handled = true;
    }

    private void OnSetAcrylicMode(object? sender, RoutedEventArgs e)
    {
        _appearanceSettings.ToggleAcrylicEffect();
        e.Handled = true;
    }


    private void OnLangRu(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.Ru);
    private void OnLangEn(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.En);
    private void OnLangUz(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.Uz);
    private void OnLangTg(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.Tg);
    private void OnLangKk(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.Kk);
    private void OnLangFr(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.Fr);
    private void OnLangDe(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.De);
    private void OnLangIt(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.It);
    private void OnLangEs(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.Es);
    private void OnLangPt(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.Pt);
    private void OnLangPtPt(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.PtPt);
    private void OnLangZhCn(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.ZhCn);
    private void OnLangZhTw(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.ZhTw);
    private void OnLangJa(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.Ja);
    private void OnLangKo(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.Ko);
    private void OnLangTr(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.Tr);
    private void OnLangUk(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.Uk);
    private void OnLangPl(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.Pl);
    private void OnLangVi(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.Vi);
    private void OnLangId(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.Id);

    private void OnAbout(object? sender, RoutedEventArgs e)
    {
        _viewModel.HelpPopoverOpen = true;
        _viewModel.HelpDocsPopoverOpen = false;
        _viewModel.UpdatePopoverOpen = false;
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
        _viewModel.UpdatePopoverOpen = false;
        _viewModel.ThemePopoverOpen = false;
        e.Handled = true;
    }

    private async void OnUpdateCheckMenu(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _viewModel.HelpPopoverOpen = false;
        _viewModel.HelpDocsPopoverOpen = false;
        _viewModel.ThemePopoverOpen = false;
        try
        {
            await _applicationUpdates.OpenManualCheckAsync(
                _windowLifetimeCts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Window shutdown owns cancellation of menu-triggered work.
        }
    }

    private async void OnUpdateCheck(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        try
        {
            await _applicationUpdates.CheckManuallyAsync(
                _windowLifetimeCts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Window shutdown owns cancellation of the active request.
        }
        catch
        {
            _viewModel.CompleteUpdateCheck(new ApplicationUpdateCheckResult(
                ApplicationUpdateAvailability.CheckFailed,
                GetApplicationVersion()));
        }
    }

    private void OnUpdateClose(object? sender, RoutedEventArgs e)
    {
        _viewModel.UpdatePopoverOpen = false;
        e.Handled = true;
    }

    private void OnUpdateOpenRepository(object? sender, RoutedEventArgs e)
    {
        OpenExternalLink(ProjectLinks.RepositoryUrl);
        e.Handled = true;
    }

    private async void OnAutomaticUpdateCheckChanged(
        object? sender,
        AutomaticUpdateCheckChangedEventArgs e)
    {
        try
        {
            await _applicationUpdates.SetAutomaticCheckEnabledAsync(
                e.Enabled,
                _windowLifetimeCts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Window shutdown owns cancellation of settings persistence.
        }
    }

    private async void OnTerminalCommandSetup(object? sender, RoutedEventArgs e)
    {
        try
        {
            await ShowTerminalCommandSetupAsync(_terminalCommandSetupService.Probe(), isAutomaticPrompt: false);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ResolveDesktopExceptionMessage(ex));
            e.Handled = true;
        }
    }

    private void OnHelpClose(object? sender, RoutedEventArgs e)
    {
        _viewModel.HelpDocsPopoverOpen = false;
        e.Handled = true;
    }

    private async Task ShowTerminalCommandSetupAsync(
        TerminalCommandSetupSnapshot snapshot,
        bool isAutomaticPrompt)
    {
        while (true)
        {
            var dialogResult = await TerminalCommandSetupDialog.ShowAsync(
                this,
                _localization,
                snapshot,
                isAutomaticPrompt);
            if (ShouldPersistTerminalCommandPromptDismissal(dialogResult))
                SaveTerminalCommandPromptDismissed();

            if (dialogResult.Action == TerminalCommandDialogAction.ConfigurePath)
            {
                var pathResult = await Task.Run(_terminalCommandSetupService.ConfigurePath);
                if (pathResult.Success)
                    return;

                await ShowErrorAsync(pathResult.ErrorMessage ?? _localization["Dialog.TerminalCommand.InstallFailed"]);
                snapshot = pathResult.Snapshot;
                isAutomaticPrompt = false;
                continue;
            }

            if (dialogResult.Action is not (TerminalCommandDialogAction.InstallOrRepair or
                TerminalCommandDialogAction.Reinstall))
                return;

            var installResult = await Task.Run(() =>
                dialogResult.Action == TerminalCommandDialogAction.Reinstall
                    ? _terminalCommandSetupService.Reinstall()
                    : _terminalCommandSetupService.InstallOrRepair());
            if (ResolveTerminalCommandPostInstallUiAction(installResult) == TerminalCommandPostInstallUiAction.ShowError)
            {
                await ShowErrorAsync(installResult.ErrorMessage ?? _localization["Dialog.TerminalCommand.InstallFailed"]);
                return;
            }

            if (RequiresTerminalCommandPathConfiguration(installResult.Snapshot))
            {
                var pathResult = await Task.Run(_terminalCommandSetupService.ConfigurePath);
                if (pathResult.Success)
                    return;

                await ShowErrorAsync(pathResult.ErrorMessage ?? _localization["Dialog.TerminalCommand.InstallFailed"]);
                snapshot = pathResult.Snapshot;
                isAutomaticPrompt = false;
                continue;
            }

            if (dialogResult.Action == TerminalCommandDialogAction.Reinstall)
            {
                await MessageDialog.ShowAsync(
                    this,
                    _localization["Dialog.TerminalCommand.Title"],
                    _localization["Dialog.TerminalCommand.ReconfigureSucceeded"],
                    _localization["Dialog.OK"],
                    height: 120);
            }

            return;
        }
    }

    internal static TerminalCommandPostInstallUiAction ResolveTerminalCommandPostInstallUiAction(
        TerminalCommandInstallResult installResult) =>
        installResult.Success
            ? TerminalCommandPostInstallUiAction.None
            : TerminalCommandPostInstallUiAction.ShowError;

    internal static bool RequiresTerminalCommandPathConfiguration(TerminalCommandSetupSnapshot snapshot) =>
        snapshot.State is
            TerminalCommandSetupState.InstalledPathMissing or
            TerminalCommandSetupState.CommandShadowed;

    private void SaveTerminalCommandPromptDismissed()
        => _appearanceSettings.MarkTerminalCommandPromptDismissed();

    internal static bool ShouldPersistTerminalCommandPromptDismissal(TerminalCommandDialogResult dialogResult)
    {
        // Choosing install, repair, or reinstall is not a dismissal. If the setup attempt fails,
        // the next startup should still be allowed to offer setup again.
        return dialogResult.Action is not (TerminalCommandDialogAction.InstallOrRepair or
                   TerminalCommandDialogAction.Reinstall or
                   TerminalCommandDialogAction.ConfigurePath) &&
               (dialogResult.DontShowAgain || dialogResult.Action == TerminalCommandDialogAction.DismissPrompt);
    }

    private async void OnResetSettings(object? sender, RoutedEventArgs e)
    {
        var confirmed = await MessageDialog.ShowConfirmationAsync(
            this,
            _localization["Dialog.ResetSettings.Title"],
            _localization["Dialog.ResetSettings.Message"],
            _localization["Dialog.ResetSettings.Confirm"],
            _localization["Dialog.Cancel"],
            height: 180);

        if (!confirmed)
        {
            e.Handled = true;
            return;
        }

        ResetThemeSettings();
        _toastService.Show(_localization["Toast.Settings.Reset"]);
        e.Handled = true;
    }

    private async void OnResetData(object? sender, RoutedEventArgs e)
    {
        var confirmed = await MessageDialog.ShowConfirmationAsync(
            this,
            _localization["Dialog.ResetData.Title"],
            _localization["Dialog.ResetData.Message"],
            _localization["Dialog.ResetData.Confirm"],
            _localization["Dialog.Cancel"]);

        if (!confirmed)
        {
            e.Handled = true;
            return;
        }

        _projectProfiles.ClearAllProfiles();
        _toastService.Show(_localization["Toast.Data.Reset"]);
        e.Handled = true;
    }

    private void ResetThemeSettings()
        => _appearanceSettings.ResetThemeSettings();



    private void OnAboutOpenLink(object? sender, RoutedEventArgs e)
    {
        OpenExternalLink(ProjectLinks.RepositoryUrl);
        e.Handled = true;
    }

    private void OnAboutSupport(object? sender, RoutedEventArgs e)
    {
        OpenExternalLink(ProjectLinks.SupportUrl);
        e.Handled = true;
    }

    private void OnSearchNext(object? sender, RoutedEventArgs e)
    {
        _searchFilterController.NavigateSearch(1);
    }

    private void OnSearchPrev(object? sender, RoutedEventArgs e)
    {
        _searchFilterController.NavigateSearch(-1);
    }

    private async void OnToggleSearch(object? sender, RoutedEventArgs e) =>
        await _searchFilterController.ToggleSearchAsync();

    private void OnSearchClose(object? sender, RoutedEventArgs e) =>
        _ = _searchFilterController.CloseSearchAsync();

    private async void OnToggleFilter(object? sender, RoutedEventArgs e) =>
        await _searchFilterController.ToggleFilterAsync();

    private void OnFilterClose(object? sender, RoutedEventArgs e) =>
        _ = _searchFilterController.CloseFilterAsync();

    private void OnFilterKeyDown(object? sender, KeyEventArgs e) =>
        _searchFilterController.HandleFilterInputKey(e);

    private void OnSearchKeyDown(object? sender, KeyEventArgs e) =>
        _searchFilterController.HandleSearchInputKey(e);

	private void OnTogglePreviewSearch(object? sender, RoutedEventArgs e) =>
		_previewSearchController.Toggle();

	private void OnPreviewSearchClose(object? sender, RoutedEventArgs e) =>
		_previewSearchController.Close();

	private void OnPreviewSearchNext(object? sender, RoutedEventArgs e) =>
		_previewSearchController.Navigate(1);

	private void OnPreviewSearchPrev(object? sender, RoutedEventArgs e) =>
		_previewSearchController.Navigate(-1);

	private void OnPreviewSearchKeyDown(object? sender, KeyEventArgs e) =>
		_previewSearchController.HandleInputKey(e);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        CancelAllMemoryCleanup();
        var mods = e.KeyModifiers;
        var shortcutModifiers = DesktopShortcutModifiers.Current;

        // The open command is always available.
        if (shortcutModifiers.IsPrimary(mods) && e.Key == Key.O)
        {
            if (_viewModel.CanChangeProjectTree)
                OnOpenFolder(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

		if (_previewSearchController.TryHandleToggleHotkey(e))
			return;

        if (_searchFilterController.TryHandleToggleHotkey(e))
            return;

		if (_previewSearchController.TryHandleNavigationHotkey(e))
			return;

		if (_previewSearchController.TryHandleEscape(e))
			return;

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

        if (_searchFilterController.TryHandleActiveToolKey(e))
            return;

        // F5 refresh (same as WinForms)
        if (e.Key == Key.F5)
        {
            if (_viewModel.CanChangeProjectTree && _viewModel.IsProjectLoaded)
                OnRefresh(this, new RoutedEventArgs());

            e.Handled = true;
            return;
        }

        // Zoom hotkeys (in WinForms they work even without a loaded project)
        if (shortcutModifiers.IsPrimaryWithOptionalShift(mods) &&
            (e.Key == Key.OemPlus || e.Key == Key.Add))
        {
            _treeViewport.ZoomIn();
            e.Handled = true;
            return;
        }

        if (shortcutModifiers.IsPrimary(mods) && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
        {
            _treeViewport.ZoomOut();
            e.Handled = true;
            return;
        }

        if (shortcutModifiers.IsPrimary(mods) && (e.Key == Key.D0 || e.Key == Key.NumPad0))
        {
            OnZoomReset(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (shortcutModifiers.IsUnboundMacOSCommandW(e.Key, mods))
        {
            e.Handled = true;
            return;
        }

        if (!_viewModel.IsProjectLoaded)
            return;

        if (shortcutModifiers.IsPrimary(mods) && e.Key == Key.B)
        {
            OnTogglePreview(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (shortcutModifiers.IsPrimary(mods) && e.Key == Key.P)
        {
            OnToggleSettings(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (shortcutModifiers.IsPrimary(mods) && e.Key == Key.E)
        {
            if (_viewModel.IsTreePaneVisible)
                _treeViewport.ExpandAll();
            e.Handled = true;
            return;
        }

        if (shortcutModifiers.IsCollapseAll(e.Key, mods))
        {
            if (_viewModel.IsTreePaneVisible)
                _treeViewport.CollapseAll();
            e.Handled = true;
            return;
        }

        // Copy hotkeys (same as WinForms)
        if (shortcutModifiers.IsPrimaryWithShift(mods) && e.Key == Key.C)
        {
            OnCopyTree(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (shortcutModifiers.IsPrimaryWithAlt(mods) && e.Key == Key.C)
        {
            OnCopyTree(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (shortcutModifiers.IsPrimaryWithAlt(mods) && e.Key == Key.V)
        {
            OnCopyContent(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (shortcutModifiers.IsPrimaryWithShift(mods) && e.Key == Key.V)
        {
            OnCopyTreeAndContent(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
    }

    private void OnTreePointerEntered(object? sender, PointerEventArgs e)
        => _treeViewport.HandleTreePointerEntered();

    private void OnWindowPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        CancelAllMemoryCleanup();
        _treeViewport.HandlePointerWheelChanged(e);
    }

    private void ShowSearch(bool focusInput = true, bool selectAllOnFocus = true) =>
        _searchFilterController.ShowSearch(focusInput, selectAllOnFocus);

    private void ShowFilter(bool focusInput = true, bool selectAllOnFocus = true) =>
        _searchFilterController.ShowFilter(focusInput, selectAllOnFocus);

    private Task CloseSearchAsync(bool focusTree = true) =>
        _searchFilterController.CloseSearchAsync(focusTree);

    private Task CloseFilterAsync(bool focusTree = true) =>
        _searchFilterController.CloseFilterAsync(focusTree);

    private bool IsSearchBarEffectivelyVisible() =>
        _searchFilterController.IsSearchEffectivelyVisible;

    private bool IsFilterBarEffectivelyVisible() =>
        _searchFilterController.IsFilterEffectivelyVisible;

    private void SyncSearchAndFilterVisualStateFromFlags() =>
        _searchFilterController.SyncVisualState();

	private Task PrepareSearchAndFilterForProjectLoadAsync()
	{
		_previewSearchController.ClearProjectState();
		return _searchFilterController.PrepareForProjectLoadAsync();
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

	private void OnContentProcessingAllChanged(object? sender, RoutedEventArgs e)
	{
		var check = (sender as CheckBox)?.IsChecked == true;
		_selectionCoordinator.HandleContentProcessingAllChanged(check);
	}

    private async void OnApplySettings(object? sender, RoutedEventArgs e)
    {
        var activeOperationType = _statusOperations.GetActiveSnapshot().OperationType;
        if (!_viewModel.TryBeginApplySettings(activeOperationType))
            return;

        var applyTask = ApplySettingsAsync();
        _latestApplySettingsTask = applyTask;
        await applyTask;
    }

    internal Task LatestApplySettingsTask => _latestApplySettingsTask;

    private async Task ApplySettingsAsync()
    {
        var applyCts = ReplaceCancellationSource(ref _applySettingsCts);
        var cancellationToken = applyCts.Token;
        void CancelApply()
        {
            applyCts.Cancel();
            _selectionCoordinator.CancelPendingRefreshes();
            _refreshPipeline.CancelActiveRefresh();
        }

        try
        {
            await using var statusLease = SelectionRefreshStatusLease.StartApplyingSettings(
                _viewModel,
                _statusOperations,
                CancelApply,
                cancellationToken);

            try
            {
                // Apply must observe the latest converged section state. A user can click Apply
                // while an earlier ignore refresh is still finishing; rebuilding the tree first
                // would capture stale root-folder availability and keep newly revealed folders hidden.
                TreeRefreshOutcome refreshOutcome;
                do
                {
                    await _selectionCoordinator.WaitForPendingRefreshesAsync(cancellationToken);
                    await _selectionCoordinator.UpdateLiveOptionsForProjectScopeIfDirtyAsync(
                        _currentPath,
                        cancellationToken);
                    await _selectionCoordinator.WaitForPendingRefreshesAsync(cancellationToken);

                    // Redaction changes do not affect the tree. Publish the draft as one operation
                    // without rebuilding the tree.
                    if (_selectionCoordinator.TryAcceptContentRedactionOnlyChangeAsApplied(_currentPath))
                    {
                        ApplySelectedContentRedactionStates();
                        await _projectProfiles.PersistIfNeededAsync(_currentPath, cancellationToken);
                        return;
                    }

                    if (_currentTree is { } currentTree &&
                        _selectionCoordinator.TryAcceptContentTransformationOnlyChangeAsApplied(_currentPath))
                    {
                        await ApplyContentTransformationSettingsAsync(currentTree, cancellationToken);
						await _projectProfiles.PersistIfNeededAsync(_currentPath, cancellationToken);
                        return;
                    }

                    refreshOutcome = await RefreshTreeAsync(
                        cancellationToken: cancellationToken,
                        postLoadCleanupReason: MemoryCleanupReason.ApplySettingsWorkCompleted,
                        preserveStatusMetrics: true);

                    // A checkbox can change while a large tree is being materialized. In that
                    // case the pipeline discards the obsolete graph and Apply converges again
                    // instead of presenting settings that describe a different tree.
                } while (refreshOutcome == TreeRefreshOutcome.StaleInput);

				await _projectProfiles.PersistIfNeededAsync(_currentPath, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is handled by status operation fallback.
            }
            catch (Exception ex)
            {
                await ShowErrorAsync(ResolveDesktopExceptionMessage(ex));
            }
        }
        finally
        {
            DisposeIfCurrent(ref _applySettingsCts, applyCts);
            _viewModel.CompleteApplySettings();
        }
    }
}
