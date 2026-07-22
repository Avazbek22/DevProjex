using System.IO.Compression;
using DevProjex.Application.Services;
using DevProjex.Tests.Integration.Helpers;

namespace DevProjex.Tests.Integration;

public sealed class ProjectCopyExportServiceIntegrationTests
{
	[Fact]
	public async Task FolderExport_NoSelectionCopiesCompleteEffectiveTreeByteForByte()
	{
		using var workspace = ProjectCopyWorkspace.Create();
		var result = await workspace.ExportFolderAsync([]);

		Assert.Equal(Path.Combine(workspace.DestinationParent, "Sample-copy"), result.DestinationPath);
		Assert.Equal(5, result.CopiedFileCount);
		Assert.Equal(6, result.CreatedDirectoryCount);
		Assert.Equal(workspace.ExpectedBytes, result.BytesWritten);
		Assert.Equal(workspace.BinaryBytes, await File.ReadAllBytesAsync(
			Path.Combine(result.DestinationPath, "assets", "image.bin"),
			TestContext.Current.CancellationToken));
		Assert.True(Directory.Exists(Path.Combine(result.DestinationPath, "docs", "empty")));
		Assert.True(File.Exists(Path.Combine(result.DestinationPath, "src", "Пример.cs")));
		Assert.True(File.Exists(Path.Combine(result.DestinationPath, "LICENSE")));
	}

	[Fact]
	public async Task FolderExport_SelectedFileCopiesOnlyFileAndRequiredDirectories()
	{
		using var workspace = ProjectCopyWorkspace.Create();
		var result = await workspace.ExportFolderAsync([workspace.Paths["unicode"]]);

		Assert.True(File.Exists(Path.Combine(result.DestinationPath, "src", "Пример.cs")));
		Assert.False(Directory.Exists(Path.Combine(result.DestinationPath, "assets")));
		Assert.False(File.Exists(Path.Combine(result.DestinationPath, "README.md")));
		Assert.Equal(1, result.CopiedFileCount);
		Assert.Equal(2, result.CreatedDirectoryCount);
	}

	[Fact]
	public async Task FolderExport_SelectedDirectoryIncludesAllDescriptorDescendantsWithoutDuplicates()
	{
		using var workspace = ProjectCopyWorkspace.Create();
		var selected = new HashSet<string>(PathComparer.Default)
		{
			workspace.Paths["src"],
			workspace.Paths["unicode"]
		};

		var result = await workspace.ExportFolderAsync(selected);

		Assert.Equal(2, result.CopiedFileCount);
		Assert.True(File.Exists(Path.Combine(result.DestinationPath, "src", "Пример.cs")));
		Assert.True(File.Exists(Path.Combine(result.DestinationPath, "src", "nested", "worker.py")));
	}

