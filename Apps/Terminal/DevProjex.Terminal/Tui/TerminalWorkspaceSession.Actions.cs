using System.Collections.ObjectModel;
using System.Globalization;
using DevProjex.Terminal.CommandLine;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DevProjex.Terminal.Tui;

#pragma warning disable CS0618

internal sealed partial class TerminalWorkspaceSession
{
	private FrameView? _controlsFrame;
	private TextView? _controlsSummary;
	private ListView? _controls;
	private ObservableCollection<TerminalWorkspaceActionRow>? _controlRows;
	private TerminalPreviewPresentation _previewPresentation = TerminalPreviewPresentation.Readable;

	private void CreateContextControls()
	{
		_controlsFrame = new FrameView
		{
			BorderStyle = LineStyle.Single,
			SchemeName = TerminalWorkspaceTheme.Panel
		};
		_controlsSummary = new TextView
		{
			X = 1,
			Y = 0,
			Width = Dim.Fill(1),
			Height = 8,
			ReadOnly = true,
			WordWrap = true,
			CanFocus = false,
			SchemeName = TerminalWorkspaceTheme.Base
		};
		_controls = new ListView
		{
			X = 0,
			Y = 8,
			Width = Dim.Fill(),
			Height = Dim.Fill(),
			ShowMarks = false,
			SchemeName = TerminalWorkspaceTheme.List
		};
		_controls.Accepted += (_, _) => _application.Invoke(ActivateSelectedControl);
		_controls.HasFocusChanged += (_, _) => UpdateWorkspaceFocus();
		_controlsFrame.Add(_controlsSummary, _controls);
		RefreshContextControls();
	}

	private void RefreshContextControls()
	{
		if (_state is null || _controls is null || _controlsSummary is null)
			return;

		var selectedIndex = Math.Max(0, _controls.SelectedItem ?? 0);
		_controlRows = new ObservableCollection<TerminalWorkspaceActionRow>(
			BuildWorkspaceActions().Select(static action => new TerminalWorkspaceActionRow(action)));
		_controls.SetSource(_controlRows);
		if (_controlRows.Count > 0)
			_controls.SelectedItem = Math.Clamp(selectedIndex, 0, _controlRows.Count - 1);

		var identity = _state.Plan.SourceIdentity;
		var source = identity?.RepositoryUrl ?? identity?.SourceReference ?? _state.Plan.SourceRoot;
		var sourceWidth = _layoutMode == TerminalWorkspaceLayoutMode.Wide
			? Math.Max(12, _terminalWidth / 3 - 5)
			: Math.Max(12, _terminalWidth - 6);
		var branch = identity?.Branch is { Length: > 0 }
			? $"\n{L("Terminal.Tui.RecentRepositories.Branch")}: {identity.Branch}"
			: string.Empty;
		_controlsSummary.Text =
			$"{L("Terminal.Tui.Source")}: {GetProjectDisplayName(_state.Plan)}\n" +
			$"{FitPathToWidth(source, sourceWidth)}{branch}\n" +
			$"{L("Terminal.Tui.Profile")}: " +
			$"{FormatProfileSource(_state.Plan.Selection.ProfileSource)}\n" +
			$"{L("Terminal.Tui.GitFiltering")}: " +
			$"{FormatGitMode(_state.Plan.GitReadiness.Mode)}\n" +
			$"{L("Terminal.Analysis.Files")}: {_state.SelectedFileCount:N0}  " +
			$"{L("Terminal.Analysis.Folders")}: {_state.SelectedFolderCount:N0}\n" +
			$"~{_state.Plan.Analysis.Metrics.Content.Tokens:N0} " +
			$"{L("Terminal.Tui.TokensShort")}  " +
			$"{L("Terminal.Tui.Warnings")}: {_state.Plan.Diagnostics.Count:N0}";
	}

