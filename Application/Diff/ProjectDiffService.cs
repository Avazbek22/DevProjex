using DevProjex.Application.Secrets;

namespace DevProjex.Application.Diff;

/// <summary>
/// Compares the old and new version of every file in a Git-scoped selection. Both versions pass
/// the same redaction before any line is compared, inside one redaction scope for the whole
/// result, so one value gets one placeholder in either version and an unchanged line holding a
/// secret compares as unchanged. Nothing outside the plan's files is read or named.
/// </summary>
public sealed class ProjectDiffService(IGitFileVersionReader versions)
{
	// Keeps cache entries for committed text apart from the working-file scans other reads reuse.
	private const string RedactionTransformIdentity = "\u001fgit-file-version-v1";

	public Task<GitRevisionAvailability> ProbeRangeAsync(
		string projectRoot,
		string diffRange,
		CancellationToken cancellationToken = default) =>
		versions.ProbeRangeAsync(projectRoot, diffRange, cancellationToken);

	public async Task<ProjectDiffResult> CompareAsync(
		ProjectDiffRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentOutOfRangeException.ThrowIfNegative(request.ContextLines);
		if (!GitScopeSelection.IsMomentary(request.Mode))
			throw new ArgumentOutOfRangeException(nameof(request), request.Mode, "A changes, staged or diff scope is required.");

		var plan = request.Plan;
		var files = plan.IncludedFiles.Order(StringComparer.Ordinal).ToArray();
		var deletedOutsideSelection = plan.Diagnostics
			.Where(static diagnostic => diagnostic.Code == GitScopeFilter.DeletedDiagnosticCode)
			.Sum(static diagnostic => diagnostic.Count ?? 0);
		var notInWorkingTree = plan.GitScopeFilesNotInWorkingTree;
		if (files.Length == 0)
			return new ProjectDiffResult(request.Mode, request.DiffRange, [], deletedOutsideSelection, null, notInWorkingTree);

		var stored = await versions
			.ReadAsync(new GitFileVersionRequest(plan.SourceRoot, request.Mode, request.DiffRange, files), cancellationToken)
			.ConfigureAwait(false);
		if (!stored.IsAvailable)
			throw new ProjectDiffException(stored.Failure, stored.FailureReason ?? "Git file versions are unavailable.");

		SecretRedactionScope? scope = null;
		if (request.Redaction is { } redaction)
		{
			await redaction.EnsureWarmUpAsync(cancellationToken).ConfigureAwait(false);
			scope = redaction.BeginOutput(files, cancellationToken, RedactionTransformIdentity);
		}

		var renameSources = stored.Files.Values
			.Where(static version => version.RenamedFrom is not null)
			.Select(static version => version.RenamedFrom!)
			.ToHashSet(StringComparer.Ordinal);
		var compared = new List<ProjectDiffFile>(files.Length);
		foreach (var file in files)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var version = stored.Files.GetValueOrDefault(file) ?? new GitFileVersions(null, null, Unsupported: true);
			// A rename source that no longer exists on the new side is shown as the rename itself.
			if (renameSources.Contains(file) && request.Mode != GitFilteringMode.Changes && version.New is null)
				continue;
			var entry = await CompareFileAsync(request, plan, file, version, scope, cancellationToken).ConfigureAwait(false);
			if (entry is not null)
				compared.Add(entry);
		}

