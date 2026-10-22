namespace DevProjex.Application.Services;

/// <summary>
/// The question every surface asks before replacing a devprojex entry DevProjex did not write in its own form.
/// </summary>
public static class McpExistingEntryConflictPresentation
{
	public static string Describe(LocalizationService localization, string clientName, McpExistingEntryConflict conflict)
	{
		ArgumentNullException.ThrowIfNull(localization);
		ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
		ArgumentNullException.ThrowIfNull(conflict);
		// The existing command comes from a configuration file; escape it so it cannot reshape the dialog.
		var existing = SingleLineTextEscaping.Escape(conflict.ExistingCommand);
		var proposed = SingleLineTextEscaping.Escape(conflict.ProposedCommand);
		var message = localization.Format(
			conflict.IsDevProjexLauncher
				? "Mcp.Connect.ExistingEntry.LauncherPrompt"
				: "Mcp.Connect.ExistingEntry.ForeignPrompt",
			clientName,
			existing,
			proposed);
		if (conflict.UnkeptArguments.Count > 0)
		{
			message += "\n\n" + localization.Format(
				"Mcp.Connect.ExistingEntry.UnkeptArguments",
				SingleLineTextEscaping.Escape(string.Join(' ', conflict.UnkeptArguments)));
		}
		return message;
	}
}
