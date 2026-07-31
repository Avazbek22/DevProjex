namespace DevProjex.Tests.Unit.Avalonia;

using global::Avalonia.Input;

public sealed class MainWindowDropAndTitleBehaviorTests
{
    [Fact]
    public void ResolveDropFolderPath_SkipsNonFolderCandidateFilteredByStorageProvider()
    {
        var method = GetPrivateStaticMethod("ResolveDropFolderPath");
        using var temp = new TemporaryDirectory();
        var folder = temp.CreateFolder("project");

        var result = (string?)method.Invoke(null, [new string?[] { null, folder }]);

        Assert.Equal(folder, result);
    }

    [Fact]
    public void ResolveDropFolderPath_ReturnsNull_WhenStorageProviderReportsNoFolder()
    {
        var method = GetPrivateStaticMethod("ResolveDropFolderPath");

        var result = (string?)method.Invoke(null, [Array.Empty<string?>()]);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveDropFolderPath_UsesFirstFolderPath_WhenSeveralAreProvided()
    {
        var method = GetPrivateStaticMethod("ResolveDropFolderPath");
        using var temp = new TemporaryDirectory();
        var firstFolder = temp.CreateFolder("first");
        var secondFolder = temp.CreateFolder("second");

        var result = (string?)method.Invoke(null, [new[] { firstFolder, secondFolder }]);

        Assert.Equal(firstFolder, result);
    }

    [Fact]
    public void ResolveDropFolderPath_SkipsEmptyLocalPathsBeforeFolder()
    {
        var method = GetPrivateStaticMethod("ResolveDropFolderPath");
        using var temp = new TemporaryDirectory();
        var folder = temp.CreateFolder("project");

        var result = (string?)method.Invoke(
            null,
            [new string?[] { null, "", "  ", folder }]);

        Assert.Equal(folder, result);
    }

    [Fact]
    public void ResolveDropFolderPath_IgnoresWhitespacePaths()
    {
        var method = GetPrivateStaticMethod("ResolveDropFolderPath");

        var result = (string?)method.Invoke(null, [new string?[] { null, "", "  " }]);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(false, DragDropEffects.None)]
    [InlineData(true, DragDropEffects.Copy)]
    public void ResolveDropEffect_MapsFolderValidityToNativeCursorFeedback(
        bool hasFolder,
        DragDropEffects expected)
    {
        var method = GetPrivateStaticMethod("ResolveDropEffect");

        var result = (DragDropEffects)method.Invoke(null, [hasFolder])!;

        Assert.Equal(expected, result);
    }

    [Fact]
    public void BuildWindowTitle_NoProjectLoaded_UsesBaseTitleWithAuthor()
    {
        var method = GetPrivateStaticMethod("BuildWindowTitle");

        var title = (string)method.Invoke(null, [null, false, null, null, null])!;

        Assert.Equal(MainWindowViewModel.BaseTitleWithAuthor, title);
    }

    [Fact]
    public void BuildWindowTitle_GitMode_NormalizesRepositoryUrlAndAppendsBranch()
    {
        var method = GetPrivateStaticMethod("BuildWindowTitle");

        var title = (string)method.Invoke(null,
        [
            @"C:\cache\repo",
            true,
            "https://github.com/user/repo.git?tab=readme#top",
            "main",
            null
        ])!;

        Assert.Equal($"{MainWindowViewModel.BaseTitle} - https://github.com/user/repo [main]", title);
    }

    [Fact]
    public void BuildWindowTitle_LocalMode_UsesProjectDisplayNameWhenAvailable()
    {
        var method = GetPrivateStaticMethod("BuildWindowTitle");

        var title = (string)method.Invoke(null,
        [
            @"C:\Projects\DevProjex",
            false,
            null,
            null,
            "DevProjex"
        ])!;

        Assert.Equal($"{MainWindowViewModel.BaseTitle} - DevProjex", title);
    }

    [Fact]
    public void BuildWindowTitle_LocalMode_FallsBackToPathWhenDisplayNameMissing()
    {
        var method = GetPrivateStaticMethod("BuildWindowTitle");
        const string projectPath = @"C:\Projects\Sample";

        var title = (string)method.Invoke(null,
        [
            projectPath,
            false,
            null,
            null,
            null
        ])!;

        Assert.Equal($"{MainWindowViewModel.BaseTitle} - {projectPath}", title);
    }

    private static MethodInfo GetPrivateStaticMethod(string name)
    {
        var method = typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method!;
    }
}
