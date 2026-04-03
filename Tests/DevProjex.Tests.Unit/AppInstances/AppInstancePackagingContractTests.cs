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

        Assert.Equal("App", applicationElement.Attribute("Id")?.Value);
    }

    private static string ResolveStoreManifestPath()
    {
        var currentDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        while (currentDirectory is not null)
        {
            var candidate = Path.Combine(
                currentDirectory.FullName,
                "Packaging",
                "Windows",
                "DevProjex.Store",
                "Package.appxmanifest");
            if (File.Exists(candidate))
                return candidate;

            currentDirectory = currentDirectory.Parent;
        }

        return Path.Combine(
            AppContext.BaseDirectory,
            "Packaging",
            "Windows",
            "DevProjex.Store",
            "Package.appxmanifest");
    }
}
