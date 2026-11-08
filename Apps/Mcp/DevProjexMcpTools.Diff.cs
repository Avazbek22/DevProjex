using System.ComponentModel;
using System.Globalization;
using DevProjex.Application.Diff;

namespace DevProjex.Mcp;

internal sealed partial class DevProjexMcpTools
{
	private const int MaximumDiffResponseCharacters = 50_000;
	private const int DefaultDiffContextLines = 3;
	private const string DiffNoChangesNotice = "[Diff] no changes in the effective selection for this scope.";
	internal const string DiffNotInWorkingTreeNotice =
		"[Diff] files changed in this range but absent from the working tree are counted, not shown; " +
		"check out the range's newer commit to review them.";
	// A constant: neither the scope value nor a path reaches it, so it stays trusted.
	internal const string GitScopeDiffNotice = "[Diff] get_diff with the same git_scope returns only the changed lines.";

	private readonly IReadOnlySet<string> getDiffArgumentNames = Allowed(agentExclusions,
		"project", "branch", "profile", "git_scope", "paths", "include_patterns", "exclude_patterns",
		"tracked_only", "max_file_bytes", "context_lines");

	[Description(
		"Shows what changed: masked unified diff hunks for a Git scope. Use it to review staged, uncommitted or ref-to-ref changes; use get_file for whole files and search_project for text. Returns per-file status, added/removed counts and hunks with context, largest changes first, under the same secret and private-data redaction as file content, with complete|partial status within 50,000 characters. Key parameters: git_scope=changes|staged|diff:A..B (default changes), paths, include_patterns, exclude_patterns, context_lines=0..20.")]
	public Task<CallToolResult> GetDiff(
		RequestContext<CallToolRequestParams> request,
		CancellationToken cancellationToken) =>
		RunProjectAsync("get_diff", request, async () =>
		{
			var arguments = McpJsonArguments.Create(request.Params, getDiffArgumentNames);
			var paths = ParsePaths(arguments);
			var includePatterns = arguments.OptionalStringArray("include_patterns");
			var excludePatterns = arguments.OptionalStringArray("exclude_patterns");
			var contextLines = arguments.OptionalInteger("context_lines", 0, 20) ?? DefaultDiffContextLines;
			var plan = await Projects.BuildPlanAsync(
				arguments.OptionalString("project"),
				arguments.OptionalString("branch"),
				paths,
				includePatterns,
				excludePatterns,
				arguments.OptionalString("profile"),
				arguments.OptionalBoolean("tracked_only", false),
				arguments.OptionalString("git_scope") ?? GitScopeSelection.ToToken(GitFilteringMode.Changes),
				arguments.OptionalInt64("max_file_bytes", 1, long.MaxValue),
				cancellationToken,
				includeOutputMetrics: false,
				exclusions: ParseExclusionsArgument(arguments),
				tolerateMissingPaths: true).ConfigureAwait(false);
			RecordPlan(plan);
			liveContext?.RecordFocusUse(plan.SourceRoot, McpLiveFocusUse.Diff);
			var diff = await Projects.CompareVersionsAsync(plan, contextLines, cancellationToken).ConfigureAwait(false);
			var selectionNotices = SelectionNotices(
				plan,
				includeFilters: true,
				new McpSelectionNoticeContext(
					HasPaths: HasItems(paths),
					HasPatterns: HasItems(includePatterns) || HasItems(excludePatterns),
					HasRootOnlyPattern: HasRootOnlyPattern(includePatterns)));
			var warnings = McpTrustedDiagnosticFormatter.FormatWarnings(plan.Diagnostics
				.Where(static diagnostic => diagnostic.Code != GitScopeFilter.DeletedDiagnosticCode)
				.ToArray());
			var affected = diff.HasChanges
				? await FindDiffImpactAsync(arguments, plan, diff, includePatterns, excludePatterns, cancellationToken)
					.ConfigureAwait(false)
				: DependencyChangeImpact.None;
			var rendered = RenderDiff(
				ContextRootPresentation.FormatLine(Projects.ResolveProtectedDocumentRoot(plan)),
				diff,
				trailingNotices: CombineTrustedNotices(warnings, selectionNotices),
				affected);
			journal?.RecordProtection(diff.Protection);
			journal?.RecordDeliveredPaths(plan.SourceRoot, rendered.DeliveredPaths);
			return McpToolResults.TextSuccess(rendered.Text);
		}, cancellationToken);

