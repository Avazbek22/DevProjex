using System.IO.Compression;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Secrets;

namespace DevProjex.Tests.Integration;

public sealed class ProjectCopyExportServiceIntegrationTests
{
	[Fact]
	public async Task ZipExportPreservesBackslashesInsideUnixFileNames()
	{
		if (OperatingSystem.IsWindows())
			Assert.Skip("Windows treats a backslash as a directory separator.");

		using var workspace = new TemporaryDirectory();
		var sourceRoot = workspace.CreateDirectory("Sample");
		var destinationRoot = workspace.CreateDirectory("exports");
		var sourceFile = Path.Combine(sourceRoot, "literal\\name.txt");
		await File.WriteAllTextAsync(sourceFile, "content", TestContext.Current.CancellationToken);
		var file = new TreeNodeDescriptor(
			Path.GetFileName(sourceFile),
			sourceFile,
			false,
			false,
			"file",
			[]);
		var root = new TreeNodeDescriptor("Sample", sourceRoot, true, false, "folder", [file]);
		var destination = Path.Combine(destinationRoot, "copy.zip");

		var result = await new ProjectCopyExportService(new ProjectCopyExportPlanBuilder()).ExportAsync(
			new ProjectCopyExportRequest(
				sourceRoot,
				"Sample",
				root,
				new HashSet<string>(PathComparer.Default),
				destination,
				ProjectCopyExportFormat.Zip,
				ProjectCopyDestinationMode.Exact),
			cancellationToken: TestContext.Current.CancellationToken);

		using var archive = ZipFile.OpenRead(result.DestinationPath);
		Assert.Contains(archive.Entries, static entry => entry.FullName == "Sample/literal\\name.txt");
		Assert.DoesNotContain(archive.Entries, static entry => entry.FullName == "Sample/literal/name.txt");
	}

	[Fact]
	public async Task FolderExport_NoSelectionCopiesCompleteEffectiveTreeByteForByte()
	{
		using var workspace = ProjectCopyWorkspace.Create();
		var result = await workspace.ExportFolderAsync([]);

		Assert.Equal(Path.Combine(workspace.DestinationParent, "Sample-copy"), result.DestinationPath);
		Assert.Equal(5, result.CopiedFileCount);
		Assert.Equal(6, result.CreatedDirectoryCount);
		Assert.Equal(workspace.ExpectedBytes, result.BytesWritten);
		Assert.Equal(0, result.RedactedValueCount);
		Assert.Equal(workspace.BinaryBytes, await File.ReadAllBytesAsync(
			Path.Combine(result.DestinationPath, "assets", "image.bin"),
			TestContext.Current.CancellationToken));
		Assert.True(Directory.Exists(Path.Combine(result.DestinationPath, "docs", "empty")));
		Assert.True(File.Exists(Path.Combine(result.DestinationPath, "src", "Пример.cs")));
		Assert.True(File.Exists(Path.Combine(result.DestinationPath, "LICENSE")));
	}

	[Fact]
	public async Task ZipAtomicReplacementPreservesExistingReaders()
	{
		using var workspace = ProjectCopyWorkspace.Create();
		var destination = Path.Combine(workspace.DestinationParent, "existing.zip");
		var originalBytes = "existing archive"u8.ToArray();
		await File.WriteAllBytesAsync(destination, originalBytes, TestContext.Current.CancellationToken);
		await using var existingReader = new FileStream(
			destination,
			FileMode.Open,
			FileAccess.Read,
			FileShare.ReadWrite | FileShare.Delete);

		var result = await new ProjectCopyExportService(new ProjectCopyExportPlanBuilder()).ExportAsync(
			new ProjectCopyExportRequest(
				workspace.SourceRoot,
				"Sample",
				workspace.Root,
				new HashSet<string>(PathComparer.Default),
				destination,
				ProjectCopyExportFormat.Zip,
				ProjectCopyDestinationMode.Exact,
				ProjectCopyConflictPolicy.ReplaceAtomically),
			cancellationToken: TestContext.Current.CancellationToken);

		var observedOriginalBytes = new byte[originalBytes.Length];
		await existingReader.ReadExactlyAsync(observedOriginalBytes, TestContext.Current.CancellationToken);
		Assert.Equal(originalBytes, observedOriginalBytes);
		Assert.Equal(destination, result.DestinationPath);
		using var replacement = ZipFile.OpenRead(destination);
		Assert.Contains(replacement.Entries, static entry => entry.FullName == "Sample/README.md");
	}

	[Theory]
	[InlineData(ProjectCopyExportFormat.Folder)]
	[InlineData(ProjectCopyExportFormat.Zip)]
	public async Task ProgressCountsDirectoriesAndFilesFromEffectiveExportPlan(ProjectCopyExportFormat format)
	{
		using var workspace = ProjectCopyWorkspace.Create();
		var updates = new List<ProjectCopyExportProgress>();
		var progress = new CallbackProgress<ProjectCopyExportProgress>(updates.Add);
		var destination = format == ProjectCopyExportFormat.Folder
			? workspace.DestinationParent
			: Path.Combine(workspace.DestinationParent, "progress.zip");

		var result = await workspace.ExportAsync(
			format,
			destination,
			[],
			progress: progress,
			cancellationToken: TestContext.Current.CancellationToken);

		var expectedEntryCount = result.CreatedDirectoryCount + result.CopiedFileCount;
		Assert.NotEmpty(updates);
		Assert.Equal(expectedEntryCount, updates[^1].ProcessedEntryCount);
		Assert.Equal(expectedEntryCount, updates[^1].TotalEntryCount);
		Assert.Equal(100, updates[^1].Percentage);
		Assert.All(updates, update => Assert.Equal(expectedEntryCount, update.TotalEntryCount));
		Assert.True(updates.Select(update => update.ProcessedEntryCount).SequenceEqual(
			updates.Select(update => update.ProcessedEntryCount).Order()));
	}

