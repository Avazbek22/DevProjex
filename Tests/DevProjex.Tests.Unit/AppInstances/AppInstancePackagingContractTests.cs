using System.Xml.Linq;

namespace DevProjex.Tests.Unit.AppInstances;

public sealed class AppInstancePackagingContractTests
{
    [Fact]
    public void WindowsStoreManifest_UsesExpectedApplicationId_ForAppsFolderActivation()
    {
        var manifestPath = ResolveStoreManifestPath();

        Assert.True(File.Exists(manifestPath), $"Store manifest was not found: {manifestPath}");

        var document = XDocument.Load(manifestPath);
        var packageNamespace = XNamespace.Get("http://schemas.microsoft.com/appx/manifest/foundation/windows10");
        var applicationElement = document
            .Descendants(packageNamespace + "Application")
            .Single();

        Assert.Equal(CommandLineExecutableAliases.WindowsStoreApplicationId, applicationElement.Attribute("Id")?.Value);
    }

    [Fact]
    public void WindowsStoreManifest_DeclaresCommandLineExecutionAliasOnUiApplication()
    {
        var manifestPath = ResolveStoreManifestPath();
        var document = XDocument.Load(manifestPath);
        var packageNamespace = XNamespace.Get("http://schemas.microsoft.com/appx/manifest/foundation/windows10");
        var uap3Namespace = XNamespace.Get("http://schemas.microsoft.com/appx/manifest/uap/windows10/3");
        var desktopNamespace = XNamespace.Get("http://schemas.microsoft.com/appx/manifest/desktop/windows10");
        var applicationElement = document
            .Descendants(packageNamespace + "Application")
            .Single();

        Assert.Equal(CommandLineExecutableAliases.WindowsStoreApplicationId, applicationElement.Attribute("Id")?.Value);
        Assert.DoesNotContain("Cli", applicationElement.Attribute("Executable")?.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var extension = applicationElement
            .Element(packageNamespace + "Extensions")
            ?.Elements(uap3Namespace + "Extension")
            .SingleOrDefault(element => element.Attribute("Category")?.Value == "windows.appExecutionAlias");

        Assert.NotNull(extension);
        Assert.Equal(CommandLineExecutableAliases.WindowsStoreUiPackageExecutable, extension.Attribute("Executable")?.Value);
        Assert.Equal("Windows.FullTrustApplication", extension.Attribute("EntryPoint")?.Value);

        var executionAlias = extension
            .Element(uap3Namespace + "AppExecutionAlias")
            ?.Element(desktopNamespace + "ExecutionAlias");

        Assert.NotNull(executionAlias);
        Assert.Equal(CommandLineExecutableAliases.WindowsStoreAlias, executionAlias.Attribute("Alias")?.Value);
        Assert.EndsWith(".exe", executionAlias.Attribute("Alias")?.Value, StringComparison.Ordinal);
        Assert.Equal(
            executionAlias.Attribute("Alias")?.Value,
            executionAlias.Attribute("Alias")?.Value?.ToLowerInvariant());
    }

    [Fact]
    public void WindowsStoreManifest_KeepsDesktopAliasNamespacesIgnorableForDownlevelTooling()
    {
        var manifestPath = ResolveStoreManifestPath();
        var document = XDocument.Load(manifestPath);
        var root = document.Root ?? throw new InvalidOperationException("Package manifest has no root element.");

        Assert.Equal(
            "http://schemas.microsoft.com/appx/manifest/uap/windows10/3",
            root.GetNamespaceOfPrefix("uap3")?.NamespaceName);
        Assert.Equal(
            "http://schemas.microsoft.com/appx/manifest/desktop/windows10",
            root.GetNamespaceOfPrefix("desktop")?.NamespaceName);

        var ignorableNamespaces = root.Attribute("IgnorableNamespaces")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];

        Assert.Contains("uap3", ignorableNamespaces);
        Assert.Contains("desktop", ignorableNamespaces);
    }