	/// <summary>
	/// Relates the changed files across the readable project: the selection without its Git scope,
	/// path and include narrowing, exactly as related_files lifts them. Builds the dependency index
	/// when the engine has none cached for that project. The section is an addition to the diff, so
	/// any failure to relate leaves the diff itself unchanged.
	/// </summary>
	private async Task<DependencyChangeImpact> FindDiffImpactAsync(
		McpJsonArguments arguments,
		ProjectContextPlan plan,
		ProjectDiffResult diff,
		IReadOnlyList<string>? includePatterns,
		IReadOnlyList<string>? excludePatterns,
		CancellationToken cancellationToken)
	{
		try
		{
			var projectPlan = await Projects.BuildRelationProjectPlanAsync(
				plan,
				arguments.OptionalString("project"),
				arguments.OptionalString("branch"),
				includePatterns,
				excludePatterns,
				arguments.OptionalString("profile"),
				arguments.OptionalBoolean("tracked_only", false),
				arguments.OptionalString("git_scope") ?? GitScopeSelection.ToToken(GitFilteringMode.Changes),
				arguments.OptionalInt64("max_file_bytes", 1, long.MaxValue),
				ParseExclusionsArgument(arguments),
				cancellationToken).ConfigureAwait(false) ?? plan;
			var changed = diff.Files
				.Where(static file => file.Status != ProjectDiffFileStatus.Deleted)
				.Select(static file => file.RelativePath)
				.ToArray();
			if (changed.Length == 0)
				return DependencyChangeImpact.None;
			var index = await Projects.DependencyFactsEngine.IndexAsync(
				projectPlan.SourceRoot,
				projectPlan.IncludedFiles,
				progress: null,
				cancellationToken,
				readableProjectFiles: projectPlan.IncludedFiles).ConfigureAwait(false);
			var candidates = projectPlan.IncludedFiles
				.Select(path => McpProjectService.ToRelative(projectPlan.SourceRoot, path))
				.ToHashSet(StringComparer.Ordinal);
			return DependencyChangeImpact.Compute(index, changed, candidates);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			return DependencyChangeImpact.None;
		}
	}

	internal static McpDiffRendering RenderDiff(
		string rootLine,
		ProjectDiffResult diff,
		string? trailingNotices,
		DependencyChangeImpact? affected = null)
	{
		ArgumentNullException.ThrowIfNull(diff);
		if (!diff.HasChanges)
		{
			return new McpDiffRendering(
				AppendTrustedNotices(
					McpSpotlight.Wrap(rootLine + "\nScope: " + FormatDiffScope(diff)),
					DiffNoChangesNotice,
					trailingNotices),
				[]);
		}

		// Every trusted line but the two partial notices is known before the body is laid out; both
		// are reserved at their longest so the response cannot cross the limit.
		var fixedNotices = CombineTrustedNotices(FormatDiffNotShownNotice(diff), trailingNotices);
		var budget = MaximumDiffResponseCharacters -
			McpSpotlight.Wrap(string.Empty).Length -
			FormatDiffSummary(diff, shown: int.MaxValue, partial: true).Length -
			FormatDiffPartialNotice(int.MaxValue).Length -
			FormatDiffHunkPartialNotice(int.MaxValue).Length -
			(fixedNotices?.Length ?? 0) -
			8;
		var layout = LayoutDiff(rootLine, diff, budget);
		var shown = layout.Shown.Count(static file => file.Omission == ProjectDiffOmission.None);
		var filesPartial = layout.NotShown.Count > 0;
		var hunksPartial = layout.PartialHunksNotShown > 0;
		var body = layout.Body;
		string? affectedNotice = null;
		if (affected is { IsEmpty: false })
		{
			// The section is optional: it is added only when it fits beside everything already laid
			// out, so it never pushes a hunk or a file out of the response.
			var section = "\n\n" + FormatAffectedSection(affected);
			var notice = FormatDiffAffectedNotice(affected);
			if (body.Length + section.Length + notice.Length + 1 <= budget)
			{
				body += section;
				affectedNotice = notice;
			}
		}
		return new McpDiffRendering(
			AppendTrustedNotices(
				McpSpotlight.Wrap(body),
				FormatDiffSummary(diff, shown, filesPartial || hunksPartial),
				FormatDiffNotShownNotice(diff),
				filesPartial ? FormatDiffPartialNotice(layout.NotShown.Count) : null,
				hunksPartial ? FormatDiffHunkPartialNotice(layout.PartialHunksNotShown) : null,
				affectedNotice,
				trailingNotices),
			layout.Shown
				.Where(static file => file.Omission == ProjectDiffOmission.None)
				.Select(static file => file.RelativePath)
				.ToArray());
	}

