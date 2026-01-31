using System;
using System.Collections.Generic;
using System.IO;
using DevProjex.Infrastructure.SmartIgnore;
using DevProjex.Tests.Unit.Helpers;
using Xunit;

namespace DevProjex.Tests.Unit;

public sealed class SmartIgnoreRulesAdditionalTests
{
	// CommonSmartIgnoreRule no longer includes folders - all folders (.git, .vs, .idea, etc.)
	// are now controlled via DotFolders filter for predictable user control.
	// This test was removed as folders are no longer part of CommonSmartIgnoreRule.

	[Fact]
	public void CommonSmartIgnoreRule_DoesNotIncludeFolders()
	{
		var rule = new CommonSmartIgnoreRule();

		var result = rule.Evaluate("any");

		// CommonSmartIgnoreRule now returns empty folder set
		Assert.Empty(result.FolderNames);
	}

	[Theory]
	// Verifies common smart ignore rule includes expected file names.
	[InlineData(".ds_store")]
	[InlineData("thumbs.db")]
	[InlineData("desktop.ini")]
	public void CommonSmartIgnoreRule_IncludesDefaultFiles(string fileName)
	{
		var rule = new CommonSmartIgnoreRule();

		var result = rule.Evaluate("any");

		Assert.Contains(fileName, result.FileNames, StringComparer.OrdinalIgnoreCase);
	}

	[Theory]
	// Verifies frontend artifacts rule returns empty sets without marker files.
	[InlineData("readme.md")]
	[InlineData("package.txt")]
	[InlineData("yarn.json")]
	[InlineData("lockfile")]
	public void FrontendArtifactsIgnoreRule_NoMarkers_ReturnsEmpty(string fileName)
	{
		using var temp = new TemporaryDirectory();
		var rule = new FrontendArtifactsIgnoreRule();
		temp.CreateFile(fileName, "content");

		var result = rule.Evaluate(temp.Path);

		Assert.Empty(result.FolderNames);
		Assert.Empty(result.FileNames);
	}

	[Theory]
	// Verifies frontend artifacts rule activates when marker files are present.
	[InlineData("package.json")]
	[InlineData("package-lock.json")]
	[InlineData("pnpm-lock.yaml")]
	[InlineData("yarn.lock")]
	public void FrontendArtifactsIgnoreRule_WithMarker_IncludesFolders(string markerFile)
	{
		using var temp = new TemporaryDirectory();
		var rule = new FrontendArtifactsIgnoreRule();
		temp.CreateFile(markerFile, "content");

		var result = rule.Evaluate(temp.Path);

		Assert.Contains("node_modules", result.FolderNames, StringComparer.OrdinalIgnoreCase);
		Assert.Contains("dist", result.FolderNames, StringComparer.OrdinalIgnoreCase);
		Assert.Contains("build", result.FolderNames, StringComparer.OrdinalIgnoreCase);
		Assert.Contains(".next", result.FolderNames, StringComparer.OrdinalIgnoreCase);
		Assert.Contains(".nuxt", result.FolderNames, StringComparer.OrdinalIgnoreCase);
		Assert.Contains(".turbo", result.FolderNames, StringComparer.OrdinalIgnoreCase);
		Assert.Contains(".svelte-kit", result.FolderNames, StringComparer.OrdinalIgnoreCase);
	}
}
