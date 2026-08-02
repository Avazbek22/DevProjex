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
            AppHostPath: null,
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

    [Theory]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    [InlineData("/usr/local/share/dotnet/dotnet")]
    public void CreateCandidates_DotnetHostContext_LaunchesEntryAssemblyThroughCurrentHost(string processPath)
    {
        var context = new AppInstanceLaunchContext(
            IsWindows: false,
            ProcessPath: processPath,
            EntryAssemblyPath: @"C:\Users\avazb\RiderProjects\DevProjex\Apps\Avalonia\bin\Debug\net10.0\DevProjex.dll",
            AppHostPath: null,
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
    public void CreateCandidates_WindowsDotnetHost_PrefersGuiAppHostAndKeepsNoWindowFallback()
    {
        var context = new AppInstanceLaunchContext(
            IsWindows: true,
            ProcessPath: @"C:\Program Files\dotnet\dotnet.exe",
            EntryAssemblyPath: @"C:\DevProjex\DevProjex.dll",
            AppHostPath: @"C:\DevProjex\DevProjex.exe",
            WorkingDirectory: @"C:\DevProjex",
            WindowsPackageFamilyName: null);

        var candidates = AppInstanceStartInfoFactory.CreateCandidates(context);

        Assert.Equal(2, candidates.Count);
        var appHost = candidates[0];
        Assert.Equal(context.AppHostPath, appHost.FileName);
        Assert.False(appHost.UseShellExecute);
        Assert.True(appHost.CreateNoWindow);
        Assert.Empty(appHost.ArgumentList);

        var dotnetFallback = candidates[1];
        Assert.Equal(context.ProcessPath, dotnetFallback.FileName);
        Assert.False(dotnetFallback.UseShellExecute);
        Assert.True(dotnetFallback.CreateNoWindow);
        Assert.Equal(
            context.EntryAssemblyPath,
            Assert.Single(dotnetFallback.ArgumentList));
    }

    [Fact]
    public void CreateCandidates_PackagedWindowsDotnetHostContext_PrefersAppsFolderAndKeepsDotnetFallback()
    {
        var context = new AppInstanceLaunchContext(
            IsWindows: true,
            ProcessPath: @"C:\Program Files\dotnet\dotnet.exe",
            EntryAssemblyPath: @"C:\Program Files\WindowsApps\DevProjex\DevProjex.dll",
            AppHostPath: null,
            WorkingDirectory: @"C:\Program Files\WindowsApps\DevProjex",
            WindowsPackageFamilyName: "StarkIndustriesDev.DevProjex_84v5br12cncq6");

        var candidates = AppInstanceStartInfoFactory.CreateCandidates(context);

        Assert.Equal(2, candidates.Count);
        Assert.Equal("explorer.exe", candidates[0].FileName);
        Assert.Equal(@"shell:AppsFolder\StarkIndustriesDev.DevProjex_84v5br12cncq6!App", candidates[0].Arguments);
        Assert.True(candidates[0].UseShellExecute);
        Assert.Equal(context.ProcessPath, candidates[1].FileName);
        Assert.False(candidates[1].UseShellExecute);
        Assert.Equal(context.EntryAssemblyPath, Assert.Single(candidates[1].ArgumentList));
    }

    [Fact]
    public void CreateCandidates_DotnetHostWithoutEntryAssembly_DoesNotLaunchBareDotnet()
    {
        var context = new AppInstanceLaunchContext(
            IsWindows: false,
            ProcessPath: "/usr/share/dotnet/dotnet",
            EntryAssemblyPath: null,
            AppHostPath: null,
            WorkingDirectory: "/opt/devprojex",
            WindowsPackageFamilyName: null);

        var candidates = AppInstanceStartInfoFactory.CreateCandidates(context);

        Assert.Empty(candidates);
    }

    [Fact]
    public void CreateCandidates_PackagedWindowsDotnetHostWithoutEntryAssembly_UsesOnlyAppsFolderActivation()
    {
        var context = new AppInstanceLaunchContext(
            IsWindows: true,
            ProcessPath: @"C:\Program Files\dotnet\dotnet.exe",
            EntryAssemblyPath: null,
            AppHostPath: null,
            WorkingDirectory: @"C:\Program Files\WindowsApps\DevProjex",
            WindowsPackageFamilyName: "StarkIndustriesDev.DevProjex_84v5br12cncq6");

        var candidate = Assert.Single(AppInstanceStartInfoFactory.CreateCandidates(context));

        Assert.Equal("explorer.exe", candidate.FileName);
        Assert.Equal(@"shell:AppsFolder\StarkIndustriesDev.DevProjex_84v5br12cncq6!App", candidate.Arguments);
        Assert.True(candidate.UseShellExecute);
    }

    [Fact]
    public void CreateCandidates_AppHostContext_UsesCurrentProcessPathWithoutArguments()
    {
        var context = new AppInstanceLaunchContext(
            IsWindows: false,
            ProcessPath: "/usr/local/bin/devprojex",
            EntryAssemblyPath: "/usr/local/bin/DevProjex.dll",
            AppHostPath: null,
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
            AppHostPath: null,
            WorkingDirectory: AppContext.BaseDirectory,
            WindowsPackageFamilyName: null);

        var candidates = AppInstanceStartInfoFactory.CreateCandidates(context);

        Assert.Empty(candidates);
    }
}
