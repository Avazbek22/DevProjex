using DevProjex.Infrastructure.SmartIgnore;
using DevProjex.Tests.Integration.Helpers;

namespace DevProjex.Tests.Integration;

public sealed class SmartIgnoreRulesTests
{
	// Verifies the common rule includes standard system files.
	// IDE/VCS folders are now controlled via DotFolders filter.
	[Fact]
	public void CommonSmartIgnoreRule_ReturnsKnownEntries()
	{
		var rule = new CommonSmartIgnoreRule();
		var result = rule.Evaluate("/root");

		// CommonSmartIgnore no longer includes IDE/VCS folders - they're controlled via DotFolders filter
		Assert.Empty(result.FolderNames);

		// System files are still filtered
		Assert.Contains("thumbs.db", result.FileNames);
		Assert.Contains(".ds_store", result.FileNames);
		Assert.Contains("desktop.ini", result.FileNames);
	}

	// Verifies frontend ignore rule is empty when no marker files exist.
	[Fact]
	public void FrontendArtifactsIgnoreRule_ReturnsEmptyWhenNoMarkers()
	{
		using var temp = new TemporaryDirectory();
		var rule = new FrontendArtifactsIgnoreRule();

		var result = rule.Evaluate(temp.Path);

		Assert.Empty(result.FolderNames);
	}

	// Verifies frontend ignore rule activates when marker files exist.
	[Fact]
	public void FrontendArtifactsIgnoreRule_ReturnsFoldersWhenMarkerPresent()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("package.json", "{}");

		var rule = new FrontendArtifactsIgnoreRule();
		var result = rule.Evaluate(temp.Path);

		Assert.Contains("dist", result.FolderNames);
		Assert.Contains("build", result.FolderNames);
	}

	// Verifies frontend ignore rule returns empty sets when the root does not exist.
	[Fact]
	public void FrontendArtifactsIgnoreRule_ReturnsEmptyWhenRootMissing()
	{
		var rule = new FrontendArtifactsIgnoreRule();

		var result = rule.Evaluate("/path/does/not/exist");

		Assert.Empty(result.FolderNames);
		Assert.Empty(result.FileNames);
	}

	// Verifies common smart-ignore file set is case-insensitive.
	[Fact]
	public void CommonSmartIgnoreRule_UsesCaseInsensitiveFiles()
	{
		var rule = new CommonSmartIgnoreRule();
		var result = rule.Evaluate("/root");

		// Folders are now empty, test files instead
		Assert.Contains("THUMBS.DB", result.FileNames);
		Assert.Contains(".DS_STORE", result.FileNames);
	}
}