	private IReadOnlyList<TerminalWorkspaceAction> BuildWorkspaceActions()
	{
		if (_state is null)
			return [];

		var plan = _state.Plan;
		var actions = new List<TerminalWorkspaceAction>
		{
			CreateAction(
				TerminalWorkspaceActionKind.Analyze,
				"Terminal.Tui.Diagnostics",
				"Terminal.Tui.Analyze",
				"Terminal.Command.Analyze",
				"A"),
			CreateAction(
				TerminalWorkspaceActionKind.Search,
				"Terminal.Tui.Selection",
				"Terminal.Tui.Search",
				"Terminal.Tui.SearchPrompt",
				"/"),
			CreateAction(
				TerminalWorkspaceActionKind.PreviewPresentation,
				"Terminal.Tui.Preview",
				"Terminal.Tui.Action.Presentation",
				"Terminal.Tui.Action.Presentation.Description",
				"V",
				LocalizePreviewPresentation()),
			CreateAction(
				TerminalWorkspaceActionKind.PreviewView,
				"Terminal.Tui.Preview",
				"Terminal.Tui.View",
				"Terminal.Option.PreviewView",
				"1/2/3",
				_workspace.LocalizeView(_previewView)),
			CreateAction(
				TerminalWorkspaceActionKind.PreviewFormat,
				"Terminal.Tui.Preview",
				"Terminal.Tui.Format",
				"Terminal.Option.DocumentFormat",
				"F",
				_format.ToString()),
			CreateAction(
				TerminalWorkspaceActionKind.GitFiltering,
				"Terminal.Tui.Selection",
				"Terminal.Tui.GitFiltering",
				"Terminal.Option.GitMode",
				"M",
				FormatGitMode(plan.GitReadiness.Mode)),
			CreateAction(
				TerminalWorkspaceActionKind.Exclusions,
				"Terminal.Tui.Selection",
				"Terminal.Tui.Exclusions",
				"Terminal.Analysis.Exclusions",
				"X",
				FormatExclusions(plan.Selection.Exclusions ?? [])),
			CreateAction(
				TerminalWorkspaceActionKind.RootFolders,
				"Terminal.Tui.Selection",
				"Terminal.Tui.RootFolders",
				"Terminal.Option.Root",
				"R",
				FormatSelectionCount(plan.SelectedRoots.Count, plan.AvailableRoots.Count)),
			CreateAction(
				TerminalWorkspaceActionKind.FileTypes,
				"Terminal.Tui.Selection",
				"Terminal.Tui.FileTypes",
				"Terminal.Option.Extension",
				"T",
				FormatSelectionCount(
					plan.SelectedExtensions.Count,
					plan.AvailableExtensions.Count)),
			CreateAction(
				TerminalWorkspaceActionKind.ExportContext,
				"Terminal.Tui.Export",
				"Terminal.Tui.ExportContext",
				"Terminal.Command.ExportContext",
				"E"),
			CreateAction(
				TerminalWorkspaceActionKind.ExportFolder,
				"Terminal.Tui.Export",
				"Menu.File.ExportProjectCopy.Folder",
				"Menu.File.ExportProjectCopy.Folder.Help",
				"Z"),
			CreateAction(
				TerminalWorkspaceActionKind.ExportZip,
				"Terminal.Tui.Export",
				"Menu.File.ExportProjectCopy.Zip",
				"Menu.File.ExportProjectCopy.Zip.Help",
				"Z"),
			CreateAction(
				TerminalWorkspaceActionKind.SaveProfile,
				"Terminal.Tui.Profile",
				"Terminal.Tui.SaveProfile",
				"Terminal.Command.ProfileExport",
				"P",
				FormatProfileSource(plan.Selection.ProfileSource)),
			CreateAction(
				TerminalWorkspaceActionKind.OpenDesktop,
				"Terminal.Tui.Profile",
				"Terminal.Tui.Welcome.OpenDesktop",
				"Terminal.Tui.Welcome.OpenDesktop.Description",
				"G"),
			CreateAction(
				TerminalWorkspaceActionKind.SourceDetails,
				"Terminal.Tui.Source",
				"Terminal.Tui.Details",
				"Terminal.Tui.Action.SourceDetails.Description",
				string.Empty)
		};

		if (plan.SourceIdentity?.SourceType == ProjectSourceType.GitClone)
		{
			actions.Add(CreateAction(
				TerminalWorkspaceActionKind.GetUpdates,
				"Terminal.Tui.RecentRepositories.Repository",
				"Terminal.Tui.Action.GetUpdates",
				"Terminal.Tui.Action.GetUpdates.Description",
				string.Empty));
			actions.Add(CreateAction(
				TerminalWorkspaceActionKind.SwitchBranch,
				"Terminal.Tui.RecentRepositories.Repository",
				"Terminal.Tui.Action.SwitchBranch",
				"Terminal.Tui.Action.SwitchBranch.Description",
				string.Empty));
			actions.Add(CreateAction(
				TerminalWorkspaceActionKind.RecentRepositories,
				"Terminal.Tui.RecentRepositories.Repository",
				"Terminal.Tui.Welcome.RecentRepositories",
				"Terminal.Tui.Welcome.RecentRepositories.Description",
				string.Empty));
		}

		return actions;
	}

