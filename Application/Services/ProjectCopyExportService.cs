using System.Buffers;
using System.IO.Compression;

namespace DevProjex.Application.Services;

public sealed class ProjectCopyExportService(ProjectCopyExportPlanBuilder planBuilder)
{
	private const int CopyBufferSize = 128 * 1024;
	private const int CleanupAttemptCount = 6;
	private const int CleanupInitialDelayMilliseconds = 25;

	public async Task<ProjectCopyExportResult> ExportAsync(
		ProjectCopyExportRequest request,
		IProgress<ProjectCopyExportProgress>? progress = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		try
		{
			var plan = planBuilder.Build(request);
			ValidateDestination(plan.ProjectRootPath, request.DestinationPath, request.Format);

			ValidateSources(plan, cancellationToken);
			return request.Format switch
			{
				ProjectCopyExportFormat.Folder => await ExportFolderAsync(
					plan,
					request.DestinationPath,
					request.DestinationMode,
					request.ConflictPolicy,
					progress,
					cancellationToken).ConfigureAwait(false),
				ProjectCopyExportFormat.Zip => await ExportZipAsync(
					plan,
					request.DestinationPath,
					request.DestinationMode,
					request.ConflictPolicy,
					progress,
					cancellationToken).ConfigureAwait(false),
				_ => throw new ProjectCopyExportException(
					ProjectCopyExportError.InvalidRequest,
					$"Unsupported project copy format: {request.Format}.")
			};
		}
		catch (Exception exception) when (exception is not OperationCanceledException and not ProjectCopyExportException)
		{
			throw exception switch
			{
				FileNotFoundException or DirectoryNotFoundException => ExportFailure(ProjectCopyExportError.SourceUnavailable, exception),
				UnauthorizedAccessException => ExportFailure(ProjectCopyExportError.AccessDenied, exception),
				IOException => ExportFailure(ProjectCopyExportError.IoFailure, exception),
				_ => ExportFailure(ProjectCopyExportError.UnexpectedFailure, exception)
			};
		}
	}

	public static void EnsureDestinationOutsideProject(string projectRootPath, string destinationPath)
	{
		try
		{
			_ = ResolveSafeDestinationOutsideSource(projectRootPath, destinationPath);
		}
		catch (ProjectCopyExportException)
		{
			throw;
		}
		catch (Exception exception) when (exception is
			       ArgumentException or
			       IOException or
			       UnauthorizedAccessException or
			       NotSupportedException)
		{
			throw UnsafeDestination(
				$"The destination path cannot be validated safely: {destinationPath}",
				exception);
		}
	}