	/// <summary>
	/// Lays files out largest change first. A file that does not fit whole is moved to the
	/// not-shown list and later, smaller files still get the room; no file is ever cut. The first
	/// file considered (or the only file in the diff) gets one exception: when it still does not
	/// fit whole, as many of its hunks as fit are shown instead, in file order, and no hunk is cut
	/// either; the rest of its hunks are listed by their range under "Not shown".
	/// </summary>
	private static (string Body, IReadOnlyList<ProjectDiffFile> Shown, IReadOnlyList<ProjectDiffFile> NotShown, int PartialHunksNotShown) LayoutDiff(
		string rootLine,
		ProjectDiffResult diff,
		int budget)
	{
		var header = rootLine + "\nScope: " + FormatDiffScope(diff) + "\n";
		var entries = diff.Files.Select(ProjectDiffText.FormatListEntry).ToArray();
		var listCharacters = entries.Sum(static entry => entry.Length + 1);
		var remaining = budget - header.Length - ChangedFilesHeading.Length - NotShownHeading.Length - 6 - listCharacters;
		var shown = new List<ProjectDiffFile>();
		var notShown = new List<ProjectDiffFile>();
		var sections = new List<string>();
		ProjectDiffFile? partialFile = null;
		IReadOnlyList<UnifiedDiffHunk> partialNotShownHunks = [];
		foreach (var file in diff.Files)
		{
			if (file.Omission != ProjectDiffOmission.None)
			{
				shown.Add(file);
				continue;
			}
			// A lower bound from the unescaped text avoids formatting (and escaping) a file that
			// cannot possibly fit; escaping only ever makes the formatted text longer.
			if (EstimateDiffFileLowerBound(file) <= remaining)
			{
				var section = ProjectDiffText.FormatFile(file);
				if (section.Length + 1 <= remaining)
				{
					remaining -= section.Length + 1;
					shown.Add(file);
					sections.Add(section);
					continue;
				}
			}
			if ((sections.Count == 0 || diff.Files.Count == 1) &&
				TryFormatPartialDiffFile(file, remaining) is { } partial)
			{
				var cost = partial.Section.Length + 1 +
					(FormatPartialListEntry(file).Length - ProjectDiffText.FormatListEntry(file).Length) +
					partial.NotShownHunks.Sum(hunk => FormatDiffHunkRangeEntry(file, hunk).Length + 1);
				// TryFormatPartialDiffFile reserves this exact cost up front, so it never overshoots.
				if (cost <= remaining)
				{
					remaining -= cost;
					shown.Add(file);
					sections.Add(partial.Section);
					partialFile = file;
					partialNotShownHunks = partial.NotShownHunks;
					continue;
				}
			}
			notShown.Add(file);
		}

		var body = new StringBuilder(header);
		if (shown.Count > 0)
		{
			body.Append('\n').Append(ChangedFilesHeading).Append('\n');
			foreach (var file in shown)
				body.Append(ReferenceEquals(file, partialFile) ? FormatPartialListEntry(file) : ProjectDiffText.FormatListEntry(file)).Append('\n');
		}
		foreach (var section in sections)
			body.Append('\n').Append(section);
		if (notShown.Count > 0 || partialNotShownHunks.Count > 0)
		{
			body.Append('\n').Append(NotShownHeading).Append('\n');
			foreach (var file in notShown)
				body.Append(ProjectDiffText.FormatListEntry(file)).Append('\n');
			if (partialFile is not null)
				foreach (var hunk in partialNotShownHunks)
					body.Append(FormatDiffHunkRangeEntry(partialFile, hunk)).Append('\n');
		}
		if (remaining < 0)
			return TrimToBudget(header, diff, budget);
		return (body.ToString().TrimEnd('\n'), shown, notShown, partialNotShownHunks.Count);
	}

