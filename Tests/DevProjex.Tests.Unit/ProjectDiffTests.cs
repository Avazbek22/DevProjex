using DevProjex.Application.Compression;
using DevProjex.Application.Context;
using DevProjex.Application.Diff;
using DevProjex.Application.Secrets;
using DevProjex.Kernel.Abstractions;
using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class CrossVersionRedactionTests
{
	private const string Placeholder = "DEVPROJEX_REDACTED[manual-secret#1]";

	[Fact]
	public void AValueMaskedInOneVersionIsMaskedInTheOther()
	{
		const string value = "s3cr3t-value";
		var oldSource = $"key = {value}\n";
		var newSource = $"moved\nkey = {value}\n";

		var (oldText, newText) = CrossVersionRedaction.Apply(
			oldSource,
			Masked(oldSource, oldSource.IndexOf(value, StringComparison.Ordinal), value.Length),
			newSource,
			Unmasked(newSource));

		Assert.Equal($"key = {Placeholder}\n", oldText);
		Assert.Equal($"moved\nkey = {Placeholder}\n", newText);
	}

	[Fact]
	public void EveryUnmaskedOccurrenceOutsidePlaceholdersIsReplaced()
	{
		const string value = "abcd1234";
		var source = $"{value} and {value}\n";

		var (_, newText) = CrossVersionRedaction.Apply(
			source,
			Masked(source, 0, value.Length),
			source,
			Unmasked(source));

		Assert.Equal($"{Placeholder} and {Placeholder}\n", newText);
	}

	[Fact]
	public void ShortValuesAreNotPropagated()
	{
		var source = "pin = 12\n";

		var (oldText, newText) = CrossVersionRedaction.Apply(source, Masked(source, 6, 2), source, Unmasked(source));

		Assert.Equal($"pin = {Placeholder}\n", oldText);
		Assert.Equal(source, newText);
	}

	[Fact]
	public void TextsWithoutMaskedValuesAreReturnedUnchanged()
	{
		var (oldText, newText) = CrossVersionRedaction.Apply("a\n", Unmasked("a\n"), "b\n", Unmasked("b\n"));

		Assert.Equal(("a\n", "b\n"), (oldText, newText));
	}

	private static SecretTextRedactionResult Masked(string source, int start, int length)
	{
		var text = string.Concat(source.AsSpan(0, start), Placeholder, source.AsSpan(start + length));
		return new SecretTextRedactionResult(
			text,
			[],
			1,
			1,
			ContentTransformMap.Create([new ContentTransformRange(start, length, Placeholder.Length)], source.Length));
	}

	private static SecretTextRedactionResult Unmasked(string source) =>
		new(source, [], 0, 0, ContentTransformMap.Identity);
}

public sealed class ProjectDiffRenameRedactionTests
{
	private const string Secret = "S3cretValue-7f2a";

	[Fact]
	public async Task TheOldSideOfARenameIsRedactedUnderItsOwnPath()
	{
		using var workspace = new TemporaryDirectory();
		var source = workspace.CreateFile("terraform.tfvars", "region = \"eu\"\n");
		var destination = workspace.CreateFile("terraform.tfvars.example", "placeholder\n");
		var oldText = $"db_password = \"{Secret}\"\nregion = \"eu\"\nzone = \"a\"\n";
		var newText = $"db_password = \"{Secret}\"\nregion = \"us\"\nzone = \"a\"\n";
		var reader = new FixedVersionReader(new Dictionary<string, GitFileVersions>
		{
			[source] = new(Stored(oldText), null),
			[destination] = new(Stored(oldText), Stored(newText), RenamedFrom: source)
		});
		using var session = new SecretRedactionSession(new NameOnlyDetector(".tfvars", Secret));
		var request = new ProjectDiffRequest(
			Plan(workspace.Path, [source, destination]),
			GitFilteringMode.Staged,
			null,
			3,
			new SecretRedactionContext(workspace.Path, session),
			static (_, _) => throw new InvalidOperationException("The staged scope never reads the working file."));

		var result = await new ProjectDiffService(reader).CompareAsync(request, TestContext.Current.CancellationToken);

		var file = Assert.Single(result.Files);
		Assert.Equal(ProjectDiffFileStatus.Renamed, file.Status);
		Assert.Equal("terraform.tfvars", file.OldRelativePath);
		var text = ProjectDiffText.FormatFile(file);
		Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
		Assert.Contains(" db_password = \"DEVPROJEX_REDACTED[", text, StringComparison.Ordinal);
		Assert.Contains("-region = \"eu\"\n+region = \"us\"\n", text, StringComparison.Ordinal);
		Assert.Equal(1, Assert.IsType<SecretRedactionSnapshot>(result.Protection).DetectedCount);
	}

	private static GitStoredFile Stored(string text)
	{
		var bytes = Encoding.UTF8.GetBytes(text);
		return new GitStoredFile(GitStoredFileKind.Regular, bytes.Length, bytes);
	}