	private TerminalWorkspaceAction CreateAction(
		TerminalWorkspaceActionKind kind,
		string categoryKey,
		string titleKey,
		string descriptionKey,
		string shortcut,
		string? value = null) =>
		new(kind, L(categoryKey), L(titleKey), L(descriptionKey), shortcut, value);

	private string FormatGitMode(GitFilteringMode mode) =>
		mode switch
		{
			GitFilteringMode.RespectGitIgnore => ".gitignore",
			GitFilteringMode.TrackedFilesOnly => L("Terminal.Tui.GitTracked"),
			_ => L("Terminal.Tui.GitNone")
		};

	private string FormatExclusions(IReadOnlyCollection<ProjectExclusion> exclusions) =>
		exclusions.Count == 0
			? L("Terminal.Tui.NoneAvailable")
			: exclusions.Count.ToString("N0", CultureInfo.CurrentCulture);

	private string FormatSelectionCount(int selected, int available) =>
		selected == available
			? $"{L("Terminal.Profile.All")} ({available:N0})"
			: $"{selected:N0}/{available:N0}";

	private string FormatProfileSource(ProjectProfileReference? source) =>
		source?.Kind switch
		{
			ProjectProfileSourceKind.Local => L("Terminal.Profile.Local"),
			ProjectProfileSourceKind.Portable => L("Terminal.Profile.Portable"),
			_ => L("Terminal.Profile.Standard")
		};

	private void ActivateSelectedControl()
	{
		if (_controlRows is null || _controls?.SelectedItem is not { } selected ||
		    selected < 0 || selected >= _controlRows.Count)
		{
			return;
		}

		ExecuteWorkspaceAction(_controlRows[selected].Action.Kind);
	}

