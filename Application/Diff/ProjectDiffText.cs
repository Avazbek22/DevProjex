using System.Globalization;

namespace DevProjex.Application.Diff;

/// <summary>
/// The plain-text form of a project diff shared by every surface. Paths are escaped onto one
/// line and content keeps its tabs, but any other control character in a line is escaped, so one
/// diff line is always one physical line and cannot drive a terminal.
/// </summary>
public static class ProjectDiffText
{
	public static string ScopeKind(GitFilteringMode mode) => mode switch
	{
		GitFilteringMode.Changes => "changes",
		GitFilteringMode.Staged => "staged",
		GitFilteringMode.Diff => "diff",
		_ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
	};

	public static string Status(ProjectDiffFileStatus status) => status switch
	{
		ProjectDiffFileStatus.Added => "added",
		ProjectDiffFileStatus.Modified => "modified",
		ProjectDiffFileStatus.Deleted => "deleted",
		ProjectDiffFileStatus.Renamed => "renamed",
		_ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
	};

	public static string? Omission(ProjectDiffOmission omission) => omission switch
	{
		ProjectDiffOmission.None => null,
		ProjectDiffOmission.Binary => "binary",
		ProjectDiffOmission.TooLarge => "too-large",
		ProjectDiffOmission.ChangeTooLarge => "change-too-large",
		ProjectDiffOmission.LineEndingsOnly => "line-endings-only",
		ProjectDiffOmission.ContentUnchanged => "content-unchanged",
		_ => throw new ArgumentOutOfRangeException(nameof(omission), omission, null)
	};

	/// <summary>"path +A -R [status]", or "path [status, reason]" for a file listed without hunks.</summary>
	public static string FormatListEntry(ProjectDiffFile file)
	{
		ArgumentNullException.ThrowIfNull(file);
		var status = file.Status == ProjectDiffFileStatus.Renamed && file.OldRelativePath is not null
			? "renamed from " + EscapePath(file.OldRelativePath)
			: Status(file.Status);
		return Omission(file.Omission) is { } reason
			? $"{EscapePath(file.RelativePath)} [{status}, {reason}]"
			: string.Create(
				CultureInfo.InvariantCulture,
				$"{EscapePath(file.RelativePath)} +{file.Added} -{file.Removed} [{status}]");
	}

	/// <summary>The "---"/"+++" header and every hunk of one file, each line ended by '\n'.</summary>
	public static string FormatFile(ProjectDiffFile file)
	{
		ArgumentNullException.ThrowIfNull(file);
		using var writer = new StringWriter(CultureInfo.InvariantCulture);
		WriteFile(writer, file);
		return writer.ToString();
	}

	public static void WriteFile(TextWriter writer, ProjectDiffFile file)
	{
		ArgumentNullException.ThrowIfNull(writer);
		ArgumentNullException.ThrowIfNull(file);
		writer.Write(file.Status == ProjectDiffFileStatus.Added
			? "--- /dev/null"
			: "--- a/" + EscapePath(file.OldRelativePath ?? file.RelativePath));
		writer.Write('\n');
		writer.Write(file.Status == ProjectDiffFileStatus.Deleted
			? "+++ /dev/null"
			: "+++ b/" + EscapePath(file.RelativePath));
		writer.Write('\n');
		foreach (var hunk in file.Hunks)
			UnifiedDiffFormatter.WriteHunk(writer, hunk, EscapeLine);
	}

	public static string EscapePath(string path) => SingleLineTextEscaping.Escape(path);

	public static string EscapeLine(string line) => SingleLineTextEscaping.Escape(line, preserveTabs: true);
}