	[Theory]
	[InlineData(ProjectCopyExportFormat.Folder)]
	[InlineData(ProjectCopyExportFormat.Zip)]
	public async Task Export_SourceReplacedByFilesystemAliasAfterValidation_IsRejected(
		ProjectCopyExportFormat format)
	{
		const string externalContent = "outside-project-content-must-not-be-copied";
		using var workspace = ProjectCopyWorkspace.Create();
		var sourcePath = workspace.Paths["unicode"];
		var sourceDirectory = Path.GetDirectoryName(sourcePath)!;
		var externalDirectory = Directory.CreateDirectory(
			Path.Combine(workspace.DestinationParent, "external-source")).FullName;
		await File.WriteAllTextAsync(
			Path.Combine(externalDirectory, Path.GetFileName(sourcePath)),
			externalContent,
			TestContext.Current.CancellationToken);
		VerifyAliasSupport();
		var sourceReplaced = false;
		var progress = new CallbackProgress<ProjectCopyExportProgress>(_ =>
		{
			if (sourceReplaced)
				return;
			sourceReplaced = true;
			if (OperatingSystem.IsWindows())
			{
				Directory.Delete(sourceDirectory, recursive: true);
				CreateWindowsJunctionOrSkip(sourceDirectory, externalDirectory);
			}
			else
			{
				File.Delete(sourcePath);
				File.CreateSymbolicLink(
					sourcePath,
					Path.Combine(externalDirectory, Path.GetFileName(sourcePath)));
			}
		});
		var destination = format == ProjectCopyExportFormat.Folder
			? workspace.DestinationParent
			: Path.Combine(workspace.DestinationParent, "alias-copy.zip");

		try
		{
			var exception = await Assert.ThrowsAsync<ProjectCopyExportException>(() =>
				workspace.ExportAsync(
					format,
					destination,
					[sourcePath],
					progress: progress,
					cancellationToken: TestContext.Current.CancellationToken));

			Assert.True(sourceReplaced);
			Assert.Equal(ProjectCopyExportError.SymbolicLinkNotSupported, exception.Error);
		}
		finally
		{
			if (OperatingSystem.IsWindows())
				DeleteDirectoryLink(sourceDirectory);
			else if (File.Exists(sourcePath))
				File.Delete(sourcePath);
		}

		void VerifyAliasSupport()
		{
			if (OperatingSystem.IsWindows())
			{
				var probe = Path.Combine(workspace.DestinationParent, "junction-probe");
				CreateWindowsJunctionOrSkip(probe, externalDirectory);
				DeleteDirectoryLink(probe);
				return;
			}

			var symlinkProbe = Path.Combine(workspace.DestinationParent, "symlink-probe");
			CreateFileLinkOrSkip(
				symlinkProbe,
				Path.Combine(externalDirectory, Path.GetFileName(sourcePath)));
			File.Delete(symlinkProbe);
		}
	}

