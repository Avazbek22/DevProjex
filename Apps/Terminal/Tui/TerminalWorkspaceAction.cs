using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.Tui;

internal enum TerminalWorkspaceActionKind
{
	Analyze,
	Search,
	PreviewView,
	PreviewFormat,
	Copy,
	OpenControls,
	FocusTree,
	FocusPreview,
	ClearFilter,
	ClearSearch,
	Quit,
	GitFiltering,
	Exclusions,
	FileTypes,
	ExportContext,
	ExportFolder,
	ExportZip,
	SaveProfile,
	OpenDesktop,
	SourceDetails,
	GetUpdates,
	SwitchBranch,
	RecentWorkspaces,
	Refresh,
	Language,
	Diagnostics,
	ReturnToWelcome,
	Help
}

internal sealed record TerminalWorkspaceAction(
	TerminalWorkspaceActionKind Kind,
	string Category,
	string Title,
	string Description,
	string Shortcut,
	string? Value = null,
	string? CommandSyntax = null,
	Func<bool>? IsAvailable = null,
	Action? Execute = null);

internal sealed class TerminalWorkspaceActionRow(TerminalWorkspaceAction action)
{
	public TerminalWorkspaceAction Action { get; } = action;

	public override string ToString()
	{
		var shortcut = string.IsNullOrWhiteSpace(Action.Shortcut)
			? "    "
			: $"[{Action.Shortcut}] ";
		var value = string.IsNullOrWhiteSpace(Action.Value)
			? string.Empty
			: $": {Action.Value}";
		return $"{shortcut}{Action.Title}{value}";
	}
}

internal sealed record TerminalPaletteItem(
	string Id,
	string Category,
	string Title,
	string Description,
	string Shortcut,
	string? Value,
	string? CommandSyntax,
	string? CommandId,
	Func<bool> IsAvailable,
	Action Execute);

internal sealed class TerminalPaletteRow(
	TerminalPaletteItem item,
	int titleColumns = 42,
	int totalColumns = 82)
{
	public TerminalPaletteItem Item { get; } = item;

	public override string ToString()
	{
		var shortcut = string.IsNullOrWhiteSpace(Item.Shortcut)
			? string.Empty
			: $"  [{Item.Shortcut}]";
		var value = string.IsNullOrWhiteSpace(Item.Value)
			? string.Empty
			: $": {Item.Value}";
		var title = Item.Title + value + shortcut;
		if (string.IsNullOrWhiteSpace(Item.CommandSyntax))
			return TerminalParameterRow.FitLabel(title, totalColumns, true);

		var leftWidth = Math.Clamp(titleColumns, 8, Math.Max(8, totalColumns - 4));
		var rightWidth = Math.Max(1, totalColumns - leftWidth - 2);
		var left = TerminalParameterRow.FitLabel(title, leftWidth, true);
		var right = TerminalParameterRow.FitLabel(
			":" + Item.CommandSyntax,
			rightWidth,
			true);
		return TerminalCellWidth.PadRight(left, leftWidth) + "  " + right;
	}
}

internal enum TerminalWorkspaceCommandExecutionStatus
{
	Success,
	Failure,
	Unavailable,
	Deferred
}

internal sealed record TerminalWorkspaceCommandExecutionResult(
	TerminalWorkspaceCommandExecutionStatus Status,
	string? Message = null)
{
	public static TerminalWorkspaceCommandExecutionResult Success(string? message = null) =>
		new(TerminalWorkspaceCommandExecutionStatus.Success, message);

	public static TerminalWorkspaceCommandExecutionResult Failure(string message) =>
		new(TerminalWorkspaceCommandExecutionStatus.Failure, message);

	public static TerminalWorkspaceCommandExecutionResult Unavailable(string? message = null) =>
		new(TerminalWorkspaceCommandExecutionStatus.Unavailable, message);

	public static TerminalWorkspaceCommandExecutionResult Deferred() =>
		new(TerminalWorkspaceCommandExecutionStatus.Deferred);
}

