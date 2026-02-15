using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DevProjex.Infrastructure.FileSystem;
using DevProjex.Kernel.Models;
using DevProjex.Tests.Integration.Helpers;
using Xunit;

namespace DevProjex.Tests.Integration;

/// <summary>
/// Tests for parallel scanning behavior in FileSystemScanner.
/// Verifies that multi-directory scanning works correctly with parallelism.
/// </summary>
public sealed class FileSystemScannerParallelTests
{
	private static IgnoreRules CreateDefaultRules() => new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(),
		SmartIgnoredFiles: new HashSet<string>());

	/// <summary>
	/// Verifies scanning collects extensions from multiple directories in parallel.
	/// </summary>
	[Fact]
	public void GetExtensions_ScansMultipleDirectoriesInParallel()
	{
		using var temp = new TemporaryDirectory();

		// Create deep directory structure
		for (int i = 0; i < 10; i++)
		{
			temp.CreateFile($"dir{i}/file.cs", $"class C{i} {{}}");
			temp.CreateFile($"dir{i}/sub/nested.txt", "nested");
		}

		var scanner = new FileSystemScanner();
		var result = scanner.GetExtensions(temp.Path, CreateDefaultRules());

		Assert.Contains(".cs", result.Value);
		Assert.Contains(".txt", result.Value);
		Assert.False(result.RootAccessDenied);
		Assert.False(result.HadAccessDenied);
	}

	/// <summary>
	/// Verifies parallel scanning deduplicates extensions correctly.
	/// </summary>
	[Fact]
	public void GetExtensions_DeduplicatesAcrossParallelScans()
	{
		using var temp = new TemporaryDirectory();

		// Multiple directories with same extensions
		for (int i = 0; i < 20; i++)
		{
			temp.CreateFile($"folder{i}/code.cs", $"class F{i} {{}}");
			temp.CreateFile($"folder{i}/readme.MD", "readme");
			temp.CreateFile($"folder{i}/data.Cs", "more code"); // Different case
		}

		var scanner = new FileSystemScanner();
		var result = scanner.GetExtensions(temp.Path, CreateDefaultRules());

		// Should have only 2 unique extensions (case-insensitive)
		Assert.Equal(2, result.Value.Count);
		Assert.Contains(".cs", result.Value);
		Assert.Contains(".MD", result.Value);
	}

	/// <summary>
	/// Verifies parallel scanning handles deep directory trees.
	/// </summary>
	[Fact]
	public void GetExtensions_HandlesDeepDirectoryTree()
	{
		using var temp = new TemporaryDirectory();

		// Create deep nested structure
		var deepPath = "level1/level2/level3/level4/level5";
		temp.CreateFile($"{deepPath}/deep.cs", "deep code");
		temp.CreateFile("shallow.txt", "shallow");

		var scanner = new FileSystemScanner();
		var result = scanner.GetExtensions(temp.Path, CreateDefaultRules());

		Assert.Contains(".cs", result.Value);
		Assert.Contains(".txt", result.Value);
	}

	/// <summary>
	/// Verifies parallel scanning handles wide directory trees.
	/// </summary>
	[Fact]
	public void GetExtensions_HandlesWideDirectoryTree()
	{
		using var temp = new TemporaryDirectory();

		// Create many sibling directories
		for (int i = 0; i < 50; i++)
		{
			temp.CreateFile($"sibling{i}/file{i}.ext{i % 5}", $"content {i}");
		}

		var scanner = new FileSystemScanner();
		var result = scanner.GetExtensions(temp.Path, CreateDefaultRules());

		// Should have 5 unique extensions (.ext0 through .ext4)
		Assert.Equal(5, result.Value.Count);
		for (int i = 0; i < 5; i++)
		{
			Assert.Contains($".ext{i}", result.Value);
		}
	}

	/// <summary>
	/// Verifies parallel scanning returns empty for non-existent path.
	/// </summary>
	[Fact]
	public void GetExtensions_ReturnsEmptyForNonExistentPath()
	{
		var scanner = new FileSystemScanner();
		var result = scanner.GetExtensions("/non/existent/path/12345", CreateDefaultRules());

		Assert.Empty(result.Value);
		Assert.False(result.RootAccessDenied);
		Assert.False(result.HadAccessDenied);
	}

	/// <summary>
	/// Verifies parallel scanning handles empty directories.
	/// </summary>
	[Fact]
	public void GetExtensions_HandlesEmptyDirectories()
	{
		using var temp = new TemporaryDirectory();

		// Create directories without files
		for (int i = 0; i < 10; i++)
		{
			temp.CreateDirectory($"empty{i}");
		}
		temp.CreateFile("hasfile/file.cs", "code");

		var scanner = new FileSystemScanner();
		var result = scanner.GetExtensions(temp.Path, CreateDefaultRules());

		Assert.Single(result.Value);
		Assert.Contains(".cs", result.Value);
	}

	/// <summary>
	/// Verifies parallel scanning respects ignore rules consistently.
	/// </summary>
	[Fact]
	public void GetExtensions_RespectsIgnoreRulesInParallel()
	{
		using var temp = new TemporaryDirectory();

		// Create structure with dot folders and files
		for (int i = 0; i < 10; i++)
		{
			temp.CreateFile($".hidden{i}/secret.key", "secret");
			temp.CreateFile($"visible{i}/.dotfile", "dot");
			temp.CreateFile($"visible{i}/normal.txt", "normal");
		}

		var scanner = new FileSystemScanner();
		var rules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: true,
			IgnoreDotFiles: true,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>());

		var result = scanner.GetExtensions(temp.Path, rules);

		Assert.Single(result.Value);
		Assert.Contains(".txt", result.Value);
		Assert.DoesNotContain(".key", result.Value);
	}

	/// <summary>
	/// Verifies parallel scanning handles mixed file/directory structure.
	/// </summary>
	[Fact]
	public void GetExtensions_HandlesMixedStructure()
	{
		using var temp = new TemporaryDirectory();

		// Root files
		temp.CreateFile("root1.cs", "root code");
		temp.CreateFile("root2.txt", "root text");

		// Nested structure
		temp.CreateFile("src/main.cs", "main");
		temp.CreateFile("src/lib/helper.cs", "helper");
		temp.CreateFile("docs/readme.md", "readme");
		temp.CreateFile("tests/test.cs", "test");

		var scanner = new FileSystemScanner();
		var result = scanner.GetExtensions(temp.Path, CreateDefaultRules());

		Assert.Equal(3, result.Value.Count);
		Assert.Contains(".cs", result.Value);
		Assert.Contains(".txt", result.Value);
		Assert.Contains(".md", result.Value);
	}

	/// <summary>
	/// Verifies concurrent GetExtensions calls don't interfere.
	/// </summary>
	[Fact]
	public async Task GetExtensions_ConcurrentCallsAreIsolated()
	{
		using var temp1 = new TemporaryDirectory();
		using var temp2 = new TemporaryDirectory();

		temp1.CreateFile("file.cs", "cs");
		temp2.CreateFile("file.txt", "txt");

		var scanner = new FileSystemScanner();
		var rules = CreateDefaultRules();

		var task1 = Task.Run(() => scanner.GetExtensions(temp1.Path, rules));
		var task2 = Task.Run(() => scanner.GetExtensions(temp2.Path, rules));

		var results = await Task.WhenAll(task1, task2);
		var result1 = results[0];
		var result2 = results[1];

		Assert.Contains(".cs", result1.Value);
		Assert.DoesNotContain(".txt", result1.Value);

		Assert.Contains(".txt", result2.Value);
		Assert.DoesNotContain(".cs", result2.Value);
	}

	/// <summary>
	/// Verifies scanner handles special characters in paths during parallel scan.
	/// </summary>
	[Fact]
	public void GetExtensions_HandlesSpecialCharactersInPaths()
	{
		using var temp = new TemporaryDirectory();

		// Create directories with special characters (that are valid on most filesystems)
		temp.CreateFile("folder with spaces/file.cs", "code");
		temp.CreateFile("folder-with-dashes/file.txt", "text");
		temp.CreateFile("folder_with_underscores/file.md", "markdown");

		var scanner = new FileSystemScanner();
		var result = scanner.GetExtensions(temp.Path, CreateDefaultRules());

		Assert.Equal(3, result.Value.Count);
		Assert.Contains(".cs", result.Value);
		Assert.Contains(".txt", result.Value);
		Assert.Contains(".md", result.Value);
	}

	/// <summary>
	/// Verifies scanner handles files without extensions in parallel.
	/// </summary>
	[Fact]
	public void GetExtensions_HandlesFilesWithoutExtension()
	{
		using var temp = new TemporaryDirectory();

		for (int i = 0; i < 10; i++)
		{
			temp.CreateFile($"dir{i}/Makefile", "make");
			temp.CreateFile($"dir{i}/Dockerfile", "docker");
			temp.CreateFile($"dir{i}/code.cs", "code");
		}

		var scanner = new FileSystemScanner();
		var result = scanner.GetExtensions(temp.Path, CreateDefaultRules());

		// Extensionless files are returned as named entries; regular files by extension.
		Assert.Equal(3, result.Value.Count);
		Assert.Contains("Makefile", result.Value);
		Assert.Contains("Dockerfile", result.Value);
		Assert.Contains(".cs", result.Value);
	}

	/// <summary>
	/// Verifies smart ignore is applied consistently across parallel scans.
	/// </summary>
	[Fact]
	public void GetExtensions_SmartIgnoreAppliedInParallel()
	{
		using var temp = new TemporaryDirectory();

		// Create structure with smart-ignored folders
		for (int i = 0; i < 10; i++)
		{
			temp.CreateFile($"src{i}/code.cs", "code");
			temp.CreateFile($"node_modules{i}/package.js", "package");
		}

		var scanner = new FileSystemScanner();
		var rules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				"node_modules0", "node_modules1", "node_modules2", "node_modules3", "node_modules4",
				"node_modules5", "node_modules6", "node_modules7", "node_modules8", "node_modules9"
			},
			SmartIgnoredFiles: new HashSet<string>())
		{
			UseSmartIgnore = true
		};

		var result = scanner.GetExtensions(temp.Path, rules);

		Assert.Single(result.Value);
		Assert.Contains(".cs", result.Value);
		Assert.DoesNotContain(".js", result.Value);
	}

	/// <summary>
	/// Verifies GetRootFolderNames returns sorted results.
	/// </summary>
	[Fact]
	public void GetRootFolderNames_ReturnsSortedResults()
	{
		using var temp = new TemporaryDirectory();

		temp.CreateDirectory("zebra");
		temp.CreateDirectory("Alpha");
		temp.CreateDirectory("beta");
		temp.CreateDirectory("123");

		var scanner = new FileSystemScanner();
		var result = scanner.GetRootFolderNames(temp.Path, CreateDefaultRules());

		Assert.Equal(4, result.Value.Count);
		Assert.Equal("123", result.Value[0]);
		Assert.Equal("Alpha", result.Value[1]);
		Assert.Equal("beta", result.Value[2]);
		Assert.Equal("zebra", result.Value[3]);
	}

	/// <summary>
	/// Verifies GetRootFileExtensions only returns root-level extensions.
	/// </summary>
	[Fact]
	public void GetRootFileExtensions_IgnoresNestedFiles()
	{
		using var temp = new TemporaryDirectory();

		// Root files
		temp.CreateFile("root.cs", "root");

		// Nested files
		temp.CreateFile("nested/deep.txt", "deep");
		temp.CreateFile("nested/deeper/verydeep.md", "verydeep");

		var scanner = new FileSystemScanner();
		var result = scanner.GetRootFileExtensions(temp.Path, CreateDefaultRules());

		Assert.Single(result.Value);
		Assert.Contains(".cs", result.Value);
		Assert.DoesNotContain(".txt", result.Value);
		Assert.DoesNotContain(".md", result.Value);
	}

	/// <summary>
	/// Verifies CanReadRoot returns correct values.
	/// </summary>
	[Fact]
	public void CanReadRoot_ReturnsCorrectValues()
	{
		using var temp = new TemporaryDirectory();

		var scanner = new FileSystemScanner();

		Assert.True(scanner.CanReadRoot(temp.Path));
		Assert.True(scanner.CanReadRoot("/non/existent")); // Returns true for non-existent (not access denied)
	}

	/// <summary>
	/// Stress test for parallel scanning with many files.
	/// </summary>
	[Fact]
	public void GetExtensions_StressTestManyFiles()
	{
		using var temp = new TemporaryDirectory();

		// Create structure with many files
		for (int dir = 0; dir < 20; dir++)
		{
			for (int file = 0; file < 20; file++)
			{
				var ext = file % 10;
				temp.CreateFile($"dir{dir}/file{file}.ext{ext}", $"content {dir}_{file}");
			}
		}

		var scanner = new FileSystemScanner();
		var result = scanner.GetExtensions(temp.Path, CreateDefaultRules());

		// Should have 10 unique extensions (.ext0 through .ext9)
		Assert.Equal(10, result.Value.Count);
		Assert.False(result.RootAccessDenied);
		Assert.False(result.HadAccessDenied);
	}
}