	[Fact]
	public async Task RedactedFolderExport_CountsOnlyProjectEntriesAndDoesNotChangeSource()
	{
		const string secret = "ghp_" + "a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL";
		using var workspace = ProjectCopyWorkspace.Create();
		await File.WriteAllTextAsync(
			workspace.Paths["readme"],
			$"token={secret}\n",
			TestContext.Current.CancellationToken);
		var sourceText = await File.ReadAllBytesAsync(
			workspace.Paths["readme"],
			TestContext.Current.CancellationToken);
		var sourceBinary = await File.ReadAllBytesAsync(
			workspace.Paths["binary"],
			TestContext.Current.CancellationToken);
		var updates = new List<ProjectCopyExportProgress>();
		var service = new ProjectCopyExportService(
			new ProjectCopyExportPlanBuilder(),
			new FileContentAnalyzer(),
			new SecretRedactionSession(new GitleaksSecretDetector()));
		var destination = Path.Combine(workspace.DestinationParent, "redacted");

		var result = await service.ExportAsync(
			new ProjectCopyExportRequest(
				workspace.SourceRoot,
				"Sample",
				workspace.Root,
				new HashSet<string>(PathComparer.Default),
				destination,
				ProjectCopyExportFormat.Folder,
				ProjectCopyDestinationMode.Exact,
				ProjectCopyConflictPolicy.Fail,
				RedactSecrets: true),
			new CallbackProgress<ProjectCopyExportProgress>(updates.Add),
			TestContext.Current.CancellationToken);

		Assert.Equal(1, result.RedactedValueCount);
		Assert.False(File.Exists(Path.Combine(result.DestinationPath, "DEVPROJEX_REDACTIONS.txt")));
		Assert.DoesNotContain(
			secret,
			await File.ReadAllTextAsync(
				Path.Combine(result.DestinationPath, "README.md"),
				TestContext.Current.CancellationToken),
			StringComparison.Ordinal);
		Assert.Equal(
			sourceBinary,
			await File.ReadAllBytesAsync(
				Path.Combine(result.DestinationPath, "assets", "image.bin"),
				TestContext.Current.CancellationToken));
		Assert.Equal(result.CreatedDirectoryCount + result.CopiedFileCount, updates[^1].TotalEntryCount);
		Assert.Equal(updates[^1].TotalEntryCount, updates[^1].ProcessedEntryCount);
		Assert.Equal(100, updates[^1].Percentage);
		Assert.Equal(
			sourceText,
			await File.ReadAllBytesAsync(workspace.Paths["readme"], TestContext.Current.CancellationToken));
		Assert.Equal(
			sourceBinary,
			await File.ReadAllBytesAsync(workspace.Paths["binary"], TestContext.Current.CancellationToken));
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
	public void SharedDestinationGuardRejectsTextExportPathInsideSourceAndAllowsSibling()
	{
		using var workspace = ProjectCopyWorkspace.Create();
		var insideSource = Path.Combine(workspace.SourceRoot, "tree.txt");
		var outsideSource = Path.Combine(workspace.DestinationParent, "tree.txt");

		var exception = Assert.Throws<ProjectCopyExportException>(() =>
			ProjectCopyExportService.EnsureDestinationOutsideProject(
				workspace.SourceRoot,
				insideSource));
		ProjectCopyExportService.EnsureDestinationOutsideProject(
			workspace.SourceRoot,
			outsideSource);

		Assert.Equal(ProjectCopyExportError.DestinationInsideSource, exception.Error);
		Assert.False(File.Exists(insideSource));
	}

	[Fact]
	public void SharedDestinationGuardRejectsCaseAliasInsideSourceOnCaseInsensitiveVolume()
	{
		var testRoot = Environment.GetEnvironmentVariable(
			"DEVPROJEX_CASE_INSENSITIVE_TEST_ROOT");
		using var workspace = new TemporaryDirectory(testRoot);
		var source = workspace.CreateDirectory("ProjectCaseAlias");
		var caseAlias = Path.Combine(workspace.Path, "pROJECTcASEaLIAS");
		if (!Directory.Exists(caseAlias))
		{
			Assert.Skip(
				$"The test root is case-sensitive: {workspace.Path}. " +
				"Set DEVPROJEX_CASE_INSENSITIVE_TEST_ROOT to validate another mounted volume.");
		}

		var destination = Path.Combine(caseAlias, "report.txt");
		var exception = Assert.Throws<ProjectCopyExportException>(() =>
			ProjectCopyExportService.EnsureDestinationOutsideProject(
				source,
				destination));

		Assert.Equal(
			ProjectCopyExportError.UnsafeDestinationPath,
			exception.Error);
		Assert.False(File.Exists(destination));
	}

	[Fact]
	public void SharedDestinationGuardRejectsWindowsSubstAliasIntoSource()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("Windows SUBST aliases are only available on Windows.");

		using var workspace = new TemporaryDirectory();
		var source = workspace.CreateDirectory("SubstSource");
		var driveLetter = FindAvailableDriveLetter();
		if (driveLetter is null)
			Assert.Skip("No free drive letter is available for the SUBST regression.");

		var drive = $"{driveLetter}:";
		var createResult = RunSubst(drive, source);
		if (createResult != 0)
			Assert.Skip($"SUBST is unavailable in this environment (exit {createResult}).");

		var destination = Path.Combine($"{drive}\\", "report.txt");
		try
		{
			var exception = Assert.Throws<ProjectCopyExportException>(() =>
				ProjectCopyExportService.EnsureDestinationOutsideProject(
					source,
					destination));

			Assert.Equal(
				ProjectCopyExportError.UnsafeDestinationPath,
				exception.Error);
			Assert.False(File.Exists(Path.Combine(source, "report.txt")));
		}
		finally
		{
			Assert.Equal(0, RunSubst(drive, "/d"));
		}
	}

	[Fact]
	public void SharedDestinationGuardRejectsWindowsSubstAliasIntoSourceSubdirectory()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("Windows SUBST aliases are only available on Windows.");

		using var workspace = new TemporaryDirectory();
		var source = workspace.CreateDirectory("SubstSource");
		var sourceSubdirectory = workspace.CreateDirectory("SubstSource/nested");
		var driveLetter = FindAvailableDriveLetter();
		if (driveLetter is null)
			Assert.Skip("No free drive letter is available for the SUBST regression.");

		var drive = $"{driveLetter}:";
		var createResult = RunSubst(drive, sourceSubdirectory);
		if (createResult != 0)
			Assert.Skip($"SUBST is unavailable in this environment (exit {createResult}).");

