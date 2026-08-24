using System.Xml.Linq;

namespace DevProjex.Tests.Unit;

public sealed class ProjectCopyExportUiContractTests
{
    [Fact]
    public void TopMenu_ProjectCopyIsSiblingOfTextExportAndPrecedesExit()
    {
        var document = ReadTopMenuDocument();
        var parent = FindNamedElement(document, "ExportProjectCopyMenuItem");
        var fileMenu = Assert.IsType<XElement>(parent.Parent);
        var siblings = fileMenu.Elements().Where(IsMenuItem).ToArray();
        var textExport = Assert.Single(siblings, item => Attribute(item, "Header") == "{Binding MenuFileExport}");
        var exit = Assert.Single(siblings, item => Attribute(item, "Header") == "{Binding MenuFileExit}");

        Assert.True(Array.IndexOf(siblings, textExport) < Array.IndexOf(siblings, parent));
        Assert.True(Array.IndexOf(siblings, parent) < Array.IndexOf(siblings, exit));
        Assert.Equal("{Binding MenuFileExportProjectCopy}", Attribute(parent, "Header"));
        Assert.DoesNotContain(parent.Elements(), element => element.Name.LocalName == "MenuItem.Header");
        Assert.Null(Attribute(parent, "HelpText"));
    }

    [Theory]
    [InlineData("ExportProjectCopyFolderMenuItem", "MenuFileExportProjectCopyFolder", "MenuFileExportProjectCopyFolderHelp", "OnExportProjectCopyToFolder")]
    [InlineData("ExportProjectCopyZipMenuItem", "MenuFileExportProjectCopyZip", "MenuFileExportProjectCopyZipHelp", "OnExportProjectCopyToZip")]
    public void ChildAction_HasIndependentAccessibleHelpIndicator(
        string itemName,
        string labelProperty,
        string helpProperty,
        string clickHandler)
    {
        var item = FindNamedElement(ReadTopMenuDocument(), itemName);
        var header = Assert.Single(item.Elements(), element => element.Name.LocalName == "MenuItem.Header");
        var row = Assert.Single(header.Elements(), element => element.Name.LocalName == "Grid");
        var indicator = Assert.Single(row.Descendants(), element =>
            element.Name.LocalName == "Border" && Attribute(element, "Classes") == "project-copy-help-indicator");
        var tooltip = Assert.Single(indicator.Descendants(), element => element.Name.LocalName == "ToolTip");
        var sharedLabelColumn = Assert.Single(row.Descendants(), element =>
            element.Name.LocalName == "ColumnDefinition" &&
            Attribute(element, "SharedSizeGroup") == "ProjectCopyActionLabel");

        Assert.Equal(clickHandler, Attribute(item, "Click"));
        Assert.Equal("11,4,0,7", Attribute(item, "Padding"));
        Assert.Equal($"{{Binding {helpProperty}}}", Attribute(item, "HelpText"));
        Assert.Equal("Auto", Attribute(sharedLabelColumn, "Width"));
        Assert.Equal("2", Attribute(indicator, "Grid.Column"));
        Assert.Contains(row.Descendants(), element =>
            element.Name.LocalName == "TextBlock" && Attribute(element, "Text") == $"{{Binding {labelProperty}}}");
        Assert.Equal("16", Attribute(indicator, "Width"));
        Assert.Equal("16", Attribute(indicator, "Height"));
        Assert.Equal("8", Attribute(indicator, "CornerRadius"));
        Assert.Null(Attribute(indicator, "Cursor"));
        Assert.Equal("OnProjectCopyHelpIndicatorPointerPressed", Attribute(indicator, "PointerPressed"));
        Assert.Equal("OnProjectCopyHelpIndicatorPointerReleased", Attribute(indicator, "PointerReleased"));
        Assert.Null(Attribute(tooltip, "Loaded"));
        Assert.Contains(indicator.Descendants(), element =>
            element.Name.LocalName == "TextBlock" && Attribute(element, "Text") == $"{{Binding {helpProperty}}}" &&
            Attribute(element, "TextWrapping") == "Wrap");
        Assert.DoesNotContain(item.Descendants(), element => element.Name.LocalName == "Button");
    }

