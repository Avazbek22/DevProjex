using DevProjex.Infrastructure.AppInstances;

namespace DevProjex.Tests.Unit.AppInstances;

public sealed class AppInstanceStartInfoFactoryTests
{
    [Fact]
    public void CreateCandidates_PackagedWindowsContext_PrefersAppsFolderActivationBeforeProcessFallback()
    {
        var context = new AppInstanceLaunchContext(
            IsWindows: true,
            ProcessPath: @"C:\Program Files\DevProjex\DevProjex.exe",
            EntryAssemblyPath: null,
            WorkingDirectory: @"C:\Program Files\DevProjex",
            WindowsPackageFamilyName: "Contoso.DevProjex_123456");

        var candidates = AppInstanceStartInfoFactory.CreateCandidates(context);

        Assert.Equal(2, candidates.Count);

        var packagedCandidate = candidates[0];
        Assert.Equal("explorer.exe", packagedCandidate.FileName);
        Assert.Equal(@"shell:AppsFolder\Contoso.DevProjex_123456!App", packagedCandidate.Arguments);
        Assert.True(packagedCandidate.UseShellExecute);

        var fallbackCandidate = candidates[1];
        Assert.Equal(context.ProcessPath, fallbackCandidate.FileName);
        Assert.False(fallbackCandidate.UseShellExecute);
        Assert.Equal(context.WorkingDirectory, fallbackCandidate.WorkingDirectory);
    }

    [Fact]
    public void CreateCandidates_DotnetHostContext_LaunchesEntryAssemblyThroughCurrentHost()
    {
        var context = new AppInstanceLaunchContext(
            IsWindows: false,
            ProcessPath: @"C:\Program Files\dotnet\dotnet.exe",
            EntryAssemblyPath: @"C:\Users\avazb\RiderProjects\DevProjex\Apps\Avalonia\DevProjex.Avalonia\bin\Debug\net10.0\DevProjex.dll",
            WorkingDirectory: @"C:\Users\avazb\RiderProjects\DevProjex",
            WindowsPackageFamilyName: null);

        var candidates = AppInstanceStartInfoFactory.CreateCandidates(context);

        var candidate = Assert.Single(candidates);
        Assert.Equal(context.ProcessPath, candidate.FileName);
        Assert.False(candidate.UseShellExecute);
        Assert.Equal(context.WorkingDirectory, candidate.WorkingDirectory);
        Assert.Single(candidate.ArgumentList);
        Assert.Equal(context.EntryAssemblyPath, candidate.ArgumentList[0]);
    }

    [Fact]
    public void CreateCandidates_AppHostContext_UsesCurrentProcessPathWithoutArguments()
    {
        var context = new AppInstanceLaunchContext(
            IsWindows: false,
            ProcessPath: "/usr/local/bin/devprojex",
            EntryAssemblyPath: "/usr/local/bin/DevProjex.dll",
            WorkingDirectory: "/usr/local/bin",
            WindowsPackageFamilyName: null);

        var candidates = AppInstanceStartInfoFactory.CreateCandidates(context);

        var candidate = Assert.Single(candidates);
        Assert.Equal(context.ProcessPath, candidate.FileName);
        Assert.False(candidate.UseShellExecute);
        Assert.Equal(context.WorkingDirectory, candidate.WorkingDirectory);
        Assert.Empty(candidate.ArgumentList);
    }

    [Fact]
    public void CreateCandidates_WithoutProcessPath_ReturnsNoFallbackCandidate()
    {
        var context = new AppInstanceLaunchContext(
            IsWindows: false,
            ProcessPath: null,
            EntryAssemblyPath: null,
            WorkingDirectory: AppContext.BaseDirectory,
            WindowsPackageFamilyName: null);

        var candidates = AppInstanceStartInfoFactory.CreateCandidates(context);

        Assert.Empty(candidates);
    }
}
