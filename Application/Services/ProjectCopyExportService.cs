using System.Buffers;
using System.IO.Compression;
using DevProjex.Application.Secrets;

namespace DevProjex.Application.Services;

public sealed class ProjectCopyExportService(
	ProjectCopyExportPlanBuilder planBuilder,
	IFileContentAnalyzer? contentAnalyzer = null,
	SecretRedactionSession? secretRedactionSession = null,
	CodeCompressionSession? codeCompressionSession = null)
{
	private const int CopyBufferSize = 128 * 1024;

	/// <summary>
	/// Named so it sorts to the top of a listing and cannot collide with a source file. It is never
	/// written for an untransformed copy.
	/// </summary>
	public const string TransformationNoticeFileName = "DEVPROJEX-NOTICE.txt";
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
			await using var prepared = request.RedactSecrets || request.RedactPrivateData || request.CompressCode ||
			                           request.StripComments || request.StripBlankLines
				? await PrepareRedactedOutputAsync(plan, request, cancellationToken).ConfigureAwait(false)
				: null;
			return request.Format switch
			{
				ProjectCopyExportFormat.Folder => await ExportFolderAsync(
					plan,
					request.DestinationPath,
					request.DestinationMode,
					request.ConflictPolicy,
					prepared,
					request.NoticeText,
					progress,
					cancellationToken).ConfigureAwait(false),
				ProjectCopyExportFormat.Zip => await ExportZipAsync(
					plan,
					request.DestinationPath,
					request.DestinationMode,
					request.ConflictPolicy,
					prepared,
					request.NoticeText,
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
				SecretScanLimitExceededException scanLimit => new ProjectCopyExportException(
					ProjectCopyExportError.SecretScanLimitExceeded,
					"Hide Secrets could not inspect a selected text file because it exceeds the supported scan limit.",
					scanLimit,
					scanLimit.Path),
				SecretDetectionException detection => new ProjectCopyExportException(
					ProjectCopyExportError.SecretDetectionFailed,
					"Hide Secrets could not inspect every selected text file. No project copy was created.",
					detection),
				FileNotFoundException or DirectoryNotFoundException => ExportFailure(ProjectCopyExportError.SourceUnavailable, exception),
				UnauthorizedAccessException => ExportFailure(ProjectCopyExportError.AccessDenied, exception),
				IOException => ExportFailure(ProjectCopyExportError.IoFailure, exception),
				_ => ExportFailure(ProjectCopyExportError.UnexpectedFailure, exception)
			};
		}
	}

	/// <summary>
	/// The notice a transformed copy carries in its own root. A folder or ZIP that is not a
	/// byte-for-byte copy of the project has to say so where it will be read - the confirmation the
	/// user clicked is gone by the time someone else opens the archive.
	///
	/// Returns null when nothing was transformed, so an untouched copy stays untouched.
	/// </summary>
	private static string? BuildTransformationNotice(
		PreparedSecretRedactionOutput? prepared,
		ProjectCopyExportPlan plan,
		ProjectCopyNoticeText? noticeText)
	{
		if (prepared is null || noticeText is null)
			return null;

		var lines = new List<string>(3);
		if (prepared.Snapshot is { } redaction && redaction.RedactedCount > 0)
			lines.Add(noticeText.Redaction);
		if (prepared.CompressionSnapshot is { CompressedFiles: > 0 })
			lines.Add(noticeText.Compression);
		// Named, not merely counted: a copy that is missing a file has to say which one, or the
		// reader cannot tell an omission from a file that was never in the project.
		if (ShouldExcludeUnscannable(prepared) && prepared.UnscannableFiles.Count > 0)
		{
			var entries = prepared.UnscannableFiles
				.Select(file => string.Concat(
					NormalizeNoticePath(plan.ProjectRootPath, file.Path),
					" - ",
					ResolveUnscannableReason(noticeText, file.Classification)))
				.OrderBy(static value => value, StringComparer.Ordinal);
			lines.Add(
				noticeText.ExcludedUnscannable +
				Environment.NewLine +
				string.Join(Environment.NewLine, entries.Select(static value => "  " + value)));
		}

		return lines.Count == 0
			? null
			: string.Join(Environment.NewLine + Environment.NewLine, lines) + Environment.NewLine;
	}

	/// <summary>
	/// A file the scanner never read is left out of a copy only when Hide Secrets is on. With
	/// compression alone there is no promise about its contents to keep, so it ships as it is.
	/// </summary>
	private static bool ShouldExcludeUnscannable(PreparedSecretRedactionOutput prepared) =>
		prepared.Snapshot is not null;

	private static bool IsExcludedFromCopy(
		PreparedSecretRedactionOutput? prepared,
		string sourcePath) =>
		prepared is not null &&
		ShouldExcludeUnscannable(prepared) &&
		prepared.GetFile(sourcePath).IsUnscannable;

	private static string NormalizeNoticePath(string projectRoot, string fullPath)
	{
		try
		{
			return PathUtility.GetPortableRelativePath(projectRoot, fullPath);
		}
		catch (ArgumentException)
		{
			return Path.GetFileName(fullPath);
		}
	}

	private async Task<PreparedSecretRedactionOutput?> PrepareRedactedOutputAsync(
		ProjectCopyExportPlan plan,
		ProjectCopyExportRequest request,
		CancellationToken cancellationToken)
	{
		if (contentAnalyzer is null ||
		    ((request.RedactSecrets || request.RedactPrivateData) && secretRedactionSession is null) ||
		    ((request.CompressCode || request.StripComments || request.StripBlankLines) &&
		     codeCompressionSession is null))
		{
			throw new ProjectCopyExportException(
				ProjectCopyExportError.InvalidRequest,
				"A content transformation was requested but its services were not configured.");
		}

		var files = plan.Entries
			.Where(static entry => !entry.IsDirectory)
			.Select(static entry => entry.SourcePath)
			.ToArray();
		var preparer = new SecretRedactionOutputPreparer(contentAnalyzer);
		var transformKinds = CodeTransformIdentity.Resolve(
			request.CompressCode,
			request.StripComments,
			request.StripBlankLines);
		var context = ContentTransformationContext.For(
			transformKinds != CodeTransformKinds.None && codeCompressionSession is not null
				? new CodeCompressionContext(plan.ProjectRootPath, codeCompressionSession, transformKinds)
				: null,
			CreateRedactionContext(plan.ProjectRootPath, request));
		return context is null
			? null
			: await preparer.PrepareAsync(context, files, cancellationToken).ConfigureAwait(false);
	}

	private static string ResolveUnscannableReason(
		ProjectCopyNoticeText noticeText,
		FileContentClassification classification) => classification switch
	{
		FileContentClassification.TooLarge => noticeText.TooLargeReason,
		FileContentClassification.UnsupportedEncoding => noticeText.UnsupportedEncodingReason,
		_ => throw new ArgumentOutOfRangeException(nameof(classification), classification, null)
	};

	private SecretRedactionContext? CreateRedactionContext(
		string projectRoot,
		ProjectCopyExportRequest request)
	{
		if (secretRedactionSession is null)
			return null;
		var features = SecretRedactionFeatureSelection.Resolve(
			request.RedactSecrets,
			request.RedactPrivateData);
		return features == SecretRedactionFeatures.None
			? null
			: new SecretRedactionContext(projectRoot, secretRedactionSession, features);
	}

	/// <summary>
	/// The notice lines a transformed project copy can carry, in the user's language. Reuses the
	/// wording the dry-run and the confirmation dialog already show, so the copy says the same thing
	/// the user was told before agreeing to it.
	/// </summary>
	public static ProjectCopyNoticeText BuildProjectCopyNoticeText(LocalizationService localization) =>
		new(
			localization["Terminal.DryRun.ProjectCopy.RedactionWarning"],
			localization["Compression.CopyNotice"],
			localization["ProjectCopy.Notice.UnscannableExcluded"],
			localization["Content.Redaction.Reason.TooLarge"],
			localization["Content.Redaction.Reason.UnsupportedEncoding"]);

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
		PreparedSecretRedactionOutput? prepared,
		ProjectCopyNoticeText? noticeText,
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
		EnsureDestinationDirectoryExists(destinationParent);
		destinationParent = ResolveSafeDestinationOutsideSource(plan.ProjectRootPath, destinationParent);
		var preferredPath = destinationMode == ProjectCopyDestinationMode.Exact
			? Path.Combine(destinationParent, Path.GetFileName(requestedPath))
			: ResolveAvailableDirectoryPath(destinationParent, $"{plan.ProjectName}-copy");
		ValidateDestinationOutsideSource(plan.ProjectRootPath, preferredPath);
		if (destinationMode == ProjectCopyDestinationMode.Exact)
			EnsureDestinationDoesNotExist(preferredPath, requestedPath);

		// A sibling staging directory keeps the final rename atomic and prevents partial results from becoming visible.
		var stagingPath = Path.Combine(destinationParent, $".devprojex-{Guid.NewGuid():N}.tmp");
		var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
		var processedEntries = 0;
		var processedFiles = 0;
		long bytesWritten = 0;
		var totalEntries = plan.Entries.Count;
		Exception? operationException = null;

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
				ReportProgress(progress, processedEntries, totalEntries, bytesWritten);
			}

			foreach (var file in plan.Entries.Where(static entry => !entry.IsDirectory))
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (IsExcludedFromCopy(prepared, file.SourcePath))
				{
					processedEntries++;
					ReportProgress(progress, processedEntries, totalEntries, bytesWritten);
					continue;
				}

				var destination = ResolveDestinationPath(stagingPath, file.RelativePath);
				Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
				var contentPath = prepared?.GetFile(file.SourcePath).ContentPath ?? file.SourcePath;
				var copiedBytes = await CopyFileAsync(contentPath, destination, buffer, cancellationToken)
					.ConfigureAwait(false);
				bytesWritten += copiedBytes;
				processedEntries++;
				processedFiles++;
				TryCopyLastWriteTime(file.SourcePath, destination);
				ReportProgress(progress, processedEntries, totalEntries, bytesWritten);
			}

			if (totalEntries == 0)
				ReportProgress(progress, 0, 0, 0);

			cancellationToken.ThrowIfCancellationRequested();
			if (BuildTransformationNotice(prepared, plan, noticeText) is { } notice)
			{
				await File.WriteAllTextAsync(
						Path.Combine(stagingPath, TransformationNoticeFileName),
						notice,
						new UTF8Encoding(false),
						cancellationToken)
					.ConfigureAwait(false);
			}

			var finalPath = destinationMode == ProjectCopyDestinationMode.Exact
				? MoveStagingDirectoryToExactPath(
					stagingPath,
					preferredPath,
					requestedPath,
					plan.ProjectRootPath,
					cancellationToken)
				: MoveStagingDirectoryToAvailablePath(
					stagingPath,
					destinationParent,
					preferredPath,
					plan.ProjectName,
					plan.ProjectRootPath,
					cancellationToken);
			var requestedFinalPath = destinationMode == ProjectCopyDestinationMode.Exact
				? requestedPath
				: Path.Combine(requestedDestinationParent, Path.GetFileName(finalPath));
			var reportedPath = ResolveReportedDestinationPath(requestedFinalPath, finalPath);
			return new ProjectCopyExportResult(
				reportedPath,
				processedFiles,
				plan.DirectoryCount,
				bytesWritten,
				prepared?.Snapshot?.RedactedCount ?? 0,
				prepared?.UnscannableFiles ?? []);
		}
		catch (Exception exception)
		{
			operationException = exception;
			throw;
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
			try
			{
				await DeleteStagingDirectoryAsync(stagingPath).ConfigureAwait(false);
			}
			catch (ProjectCopyExportException cleanupException) when (operationException is not null)
			{
				throw DestinationUnavailable(
					cleanupException.Message,
					new AggregateException(operationException, cleanupException));
			}
		}
	}

	private static async Task<ProjectCopyExportResult> ExportZipAsync(
		ProjectCopyExportPlan plan,
		string destinationArchivePath,
		ProjectCopyDestinationMode destinationMode,
		ProjectCopyConflictPolicy conflictPolicy,
		PreparedSecretRedactionOutput? prepared,
		ProjectCopyNoticeText? noticeText,
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
		EnsureDestinationDirectoryExists(destinationDirectory);
		destinationDirectory = ResolveSafeDestinationOutsideSource(plan.ProjectRootPath, destinationDirectory);
		var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(requestedDestinationPath));
		ValidateDestinationOutsideSource(plan.ProjectRootPath, destinationPath);
		if (destinationMode == ProjectCopyDestinationMode.Exact &&
		    conflictPolicy == ProjectCopyConflictPolicy.Fail)
		{
			EnsureDestinationDoesNotExist(destinationPath, requestedDestinationPath);
		}

		var stagingPath = Path.Combine(destinationDirectory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
		ValidateDestinationOutsideSource(plan.ProjectRootPath, stagingPath);
		var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
		var processedEntries = 0;
		var processedFiles = 0;
		long bytesWritten = 0;
		var totalEntries = plan.Entries.Count;
		Exception? operationException = null;

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
					ReportProgress(progress, processedEntries, totalEntries, bytesWritten);
				}

				foreach (var file in plan.Entries.Where(static entry => !entry.IsDirectory))
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (IsExcludedFromCopy(prepared, file.SourcePath))
					{
						processedEntries++;
						ReportProgress(progress, processedEntries, totalEntries, bytesWritten);
						continue;
					}

					var entryName = BuildZipEntryName(plan.ProjectName, file.RelativePath, isDirectory: false);
					var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
					TrySetZipLastWriteTime(entry, file.SourcePath);
					var contentPath = prepared?.GetFile(file.SourcePath).ContentPath ?? file.SourcePath;
					await using var source = OpenSourceFile(contentPath);
					await using var destination = entry.Open();
					var copiedBytes = await CopyStreamAsync(source, destination, buffer, cancellationToken).ConfigureAwait(false);
					bytesWritten += copiedBytes;
					processedEntries++;
					processedFiles++;
					ReportProgress(progress, processedEntries, totalEntries, bytesWritten);
				}

				if (totalEntries == 0)
					ReportProgress(progress, 0, 0, 0);

				if (BuildTransformationNotice(prepared, plan, noticeText) is { } notice)
				{
					var noticeEntry = archive.CreateEntry(
						BuildZipEntryName(plan.ProjectName, TransformationNoticeFileName, isDirectory: false),
						CompressionLevel.Optimal);
					await using var noticeStream = noticeEntry.Open();
					await using var noticeWriter = new StreamWriter(noticeStream, new UTF8Encoding(false));
					await noticeWriter.WriteAsync(notice.AsMemory(), cancellationToken).ConfigureAwait(false);
				}
			}

			cancellationToken.ThrowIfCancellationRequested();
			ValidateDestinationOutsideSource(plan.ProjectRootPath, stagingPath);
			ValidateDestinationOutsideSource(plan.ProjectRootPath, destinationPath);
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				var overwrite = destinationMode == ProjectCopyDestinationMode.AutomaticName ||
				                conflictPolicy == ProjectCopyConflictPolicy.ReplaceAtomically;
				File.Move(stagingPath, destinationPath, overwrite);
			}
			catch (IOException exception) when (Path.Exists(destinationPath))
			{
				throw DestinationConflict(
					ResolveReportedDestinationPath(
						requestedDestinationPath,
						destinationPath),
					exception);
			}
			var reportedPath = ResolveReportedDestinationPath(requestedDestinationPath, destinationPath);
			return new ProjectCopyExportResult(
				reportedPath,
				processedFiles,
				plan.DirectoryCount,
				bytesWritten,
				prepared?.Snapshot?.RedactedCount ?? 0,
				prepared?.UnscannableFiles ?? []);
		}
		catch (Exception exception)
		{
			operationException = exception;
			throw;
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
			try
			{
				await DeleteStagingFileAsync(stagingPath).ConfigureAwait(false);
			}
			catch (ProjectCopyExportException cleanupException) when (operationException is not null)
			{
				throw DestinationUnavailable(
					cleanupException.Message,
					new AggregateException(operationException, cleanupException));
			}
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

	private static void EnsureDestinationDoesNotExist(
		string path,
		string? requestedPath = null)
	{
		if (Path.Exists(path))
		{
			throw DestinationConflict(
				requestedPath is null
					? path
					: ResolveReportedDestinationPath(requestedPath, path));
		}
	}

	private static void EnsureDestinationDirectoryExists(string path)
	{
		if (!Directory.Exists(path))
		{
			throw DestinationUnavailable(
				$"The destination parent directory does not exist: {path}");
		}
	}

	private static void ValidateDestinationOutsideSource(string rootPath, string path)
	{
		EnsureDestinationOutsideProject(rootPath, path);
	}

	private static string ResolveSafeDestinationOutsideSource(string rootPath, string path)
	{
		var normalizedRoot = PathUtility.Normalize(rootPath);
		var normalizedDestination = PathUtility.Normalize(path);
		if (IsPathInsideOrdinal(normalizedDestination, normalizedRoot))
		{
			throw new ProjectCopyExportException(
				ProjectCopyExportError.DestinationInsideSource,
				"The project copy destination cannot be the source project or a path inside it.");
		}

		if (!Directory.Exists(normalizedRoot))
			throw SourceUnavailable($"The source project is no longer available: {normalizedRoot}");

		var canonicalRoot = ResolveCanonicalPath(normalizedRoot);
		var canonicalDestination = ResolveCanonicalPath(normalizedDestination);
		if (IsPathInsideOrdinal(canonicalDestination, canonicalRoot))
		{
			throw UnsafeDestination(
				$"The destination resolves to the source project or a path inside it: {normalizedDestination}");
		}
		EnsureDestinationIdentityOutsideSource(
			canonicalRoot,
			canonicalDestination,
			normalizedDestination);

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

			var normalizedLinkPath = PathUtility.Normalize(canonicalPath);
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

	public static string ResolveDestinationOutsideProject(
		string projectRootPath,
		string destinationPath)
	{
		try
		{
			var normalizedDestination = PathUtility.Normalize(destinationPath);
			_ = ResolveSafeDestinationOutsideSource(
				projectRootPath,
				normalizedDestination);
			var destinationParent = Path.GetDirectoryName(normalizedDestination);
			if (string.IsNullOrWhiteSpace(destinationParent))
				throw UnsafeDestination($"The destination parent is unavailable: {normalizedDestination}");

			var resolvedParent = ResolveSafeDestinationOutsideSource(
				projectRootPath,
				destinationParent);
			EnsureDestinationDirectoryExists(resolvedParent);
			return Path.Combine(
				resolvedParent,
				Path.GetFileName(normalizedDestination));
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
				$"The destination path cannot be resolved safely: {destinationPath}",
				exception);
		}
	}

	private static bool IsPathInsideOrdinal(
		string candidatePath,
		string rootPath)
	{
		var normalizedCandidate = PathUtility.Normalize(candidatePath);
		var normalizedRoot = PathUtility.Normalize(rootPath);
		if (normalizedCandidate.Equals(normalizedRoot, StringComparison.Ordinal))
			return true;

		var rootPrefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
			? normalizedRoot
			: normalizedRoot + Path.DirectorySeparatorChar;
		return normalizedCandidate.StartsWith(rootPrefix, StringComparison.Ordinal);
	}

	private static void EnsureDestinationIdentityOutsideSource(
		string canonicalRoot,
		string canonicalDestination,
		string requestedDestination)
	{
		if (!OperatingSystem.IsWindows() &&
		    !OperatingSystem.IsLinux() &&
		    !OperatingSystem.IsMacOS())
		{
			return;
		}

		if (!FileSystemPathIdentity.TryEnumerateMountPointsInside(
			    canonicalRoot,
			    out var nestedMountPoints))
		{
			throw UnsafeDestination(
				$"The source filesystem boundaries cannot be established safely: {canonicalRoot}");
		}

		var protectedPaths = new List<string>(nestedMountPoints.Count + 1)
		{
			canonicalRoot
		};
		protectedPaths.AddRange(nestedMountPoints);
		var protectedBoundaries = new List<ProtectedSourceBoundary>(
			protectedPaths.Count);
		foreach (var protectedPath in protectedPaths)
		{
			if (!FileSystemPathIdentity.TryRead(
				    protectedPath,
				    out var protectedIdentity))
			{
				throw UnsafeDestination(
					$"The source path identity cannot be established safely: {protectedPath}");
			}
			if (!FileSystemPathIdentity.TryReadLocation(
				    protectedPath,
				    out var protectedLocation))
			{
				throw UnsafeDestination(
					$"The source filesystem location cannot be established safely: {protectedPath}");
			}

			protectedBoundaries.Add(new ProtectedSourceBoundary(
				protectedPath,
				protectedIdentity,
				protectedLocation));
		}

		var current = ResolveNearestExistingPath(canonicalDestination);
		if (current is not null)
		{
			if (!FileSystemPathIdentity.TryReadLocation(current, out var destinationLocation))
			{
				throw UnsafeDestination(
					$"The destination filesystem location cannot be established safely: {current}");
			}
			if (protectedBoundaries.Any(boundary =>
				    boundary.Location.NamespaceId.Equals(
					    destinationLocation.NamespaceId,
					    StringComparison.Ordinal) &&
				    IsLocationInsideOrdinal(
					    destinationLocation.CanonicalPath,
					    boundary.Location.CanonicalPath)))
			{
				throw UnsafeDestination(
					$"The destination resolves to the source project or a path inside it: {requestedDestination}");
			}

			if (!FileSystemPathIdentity.TryRead(
				    current,
				    out var destinationIdentity))
			{
				throw UnsafeDestination(
					$"The destination path identity cannot be established safely: {current}");
			}
			foreach (var boundary in protectedBoundaries)
			{
				if (!TryResolveEquivalentSourcePath(
					    boundary.Location,
					    destinationLocation,
					    boundary.Path,
					    out var equivalentSourcePath) ||
				    !FileSystemPathIdentity.TryRead(
					    equivalentSourcePath,
					    out var equivalentSourceIdentity) ||
				    equivalentSourceIdentity != destinationIdentity)
				{
					continue;
				}

				throw UnsafeDestination(
					$"The destination resolves to the source project or a path inside it: {requestedDestination}");
			}
		}

		while (current is not null)
		{
			if (!FileSystemPathIdentity.TryRead(current, out var currentIdentity))
			{
				throw UnsafeDestination(
					$"The destination path identity cannot be established safely: {current}");
			}
			if (protectedBoundaries.Any(boundary =>
				    currentIdentity == boundary.Identity))
			{
				throw UnsafeDestination(
					$"The destination resolves to the source project or a path inside it: {requestedDestination}");
			}

			current = GetParentPath(current);
		}
	}

	private readonly record struct ProtectedSourceBoundary(
		string Path,
		FileSystemPathIdentity Identity,
		FileSystemPathLocation Location);

	private static bool IsLocationInsideOrdinal(
		string candidatePath,
		string rootPath)
	{
		var normalizedCandidate = candidatePath.TrimEnd(
			Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar,
			'\\',
			'/');
		var normalizedRoot = rootPath.TrimEnd(
			Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar,
			'\\',
			'/');
		if (normalizedCandidate.Equals(normalizedRoot, StringComparison.Ordinal))
			return true;
		if (!normalizedCandidate.StartsWith(normalizedRoot, StringComparison.Ordinal) ||
		    normalizedCandidate.Length <= normalizedRoot.Length)
		{
			return false;
		}

		var next = normalizedCandidate[normalizedRoot.Length];
		return next is '\\' or '/';
	}

	private static bool TryResolveEquivalentSourcePath(
		FileSystemPathLocation sourceLocation,
		FileSystemPathLocation destinationLocation,
		string canonicalRoot,
		out string equivalentSourcePath)
	{
		equivalentSourcePath = string.Empty;
		if (!sourceLocation.NamespaceId.Equals(
			    destinationLocation.NamespaceId,
			    StringComparison.Ordinal))
		{
			return false;
		}

		var separators = new[]
		{
			Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar
		};
		var sourceSegments = sourceLocation.CanonicalPath.Split(
			separators,
			StringSplitOptions.RemoveEmptyEntries);
		var destinationSegments = destinationLocation.CanonicalPath.Split(
			separators,
			StringSplitOptions.RemoveEmptyEntries);
		if (destinationSegments.Length < sourceSegments.Length)
			return false;

		for (var index = 0; index < sourceSegments.Length; index++)
		{
			var sourceSegment = sourceSegments[index].Normalize(NormalizationForm.FormC);
			var destinationSegment = destinationSegments[index].Normalize(NormalizationForm.FormC);
			if (!sourceSegment.Equals(
				    destinationSegment,
				    StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
		}

		equivalentSourcePath = canonicalRoot;
		for (var index = sourceSegments.Length;
		     index < destinationSegments.Length;
		     index++)
		{
			equivalentSourcePath = Path.Combine(
				equivalentSourcePath,
				destinationSegments[index]);
		}

		return true;
	}

	private static string? ResolveNearestExistingPath(string path)
	{
		string? current = path;
		while (current is not null)
		{
			try
			{
				_ = File.GetAttributes(current);
				return current;
			}
			catch (Exception exception) when (exception is
				       FileNotFoundException or
				       DirectoryNotFoundException)
			{
			}
			catch (Exception exception) when (exception is
				       UnauthorizedAccessException or
				       IOException or
				       NotSupportedException)
			{
				throw UnsafeDestination(
					$"The destination path cannot be inspected safely: {current}",
					exception);
			}

			current = GetParentPath(current);
		}

		return null;
	}

	private static string? GetParentPath(string path)
	{
		var parent = Directory.GetParent(path)?.FullName;
		return parent is null || parent.Equals(path, StringComparison.Ordinal)
			? null
			: parent;
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

	public static string ResolveReportedDestinationPath(string requestedPath, string physicalPath)
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
			if (!Path.Exists(candidate))
				return candidate;
		}
	}

	private static string MoveStagingDirectoryToAvailablePath(
		string stagingPath,
		string destinationParent,
		string preferredPath,
		string projectName,
		string projectRootPath,
		CancellationToken cancellationToken)
	{
		var candidate = preferredPath;
		while (true)
		{
			ValidateDestinationOutsideSource(projectRootPath, stagingPath);
			ValidateDestinationOutsideSource(projectRootPath, candidate);
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				Directory.Move(stagingPath, candidate);
				return candidate;
			}
			catch (IOException) when (Path.Exists(candidate))
			{
				candidate = ResolveAvailableDirectoryPath(destinationParent, $"{projectName}-copy");
			}
		}
	}

	private static string MoveStagingDirectoryToExactPath(
		string stagingPath,
		string destinationPath,
		string requestedDestinationPath,
		string projectRootPath,
		CancellationToken cancellationToken)
	{
		ValidateDestinationOutsideSource(projectRootPath, stagingPath);
		ValidateDestinationOutsideSource(projectRootPath, destinationPath);
		EnsureDestinationDoesNotExist(destinationPath, requestedDestinationPath);
		cancellationToken.ThrowIfCancellationRequested();
		try
		{
			Directory.Move(stagingPath, destinationPath);
			return destinationPath;
		}
		catch (IOException exception) when (Path.Exists(destinationPath))
		{
			throw DestinationConflict(
				ResolveReportedDestinationPath(
					requestedDestinationPath,
					destinationPath),
				exception);
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
		var normalizedRelative = PathUtility.NormalizeSeparators(relativePath);
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

	private static async Task DeleteStagingDirectoryAsync(string path)
	{
		Exception? cleanupException = null;
		for (var attempt = 1; attempt <= CleanupAttemptCount; attempt++)
		{
			try
			{
				Directory.Delete(path, recursive: true);
				return;
			}
			catch (DirectoryNotFoundException)
			{
				return;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				cleanupException = exception;
				if (attempt == CleanupAttemptCount)
					break;

				// Windows scanners can briefly retain a handle after the export stream closes.
				await Task.Delay(CleanupInitialDelayMilliseconds * attempt).ConfigureAwait(false);
			}
		}

		throw DestinationUnavailable(
			$"The temporary project export directory could not be removed: {path}",
			cleanupException);
	}

	private static async Task DeleteStagingFileAsync(string path)
	{
		Exception? cleanupException = null;
		for (var attempt = 1; attempt <= CleanupAttemptCount; attempt++)
		{
			try
			{
				File.Delete(path);
				return;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				cleanupException = exception;
				if (attempt == CleanupAttemptCount)
					break;

				await Task.Delay(CleanupInitialDelayMilliseconds * attempt).ConfigureAwait(false);
			}
		}

		throw DestinationUnavailable(
			$"The temporary project export file could not be removed: {path}",
			cleanupException);
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
