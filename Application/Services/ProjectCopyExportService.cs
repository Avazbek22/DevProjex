using System.Buffers;
using System.IO.Compression;

namespace DevProjex.Application.Services;

public sealed class ProjectCopyExportService(ProjectCopyExportPlanBuilder planBuilder)
{
	private const int CopyBufferSize = 128 * 1024;

	public async Task<ProjectCopyExportResult> ExportAsync(
		ProjectCopyExportRequest request,
		IProgress<ProjectCopyExportProgress>? progress = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		var plan = planBuilder.Build(request);
		ValidateDestination(plan.ProjectRootPath, request.DestinationPath, request.Format);

		ValidateSources(plan, cancellationToken);
		return request.Format switch
		{
			ProjectCopyExportFormat.Folder => await ExportFolderAsync(
				plan, request.DestinationPath, progress, cancellationToken).ConfigureAwait(false),
			ProjectCopyExportFormat.Zip => await ExportZipAsync(
				plan, request.DestinationPath, progress, cancellationToken).ConfigureAwait(false),
			_ => throw new ProjectCopyExportException(
				ProjectCopyExportError.InvalidRequest,
				$"Unsupported project copy format: {request.Format}.")
		};
	}

	private static async Task<ProjectCopyExportResult> ExportFolderAsync(
		ProjectCopyExportPlan plan,
		string destinationParentPath,
		IProgress<ProjectCopyExportProgress>? progress,
		CancellationToken cancellationToken)
	{
		var destinationParent = PathUtility.Normalize(destinationParentPath);
		Directory.CreateDirectory(destinationParent);
		var preferredPath = ResolveAvailableDirectoryPath(destinationParent, $"{plan.ProjectName}-copy");
		// A sibling staging directory keeps the final rename atomic and prevents partial results from becoming visible.
		var stagingPath = Path.Combine(destinationParent, $".devprojex-{Guid.NewGuid():N}.tmp");
		var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
		var processedFiles = 0;
		long bytesWritten = 0;

		try
		{
			Directory.CreateDirectory(stagingPath);
			foreach (var directory in plan.Entries.Where(static entry => entry.IsDirectory))
			{
				cancellationToken.ThrowIfCancellationRequested();
				var destination = ResolveDestinationPath(stagingPath, directory.RelativePath);
				Directory.CreateDirectory(destination);
			}

			foreach (var file in plan.Entries.Where(static entry => !entry.IsDirectory))
			{
				cancellationToken.ThrowIfCancellationRequested();
				var destination = ResolveDestinationPath(stagingPath, file.RelativePath);
				Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
				var copiedBytes = await CopyFileAsync(file.SourcePath, destination, buffer, cancellationToken)
					.ConfigureAwait(false);
				bytesWritten += copiedBytes;
				processedFiles++;
				TryCopyLastWriteTime(file.SourcePath, destination);
				ReportProgress(progress, processedFiles, plan.FileCount, bytesWritten);
			}

			if (plan.FileCount == 0)
				ReportProgress(progress, 0, 0, 0);

			cancellationToken.ThrowIfCancellationRequested();
			var finalPath = MoveStagingDirectoryToAvailablePath(stagingPath, destinationParent, preferredPath, plan.ProjectName);
			return new ProjectCopyExportResult(finalPath, processedFiles, plan.DirectoryCount, bytesWritten);
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
			TryDeleteDirectory(stagingPath);
		}
	}

	private static async Task<ProjectCopyExportResult> ExportZipAsync(
		ProjectCopyExportPlan plan,
		string destinationArchivePath,
		IProgress<ProjectCopyExportProgress>? progress,
		CancellationToken cancellationToken)
	{
		var destinationPath = EnsureZipExtension(PathUtility.Normalize(destinationArchivePath));
		var destinationDirectory = Path.GetDirectoryName(destinationPath);
		if (string.IsNullOrWhiteSpace(destinationDirectory))
			throw new ProjectCopyExportException(ProjectCopyExportError.DestinationUnavailable, "The ZIP destination directory is unavailable.");

		Directory.CreateDirectory(destinationDirectory);
		var stagingPath = Path.Combine(destinationDirectory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
		var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
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
					processedFiles++;
					ReportProgress(progress, processedFiles, plan.FileCount, bytesWritten);
				}

				if (plan.FileCount == 0)
					ReportProgress(progress, 0, 0, 0);
			}

			cancellationToken.ThrowIfCancellationRequested();
			File.Move(stagingPath, destinationPath, overwrite: true);
			return new ProjectCopyExportResult(destinationPath, processedFiles, plan.DirectoryCount, bytesWritten);
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
			TryDeleteFile(stagingPath);
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
					throw UnsafeSource($"A source directory is no longer available: {entry.SourcePath}");
				continue;
			}

			if (!File.Exists(entry.SourcePath))
				throw UnsafeSource($"A source file is no longer available: {entry.SourcePath}");

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
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			throw UnsafeSource($"A source path cannot be inspected safely: {path}", exception);
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

		if (PathUtility.IsPathInside(normalizedDestination, rootPath))
		{
			throw new ProjectCopyExportException(
				ProjectCopyExportError.DestinationInsideSource,
				"The project copy destination cannot be the source project or a path inside it.");
		}
	}

	private static string ResolveDestinationPath(string stagingRoot, string relativePath)
	{
		var destination = string.IsNullOrEmpty(relativePath)
			? stagingRoot
			: Path.GetFullPath(Path.Combine(stagingRoot, relativePath));
		if (!PathUtility.IsPathInside(destination, stagingRoot))
			throw UnsafeSource($"An export entry escapes the staging directory: {relativePath}");

		return destination;
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
		string projectName)
	{
		var candidate = preferredPath;
		while (true)
		{
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
		int processedFiles,
		int totalFiles,
		long bytesWritten)
	{
		var percentage = totalFiles == 0 ? 100 : processedFiles * 100d / totalFiles;
		progress?.Report(new ProjectCopyExportProgress(processedFiles, totalFiles, bytesWritten, percentage));
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

	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path))
				Directory.Delete(path, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Best-effort cleanup must not hide the original export failure.
		}
	}

	private static void TryDeleteFile(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Best-effort cleanup must not hide the original export failure.
		}
	}

	private static ProjectCopyExportException UnsafeSource(string message, Exception? innerException = null) =>
		new(ProjectCopyExportError.UnsafeSourcePath, message, innerException);
}