	private static async Task<ProjectCopyExportResult> ExportFolderAsync(
		ProjectCopyExportPlan plan,
		string destinationPath,
		ProjectCopyDestinationMode destinationMode,
		ProjectCopyConflictPolicy conflictPolicy,
		IProgress<ProjectCopyExportProgress>? progress,
		CancellationToken cancellationToken)
	{
		if (conflictPolicy == ProjectCopyConflictPolicy.ReplaceAtomically)
		{
			throw new ProjectCopyExportException(
				ProjectCopyExportError.InvalidRequest,
				"Atomic replacement is not supported for folder export.");
		}

		var requestedPath = PathUtility.Normalize(destinationPath);
		var requestedDestinationParent = destinationMode == ProjectCopyDestinationMode.Exact
			? Path.GetDirectoryName(requestedPath)
			: requestedPath;
		if (string.IsNullOrWhiteSpace(requestedDestinationParent))
			throw DestinationUnavailable("The folder destination parent is unavailable.");

		var destinationParent = ResolveSafeDestinationOutsideSource(
			plan.ProjectRootPath,
			requestedDestinationParent);
		Directory.CreateDirectory(destinationParent);
		destinationParent = ResolveSafeDestinationOutsideSource(plan.ProjectRootPath, destinationParent);
		var preferredPath = destinationMode == ProjectCopyDestinationMode.Exact
			? Path.Combine(destinationParent, Path.GetFileName(requestedPath))
			: ResolveAvailableDirectoryPath(destinationParent, $"{plan.ProjectName}-copy");
		ValidateDestinationOutsideSource(plan.ProjectRootPath, preferredPath);
		if (destinationMode == ProjectCopyDestinationMode.Exact)
			EnsureDestinationDoesNotExist(preferredPath);

		// A sibling staging directory keeps the final rename atomic and prevents partial results from becoming visible.
		var stagingPath = Path.Combine(destinationParent, $".devprojex-{Guid.NewGuid():N}.tmp");
		var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
		var processedEntries = 0;
		var processedFiles = 0;
		long bytesWritten = 0;

		try
		{
			Directory.CreateDirectory(stagingPath);
			ValidateDestinationOutsideSource(plan.ProjectRootPath, stagingPath);
			foreach (var directory in plan.Entries.Where(static entry => entry.IsDirectory))
			{
				cancellationToken.ThrowIfCancellationRequested();
				var destination = ResolveDestinationPath(stagingPath, directory.RelativePath);
				Directory.CreateDirectory(destination);
				processedEntries++;
				ReportProgress(progress, processedEntries, plan.Entries.Count, bytesWritten);
			}

			foreach (var file in plan.Entries.Where(static entry => !entry.IsDirectory))
			{
				cancellationToken.ThrowIfCancellationRequested();
				var destination = ResolveDestinationPath(stagingPath, file.RelativePath);
				Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
				var copiedBytes = await CopyFileAsync(file.SourcePath, destination, buffer, cancellationToken)
					.ConfigureAwait(false);
				bytesWritten += copiedBytes;
				processedEntries++;
				processedFiles++;
				TryCopyLastWriteTime(file.SourcePath, destination);
				ReportProgress(progress, processedEntries, plan.Entries.Count, bytesWritten);
			}

			if (plan.Entries.Count == 0)
				ReportProgress(progress, 0, 0, 0);

			cancellationToken.ThrowIfCancellationRequested();
			var finalPath = destinationMode == ProjectCopyDestinationMode.Exact
				? MoveStagingDirectoryToExactPath(
					stagingPath,
					preferredPath,
					plan.ProjectRootPath)
				: MoveStagingDirectoryToAvailablePath(
					stagingPath,
					destinationParent,
					preferredPath,
					plan.ProjectName,
					plan.ProjectRootPath);
			var requestedFinalPath = destinationMode == ProjectCopyDestinationMode.Exact
				? requestedPath
				: Path.Combine(requestedDestinationParent, Path.GetFileName(finalPath));
			var reportedPath = ResolveReportedDestinationPath(requestedFinalPath, finalPath);
			return new ProjectCopyExportResult(reportedPath, processedFiles, plan.DirectoryCount, bytesWritten);
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
			await TryDeleteDirectoryAsync(stagingPath).ConfigureAwait(false);
		}
	}