		var ordered = compared
			.OrderByDescending(static file => file.ChangedLines)
			.ThenBy(static file => file.RelativePath, StringComparer.Ordinal)
			.ToArray();
		return new ProjectDiffResult(
			request.Mode,
			request.DiffRange,
			ordered,
			deletedOutsideSelection,
			scope?.Complete(),
			notInWorkingTree);
	}

	private static async Task<ProjectDiffFile?> CompareFileAsync(
		ProjectDiffRequest request,
		ProjectContextPlan plan,
		string file,
		GitFileVersions version,
		SecretRedactionScope? scope,
		CancellationToken cancellationToken)
	{
		var relativePath = PathUtility.GetPortableRelativePath(plan.SourceRoot, file);
		string? oldRelativePath = version.RenamedFrom is null
			? null
			: PathUtility.GetPortableRelativePath(plan.SourceRoot, version.RenamedFrom);
		if (version.Unsupported)
			return Omitted(ProjectDiffFileStatus.Modified, ProjectDiffOmission.Binary);

		VersionText? oldText = version.Old is null ? null : Decode(file, version.Old, cancellationToken);
		VersionText? newText = request.Mode == GitFilteringMode.Changes
			? await ReadWorkingAsync(request, file, cancellationToken).ConfigureAwait(false)
			: version.New is null ? null : Decode(file, version.New, cancellationToken);
		if (oldText is null && newText is null)
			return null;
		var status = oldText is null
			? ProjectDiffFileStatus.Added
			: newText is null
				? ProjectDiffFileStatus.Deleted
				: oldRelativePath is not null
					? ProjectDiffFileStatus.Renamed
					: ProjectDiffFileStatus.Modified;

		var omission = Omission(oldText, newText);
		if (omission != ProjectDiffOmission.None)
			return Omitted(status, omission);

		var (oldMasked, newMasked) = Mask(file, oldText?.Text ?? string.Empty, newText?.Text ?? string.Empty, scope, cancellationToken);
		var diff = LineDiff.Compare(oldMasked, newMasked, request.ContextLines, cancellationToken);
		switch (diff.Outcome)
		{
			case LineDiffOutcome.TooLarge:
				return Omitted(status, ProjectDiffOmission.TooLarge);
			case LineDiffOutcome.ChangeTooLarge:
				return Omitted(status, ProjectDiffOmission.ChangeTooLarge);
			case LineDiffOutcome.LineEndingsOnly or LineDiffOutcome.Unchanged
				when status == ProjectDiffFileStatus.Modified:
				return Omitted(status, ProjectDiffOmission.LineEndingsOnly);
			case LineDiffOutcome.LineEndingsOnly or LineDiffOutcome.Unchanged:
				// An added or deleted empty file, or a pure rename, still names the change.
				return Omitted(status, ProjectDiffOmission.None);
			default:
				return new ProjectDiffFile(file, relativePath, oldRelativePath, status, ProjectDiffOmission.None,
					diff.Added, diff.Removed, diff.Hunks);
		}

		ProjectDiffFile Omitted(ProjectDiffFileStatus fileStatus, ProjectDiffOmission reason) =>
			new(file, relativePath, oldRelativePath, fileStatus, reason, 0, 0, []);
	}

	private static ProjectDiffOmission Omission(VersionText? oldText, VersionText? newText)
	{
		if (oldText?.Omission == ProjectDiffOmission.Binary || newText?.Omission == ProjectDiffOmission.Binary)
			return ProjectDiffOmission.Binary;
		return oldText?.Omission == ProjectDiffOmission.TooLarge || newText?.Omission == ProjectDiffOmission.TooLarge
			? ProjectDiffOmission.TooLarge
			: ProjectDiffOmission.None;
	}

	private static (string Old, string New) Mask(
		string file,
		string oldText,
		string newText,
		SecretRedactionScope? scope,
		CancellationToken cancellationToken)
	{
		if (scope is null)
			return (oldText, newText);
		// Both versions use the selected path as their identity, so path-based detector policy and
		// hand marks apply to the old side exactly as they apply to the file a read returns.
		var oldResult = scope.Redact(file, oldText, cancellationToken);
		var newResult = scope.Redact(file, newText, cancellationToken);
		return CrossVersionRedaction.Apply(oldText, oldResult, newText, newResult);
	}

	private static VersionText Decode(string file, GitStoredFile stored, CancellationToken cancellationToken)
	{
		switch (stored.Kind)
		{
			case GitStoredFileKind.NotRegularFile:
				return VersionText.Omitted(ProjectDiffOmission.Binary);
			case GitStoredFileKind.TooLarge:
				return VersionText.Omitted(ProjectDiffOmission.TooLarge);
		}
		if (stored.Content is null || stored.Size > SecretRedactionOutputPreparer.MaximumScannableFileBytes)
			return VersionText.Omitted(ProjectDiffOmission.TooLarge);
		var fact = FileContentAnalyzer.ReadStoredFact(
			file,
			stored.Content,
			SecretRedactionOutputPreparer.MaximumScannableFileBytes,
			cancellationToken);
		return FromClassification(fact.Classification, fact.Content);
	}

	private static async Task<VersionText> ReadWorkingAsync(
		ProjectDiffRequest request,
		string file,
		CancellationToken cancellationToken)
	{
		var read = await request.ReadWorkingFile(file, cancellationToken).ConfigureAwait(false);
		return FromClassification(read.Classification, read.Content?.Content);
	}

	private static VersionText FromClassification(FileContentClassification classification, string? content) =>
		classification switch
		{
			FileContentClassification.Text when content is not null => new VersionText(content, ProjectDiffOmission.None),
			FileContentClassification.TooLarge => VersionText.Omitted(ProjectDiffOmission.TooLarge),
			_ => VersionText.Omitted(ProjectDiffOmission.Binary)
		};

	private sealed record VersionText(string Text, ProjectDiffOmission Omission)
	{
		public static VersionText Omitted(ProjectDiffOmission omission) => new(string.Empty, omission);
	}
}
