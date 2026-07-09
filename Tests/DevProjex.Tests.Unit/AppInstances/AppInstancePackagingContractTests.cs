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
    public void AvaloniaProject_KeepsTmdsDbusProtocolReferenceForLinuxDesktopRuntime()
    {
        var repositoryRoot = ResolveRepositoryRoot();
        var avaloniaProjectPath = Path.Combine(
            repositoryRoot,
            "Apps",
            "Avalonia",
            "DevProjex.Avalonia",
            "DevProjex.Avalonia.csproj");
        var centralPackagesPath = Path.Combine(repositoryRoot, "Directory.Packages.props");

        var avaloniaProject = XDocument.Load(avaloniaProjectPath);
        var centralPackages = XDocument.Load(centralPackagesPath);

        var avaloniaPackageReferences = avaloniaProject
            .Descendants("PackageReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        var dbusVersion = centralPackages
            .Descendants("PackageVersion")
            .SingleOrDefault(element => element.Attribute("Include")?.Value == "Tmds.DBus.Protocol")
            ?.Attribute("Version")
            ?.Value;

        Assert.Contains("Tmds.DBus.Protocol", avaloniaPackageReferences);
        Assert.False(string.IsNullOrWhiteSpace(dbusVersion));
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
        Assert.Contains($"Exec={CommandLineExecutableAliases.UnixCommand} %F", desktopEntry, StringComparison.Ordinal);
        Assert.Contains($"Icon={CommandLineExecutableAliases.UnixCommand}", desktopEntry, StringComparison.Ordinal);
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
