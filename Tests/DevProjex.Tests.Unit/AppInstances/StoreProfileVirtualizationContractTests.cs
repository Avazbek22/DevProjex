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

	[Fact]
	public void ProjectAndManifestAgreeOnMinimumPlatformVersion()
	{
		var requiredMinimumVersion = new Version(10, 0, 18362, 0);
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
		var projectMinVersion = Version.Parse(projectMinVersionText!);

		var manifestDocument = XDocument.Load(ResolveStoreManifestPath());
		var manifestTarget = Assert.Single(manifestDocument.Descendants(foundation + "TargetDeviceFamily"), element =>
			element.Attribute("Name")?.Value == "Windows.Desktop");
		var manifestMinVersion = Version.Parse(manifestTarget.Attribute("MinVersion")!.Value);

		Assert.Equal(manifestMinVersion, projectMinVersion);
		Assert.True(
			projectMinVersion >= requiredMinimumVersion,
			"Store project TargetPlatformMinVersion must satisfy the desktop6:FileSystemWriteVirtualization " +
			"requirement (Windows 10 version 1903 / build 18362 or newer).");
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
