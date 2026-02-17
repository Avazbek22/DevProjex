namespace DevProjex.Tests.Integration;

public sealed class ExportFormatRulesWiringIntegrationTests
{
    [Fact]
    public void MainWindow_ExportTree_UsesFormatForDefaultExtensionAndConditionalFileTypes()
    {
        var content = ReadMainWindowCode();
        var body = Slice(content, "private async void OnExportTreeToFile(", "private async void OnExportContentToFile(");

        Assert.Contains("var saveAsJson = format == TreeTextFormat.Json;", body, StringComparison.Ordinal);
        Assert.Contains("BuildSuggestedExportFileName(\"tree\", saveAsJson)", body, StringComparison.Ordinal);
        Assert.Contains("useJsonDefaultExtension: saveAsJson", body, StringComparison.Ordinal);
        Assert.Contains("allowBothExtensions: saveAsJson", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_ExportTree_PassesCurrentFormatToTreePayloadBuilder()
    {
        var content = ReadMainWindowCode();
        var body = Slice(content, "private async void OnExportTreeToFile(", "private async void OnExportContentToFile(");

        Assert.Contains("var format = GetCurrentTreeTextFormat();", body, StringComparison.Ordinal);
        Assert.Contains("var content = BuildTreeTextForSelection(selected, format);", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_ExportContent_AlwaysUsesTxtFileType()
    {
        var content = ReadMainWindowCode();
        var body = Slice(content, "private async void OnExportContentToFile(", "private async void OnExportTreeAndContentToFile(");

        Assert.Contains("BuildSuggestedExportFileName(\"content\", saveAsJson: false)", body, StringComparison.Ordinal);
        Assert.Contains("useJsonDefaultExtension: false", body, StringComparison.Ordinal);
        Assert.Contains("allowBothExtensions: false", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_ExportContent_DoesNotDependOnTreeFormatToggle()
    {
        var content = ReadMainWindowCode();
        var body = Slice(content, "private async void OnExportContentToFile(", "private async void OnExportTreeAndContentToFile(");

        Assert.DoesNotContain("GetCurrentTreeTextFormat()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_ExportTreeAndContent_UsesFormatForPayloadButForcesTxtFileType()
    {
        var content = ReadMainWindowCode();
        var body = Slice(content, "private async void OnExportTreeAndContentToFile(", "private TreeTextFormat GetCurrentTreeTextFormat()");

        Assert.Contains("var format = GetCurrentTreeTextFormat();", body, StringComparison.Ordinal);
        Assert.Contains("_treeAndContentExport.BuildAsync(_currentPath!, _currentTree!.Root, selected, format, CancellationToken.None)", body, StringComparison.Ordinal);
        Assert.Contains("var saveAsJson = false;", body, StringComparison.Ordinal);
        Assert.Contains("BuildSuggestedExportFileName(\"tree_content\", saveAsJson)", body, StringComparison.Ordinal);
        Assert.Contains("useJsonDefaultExtension: false", body, StringComparison.Ordinal);
        Assert.Contains("allowBothExtensions: false", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_ExportHandlers_UseExpectedLocalizedDialogTitles()
    {
        var content = ReadMainWindowCode();
        var treeBody = Slice(content, "private async void OnExportTreeToFile(", "private async void OnExportContentToFile(");
        var contentBody = Slice(content, "private async void OnExportContentToFile(", "private async void OnExportTreeAndContentToFile(");
        var combinedBody = Slice(content, "private async void OnExportTreeAndContentToFile(", "private TreeTextFormat GetCurrentTreeTextFormat()");

        Assert.Contains("_viewModel.MenuFileExportTree", treeBody, StringComparison.Ordinal);
        Assert.Contains("_viewModel.MenuFileExportContent", contentBody, StringComparison.Ordinal);
        Assert.Contains("_viewModel.MenuFileExportTreeAndContent", combinedBody, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_SavePicker_UsesExpectedTypeChoicesAndDefaultExtensionLogic()
    {
        var content = ReadMainWindowCode();
        var body = Slice(content, "private async Task<bool> TryExportTextToFileAsync(", "private string BuildSuggestedExportFileName(");

        Assert.Contains("DefaultExtension = useJsonDefaultExtension ? \"json\" : \"txt\"", body, StringComparison.Ordinal);
        Assert.Contains("FileTypeChoices = allowBothExtensions", body, StringComparison.Ordinal);
        Assert.Contains("? new[] { jsonFileType, textFileType }", body, StringComparison.Ordinal);
        Assert.Contains(": useJsonDefaultExtension", body, StringComparison.Ordinal);
        Assert.Contains("? new[] { jsonFileType }", body, StringComparison.Ordinal);
        Assert.Contains(": new[] { textFileType }", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_SavePicker_AllowsMixedChoicesOnlyWhenRequestedByCaller()
    {
        var content = ReadMainWindowCode();
        var treeBody = Slice(content, "private async void OnExportTreeToFile(", "private async void OnExportContentToFile(");
        var pickerBody = Slice(content, "private async Task<bool> TryExportTextToFileAsync(", "private string BuildSuggestedExportFileName(");

        Assert.Contains("allowBothExtensions: saveAsJson", treeBody, StringComparison.Ordinal);
        Assert.Contains("new[] { jsonFileType, textFileType }", pickerBody, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_SavePicker_DefinesJsonAndTextFileTypeMetadata()
    {
        var content = ReadMainWindowCode();
        var body = Slice(content, "private async Task<bool> TryExportTextToFileAsync(", "private string BuildSuggestedExportFileName(");

        Assert.Contains("new FilePickerFileType(\"JSON\")", body, StringComparison.Ordinal);
        Assert.Contains("Patterns = new[] { \"*.json\" }", body, StringComparison.Ordinal);
        Assert.Contains("MimeTypes = new[] { \"application/json\" }", body, StringComparison.Ordinal);
        Assert.Contains("new FilePickerFileType(\"Text\")", body, StringComparison.Ordinal);
        Assert.Contains("Patterns = new[] { \"*.txt\" }", body, StringComparison.Ordinal);
        Assert.Contains("MimeTypes = new[] { \"text/plain\" }", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_SavePicker_UsesExplicitFlagsInSignatureAndExtensionSelection()
    {
        var content = ReadMainWindowCode();
        var body = Slice(content, "private async Task<bool> TryExportTextToFileAsync(", "private string BuildSuggestedExportFileName(");

        Assert.Contains("bool useJsonDefaultExtension,", body, StringComparison.Ordinal);
        Assert.Contains("bool allowBothExtensions)", body, StringComparison.Ordinal);
        Assert.Contains("DefaultExtension = useJsonDefaultExtension ? \"json\" : \"txt\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_SavePicker_WritesExportContentToSelectedStream()
    {
        var content = ReadMainWindowCode();
        var body = Slice(content, "private async Task<bool> TryExportTextToFileAsync(", "private string BuildSuggestedExportFileName(");

        Assert.Contains("await using var stream = await file.OpenWriteAsync();", body, StringComparison.Ordinal);
        Assert.Contains("await _textFileExport.WriteAsync(stream, content);", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_SuggestedFileName_UsesExtensionFromSaveAsJsonFlag()
    {
        var content = ReadMainWindowCode();
        var body = Slice(content, "private string BuildSuggestedExportFileName(", "private void OnExpandAll(");

        Assert.Contains("var extension = saveAsJson ? \"json\" : \"txt\";", body, StringComparison.Ordinal);
        Assert.Contains("return $\"{sanitized}_{suffix}.{extension}\";", body, StringComparison.Ordinal);
    }

    private static string ReadMainWindowCode()
    {
        var repoRoot = FindRepositoryRoot();
        var file = Path.Combine(repoRoot, "Apps", "Avalonia", "DevProjex.Avalonia", "MainWindow.axaml.cs");
        return File.ReadAllText(file);
    }

    private static string Slice(string content, string startMarker, string endMarker)
    {
        var start = content.IndexOf(startMarker, StringComparison.Ordinal);
        var end = content.IndexOf(endMarker, StringComparison.Ordinal);

        Assert.True(start >= 0, $"Start marker not found: {startMarker}");
        Assert.True(end > start, $"End marker not found after start: {endMarker}");

        return content.Substring(start, end - start);
    }

    private static string FindRepositoryRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) ||
                File.Exists(Path.Combine(dir, "DevProjex.sln")))
                return dir;

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
