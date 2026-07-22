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
        Assert.Equal("Help", Attribute(indicator, "Cursor"));
        Assert.Equal("OnProjectCopyHelpIndicatorPointerPressed", Attribute(indicator, "PointerPressed"));
        Assert.Equal("OnProjectCopyHelpIndicatorPointerReleased", Attribute(indicator, "PointerReleased"));
        Assert.Equal("OnToolTipLoaded", Attribute(tooltip, "Loaded"));
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
            "DevProjex.Avalonia",
            "Views",
            "TopMenuBarView.axaml.cs");
        AssertPointerHandlerConsumes(source, "OnProjectCopyHelpIndicatorPointerPressed");
        AssertPointerHandlerConsumes(source, "OnProjectCopyHelpIndicatorPointerReleased");
    }

    [Fact]
    public void ProjectCopyExport_UsesCurrentTreeAndLocalizedToastWithoutRawErrors()
    {
        var source = ReadRepositoryFile(
            "Apps",
            "Avalonia",
            "DevProjex.Avalonia",
            "MainWindow.ProjectCopyExport.cs");

        Assert.Contains("_currentTree.Root", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_filterBaseTree", source, StringComparison.Ordinal);
        Assert.Contains("GetCheckedPaths()", source, StringComparison.Ordinal);
        Assert.Contains("ProjectCopyExportErrorPresentation.ResolveLocalizationKey", source, StringComparison.Ordinal);
        Assert.DoesNotContain("exception.Message", source, StringComparison.Ordinal);
        Assert.DoesNotContain("innerException.Message", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowErrorAsync", source, StringComparison.Ordinal);
    }

    private static XDocument ReadTopMenuDocument() => XDocument.Load(Path.Combine(
        FindRepositoryRoot(),
        "Apps",
        "Avalonia",
        "DevProjex.Avalonia",
        "Views",
        "TopMenuBarView.axaml"));

    private static XElement FindNamedElement(XDocument document, string name) =>
        Assert.Single(document.Descendants(), element => Attribute(element, "Name") == name);

    private static bool IsMenuItem(XElement element) => element.Name.LocalName == "MenuItem";

    private static string? Attribute(XElement element, string localName) =>
        element.Attributes().SingleOrDefault(attribute =>
            attribute.Name.LocalName == localName ||
            attribute.Name.LocalName.EndsWith($".{localName}", StringComparison.Ordinal))?.Value;

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
