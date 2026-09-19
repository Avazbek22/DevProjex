using System.Xml.Linq;

namespace DevProjex.Tests.Unit.AppInstances;

public sealed class StoreProfileVirtualizationContractTests
{
	[Fact]
	public void ManifestDisablesProfileStorageVirtualization()
	{
		var document = XDocument.Load(ResolveStoreManifestPath());
		var foundation = XNamespace.Get(
			"http://schemas.microsoft.com/appx/manifest/foundation/windows10");
		var desktop6 = XNamespace.Get(
			"http://schemas.microsoft.com/appx/manifest/desktop/windows10/6");
		var restricted = XNamespace.Get(
			"http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities");

		Assert.Equal(
			"disabled",
			Assert.Single(document.Descendants(desktop6 + "FileSystemWriteVirtualization")).Value);
		Assert.Single(document.Descendants(restricted + "Capability"), element =>
			element.Attribute("Name")?.Value == "unvirtualizedResources");
		var target = Assert.Single(document.Descendants(foundation + "TargetDeviceFamily"), element =>
			element.Attribute("Name")?.Value == "Windows.Desktop");
		Assert.True(
			Version.Parse(target.Attribute("MinVersion")!.Value) >= new Version(10, 0, 18362, 0));
		Assert.Contains(
			"desktop6",
			document.Root!.Attribute("IgnorableNamespaces")!.Value.Split(
				' ',
				StringSplitOptions.RemoveEmptyEntries));
	}

	private static string ResolveStoreManifestPath()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null)
		{
			var candidate = Path.Combine(
				directory.FullName,
				"Packaging",
				"Windows",
				"DevProjex.Store",
				"Package.appxmanifest");
			if (File.Exists(candidate))
				return candidate;
			directory = directory.Parent;
		}

		throw new DirectoryNotFoundException("Could not locate the repository root.");
	}
}