    [Fact]
    public void WindowsStoreManifest_DoesNotDeclareSeparateCliApplication()
    {
        var manifestPath = ResolveStoreManifestPath();
        var document = XDocument.Load(manifestPath);
        var packageNamespace = XNamespace.Get("http://schemas.microsoft.com/appx/manifest/foundation/windows10");
        var applications = document
            .Descendants(packageNamespace + "Application")
            .ToArray();

        Assert.Single(applications);
        Assert.DoesNotContain(
            applications,
            application =>
                (application.Attribute("Id")?.Value.Contains("Cli", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (application.Attribute("Executable")?.Value.Contains("Cli", StringComparison.OrdinalIgnoreCase) ?? false));
    }

    [Fact]
    public void Repository_DoesNotContainSeparateCliProjectForCommandLineStartup()
    {
        var repositoryRoot = ResolveRepositoryRoot();
        var cliProjectCandidates = Directory
            .EnumerateFiles(repositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileNameWithoutExtension(path).Contains("DevProjex.Cli", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(cliProjectCandidates);
    }

    [Fact]
    public void Repository_DoesNotDirectlyOverrideTmdsDbusProtocolAnywhere()
    {
        var repositoryRoot = ResolveRepositoryRoot();
        var packageFiles = Directory
            .EnumerateFiles(repositoryRoot, "*.*", SearchOption.AllDirectories)
            .Where(static path =>
                path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
            .Where(static path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var directOverrides = packageFiles
            .SelectMany(packageFile =>
            {
                var document = XDocument.Load(packageFile);
                return document
                    .Descendants()
                    .Where(static element =>
                        element.Name.LocalName is "PackageReference" or "PackageVersion" &&
                        element.Attribute("Include")?.Value == "Tmds.DBus.Protocol")
                    .Select(_ => Path.GetRelativePath(repositoryRoot, packageFile));
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Empty(directOverrides);
    }

    [Fact]
    public void AvaloniaProject_DoesNotOverrideAvaloniaFreeDesktopTmdsDbusProtocolVersion()
    {
        var repositoryRoot = ResolveRepositoryRoot();
        var avaloniaProjectPath = Path.Combine(
            repositoryRoot,
            "Apps",
            "Avalonia",
            "DevProjex.Avalonia.csproj");
        var centralPackagesPath = Path.Combine(repositoryRoot, "Directory.Packages.props");

        var avaloniaProject = XDocument.Load(avaloniaProjectPath);
        var centralPackages = XDocument.Load(centralPackagesPath);

        var directTmdsReferences = avaloniaProject
            .Descendants("PackageReference")
            .Where(element => element.Attribute("Include")?.Value == "Tmds.DBus.Protocol")
            .ToArray();
        var centralTmdsVersions = centralPackages
            .Descendants("PackageVersion")
            .Where(element => element.Attribute("Include")?.Value == "Tmds.DBus.Protocol")
            .ToArray();

        // Avalonia.FreeDesktop owns the DBus compatibility boundary. A direct Tmds
        // override can recreate the Linux/X11 startup TypeLoadException we saw on Arch.
        Assert.Empty(directTmdsReferences);
        Assert.Empty(centralTmdsVersions);
    }

    [Fact]
    public void AvaloniaRestoreGraph_UsesTmdsDbusProtocolCompatibleWithAvaloniaFreeDesktop()
    {
        var repositoryRoot = ResolveRepositoryRoot();
        var assetsPath = Path.Combine(
            repositoryRoot,
            "Apps",
            "Avalonia",
            "obj",
            "project.assets.json");

        Assert.True(
            File.Exists(assetsPath),
            $"Project assets were not found. Run dotnet restore before this packaging contract test: {assetsPath}");

        using var document = JsonDocument.Parse(File.ReadAllText(assetsPath));
        var libraries = document.RootElement.GetProperty("libraries");
        var resolvedAvaloniaFreeDesktopVersions = libraries
            .EnumerateObject()
            .Where(static property => property.Name.StartsWith("Avalonia.FreeDesktop/", StringComparison.Ordinal))
            .Select(static property => property.Name["Avalonia.FreeDesktop/".Length..])
            .ToArray();
        var resolvedTmdsVersions = libraries
            .EnumerateObject()
            .Where(static property => property.Name.StartsWith("Tmds.DBus.Protocol/", StringComparison.Ordinal))
            .Select(static property => property.Name["Tmds.DBus.Protocol/".Length..])
            .ToArray();

        var resolvedAvaloniaFreeDesktopVersion = Assert.Single(resolvedAvaloniaFreeDesktopVersions);
        var resolvedVersion = Assert.Single(resolvedTmdsVersions);
        Assert.True(
            Version.Parse(resolvedAvaloniaFreeDesktopVersion) >= new Version(12, 1, 0),
            $"Avalonia.FreeDesktop 12.1.0+ is required for the updated DBus API boundary. Resolved: {resolvedAvaloniaFreeDesktopVersion}");
        Assert.True(
            Version.Parse(resolvedVersion) >= new Version(0, 94, 0),
            $"Avalonia.FreeDesktop 12.1.0+ should resolve the updated Tmds.DBus.Protocol graph. Resolved: {resolvedVersion}");
    }

    [Fact]
    public void DirectoryBuildProps_DoesNotDisableReferenceAssembliesForCiBuilds()
    {
        var repositoryRoot = ResolveRepositoryRoot();
        var propsPath = Path.Combine(repositoryRoot, "Directory.Build.props");
        var document = XDocument.Load(propsPath);

        var ciReferenceAssemblyDisables = document
            .Descendants()
            .Where(static element => element.Name.LocalName == "ProduceReferenceAssembly")
            .Where(static element => string.Equals(element.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase))
            .Where(static element => (element.Attribute("Condition")?.Value ?? string.Empty)
                .Contains("CI", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Project-reference builds must keep SDK reference assemblies enabled. Disabling
        // them makes downstream projects compile against bin output and can race on macOS CI.
        Assert.Empty(ciReferenceAssemblyDisables);
    }

    [Fact]
    public void ReleaseValidationWorkflow_CatchesLinuxX11DbusStartupRegressions()
    {
        var repositoryRoot = ResolveRepositoryRoot();
        var workflowPath = Path.Combine(repositoryRoot, ".github", "workflows", "release-validate.yml");
        var workflow = File.ReadAllText(workflowPath);

        Assert.Contains("linux-x64", workflow, StringComparison.Ordinal);
        Assert.Contains("linux-arm64", workflow, StringComparison.Ordinal);
        Assert.Contains("Validate Linux DBus Dependency Graph", workflow, StringComparison.Ordinal);
        Assert.Contains("Tmds.DBus.Protocol/*", workflow, StringComparison.Ordinal);
        Assert.Contains("Do not override Tmds.DBus.Protocol directly", workflow, StringComparison.Ordinal);
        Assert.Contains("Avalonia.FreeDesktop/*", workflow, StringComparison.Ordinal);
        Assert.Contains("[Version]\"12.1.0\"", workflow, StringComparison.Ordinal);
        Assert.Contains("[Version]\"0.94.0\"", workflow, StringComparison.Ordinal);
        Assert.Contains("Startup Smoke (Linux X11)", workflow, StringComparison.Ordinal);
        Assert.Contains("xvfb-run -a", workflow, StringComparison.Ordinal);
        Assert.Contains("env -u CI \"$2\"", workflow, StringComparison.Ordinal);
        Assert.Contains("Portable Launcher ConPTY TUI Smoke", workflow, StringComparison.Ordinal);
        Assert.Contains("DEVPROJEX_TUI_TEST_BINARY", workflow, StringComparison.Ordinal);
        Assert.Contains("/p:PublishSingleFile=true", workflow, StringComparison.Ordinal);
        Assert.Contains("/p:IncludeNativeLibrariesForSelfExtract=true", workflow, StringComparison.Ordinal);
        Assert.Contains("/p:PublishTrimmed=false", workflow, StringComparison.Ordinal);
        Assert.Contains("Directory.Packages.props", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseScript_PublishesLinuxArtifactsWithAvaloniaSafeSingleFileSettings()
    {
        var repositoryRoot = ResolveRepositoryRoot();
        var releaseScriptPath = Path.Combine(repositoryRoot, "Scripts", "release-all.ps1");
        var releaseScript = File.ReadAllText(releaseScriptPath);

        Assert.Contains("Rid = \"linux-x64\"", releaseScript, StringComparison.Ordinal);
        Assert.Contains("Rid = \"linux-arm64\"", releaseScript, StringComparison.Ordinal);
        Assert.Contains("DevProjex.v$version.linux-x64.tar.gz", releaseScript, StringComparison.Ordinal);
        Assert.Contains("DevProjex.v$version.linux-arm64.tar.gz", releaseScript, StringComparison.Ordinal);
        Assert.Contains("DevProjex.v$version.osx-x64.app.tar.gz", releaseScript, StringComparison.Ordinal);
        Assert.Contains("DevProjex.v$version.osx-arm64.app.tar.gz", releaseScript, StringComparison.Ordinal);
        Assert.Contains("New-UstarGzipArchive", releaseScript, StringComparison.Ordinal);
        Assert.Contains("Read-UstarGzipArchive", releaseScript, StringComparison.Ordinal);
        Assert.Contains("-GitHubOnly:$GitHubArtifactsOnly", releaseScript, StringComparison.Ordinal);
        Assert.Contains("(Join-Path $sourceRoot \"artifacts\")", releaseScript, StringComparison.Ordinal);
        Assert.Contains("(Join-Path $sourceRoot \".artifacts\")", releaseScript, StringComparison.Ordinal);
        Assert.Contains("\"bin\"", releaseScript, StringComparison.Ordinal);
        Assert.Contains("\"obj\"", releaseScript, StringComparison.Ordinal);
        Assert.Contains("Assert-IsolatedWorkspaceCapacity -sourceRoot $sourceRoot", releaseScript, StringComparison.Ordinal);
        Assert.Contains("$sourceBytes * 2", releaseScript, StringComparison.Ordinal);
        Assert.Contains("$isolatedPackages + [System.IO.Path]::DirectorySeparatorChar", releaseScript, StringComparison.Ordinal);
        Assert.Contains("Resolve-IsolatedWorkspaceCleanupTarget", releaseScript, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $cleanupTarget -Recurse", releaseScript, StringComparison.Ordinal);
        Assert.DoesNotContain("cmd /c", releaseScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("linux-x64.portable", releaseScript, StringComparison.Ordinal);
        Assert.DoesNotContain("linux-arm64.portable", releaseScript, StringComparison.Ordinal);
        Assert.Contains("\"/p:PublishSingleFile=true\"", releaseScript, StringComparison.Ordinal);
        Assert.Contains("\"/p:IncludeNativeLibrariesForSelfExtract=true\"", releaseScript, StringComparison.Ordinal);
        Assert.Contains("\"/p:PublishReadyToRun=true\"", releaseScript, StringComparison.Ordinal);
        Assert.Contains("\"/p:PublishTrimmed=false\"", releaseScript, StringComparison.Ordinal);
        Assert.Contains(
            "Get-ChildItem -LiteralPath $ridOutDir -File -Recurse",
            releaseScript,
            StringComparison.Ordinal);
        Assert.Contains("$publishedFiles.Count -ne 1", releaseScript, StringComparison.Ordinal);
        Assert.Contains(
            "Get-RelativePublishedPath -basePath $ridOutDir -publishedPath $_.FullName",
            releaseScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "Build-GitHubArtifactsInWorkspace -version $resolvedVersion -configuration \"Release\" -storePackageVersion $storePackageVersion",
            releaseScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\"/p:PublishTrimmed=true\"", releaseScript, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsStoreManifest_TargetsWindowsVersionSupportingExecutionAliases()
    {
        var manifestPath = ResolveStoreManifestPath();
        var document = XDocument.Load(manifestPath);
        var packageNamespace = XNamespace.Get("http://schemas.microsoft.com/appx/manifest/foundation/windows10");
        var targetDeviceFamily = document
            .Descendants(packageNamespace + "TargetDeviceFamily")
            .Single();

        var minVersion = Version.Parse(targetDeviceFamily.Attribute("MinVersion")?.Value ?? "0.0.0.0");

        Assert.True(
            minVersion >= new Version(10, 0, 16299, 0),
            "uap5 AppExecutionAlias requires Windows 10 version 1709 / build 16299 or newer.");
    }

    [Fact]
    public void LinuxPackaging_DocumentsDevprojexPathCommandAndDesktopEntryUsesIt()
    {
        var repositoryRoot = ResolveRepositoryRoot();
        var readmePath = Path.Combine(repositoryRoot, "Packaging", "Linux", "README.md");
        var desktopEntryPath = Path.Combine(repositoryRoot, "Packaging", "Linux", "devprojex.desktop");

        var readme = File.ReadAllText(readmePath);
        var desktopEntry = File.ReadAllText(desktopEntryPath);

        Assert.Contains($"/usr/local/bin/{CommandLineExecutableAliases.UnixCommand}", readme, StringComparison.Ordinal);
        Assert.Contains($"~/.local/bin/{CommandLineExecutableAliases.UnixCommand}", readme, StringComparison.Ordinal);
        Assert.Contains($"Exec={CommandLineExecutableAliases.UnixCommand} open %f", desktopEntry, StringComparison.Ordinal);
        Assert.Contains($"Icon={CommandLineExecutableAliases.UnixCommand}", desktopEntry, StringComparison.Ordinal);
        Assert.Contains("always open DevProjex Desktop", readme, StringComparison.Ordinal);
        Assert.DoesNotContain(CommandLineExecutableAliases.WindowsPortableExecutable, readme, StringComparison.Ordinal);
        Assert.DoesNotContain(CommandLineExecutableAliases.WindowsStoreAlias, desktopEntry, StringComparison.Ordinal);
    }

    [Fact]
    public void MacOsPackaging_DocumentsDevprojexTerminalAliasWithoutSelfModifyingPath()
    {
        var repositoryRoot = ResolveRepositoryRoot();
        var readmePath = Path.Combine(repositoryRoot, "Packaging", "MacOS", "README.md");

        var readme = File.ReadAllText(readmePath);

        Assert.Contains($"~/.local/bin/{CommandLineExecutableAliases.UnixCommand}", readme, StringComparison.Ordinal);
        Assert.Contains($"/Applications/{CommandLineExecutableAliases.DisplayName}.app/Contents/MacOS/{CommandLineExecutableAliases.DisplayName}", readme, StringComparison.Ordinal);
        Assert.Contains("DEVPROJEX_TERMINAL_HOST=1", readme, StringComparison.Ordinal);
        Assert.Contains("<string>14.0</string>", readme, StringComparison.Ordinal);
        Assert.Contains("DevProjex.v<version>.osx-<architecture>.app.tar.gz", readme, StringComparison.Ordinal);
        Assert.Contains("generates `app.icns` deterministically", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("generate-app-icns.sh", readme, StringComparison.Ordinal);
        Assert.Contains("does not modify shell profiles or global environment variables", readme, StringComparison.Ordinal);
        Assert.DoesNotContain(CommandLineExecutableAliases.WindowsStoreAlias, readme, StringComparison.Ordinal);
    }

    private static string ResolveStoreManifestPath()
    {
        return Path.Combine(
            ResolveRepositoryRoot(),
            "Packaging",
            "Windows",
            "DevProjex.Store",
            "Package.appxmanifest");
    }

    private static string ResolveRepositoryRoot()
    {
        var currentDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        while (currentDirectory is not null)
        {
            if (File.Exists(Path.Combine(currentDirectory.FullName, "DevProjex.sln")) &&
                Directory.Exists(Path.Combine(currentDirectory.FullName, "Packaging")))
                return currentDirectory.FullName;

            currentDirectory = currentDirectory.Parent;
        }

        return AppContext.BaseDirectory;
    }
}
