using DevProjex.Application.Diagnostics;
using DevProjex.Application.Secrets;
using DevProjex.Application.UseCases;
using DevProjex.Infrastructure.FileSystem;
using DevProjex.Kernel.Abstractions;
using DevProjex.Avalonia.Views;
using Avalonia;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowIgnoreOptionsUiTests
{
    [AvaloniaFact]
    public async Task HideSecrets_IsOptInAndUpdatesPreviewCountWithoutChangingSource()
    {
        const string secret = "AKIA" + "Z7M3Q5X2P6N4R7T5";
        using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
        var sourcePath = Path.Combine(project.RootPath, "src", "Secrets.cs");
		var sourceText = await File.ReadAllTextAsync(sourcePath);
		await File.WriteAllTextAsync(
			sourcePath,
			string.Concat(Enumerable.Repeat("// preview padding\n", 240)) + sourceText);
        var sourceBefore = File.ReadAllBytes(sourcePath);
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.HideSecrets,
                visible: true,
                isChecked: false);
            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
            var originalPreview = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
            Assert.Contains(secret, originalPreview, StringComparison.Ordinal);

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HideSecrets);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.HideSecrets,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var document = UiTestDriver
                        .GetRequiredControl<DevProjex.Avalonia.Controls.VirtualizedPreviewTextControl>(
                            window,
                            "PreviewTextControl")
                        .Document;
					return document?.Redactions.Count == 1 &&
					       document.Redactions[0].State == SecretPreviewSpanState.Redacted;
                },
                "Hide Secrets preview and count to converge");

            var redactedPreview = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
            Assert.DoesNotContain(secret, redactedPreview, StringComparison.Ordinal);
            Assert.Contains(
                "DEVPROJEX_REDACTED[aws-access-token#1]",
                redactedPreview,
                StringComparison.Ordinal);
            await UiTestDriver.WaitForIgnoreOptionLabelAsync(
                window,
                IgnoreOptionId.HideSecrets,
                "Hide secrets (1/1)");
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => string.Equals(
					UiTestDriver.GetViewModel(window).SettingsSecretsNotice,
					"Found: 1. Hidden: 1.",
					StringComparison.Ordinal),
				"the content-processing tooltip to publish detected and hidden counts");

			var previewControl = UiTestDriver
				.GetRequiredControl<DevProjex.Avalonia.Controls.VirtualizedPreviewTextControl>(
					window,
					"PreviewTextControl");
			var previewScrollViewer = UiTestDriver.GetRequiredControl<ScrollViewer>(
				window,
				"PreviewTextScrollViewer");
			previewControl.Focus();
			window.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.None, null);
			window.KeyRelease(Key.Down, RawInputModifiers.Alt, PhysicalKey.None, null);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			var viewportBeforeOverride = previewScrollViewer.Offset;
			Assert.True(viewportBeforeOverride.Y > 0);
			window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
			window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => previewControl.Document?.Redactions.Count == 1 &&
				      previewControl.Document.Redactions[0].State == SecretPreviewSpanState.KeptAsIs,
				"the keyboard override to restore the detected value");
			await UiTestDriver.WaitForIgnoreOptionLabelAsync(
				window,
				IgnoreOptionId.HideSecrets,
				"Hide secrets (1/0)");
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => string.Equals(
					UiTestDriver.GetViewModel(window).SettingsSecretsNotice,
					"Found: 1. Hidden: 0.",
					StringComparison.Ordinal),
				"the content-processing tooltip to update after a redaction override");
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.InRange(
				Math.Abs(previewScrollViewer.Offset.Y - viewportBeforeOverride.Y),
				0,
				1);

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HideSecrets);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.HideSecrets,
                visible: true,
                isChecked: false);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => string.Equals(
                    UiTestDriver.ComputeCurrentPreviewCopyPayload(window),
                    originalPreview,
                    StringComparison.Ordinal),
                "disabled Hide Secrets preview to return to the original payload");

            Assert.Equal(sourceBefore, File.ReadAllBytes(sourcePath));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

	[AvaloniaFact]
	public async Task HideSecrets_WithNoFindings_UpdatesOnlyContentState()
	{
		using var project = UiTestProject.CreateWithPythonSmartIgnoreWorkspace();
		await AssertHideSecretsIsolatedFromPathSelectionAsync(project, expectedCount: 0);
	}

	[AvaloniaFact]
	public async Task HideSecrets_WithFindings_UpdatesOnlyContentState()
	{
		using var project = UiTestProject.CreateWithPythonSmartIgnoreWorkspace();
		Directory.CreateDirectory(Path.Combine(project.RootPath, "secrets"));
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "secrets", "credentials.env"),
			"AWS_ACCESS_KEY_ID=AKIA" + "Z7M3Q5X2P6N4R7T5\n");

		await AssertHideSecretsIsolatedFromPathSelectionAsync(project, expectedCount: 1);
	}

	private static async Task AssertHideSecretsIsolatedFromPathSelectionAsync(
		UiTestProject project,
		int expectedCount)
	{
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		try
		{
			await UiTestDriver.WaitForIgnoreOptionStateAsync(
				window,
				IgnoreOptionId.SmartIgnore,
				visible: true,
				isChecked: true);
			Assert.False(UiTestDriver.GetViewModel(window).IsAnyPreviewVisible);

			var tree = UiTestDriver.GetCurrentTreeIdentity(window);
			var inventory = UiTestDriver.GetCurrentTreeInventoryIdentity(window);
			var previewDocument = UiTestDriver.GetViewModel(window).PreviewDocument;
			var selectionRevision = UiTestDriver.GetSelectionRevision(window);
			using var measurement = IgnorePipelineDiagnostics.BeginMeasurement();

			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HideSecrets);
			await UiTestDriver.WaitForIgnoreOptionLabelAsync(
				window,
				IgnoreOptionId.HideSecrets,
				$"Hide secrets ({expectedCount}/{expectedCount})");
			var viewModel = UiTestDriver.GetViewModel(window);
			if (expectedCount == 0)
			{
				await UiTestDriver.WaitForConditionAsync(
					window,
					() => !viewModel.HasContentProcessingOptions &&
					      !UiTestDriver.GetRequiredControl<Grid>(window, "ContentProcessingSection").IsVisible,
					"the content-processing section to hide after a completed empty scan");
				Assert.True(viewModel.HideSecretsOption?.IsChecked);
			}
			else
			{
				Assert.True(UiTestDriver.GetRequiredControl<Grid>(window, "ContentProcessingSection").IsVisible);
			}

			await UiTestDriver.WaitForIgnoreOptionStateAsync(
				window,
				IgnoreOptionId.SmartIgnore,
				visible: true,
				isChecked: true);
			Assert.Same(tree, UiTestDriver.GetCurrentTreeIdentity(window));
			Assert.Same(inventory, UiTestDriver.GetCurrentTreeInventoryIdentity(window));
			Assert.Same(previewDocument, UiTestDriver.GetViewModel(window).PreviewDocument);
			Assert.Equal(selectionRevision, UiTestDriver.GetSelectionRevision(window));

			if (expectedCount > 0)
			{
				await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HideSecrets);
				await UiTestDriver.WaitForIgnoreOptionStateAsync(
					window,
					IgnoreOptionId.HideSecrets,
					visible: true,
					isChecked: false);
				await UiTestDriver.WaitForIgnoreOptionLabelAsync(
					window,
					IgnoreOptionId.HideSecrets,
					"Hide secrets");
			}
			await UiTestDriver.WaitForIgnoreOptionStateAsync(
				window,
				IgnoreOptionId.SmartIgnore,
				visible: true,
				isChecked: true);

			Assert.Same(tree, UiTestDriver.GetCurrentTreeIdentity(window));
			Assert.Same(inventory, UiTestDriver.GetCurrentTreeInventoryIdentity(window));
			Assert.Same(previewDocument, UiTestDriver.GetViewModel(window).PreviewDocument);
			Assert.Equal(selectionRevision, UiTestDriver.GetSelectionRevision(window));
			var diagnostics = measurement.Capture();
			Assert.Equal(0, diagnostics.WorkspaceScans);
			Assert.Equal(0, diagnostics.DirectoryEnumerations);
			Assert.Equal(0, diagnostics.FileEnumerations);
			Assert.Equal(0, diagnostics.IgnoreRulesBuilds);
			Assert.Equal(0, diagnostics.FullSelectionRefreshes);
			Assert.Equal(0, diagnostics.LiveSelectionRefreshes);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

    [AvaloniaFact]
    public async Task IgnoredNumericExtensions_AreNotOfferedUntilTheirOwningIgnoreRuleIsDisabled()
    {
        using var project = UiTestProject.CreateWithIgnoredNumericExtensions();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            Assert.Contains(viewModel.Extensions, option => option.Name == ".1770912967592");
            Assert.Contains(viewModel.Extensions, option => option.Name == ".1770912967593");
            Assert.DoesNotContain(viewModel.Extensions, option => option.Name == ".1770912967589");
            Assert.DoesNotContain(viewModel.Extensions, option => option.Name == ".1770912967590");
            Assert.DoesNotContain(viewModel.Extensions, option => option.Name == ".1770912967591");

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.EmptyFiles);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => UiTestDriver.GetViewModel(window).Extensions.Any(option => option.Name == ".1770912967589") &&
                      UiTestDriver.GetViewModel(window).Extensions.Any(option => option.Name == ".1770912967590"),
                "empty-file numeric extensions to become available");

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.EmptyFiles);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => UiTestDriver.GetViewModel(window).Extensions.All(option =>
                    option.Name is not ".1770912967589" and not ".1770912967590"),
                "empty-file numeric extensions to be removed again");

            Assert.Contains(UiTestDriver.GetViewModel(window).Extensions, option => option.Name == ".1770912967593");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task NewWorkspace_WithDynamicIgnoreEntries_KeepsDynamicOptionsCheckedByDefault()
    {
        using var project = UiTestProject.CreateWithDynamicIgnoreEntries();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.ExtensionlessFiles,
                visible: true,
                isChecked: true);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                visible: true,
                isChecked: true);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFiles,
                visible: true,
                isChecked: true);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.HideSecrets,
                visible: true,
                isChecked: false);
			Assert.True(UiTestDriver.GetViewModel(window).AllIgnoreChecked);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ExtensionSelectionRefresh_ShowsAndHidesEmptyFoldersCounterBasedOnEffectiveTreeDelta()
    {
        using var project = UiTestProject.CreateWithExtensionSensitiveEmptyFolders();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                visible: false);

            var markdownOption = UiTestDriver.GetViewModel(window).Extensions.Single(option => option.Name == ".md");
            markdownOption.IsChecked = false;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionLabelAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                "Empty folders (2)");

            markdownOption = UiTestDriver.GetViewModel(window).Extensions.Single(option => option.Name == ".md");
            markdownOption.IsChecked = true;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                visible: false);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ExtensionsAllToggleRefresh_RecomputesEmptyFoldersCounterForBulkSelectionChanges()
    {
        using var project = UiTestProject.CreateWithExtensionSensitiveEmptyFolders();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                visible: false);

            var allExtensionsCheckBox = UiTestDriver.GetRequiredControl<CheckBox>(window, "ExtensionsAllCheckBox");
            await UiTestDriver.ClickAsync(window, allExtensionsCheckBox);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionLabelAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                "Empty folders (4)");

            await UiTestDriver.ClickAsync(window, allExtensionsCheckBox);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                visible: false);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task GitIgnoredExtensionlessNoise_DoesNotInflateExtensionlessCounter()
    {
        using var project = UiTestProject.CreateWithGitIgnoredExtensionlessNoise();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.ExtensionlessFiles,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionLabelAsync(
                window,
                IgnoreOptionId.ExtensionlessFiles,
                "Files without extension (1)");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task CleanGitAndSmartControllers_RemainHiddenUntilTheyAffectVisibleContent()
    {
        using var project = UiTestProject.CreateWithCleanGitAndSmartWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.UseGitIgnore, visible: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.SmartIgnore, visible: false);

            var artifactPath = Path.Combine(project.RootPath, "bin", "Debug", "net10.0", "App.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            await File.WriteAllTextAsync(artifactPath, "binary");

            await UiTestDriver.RefreshProjectAsync(window);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.SmartIgnore, visible: false);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task RepositoryGitModes_AllowTrackedGitIgnoreSmartOnlyAndNoFiltering()
    {
        EnsureGitAvailable();
        using var project = UiTestProject.CreateWithCleanGitAndSmartWorkspace();
        await File.WriteAllTextAsync(
            Path.Combine(project.RootPath, "LocalOnly.cs"),
            "class LocalOnly {}\n",
            TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(project.RootPath, "logs"));
        await File.WriteAllTextAsync(
            Path.Combine(project.RootPath, "logs", "ignored.log"),
            "ignored by gitignore\n",
            TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(project.RootPath, "bin", "Debug"));
        await File.WriteAllTextAsync(
            Path.Combine(project.RootPath, "bin", "Debug", "app.dll"),
            "generated artifact\n",
            TestContext.Current.CancellationToken);
        RunGit(project.RootPath, "init", "--quiet");
        RunGit(
            project.RootPath,
            "add",
            "--",
            ".gitignore",
            "App.csproj",
            "Program.cs");
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.TrackedGitFilesOnly,
                visible: true,
                isChecked: false);
            var initialIgnoreOptions = UiTestDriver.GetViewModel(window).IgnoreOptions;
            Assert.DoesNotContain(initialIgnoreOptions, option => option.Id == IgnoreOptionId.SmartIgnore);
            Assert.Equal(
                IgnoreOptionId.TrackedGitFilesOnly,
                Assert.Single(initialIgnoreOptions, option => option.IsControllerGroupEnd).Id);
            await WaitForProjectTreePathStateAsync(window, exists: true, "LocalOnly.cs");
            await WaitForProjectTreePathStateAsync(window, exists: false, "logs", "ignored.log");

            await SetIgnoreOptionCheckedAsync(
                window,
                IgnoreOptionId.TrackedGitFilesOnly,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await WaitForProjectTreePathStateAsync(window, exists: false, "LocalOnly.cs");
            await WaitForProjectTreePathStateAsync(window, exists: true, "Program.cs");

            await SetIgnoreOptionCheckedAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.TrackedGitFilesOnly,
                visible: true,
                isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.TrackedGitFilesOnly,
                visible: true,
                isChecked: false);
            await WaitForProjectTreePathStateAsync(window, exists: true, "LocalOnly.cs");

            await SetIgnoreOptionCheckedAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                isChecked: false);
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.TrackedGitFilesOnly,
                visible: true,
                isChecked: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            var smartOnlyIgnoreOptions = UiTestDriver.GetViewModel(window).IgnoreOptions;
            Assert.Equal(
                [
                    IgnoreOptionId.SmartIgnore,
                    IgnoreOptionId.HideSecrets,
                    IgnoreOptionId.UseGitIgnore,
                    IgnoreOptionId.TrackedGitFilesOnly
                ],
                smartOnlyIgnoreOptions.Take(4).Select(static option => option.Id));
            Assert.False(UiTestDriver.GetViewModel(window).AllIgnoreChecked);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.TrackedGitFilesOnly,
                visible: true,
                isChecked: false);
            await WaitForProjectTreePathStateAsync(window, exists: true, "logs", "ignored.log");
            await WaitForProjectTreePathStateAsync(window, exists: false, "bin", "Debug", "app.dll");

            await SetIgnoreOptionCheckedAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await WaitForProjectTreePathStateAsync(window, exists: true, "bin", "Debug", "app.dll");

            await UiTestDriver.ClickAsync(
                window,
                UiTestDriver.GetRequiredControl<CheckBox>(window, "IgnoreAllCheckBox"));
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.TrackedGitFilesOnly,
                visible: true,
                isChecked: false);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SwitchingToTrackedOnlyRescansGitIgnoredContainerForNestedRepository()
    {
        EnsureGitAvailable();
        using var project = UiTestProject.CreateWithIgnoredNestedGitRepositoryWorkspace();
        RunGit(project.RootPath, "init", "--quiet");
        RunGit(
            project.RootPath,
            "add",
            "--",
            ".gitignore",
            "App.csproj",
            "Program.cs");
        var nestedRepositoryPath = Path.Combine(project.RootPath, "ignored-container", "nested");
        RunGit(nestedRepositoryPath, "init", "--quiet");
        RunGit(nestedRepositoryPath, "add", "--", "Nested.csproj", "Tracked.cs");
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.TrackedGitFilesOnly,
                visible: true,
                isChecked: false);
            await WaitForProjectTreePathStateAsync(
                window,
                exists: false,
                "ignored-container",
                "nested",
                "Tracked.cs");

            await SetIgnoreOptionCheckedAsync(
                window,
                IgnoreOptionId.TrackedGitFilesOnly,
                isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);

            await WaitForProjectTreePathStateAsync(
                window,
                exists: true,
                "ignored-container",
                "nested",
                "Tracked.cs");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ExplicitUncheckedGitIgnoreController_RemainsVisibleWhenDotFilesTakesOver()
    {
        using var project = UiTestProject.CreateWithGitIgnoreDotFileOnlyWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFiles,
                visible: true,
                isChecked: true);

            await SetIgnoreAllCheckedAsync(window, isChecked: true);
            await UiTestDriver.ClickAsync(window, UiTestDriver.GetRequiredControl<CheckBox>(window, "IgnoreAllCheckBox"));
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.UseGitIgnore, visible: true, isChecked: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.DotFiles, visible: true, isChecked: false);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFiles, isChecked: true);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.DotFiles, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.UseGitIgnore, visible: true, isChecked: false);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task CleanPythonSmartController_RemainsHiddenUntilSmartArtifactAppears()
    {
        using var project = UiTestProject.CreateWithCleanPythonSmartWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.SmartIgnore, visible: false);

            var artifactPath = Path.Combine(project.RootPath, "src", "__pycache__", "app.cpython-310.pyc");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            await File.WriteAllTextAsync(artifactPath, "binary");

            await UiTestDriver.RefreshProjectAsync(window);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SmartIgnoreNegativeMatrix_RefreshAndToggleCyclePrunesOnlyNewProvenArtifact()
    {
        using var project = UiTestProject.CreateWithSmartIgnoreNegativeMatrixWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.SmartIgnore, visible: false);
            await AssertSmartIgnoreNegativeSourcePathsAsync(window);
            await AssertExtensionStatesAsync(
                window,
                visibleChecked: [".cs", ".json", ".md", ".nupkg", ".php", ".txt"],
                hidden: [".user"]);

            WriteTextFile(project.RootPath, Path.Combine("obj", "project.assets.json"), "{}\n");
            WriteTextFile(project.RootPath, "App.csproj.user", "local state\n");
            await UiTestDriver.RefreshProjectAsync(window);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await AssertSmartIgnoreNegativeSourcePathsAsync(window);
            await WaitForProjectTreePathStateAsync(window, exists: false, "obj", "project.assets.json");
            await WaitForExtensionStateAsync(window, ".user", visible: false);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: false);
            await AssertSmartIgnoreNegativeSourcePathsAsync(window);
            await WaitForProjectTreePathStateAsync(window, exists: true, "obj", "project.assets.json");
            await WaitForExtensionStateAsync(window, ".user", visible: true, isChecked: true);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await AssertSmartIgnoreNegativeSourcePathsAsync(window);
            await WaitForProjectTreePathStateAsync(window, exists: false, "obj", "project.assets.json");
            await WaitForExtensionStateAsync(window, ".user", visible: false);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task HiddenDotFolderOverlap_ShowsHiddenFoldersOnlyWhenDotFoldersNoLongerHidesSameFolder()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var project = UiTestProject.CreateWithHiddenDotFolderOverlapWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionLabelAsync(
                window,
                IgnoreOptionId.DotFolders,
                "dot folders (2)");
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.HiddenFolders,
                visible: false);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.HiddenFolders,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionLabelAsync(
                window,
                IgnoreOptionId.HiddenFolders,
                "Hidden folders (1)");
            await WaitForProjectTreePathStateAsync(window, exists: false, ".git", "config.txt");
            await WaitForProjectTreePathStateAsync(window, exists: false, ".hidden-dot", "payload.txt");

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.HiddenFolders, isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.HiddenFolders,
                visible: true,
                isChecked: false);
            // Disabling user-facing dot/hidden filters may expose ordinary project data, but
            // it must never override the independently selected Git administrative boundary.
            await WaitForProjectTreePathStateAsync(window, exists: false, ".git", "config.txt");
            await WaitForProjectTreePathStateAsync(window, exists: true, ".hidden-dot", "payload.txt");

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.HiddenFolders,
                visible: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: true);
            await WaitForProjectTreePathStateAsync(window, exists: false, ".git", "config.txt");
            await WaitForProjectTreePathStateAsync(window, exists: false, ".hidden-dot", "payload.txt");

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.HiddenFolders,
                visible: true,
                isChecked: false);
            await WaitForProjectTreePathStateAsync(window, exists: false, ".git", "config.txt");
            await WaitForProjectTreePathStateAsync(window, exists: true, ".hidden-dot", "payload.txt");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task RefreshProject_NewGitIgnoreInExistingScope_BypassesHotDiscoveryCacheImmediately()
    {
        using var project = UiTestProject.CreateWithPythonSmartIgnoreWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: false);

            WriteTextFile(project.RootPath, ".gitignore", "*.log\n");
            WriteTextFile(project.RootPath, Path.Combine("logs", "runtime.log"), "ignored immediately\n");
            await UiTestDriver.RefreshProjectAsync(window);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await WaitForProjectTreePathStateAsync(window, exists: false, "logs", "runtime.log");
            await AssertIgnoreOptionsStayStableAsync(window);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SuccessfulCloneCommit_LoadsManagedIgnoreBoundaryThroughProductionPath()
    {
        using var initialProject = UiTestProject.CreateWithDynamicIgnoreEntries();
        using var clonedProject = UiTestProject.CreateWithManagedGitCloneContentWorkspace();
        WriteTextFile(clonedProject.RootPath, "App.csproj", "<Project />\n");
        WriteTextFile(clonedProject.RootPath, ".gitignore", "logs/\n");
        WriteTextFile(clonedProject.RootPath, Path.Combine("logs", "runtime.log"), "git ignored\n");
        WriteTextFile(clonedProject.RootPath, Path.Combine("obj", "project.assets.json"), "{}\n");
        RunGit(clonedProject.RootPath, "init", "--quiet");
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(initialProject);

        try
        {
            var result = new GitCloneResult(
                Success: true,
                LocalPath: clonedProject.RootPath,
                SourceType: ProjectSourceType.GitClone,
                DefaultBranch: "main",
                RepositoryName: "managed-ignore-probe",
                RepositoryUrl: "https://example.test/managed-ignore-probe.git",
                ErrorMessage: null);
            await window.Dispatcher.InvokeAsync(() =>
                window.ApplySuccessfulGitCloneAsync(
                    result,
                    clonedProject.RootPath,
                    result.RepositoryUrl!,
                    TestContext.Current.CancellationToken));

            Assert.Equal(ProjectSourceType.GitClone, UiTestDriver.GetViewModel(window).ProjectSourceType);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await WaitForProjectTreePathStateAsync(window, exists: false, ".git", "HEAD");
            await WaitForProjectTreePathStateAsync(window, exists: false, "logs", "runtime.log");
            await WaitForProjectTreePathStateAsync(window, exists: false, "obj", "project.assets.json");

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.UseGitIgnore);
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);

            await WaitForProjectTreePathStateAsync(window, exists: false, ".git", "HEAD");
            await WaitForProjectTreePathStateAsync(window, exists: true, "logs", "runtime.log");
            await WaitForProjectTreePathStateAsync(window, exists: false, "obj", "project.assets.json");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PythonProjectWithGitIgnore_KeepsGitAndSmartControllersIndependent()
    {
        using var project = UiTestProject.CreateWithPythonGitIgnoreWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);

            await WaitForProjectTreePathStateAsync(window, exists: true, "src", "app.py");
            await WaitForProjectTreePathStateAsync(window, exists: false, "src", "__pycache__");
            await WaitForProjectTreePathStateAsync(window, exists: false, "logs", "app.log");

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.UseGitIgnore);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: false);

            await UiTestDriver.ClickApplySettingsAsync(window);
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);

            await WaitForProjectTreePathStateAsync(window, exists: false, "src", "__pycache__");
            await WaitForProjectTreePathStateAsync(window, exists: true, "logs", "app.log");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PythonProjectWithIdeaFolder_KeepsDotFoldersToggleAvailableAfterSmartIgnoreChanges()
    {
        using var project = UiTestProject.CreateWithPythonSmartIgnoreAndIdeaWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: false);

            if (UiTestDriver.GetViewModel(window).IgnoreOptions.Single(option => option.Id == IgnoreOptionId.DotFolders).IsChecked is false)
            {
                await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.DotFolders);
                await UiTestDriver.WaitForIgnoreOptionStateAsync(
                    window,
                    IgnoreOptionId.DotFolders,
                    visible: true,
                    isChecked: true);
                await UiTestDriver.ClickApplySettingsAsync(window);
                await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
            }

            await WaitForProjectTreePathStateAsync(window, exists: false, ".idea");

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.SmartIgnore);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: true);

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.DotFolders);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: false);

            await UiTestDriver.ClickApplySettingsAsync(window);
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);

            await WaitForProjectTreePathStateAsync(window, exists: true, ".idea", "workspace.xml");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task NestedPythonProjectWithIdeaFolder_SmartOnlyKeepsDotFolderVisible()
    {
        using var project = UiTestProject.CreateWithNestedPythonSmartIgnoreAndIdeaWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: false);
            await WaitForProjectTreePathStateAsync(window, exists: true, "lab2", ".idea", "workspace.xml");
            await WaitForProjectTreePathStateAsync(window, exists: false, "lab2", "__pycache__");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task NestedPolyglotWorkspace_AllOffAndSingleIgnoreTogglesStayScoped()
    {
        using var project = UiTestProject.CreateWithNestedPolyglotIgnoreMatrixWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.SmartIgnore, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.UseGitIgnore, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.DotFolders, visible: true, isChecked: true);

            await SetVisibleIgnoreOptionsCheckedAsync(window, isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            Assert.False(UiTestDriver.GetViewModel(window).AllIgnoreChecked);
            Assert.DoesNotContain(UiTestDriver.GetViewModel(window).IgnoreOptions, option => option.IsChecked);
            await AssertExtensionStatesAsync(
                window,
                visibleChecked: [".dll", ".log", ".js", ".pyc", ".xml", ".env"],
                hidden: []);
            await AssertNestedPolyglotTreeStateAsync(
                window,
                visiblePaths:
                [
                    ["api", "bin", "Debug", "app.dll"],
                    ["api", "logs", "runtime.log"],
                    ["web", "node_modules", "pkg", "index.js"],
                    ["python", "__pycache__", "app.pyc"],
                    [".idea", "workspace.xml"],
                    [".env"],
                    ["README"],
                    ["empty.txt"],
                    ["empty-root"]
                ],
                hiddenPaths: []);
            await AssertIgnoreOptionsStayStableAsync(window);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.SmartIgnore, visible: true, isChecked: true);
            await AssertExtensionStatesAsync(
                window,
                visibleChecked: [".log", ".xml", ".env"],
                hidden: [".dll", ".js", ".pyc"]);
            await AssertNestedPolyglotTreeStateAsync(
                window,
                visiblePaths:
                [
                    ["api", "logs", "runtime.log"],
                    [".idea", "workspace.xml"],
                    [".env"],
                    ["README"],
                    ["empty.txt"],
                    ["empty-root"]
                ],
                hiddenPaths:
                [
                    ["api", "bin", "Debug", "app.dll"],
                    ["web", "node_modules", "pkg", "index.js"],
                    ["python", "__pycache__", "app.pyc"]
                ]);
            await AssertIgnoreOptionsStayStableAsync(window);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: false);
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.UseGitIgnore, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.UseGitIgnore, visible: true, isChecked: true);
            await AssertExtensionStatesAsync(
                window,
                visibleChecked: [".dll", ".js", ".pyc", ".xml", ".env"],
                hidden: [".log"]);
            await AssertNestedPolyglotTreeStateAsync(
                window,
                visiblePaths:
                [
                    ["api", "bin", "Debug", "app.dll"],
                    ["web", "node_modules", "pkg", "index.js"],
                    ["python", "__pycache__", "app.pyc"],
                    [".idea", "workspace.xml"],
                    [".env"],
                    ["README"],
                    ["empty.txt"],
                    ["empty-root"]
                ],
                hiddenPaths: [["api", "logs", "runtime.log"]]);
            await AssertIgnoreOptionsStayStableAsync(window);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.UseGitIgnore, isChecked: false);
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: true);
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFiles, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.DotFolders, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.DotFiles, visible: true, isChecked: true);
            await AssertExtensionStatesAsync(
                window,
                visibleChecked: [".dll", ".log", ".js", ".pyc"],
                hidden: [".xml", ".env"]);
            await AssertNestedPolyglotTreeStateAsync(
                window,
                visiblePaths:
                [
                    ["api", "bin", "Debug", "app.dll"],
                    ["api", "logs", "runtime.log"],
                    ["web", "node_modules", "pkg", "index.js"],
                    ["python", "__pycache__", "app.pyc"],
                    ["README"],
                    ["empty.txt"],
                    ["empty-root"]
                ],
                hiddenPaths:
                [
                    [".idea", "workspace.xml"],
                    [".env"]
                ]);
            await AssertIgnoreOptionsStayStableAsync(window);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task HierarchicalGitIgnore_ToggleAndRefreshKeepFourScopesAtomic()
    {
        using var project = UiTestProject.CreateWithHierarchicalGitIgnoreCombatWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);
            await AssertHierarchicalGitIgnoreTreeStateAsync(window, gitIgnoreEnabled: true);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.UseGitIgnore, isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await AssertHierarchicalGitIgnoreTreeStateAsync(window, gitIgnoreEnabled: false);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.UseGitIgnore, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await AssertHierarchicalGitIgnoreTreeStateAsync(window, gitIgnoreEnabled: true);

            await UiTestDriver.RefreshProjectAsync(window);
            await AssertHierarchicalGitIgnoreTreeStateAsync(window, gitIgnoreEnabled: true);
            await AssertIgnoreOptionsStayStableAsync(window);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PythonProjectWithIdeaGitIgnore_DoesNotExposeGitIgnoreOptionAcrossSmartAndDotToggles()
    {
        using var project = UiTestProject.CreateWithPythonSmartIgnoreAndIdeaWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: false);
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: false);
            await AssertPythonIdeaWorkspaceStateAsync(
                window,
                smartChecked: false,
                dotChecked: true,
                ideaVisible: false,
                pycacheVisible: true);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: false);
            await AssertPythonIdeaWorkspaceStateAsync(
                window,
                smartChecked: false,
                dotChecked: false,
                ideaVisible: true,
                pycacheVisible: true);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PythonProjectWithIdeaFolder_IgnoreOptionsStayStableAcrossRepeatedRefreshes()
    {
        using var project = UiTestProject.CreateWithPythonSmartIgnoreAndIdeaWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true);

            if (UiTestDriver.GetViewModel(window).IgnoreOptions.Single(option => option.Id == IgnoreOptionId.DotFolders).IsChecked is false)
            {
                await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.DotFolders);
                await UiTestDriver.WaitForIgnoreOptionStateAsync(
                    window,
                    IgnoreOptionId.DotFolders,
                    visible: true,
                    isChecked: true);
                await UiTestDriver.ClickApplySettingsAsync(window);
                await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
            }

            await AssertIgnoreOptionsStayStableAsync(window);

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.SmartIgnore);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: false);
            await UiTestDriver.ClickApplySettingsAsync(window);
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: true);

            await AssertIgnoreOptionsStayStableAsync(window);

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.DotFolders);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: false);
            await UiTestDriver.ClickApplySettingsAsync(window);
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
            await WaitForProjectTreePathStateAsync(window, exists: true, ".idea", "workspace.xml");

            await AssertIgnoreOptionsStayStableAsync(window);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PythonProjectWithIdeaFolder_RepeatedSmartAndDotFolderCyclesKeepTreeAndTogglesAligned()
    {
        using var project = UiTestProject.CreateWithPythonSmartIgnoreAndIdeaWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true);
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);

            for (var cycle = 0; cycle < 2; cycle++)
            {
                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: false);
                await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
                await UiTestDriver.WaitForIgnoreOptionStateAsync(
                    window,
                    IgnoreOptionId.SmartIgnore,
                    visible: true,
                    isChecked: false);
                await UiTestDriver.WaitForIgnoreOptionStateAsync(
                    window,
                    IgnoreOptionId.DotFolders,
                    visible: true,
                    isChecked: true);
                await WaitForProjectTreePathStateAsync(window, exists: false, ".idea", "workspace.xml");
                await WaitForProjectTreePathStateAsync(window, exists: true, "src", "__pycache__", "app.pyc");

                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: false);
                await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
                await UiTestDriver.WaitForIgnoreOptionStateAsync(
                    window,
                    IgnoreOptionId.DotFolders,
                    visible: true,
                    isChecked: false);
                await WaitForProjectTreePathStateAsync(window, exists: true, ".idea", "workspace.xml");
                await WaitForProjectTreePathStateAsync(window, exists: true, "src", "__pycache__", "app.pyc");

                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: true);
                await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
                await UiTestDriver.WaitForIgnoreOptionStateAsync(
                    window,
                    IgnoreOptionId.SmartIgnore,
                    visible: true,
                    isChecked: true);
                await UiTestDriver.WaitForIgnoreOptionStateAsync(
                    window,
                    IgnoreOptionId.DotFolders,
                    visible: true,
                    isChecked: false);
                await WaitForProjectTreePathStateAsync(window, exists: true, ".idea", "workspace.xml");
                await WaitForProjectTreePathStateAsync(window, exists: false, "src", "__pycache__");

                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: true);
                await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
                await UiTestDriver.WaitForIgnoreOptionStateAsync(
                    window,
                    IgnoreOptionId.DotFolders,
                    visible: true,
                    isChecked: true);
                await WaitForProjectTreePathStateAsync(window, exists: false, ".idea", "workspace.xml");
                await WaitForProjectTreePathStateAsync(window, exists: false, "src", "__pycache__");
            }

            await AssertIgnoreOptionsStayStableAsync(window);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PythonProjectWithIdeaFolder_RapidSmartAndDotFolderChangesConvergeToLastAppliedState()
    {
        using var project = UiTestProject.CreateWithPythonSmartIgnoreAndIdeaWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true);
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);

            for (var cycle = 0; cycle < 3; cycle++)
            {
                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: false);
                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: false);
                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: true);
                await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
                await AssertPythonIdeaWorkspaceStateAsync(
                    window,
                    smartChecked: true,
                    dotChecked: false,
                    ideaVisible: true,
                    pycacheVisible: false);
                await AssertIgnoreOptionsStayStableAsync(window);

                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: true);
                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: false);
                await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
                await AssertPythonIdeaWorkspaceStateAsync(
                    window,
                    smartChecked: false,
                    dotChecked: true,
                    ideaVisible: false,
                    pycacheVisible: true);
                await AssertIgnoreOptionsStayStableAsync(window);
            }

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: true);
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await AssertPythonIdeaWorkspaceStateAsync(
                window,
                smartChecked: true,
                dotChecked: true,
                ideaVisible: false,
                pycacheVisible: false);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PythonProjectWithIdeaFolder_BlockedStaleSmartRefreshCannotRestoreOldIgnoreChecks()
    {
        using var project = UiTestProject.CreateWithPythonSmartIgnoreAndIdeaWorkspace();
        using var blockingScanner = new SwitchableBlockingFileSystemScanner(
            project.RootPath,
            ignoreCancellation: true);
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(
            project,
            configureServices: services => services with
            {
                ScanOptionsUseCase = new ScanOptionsUseCase(
                    LegacyWorkspaceScannerTestAdapter.Adapt(blockingScanner))
            });

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);

            blockingScanner.EnableBlocking();
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: false);
            Assert.True(
                blockingScanner.WaitForBlockedCall(TimeSpan.FromSeconds(10)),
                "The stale Python smart-ignore refresh did not reach the controlled scanner block.");

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: false);
            blockingScanner.Release();
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);

            await AssertPythonIdeaWorkspaceStateAsync(
                window,
                smartChecked: false,
                dotChecked: false,
                ideaVisible: true,
                pycacheVisible: true);
            await AssertIgnoreOptionsStayStableAsync(window);
        }
        finally
        {
            blockingScanner.Release();
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    private static async Task WaitForExtensionStateAsync(
        MainWindow window,
        string extensionName,
        bool visible,
        bool? isChecked = null)
    {
        await UiTestDriver.WaitForConditionAsync(
            window,
            () =>
            {
                var option = UiTestDriver.GetViewModel(window).Extensions
                    .FirstOrDefault(candidate => string.Equals(candidate.Name, extensionName, StringComparison.OrdinalIgnoreCase));
                if (!visible)
                    return option is null;

                return option is not null && (isChecked is null || option.IsChecked == isChecked);
            },
            $"extension option '{extensionName}' to be visible={visible}, checked={isChecked?.ToString() ?? "<any>"}");
    }

    private static void MutateExternalRefreshWorkspace(string rootPath)
    {
        WriteTextFile(rootPath, Path.Combine("api", ".gitignore"), "logs/\n");
        WriteTextFile(rootPath, Path.Combine("api", "App.csproj"), "<Project />\n");
        WriteTextFile(rootPath, Path.Combine("api", "src", "Program.cs"), "class Program {}\n");
        WriteTextFile(rootPath, Path.Combine("api", "logs", "runtime.log"), "ignored by nested gitignore\n");
        WriteTextFile(rootPath, Path.Combine("web", "package.json"), "{}\n");
        WriteTextFile(rootPath, Path.Combine("web", "src", "app.ts"), "export const app = true;\n");
        WriteTextFile(rootPath, Path.Combine("web", "node_modules", "pkg", "index.js"), "module.exports = {};\n");
        WriteTextFile(rootPath, Path.Combine("generated", "report.log"), "new visible log\n");
        WriteTextFile(rootPath, "new-data.csv", "2,updated\n");
        WriteTextFile(rootPath, Path.Combine(".idea", "workspace.xml"), "<project />\n");
        WriteTextFile(rootPath, ".env", "APP_ENV=test\n");
        Directory.CreateDirectory(Path.Combine(rootPath, "empty-root"));
    }

    private static void MutateExternalRefreshWorkspaceSecondWave(string rootPath)
    {
        WriteTextFile(rootPath, Path.Combine("cli", "go.mod"), "module refreshstage\n");
        WriteTextFile(rootPath, Path.Combine("cli", "main.go"), "package main\nfunc main() {}\n");
        WriteTextFile(rootPath, Path.Combine("scripts", "run.py"), "print('refresh stage')\n");
        WriteTextFile(rootPath, Path.Combine("scripts", "debug.log"), "manual extension state must survive refresh\n");
        WriteTextFile(rootPath, Path.Combine(".vscode", "settings.json"), "{}\n");
        WriteTextFile(rootPath, "Dockerfile", "FROM scratch\n");
        Directory.CreateDirectory(Path.Combine(rootPath, "second-empty-root"));
    }

    private static void WriteTextFile(string rootPath, string relativePath, string content)
    {
        var fullPath = Path.Combine(rootPath, relativePath);
        var directoryPath = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
            Directory.CreateDirectory(directoryPath);

        File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static async Task AssertPythonIdeaWorkspaceStateAsync(
        MainWindow window,
        bool smartChecked,
        bool dotChecked,
        bool ideaVisible,
        bool pycacheVisible)
    {
        await UiTestDriver.WaitForIgnoreOptionStateAsync(
            window,
            IgnoreOptionId.SmartIgnore,
            visible: true,
            isChecked: smartChecked);
        await UiTestDriver.WaitForIgnoreOptionStateAsync(
            window,
            IgnoreOptionId.DotFolders,
            visible: true,
            isChecked: dotChecked);
        await WaitForProjectTreePathStateAsync(window, exists: ideaVisible, ".idea", "workspace.xml");
        await WaitForProjectTreePathStateAsync(window, exists: pycacheVisible, "src", "__pycache__", "app.pyc");
    }

    private static async Task AssertHierarchicalGitIgnoreTreeStateAsync(
        MainWindow window,
        bool gitIgnoreEnabled)
    {
        string[][] alwaysVisiblePaths =
        [
            ["repo", "keep.rootdrop"],
            ["repo", "module", "module-keep.rootdrop"],
            ["repo", "module", "child", "rescue.moddrop"],
            ["repo", "module", "child", "grand", "visible.deepdrop"],
            ["repo", "module", "child", "grand", "invalid", "visible.txt"],
            ["repo", "outside", "visible.siblingdrop"]
        ];
        string[][] gitIgnoredPaths =
        [
            ["repo", "drop.rootdrop"],
            ["repo", "module", "drop.moddrop"],
            ["repo", "module", "child", "drop.deepdrop"],
            ["repo", "module", "child", "grand", "drop.lastdrop"],
            ["repo", "sibling", "drop.siblingdrop"]
        ];

        foreach (var path in alwaysVisiblePaths)
            await WaitForProjectTreePathStateAsync(window, exists: true, path);
        foreach (var path in gitIgnoredPaths)
            await WaitForProjectTreePathStateAsync(window, exists: !gitIgnoreEnabled, path);
    }

    private static async Task AssertIgnoreOptionsStayStableAsync(MainWindow window)
    {
        var expected = CaptureIgnoreOptionState(window);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
            Assert.Equal(expected, CaptureIgnoreOptionState(window));
        }
    }

    private static IReadOnlyList<(IgnoreOptionId Id, bool IsChecked)> CaptureIgnoreOptionState(MainWindow window)
    {
        return UiTestDriver.GetViewModel(window).IgnoreOptions
            .Select(option => (option.Id, option.IsChecked))
            .ToArray();
    }

    private static async Task SetIgnoreOptionCheckedAsync(
        MainWindow window,
        IgnoreOptionId optionId,
        bool isChecked)
    {
        await UiTestDriver.WaitForIgnoreOptionStateAsync(window, optionId, visible: true);

        var option = UiTestDriver.GetViewModel(window).IgnoreOptions.Single(candidate => candidate.Id == optionId);
        if (option.IsChecked == isChecked)
            return;

        await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, optionId);
        await UiTestDriver.WaitForIgnoreOptionStateAsync(
            window,
            optionId,
            visible: true,
            isChecked: isChecked);
    }

    [AvaloniaFact]
    public async Task HideSecrets_IsRenderedInItsOwnSectionAndIsIndependentFromIgnoreAll()
    {
        using var project = UiTestProject.CreateWithPythonSmartIgnoreWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            var hideSecrets = Assert.IsType<IgnoreOptionViewModel>(viewModel.HideSecretsOption);
            Assert.DoesNotContain(viewModel.PathIgnoreOptions, static option => option.Id == IgnoreOptionId.HideSecrets);
            var checkBox = UiTestDriver.GetRequiredIgnoreOptionCheckBox(window, IgnoreOptionId.HideSecrets);
			var processingList = UiTestDriver.GetRequiredControl<ListBox>(window, "ContentProcessingOptionsList");
			var processingBorder = UiTestDriver.GetRequiredControl<Border>(window, "ContentProcessingOptionsBorder");
			var processingContent = Assert.IsType<Grid>(processingBorder.Child);
			var helpIndicator = UiTestDriver.GetRequiredControl<Border>(window, "ContentProcessingHelpIndicator");
			var questionIcon = UiTestDriver.GetRequiredControl<Border>(window, "ContentProcessingQuestionIcon");
			var warningIcon = UiTestDriver.GetRequiredControl<Grid>(window, "ContentProcessingWarningIcon");
            var ignoreList = UiTestDriver.GetRequiredControl<ListBox>(window, "IgnoreOptionsList");
			Assert.Equal("Content processing:", UiTestDriver.GetRequiredControl<TextBlock>(window, "ContentProcessingHeaderText").Text);
			Assert.Contains(processingList, processingContent.Children);
			Assert.Contains(helpIndicator, processingContent.Children);
			Assert.Null(helpIndicator.Cursor);
			Assert.False(helpIndicator.IsVisible);

			viewModel.SetContentProcessingStatus(SecretScanState.Completed, detectedCount: 3, hiddenCount: 2);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.True(helpIndicator.IsVisible);
			await UiTestDriver.ClickAsync(window, helpIndicator);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => ToolTip.GetIsOpen(helpIndicator),
				"the content-processing tooltip to open on click");
			var helpToolTip = Assert.IsType<ToolTip>(ToolTip.GetTip(helpIndicator));
			var helpText = Assert.IsType<TextBlock>(helpToolTip.Content);
			Assert.Equal("Found: 3. Hidden: 2.", helpText.Text);
			Assert.Equal(PlacementMode.Left, ToolTip.GetPlacement(helpIndicator));
			Assert.True(questionIcon.IsVisible);
			Assert.False(warningIcon.IsVisible);
			var helpIndicatorTransform = helpIndicator.RenderTransform?.Value ?? Matrix.Identity;
			Assert.InRange(helpIndicatorTransform.M32, 3, 5);
			var questionIconTransform = questionIcon.RenderTransform?.Value ?? Matrix.Identity;
			Assert.InRange(questionIconTransform.M32, -1.5, -0.5);

			viewModel.SetContentProcessingStatus(SecretScanState.Failed);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.Equal("The analysis could not be completed.", helpText.Text);
			Assert.False(questionIcon.IsVisible);
			Assert.True(warningIcon.IsVisible);

			viewModel.SetContentProcessingStatus(SecretScanState.Completed, detectedCount: 0, hiddenCount: 0);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.Equal("DevProjex found no secrets", viewModel.SettingsSecretsNotice);
			Assert.False(UiTestDriver.GetRequiredControl<Grid>(window, "ContentProcessingSection").IsVisible);

			viewModel.SetContentProcessingStatus(SecretScanState.Disabled);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.Same(viewModel.ContentProcessingOptions, processingList.ItemsSource);
			Assert.Single(viewModel.ContentProcessingOptions);
			Assert.Contains(checkBox.GetVisualAncestors(), ancestor => ReferenceEquals(ancestor, processingList));
            Assert.DoesNotContain(checkBox.GetVisualAncestors(), ancestor => ReferenceEquals(ancestor, ignoreList));
			var settingsPanel = UiTestDriver.GetRequiredControl<SettingsPanelView>(window, "SettingsPanel");
			var processingPosition = Assert.IsType<Point>(processingList.TranslatePoint(default, settingsPanel));
			var ignorePosition = Assert.IsType<Point>(ignoreList.TranslatePoint(default, settingsPanel));
			Assert.True(processingPosition.Y < ignorePosition.Y);

			viewModel.ContentProcessingOptions.Add(
				new IgnoreOptionViewModel(IgnoreOptionId.HiddenFiles, "Future transformation", false));
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.Equal(2, processingList.ItemCount);

            var ignoreAll = UiTestDriver.GetRequiredControl<CheckBox>(window, "IgnoreAllCheckBox");
            await UiTestDriver.RaiseButtonClickAsync(ignoreAll);
            Assert.False(hideSecrets.IsChecked);
            await UiTestDriver.RaiseButtonClickAsync(ignoreAll);
            Assert.False(hideSecrets.IsChecked);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    private static async Task SetIgnoreAllCheckedAsync(MainWindow window, bool isChecked)
    {
        var viewModel = UiTestDriver.GetViewModel(window);
        if (viewModel.AllIgnoreChecked == isChecked)
            return;

        await UiTestDriver.ClickAsync(
            window,
            UiTestDriver.GetRequiredControl<CheckBox>(window, "IgnoreAllCheckBox"));
        await UiTestDriver.WaitForConditionAsync(
            window,
            () => viewModel.AllIgnoreChecked == isChecked,
            $"the all-ignore checkbox to become {isChecked}");
        await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
    }

    private static void EnsureGitAvailable()
    {
        var startInfo = CreateGitStartInfo(workingDirectory: null);
        startInfo.ArgumentList.Add("--version");
        Process? startedProcess;
        try
        {
            startedProcess = Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip("Git is not available in this test environment.");
            return;
        }

        using var process = startedProcess;
        if (process is null)
            Assert.Skip("Git is not available in this test environment.");
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10_000) || process.ExitCode != 0)
            Assert.Skip("Git is not available in this test environment.");
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = CreateGitStartInfo(workingDirectory);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(20_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Git command did not complete within 20 seconds.");
        }

        Assert.True(process.ExitCode == 0, $"git failed ({process.ExitCode}): {error}{output}");
    }

    private static ProcessStartInfo CreateGitStartInfo(string? workingDirectory) =>
        new("git")
        {
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

    private static async Task SetVisibleIgnoreOptionsCheckedAsync(
        MainWindow window,
        bool isChecked)
    {
        foreach (var optionId in UiTestDriver.GetViewModel(window).IgnoreOptions.Select(option => option.Id).ToArray())
            await SetIgnoreOptionCheckedAsync(window, optionId, isChecked);
    }

    private static async Task ApplySettingsAndWaitForIgnoreRefreshAsync(MainWindow window)
    {
        await UiTestDriver.ClickApplySettingsAsync(window);
        await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
    }

    private static async Task AssertNestedPolyglotTreeStateAsync(
        MainWindow window,
        IReadOnlyCollection<string[]> visiblePaths,
        IReadOnlyCollection<string[]> hiddenPaths)
    {
        foreach (var visiblePath in visiblePaths)
            await WaitForProjectTreePathStateAsync(window, exists: true, visiblePath);
        foreach (var hiddenPath in hiddenPaths)
            await WaitForProjectTreePathStateAsync(window, exists: false, hiddenPath);
    }

    private static async Task AssertExtensionStatesAsync(
        MainWindow window,
        IReadOnlyCollection<string> visibleChecked,
        IReadOnlyCollection<string> hidden)
    {
        // These assertions bind the user-visible extension list to the currently applied
        // ignore controllers. A path can be correct while the extension checklist is stale.
        foreach (var extension in visibleChecked)
            await WaitForExtensionStateAsync(window, extension, visible: true, isChecked: true);
        foreach (var extension in hidden)
            await WaitForExtensionStateAsync(window, extension, visible: false);
    }

    private static async Task AssertSmartIgnoreNegativeSourcePathsAsync(MainWindow window)
    {
        string[][] visiblePaths =
        [
            ["obj-backup", "project.assets.json"],
            ["build", "README.md"],
            ["build", "docs", "CMakeCache.txt"],
            ["vendor", "src", "autoload.php"],
            ["packages", "Alpha", "Alpha.nupkg"],
            ["m2-backup", "repository", "service", "package.json"],
            ["cmake-build", "CMakeCache.txt"]
        ];

        await UiTestDriver.WaitForConditionAsync(
            window,
            () => visiblePaths.All(path => ProjectTreeContainsPath(window, path)),
            "all source lookalikes in the Smart Ignore negative matrix to remain visible");
        await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);
    }

    private static async Task WaitForProjectTreePathStateAsync(
        MainWindow window,
        bool exists,
        params string[] relativeDisplayPath)
    {
        await UiTestDriver.WaitForConditionAsync(
            window,
            () => ProjectTreeContainsPath(window, relativeDisplayPath) == exists,
            $"project tree path '{string.Join("/", relativeDisplayPath)}' to exist={exists}");

        await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);
    }

    private static bool ProjectTreeContainsPath(MainWindow window, IReadOnlyList<string> relativeDisplayPath)
    {
        var roots = UiTestDriver.GetViewModel(window).TreeNodes;
        if (roots.Count != 1)
            return false;

        return ContainsTreePath(roots[0].Children, relativeDisplayPath);
    }

    private static bool ContainsTreePath(IEnumerable<TreeNodeViewModel> candidates, IReadOnlyList<string> displayPath)
    {
        var current = candidates;
        foreach (var segment in displayPath)
        {
            var match = current.FirstOrDefault(node => string.Equals(node.DisplayName, segment, StringComparison.Ordinal));
            if (match is null)
                return false;

            current = match.Children;
        }

        return true;
    }

    private sealed class SwitchableBlockingFileSystemScanner(
        string blockedRootPath,
        bool ignoreCancellation = false)
        : IFileSystemScanner,
            IFileSystemScannerAdvanced,
            IFileSystemScannerEffectiveEmptyFolderCounter,
            IFileSystemScannerEffectiveIgnoreCountsProvider,
            IFileSystemScannerIgnoreSectionSnapshotProvider,
            IFileSystemScannerExtensionPolicySnapshotProvider,
            IFileSystemScannerProjectWorkspaceScanner,
            IDisposable
    {
        private readonly FileSystemScanner _inner = new();
        private readonly ManualResetEventSlim _blocked = new(initialState: false);
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private readonly bool _ignoreCancellation = ignoreCancellation;
        private int _enabled;

        public void EnableBlocking() => Volatile.Write(ref _enabled, 1);

        public bool WaitForBlockedCall(TimeSpan timeout) => _blocked.Wait(timeout);

        public void Release() => _release.Set();

        public bool CanReadRoot(string rootPath) => _inner.CanReadRoot(rootPath);

        public ScanResult<HashSet<string>> GetExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetExtensions(rootPath, rules, EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<HashSet<string>> GetRootFileExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetRootFileExtensions(rootPath, rules, EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<List<string>> GetRootFolderNames(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetRootFolderNames(rootPath, rules, EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<ExtensionsScanData> GetExtensionsWithIgnoreOptionCounts(
            string rootPath,
            IgnoreRules rules,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetExtensionsWithIgnoreOptionCounts(rootPath, rules, EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<ExtensionsScanData> GetRootFileExtensionsWithIgnoreOptionCounts(
            string rootPath,
            IgnoreRules rules,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetRootFileExtensionsWithIgnoreOptionCounts(rootPath, rules, EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<int> GetEffectiveEmptyFolderCount(
            string rootPath,
            IReadOnlySet<string> allowedExtensions,
            IgnoreRules rules,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetEffectiveEmptyFolderCount(rootPath, allowedExtensions, rules, EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<IgnoreOptionCounts> GetEffectiveIgnoreOptionCounts(
            string rootPath,
            IReadOnlySet<string> allowedExtensions,
            IgnoreRules rules,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetEffectiveIgnoreOptionCounts(rootPath, allowedExtensions, rules, EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<IgnoreOptionCounts> GetEffectiveRootFileIgnoreOptionCounts(
            string rootPath,
            IReadOnlySet<string> allowedExtensions,
            IgnoreRules rules,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetEffectiveRootFileIgnoreOptionCounts(rootPath, allowedExtensions, rules, EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
            string rootPath,
            IgnoreRules extensionDiscoveryRules,
            IgnoreRules effectiveRules,
            IReadOnlySet<string>? effectiveAllowedExtensions,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetIgnoreSectionSnapshot(
                rootPath,
                extensionDiscoveryRules,
                effectiveRules,
                effectiveAllowedExtensions,
                EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
            string rootPath,
            IgnoreRules extensionDiscoveryRules,
            IgnoreRules effectiveRules,
            IReadOnlySet<string>? effectiveAllowedExtensions,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetRootFileIgnoreSectionSnapshot(
                rootPath,
                extensionDiscoveryRules,
                effectiveRules,
                effectiveAllowedExtensions,
                EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
            string rootPath,
            IgnoreRules extensionDiscoveryRules,
            IgnoreRules effectiveRules,
            IExtensionInclusionPolicy? effectiveExtensionPolicy,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetIgnoreSectionSnapshot(
                rootPath,
                extensionDiscoveryRules,
                effectiveRules,
                effectiveExtensionPolicy,
                EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
            string rootPath,
            IgnoreRules extensionDiscoveryRules,
            IgnoreRules effectiveRules,
            IExtensionInclusionPolicy? effectiveExtensionPolicy,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetRootFileIgnoreSectionSnapshot(
                rootPath,
                extensionDiscoveryRules,
                effectiveRules,
                effectiveExtensionPolicy,
                EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<ProjectWorkspaceScanSnapshot> ScanProjectWorkspace(
            ProjectWorkspaceScanRequest request,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(request.RootPath, cancellationToken);
            return _inner.ScanProjectWorkspace(
                request,
                EffectiveCancellationToken(cancellationToken));
        }

        public void Dispose()
        {
            _blocked.Dispose();
            _release.Dispose();
        }

        private void MaybeBlock(string rootPath, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _enabled) == 0 || !IsInsideBlockedRoot(rootPath))
                return;

            _blocked.Set();
            if (_ignoreCancellation)
            {
                if (!_release.Wait(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("Timed out waiting to release the controlled stale refresh.");
                return;
            }

            var signaled = WaitHandle.WaitAny(
                [_release.WaitHandle, cancellationToken.WaitHandle],
                TimeSpan.FromSeconds(30));
            if (signaled == WaitHandle.WaitTimeout)
                throw new TimeoutException("Timed out waiting to release the controlled stale refresh.");

            cancellationToken.ThrowIfCancellationRequested();
        }

        private CancellationToken EffectiveCancellationToken(CancellationToken cancellationToken) =>
            _ignoreCancellation ? CancellationToken.None : cancellationToken;

        private bool IsInsideBlockedRoot(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var rootPath = Path.GetFullPath(blockedRootPath).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);

            if (string.Equals(fullPath, rootPath, PathComparison))
                return true;
            if (!fullPath.StartsWith(rootPath, PathComparison))
                return false;

            var next = fullPath[rootPath.Length];
            return next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar;
        }

        private static StringComparison PathComparison => OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
    }
}