	private static async Task<ProjectCopyExportResult> ExportZipAsync(
		ProjectCopyExportPlan plan,
		string destinationArchivePath,
		ProjectCopyDestinationMode destinationMode,
		ProjectCopyConflictPolicy conflictPolicy,
		IProgress<ProjectCopyExportProgress>? progress,
		CancellationToken cancellationToken)
	{
		var requestedDestinationPath = PathUtility.Normalize(destinationArchivePath);
		if (destinationMode == ProjectCopyDestinationMode.Exact &&
		    !requestedDestinationPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
		{
			throw new ProjectCopyExportException(
				ProjectCopyExportError.InvalidRequest,
				"The exact ZIP destination must use the .zip extension.");
		}

		if (destinationMode == ProjectCopyDestinationMode.AutomaticName)
			requestedDestinationPath = EnsureZipExtension(requestedDestinationPath);

		var requestedDestinationDirectory = Path.GetDirectoryName(requestedDestinationPath);
		if (string.IsNullOrWhiteSpace(requestedDestinationDirectory))
			throw new ProjectCopyExportException(ProjectCopyExportError.DestinationUnavailable, "The ZIP destination directory is unavailable.");

		ValidateDestinationOutsideSource(plan.ProjectRootPath, requestedDestinationPath);
		var destinationDirectory = ResolveSafeDestinationOutsideSource(
			plan.ProjectRootPath,
			requestedDestinationDirectory);
		Directory.CreateDirectory(destinationDirectory);
		destinationDirectory = ResolveSafeDestinationOutsideSource(plan.ProjectRootPath, destinationDirectory);
		var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(requestedDestinationPath));
		ValidateDestinationOutsideSource(plan.ProjectRootPath, destinationPath);
		if (destinationMode == ProjectCopyDestinationMode.Exact &&
		    conflictPolicy == ProjectCopyConflictPolicy.Fail)
		{
			EnsureDestinationDoesNotExist(destinationPath);
		}

		var stagingPath = Path.Combine(destinationDirectory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
		ValidateDestinationOutsideSource(plan.ProjectRootPath, stagingPath);
		var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
		var processedEntries = 0;
		var processedFiles = 0;
		long bytesWritten = 0;

		try
		{
			await using (var archiveStream = OpenDestinationFile(stagingPath))
			using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
			{
				foreach (var directory in plan.Entries.Where(static entry => entry.IsDirectory))
				{
					cancellationToken.ThrowIfCancellationRequested();
					var entryName = BuildZipEntryName(plan.ProjectName, directory.RelativePath, isDirectory: true);
					var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
					TrySetZipLastWriteTime(entry, directory.SourcePath);
					processedEntries++;
					ReportProgress(progress, processedEntries, plan.Entries.Count, bytesWritten);
				}

				foreach (var file in plan.Entries.Where(static entry => !entry.IsDirectory))
				{
					cancellationToken.ThrowIfCancellationRequested();
					var entryName = BuildZipEntryName(plan.ProjectName, file.RelativePath, isDirectory: false);
					var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
					TrySetZipLastWriteTime(entry, file.SourcePath);
					await using var source = OpenSourceFile(file.SourcePath);
					await using var destination = entry.Open();
					var copiedBytes = await CopyStreamAsync(source, destination, buffer, cancellationToken).ConfigureAwait(false);
					bytesWritten += copiedBytes;
					processedEntries++;
					processedFiles++;
					ReportProgress(progress, processedEntries, plan.Entries.Count, bytesWritten);
				}

				if (plan.Entries.Count == 0)
					ReportProgress(progress, 0, 0, 0);
			}

			cancellationToken.ThrowIfCancellationRequested();
			ValidateDestinationOutsideSource(plan.ProjectRootPath, stagingPath);
			ValidateDestinationOutsideSource(plan.ProjectRootPath, destinationPath);
			try
			{
				var overwrite = destinationMode == ProjectCopyDestinationMode.AutomaticName ||
				                conflictPolicy == ProjectCopyConflictPolicy.ReplaceAtomically;
				File.Move(stagingPath, destinationPath, overwrite);
			}
			catch (IOException exception) when (File.Exists(destinationPath) || Directory.Exists(destinationPath))
			{
				throw DestinationConflict(destinationPath, exception);
			}
			var reportedPath = ResolveReportedDestinationPath(requestedDestinationPath, destinationPath);
			return new ProjectCopyExportResult(reportedPath, processedFiles, plan.DirectoryCount, bytesWritten);
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
			await TryDeleteFileAsync(stagingPath).ConfigureAwait(false);
		}
	}

	private static void ValidateSources(ProjectCopyExportPlan plan, CancellationToken cancellationToken)
	{
		// The descriptor is authoritative, but every segment is revalidated to prevent a link from escaping the root.
		var validatedPaths = new HashSet<string>(PathComparer.Default);
		foreach (var entry in plan.Entries)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ValidateNoReparsePoints(plan.ProjectRootPath, entry.SourcePath, validatedPaths);

			if (entry.IsDirectory)
			{
				if (!Directory.Exists(entry.SourcePath))
					throw SourceUnavailable($"A source directory is no longer available: {entry.SourcePath}");
				continue;
			}

			if (!File.Exists(entry.SourcePath))
				throw SourceUnavailable($"A source file is no longer available: {entry.SourcePath}");

			_ = new FileInfo(entry.SourcePath).Length;
		}
	}