	private static ProjectContextPlan Plan(string root, IReadOnlyList<string> files)
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
			new ProjectContextGitReadiness(GitFilteringMode.Staged, 0, true),
			"rename-redaction");
	}

	private sealed class FixedVersionReader(IReadOnlyDictionary<string, GitFileVersions> files) : IGitFileVersionReader
	{
		public Task<GitFileVersionSet> ReadAsync(GitFileVersionRequest request, CancellationToken cancellationToken = default) =>
			Task.FromResult(new GitFileVersionSet(files));

		public Task<GitRevisionAvailability> ProbeRangeAsync(
			string projectRoot,
			string diffRange,
			CancellationToken cancellationToken = default) =>
			Task.FromResult(GitRevisionAvailability.Unknown);
	}

	// Finds the value only in files whose name selects the structured rule, as real path-based rules do.
	private sealed class NameOnlyDetector(string extension, string secret) : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default)
		{
			if (!repositoryRelativePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
				return [];
			var findings = new List<DetectedSecret>();
			for (var start = content.IndexOf(secret, StringComparison.Ordinal);
				 start >= 0;
				 start = content.IndexOf(secret, start + secret.Length, StringComparison.Ordinal))
			{
				findings.Add(new DetectedSecret("config-secret", start, secret.Length, secret, 0));
			}
			return findings;
		}
	}
}

public sealed class ProjectDiffTextTests
{
	[Fact]
	public void ListEntriesCarryCountsAndStatus()
	{
		Assert.Equal("src/a.cs +3 -1 [modified]", ProjectDiffText.FormatListEntry(File("src/a.cs", ProjectDiffFileStatus.Modified, added: 3, removed: 1)));
		Assert.Equal("logo.png [added, binary]", ProjectDiffText.FormatListEntry(File("logo.png", ProjectDiffFileStatus.Added, ProjectDiffOmission.Binary)));
		Assert.Equal(
			"new.cs +0 -0 [renamed from old.cs]",
			ProjectDiffText.FormatListEntry(File("new.cs", ProjectDiffFileStatus.Renamed) with { OldRelativePath = "old.cs" }));
	}

	[Fact]
	public void FileHeadersUseDevNullForAddedAndDeletedFiles()
	{
		var hunk = new UnifiedDiffHunk(0, 0, 1, 1, [new UnifiedDiffLine(UnifiedDiffLineKind.Added, "x")]);

		Assert.Equal(
			"--- /dev/null\n+++ b/a.txt\n@@ -0,0 +1 @@\n+x\n",
			ProjectDiffText.FormatFile(File("a.txt", ProjectDiffFileStatus.Added, added: 1) with { Hunks = [hunk] }));
		Assert.StartsWith(
			"--- a/a.txt\n+++ /dev/null\n",
			ProjectDiffText.FormatFile(File("a.txt", ProjectDiffFileStatus.Deleted)),
			StringComparison.Ordinal);
		Assert.StartsWith(
			"--- a/old.txt\n+++ b/new.txt\n",
			ProjectDiffText.FormatFile(File("new.txt", ProjectDiffFileStatus.Renamed) with { OldRelativePath = "old.txt" }),
			StringComparison.Ordinal);
	}

	[Fact]
	public void ControlCharactersAreEscapedButTabsStay()
	{
		var hunk = new UnifiedDiffHunk(1, 1, 1, 1,
		[
			new UnifiedDiffLine(UnifiedDiffLineKind.Removed, "\tbefore\u001b[31m"),
			new UnifiedDiffLine(UnifiedDiffLineKind.Added, "after\rcarriage")
		]);

		var text = ProjectDiffText.FormatFile(File("line\nbreak.txt", ProjectDiffFileStatus.Modified) with { Hunks = [hunk] });

		Assert.Equal(
			"--- a/line\\nbreak.txt\n+++ b/line\\nbreak.txt\n@@ -1 +1 @@\n-\tbefore\\u001B[31m\n+after\\rcarriage\n",
			text);
	}

	private static ProjectDiffFile File(
		string path,
		ProjectDiffFileStatus status,
		ProjectDiffOmission omission = ProjectDiffOmission.None,
		int added = 0,
		int removed = 0) =>
		new(Path.GetFullPath(path), path, null, status, omission, added, removed, []);
}

