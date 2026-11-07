using System.ComponentModel;
using System.Globalization;
using DevProjex.Application.Diff;

namespace DevProjex.Mcp;

internal sealed partial class DevProjexMcpTools
{
	private const int MaximumDiffResponseCharacters = 50_000;
	private const int DefaultDiffContextLines = 3;
	private const string DiffNoChangesNotice = "[Diff] no changes in the effective selection for this scope.";
	// A constant: neither the scope value nor a path reaches it, so it stays trusted.
	internal const string GitScopeDiffNotice = "[Diff] get_diff with the same git_scope returns only the changed lines.";

	private readonly IReadOnlySet<string> getDiffArgumentNames = McpJsonArguments.FreezeAllowed(
		"project", "branch", "git_scope", "paths", "include_patterns", "exclude_patterns", "context_lines");

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
				profile: null,
				trackedOnly: false,
				arguments.OptionalString("git_scope") ?? GitScopeSelection.ToToken(GitFilteringMode.Changes),
				maximumFileBytes: null,
				cancellationToken,
				includeOutputMetrics: false,
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
			var rendered = RenderDiff(
				ContextRootPresentation.FormatLine(Projects.ResolveProtectedDocumentRoot(plan)),
				diff,
				trailingNotices: CombineTrustedNotices(warnings, selectionNotices));
			journal?.RecordProtection(diff.Protection);
			journal?.RecordDeliveredPaths(plan.SourceRoot, rendered.DeliveredPaths);
			return McpToolResults.TextSuccess(rendered.Text);
		}, cancellationToken);

	internal static McpDiffRendering RenderDiff(string rootLine, ProjectDiffResult diff, string? trailingNotices)
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

		// Every trusted line but the partial notice is known before the body is laid out; the
		// partial notice is reserved at its longest so the response cannot cross the limit.
		var fixedNotices = CombineTrustedNotices(FormatDiffNotShownNotice(diff), trailingNotices);
		var budget = MaximumDiffResponseCharacters -
			McpSpotlight.Wrap(string.Empty).Length -
			FormatDiffSummary(diff, shown: int.MaxValue, partial: true).Length -
			FormatDiffPartialNotice(int.MaxValue).Length -
			(fixedNotices?.Length ?? 0) -
			8;
		var layout = LayoutDiff(rootLine, diff, budget);
		var shown = layout.Shown.Count(static file => file.Omission == ProjectDiffOmission.None);
		var partial = layout.NotShown.Count > 0;
		return new McpDiffRendering(
			AppendTrustedNotices(
				McpSpotlight.Wrap(layout.Body),
				FormatDiffSummary(diff, shown, partial),
				FormatDiffNotShownNotice(diff),
				partial ? FormatDiffPartialNotice(layout.NotShown.Count) : null,
				trailingNotices),
			layout.Shown
				.Where(static file => file.Omission == ProjectDiffOmission.None)
				.Select(static file => file.RelativePath)
				.ToArray());
	}

	/// <summary>
	/// Lays files out largest change first. A file that does not fit whole is moved to the
	/// not-shown list and later, smaller files still get the room; no file is ever cut.
	/// </summary>
	private static (string Body, IReadOnlyList<ProjectDiffFile> Shown, IReadOnlyList<ProjectDiffFile> NotShown) LayoutDiff(
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
		foreach (var file in diff.Files)
		{
			if (file.Omission != ProjectDiffOmission.None)
			{
				shown.Add(file);
				continue;
			}
			var section = ProjectDiffText.FormatFile(file);
			if (section.Length + 1 <= remaining)
			{
				remaining -= section.Length + 1;
				shown.Add(file);
				sections.Add(section);
			}
			else
			{
				notShown.Add(file);
			}
		}

		var body = new StringBuilder(header);
		if (shown.Count > 0)
		{
			body.Append('\n').Append(ChangedFilesHeading).Append('\n');
			foreach (var file in shown)
				body.Append(ProjectDiffText.FormatListEntry(file)).Append('\n');
		}
		foreach (var section in sections)
			body.Append('\n').Append(section);
		if (notShown.Count > 0)
		{
			body.Append('\n').Append(NotShownHeading).Append('\n');
			foreach (var file in notShown)
				body.Append(ProjectDiffText.FormatListEntry(file)).Append('\n');
		}
		if (remaining < 0)
			return TrimToBudget(header, diff, budget);
		return (body.ToString().TrimEnd('\n'), shown, notShown);
	}

	// Only reached when the file list alone exceeds the response limit: the list is cut at a whole
	// entry and every file counts as not shown.
	private static (string Body, IReadOnlyList<ProjectDiffFile> Shown, IReadOnlyList<ProjectDiffFile> NotShown) TrimToBudget(
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
		return (body.ToString().TrimEnd('\n'), [], diff.Files);
	}

	private const string ChangedFilesHeading = "Changed files (largest first):";
	private const string NotShownHeading = "Not shown:";

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
		var parts = new List<string>(4);
		Add("binary", diff.Count(ProjectDiffOmission.Binary));
		Add("too-large", diff.Count(ProjectDiffOmission.TooLarge));
		Add("line-endings-only", diff.Count(ProjectDiffOmission.LineEndingsOnly));
		Add("deleted", diff.DeletedOutsideSelection);
		return parts.Count == 0 ? null : "[Diff] not shown: " + string.Join(" · ", parts);

		void Add(string name, int count)
		{
			if (count > 0)
				parts.Add(name + "=" + count.ToString(CultureInfo.InvariantCulture));
		}
	}

	internal static string FormatDiffPartialNotice(int notShown) =>
		$"[Diff] partial · {(notShown == int.MaxValue ? "0000000" : notShown.ToString(CultureInfo.InvariantCulture))} " +
		"file(s) not shown to stay within 50,000 characters; narrow paths or include_patterns, or lower context_lines.";

	private static string? FormatGitScopeDiffNotice(McpJsonArguments arguments) =>
		arguments.OptionalString("git_scope") is null ? null : GitScopeDiffNotice;
}

internal sealed record McpDiffRendering(string Text, IReadOnlyList<string> DeliveredPaths);