	// Only reached when the file list alone exceeds the response limit: the list is cut at a whole
	// entry and every file counts as not shown.
	private static (string Body, IReadOnlyList<ProjectDiffFile> Shown, IReadOnlyList<ProjectDiffFile> NotShown, int PartialHunksNotShown) TrimToBudget(
		string header,
		ProjectDiffResult diff,
		int budget)
	{
		var body = new StringBuilder(header).Append('\n').Append(NotShownHeading).Append('\n');
		var listed = 0;
		foreach (var file in diff.Files)
		{
			var entry = ProjectDiffText.FormatListEntry(file);
			if (body.Length + entry.Length + 64 > budget)
				break;
			body.Append(entry).Append('\n');
			listed++;
		}
		if (listed < diff.Files.Count)
		{
			body.Append("… ").Append((diff.Files.Count - listed).ToString(CultureInfo.InvariantCulture))
				.Append(" more changed file(s)\n");
		}
		return (body.ToString().TrimEnd('\n'), [], diff.Files, 0);
	}

	/// <summary>The characters <see cref="ProjectDiffText.FormatFile"/> writes, without escaping or writing them.</summary>
	private static long EstimateDiffFileLowerBound(ProjectDiffFile file)
	{
		long characters = (file.Status == ProjectDiffFileStatus.Added
			? "--- /dev/null"
			: "--- a/" + (file.OldRelativePath ?? file.RelativePath)).Length + 1;
		characters += (file.Status == ProjectDiffFileStatus.Deleted
			? "+++ /dev/null"
			: "+++ b/" + file.RelativePath).Length + 1;
		foreach (var hunk in file.Hunks)
			characters += UnifiedDiffFormatter.MeasureHunk(hunk);
		return characters;
	}

	/// <summary>
	/// Formats as many of a file's hunks, in file order, as fit within <paramref name="remaining"/>;
	/// no hunk is ever cut. Returns null when not even its first hunk fits.
	/// </summary>
	/// <remarks>
	/// The caller charges <paramref name="remaining"/> for the section plus the file's "partial"
	/// list entry and a "not shown" range line per hunk left out. Showing one more hunk removes
	/// one range line but can cost far more than it saves, so the budget for hunk text is reserved
	/// up front for the worst case (every hunk left out) rather than reduced one hunk at a time
	/// until it happens to fit.
	/// </remarks>
	private static (string Section, IReadOnlyList<UnifiedDiffHunk> NotShownHunks)? TryFormatPartialDiffFile(
		ProjectDiffFile file,
		int remaining)
	{
		var headerLines =
			(file.Status == ProjectDiffFileStatus.Added
				? "--- /dev/null"
				: "--- a/" + ProjectDiffText.EscapePath(file.OldRelativePath ?? file.RelativePath)) + "\n" +
			(file.Status == ProjectDiffFileStatus.Deleted
				? "+++ /dev/null"
				: "+++ b/" + ProjectDiffText.EscapePath(file.RelativePath)) + "\n";
		var entryDelta = FormatPartialListEntry(file).Length - ProjectDiffText.FormatListEntry(file).Length;
		var allHunksRangeLines = file.Hunks.Sum(hunk => FormatDiffHunkRangeEntry(file, hunk).Length + 1);
		var textBudget = remaining - entryDelta - allHunksRangeLines - 1;
		if (headerLines.Length > textBudget)
			return null;
		var builder = new StringBuilder(headerLines);
		var budgetLeft = textBudget - headerLines.Length;
		var notShown = new List<UnifiedDiffHunk>();
		var shownCount = 0;
		var cut = false;
		foreach (var hunk in file.Hunks)
		{
			if (!cut)
			{
				if (UnifiedDiffFormatter.MeasureHunk(hunk) <= budgetLeft)
				{
					using var writer = new StringWriter(CultureInfo.InvariantCulture);
					UnifiedDiffFormatter.WriteHunk(writer, hunk, ProjectDiffText.EscapeLine);
					var text = writer.ToString();
					if (text.Length <= budgetLeft)
					{
						budgetLeft -= text.Length;
						builder.Append(text);
						shownCount++;
						continue;
					}
				}
				cut = true;
			}
			notShown.Add(hunk);
		}
		return shownCount > 0 ? (builder.ToString(), notShown) : null;
	}

	private static string FormatPartialListEntry(ProjectDiffFile file)
	{
		var status = file.Status == ProjectDiffFileStatus.Renamed && file.OldRelativePath is not null
			? "renamed from " + ProjectDiffText.EscapePath(file.OldRelativePath)
			: ProjectDiffText.Status(file.Status);
		return string.Create(
			CultureInfo.InvariantCulture,
			$"{ProjectDiffText.EscapePath(file.RelativePath)} +{file.Added} -{file.Removed} [{status}, partial]");
	}