internal sealed record TerminalWorkspaceCommandAction(
	TerminalWorkspaceCommandDefinition Definition,
	Func<bool> IsAvailable,
	Func<TerminalWorkspaceCommand, TerminalWorkspaceCommandExecutionResult> Execute,
	Func<string?>? UnavailableMessage = null);

internal sealed class TerminalWorkspaceActionRegistry
{
	private readonly IReadOnlyDictionary<string, TerminalWorkspaceCommandAction> _commands;
	private readonly IReadOnlyDictionary<string, TerminalPaletteItem> _paletteItems;

	public TerminalWorkspaceActionRegistry(
		IEnumerable<TerminalPaletteItem> paletteItems,
		IEnumerable<TerminalWorkspaceCommandAction> commandActions)
	{
		ArgumentNullException.ThrowIfNull(paletteItems);
		ArgumentNullException.ThrowIfNull(commandActions);
		var commands = commandActions.ToArray();
		if (commands.GroupBy(static item => item.Definition.Id, StringComparer.Ordinal)
			.Any(static group => group.Count() > 1))
		{
			throw new ArgumentException("Command action ids must be unique.", nameof(commandActions));
		}
		var missing = TerminalWorkspaceCommandCatalog.All
			.Where(definition => commands.All(action => action.Definition.Id != definition.Id))
			.Select(static definition => definition.Id)
			.ToArray();
		if (missing.Length > 0)
		{
			throw new ArgumentException(
				$"Command handlers are missing: {string.Join(", ", missing)}.",
				nameof(commandActions));
		}

		_commands = commands.ToDictionary(
			static action => action.Definition.Id,
			StringComparer.Ordinal);

		PaletteItems = paletteItems.ToArray();
		if (PaletteItems.Any(static item => string.IsNullOrWhiteSpace(item.Id)))
			throw new ArgumentException("Every palette action must have a stable id.", nameof(paletteItems));
		if (PaletteItems.GroupBy(static item => item.Id, StringComparer.Ordinal).Any(static group => group.Count() > 1))
			throw new ArgumentException("Palette action ids must be unique.", nameof(paletteItems));
		foreach (var item in PaletteItems)
		{
			var hasSyntax = !string.IsNullOrWhiteSpace(item.CommandSyntax);
			var hasCommandId = !string.IsNullOrWhiteSpace(item.CommandId);
			if (hasSyntax != hasCommandId || hasCommandId && !_commands.ContainsKey(item.CommandId!))
			{
				throw new ArgumentException(
					$"Palette action '{item.Id}' has an invalid command binding.",
					nameof(paletteItems));
			}
		}
		_paletteItems = PaletteItems.ToDictionary(
			static item => item.Id,
			StringComparer.Ordinal);
	}

	public IReadOnlyList<TerminalPaletteItem> PaletteItems { get; }

	public TerminalWorkspaceCommandExecutionResult Execute(TerminalWorkspaceCommand command)
	{
		ArgumentNullException.ThrowIfNull(command);
		if (!_commands.TryGetValue(command.Definition.Id, out var action))
			return TerminalWorkspaceCommandExecutionResult.Failure(command.Definition.Id);
		return action.IsAvailable()
			? action.Execute(command)
			: TerminalWorkspaceCommandExecutionResult.Unavailable(
				action.UnavailableMessage?.Invoke());
	}

	public TerminalWorkspaceCommandExecutionResult Execute(TerminalPaletteItem item)
	{
		ArgumentNullException.ThrowIfNull(item);
		if (!_paletteItems.TryGetValue(item.Id, out var action))
			return TerminalWorkspaceCommandExecutionResult.Failure(item.Id);
		if (!action.IsAvailable() ||
		    action.CommandId is { } commandId &&
		    (!_commands.TryGetValue(commandId, out var command) || !command.IsAvailable()))
		{
			return TerminalWorkspaceCommandExecutionResult.Unavailable();
		}
		action.Execute();
		return TerminalWorkspaceCommandExecutionResult.Success();
	}
}
