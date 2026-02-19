namespace DevProjex.Tests.Integration;

public sealed class ResourceStoreTests
{
	// Verifies localization resources are loaded for English.
	[Fact]
	public void JsonLocalizationCatalog_LoadsResources()
	{
		var catalog = new JsonLocalizationCatalog();
		var dict = catalog.Get(AppLanguage.En);

		Assert.Contains("Settings.Ignore.UseGitIgnore", dict.Keys);
		Assert.Contains("Settings.Ignore.SmartIgnore", dict.Keys);
	}

	// Verifies embedded icon store returns bytes for known keys.
	[Fact]
	public void EmbeddedIconStore_ReturnsKnownIconBytes()
	{
		var store = new EmbeddedIconStore();

		Assert.Contains("folder", store.Keys);
		var bytes = store.GetIconBytes("folder");
		Assert.NotEmpty(bytes);
	}

	// Verifies requesting an unknown icon key throws.
	[Fact]
	public void EmbeddedIconStore_ThrowsForMissingKey()
	{
		var store = new EmbeddedIconStore();

		Assert.Throws<KeyNotFoundException>(() => store.GetIconBytes("missing"));
	}

	// Verifies icon mapping uses file extensions.
	[Fact]
	public void IconMapper_MapsKnownExtensions()
	{
		var mapper = new IconMapper();
		var node = new FileSystemNode("Program.cs", "/root/Program.cs", false, false, new List<FileSystemNode>());

		var key = mapper.GetIconKey(node);

		Assert.Equal("csharp", key);
	}

	// Verifies icon mapping marks gray folders correctly.
	[Fact]
	public void IconMapper_MapsGrayFolders()
	{
		var mapper = new IconMapper();
		var node = new FileSystemNode("bin", "/root/bin", true, false, new List<FileSystemNode>());

		var key = mapper.GetIconKey(node);

		Assert.Equal("grayFolder", key);
	}

	// Verifies unknown files map to the default unknown icon key.
	[Fact]
	public void IconMapper_ReturnsUnknownForUnmappedFile()
	{
		var mapper = new IconMapper();
		var node = new FileSystemNode("file.unknownext", "/root/file.unknownext", false, false, new List<FileSystemNode>());

		var key = mapper.GetIconKey(node);

		Assert.Equal("unknownFile", key);
	}
}
