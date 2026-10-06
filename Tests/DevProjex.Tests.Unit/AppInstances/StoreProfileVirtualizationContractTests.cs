using System.Xml.Linq;

namespace DevProjex.Tests.Unit.AppInstances;

public sealed class StoreProfileVirtualizationContractTests
{
	[Fact]
	public void ManifestKeepsDefaultAppDataVirtualization()
	{
		// Microsoft Store certification denied the unvirtualizedResources restricted capability
		// (policy 10.6.3), and every AppData or registry virtualization override requires it.
		var document = XDocument.Load(ResolveStoreManifestPath());
		var overrideNames = new[]
		{
			"FileSystemWriteVirtualization",
			"RegistryWriteVirtualization",
			"ExcludedDirectories",
			"ExcludedKeys"
		};

		Assert.DoesNotContain(document.Descendants(), element => overrideNames.Contains(element.Name.LocalName));
		var capabilities = Assert.Single(document.Root!.Elements(), element => element.Name.LocalName == "Capabilities");
		var capability = Assert.Single(capabilities.Elements());
		Assert.Equal("runFullTrust", capability.Attribute("Name")?.Value);
	}

	[Fact]
	public void ProjectAndManifestAgreeOnMinimumPlatformVersion()
	{
		var msbuild = XNamespace.Get("http://schemas.microsoft.com/developer/msbuild/2003");
		var foundation = XNamespace.Get(
			"http://schemas.microsoft.com/appx/manifest/foundation/windows10");

		var projectDocument = XDocument.Load(ResolveStoreProjectPath());
		var projectMinVersionText = projectDocument.Root!
			.Descendants(msbuild + "TargetPlatformMinVersion")
			.Select(element => element.Value)
			.SingleOrDefault();
		Assert.False(
			string.IsNullOrWhiteSpace(projectMinVersionText),
			"Store project must declare TargetPlatformMinVersion.");

		var manifestDocument = XDocument.Load(ResolveStoreManifestPath());
		var manifestTarget = Assert.Single(manifestDocument.Descendants(foundation + "TargetDeviceFamily"), element =>
			element.Attribute("Name")?.Value == "Windows.Desktop");

		Assert.Equal(
			Version.Parse(manifestTarget.Attribute("MinVersion")!.Value),
			Version.Parse(projectMinVersionText!));
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

	private static string ResolveStoreProjectPath()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null)
		{
			var candidate = Path.Combine(
				directory.FullName,
				"Packaging",
				"Windows",
				"DevProjex.Store",
				"DevProjex.Store.wapproj");
			if (File.Exists(candidate))
				return candidate;
			directory = directory.Parent;
		}

		throw new DirectoryNotFoundException("Could not locate the repository root.");
	}
}
