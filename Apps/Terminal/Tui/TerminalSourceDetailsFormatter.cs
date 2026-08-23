using System.Globalization;
using DevProjex.Terminal.Rendering;

namespace DevProjex.Terminal.Tui;

internal static class TerminalSourceDetailsFormatter
{
	public static string Format(
		string sourceRoot,
		ProjectSourceIdentity? identity,
		RepositoryCacheIndexEntry? cacheEntry,
		Func<string, string> localize,
		CultureInfo culture)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
		ArgumentNullException.ThrowIfNull(localize);
		ArgumentNullException.ThrowIfNull(culture);

		if (identity?.SourceType != ProjectSourceType.GitClone)
		{
			return Field(
				localize("Terminal.Tui.SourceReference"),
				TerminalTextEscaping.EscapeSingleLine(identity?.SourceReference ?? sourceRoot));
		}

		var repositoryUrl = RepositoryUrlUtility.ToSafeDisplay(
			identity.RepositoryUrl ?? identity.SourceReference);
		var fields = new List<string>();
		Add(fields, localize("Terminal.Tui.RepositoryUrl"), repositoryUrl);
		Add(
			fields,
			localize("Terminal.Tui.RecentRepositories.Branch"),
			identity.Branch ?? cacheEntry?.Branch);
		var commit = identity.CommitHash ?? cacheEntry?.CommitHash;
		Add(
			fields,
			localize("Terminal.Tui.Commit"),
			commit is null ? null : commit[..Math.Min(12, commit.Length)]);
		if (cacheEntry is not null)
		{
			Add(
				fields,
				localize("Terminal.Analysis.Size"),
				TerminalWorkspace.FormatBytes(cacheEntry.ApproximateSizeBytes, culture));
			Add(
				fields,
				localize("Terminal.Tui.Recent.LastOpened"),
				cacheEntry.LastUsedUtc.ToLocalTime().ToString("g", culture));
		}

		return string.Join(Environment.NewLine, fields);
	}

	private static void Add(ICollection<string> fields, string label, string? value)
	{
		if (!string.IsNullOrWhiteSpace(value))
			fields.Add(Field(label, TerminalTextEscaping.EscapeSingleLine(value)));
	}

	private static string Field(string label, string value) =>
		$"{label.TrimEnd().TrimEnd(':')}: {value}";
}