	private static void ValidateNoReparsePoints(string rootPath, string sourcePath, HashSet<string> validatedPaths)
	{
		var relativePath = Path.GetRelativePath(rootPath, sourcePath);
		var currentPath = rootPath;
		ValidatePathAttributes(currentPath, validatedPaths);

		if (relativePath == ".")
			return;

		foreach (var segment in relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
		{
			currentPath = Path.Combine(currentPath, segment);
			ValidatePathAttributes(currentPath, validatedPaths);
		}
	}

	private static void ValidatePathAttributes(string path, HashSet<string> validatedPaths)
	{
		if (!validatedPaths.Add(path))
			return;

		FileAttributes attributes;
		try
		{
			attributes = File.GetAttributes(path);
		}
		catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
		{
			throw SourceUnavailable($"A source path is no longer available: {path}", exception);
		}
		catch (UnauthorizedAccessException exception)
		{
			throw ExportFailure(ProjectCopyExportError.AccessDenied, exception);
		}
		catch (IOException exception)
		{
			throw ExportFailure(ProjectCopyExportError.IoFailure, exception);
		}

		if ((attributes & FileAttributes.ReparsePoint) != 0)
		{
			throw new ProjectCopyExportException(
				ProjectCopyExportError.SymbolicLinkNotSupported,
				$"Symbolic links and reparse points are not exported: {path}");
		}
	}

	private static void ValidateDestination(string rootPath, string destinationPath, ProjectCopyExportFormat format)
	{
		if (string.IsNullOrWhiteSpace(destinationPath))
			throw new ProjectCopyExportException(ProjectCopyExportError.DestinationUnavailable, "The destination path is required.");

		var normalizedDestination = PathUtility.Normalize(destinationPath);
		if (format == ProjectCopyExportFormat.Zip)
			normalizedDestination = EnsureZipExtension(normalizedDestination);

		ValidateDestinationOutsideSource(rootPath, normalizedDestination);
	}

	private static void EnsureDestinationDoesNotExist(string path)
	{
		if (Directory.Exists(path) || File.Exists(path))
			throw DestinationConflict(path);
	}

	private static void ValidateDestinationOutsideSource(string rootPath, string path)
	{
		EnsureDestinationOutsideProject(rootPath, path);
	}

	private static string ResolveSafeDestinationOutsideSource(string rootPath, string path)
	{
		var normalizedRoot = PathUtility.Normalize(rootPath);
		var normalizedDestination = PathUtility.Normalize(path);
		if (PathUtility.IsPathInside(normalizedDestination, normalizedRoot))
		{
			throw new ProjectCopyExportException(
				ProjectCopyExportError.DestinationInsideSource,
				"The project copy destination cannot be the source project or a path inside it.");
		}

		if (!Directory.Exists(normalizedRoot))
			throw SourceUnavailable($"The source project is no longer available: {normalizedRoot}");

		var canonicalRoot = ResolveCanonicalPath(normalizedRoot);
		var canonicalDestination = ResolveCanonicalPath(normalizedDestination);
		if (PathUtility.IsPathInside(canonicalDestination, canonicalRoot))
		{
			throw UnsafeDestination(
				$"The destination resolves to the source project or a path inside it: {normalizedDestination}");
		}

		return canonicalDestination;
	}

	private static string ResolveCanonicalPath(string path)
	{
		return ResolveCanonicalPath(path, new HashSet<string>(PathComparer.Default));
	}

	private static string ResolveCanonicalPath(string path, HashSet<string> resolvingLinks)
	{
		var normalizedPath = PathUtility.Normalize(path);
		var root = Path.GetPathRoot(normalizedPath);
		if (string.IsNullOrWhiteSpace(root))
			throw UnsafeDestination($"The destination path has no filesystem root: {normalizedPath}");

		var lexicalPath = PathUtility.Normalize(root);
		var canonicalPath = ResolveExistingPathSegment(lexicalPath, lexicalPath, resolvingLinks);
		var relativePath = Path.GetRelativePath(root, normalizedPath);
		if (relativePath == ".")
			return canonicalPath;

		var missingSegmentReached = false;
		foreach (var segment in relativePath.Split(
			         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
			         StringSplitOptions.RemoveEmptyEntries))
		{
			lexicalPath = Path.Combine(lexicalPath, segment);
			var nextCanonicalPath = Path.Combine(canonicalPath, segment);
			if (missingSegmentReached || !PathExists(lexicalPath))
			{
				missingSegmentReached = true;
				canonicalPath = nextCanonicalPath;
				continue;
			}

			canonicalPath = ResolveExistingPathSegment(lexicalPath, nextCanonicalPath, resolvingLinks);
		}

		return PathUtility.Normalize(canonicalPath);
	}

	private static string ResolveExistingPathSegment(
		string lexicalPath,
		string canonicalPath,
		HashSet<string> resolvingLinks)
	{
		try
		{
			var attributes = File.GetAttributes(lexicalPath);
			FileSystemInfo link = (attributes & FileAttributes.Directory) != 0
				? new DirectoryInfo(lexicalPath)
				: new FileInfo(lexicalPath);
			var target = link.ResolveLinkTarget(returnFinalTarget: false);
			if (target is null && (attributes & FileAttributes.ReparsePoint) == 0)
				return PathUtility.Normalize(canonicalPath);

			if (target is null)
				throw UnsafeDestination($"The destination link cannot be resolved safely: {lexicalPath}");

			var normalizedLinkPath = PathUtility.Normalize(lexicalPath);
			if (!resolvingLinks.Add(normalizedLinkPath))
				throw UnsafeDestination($"The destination path contains a symbolic-link cycle: {lexicalPath}");

			try
			{
				// A link target can itself contain aliased ancestors, such as macOS /var -> /private/var.
				// Re-walking the target is required before source/destination containment is compared.
				return ResolveCanonicalPath(target.FullName, resolvingLinks);
			}
			finally
			{
				resolvingLinks.Remove(normalizedLinkPath);
			}
		}
		catch (ProjectCopyExportException)
		{
			throw;
		}
		catch (Exception exception) when (exception is
			       FileNotFoundException or
			       DirectoryNotFoundException or
			       UnauthorizedAccessException or
			       IOException or
			       NotSupportedException)
		{
			throw UnsafeDestination($"The destination path cannot be resolved safely: {lexicalPath}", exception);
		}
	}

	private static bool PathExists(string path)
	{
		try
		{
			_ = File.GetAttributes(path);
			return true;
		}
		catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
		{
			return false;
		}
		catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or NotSupportedException)
		{
			throw UnsafeDestination($"The destination path cannot be inspected safely: {path}", exception);
		}
	}