	private void ExecuteWorkspaceAction(TerminalWorkspaceActionKind action)
	{
		switch (action)
		{
			case TerminalWorkspaceActionKind.Analyze:
				AnalyzeCurrentContext();
				break;
			case TerminalWorkspaceActionKind.Search:
				SearchTree();
				break;
			case TerminalWorkspaceActionKind.PreviewPresentation:
				SelectPreviewPresentation();
				break;
			case TerminalWorkspaceActionKind.PreviewView:
				SelectPreviewView();
				break;
			case TerminalWorkspaceActionKind.PreviewFormat:
				SelectPreviewFormat();
				break;
			case TerminalWorkspaceActionKind.OpenControls:
				FocusPane(TerminalWorkspacePane.Controls);
				break;
			case TerminalWorkspaceActionKind.GitFiltering:
				ChangeGitMode();
				break;
			case TerminalWorkspaceActionKind.Exclusions:
				ChangeExclusions();
				break;
			case TerminalWorkspaceActionKind.RootFolders:
				ChangeRootsOrTypes(roots: true);
				break;
			case TerminalWorkspaceActionKind.FileTypes:
				ChangeRootsOrTypes(roots: false);
				break;
			case TerminalWorkspaceActionKind.ExportContext:
				ExportContext();
				break;
			case TerminalWorkspaceActionKind.ExportFolder:
				ExportProject(ProjectCopyExportFormat.Folder);
				break;
			case TerminalWorkspaceActionKind.ExportZip:
				ExportProject(ProjectCopyExportFormat.Zip);
				break;
			case TerminalWorkspaceActionKind.SaveProfile:
				SaveProfile();
				break;
			case TerminalWorkspaceActionKind.OpenDesktop:
				OpenCurrentStateInDesktop();
				break;
			case TerminalWorkspaceActionKind.SourceDetails:
				ShowSourceDetails();
				break;
			case TerminalWorkspaceActionKind.GetUpdates:
				GetRepositoryUpdates();
				break;
			case TerminalWorkspaceActionKind.SwitchBranch:
				SwitchRepositoryBranch();
				break;
			case TerminalWorkspaceActionKind.RecentRepositories:
				ShowWelcome();
				_application.Invoke(BeginOpenRecentRepositories);
				break;
			case TerminalWorkspaceActionKind.ReturnToWelcome:
				if (Confirm(L("Terminal.Tui.BackToWelcome"), L("Terminal.Tui.ConfirmBackToWelcome")))
					ShowWelcome();
				break;
			case TerminalWorkspaceActionKind.Help:
				ShowHelp(welcome: false);
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(action), action, null);
		}
	}

	private void ShowActionPalette()
	{
		var items = _screen == TerminalWorkspaceScreen.Welcome
			? BuildWelcomePaletteItems()
			: BuildWorkspacePaletteItems();
		if (items.Count == 0)
			return;

		var width = ResolveDialogWidth(92);
		var preferredHeight = Math.Min(items.Count + 10, 28);
		var height = Math.Clamp(
			preferredHeight,
			14,
			Math.Max(14, _application.Screen.Height - 2));
		using var dialog = CreateDialog(L("Terminal.Tui.ActionPalette"), width, height);
		var prompt = new Label
		{
			X = 1,
			Y = 0,
			Text = L("Terminal.Tui.ActionPalette.Search"),
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		var input = new TextField
		{
			X = 1,
			Y = 1,
			Width = Dim.Fill(1),
			SchemeName = TerminalWorkspaceTheme.List
		};
		var rows = new ObservableCollection<TerminalPaletteRow>();
		var list = new ListView
		{
			X = 1,
			Y = 3,
			Width = Dim.Fill(1),
			Height = Dim.Fill(6),
			SchemeName = TerminalWorkspaceTheme.List
		};
		list.SetSource(rows);
		var detail = new TextView
		{
			X = 1,
			Y = Pos.AnchorEnd(5),
			Width = Dim.Fill(1),
			Height = 4,
			ReadOnly = true,
			WordWrap = true,
			CanFocus = false,
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		TerminalPaletteItem? selectedItem = null;

		void Refresh()
		{
			var filter = input.Text?.ToString() ?? string.Empty;
			var filtered = items
				.Where(item =>
					filter.Length == 0 ||
					item.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
					item.Description.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
				.ToArray();
			rows.Clear();
			foreach (var item in filtered)
				rows.Add(new TerminalPaletteRow(item));
			if (rows.Count > 0)
				list.SelectedItem = 0;
			detail.Text = rows.Count > 0
				? BuildPaletteDetail(rows[0].Item)
				: L("Terminal.Tui.NoneAvailable");
			list.SetNeedsDraw();
		}

		void UpdateDetail()
		{
			if (list.SelectedItem is { } index && index >= 0 && index < rows.Count)
				detail.Text = BuildPaletteDetail(rows[index].Item);
		}

		void Accept()
		{
			if (list.SelectedItem is not { } index || index < 0 || index >= rows.Count)
				return;
			selectedItem = rows[index].Item;
			_application.RequestStop(dialog);
		}

		void MoveResultSelection(int delta)
		{
			if (rows.Count == 0)
				return;
			var current = Math.Clamp(list.SelectedItem ?? 0, 0, rows.Count - 1);
			list.SelectedItem = Math.Clamp(current + delta, 0, rows.Count - 1);
			UpdateDetail();
		}

		input.TextChanged += (_, _) => Refresh();
		input.KeyDown += (_, key) =>
		{
			if (key == Key.CursorDown || key.NoShift == Key.J.WithCtrl)
			{
				key.Handled = true;
				MoveResultSelection(1);
			}
			else if (key == Key.CursorUp || key.NoShift == Key.K.WithCtrl)
			{
				key.Handled = true;
				MoveResultSelection(-1);
			}
			else if (key == Key.PageDown || key == Key.PageUp)
			{
				key.Handled = true;
				MoveResultSelection(key == Key.PageDown ? 5 : -5);
			}
			else if (key == Key.Enter)
			{
				key.Handled = true;
				Accept();
			}
		};
		list.ValueChanged += (_, _) => UpdateDetail();
		list.Accepted += (_, _) => Accept();
		dialog.Add(prompt, input, list, detail);
		dialog.AddButton(new Button { Text = L("Terminal.Tui.Back") });
		var execute = new Button { Text = L("Terminal.Tui.ActionPalette.Run") };
		execute.Accepted += (_, _) => Accept();
		dialog.AddButton(execute);
		Refresh();
		RunOverlay(dialog, input);
		if (selectedItem is not null)
			_application.Invoke(selectedItem.Execute);
	}

	private IReadOnlyList<TerminalPaletteItem> BuildWorkspacePaletteItems()
	{
		var actions = BuildWorkspaceActions().ToList();
		actions.Add(CreateAction(
			TerminalWorkspaceActionKind.OpenControls,
			"Terminal.Tui.Source",
			"Terminal.Tui.ContextControls",
			"Terminal.Tui.Action.OpenControls.Description",
			"Tab/F6"));
		actions.Add(CreateAction(
			TerminalWorkspaceActionKind.ReturnToWelcome,
			"Terminal.Tui.Source",
			"Terminal.Tui.BackToWelcome",
			"Terminal.Tui.ConfirmBackToWelcome",
			"Esc"));
		actions.Add(CreateAction(
			TerminalWorkspaceActionKind.Help,
			"Terminal.Tui.Source",
			"Terminal.Tui.Help",
			"Terminal.Tui.Welcome.Help.Description",
			"?"));
		return actions
			.Select(action => new TerminalPaletteItem(
				action.Category,
				action.Title,
				action.Description,
				action.Shortcut,
				action.Value,
				() => ExecuteWorkspaceAction(action.Kind)))
			.ToArray();
	}

	private IReadOnlyList<TerminalPaletteItem> BuildWelcomePaletteItems()
	{
		if (_welcomeContext is null)
			return [];
		return BuildWelcomeActions(_welcomeContext)
			.Select(action => new TerminalPaletteItem(
				L("Terminal.Tui.Actions"),
				action.Title,
				action.Description,
				string.Empty,
				null,
				() => ActivateWelcomeAction(action.Kind)))
			.ToArray();
	}

	private static string BuildPaletteDetail(TerminalPaletteItem item)
	{
		var value = string.IsNullOrWhiteSpace(item.Value)
			? string.Empty
			: $" · {item.Value}";
		return $"{item.Category} · {item.Title}{value}\n{item.Description}";
	}

	private void ActivateWelcomeAction(TerminalWelcomeActionKind kind)
	{
		if (_welcomeRows is null || _welcomeList is null)
			return;
		for (var index = 0; index < _welcomeRows.Count; index++)
		{
			if (_welcomeRows[index].Action.Kind != kind)
				continue;
			_welcomeList.SelectedItem = index;
			ActivateWelcomeSelection();
			return;
		}
	}

	private void SelectPreviewPresentation()
	{
		var values = new[]
		{
			L("Terminal.Tui.Preview.Readable"),
			L("Terminal.Tui.Preview.Raw")
		};
		var selected = SelectFromList(
			L("Terminal.Tui.Action.Presentation"),
			L("Terminal.Tui.Action.Presentation.Description"),
			values);
		if (selected is null)
			return;
		_previewPresentation = string.Equals(selected, values[0], StringComparison.Ordinal)
			? TerminalPreviewPresentation.Readable
			: TerminalPreviewPresentation.RawOutput;
		RefreshWorkspace();
		SchedulePreviewRefresh();
	}

	private void SelectPreviewView()
	{
		var values = Enum.GetValues<ProjectContextView>()
			.Select(_workspace.LocalizeView)
			.ToArray();
		var selected = SelectFromList(
			L("Terminal.Tui.Action.PreviewView"),
			L("Terminal.Tui.Action.PreviewView.Description"),
			values);
		var index = Array.IndexOf(values, selected);
		if (index < 0)
			return;
		_previewView = Enum.GetValues<ProjectContextView>()[index];
		RefreshWorkspace();
		SchedulePreviewRefresh();
	}

	private void SelectPreviewFormat()
	{
		var formats = Enum.GetValues<ProjectContextDocumentFormat>();
		var values = formats.Select(static format => format.ToString()).ToArray();
		var selected = SelectFromList(
			L("Terminal.Tui.Action.Format"),
			L("Terminal.Tui.Action.Format.Description"),
			values);
		var index = Array.IndexOf(values, selected);
		if (index < 0)
			return;
		_format = formats[index];
		RefreshWorkspace();
		SchedulePreviewRefresh();
	}

	private string LocalizePreviewPresentation() =>
		L(_previewPresentation == TerminalPreviewPresentation.Readable
			? "Terminal.Tui.Preview.Readable"
			: "Terminal.Tui.Preview.Raw");

	private void AnalyzeCurrentContext()
	{
		if (_state is null)
			return;
		_activeOperationTask = RunOperationAsync(
			L("Terminal.Tui.Analyze"),
			async token =>
			{
				var plan = await _controller.BuildCurrentPlanAsync(_state, token)
					.ConfigureAwait(false);
				return $"{L("Terminal.Analysis.Files")}: {plan.IncludedFiles.Count}\n" +
				       $"{L("Terminal.Analysis.Folders")}: {plan.IncludedFolders.Count}\n" +
				       $"{L("Terminal.Analysis.Characters")}: {plan.Analysis.Metrics.Content.Chars:N0}\n" +
				       $"{L("Terminal.Analysis.Tokens")}: {plan.Analysis.Metrics.Content.Tokens:N0}\n" +
				       $"{L("Terminal.Tui.Diagnostics")}: {plan.Diagnostics.Count}\n" +
				       $"{L("Terminal.Analysis.Fingerprint")}: {plan.Fingerprint}";
			});
	}

	private void OpenCurrentStateInDesktop()
	{
		if (_state is null)
			return;
		_activeOperationTask = RunOperationAsync(
			L("Terminal.Tui.Welcome.OpenDesktop"),
			async token =>
			{
				var exitCode = await _controller.OpenDesktopAsync(_state, token)
					.ConfigureAwait(false);
				return exitCode == CommandLineExitCodes.Success
					? L("Terminal.Tui.DesktopAccepted")
					: throw new TerminalWorkspaceOperationException("DPX-DESKTOP-REQUEST-FAILED");
			});
	}

	private void ShowSourceDetails()
	{
		if (_state is null)
			return;
		var identity = _state.Plan.SourceIdentity;
		var text = new StringBuilder()
			.Append(L("Terminal.Tui.Source")).Append(": ")
			.AppendLine(GetProjectDisplayName(_state.Plan))
			.Append(L("Terminal.Tui.SourceReference")).Append(": ")
			.AppendLine(identity?.SourceReference ?? _state.Plan.SourceRoot);
		if (identity?.RepositoryUrl is { Length: > 0 } repositoryUrl)
			text.Append(L("Terminal.Tui.RepositoryUrl")).AppendLine().AppendLine(repositoryUrl);
		if (identity?.Branch is { Length: > 0 } branch)
			text.Append(L("Terminal.Tui.RecentRepositories.Branch")).Append(": ").AppendLine(branch);
		if (identity?.CommitHash is { Length: > 0 } commit)
			text.Append(L("Terminal.Tui.Commit")).Append(": ").AppendLine(commit[..Math.Min(12, commit.Length)]);
		if (identity?.IsCachedRepository == true)
		{
			text.AppendLine()
				.Append(L("Terminal.Tui.InternalCachePath"))
				.AppendLine(":")
				.Append(_state.Plan.SourceRoot);
		}
		ShowNotice(
			L("Terminal.Tui.Details"),
			text.ToString(),
			TerminalWorkspaceTheme.Dialog);
	}

	private void GetRepositoryUpdates()
	{
		if (_state?.Plan.SourceIdentity?.SourceType != ProjectSourceType.GitClone)
			return;
		_activeOperationTask = RunOperationAsync(
			L("Terminal.Tui.Action.GetUpdates"),
			async token =>
			{
				var updated = await _services.GitRepositoryService
					.PullUpdatesAsync(_state.Plan.SourceRoot, cancellationToken: token)
					.ConfigureAwait(false);
				if (!updated)
					throw new TerminalWorkspaceOperationException("DPX-TUI-GIT-UPDATE-FAILED");
				await _controller
					.RebuildRepositoryAsync(_state, _state.BuildSelection(), token)
					.ConfigureAwait(false);
				return L("Terminal.Tui.RepositoryUpdated");
			});
	}

	private void SwitchRepositoryBranch()
	{
		if (_state?.Plan.SourceIdentity?.SourceType != ProjectSourceType.GitClone)
			return;
		_activeOperationTask = RunOperationAsync(
			L("Terminal.Tui.Action.SwitchBranch"),
			async token =>
			{
				var branches = await _services.GitRepositoryService
					.GetBranchesAsync(_state.Plan.SourceRoot, token)
					.ConfigureAwait(false);
				var branchNames = branches
					.Select(static branch => branch.Name)
					.Distinct(StringComparer.Ordinal)
					.ToArray();
				var selected = await InvokeAsync(() => SelectFromList(
					L("Terminal.Tui.Action.SwitchBranch"),
					L("Terminal.Tui.Action.SwitchBranch.Description"),
					branchNames)).ConfigureAwait(false);
				if (string.IsNullOrWhiteSpace(selected))
					return null;
				var switched = await _services.GitRepositoryService
					.SwitchBranchAsync(
						_state.Plan.SourceRoot,
						selected,
						cancellationToken: token)
					.ConfigureAwait(false);
				if (!switched)
					throw new TerminalWorkspaceOperationException("DPX-TUI-GIT-BRANCH-FAILED");
				await _controller
					.RebuildRepositoryAsync(_state, _state.BuildSelection(), token)
					.ConfigureAwait(false);
				return $"{L("Terminal.Tui.RecentRepositories.Branch")}: {selected}";
			});
	}

	private void FocusPane(TerminalWorkspacePane pane)
	{
		_activePane = pane;
		ApplyWorkspaceLayout();
		var view = pane switch
		{
			TerminalWorkspacePane.Tree => (View?)_tree,
			TerminalWorkspacePane.Preview => _preview,
			_ => _controls
		};
		view?.SetFocus();
		UpdateWorkspaceFocus();
	}
}

#pragma warning restore CS0618
