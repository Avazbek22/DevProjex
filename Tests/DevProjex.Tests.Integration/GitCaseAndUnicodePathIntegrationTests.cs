using DevProjex.Application.Context;
using DevProjex.Application.Diff;

namespace DevProjex.Tests.Integration;

/// <summary>
/// A file renamed only by case or Unicode normalization form still resolves to its committed
/// history: <c>ls-tree --literal-pathspecs</c> matches by exact bytes regardless of
/// <c>core.ignorecase</c>, so the old side of such a file used to be reported as absent (the file
/// looked added, not modified). These tests exercise the real Git reader through
/// <see cref="ProjectDiffService"/> end to end.
/// </summary>
public sealed class GitCaseAndUnicodePathIntegrationTests
{
	[Fact]
	public async Task ACaseOnlyRenameIsModifiedWithAnExactHunkInChangesMode()
	{
		if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
			Assert.Skip("Requires a case-insensitive filesystem to rename a file's case in place.");

		using var repository = new GitModeRepository();
		var original = repository.Write("Readme.md", "first line\nsecond line\n");
		repository.Commit();
		var renamed = Path.Combine(repository.Root, "README.md");
		File.Move(original, renamed);
		File.WriteAllText(renamed, "first line\nsecond line changed\n");

		var result = await CompareAsync(repository, GitFilteringMode.Changes, null, renamed);

		var entry = Assert.Single(result.Files);
		Assert.Equal(ProjectDiffFileStatus.Modified, entry.Status);
		Assert.Equal(ProjectDiffOmission.None, entry.Omission);
		Assert.Equal(1, entry.Added);
		Assert.Equal(1, entry.Removed);
		var hunk = Assert.Single(entry.Hunks);
		Assert.Contains(hunk.Lines, static line => line.Kind == UnifiedDiffLineKind.Removed && line.Text == "second line");
		Assert.Contains(hunk.Lines, static line => line.Kind == UnifiedDiffLineKind.Added && line.Text == "second line changed");
	}

	[Fact]
	public async Task ACaseOnlyRenameIsModifiedWithAnExactHunkInStagedMode()
	{
		if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
			Assert.Skip("Requires a case-insensitive filesystem to rename a file's case in place.");

		using var repository = new GitModeRepository();
		var original = repository.Write("Readme.md", "first line\nsecond line\n");
		repository.Commit();
		var renamed = Path.Combine(repository.Root, "README.md");
		File.Move(original, renamed);
		File.WriteAllText(renamed, "first line\nsecond line changed\n");
		// A case-only rename a case-insensitive filesystem resolves in place can leave the index
		// entry's stat cache believing nothing changed; dropping and re-adding it is the
		// reliable way to stage the new spelling with its new content for this fixture.
		repository.Git("rm", "--cached", "--ignore-unmatch", "Readme.md");
		repository.Git("add", "README.md");

		var result = await CompareAsync(repository, GitFilteringMode.Staged, null, renamed);

		var entry = Assert.Single(result.Files);
		Assert.Equal(ProjectDiffFileStatus.Modified, entry.Status);
		Assert.Equal(ProjectDiffOmission.None, entry.Omission);
		Assert.Equal(1, entry.Added);
		Assert.Equal(1, entry.Removed);
	}

	[Fact]
	public async Task AnNfdSpelledPathStillResolvesItsCommittedSideOnMacOs()
	{
		if (!OperatingSystem.IsMacOS())
			Assert.Skip("Unicode normalization-insensitive matching is macOS-only (core.precomposeunicode).");

		// "café" with the accented letter stored decomposed (NFD), the form macOS itself tends to
		// hand back from directory enumeration even though Git records the precomposed (NFC) form.
		const string nfcName = "café.md";
		const string nfdName = "café.md";
		using var repository = new GitModeRepository();
		var original = repository.Write(nfcName, "first line\nsecond line\n");
		repository.Commit();
		var requestedPath = Path.Combine(repository.Root, nfdName);
		File.WriteAllText(Path.Combine(repository.Root, nfcName), "first line\nsecond line changed\n");

		var versions = await new GitFileVersionReader().ReadAsync(
			new GitFileVersionRequest(repository.Root, GitFilteringMode.Changes, null, [requestedPath]),
			TestContext.Current.CancellationToken);

		Assert.True(versions.IsAvailable, versions.FailureReason);
		var stored = versions.Files[requestedPath].Old;
		Assert.NotNull(stored);
		Assert.Equal(GitStoredFileKind.Regular, stored!.Kind);
		Assert.Equal("first line\nsecond line\n", Encoding.UTF8.GetString(stored.Content!));
	}

	private static async Task<ProjectDiffResult> CompareAsync(
		GitModeRepository repository,
		GitFilteringMode mode,
		string? diffRange,
		params string[] files)
	{
		var request = new ProjectDiffRequest(
			Plan(repository.Root, files, mode),
			mode,
			diffRange,
			3,
			null,
			(file, cancellationToken) => ReadWorkingFileAsync(file, cancellationToken));
		return await new ProjectDiffService(new GitFileVersionReader())
			.CompareAsync(request, TestContext.Current.CancellationToken);
	}

	private static async ValueTask<FileContentReadResult> ReadWorkingFileAsync(
		string file,
		CancellationToken cancellationToken)
	{
		var bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
		var text = Encoding.UTF8.GetString(bytes);
		var lineCount = text.Length == 0 ? 0 : text.Count(static character => character == '\n') + 1;
		return new FileContentReadResult(
			FileContentClassification.Text,
			new TextFileContent(text, bytes.Length, lineCount, bytes.Length, false, false),
			TextFileEncoding.Utf8);
	}

	private static ProjectContextPlan Plan(string root, IReadOnlyList<string> files, GitFilteringMode mode)
	{
		var tree = new TreeNodeDescriptor("project", root, true, false, "folder", []);
		var analysis = new ProjectAnalysisReport(
			ProjectAnalysisReport.CurrentSchemaVersion,
			DateTimeOffset.UnixEpoch,
			root,
			new ProjectAnalysisSelectionReport([], [], []),
			new ProjectAnalysisInventoryReport([], [], new ProjectTreeSummaryReport(1, files.Count, 0)),
			new ProjectAnalysisOutputMetricsReport(
				ProjectOutputMetricsReport.Empty,
				ProjectOutputMetricsReport.Empty),
			new ProjectAnalysisTimingReport(0, 0, 0),
			new ProjectAnalysisDiagnosticsReport(false, false, []));
		return new ProjectContextPlan(
			root,
			ProjectSelectionSpec.Standard,
			[],
			[],
			[],
			[],
			tree,
			tree,
			new HashSet<string>(PathComparer.Default),
			files,
			[root],
			analysis,
			[],
			new ProjectContextGitReadiness(mode, 0, true),
			"git-case-and-unicode-path");
	}
}
