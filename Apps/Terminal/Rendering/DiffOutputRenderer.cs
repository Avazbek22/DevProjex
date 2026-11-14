using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevProjex.Application.Diff;

namespace DevProjex.Terminal.Rendering;

/// <summary>
/// Renders a project diff for the command line and the Terminal Workspace. Every line ends with
/// '\n', as unified diff tools expect, on every platform.
/// </summary>
internal static class DiffOutputRenderer
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	public static string FormatText(ProjectDiffResult diff, LocalizationService localization)
	{
		ArgumentNullException.ThrowIfNull(diff);
		ArgumentNullException.ThrowIfNull(localization);
		if (!diff.HasChanges)
			return localization["Terminal.Diff.None"] + "\n";

		var output = new StringBuilder();
		foreach (var file in diff.Files.Where(static file => file.Omission == ProjectDiffOmission.None))
		{
			if (output.Length > 0)
				output.Append('\n');
			output.Append(ProjectDiffText.FormatFile(file));
		}
		var omitted = diff.Files.Where(static file => file.Omission != ProjectDiffOmission.None).ToArray();
		if (omitted.Length > 0)
		{
			if (output.Length > 0)
				output.Append('\n');
			output.Append(localization["Terminal.Diff.NotShown"]).Append('\n');
			foreach (var file in omitted)
				output.Append(ProjectDiffText.FormatListEntry(file)).Append('\n');
		}
		if (output.Length > 0)
			output.Append('\n');
		output.Append(localization.Format(
				"Terminal.Diff.Summary",
				diff.Files.Count,
				diff.ShownCount,
				diff.Added,
				diff.Removed))
			.Append('\n');
		if (diff.DeletedOutsideSelection > 0)
			output.Append(localization.Format("Terminal.Diff.Deleted", diff.DeletedOutsideSelection)).Append('\n');
		if (diff.NotInWorkingTree > 0)
		{
			output.Append(localization.Format("Terminal.Diff.NotInWorkingTree", diff.NotInWorkingTree)).Append('\n');
			if (diff.Mode == GitFilteringMode.Diff)
				output.Append(localization["Terminal.Diff.NotInWorkingTreeHint"]).Append('\n');
		}
		return output.ToString();
	}

	public static string FormatJson(ProjectDiffResult diff)
	{
		ArgumentNullException.ThrowIfNull(diff);
		var shown = diff.Files.Where(static file => file.Omission == ProjectDiffOmission.None).ToArray();
		var notShown = diff.Files.Where(static file => file.Omission != ProjectDiffOmission.None).ToArray();
		var document = new DiffDocument(
			SchemaVersion: 1,
			Kind: "devprojex-diff",
			Scope: GitScopeSelection.ToToken(diff.Mode, diff.Mode == GitFilteringMode.Diff ? diff.DiffRange : null),
			Files: shown.Select(static file => new DiffFileDocument(
					file.RelativePath,
					file.OldRelativePath,
					ProjectDiffText.Status(file.Status),
					file.Added,
					file.Removed,
					file.Hunks.Select(static hunk => new DiffHunkDocument(
							hunk.OldStart,
							hunk.OldLines,
							hunk.NewStart,
							hunk.NewLines,
							Lines(hunk)))
						.ToArray()))
				.ToArray(),
			NotShown: notShown.Select(static file => new DiffOmittedDocument(
					file.RelativePath,
					file.OldRelativePath,
					ProjectDiffText.Status(file.Status),
					ProjectDiffText.Omission(file.Omission)!))
				.ToArray(),
			Counts: new DiffCountsDocument(
				diff.Files.Count,
				shown.Length,
				diff.Added,
				diff.Removed,
				diff.Count(ProjectDiffOmission.Binary),
				diff.Count(ProjectDiffOmission.TooLarge),
				diff.Count(ProjectDiffOmission.ChangeTooLarge),
				diff.Count(ProjectDiffOmission.LineEndingsOnly),
				diff.Count(ProjectDiffOmission.ContentUnchanged),
				diff.DeletedOutsideSelection,
				diff.NotInWorkingTree),
			Complete: notShown.Length == 0 && diff.DeletedOutsideSelection == 0 && diff.NotInWorkingTree == 0);
		return JsonSerializer.Serialize(document, JsonOptions) + "\n";
	}

	private static string[] Lines(UnifiedDiffHunk hunk)
	{
		var lines = new List<string>(hunk.Lines.Count);
		foreach (var line in hunk.Lines)
		{
			lines.Add(UnifiedDiffFormatter.Prefix(line.Kind) + line.Text);
			if (line.IsUnterminated)
				lines.Add(UnifiedDiffFormatter.NoNewlineMarker);
		}
		return lines.ToArray();
	}

	private sealed record DiffDocument(
		int SchemaVersion,
		string Kind,
		string Scope,
		IReadOnlyList<DiffFileDocument> Files,
		IReadOnlyList<DiffOmittedDocument> NotShown,
		DiffCountsDocument Counts,
		bool Complete);

	private sealed record DiffFileDocument(
		string Path,
		string? OldPath,
		string Status,
		int Added,
		int Removed,
		IReadOnlyList<DiffHunkDocument> Hunks);

	private sealed record DiffHunkDocument(
		int OldStart,
		int OldLines,
		int NewStart,
		int NewLines,
		IReadOnlyList<string> Lines);

	private sealed record DiffOmittedDocument(string Path, string? OldPath, string Status, string Reason);

	private sealed record DiffCountsDocument(
		int Files,
		int Shown,
		int Added,
		int Removed,
		int Binary,
		int TooLarge,
		int ChangeTooLarge,
		int LineEndingsOnly,
		int ContentUnchanged,
		int Deleted,
		int NotInWorkingTree);
}