public sealed class StoredContentDecodingTests
{
	[Fact]
	public void StoredBytesAreClassifiedLikeAFileOnDisk()
	{
		Assert.Equal("a\nb\n", FileContentAnalyzer.ReadStoredFact("a.txt", "a\nb\n"u8.ToArray(), 1024).Content);
		Assert.Equal(FileContentClassification.Binary, FileContentAnalyzer.ReadStoredFact("a.png", "text"u8.ToArray(), 1024).Classification);
		Assert.Equal(FileContentClassification.Binary, FileContentAnalyzer.ReadStoredFact("a.txt", [0x61, 0, 0x62], 1024).Classification);
		Assert.Equal(FileContentClassification.UnsupportedEncoding, FileContentAnalyzer.ReadStoredFact("a.txt", [0x61, 0xC3, 0x28], 1024).Classification);
		Assert.Equal(FileContentClassification.TooLarge, FileContentAnalyzer.ReadStoredFact("a.txt", "x\n"u8.ToArray().Concat(Enumerable.Repeat((byte)'x', 2048)).ToArray(), 1024).Classification);
		Assert.Equal("é\n", FileContentAnalyzer.ReadStoredFact("a.txt", [0xFF, 0xFE, 0xE9, 0x00, 0x0A, 0x00], 1024).Content);
		Assert.Equal("", FileContentAnalyzer.ReadStoredFact("a.txt", [], 1024).Content);
	}
}

public sealed class McpDiffLayoutTests
{
	[Fact]
	public void AnEmptyResultSaysSoWithoutASummary()
	{
		var rendered = DevProjexMcpTools.RenderDiff("Root: /project", Result([]), trailingNotices: null);

		Assert.Contains("Root: /project\nScope: changes\n</untrusted-data-", rendered.Text, StringComparison.Ordinal);
		Assert.EndsWith("[Diff] no changes in the effective selection for this scope.", rendered.Text, StringComparison.Ordinal);
		Assert.DoesNotContain("[Diff] scope=", rendered.Text, StringComparison.Ordinal);
	}

	[Fact]
	public void AFileThatDoesNotFitIsListedAndSmallerOnesStillAppear()
	{
		var large = Changed("large.txt", 900, 30);
		var medium = Changed("medium.txt", 800, 30);
		var small = Changed("small.txt", 3, 10);

		var rendered = DevProjexMcpTools.RenderDiff("Root: /project", Result([large, medium, small]), trailingNotices: null);

		Assert.InRange(rendered.Text.Length, 1, 50_000);
		Assert.Contains("+++ b/large.txt\n", rendered.Text, StringComparison.Ordinal);
		Assert.DoesNotContain("+++ b/medium.txt", rendered.Text, StringComparison.Ordinal);
		Assert.Contains("+++ b/small.txt\n", rendered.Text, StringComparison.Ordinal);
		Assert.Contains("Not shown:\nmedium.txt +800 -0 [added]", rendered.Text, StringComparison.Ordinal);
		Assert.Contains("[Diff] scope=changes · files=3 · shown=2 · added=1703 · removed=0 · partial", rendered.Text, StringComparison.Ordinal);
		Assert.Contains("[Diff] partial · 1 file(s) not shown", rendered.Text, StringComparison.Ordinal);
		Assert.Equal(["large.txt", "small.txt"], rendered.DeliveredPaths);
	}

	[Fact]
	public void NotShownCountsListOnlyNonZeroFieldsInOrder()
	{
		var result = Result(
			[
				Changed("a.txt", 1, 5),
				new ProjectDiffFile("/p/b.png", "b.png", null, ProjectDiffFileStatus.Modified, ProjectDiffOmission.Binary, 0, 0, []),
				new ProjectDiffFile("/p/c.txt", "c.txt", null, ProjectDiffFileStatus.Modified, ProjectDiffOmission.LineEndingsOnly, 0, 0, [])
			],
			deleted: 2);

		Assert.Equal(
			"[Diff] not shown: binary=1 · line-endings-only=1 · deleted=2",
			DevProjexMcpTools.FormatDiffNotShownNotice(result));
	}

	[Fact]
	public void ARangeScopeIsNamedOnlyInsideTheBlock()
	{
		var result = Result([Changed("a.txt", 1, 5)]) with { Mode = GitFilteringMode.Diff, DiffRange = "main..topic" };

		var rendered = DevProjexMcpTools.RenderDiff("Root: /project", result, trailingNotices: null);

		Assert.Contains("Scope: diff:main..topic\n", rendered.Text, StringComparison.Ordinal);
		Assert.Contains("[Diff] scope=diff · files=1", rendered.Text, StringComparison.Ordinal);
		var trusted = rendered.Text[(rendered.Text.LastIndexOf("</untrusted-data-", StringComparison.Ordinal))..];
		Assert.DoesNotContain("main", trusted, StringComparison.Ordinal);
	}

	private static ProjectDiffResult Result(IReadOnlyList<ProjectDiffFile> files, int deleted = 0) =>
		new(GitFilteringMode.Changes, null, files, deleted, null);

	private static ProjectDiffFile Changed(string path, int lines, int width)
	{
		var hunk = new UnifiedDiffHunk(
			0,
			0,
			1,
			lines,
			Enumerable.Range(0, lines).Select(_ => new UnifiedDiffLine(UnifiedDiffLineKind.Added, new string('x', width))).ToArray());
		return new ProjectDiffFile("/p/" + path, path, null, ProjectDiffFileStatus.Added, ProjectDiffOmission.None, lines, 0, [hunk]);
	}
}