		var destination = Path.Combine($"{drive}\\", "report.txt");
		try
		{
			var exception = Assert.Throws<ProjectCopyExportException>(() =>
				ProjectCopyExportService.EnsureDestinationOutsideProject(
					source,
					destination));

			Assert.Equal(
				ProjectCopyExportError.UnsafeDestinationPath,
				exception.Error);
			Assert.False(File.Exists(Path.Combine(sourceSubdirectory, "report.txt")));
		}
		finally
		{
			Assert.Equal(0, RunSubst(drive, "/d"));
		}
	}

	[Fact]
	public async Task AtomicFileReplacementDoesNotFollowExistingHardLinkIntoSource()
	{
		using var workspace = new TemporaryDirectory();
		var sourceRoot = workspace.CreateDirectory("source");
		var sourceFile = workspace.CreateFile("source/app.cs", "ORIGINAL");
		var outputDirectory = workspace.CreateDirectory("output");
		var destination = Path.Combine(outputDirectory, "report.txt");
		CreateHardLinkOrSkip(destination, sourceFile);

		var resolvedDestination = ProjectCopyExportService.ResolveDestinationOutsideProject(
			sourceRoot,
			destination);
		await AtomicFileOutput.WriteAsync(
			resolvedDestination,
			overwrite: true,
			async (stream, cancellationToken) =>
			{
				var payload = Encoding.UTF8.GetBytes("EXPORTED");
				await stream.WriteAsync(payload, cancellationToken);
			},
			TestContext.Current.CancellationToken,
			path => ProjectCopyExportService.ResolveDestinationOutsideProject(
				sourceRoot,
				path));

		Assert.Equal(
			"ORIGINAL",
			await File.ReadAllTextAsync(
				sourceFile,
				TestContext.Current.CancellationToken));
		Assert.Equal(
			"EXPORTED",
			await File.ReadAllTextAsync(
				destination,
				TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task AtomicFileReplacementAllowsConcurrentDeleteSharingReader()
	{
		using var workspace = new TemporaryDirectory();
		var destination = workspace.CreateFile("report.txt", "ORIGINAL");
		await using var reader = new FileStream(
			destination,
			FileMode.Open,
			FileAccess.Read,
			FileShare.ReadWrite | FileShare.Delete);

		await AtomicFileOutput.WriteAsync(
			destination,
			overwrite: true,
			async (stream, cancellationToken) =>
			{
				await stream.WriteAsync("REPLACED"u8.ToArray(), cancellationToken);
			},
			TestContext.Current.CancellationToken);

		using var originalReader = new StreamReader(
			reader,
			Encoding.UTF8,
			detectEncodingFromByteOrderMarks: false,
			leaveOpen: true);
		Assert.Equal("ORIGINAL", await originalReader.ReadToEndAsync(
			TestContext.Current.CancellationToken));
		Assert.Equal("REPLACED", await File.ReadAllTextAsync(
			destination,
			TestContext.Current.CancellationToken));
	}

	[Fact]
	public void ExistingDestinationFileLinkIntoSourceIsRejectedWithoutEffects()
	{
		using var workspace = new TemporaryDirectory();
		var sourceRoot = workspace.CreateDirectory("source");
		var sourceFile = workspace.CreateFile("source/app.cs", "ORIGINAL");
		var destination = Path.Combine(
			workspace.CreateDirectory("output"),
			"report.txt");
		CreateFileLinkOrSkip(destination, sourceFile);

		var exception = Assert.Throws<ProjectCopyExportException>(() =>
			ProjectCopyExportService.ResolveDestinationOutsideProject(
				sourceRoot,
				destination));

		Assert.Equal(
			ProjectCopyExportError.UnsafeDestinationPath,
			exception.Error);
		Assert.Equal("ORIGINAL", File.ReadAllText(sourceFile));
		Assert.NotNull(new FileInfo(destination).LinkTarget);
	}

	[Fact]
	public async Task AtomicFileReplacementReplacesSafeFileLinkWithoutChangingTarget()
	{
		using var workspace = new TemporaryDirectory();
		var sourceRoot = workspace.CreateDirectory("source");
		workspace.CreateFile("source/app.cs", "SOURCE");
		var safeTarget = workspace.CreateFile("safe/target.txt", "TARGET");
		var destination = Path.Combine(
			workspace.CreateDirectory("output"),
			"report.txt");
		CreateFileLinkOrSkip(destination, safeTarget);

		await AtomicFileOutput.WriteAsync(
			destination,
			overwrite: true,
			async (stream, cancellationToken) =>
			{
				var payload = Encoding.UTF8.GetBytes("EXPORTED");
				await stream.WriteAsync(payload, cancellationToken);
			},
			TestContext.Current.CancellationToken,
			path => ProjectCopyExportService.ResolveDestinationOutsideProject(
				sourceRoot,
				path));

		Assert.Equal(
			"TARGET",
			await File.ReadAllTextAsync(
				safeTarget,
				TestContext.Current.CancellationToken));
		Assert.Equal(
			"EXPORTED",
			await File.ReadAllTextAsync(
				destination,
				TestContext.Current.CancellationToken));
		Assert.Null(new FileInfo(destination).LinkTarget);
	}

	[Fact]
	public async Task AtomicFileForceRaceWithDirectoryReturnsConflictAndCleansStaging()
	{
		using var workspace = new TemporaryDirectory();
		var outputDirectory = workspace.CreateDirectory("output");
		var destination = Path.Combine(outputDirectory, "report.txt");
		var validationCount = 0;

		var exception = await Assert.ThrowsAsync<AtomicFileOutputConflictException>(
			() => AtomicFileOutput.WriteAsync(
				destination,
				overwrite: true,
				async (stream, cancellationToken) =>
				{
					await stream.WriteAsync(
						"payload"u8.ToArray(),
						cancellationToken);
				},
				TestContext.Current.CancellationToken,
				path =>
				{
					validationCount++;
					if (validationCount == 3)
						Directory.CreateDirectory(destination);
					return path;
				}));

		Assert.Equal(destination, exception.Path);
		Assert.Equal(3, validationCount);
		Assert.True(Directory.Exists(destination));
		Assert.Empty(Directory.EnumerateFiles(outputDirectory, ".*.tmp"));
	}

	[Fact]
	public async Task AtomicFileConflictDuringFirstRevalidationReportsStableRequestedAlias()
	{
		using var workspace = new TemporaryDirectory();
		var sourceRoot = workspace.CreateDirectory("source");
		workspace.CreateFile("source/app.cs", "SOURCE");
		var externalTarget = workspace.CreateDirectory("external-target");
		var alias = Path.Combine(workspace.Path, "stable-alias");
		if (OperatingSystem.IsWindows())
			CreateWindowsJunctionOrSkip(alias, externalTarget);
		else
			CreateDirectoryLinkOrSkip(alias, externalTarget);

		var requestedDestination = Path.Combine(alias, "report.txt");
		var physicalDestination = Path.Combine(externalTarget, "report.txt");
		var validationCount = 0;
		var writeInvoked = false;

		try
		{
			var exception = await Assert.ThrowsAsync<AtomicFileOutputConflictException>(
				() => AtomicFileOutput.WriteAsync(
					requestedDestination,
					overwrite: false,
					(_, _) =>
					{
						writeInvoked = true;
						return Task.CompletedTask;
					},
					TestContext.Current.CancellationToken,
					path =>
					{
						validationCount++;
						if (validationCount == 2)
							File.WriteAllText(physicalDestination, "EXISTING");

						return ExactFileOutputDestinationPolicy.Resolve(
							sourceRoot,
							path,
							overwrite: false);
					}));

			Assert.Equal(2, validationCount);
			Assert.False(writeInvoked);
			Assert.Equal(Path.GetFullPath(requestedDestination), exception.Path);
			Assert.Equal("EXISTING", File.ReadAllText(physicalDestination));
			Assert.Empty(Directory.EnumerateFiles(externalTarget, ".*.tmp"));
		}
		finally
		{
			DeleteDirectoryLink(alias);
		}
	}

	[Fact]
	public async Task AtomicFileCleanupRetriesTransientWindowsDeleteLock()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("Windows file-sharing semantics are required.");

		using var workspace = new TemporaryDirectory();
		var outputDirectory = workspace.CreateDirectory("output");
		var destination = Path.Combine(outputDirectory, "report.txt");
		var validationCount = 0;
		FileStream? heldStagingFile = null;
		Thread? releaseThread = null;

		try
		{
			var exception = await Assert.ThrowsAsync<IOException>(
				() => AtomicFileOutput.WriteAsync(
					destination,
					overwrite: false,
					async (stream, cancellationToken) =>
					{
						await stream.WriteAsync(
							"payload"u8.ToArray(),
							cancellationToken);
					},
					TestContext.Current.CancellationToken,
					path =>
					{
						validationCount++;
						if (validationCount != 3)
							return path;

						var stagingPath = Assert.Single(
							Directory.EnumerateFiles(outputDirectory, ".*.tmp"));
						heldStagingFile = new FileStream(
							stagingPath,
							FileMode.Open,
							FileAccess.Read,
							FileShare.Read);
						releaseThread = new Thread(() =>
						{
							Thread.Sleep(175);
							heldStagingFile.Dispose();
						}) { IsBackground = true };
						releaseThread.Start();
						return path;
					}));

			Assert.IsNotType<AtomicFileOutputCleanupException>(exception);
			Assert.NotNull(releaseThread);
			Assert.True(releaseThread.Join(TimeSpan.FromSeconds(5)));
			Assert.False(File.Exists(destination));
			Assert.Empty(Directory.EnumerateFiles(outputDirectory, ".*.tmp"));
		}
		finally
		{
			heldStagingFile?.Dispose();
		}
	}

	[Fact]
	public async Task AtomicFileCleanupReportsPermanentWindowsDeleteLock()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("Windows file-sharing semantics are required.");

		using var workspace = new TemporaryDirectory();
		var outputDirectory = workspace.CreateDirectory("output");
		var destination = Path.Combine(outputDirectory, "report.txt");
		var validationCount = 0;
		FileStream? heldStagingFile = null;

		try
		{
			var exception = await Assert.ThrowsAsync<AtomicFileOutputCleanupException>(
				() => AtomicFileOutput.WriteAsync(
					destination,
					overwrite: false,
					async (stream, cancellationToken) =>
					{
						await stream.WriteAsync(
							"payload"u8.ToArray(),
							cancellationToken);
					},
					TestContext.Current.CancellationToken,
					path =>
					{
						validationCount++;
						if (validationCount != 3)
							return path;

						var stagingPath = Assert.Single(
							Directory.EnumerateFiles(outputDirectory, ".*.tmp"));
						heldStagingFile = new FileStream(
							stagingPath,
							FileMode.Open,
							FileAccess.Read,
							FileShare.Read);
						return path;
					}));

			Assert.Equal(destination, exception.OutputPath);
			Assert.Equal(heldStagingFile!.Name, exception.TemporaryPath);
			Assert.IsType<IOException>(exception.OperationException);
			Assert.IsAssignableFrom<IOException>(exception.CleanupException);
			Assert.True(File.Exists(exception.TemporaryPath));
			Assert.False(File.Exists(destination));
		}
		finally
		{
			var stagingPath = heldStagingFile?.Name;
			heldStagingFile?.Dispose();
			if (stagingPath is not null)
				File.Delete(stagingPath);
		}
	}

	[Fact]
	public void SharedDestinationGuardAllowsCaseDistinctSiblingOnCaseSensitiveVolume()
	{
		using var workspace = new TemporaryDirectory();
		var source = workspace.CreateDirectory("ProjectCaseDistinct");
		var distinctSibling = Path.Combine(workspace.Path, "pROJECTcASEdISTINCT");
		if (Directory.Exists(distinctSibling))
		{
			Assert.Skip($"The test root is case-insensitive: {workspace.Path}.");
		}
		Directory.CreateDirectory(distinctSibling);
		var destination = Path.Combine(distinctSibling, "report.txt");

		ProjectCopyExportService.EnsureDestinationOutsideProject(
			source,
			destination);

		Assert.False(File.Exists(destination));
	}

	[Fact]
	public void SharedDestinationGuardSupportsUnixExecuteOnlyAncestorTraversal()
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip("POSIX execute-only directory traversal is unavailable on Windows.");
			return;
		}

		using var workspace = new TemporaryDirectory();
		var restrictedParent = workspace.CreateDirectory("traverse-only");
		var source = workspace.CreateDirectory("traverse-only/project");
		var destination = Path.Combine(workspace.Path, "output", "report.txt");
		var originalMode = File.GetUnixFileMode(restrictedParent);
		try
		{
			File.SetUnixFileMode(
				restrictedParent,
				UnixFileMode.UserExecute);

			ProjectCopyExportService.EnsureDestinationOutsideProject(
				source,
				destination);
		}
		finally
		{
			File.SetUnixFileMode(restrictedParent, originalMode);
		}

		Assert.False(File.Exists(destination));
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

	[Theory]
	[InlineData(ProjectCopyExportFormat.Folder, "submission")]
	[InlineData(ProjectCopyExportFormat.Zip, "submission.zip")]
	public async Task StableExternalDestinationAliasIsReportedForSuccessAndConflict(
		ProjectCopyExportFormat format,
		string outputName)
	{
		using var workspace = ProjectCopyWorkspace.Create();
		var externalTarget = Directory.CreateDirectory(
			Path.Combine(workspace.DestinationParent, "stable-target")).FullName;
		var alias = Path.Combine(workspace.DestinationParent, "stable-alias");
		CreateDirectoryLinkOrSkip(alias, externalTarget);
		var requestedDestination = Path.Combine(alias, outputName);
		var physicalDestination = Path.Combine(externalTarget, outputName);
		var request = new ProjectCopyExportRequest(
			workspace.SourceRoot,
			"Sample",
			workspace.Root,
			new HashSet<string>(PathComparer.Default),
			requestedDestination,
			format,
			ProjectCopyDestinationMode.Exact,
			ProjectCopyConflictPolicy.Fail);

		try
		{
			var service = new ProjectCopyExportService(new ProjectCopyExportPlanBuilder());

			var result = await service.ExportAsync(
				request,
				cancellationToken: TestContext.Current.CancellationToken);

			var authoritativePhysicalDestination =
				ProjectCopyExportService.ResolveDestinationOutsideProject(
					workspace.SourceRoot,
					physicalDestination);
			Assert.Equal(Path.GetFullPath(requestedDestination), result.DestinationPath);
			Assert.NotEqual(authoritativePhysicalDestination, result.DestinationPath);
			Assert.True(format == ProjectCopyExportFormat.Folder
				? File.Exists(Path.Combine(physicalDestination, "README.md"))
				: File.Exists(physicalDestination));

			var exception = await Assert.ThrowsAsync<ProjectCopyExportException>(() =>
				service.ExportAsync(
					request,
					cancellationToken: TestContext.Current.CancellationToken));

			Assert.Equal(ProjectCopyExportError.DestinationConflict, exception.Error);
			Assert.Equal(Path.GetFullPath(requestedDestination), exception.PathContext);
			Assert.Empty(FindStagingArtifacts(externalTarget));
		}
		finally
		{
			DeleteDirectoryLink(alias);
		}
	}

	[Theory]
	[InlineData(ProjectCopyExportFormat.Folder)]
	[InlineData(ProjectCopyExportFormat.Zip)]
	public async Task DestinationSymlinkRetargetedDuringExportCannotRedirectWritesIntoSource(
		ProjectCopyExportFormat format)
	{
		if (OperatingSystem.IsWindows())
			Assert.Skip("Atomic symbolic-link retargeting is covered by Unix runners.");

		using var workspace = ProjectCopyWorkspace.Create();
		var externalTarget = Directory.CreateDirectory(Path.Combine(workspace.DestinationParent, "stable-target")).FullName;
		var linkPath = Path.Combine(workspace.DestinationParent, "mutable-link");
		CreateDirectoryLinkOrSkip(linkPath, externalTarget);
		var retargeted = false;
		var progress = new CallbackProgress<ProjectCopyExportProgress>(value =>
		{
			if (retargeted || value.ProcessedEntryCount != 1)
				return;

			Directory.Delete(linkPath);
			Directory.CreateSymbolicLink(linkPath, workspace.SourceRoot);
			retargeted = true;
		});
		var destination = format == ProjectCopyExportFormat.Folder
			? linkPath
			: Path.Combine(linkPath, "retargeted.zip");

		try
		{
			var result = await workspace.ExportAsync(
				format,
				destination,
				[],
				progress: progress,
				cancellationToken: TestContext.Current.CancellationToken);

			Assert.True(retargeted);
			Assert.True(format == ProjectCopyExportFormat.Folder
				? File.Exists(Path.Combine(externalTarget, "Sample-copy", "README.md"))
				: File.Exists(Path.Combine(externalTarget, "retargeted.zip")));
			var physicalDestination = format == ProjectCopyExportFormat.Folder
				? Path.Combine(externalTarget, "Sample-copy")
				: Path.Combine(externalTarget, "retargeted.zip");
			var requestedReportedDestination = format == ProjectCopyExportFormat.Folder
				? Path.Combine(linkPath, "Sample-copy")
				: destination;
			var authoritativePhysicalDestination =
				ProjectCopyExportService.ResolveDestinationOutsideProject(
					workspace.SourceRoot,
					physicalDestination);
			Assert.Equal(authoritativePhysicalDestination, result.DestinationPath);
			Assert.NotEqual(
				Path.GetFullPath(requestedReportedDestination),
				result.DestinationPath);
			Assert.True(format == ProjectCopyExportFormat.Folder
				? Directory.Exists(physicalDestination)
				: File.Exists(physicalDestination));
			Assert.False(Directory.Exists(Path.Combine(workspace.SourceRoot, "Sample-copy")));
			Assert.False(File.Exists(Path.Combine(workspace.SourceRoot, "retargeted.zip")));
			Assert.Empty(FindStagingArtifacts(workspace.SourceRoot));
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

			Assert.Equal(
				Path.Combine(junctionPath, "Sample-copy"),
				result.DestinationPath);
			Assert.NotEqual(
				Path.Combine(externalTarget, "Sample-copy"),
				result.DestinationPath);
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
			if (value.ProcessedEntryCount == 1)
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
	public async Task CancellationRetriesWindowsStagingCleanupAfterTransientFileLock()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("Windows file deletion semantics are required for the transient-lock regression.");

		using var workspace = ProjectCopyWorkspace.Create();
		using var cancellation = new CancellationTokenSource();
		FileStream? heldFile = null;
		Thread? releaseThread = null;
		var firstFileProgress = CountDirectories(workspace.Root) + 1;
		var progress = new CallbackProgress<ProjectCopyExportProgress>(value =>
		{
			if (heldFile is not null || value.ProcessedEntryCount != firstFileProgress)
				return;

			var stagingPath = Assert.Single(FindStagingArtifacts(workspace.DestinationParent));
			var copiedFile = Assert.Single(Directory.EnumerateFiles(stagingPath, "*", SearchOption.AllDirectories));
			heldFile = new FileStream(copiedFile, FileMode.Open, FileAccess.Read, FileShare.Read);
			releaseThread = new Thread(() =>
			{
				Thread.Sleep(200);
				heldFile.Dispose();
			}) { IsBackground = true };
			releaseThread.Start();
			cancellation.Cancel();
		});

		try
		{
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
				workspace.ExportAsync(
					ProjectCopyExportFormat.Folder,
					workspace.DestinationParent,
					[],
					progress: progress,
					cancellationToken: cancellation.Token));
			Assert.NotNull(releaseThread);
			Assert.True(releaseThread.Join(TimeSpan.FromSeconds(5)));

			Assert.False(Directory.Exists(Path.Combine(workspace.DestinationParent, "Sample-copy")));
			Assert.Empty(FindStagingArtifacts(workspace.DestinationParent));
		}
		finally
		{
			heldFile?.Dispose();
		}
	}

	[Fact]
	public async Task CancellationReportsPermanentWindowsStagingCleanupFailure()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("Windows file deletion semantics are required.");

		using var workspace = ProjectCopyWorkspace.Create();
		using var cancellation = new CancellationTokenSource();
		FileStream? heldFile = null;
		string? stagingPath = null;
		var firstFileProgress = CountDirectories(workspace.Root) + 1;
		var progress = new CallbackProgress<ProjectCopyExportProgress>(value =>
		{
			if (heldFile is not null || value.ProcessedEntryCount != firstFileProgress)
				return;

			stagingPath = Assert.Single(FindStagingArtifacts(workspace.DestinationParent));
			var copiedFile = Assert.Single(
				Directory.EnumerateFiles(stagingPath, "*", SearchOption.AllDirectories));
			heldFile = new FileStream(
				copiedFile,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read);
			cancellation.Cancel();
		});

		try
		{
			var exception = await Assert.ThrowsAsync<ProjectCopyExportException>(
				() => workspace.ExportAsync(
					ProjectCopyExportFormat.Folder,
					workspace.DestinationParent,
					[],
					progress: progress,
					cancellationToken: cancellation.Token));

			Assert.Equal(
				ProjectCopyExportError.DestinationUnavailable,
				exception.Error);
			var aggregate = Assert.IsType<AggregateException>(
				exception.InnerException);
			Assert.Contains(
				aggregate.InnerExceptions,
				static inner => inner is OperationCanceledException);
			Assert.Contains(
				aggregate.InnerExceptions,
				static inner => inner is ProjectCopyExportException
				{
					Error: ProjectCopyExportError.DestinationUnavailable
				});
			Assert.NotNull(stagingPath);
			Assert.True(Directory.Exists(stagingPath));
			Assert.False(
				Directory.Exists(
					Path.Combine(workspace.DestinationParent, "Sample-copy")));
		}
		finally
		{
			heldFile?.Dispose();
			if (stagingPath is not null && Directory.Exists(stagingPath))
				Directory.Delete(stagingPath, recursive: true);
		}
	}

	[Theory]
	[InlineData(ProjectCopyExportFormat.Folder)]
	[InlineData(ProjectCopyExportFormat.Zip)]
	public async Task SourceDisappearingAfterStagingCreationLeavesNoPartialResult(
		ProjectCopyExportFormat format)
	{
		using var workspace = ProjectCopyWorkspace.Create();
		var sourceRemoved = false;
		var directoryCount = CountDirectories(workspace.Root);
		var progress = new CallbackProgress<ProjectCopyExportProgress>(value =>
		{
			if (sourceRemoved || value.ProcessedEntryCount != directoryCount)
				return;

			File.Delete(workspace.Paths["readme"]);
			sourceRemoved = true;
		});
		var destination = format == ProjectCopyExportFormat.Folder
			? workspace.DestinationParent
			: Path.Combine(workspace.DestinationParent, "read-failure.zip");

		var exception = await Assert.ThrowsAsync<ProjectCopyExportException>(() =>
			workspace.ExportAsync(
				format,
				destination,
				[],
				progress: progress,
				cancellationToken: TestContext.Current.CancellationToken));

		Assert.True(sourceRemoved);
		Assert.Equal(ProjectCopyExportError.SourceUnavailable, exception.Error);
		Assert.False(Directory.Exists(Path.Combine(workspace.DestinationParent, "Sample-copy")));
		Assert.False(File.Exists(Path.Combine(workspace.DestinationParent, "read-failure.zip")));
		Assert.Empty(FindStagingArtifacts(workspace.DestinationParent));
	}

	[Fact]
	public async Task ExistingZipRemainsUntouchedWhenExportIsCanceledBeforeAtomicReplace()
	{
		using var workspace = ProjectCopyWorkspace.Create();
		var destination = Path.Combine(workspace.DestinationParent, "existing.zip");
		var originalBytes = Encoding.UTF8.GetBytes("existing archive sentinel");
		await File.WriteAllBytesAsync(destination, originalBytes, TestContext.Current.CancellationToken);
		using var cancellation = new CancellationTokenSource();
		var progress = new CallbackProgress<ProjectCopyExportProgress>(_ => cancellation.Cancel());

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			workspace.ExportAsync(
				ProjectCopyExportFormat.Zip,
				destination,
				[],
				progress: progress,
				cancellationToken: cancellation.Token));

		Assert.Equal(originalBytes, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
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

	private static int CountDirectories(TreeNodeDescriptor root)
	{
		var count = 0;
		var pending = new Stack<TreeNodeDescriptor>();
		pending.Push(root);
		while (pending.TryPop(out var node))
		{
			if (!node.IsDirectory)
				continue;

			count++;
			foreach (var child in node.Children)
				pending.Push(child);
		}

		return count;
	}

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

	private static char? FindAvailableDriveLetter()
	{
		for (var driveLetter = 'Z'; driveLetter >= 'D'; driveLetter--)
		{
			if (!Directory.Exists($"{driveLetter}:\\"))
				return driveLetter;
		}

		return null;
	}

	private static int RunSubst(string drive, string target)
	{
		try
		{
			using var process = Process.Start(new ProcessStartInfo("subst.exe")
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				ArgumentList = { drive, target }
			});
			if (process is null || !process.WaitForExit(TimeSpan.FromSeconds(10)))
			{
				try
				{
					process?.Kill(entireProcessTree: true);
				}
				catch (InvalidOperationException)
				{
				}

				return -1;
			}

			return process.ExitCode;
		}
		catch (System.ComponentModel.Win32Exception)
		{
			return -1;
		}
	}

	private static void CreateFileLinkOrSkip(string linkPath, string targetPath)
	{
		try
		{
			File.CreateSymbolicLink(linkPath, targetPath);
			if (new FileInfo(linkPath).LinkTarget is null)
				Assert.Skip("The filesystem did not preserve the symbolic link.");
		}
		catch (Exception exception) when (exception is
			       UnauthorizedAccessException or
			       IOException or
			       PlatformNotSupportedException)
		{
			Assert.Skip($"File symbolic links are unavailable: {exception.GetType().Name}.");
		}
	}

	private static void CreateHardLinkOrSkip(string linkPath, string targetPath)
	{
		var startInfo = new ProcessStartInfo
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		if (OperatingSystem.IsWindows())
		{
			startInfo.FileName = "fsutil.exe";
			startInfo.ArgumentList.Add("hardlink");
			startInfo.ArgumentList.Add("create");
			startInfo.ArgumentList.Add(linkPath);
			startInfo.ArgumentList.Add(targetPath);
		}
		else
		{
			startInfo.FileName = "ln";
			startInfo.ArgumentList.Add(targetPath);
			startInfo.ArgumentList.Add(linkPath);
		}

		try
		{
			using var process = Process.Start(startInfo);
			if (process is null ||
			    !process.WaitForExit(TimeSpan.FromSeconds(10)) ||
			    process.ExitCode != 0 ||
			    !File.Exists(linkPath))
			{
				Assert.Skip("The test environment did not allow creating a hard link.");
			}
		}
		catch (Exception exception) when (exception is
			       InvalidOperationException or
			       IOException or
			       System.ComponentModel.Win32Exception)
		{
			Assert.Skip($"Hard-link creation is unavailable: {exception.GetType().Name}.");
		}
	}

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
