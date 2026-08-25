using DevProjex.Terminal.Rendering;
using Terminal.Gui.Text;

namespace DevProjex.Terminal.Tui;

internal sealed class TerminalRecentWorkspaceRow(
	RecentWorkspaceDescriptor workspace,
	Func<RecentWorkspaceKind, string> kindLabel,
	Func<DateTimeOffset, string> openedLabel)
{
	private const int KindWidth = 10;
	private const int NameWidth = 28;

	public RecentWorkspaceDescriptor Workspace { get; } = workspace;
	public bool IsSelected { get; set; }

	public override string ToString()
	{
		var marker = IsSelected ? ">" : " ";
		var kind = FitToColumns(kindLabel(Workspace.Kind), KindWidth);
		var name = FitToColumns(
			TerminalRecentWorkspacePresentation.DisplayName(Workspace),
			NameWidth);
		var opened = openedLabel(Workspace.OpenedUtc);
		return $"{marker} {PadToColumns(kind, KindWidth)} " +
		       $"{PadToColumns(name, NameWidth)} {opened}";
	}

	internal static string FitToColumns(string value, int width)
	{
		value = TerminalTextEscaping.EscapeSingleLine(value);
		if (string.IsNullOrEmpty(value) || width <= 0)
			return string.Empty;
		if (value.GetColumns() <= width)
			return value;
		if (width <= 3)
			return new string('.', width);

		var builder = new StringBuilder();
		var available = width - 3;
		foreach (var rune in value.EnumerateRunes())
		{
			var columns = Math.Max(0, rune.GetColumns());
			if (columns > available)
				break;
			builder.Append(rune);
			available -= columns;
		}
		return builder.Append("...").ToString();
	}

	private static string PadToColumns(string value, int width) =>
		value + new string(' ', Math.Max(0, width - value.GetColumns()));
}

internal static class TerminalRecentWorkspacePresentation
{
	public static string DisplayName(RecentWorkspaceDescriptor workspace) =>
		TerminalTextEscaping.EscapeSingleLine(workspace.DisplayName);

	public static string DisplaySource(RecentWorkspaceDescriptor workspace) =>
		TerminalTextEscaping.EscapeSingleLine(workspace.DisplaySource);
}

internal enum TerminalRecentWorkspaceDecisionKind
{
	Back,
	Open,
	Remove
}

internal sealed record TerminalRecentWorkspaceDecision(
	TerminalRecentWorkspaceDecisionKind Kind,
	RecentWorkspaceDescriptor? Workspace = null);