	private static string ResolveDestinationPath(string stagingRoot, string relativePath)
	{
		var destination = string.IsNullOrEmpty(relativePath)
			? stagingRoot
			: Path.GetFullPath(Path.Combine(stagingRoot, relativePath));
		if (!PathUtility.IsPathInside(destination, stagingRoot))
			throw UnsafeDestination($"An export entry escapes the staging directory: {relativePath}");

		return destination;
	}

	private static string ResolveReportedDestinationPath(string requestedPath, string physicalPath)
	{
		try
		{
			var canonicalRequestedPath = ResolveCanonicalPath(requestedPath);
			var canonicalPhysicalPath = ResolveCanonicalPath(physicalPath);
			return PathComparer.Default.Equals(canonicalRequestedPath, canonicalPhysicalPath)
				? requestedPath
				: physicalPath;
		}
		catch (ProjectCopyExportException)
		{
			// The physical result remains authoritative if a destination alias changes after export.
			return physicalPath;
		}
	}

	private static string ResolveAvailableDirectoryPath(string parentPath, string baseName)
	{
		for (var suffix = 1; ; suffix++)
		{
			var name = suffix == 1 ? baseName : $"{baseName} ({suffix})";
			var candidate = Path.Combine(parentPath, name);
			if (!Directory.Exists(candidate) && !File.Exists(candidate))
				return candidate;
		}
	}

	private static string MoveStagingDirectoryToAvailablePath(
		string stagingPath,
		string destinationParent,
		string preferredPath,
		string projectName,
		string projectRootPath)
	{
		var candidate = preferredPath;
		while (true)
		{
			ValidateDestinationOutsideSource(projectRootPath, stagingPath);
			ValidateDestinationOutsideSource(projectRootPath, candidate);
			try
			{
				Directory.Move(stagingPath, candidate);
				return candidate;
			}
			catch (IOException) when (Directory.Exists(candidate) || File.Exists(candidate))
			{
				candidate = ResolveAvailableDirectoryPath(destinationParent, $"{projectName}-copy");
			}
		}
	}

	private static string MoveStagingDirectoryToExactPath(
		string stagingPath,
		string destinationPath,
		string projectRootPath)
	{
		ValidateDestinationOutsideSource(projectRootPath, stagingPath);
		ValidateDestinationOutsideSource(projectRootPath, destinationPath);
		EnsureDestinationDoesNotExist(destinationPath);
		try
		{
			Directory.Move(stagingPath, destinationPath);
			return destinationPath;
		}
		catch (IOException exception) when (Directory.Exists(destinationPath) || File.Exists(destinationPath))
		{
			throw DestinationConflict(destinationPath, exception);
		}
	}