	private static string FormatDiffHunkRangeEntry(ProjectDiffFile file, UnifiedDiffHunk hunk) =>
		ProjectDiffText.EscapePath(file.RelativePath) + " " + UnifiedDiffFormatter.FormatHeader(hunk);

	private const string AffectedDependentsHeading = "Files that import the changed files:";
	private const string AffectedTestsHeading = "Tests that import or are named after the changed files:";

	private static string FormatAffectedSection(DependencyChangeImpact affected)
	{
		var section = new StringBuilder();
		Append(AffectedDependentsHeading, affected.Dependents);
		Append(AffectedTestsHeading, affected.Tests);
		return section.ToString().TrimEnd('\n');

		void Append(string heading, IReadOnlyList<string> paths)
		{
			if (paths.Count == 0)
				return;
			if (section.Length > 0)
				section.Append('\n');
			section.Append(heading).Append('\n');
			foreach (var path in paths)
				section.Append(ProjectDiffText.EscapePath(path)).Append('\n');
		}
	}

	internal static string FormatDiffAffectedNotice(DependencyChangeImpact affected) =>
		string.Create(
			CultureInfo.InvariantCulture,
			$"[Diff] affected: dependents={affected.DependentCount} · tests={affected.TestCount}");

	private const string ChangedFilesHeading = "Changed files (largest first):";
	// Files with hunks left out only for the response limit; trusted reasons are counted in "[Diff] not shown:".
	private const string NotShownHeading = "Not shown (size limit):";

	private static string FormatDiffScope(ProjectDiffResult diff) =>
		McpTextEscaping.EscapeSingleLine(GitScopeSelection.ToToken(
			diff.Mode,
			diff.Mode == GitFilteringMode.Diff ? diff.DiffRange : null));

	internal static string FormatDiffSummary(ProjectDiffResult diff, int shown, bool partial) =>
		string.Create(
			CultureInfo.InvariantCulture,
			$"[Diff] scope={ProjectDiffText.ScopeKind(diff.Mode)} · files={diff.Files.Count} · shown={(shown == int.MaxValue ? diff.Files.Count : shown)} · added={diff.Added} · removed={diff.Removed} · {(partial ? "partial" : "complete")}");

	internal static string? FormatDiffNotShownNotice(ProjectDiffResult diff)
	{
		var parts = new List<string>(5);
		Add("binary", diff.Count(ProjectDiffOmission.Binary));
		Add("too-large", diff.Count(ProjectDiffOmission.TooLarge));
		Add("change-too-large", diff.Count(ProjectDiffOmission.ChangeTooLarge));
		Add("line-endings-only", diff.Count(ProjectDiffOmission.LineEndingsOnly));
		Add("content-unchanged", diff.Count(ProjectDiffOmission.ContentUnchanged));
		Add("deleted", diff.DeletedOutsideSelection);
		Add("not-in-working-tree", diff.NotInWorkingTree);
		if (parts.Count == 0)
			return null;
		var notice = "[Diff] not shown: " + string.Join(" · ", parts);
		return diff.NotInWorkingTree > 0 && diff.Mode == GitFilteringMode.Diff
			? notice + "\n" + DiffNotInWorkingTreeNotice
			: notice;

		void Add(string name, int count)
		{
			if (count > 0)
				parts.Add(name + "=" + count.ToString(CultureInfo.InvariantCulture));
		}
	}

	internal static string FormatDiffPartialNotice(int notShown) =>
		$"[Diff] partial · {(notShown == int.MaxValue ? "0000000" : notShown.ToString(CultureInfo.InvariantCulture))} " +
		"file(s) not shown to stay within 50,000 characters; narrow paths or include_patterns, or lower context_lines.";

	internal static string FormatDiffHunkPartialNotice(int notShownHunks) =>
		$"[Diff] partial · {(notShownHunks == int.MaxValue ? "0000000" : notShownHunks.ToString(CultureInfo.InvariantCulture))} " +
		"hunk(s) of a file that does not fit were not shown; their line ranges are listed, read them with get_file.";

	private static string? FormatGitScopeDiffNotice(McpJsonArguments arguments) =>
		arguments.OptionalString("git_scope") is null ? null : GitScopeDiffNotice;
}

internal sealed record McpDiffRendering(string Text, IReadOnlyList<string> DeliveredPaths);
