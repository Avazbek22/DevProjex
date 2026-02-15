using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DevProjex.Application.Services;
using DevProjex.Kernel.Contracts;
using DevProjex.Kernel.Models;
using Xunit;

namespace DevProjex.Tests.Unit;

public sealed class TreeExportServiceJsonCompactTests
{
	[Fact]
	public void BuildFullTree_JsonCompactSchema_UsesDirsAndFilesWithoutLegacyFields()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();

		var result = service.BuildFullTree(fixture.RootPath, fixture.Root, TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(result);
		var root = doc.RootElement.GetProperty("root");
		Assert.True(root.TryGetProperty("dirs", out _));
		Assert.True(root.TryGetProperty("files", out _));
		Assert.False(root.TryGetProperty("fullPath", out _));
		Assert.False(root.TryGetProperty("isDirectory", out _));
		Assert.False(root.TryGetProperty("children", out _));
	}

	[Fact]
	public void BuildFullTree_JsonCompactSchema_OmitsEmptyCollections()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();

		var result = service.BuildFullTree(fixture.RootPath, fixture.Root, TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(result);
		var emptyDir = FindDirByName(doc.RootElement.GetProperty("root"), "empty");
		Assert.False(emptyDir.TryGetProperty("dirs", out _));
		Assert.False(emptyDir.TryGetProperty("files", out _));
	}

	[Fact]
	public void BuildFullTree_JsonCompactSchema_IncludesAccessDeniedOnlyWhenTrue()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();

		var result = service.BuildFullTree(fixture.RootPath, fixture.Root, TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(result);
		var root = doc.RootElement.GetProperty("root");
		var deniedDir = FindDirByName(root, "denied");
		var srcDir = FindDirByName(root, "src");

		Assert.True(deniedDir.TryGetProperty("accessDenied", out var deniedValue));
		Assert.True(deniedValue.GetBoolean());
		Assert.False(srcDir.TryGetProperty("accessDenied", out _));
	}

	[Fact]
	public void BuildFullTree_JsonCompactSchema_UsesRelativePaths()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();

		var result = service.BuildFullTree(fixture.RootPath, fixture.Root, TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(result);
		var root = doc.RootElement.GetProperty("root");
		var srcDir = FindDirByName(root, "src");
		var innerDir = FindDirByName(srcDir, "inner");

		Assert.Equal(".", root.GetProperty("path").GetString());
		Assert.Equal("src", srcDir.GetProperty("path").GetString());
		Assert.Equal("src/inner", innerDir.GetProperty("path").GetString());
	}

	[Fact]
	public void BuildSelectedTree_JsonCompactSchema_FileSelectionKeepsAncestorsAndFileOnly()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();
		var selected = new HashSet<string> { fixture.InnerFilePath };

		var result = service.BuildSelectedTree(fixture.RootPath, fixture.Root, selected, TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(result);
		var root = doc.RootElement.GetProperty("root");
		var srcDir = FindDirByName(root, "src");
		var innerDir = FindDirByName(srcDir, "inner");

		Assert.False(root.TryGetProperty("files", out _));
		Assert.Equal("c.cs", innerDir.GetProperty("files")[0].GetString());
		Assert.Equal(1, innerDir.GetProperty("files").GetArrayLength());
	}

	[Fact]
	public void BuildSelectedTree_JsonCompactSchema_DirectorySelectionDoesNotForceAllDescendants()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();
		var selected = new HashSet<string> { fixture.SrcDirPath };

		var result = service.BuildSelectedTree(fixture.RootPath, fixture.Root, selected, TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(result);
		var root = doc.RootElement.GetProperty("root");
		var srcDir = FindDirByName(root, "src");

		Assert.False(srcDir.TryGetProperty("dirs", out _));
		Assert.False(srcDir.TryGetProperty("files", out _));
	}

	[Fact]
	public void BuildSelectedTree_JsonCompactSchema_RootSelectionReturnsRootNodeOnly()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();
		var selected = new HashSet<string> { fixture.RootPath };

		var result = service.BuildSelectedTree(fixture.RootPath, fixture.Root, selected, TreeTextFormat.Json);

		using var doc = JsonDocument.Parse(result);
		var root = doc.RootElement.GetProperty("root");
		Assert.Equal(".", root.GetProperty("path").GetString());
		Assert.False(root.TryGetProperty("dirs", out _));
		Assert.False(root.TryGetProperty("files", out _));
	}

	[Fact]
	public void BuildSelectedTree_JsonCompactSchema_NoSelectionReturnsEmptyString()
	{
		var fixture = CreateFixture();
		var service = new TreeExportService();

		var result = service.BuildSelectedTree(
			fixture.RootPath,
			fixture.Root,
			new HashSet<string>(),
			TreeTextFormat.Json);

		Assert.Equal(string.Empty, result);
	}

	private static JsonElement FindDirByName(JsonElement node, string name)
	{
		if (!node.TryGetProperty("dirs", out var dirs))
			throw new Xunit.Sdk.XunitException($"dirs not found when searching for '{name}'.");

		foreach (var candidate in dirs.EnumerateArray())
		{
			if (string.Equals(candidate.GetProperty("name").GetString(), name, System.StringComparison.Ordinal))
				return candidate;
		}

		throw new Xunit.Sdk.XunitException($"Directory '{name}' not found.");
	}

	private static (
		string RootPath,
		TreeNodeDescriptor Root,
		string SrcDirPath,
		string InnerFilePath
	) CreateFixture()
	{
		var rootPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DevProjexJsonCompactFixture"));
		var srcPath = Path.Combine(rootPath, "src");
		var innerPath = Path.Combine(srcPath, "inner");
		var innerFilePath = Path.Combine(innerPath, "c.cs");

		var innerDir = new TreeNodeDescriptor(
			"inner",
			innerPath,
			true,
			false,
			"folder",
			new List<TreeNodeDescriptor>
			{
				new("c.cs", innerFilePath, false, false, "csharp", new List<TreeNodeDescriptor>())
			});

		var srcDir = new TreeNodeDescriptor(
			"src",
			srcPath,
			true,
			false,
			"folder",
			new List<TreeNodeDescriptor>
			{
				new("a.cs", Path.Combine(srcPath, "a.cs"), false, false, "csharp", new List<TreeNodeDescriptor>()),
				new("b.cs", Path.Combine(srcPath, "b.cs"), false, false, "csharp", new List<TreeNodeDescriptor>()),
				innerDir
			});

		var deniedDir = new TreeNodeDescriptor(
			"denied",
			Path.Combine(rootPath, "denied"),
			true,
			true,
			"folder",
			new List<TreeNodeDescriptor>());

		var emptyDir = new TreeNodeDescriptor(
			"empty",
			Path.Combine(rootPath, "empty"),
			true,
			false,
			"folder",
			new List<TreeNodeDescriptor>());

		var root = new TreeNodeDescriptor(
			"DevProjex",
			rootPath,
			true,
			false,
			"folder",
			new List<TreeNodeDescriptor>
			{
				srcDir,
				deniedDir,
				emptyDir,
				new("README.md", Path.Combine(rootPath, "README.md"), false, false, "text", new List<TreeNodeDescriptor>())
			});

		return (rootPath, root, srcPath, innerFilePath);
	}
}
