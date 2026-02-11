using System;
using System.Collections.Generic;
using System.Linq;
using DevProjex.Infrastructure.FileSystem;
using DevProjex.Kernel.Models;
using DevProjex.Tests.Integration.Helpers;
using Xunit;

namespace DevProjex.Tests.Integration;

public sealed class TreeBuilderTests
{
	// Verifies root-folder filtering applies to directories while root files still match extensions.
	[Fact]
	public void Build_FiltersByRootFoldersAndExtensions()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/app.cs", "class A {}");
		temp.CreateFile("src/readme.md", "hello");
		temp.CreateFile("docs/info.txt", "doc");
		temp.CreateFile("root.txt", "root");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "docs" },
			IgnoreRules: new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()));

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		var children = result.Root.Children.Select(c => c.Name).ToList();
		Assert.Contains("docs", children);
		Assert.DoesNotContain("src", children);
		Assert.Contains("root.txt", children);

		var docs = result.Root.Children.First(c => c.Name == "docs");
		Assert.Single(docs.Children);
		Assert.Equal("info.txt", docs.Children[0].Name);
	}

	// Verifies directories are ordered before files in the root listing.
	[Fact]
	public void Build_OrdersDirectoriesBeforeFiles()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("a.txt", "a");
		temp.CreateFile("folder/b.txt", "b");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "folder" },
			IgnoreRules: new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()));

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.Equal("folder", result.Root.Children.First().Name);
		Assert.False(result.Root.Children.Last().IsDirectory);
	}

	// Verifies no files are included when no extensions are allowed.
	[Fact]
	public void Build_SkipsFilesWhenAllowedExtensionsEmpty()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("root.txt", "root");
		temp.CreateFile("src/app.cs", "class A {}");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(),
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src" },
			IgnoreRules: new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()));

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.DoesNotContain(result.Root.Children, child => !child.IsDirectory);
	}

	// Verifies dot folders are excluded when the ignore rule is set.
	[Fact]
	public void Build_RespectsDotFolderIgnoreRule()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".cache/hidden.txt", "hidden");
		temp.CreateFile("visible.txt", "visible");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			IgnoreRules: new IgnoreRules(false, false, true, false, new HashSet<string>(), new HashSet<string>()));

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.DoesNotContain(result.Root.Children, child => child.Name == ".cache");
	}

	// Verifies smart-ignored folders are excluded.
	[Fact]
	public void Build_RespectsSmartIgnoredFolders()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("ignored/skip.txt", "skip");
		temp.CreateFile("keep/ok.txt", "ok");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ignored", "keep" },
			IgnoreRules: new IgnoreRules(false, false, false, false,
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ignored" },
				new HashSet<string>()));

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.DoesNotContain(result.Root.Children, child => child.Name == "ignored");
		Assert.Contains(result.Root.Children, child => child.Name == "keep");
	}

	// Verifies allowed extensions matching is case-insensitive.
	[Fact]
	public void Build_RespectsAllowedExtensionsCaseInsensitive()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("note.txt", "note");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".TXT" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			IgnoreRules: new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()));

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.Contains(result.Root.Children, child => child.Name == "note.txt");
	}

	// Verifies name filter keeps matching root files and matching descendants only.
	[Fact]
	public void Build_NameFilter_FiltersFilesAndDirectoriesBySubstring()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("order.cs", "class Order {}");
		temp.CreateFile("other.txt", "other");
		temp.CreateFile("src/order.handler.cs", "class Handler {}");
		temp.CreateFile("src/note.md", "note");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".txt", ".md" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src" },
			IgnoreRules: new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()),
			NameFilter: "order");

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		var rootNames = result.Root.Children.Select(c => c.Name).ToList();
		Assert.Contains("order.cs", rootNames);
		Assert.Contains("src", rootNames);
		Assert.DoesNotContain("other.txt", rootNames);

		var src = result.Root.Children.First(c => c.Name == "src");
		Assert.Single(src.Children);
		Assert.Equal("order.handler.cs", src.Children[0].Name);
	}

	// Verifies name filter keeps directories that contain matching children.
	[Fact]
	public void Build_NameFilter_IncludesDirectoryWhenChildMatches()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("domain/invoice.txt", "invoice");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "domain" },
			IgnoreRules: new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()),
			NameFilter: "invoice");

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		var domain = result.Root.Children.Single(c => c.Name == "domain");
		Assert.Single(domain.Children);
		Assert.Equal("invoice.txt", domain.Children[0].Name);
	}

	// Verifies name filter drops directories without matching descendants.
	[Fact]
	public void Build_NameFilter_ExcludesDirectoryWithoutMatches()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("services/service.txt", "service");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "services" },
			IgnoreRules: new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()),
			NameFilter: "order");

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.DoesNotContain(result.Root.Children, child => child.Name == "services");
	}

	// Verifies name filter keeps a matching directory even if it is empty.
	[Fact]
	public void Build_NameFilter_IncludesEmptyDirectoryWhenNameMatches()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateDirectory("orders");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" },
			IgnoreRules: new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()),
			NameFilter: "orders");

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		var orders = result.Root.Children.Single(c => c.Name == "orders");
		Assert.Empty(orders.Children);
	}

	// Verifies name filter respects allowed extensions for matching files.
	[Fact]
	public void Build_NameFilter_RespectsAllowedExtensions()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/order.bin", "bin");
		temp.CreateFile("src/order.txt", "text");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src" },
			IgnoreRules: new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()),
			NameFilter: "order");

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		var src = result.Root.Children.Single(c => c.Name == "src");
		Assert.Single(src.Children);
		Assert.Equal("order.txt", src.Children[0].Name);
	}

	// Verifies root folder filtering still applies when a name filter matches.
	[Fact]
	public void Build_NameFilter_DoesNotOverrideRootFolderFiltering()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("orders/order.txt", "order");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src" },
			IgnoreRules: new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()),
			NameFilter: "order");

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.DoesNotContain(result.Root.Children, child => child.Name == "orders");
	}

	// Verifies name filter does not include root files without a match.
	[Fact]
	public void Build_NameFilter_ExcludesRootFilesWithoutMatch()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("root.txt", "root");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			IgnoreRules: new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()),
			NameFilter: "order");

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		Assert.DoesNotContain(result.Root.Children, child => child.Name == "root.txt");
	}

	[Fact]
	public void Build_WithGitIgnoreNegation_KeepsExplicitlyUnignoredFile()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("build/keep.txt", "keep");
		temp.CreateFile("build/drop.txt", "drop");

		var matcher = GitIgnoreMatcher.Build(temp.Path, new[] { "build/", "!build/keep.txt" });
		var rules = new IgnoreRules(false, false, false, false,
			new HashSet<string>(), new HashSet<string>())
		{
			UseGitIgnore = true,
			GitIgnoreMatcher = matcher
		};

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "build" },
			IgnoreRules: rules);

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);

		var build = result.Root.Children.Single(child => child.Name == "build");
		Assert.Contains(build.Children, child => child.Name == "keep.txt");
		Assert.DoesNotContain(build.Children, child => child.Name == "drop.txt");
	}
}