    [Fact]
    public void HelpIndicatorPointerEventsAreConsumedWithoutInvokingExport()
    {
        var source = ReadRepositoryFile(
            "Apps",
            "Avalonia",
            "Views",
            "TopMenuBarView.axaml.cs");
        AssertPointerHandlerConsumes(source, "OnProjectCopyHelpIndicatorPointerPressed");
        AssertPointerHandlerConsumes(source, "OnProjectCopyHelpIndicatorPointerReleased");
        Assert.Contains("ToolTip.SetIsOpen(indicator, true);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectCopyExport_UsesCurrentTreeAndLocalizedToastWithoutRawErrors()
    {
        var source = ReadRepositoryFile(
            "Apps",
            "Avalonia",
            "MainWindow.ProjectCopyExport.cs");

        Assert.Contains("_currentTree.Root", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_filterBaseTree", source, StringComparison.Ordinal);
        Assert.Contains("GetCheckedPaths()", source, StringComparison.Ordinal);
        Assert.Contains("Picker.ProjectCopy.Folder", source, StringComparison.Ordinal);
        Assert.Contains(
            "Title = _localization.Format(\"Picker.ProjectCopy.Folder\", folderName)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("SuggestedFileName = folderName", source, StringComparison.Ordinal);
        Assert.Contains("AddPathWrapOpportunities", source, StringComparison.Ordinal);
        Assert.Contains("ProjectCopyResultToastDuration", source, StringComparison.Ordinal);
        Assert.Contains("ProcessedEntryCount", source, StringComparison.Ordinal);
        Assert.Contains("TotalEntryCount", source, StringComparison.Ordinal);
        Assert.Contains("ProjectCopyExportErrorPresentation.ResolveLocalizationKey", source, StringComparison.Ordinal);
        Assert.DoesNotContain("exception.Message", source, StringComparison.Ordinal);
        Assert.DoesNotContain("innerException.Message", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowErrorAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TextFileExport_RejectsCanonicalDestinationInsideLoadedProjectBeforeOpeningStream()
    {
        var source = ReadRepositoryFile(
            "Apps",
            "Avalonia",
            "MainWindow.TextOutput.cs");
        Assert.Contains(
            "result.Content,\n                snapshot.RootPath,",
            source.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        var methodStart = source.IndexOf(
            "private async Task<bool> TryExportTextToFileAsync",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private static IReadOnlyList<FilePickerFileType>",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
		var method = source[methodStart..methodEnd];
		var guardIndex = method.IndexOf(
			"ProjectCopyExportService.ResolveDestinationOutsideProject",
			StringComparison.Ordinal);
		var streamOpenIndex = method.IndexOf("file.OpenWriteAsync()", StringComparison.Ordinal);

		Assert.Contains("file.TryGetLocalPath()", method, StringComparison.Ordinal);
		Assert.Contains("await Task.Run", method, StringComparison.Ordinal);
		Assert.Contains("AtomicFileOutput.WriteAsync(", method, StringComparison.Ordinal);
		Assert.DoesNotContain("FileMode.Create", method, StringComparison.Ordinal);
		Assert.DoesNotContain("file.OpenWriteAsync()", method, StringComparison.Ordinal);
		Assert.Contains("string sourceRootPath", method, StringComparison.Ordinal);
		Assert.DoesNotContain("_currentPath", method, StringComparison.Ordinal);
		Assert.Contains(
			"Path.GetFullPath(destinationPath)",
			method,
			StringComparison.Ordinal);
		Assert.Contains(
			"path => ProjectCopyExportService.ResolveDestinationOutsideProject(",
			method,
			StringComparison.Ordinal);
		Assert.Contains(
			"_localization[\"Error.ProjectCopy.UnsafeDestinationPath\"]",
			method,
			StringComparison.Ordinal);
		Assert.Contains(
			"ProjectCopyExportErrorPresentation.ResolveLocalizationKey",
			method,
			StringComparison.Ordinal);
		Assert.True(guardIndex >= 0);
		Assert.Equal(-1, streamOpenIndex);
    }

    [Fact]
    public void TextFileExport_CapturesWindowLifetimeBeforePickerAndUsesItForEveryWrite()
    {
        var source = ReadRepositoryFile(
            "Apps",
            "Avalonia",
            "MainWindow.TextOutput.cs");
        var methodStart = source.IndexOf(
            "private async Task<bool> TryExportTextToFileAsync",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private static IReadOnlyList<FilePickerFileType>",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var method = source[methodStart..methodEnd].ReplaceLineEndings("\n");
        var lifetimeCaptureIndex = method.IndexOf(
            "var windowLifetime = _windowLifetimeCts;",
            StringComparison.Ordinal);
        var lifetimeTokenIndex = method.IndexOf(
            "var cancellationToken = windowLifetime.Token;",
            StringComparison.Ordinal);
        var pickerIndex = method.IndexOf(
            "await StorageProvider.SaveFilePickerAsync(options)",
            StringComparison.Ordinal);
        var postPickerCancellationIndex = method.IndexOf(
            "cancellationToken.ThrowIfCancellationRequested();",
            pickerIndex,
            StringComparison.Ordinal);

        Assert.True(lifetimeCaptureIndex >= 0 && lifetimeCaptureIndex < pickerIndex);
        Assert.True(lifetimeTokenIndex > lifetimeCaptureIndex && lifetimeTokenIndex < pickerIndex);
        Assert.True(postPickerCancellationIndex > pickerIndex);
        Assert.Contains(
            "writeCancellationToken)",
            method,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_textFileExport.WriteAsync(stream, content, cancellationToken)",
            method,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectCopyExport_DisablesTreeMutationButKeepsReadOnlyAndIgnoredActionsVisuallyAvailable()
    {
        var topMenu = ReadTopMenuDocument();
        var window = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "Apps",
            "Avalonia",
            "MainWindow.axaml"));
        var settings = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "Apps",
            "Avalonia",
            "Views",
            "SettingsPanelView.axaml"));

        Assert.Equal("{Binding CanChangeProjectTree}", Attribute(FindMenuByHeader(topMenu, "{Binding MenuFileOpen}"), "IsEnabled"));
        Assert.Equal("{Binding CanChangeProjectTree}", Attribute(FindNamedElement(topMenu, "RecentMenuItem"), "IsEnabled"));
        Assert.Equal("{Binding CanChangeProjectTree}", Attribute(FindNamedElement(topMenu, "GitMenuItem"), "IsEnabled"));
        Assert.Equal("{Binding CanRefreshLocalProject}", Attribute(FindNamedElement(topMenu, "RefreshMenuItem"), "IsEnabled"));
        Assert.Equal("{Binding IsProjectLoaded}", Attribute(FindMenuByHeader(topMenu, "{Binding MenuFileExport}"), "IsEnabled"));
        Assert.Equal("{Binding IsProjectLoaded}", Attribute(FindMenuByHeader(topMenu, "{Binding MenuCopy}"), "IsEnabled"));
        Assert.Equal("{Binding IsProjectLoaded}", Attribute(FindMenuByHeader(topMenu, "{Binding MenuView}"), "IsEnabled"));
        Assert.Equal("{Binding IsSearchAvailable}", Attribute(FindMenuByHeader(topMenu, "{Binding MenuSearch}"), "IsEnabled"));
        Assert.Equal("{Binding IsProjectLoaded}", Attribute(FindMenuByHeader(topMenu, "{Binding MenuOptions}"), "IsEnabled"));
        Assert.Equal("{Binding IsProjectLoaded}", Attribute(FindNamedElement(topMenu, "FormatSegmentedControl"), "IsEnabled"));
        Assert.Equal("{Binding IsProjectLoaded}", Attribute(FindNamedElement(topMenu, "PreviewToggleButton"), "IsEnabled"));
        Assert.Equal("{Binding IsProjectLoaded}", Attribute(FindNamedElement(topMenu, "FilterToggleButton"), "IsEnabled"));
        Assert.Equal("{Binding IsSearchAvailable}", Attribute(FindNamedElement(window, "SearchBar"), "IsEnabled"));
        Assert.Equal("{Binding IsSearchFilterAvailable}", Attribute(FindNamedElement(window, "FilterBar"), "IsEnabled"));
        Assert.Equal("{Binding CanUseProjectWorkspaceActions}", Attribute(FindNamedElement(window, "ProjectTree"), "IsEnabled"));
        Assert.Equal("{Binding IsProjectLoaded}", Attribute(FindNamedElement(window, "PreviewBar"), "IsEnabled"));
        Assert.Equal("{Binding AreFilterSettingsEnabled}", Attribute(FindNamedElement(settings, "PanelRoot"), "IsEnabled"));
    }

    [Fact]
    public void ProjectCopyExport_CancelsPendingPreviewAndGuardsPreviewAndFormatActions()
    {
        var exportSource = ReadRepositoryFile(
            "Apps",
            "Avalonia",
            "MainWindow.ProjectCopyExport.cs");
        var menuSource = ReadRepositoryFile(
            "Apps",
            "Avalonia",
            "Views",
            "TopMenuBarView.axaml.cs");
        var windowSource = ReadRepositoryFile(
            "Apps",
            "Avalonia",
            "MainWindow.axaml.cs");
        var outputSource = ReadRepositoryFile(
            "Apps",
            "Avalonia",
            "MainWindow.TextOutput.cs");

        Assert.Contains("CancelPreviewRefresh();", exportSource, StringComparison.Ordinal);
        Assert.Contains("await Task.Run(", exportSource, StringComparison.Ordinal);
        Assert.Contains("CanTogglePreview: false", menuSource, StringComparison.Ordinal);
        Assert.Contains("CanUseProjectWorkspaceActions: true", menuSource, StringComparison.Ordinal);
        Assert.Contains("if (!_viewModel.CanTogglePreview)", windowSource, StringComparison.Ordinal);
        Assert.Contains(
            "if (!_viewModel.CanTogglePreview || !_viewModel.IsPreviewMode)",
            windowSource,
            StringComparison.Ordinal);
        Assert.Contains("BeginOutputPreparationStatus()", outputSource, StringComparison.Ordinal);
        Assert.Contains("if (_viewModel.IsProjectCopyExportInProgress)", outputSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ClosingWindow_CancelsProjectCopyAndWaitsForStagingCleanupBeforeClosing()
    {
        var compositionSource = ReadRepositoryFile(
            "Apps",
            "Avalonia",
            "MainWindow.Composition.cs");
        var lifecycleSource = ReadRepositoryFile(
            "Apps",
            "Avalonia",
            "MainWindow.Lifecycle.cs");
        var exportSource = ReadRepositoryFile(
            "Apps",
            "Avalonia",
            "MainWindow.ProjectCopyExport.cs");

        Assert.Contains("Closing += OnWindowClosing", compositionSource, StringComparison.Ordinal);
        Assert.Contains("e.Cancel = true", lifecycleSource, StringComparison.Ordinal);
        Assert.Contains("_projectCopyExportCts.Cancel()", lifecycleSource, StringComparison.Ordinal);
        Assert.Contains("await completion", lifecycleSource, StringComparison.Ordinal);
        Assert.Contains("_allowCloseAfterProjectCopyExportCleanup = true", lifecycleSource, StringComparison.Ordinal);
        Assert.Contains("completion.TrySetResult(true)", exportSource, StringComparison.Ordinal);
    }

    private static XDocument ReadTopMenuDocument() => XDocument.Load(Path.Combine(
        FindRepositoryRoot(),
        "Apps",
        "Avalonia",
        "Views",
        "TopMenuBarView.axaml"));

    private static XElement FindNamedElement(XDocument document, string name) =>
        Assert.Single(document.Descendants(), element => Attribute(element, "Name") == name);

    private static XElement FindMenuByHeader(XDocument document, string header) =>
        Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "MenuItem" && Attribute(element, "Header") == header);

    private static bool IsMenuItem(XElement element) => element.Name.LocalName == "MenuItem";

    private static string? Attribute(XElement element, string localName)
    {
        var exact = element.Attributes().SingleOrDefault(attribute =>
            attribute.Name.LocalName == localName);
        return exact?.Value ?? element.Attributes().SingleOrDefault(attribute =>
            attribute.Name.LocalName.EndsWith($".{localName}", StringComparison.Ordinal))?.Value;
    }

    private static string ReadRepositoryFile(params string[] parts) =>
        File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. parts]));

    private static void AssertPointerHandlerConsumes(string source, string methodName)
    {
        var handlerStart = source.IndexOf(methodName, StringComparison.Ordinal);
        Assert.True(handlerStart >= 0, $"Handler {methodName} was not found.");
        var nextMethod = source.IndexOf("private ", handlerStart + methodName.Length, StringComparison.Ordinal);
        var handler = nextMethod < 0 ? source[handlerStart..] : source[handlerStart..nextMethod];

        Assert.Contains("e.Handled = true", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("ExportProjectCopyToFolderRequested", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("ExportProjectCopyToZipRequested", handler, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (File.Exists(Path.Combine(directory, "DevProjex.sln")))
                return directory;

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