	[Fact]
	public async Task FolderExport_NameConflictUsesNextFreeSuffixWithoutChangingExistingFolder()
	{
		using var workspace = ProjectCopyWorkspace.Create();
		var existing = Path.Combine(workspace.DestinationParent, "Sample-copy");
		Directory.CreateDirectory(existing);
		await File.WriteAllTextAsync(
			Path.Combine(existing, "keep.txt"),
			"do not replace",
			TestContext.Current.CancellationToken);

		var result = await workspace.ExportFolderAsync([]);

		Assert.Equal(Path.Combine(workspace.DestinationParent, "Sample-copy (2)"), result.DestinationPath);
		Assert.Equal("do not replace", await File.ReadAllTextAsync(
			Path.Combine(existing, "keep.txt"),
			TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task FolderAndZipExportPreserveFileLastWriteTimeWhenSupported()
	{
		using var workspace = ProjectCopyWorkspace.Create();
		var expected = new DateTime(2024, 4, 5, 6, 7, 8, DateTimeKind.Utc);
		File.SetLastWriteTimeUtc(workspace.Paths["readme"], expected);

		var folder = await workspace.ExportFolderAsync([workspace.Paths["readme"]]);
		var zip = await workspace.ExportZipAsync([workspace.Paths["readme"]]);

		Assert.Equal(expected, File.GetLastWriteTimeUtc(Path.Combine(folder.DestinationPath, "README.md")));
		using var archive = ZipFile.OpenRead(zip.DestinationPath);
		var entry = Assert.Single(archive.Entries, candidate => candidate.FullName == "Sample/README.md");
		Assert.Equal(expected, entry.LastWriteTime.UtcDateTime);
	}

	[Fact]
	public async Task ZipExport_ContainsSingleProjectRootNestedStructureAndEmptyDirectories()
	{
		using var workspace = ProjectCopyWorkspace.Create();
		var result = await workspace.ExportZipAsync([]);

		Assert.EndsWith("Sample-copy.zip", result.DestinationPath, StringComparison.Ordinal);
		using var archive = ZipFile.OpenRead(result.DestinationPath);
		var names = archive.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
		Assert.All(names, name => Assert.StartsWith("Sample/", name, StringComparison.Ordinal));
		Assert.Contains("Sample/docs/empty/", names);
		Assert.Contains("Sample/src/Пример.cs", names);
		Assert.Contains("Sample/LICENSE", names);
		Assert.DoesNotContain(names, name => name.StartsWith("README.md", StringComparison.Ordinal));

		var binaryEntry = archive.GetEntry("Sample/assets/image.bin");
		Assert.NotNull(binaryEntry);
		await using var stream = binaryEntry!.Open();
		using var memory = new MemoryStream();
		await stream.CopyToAsync(memory, TestContext.Current.CancellationToken);
		Assert.Equal(workspace.BinaryBytes, memory.ToArray());
	}

	[Fact]
	public async Task EffectiveTree_OmittedIgnoreAndHiddenItemsAreNeverRediscovered()
	{
		using var workspace = ProjectCopyWorkspace.Create();
		workspace.CreateIgnoredFilesOutsideDescriptor();

		var folder = await workspace.ExportFolderAsync([]);
		var zip = await workspace.ExportZipAsync([]);

		Assert.False(Directory.Exists(Path.Combine(folder.DestinationPath, "bin")));
		Assert.False(Directory.Exists(Path.Combine(folder.DestinationPath, "node_modules")));
		Assert.False(File.Exists(Path.Combine(folder.DestinationPath, ".env")));
		using var archive = ZipFile.OpenRead(zip.DestinationPath);
		Assert.DoesNotContain(archive.Entries, entry =>
			entry.FullName.Contains("node_modules", StringComparison.Ordinal) ||
			entry.FullName.Contains("/bin/", StringComparison.Ordinal) ||
			entry.FullName.EndsWith("/.env", StringComparison.Ordinal));
	}

	[Fact]
	public async Task GitIgnoreAndSmartIgnoreEffectiveTreeFlowsEndToEndIntoPhysicalExport()
	{
		using var source = new TemporaryDirectory();
		using var destination = new TemporaryDirectory();
		source.CreateFile("project/.gitignore", "ignored.txt\n");
		source.CreateFile("project/keep.cs", "class Keep {}\n");
		source.CreateFile("project/ignored.txt", "ignored by git\n");
		source.CreateFile("project/node_modules/pkg/index.js", "ignored by smart rules\n");

		var smartIgnore = new SmartIgnoreService([new FixedSmartIgnoreRule(["node_modules"])]);
		var rules = new IgnoreRulesService(smartIgnore).Build(
			source.Path,
			[IgnoreOptionId.UseGitIgnore, IgnoreOptionId.SmartIgnore],
			["project"]);
		var tree = new TreeBuilder().Build(
			source.Path,
			new TreeFilterOptions(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".txt", ".js", ".gitignore" },
				new HashSet<string>(PathComparer.Default) { "project" },
				rules),
			TestContext.Current.CancellationToken);
		var descriptor = ToDescriptor(tree.Root);
		var service = new ProjectCopyExportService(new ProjectCopyExportPlanBuilder());

		var result = await service.ExportAsync(
			new ProjectCopyExportRequest(
				source.Path,
				"Workspace",
				descriptor,
				new HashSet<string>(PathComparer.Default),
				destination.Path,
				ProjectCopyExportFormat.Folder),
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(File.Exists(Path.Combine(result.DestinationPath, "project", "keep.cs")));
		Assert.False(File.Exists(Path.Combine(result.DestinationPath, "project", "ignored.txt")));
		Assert.False(Directory.Exists(Path.Combine(result.DestinationPath, "project", "node_modules")));
	}

	[Theory]
	[InlineData(ProjectCopyExportFormat.Folder)]
	[InlineData(ProjectCopyExportFormat.Zip)]
	public async Task DestinationInsideSourceIsRejectedBeforeWriting(ProjectCopyExportFormat format)
	{
		using var workspace = ProjectCopyWorkspace.Create();
		var destination = format == ProjectCopyExportFormat.Folder
			? workspace.SourceRoot
			: Path.Combine(workspace.SourceRoot, "copy.zip");

		var exception = await Assert.ThrowsAsync<ProjectCopyExportException>(() =>
			workspace.ExportAsync(
				format,
				destination,
				[],
				cancellationToken: TestContext.Current.CancellationToken));

		Assert.Equal(ProjectCopyExportError.DestinationInsideSource, exception.Error);
		Assert.False(File.Exists(Path.Combine(workspace.SourceRoot, "copy.zip")));
		Assert.Empty(FindStagingArtifacts(workspace.SourceRoot));
	}

	[Fact]
	public async Task FolderDestinationSymlinkIntoSourceIsRejectedBeforeStaging()
	{
		using var workspace = ProjectCopyWorkspace.Create();
		var linkPath = Path.Combine(workspace.DestinationParent, "source-link");
		CreateDirectoryLinkOrSkip(linkPath, workspace.SourceRoot);

		try
		{
			var exception = await Assert.ThrowsAsync<ProjectCopyExportException>(() =>
				workspace.ExportAsync(
					ProjectCopyExportFormat.Folder,
					linkPath,
					[],
					cancellationToken: TestContext.Current.CancellationToken));

			Assert.Equal(ProjectCopyExportError.UnsafeDestinationPath, exception.Error);
			Assert.False(Directory.Exists(Path.Combine(workspace.SourceRoot, "Sample-copy")));
			Assert.Empty(FindStagingArtifacts(workspace.SourceRoot));
		}
		finally
		{
			DeleteDirectoryLink(linkPath);
		}
	}

	[Fact]
	public async Task ZipDestinationDirectorySymlinkIntoSourceIsRejectedBeforeStaging()
	{
		using var workspace = ProjectCopyWorkspace.Create();
		var linkPath = Path.Combine(workspace.DestinationParent, "source-link");
		CreateDirectoryLinkOrSkip(linkPath, workspace.SourceRoot);

		try
		{
			var destination = Path.Combine(linkPath, "copy.zip");
			var exception = await Assert.ThrowsAsync<ProjectCopyExportException>(() =>
				workspace.ExportAsync(
					ProjectCopyExportFormat.Zip,
					destination,
					[],
					cancellationToken: TestContext.Current.CancellationToken));

			Assert.Equal(ProjectCopyExportError.UnsafeDestinationPath, exception.Error);
			Assert.False(File.Exists(Path.Combine(workspace.SourceRoot, "copy.zip")));
			Assert.Empty(FindStagingArtifacts(workspace.SourceRoot));
		}
		finally
		{
			DeleteDirectoryLink(linkPath);
		}
	}

	[Fact]
	public async Task SafeExternalDestinationSymlinkResolvesToTargetAndExports()
	{
		using var workspace = ProjectCopyWorkspace.Create();
		var externalTarget = Directory.CreateDirectory(Path.Combine(workspace.DestinationParent, "safe-target")).FullName;
		var linkPath = Path.Combine(workspace.DestinationParent, "safe-link");
		CreateDirectoryLinkOrSkip(linkPath, externalTarget);

		try
		{
			var result = await workspace.ExportAsync(
				ProjectCopyExportFormat.Folder,
				linkPath,
				[],
				cancellationToken: TestContext.Current.CancellationToken);

			Assert.True(Directory.Exists(result.DestinationPath));
			Assert.True(File.Exists(Path.Combine(externalTarget, "Sample-copy", "README.md")));
			Assert.Empty(FindStagingArtifacts(externalTarget));
		}
		finally
		{
			DeleteDirectoryLink(linkPath);
		}
	}

	[Fact]
	public async Task MacOsSystemTemporaryPathSymlinkDoesNotMakeExternalDestinationUnsafe()
	{
		if (!OperatingSystem.IsMacOS())
			Assert.Skip("The macOS system temporary-path regression only applies to macOS.");

		using var workspace = ProjectCopyWorkspace.Create();
		using var destination = new TemporaryDirectory();
		var destinationParent = destination.CreateDirectory("exports");

		var result = await workspace.ExportAsync(
			ProjectCopyExportFormat.Folder,
			destinationParent,
			[],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(Directory.Exists(result.DestinationPath));
		Assert.False(PathUtility.IsPathInside(result.DestinationPath, workspace.SourceRoot));
		Assert.True(File.Exists(Path.Combine(result.DestinationPath, "README.md")));
		Assert.Empty(FindStagingArtifacts(destinationParent));
	}

	[Fact]
	public async Task WindowsJunctionIntoSourceIsRejectedBeforeWriting()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("Windows junctions are only available on Windows.");

		using var workspace = ProjectCopyWorkspace.Create();
		var junctionPath = Path.Combine(workspace.DestinationParent, "source-junction");
		CreateWindowsJunctionOrSkip(junctionPath, workspace.SourceRoot);

		try
		{
			var exception = await Assert.ThrowsAsync<ProjectCopyExportException>(() =>
				workspace.ExportAsync(
					ProjectCopyExportFormat.Folder,
					junctionPath,
					[],
					cancellationToken: TestContext.Current.CancellationToken));

			Assert.Equal(ProjectCopyExportError.UnsafeDestinationPath, exception.Error);
			Assert.False(Directory.Exists(Path.Combine(workspace.SourceRoot, "Sample-copy")));
			Assert.Empty(FindStagingArtifacts(workspace.SourceRoot));
		}
		finally
		{
			DeleteDirectoryLink(junctionPath);
		}
	}

	[Fact]
	public async Task SafeExternalWindowsJunctionResolvesToTargetAndExports()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("Windows junctions are only available on Windows.");

		using var workspace = ProjectCopyWorkspace.Create();
		var externalTarget = Directory.CreateDirectory(Path.Combine(workspace.DestinationParent, "junction-target")).FullName;
		var junctionPath = Path.Combine(workspace.DestinationParent, "safe-junction");
		CreateWindowsJunctionOrSkip(junctionPath, externalTarget);

		try
		{
			var result = await workspace.ExportAsync(
				ProjectCopyExportFormat.Folder,
				junctionPath,
				[],
				cancellationToken: TestContext.Current.CancellationToken);

			Assert.True(Directory.Exists(result.DestinationPath));
			Assert.True(File.Exists(Path.Combine(externalTarget, "Sample-copy", "README.md")));
			Assert.Empty(FindStagingArtifacts(externalTarget));
		}
		finally
		{
			DeleteDirectoryLink(junctionPath);
		}
	}

	[Fact]
	public async Task OrdinaryExternalDestinationStillExportsFolderAndZip()
	{
		using var workspace = ProjectCopyWorkspace.Create();

		var folder = await workspace.ExportFolderAsync([]);
		var zip = await workspace.ExportZipAsync([]);

		Assert.True(Directory.Exists(folder.DestinationPath));
		Assert.True(File.Exists(zip.DestinationPath));
		Assert.Empty(FindStagingArtifacts(workspace.DestinationParent));
	}

	[Fact]
	public async Task DescriptorPathOutsideProjectRootIsRejected()
	{
		using var workspace = ProjectCopyWorkspace.Create();
		var outsideFile = Path.Combine(workspace.DestinationParent, "outside.txt");
		await File.WriteAllTextAsync(outsideFile, "outside", TestContext.Current.CancellationToken);
		var unsafeRoot = new TreeNodeDescriptor("Sample", workspace.SourceRoot, true, false, "folder",
		[
			new TreeNodeDescriptor("outside.txt", outsideFile, false, false, "file", [])
		]);

		var exception = await Assert.ThrowsAsync<ProjectCopyExportException>(() =>
			workspace.ExportAsync(
				ProjectCopyExportFormat.Folder,
				workspace.DestinationParent,
				[],
				unsafeRoot,
				cancellationToken: TestContext.Current.CancellationToken));

		Assert.Equal(ProjectCopyExportError.UnsafeSourcePath, exception.Error);
		Assert.False(Directory.Exists(Path.Combine(workspace.DestinationParent, "Sample-copy")));
	}

	[Theory]
	[InlineData(ProjectCopyExportFormat.Folder)]
	[InlineData(ProjectCopyExportFormat.Zip)]
	public async Task CancellationRemovesStagingAndLeavesNoFinalResult(ProjectCopyExportFormat format)
	{
		using var workspace = ProjectCopyWorkspace.Create();
		using var cancellation = new CancellationTokenSource();
		var progress = new CallbackProgress<ProjectCopyExportProgress>(value =>
		{
			if (value.ProcessedFileCount == 1)
				cancellation.Cancel();
		});
		var destination = format == ProjectCopyExportFormat.Folder
			? workspace.DestinationParent
			: Path.Combine(workspace.DestinationParent, "canceled.zip");

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			workspace.ExportAsync(format, destination, [], progress: progress, cancellationToken: cancellation.Token));

		Assert.False(Directory.Exists(Path.Combine(workspace.DestinationParent, "Sample-copy")));
		Assert.False(File.Exists(Path.Combine(workspace.DestinationParent, "canceled.zip")));
		Assert.Empty(FindStagingArtifacts(workspace.DestinationParent));
	}

	[Fact]
	public async Task MissingSourceFailsWithoutPartialDestination()
	{
		using var workspace = ProjectCopyWorkspace.Create();
		File.Delete(workspace.Paths["readme"]);

		await Assert.ThrowsAsync<ProjectCopyExportException>(() => workspace.ExportFolderAsync([]));

		Assert.False(Directory.Exists(Path.Combine(workspace.DestinationParent, "Sample-copy")));
		Assert.Empty(FindStagingArtifacts(workspace.DestinationParent));
	}

	[Fact]
	public async Task NestedSymbolicLinkNeverReadsExternalTarget()
	{
		using var workspace = ProjectCopyWorkspace.Create();
		var externalFile = Path.Combine(workspace.DestinationParent, "secret.txt");
		await File.WriteAllTextAsync(externalFile, "secret", TestContext.Current.CancellationToken);
		var linkPath = Path.Combine(workspace.SourceRoot, "linked-secret.txt");
		try
		{
			File.CreateSymbolicLink(linkPath, externalFile);
		}
		catch (Exception linkCreationException) when (linkCreationException is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
		{
			Assert.Skip("Symbolic links are not available in this test environment.");
		}

		var linkNode = new TreeNodeDescriptor("linked-secret.txt", linkPath, false, false, "file", []);
		var tree = workspace.Root with { Children = [.. workspace.Root.Children, linkNode] };
		var exportException = await Assert.ThrowsAsync<ProjectCopyExportException>(() =>
			workspace.ExportAsync(
				ProjectCopyExportFormat.Folder,
				workspace.DestinationParent,
				[],
				tree,
				cancellationToken: TestContext.Current.CancellationToken));

		Assert.Equal(ProjectCopyExportError.SymbolicLinkNotSupported, exportException.Error);
		Assert.False(Directory.Exists(Path.Combine(workspace.DestinationParent, "Sample-copy")));
	}

	private static string[] FindStagingArtifacts(string path) =>
		Directory.Exists(path)
			? Directory.GetFileSystemEntries(path, "*devprojex*.tmp", SearchOption.TopDirectoryOnly)
			: [];

	private static void CreateDirectoryLinkOrSkip(string linkPath, string targetPath)
	{
		try
		{
			Directory.CreateSymbolicLink(linkPath, targetPath);
		}
		catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
		{
			Assert.Skip($"Directory symbolic links are unavailable: {exception.GetType().Name}.");
		}
	}

	private static void CreateWindowsJunctionOrSkip(string junctionPath, string targetPath)
	{
		using var process = new Process
		{
			StartInfo = new ProcessStartInfo("cmd.exe")
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			}
		};
		process.StartInfo.ArgumentList.Add("/c");
		process.StartInfo.ArgumentList.Add("mklink");
		process.StartInfo.ArgumentList.Add("/J");
		process.StartInfo.ArgumentList.Add(junctionPath);
		process.StartInfo.ArgumentList.Add(targetPath);

		try
		{
			process.Start();
			process.WaitForExit();
			if (process.ExitCode != 0 || !Directory.Exists(junctionPath))
				Assert.Skip("The test environment did not allow creating a Windows junction.");
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
		{
			Assert.Skip($"Windows junction creation is unavailable: {exception.GetType().Name}.");
		}
	}

	private static void DeleteDirectoryLink(string path)
	{
		try
		{
			if (Directory.Exists(path))
				Directory.Delete(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// The enclosing temporary workspace performs a final best-effort cleanup.
		}
	}

	private static TreeNodeDescriptor ToDescriptor(FileSystemNode node) =>
		new(
			node.Name,
			node.FullPath,
			node.IsDirectory,
			node.IsAccessDenied,
			node.IsDirectory ? "folder" : "file",
			node.Children.Select(ToDescriptor).ToArray());

	private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
	{
		public void Report(T value) => callback(value);
	}

	private sealed class FixedSmartIgnoreRule(IReadOnlyCollection<string> folders) : ISmartIgnoreRule
	{
		public SmartIgnoreResult Evaluate(string rootPath) =>
			new(
				new HashSet<string>(folders, StringComparer.OrdinalIgnoreCase),
				new HashSet<string>(StringComparer.OrdinalIgnoreCase));
	}

	private sealed class ProjectCopyWorkspace : IDisposable
	{
		private readonly TemporaryDirectory _temporary = new();
		private readonly ProjectCopyExportService _service = new(new ProjectCopyExportPlanBuilder());

		private ProjectCopyWorkspace()
		{
			SourceRoot = _temporary.CreateDirectory("Sample");
			DestinationParent = _temporary.CreateDirectory("exports");
			BinaryBytes = [0, 1, 2, 127, 128, 255, 0, 42];
			Paths = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["readme"] = CreateText("README.md", "readme"),
				["unicode"] = CreateText(Path.Combine("src", "Пример.cs"), "class Пример {}"),
				["worker"] = CreateText(Path.Combine("src", "nested", "worker.py"), "print('ok')"),
				["binary"] = CreateBinary(Path.Combine("assets", "image.bin"), BinaryBytes),
				["extensionless"] = CreateText("LICENSE", "license")
			};
			var emptyDirectory = Directory.CreateDirectory(Path.Combine(SourceRoot, "docs", "empty")).FullName;
			Root = DirectoryNode(SourceRoot,
				FileNode(Paths["readme"]),
				DirectoryNode(Path.Combine(SourceRoot, "src"),
					FileNode(Paths["unicode"]),
					DirectoryNode(Path.Combine(SourceRoot, "src", "nested"), FileNode(Paths["worker"]))),
				DirectoryNode(Path.Combine(SourceRoot, "assets"), FileNode(Paths["binary"])),
				DirectoryNode(Path.Combine(SourceRoot, "docs"), DirectoryNode(emptyDirectory)),
				FileNode(Paths["extensionless"]));
			Paths["src"] = Path.Combine(SourceRoot, "src");
			Paths["empty"] = emptyDirectory;
			ExpectedBytes = Paths.Values
				.Where(File.Exists)
				.Distinct(PathComparer.Default)
				.Sum(path => new FileInfo(path).Length);
		}

		public string SourceRoot { get; }
		public string DestinationParent { get; }
		public byte[] BinaryBytes { get; }
		public Dictionary<string, string> Paths { get; }
		public TreeNodeDescriptor Root { get; }
		public long ExpectedBytes { get; }

		public static ProjectCopyWorkspace Create() => new();

		public Task<ProjectCopyExportResult> ExportFolderAsync(IEnumerable<string> selected) =>
			ExportAsync(ProjectCopyExportFormat.Folder, DestinationParent, selected);

		public Task<ProjectCopyExportResult> ExportZipAsync(IEnumerable<string> selected) =>
			ExportAsync(ProjectCopyExportFormat.Zip, Path.Combine(DestinationParent, "Sample-copy"), selected);

		public Task<ProjectCopyExportResult> ExportAsync(
			ProjectCopyExportFormat format,
			string destination,
			IEnumerable<string> selected,
			TreeNodeDescriptor? tree = null,
			IProgress<ProjectCopyExportProgress>? progress = null,
			CancellationToken cancellationToken = default) =>
			_service.ExportAsync(
				new ProjectCopyExportRequest(
					SourceRoot,
					"Sample",
					tree ?? Root,
					selected.ToHashSet(PathComparer.Default),
					destination,
					format),
				progress,
				cancellationToken == default ? TestContext.Current.CancellationToken : cancellationToken);

		public void CreateIgnoredFilesOutsideDescriptor()
		{
			CreateText(".env", "TOKEN=secret");
			CreateText(Path.Combine("bin", "app.dll"), "binary artifact");
			CreateText(Path.Combine("node_modules", "package", "index.js"), "artifact");
		}

		private string CreateText(string relativePath, string content)
		{
			var path = Path.Combine(SourceRoot, relativePath);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, content);
			return path;
		}

		private string CreateBinary(string relativePath, byte[] bytes)
		{
			var path = Path.Combine(SourceRoot, relativePath);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllBytes(path, bytes);
			return path;
		}

		private static TreeNodeDescriptor DirectoryNode(string path, params TreeNodeDescriptor[] children) =>
			new(Path.GetFileName(path), path, true, false, "folder", children);

		private static TreeNodeDescriptor FileNode(string path) =>
			new(Path.GetFileName(path), path, false, false, "file", []);

		public void Dispose() => _temporary.Dispose();
	}
}
