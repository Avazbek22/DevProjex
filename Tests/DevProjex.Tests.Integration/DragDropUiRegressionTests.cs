using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Xunit;

namespace DevProjex.Tests.Integration;

public sealed class DragDropUiRegressionTests
{
    private static readonly Lazy<string> RepoRoot = new(FindRepositoryRoot);

    [Fact]
    public void TopMenuBar_MenuSpansBothColumns_ToAvoidFilterAreaSeam()
    {
        var file = Path.Combine(RepoRoot.Value, "Apps", "Avalonia", "DevProjex.Avalonia", "Views", "TopMenuBarView.axaml");
        var content = File.ReadAllText(file);

        Assert.Contains("Name=\"MainMenu\" Grid.Column=\"0\" Grid.ColumnSpan=\"2\"", content);
        Assert.Contains("Button Grid.Column=\"1\"", content);
        Assert.Contains("Classes=\"menu-icon-button\"", content);
    }

    [Fact]
    public void MainWindow_DropZoneUsesHotkeyHintAndNoSubtitleBinding()
    {
        var file = Path.Combine(RepoRoot.Value, "Apps", "Avalonia", "DevProjex.Avalonia", "MainWindow.axaml");
        var content = File.ReadAllText(file);

        Assert.Contains("Text=\"{Binding DropZoneHotkeyHint}\"", content);
        Assert.DoesNotContain("Text=\"{Binding DropZoneSubtitle}\"", content);
    }

    [Fact]
    public void MainWindowViewModel_DoesNotReferenceDropZoneSubtitleLocalizationKey()
    {
        var file = Path.Combine(RepoRoot.Value, "Apps", "Avalonia", "DevProjex.Avalonia", "ViewModels", "MainWindowViewModel.cs");
        var content = File.ReadAllText(file);

        Assert.DoesNotContain("DropZoneSubtitle", content);
        Assert.DoesNotContain("DropZone.Subtitle", content);
    }

    [Fact]
    public void LocalizationFiles_DoNotContainDropZoneSubtitleKey()
    {
        var localizationDir = Path.Combine(RepoRoot.Value, "Assets", "Localization");
        var files = Directory.GetFiles(localizationDir, "*.json");

        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var json = File.ReadAllText(file);
            var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            Assert.NotNull(keys);
            Assert.DoesNotContain("DropZone.Subtitle", keys!.Keys);
        }
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