	private static async Task<long> CopyFileAsync(
		string sourcePath,
		string destinationPath,
		byte[] buffer,
		CancellationToken cancellationToken)
	{
		await using var source = OpenSourceFile(sourcePath);
		await using var destination = OpenDestinationFile(destinationPath);
		return await CopyStreamAsync(source, destination, buffer, cancellationToken).ConfigureAwait(false);
	}

	private static async Task<long> CopyStreamAsync(
		Stream source,
		Stream destination,
		byte[] buffer,
		CancellationToken cancellationToken)
	{
		long copiedBytes = 0;
		while (true)
		{
			var read = await source.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken).ConfigureAwait(false);
			if (read == 0)
				return copiedBytes;

			await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
			copiedBytes += read;
		}
	}

	private static FileStream OpenSourceFile(string path) => new(
		path,
		FileMode.Open,
		FileAccess.Read,
		FileShare.Read,
		CopyBufferSize,
		FileOptions.Asynchronous | FileOptions.SequentialScan);

	private static FileStream OpenDestinationFile(string path) => new(
		path,
		FileMode.CreateNew,
		FileAccess.Write,
		FileShare.None,
		CopyBufferSize,
		FileOptions.Asynchronous | FileOptions.SequentialScan);

	private static string BuildZipEntryName(string projectName, string relativePath, bool isDirectory)
	{
		var normalizedRelative = relativePath.Replace('\\', '/');
		var name = string.IsNullOrEmpty(normalizedRelative)
			? projectName
			: $"{projectName}/{normalizedRelative}";
		return isDirectory ? $"{name.TrimEnd('/')}/" : name;
	}

	private static string EnsureZipExtension(string path) =>
		path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? path : $"{path}.zip";

	private static void ReportProgress(
		IProgress<ProjectCopyExportProgress>? progress,
		int processedEntries,
		int totalEntries,
		long bytesWritten)
	{
		var percentage = totalEntries == 0 ? 100 : processedEntries * 100d / totalEntries;
		progress?.Report(new ProjectCopyExportProgress(processedEntries, totalEntries, bytesWritten, percentage));
	}

	private static void TryCopyLastWriteTime(string sourcePath, string destinationPath)
	{
		try
		{
			File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourcePath));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			// Timestamps are optional metadata; copied file contents remain valid without them.
		}
	}

	private static void TrySetZipLastWriteTime(ZipArchiveEntry entry, string sourcePath)
	{
		try
		{
			entry.LastWriteTime = new DateTimeOffset(File.GetLastWriteTime(sourcePath));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			// ZIP timestamps have a narrower supported range than filesystem timestamps.
		}
	}

	private static async Task TryDeleteDirectoryAsync(string path)
	{
		for (var attempt = 1; attempt <= CleanupAttemptCount; attempt++)
		{
			try
			{
				if (Directory.Exists(path))
					Directory.Delete(path, recursive: true);
				return;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				if (attempt == CleanupAttemptCount)
					return;

				// Windows scanners can briefly retain a handle after the export stream closes.
				await Task.Delay(CleanupInitialDelayMilliseconds * attempt).ConfigureAwait(false);
			}
		}
	}

	private static async Task TryDeleteFileAsync(string path)
	{
		for (var attempt = 1; attempt <= CleanupAttemptCount; attempt++)
		{
			try
			{
				if (File.Exists(path))
					File.Delete(path);
				return;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				if (attempt == CleanupAttemptCount)
					return;

				await Task.Delay(CleanupInitialDelayMilliseconds * attempt).ConfigureAwait(false);
			}
		}
	}

	private static ProjectCopyExportException SourceUnavailable(string message, Exception? innerException = null) =>
		new(ProjectCopyExportError.SourceUnavailable, message, innerException);

	private static ProjectCopyExportException UnsafeDestination(string message, Exception? innerException = null) =>
		new(ProjectCopyExportError.UnsafeDestinationPath, message, innerException);

	private static ProjectCopyExportException DestinationUnavailable(string message, Exception? innerException = null) =>
		new(ProjectCopyExportError.DestinationUnavailable, message, innerException);

	private static ProjectCopyExportException DestinationConflict(string path, Exception? innerException = null) =>
		new(
			ProjectCopyExportError.DestinationConflict,
			$"The destination already exists: {path}",
			innerException,
			path);

	private static ProjectCopyExportException ExportFailure(ProjectCopyExportError error, Exception exception) =>
		new(error, $"Project copy export failed with {exception.GetType().Name}.", exception);
}
