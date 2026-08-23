using System.Collections.ObjectModel;
using System.Globalization;
using DevProjex.Application.Secrets;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DevProjex.Terminal.Tui;

#pragma warning disable CS0618

internal sealed partial class TerminalWorkspaceSession
{
	private const int WideControlsWidth = 38;
	private const int ContentControlsFrameHeight = 7;

	private FrameView? _controlsFrame;
	private Label? _controlsPanelHeading;
	private Label? _profileSourceLabel;
	private FrameView? _contentControlsFrame;
	private FrameView? _exclusionControlsFrame;
	private FrameView? _extensionControlsFrame;
	private View? _filterControlsHost;
	private TerminalParameterListView? _contentControls;
	private TerminalParameterListView? _exclusionAllControl;
	private TerminalParameterListView? _exclusionControls;
	private TerminalParameterListView? _extensionAllControl;
	private TerminalParameterListView? _extensionControls;
	private ObservableCollection<TerminalParameterRow>? _contentControlRows;
	private ObservableCollection<TerminalParameterRow>? _exclusionAllControlRows;
	private ObservableCollection<TerminalParameterRow>? _exclusionControlRows;
	private ObservableCollection<TerminalParameterRow>? _extensionAllControlRows;
	private ObservableCollection<TerminalParameterRow>? _extensionControlRows;
	private string? _selectedContentControlKey;
	private string? _selectedExclusionControlKey;
	private string? _selectedExtensionControlKey;
	private TerminalControlSection _activeControlSection = TerminalControlSection.Content;
	private TerminalControlSection? _activeAggregateControlSection;
	private GitFilteringMode _preferredGitMode = GitFilteringMode.RespectGitIgnore;

	private void CreateContextControls()
	{
		_controlsFrame = new TerminalLiteralFrameView
		{
			BorderStyle = _presentation.BorderStyle,
			SchemeName = TerminalWorkspaceTheme.Panel
		};
		_controlsPanelHeading = new TerminalLiteralLabel
		{
			X = 0,
			Y = 0,
			Width = Dim.Fill(),
			Height = 1,
			Visible = _options.Plain,
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		_profileSourceLabel = new TerminalLiteralLabel
		{
			X = 0,
			Y = _options.Plain ? 1 : 0,
			Width = Dim.Fill(),
			Height = 1,
			SchemeName = TerminalWorkspaceTheme.Secondary
		};

		(_contentControlsFrame, _contentControls) = CreateControlSection(
			L("Settings.Secrets.Title"),
			TerminalControlSection.Content,
			showVerticalScrollBar: false);
		_contentControlsFrame.Y = _options.Plain ? 1 : 0;
		_contentControlsFrame.Height = ContentControlsFrameHeight;

		_filterControlsHost = new View
		{
			X = 0,
			Y = Pos.Bottom(_contentControlsFrame),
			Width = Dim.Fill(),
			Height = Dim.Fill()
		};
		(_exclusionControlsFrame, _exclusionControls) = CreateControlSection(
			L("Terminal.Tui.Exclusions"),
			TerminalControlSection.Exclusions,
			showVerticalScrollBar: true);
		_exclusionAllControl = AddAggregateControl(
			_exclusionControlsFrame,
			_exclusionControls,
			TerminalControlSection.Exclusions);
		_exclusionControlsFrame.Height = Dim.Percent(50);
		(_extensionControlsFrame, _extensionControls) = CreateControlSection(
			L("Terminal.Tui.FileTypes"),
			TerminalControlSection.Extensions,
			showVerticalScrollBar: true);
		_extensionAllControl = AddAggregateControl(
			_extensionControlsFrame,
			_extensionControls,
			TerminalControlSection.Extensions);
		_extensionControlsFrame.Y = Pos.Bottom(_exclusionControlsFrame);
		_extensionControlsFrame.Height = Dim.Fill();
		_filterControlsHost.Add(_exclusionControlsFrame, _extensionControlsFrame);
		_controlsFrame.Add(
			_controlsPanelHeading,
			_profileSourceLabel,
			_contentControlsFrame,
			_filterControlsHost);
		if (_state?.Plan.GitReadiness.Mode is not GitFilteringMode.None and { } mode)
			_preferredGitMode = mode;
		RefreshContextControls();
	}

	private (FrameView Frame, TerminalParameterListView List) CreateControlSection(
		string title,
		TerminalControlSection section,
		bool showVerticalScrollBar)
	{
		var frame = new TerminalLiteralFrameView
		{
			X = 0,
			Y = 0,
			Width = Dim.Fill(),
			Title = title,
			BorderStyle = _presentation.BorderStyle,
			SchemeName = TerminalWorkspaceTheme.Panel
		};
		var list = new TerminalParameterListView(
			showVerticalScrollBar,
			_environment.SupportsUnicode && !_options.Plain)
		{
			X = 0,
			Y = 0,
			Width = Dim.Fill(),
			Height = Dim.Fill(),
			ShowMarks = false,
			SchemeName = TerminalWorkspaceTheme.List
		};
		list.SelectionToggleRequested += (_, _) =>
			_application.Invoke(() =>
			{
				_activePane = TerminalWorkspacePane.Controls;
				_activeControlSection = section;
				_activeAggregateControlSection = null;
				UpdateWorkspaceFocus();
				ActivateSelectedControl(section);
			});
		list.InteractionStarted += (_, _) =>
		{
			_activePane = TerminalWorkspacePane.Controls;
			_activeControlSection = section;
			_activeAggregateControlSection = null;
			UpdateWorkspaceFocus();
		};
		list.ValueChanged += (_, _) => TrackSelectedControl(section);
		list.HasFocusChanged += (_, _) => UpdateWorkspaceFocus();
		frame.Add(list);
		return (frame, list);
	}

	private TerminalParameterListView AddAggregateControl(
		FrameView frame,
		TerminalParameterListView rows,
		TerminalControlSection section)
	{
		rows.Y = 1;
		rows.Height = Dim.Fill();
		var aggregate = new TerminalParameterListView
		{
			X = 0,
			Y = 0,
			Width = Dim.Fill(),
			Height = 1,
			ShowMarks = false,
			SchemeName = TerminalWorkspaceTheme.List
		};
		aggregate.SelectionToggleRequested += (_, _) =>
			_application.Invoke(() =>
			{
				_activePane = TerminalWorkspacePane.Controls;
				_activeControlSection = section;
				_activeAggregateControlSection = section;
				UpdateWorkspaceFocus();
				ActivateAggregateControl(section);
			});
		aggregate.InteractionStarted += (_, _) =>
		{
			_activePane = TerminalWorkspacePane.Controls;
			_activeControlSection = section;
			_activeAggregateControlSection = section;
			UpdateWorkspaceFocus();
		};
		aggregate.HasFocusChanged += (_, _) => UpdateWorkspaceFocus();
		frame.Add(aggregate);
		return aggregate;
	}

	private void RefreshContextControls()
	{
		if (_state is null || _contentControls is null ||
			_exclusionAllControl is null || _exclusionControls is null ||
			_extensionAllControl is null || _extensionControls is null)
			return;

		TrackSelectedControl(TerminalControlSection.Content);
		TrackSelectedControl(TerminalControlSection.Exclusions);
		TrackSelectedControl(TerminalControlSection.Extensions);
		_contentControlRows = ReplaceControlRows(
			_contentControls,
			BuildContentParameterRows(),
			_selectedContentControlKey);
		_exclusionAllControlRows = ReplaceControlRows(
			_exclusionAllControl,
			[_parameterRowsBuilder.BuildExclusionAggregate(_state.Plan)],
			"exclusions:all");
		_exclusionControlRows = ReplaceControlRows(
			_exclusionControls,
			BuildExclusionParameterRows(),
			_selectedExclusionControlKey);
		_extensionAllControlRows = ReplaceControlRows(
			_extensionAllControl,
			[_parameterRowsBuilder.BuildExtensionAggregate(_state.Plan)],
			"extensions:all");
		_extensionControlRows = ReplaceControlRows(
			_extensionControls,
			BuildExtensionParameterRows(),
			_selectedExtensionControlKey);
		RefreshControlTitles();
		RefreshProfileSource();
	}

	private void RefreshProfileSource()
	{
		if (_profileSourceLabel is null || _contentControlsFrame is null)
			return;
		var text = TerminalProfileSourcePresentation.Format(
			_state?.Plan.Selection.ProfileSource,
			L("Terminal.Tui.SavedSettings"),
			L("Terminal.Tui.Settings.Project"),
			L("Terminal.Tui.Settings.File"),
			ResolveControlLabelWidth(markerColumns: 2),
			_environment.SupportsUnicode && !_options.Plain);
		var visible = text is not null;
		_profileSourceLabel.Visible = visible;
		_profileSourceLabel.Text = text ?? string.Empty;
		var headingHeight = _options.Plain ? 1 : 0;
		_profileSourceLabel.Y = headingHeight;
		_contentControlsFrame.Y = headingHeight + (visible ? 1 : 0);
	}

	private ObservableCollection<TerminalParameterRow> ReplaceControlRows(
		TerminalParameterListView list,
		IReadOnlyList<TerminalParameterRow> rows,
		string? selectedKey)
	{
		var source = new ObservableCollection<TerminalParameterRow>(rows);
		list.SetSource(source);
		if (source.Count > 0)
		{
			var selectedIndex = selectedKey is null
				? 0
				: source
					.Select((row, index) => (row, index))
					.FirstOrDefault(pair => pair.row.Key == selectedKey)
					.index;
			list.SelectedItem = Math.Clamp(selectedIndex, 0, source.Count - 1);
		}
		return source;
	}

	private IReadOnlyList<TerminalParameterRow> BuildContentParameterRows() =>
		_state is null
			? []
			: _parameterRowsBuilder.BuildContent(
				_state.Plan,
				GetContentRedactionSnapshot(_state.Plan));

	private IReadOnlyList<TerminalParameterRow> BuildExclusionParameterRows() =>
		_state is null ? [] : _parameterRowsBuilder.BuildExclusions(_state.Plan);

	private IReadOnlyList<TerminalParameterRow> BuildExtensionParameterRows() =>
		_state is null ? [] : _parameterRowsBuilder.BuildExtensions(_state.Plan);

	private SecretRedactionSnapshot? GetContentRedactionSnapshot(ProjectContextPlan plan)
	{
		var features = SecretRedactionFeatureSelection.Resolve(
			plan.Selection.HideSecrets == true,
			plan.Selection.HidePrivateData == true);
		if (features == SecretRedactionFeatures.None)
			return null;
		var kinds = CodeTransformIdentity.Resolve(
			plan.Selection.CompressCode == true,
			plan.Selection.StripComments == true,
			plan.Selection.StripBlankLines == true);
		var transformIdentity = kinds == CodeTransformKinds.None
			? string.Empty
			: _services.CodeCompressionSession.GetTransformIdentity(kinds);
		return _services.SecretRedactionSession.GetSnapshot(
			plan.SourceRoot,
			plan.IncludedFiles,
			transformIdentity,
			features);
	}

	private string FitControlLabel(string value) =>
		TerminalParameterRow.FitLabel(
			value,
			ResolveControlLabelWidth(markerColumns: 6),
			_environment.SupportsUnicode && !_options.Plain);

	private string FitControlInformationLabel(string value) =>
		TerminalParameterRow.FitLabel(
			value,
			ResolveControlLabelWidth(markerColumns: 2),
			_environment.SupportsUnicode && !_options.Plain);

	private int ResolveControlLabelWidth(int markerColumns)
	{
		var panelWidth = _layoutMode == TerminalWorkspaceLayoutMode.Wide
			? WideControlsWidth
			: Math.Max(1, _terminalWidth);
		return Math.Max(4, panelWidth - markerColumns - 2);
	}

	private void TrackSelectedControl(TerminalControlSection section)
	{
		var (list, rows) = GetControlSection(section);
		if (list?.SelectedItem is not { } index || rows is null ||
			index < 0 || index >= rows.Count)
		{
			return;
		}
		SetSelectedControlKey(section, rows[index].Key);
	}

	private (TerminalParameterListView? List, ObservableCollection<TerminalParameterRow>? Rows)
		GetControlSection(TerminalControlSection section) =>
		section switch
		{
			TerminalControlSection.Content => (_contentControls, _contentControlRows),
			TerminalControlSection.Exclusions => (_exclusionControls, _exclusionControlRows),
			TerminalControlSection.Extensions => (_extensionControls, _extensionControlRows),
			_ => throw new ArgumentOutOfRangeException(nameof(section), section, null)
		};

	private FrameView? GetControlSectionFrame(TerminalControlSection section) =>
		section switch
		{
			TerminalControlSection.Content => _contentControlsFrame,
			TerminalControlSection.Exclusions => _exclusionControlsFrame,
			TerminalControlSection.Extensions => _extensionControlsFrame,
			_ => throw new ArgumentOutOfRangeException(nameof(section), section, null)
		};

	private void SetSelectedControlKey(TerminalControlSection section, string key)
	{
		switch (section)
		{
			case TerminalControlSection.Content:
				_selectedContentControlKey = key;
				break;
			case TerminalControlSection.Exclusions:
				_selectedExclusionControlKey = key;
				break;
			case TerminalControlSection.Extensions:
				_selectedExtensionControlKey = key;
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(section), section, null);
		}
	}

	private void RefreshControlTitles()
	{
		if (_contentControlsFrame is not null)
			_contentControlsFrame.Title = L("Settings.Secrets.Title");
		if (_exclusionControlsFrame is not null)
			_exclusionControlsFrame.Title = L("Terminal.Tui.Exclusions");
		if (_extensionControlsFrame is not null)
			_extensionControlsFrame.Title = L("Terminal.Tui.FileTypes");
	}

	private bool ControlsHaveFocus =>
		_contentControls?.HasFocus == true ||
		_exclusionAllControl?.HasFocus == true ||
		_exclusionControls?.HasFocus == true ||
		_extensionAllControl?.HasFocus == true ||
		_extensionControls?.HasFocus == true;

	private TerminalControlSection ResolveFocusedControlSection() =>
		_exclusionAllControl?.HasFocus == true || _exclusionControls?.HasFocus == true
			? TerminalControlSection.Exclusions
			: _extensionAllControl?.HasFocus == true || _extensionControls?.HasFocus == true
				? TerminalControlSection.Extensions
				: _contentControls?.HasFocus == true
					? TerminalControlSection.Content
					: _activeControlSection;

	private TerminalParameterListView? ActiveControlList =>
		GetControlSection(_activeControlSection).List;

	private View? ActiveControlView =>
		IsAggregateControlFocused(_activeControlSection) ||
		_activeAggregateControlSection == _activeControlSection
			? GetAggregateControlSection(_activeControlSection).List ?? ActiveControlList
			: ActiveControlList;

	private bool IsAggregateControlFocused(TerminalControlSection section) =>
		GetAggregateControlSection(section).List?.HasFocus == true;

	private void FocusControlSection(TerminalControlSection section, bool movePane = true)
	{
		_activeControlSection = section;
		if (movePane)
		{
			_activePane = TerminalWorkspacePane.Controls;
			ApplyWorkspaceLayout();
		}
		var target = GetAggregateControlSection(section).List ?? GetControlSection(section).List;
		_activeAggregateControlSection = GetAggregateControlSection(section).List is null
			? null
			: section;
		target?.SetFocus();
		// Layout and focus notifications can report the previously focused list while a
		// single-pane transition is being completed. The requested section is authoritative.
		_activePane = TerminalWorkspacePane.Controls;
		_activeControlSection = section;
		UpdateWorkspaceFocus();
	}

	private enum TerminalControlSection
	{
		Content,
		Exclusions,
		Extensions
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
				TerminalWorkspaceActionKind.PreviewView,
				"Terminal.Tui.Preview",
				"Terminal.Tui.Action.PreviewView",
				"Terminal.Tui.Action.PreviewView.Description",
				"1/2/3",
				_workspace.LocalizeView(_previewView)),
			CreateAction(
				TerminalWorkspaceActionKind.PreviewFormat,
				"Terminal.Tui.Preview",
				"Terminal.Tui.Action.Format",
				"Terminal.Tui.Action.Format.Description",
				"F",
				TerminalWorkspace.FormatContextFormat(_format)),
			CreateAction(
				TerminalWorkspaceActionKind.GitFiltering,
				"Terminal.Tui.Selection",
				"Terminal.Tui.GitFiltering",
				"Terminal.Tui.Action.FocusExclusions.Description",
				"M",
				FormatGitMode(plan.GitReadiness.Mode)),
			CreateAction(
				TerminalWorkspaceActionKind.Exclusions,
				"Terminal.Tui.Selection",
				"Terminal.Tui.Exclusions",
				"Terminal.Tui.Action.FocusExclusions.Description",
				"X",
				FormatExclusions(plan.Selection.Exclusions ?? [])),
			CreateAction(
				TerminalWorkspaceActionKind.FileTypes,
				"Terminal.Tui.Selection",
				"Terminal.Tui.FileTypes",
				"Terminal.Tui.Action.FocusFileTypes.Description",
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
				"P"),
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
				TerminalWorkspaceActionKind.RecentWorkspaces,
				"Terminal.Tui.RecentRepositories.Repository",
				"Terminal.Tui.Welcome.Recent",
				"Terminal.Tui.Welcome.Recent.Description",
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
		L(ProjectPresentationCatalog.Get(mode).LabelKey);

	private string FormatExclusions(IReadOnlyCollection<ProjectExclusion> exclusions)
	{
		var pathExclusionCount = ProjectPresentationCatalog.Exclusions.Count(
			descriptor => exclusions.Contains(descriptor.RequireId()));
		return pathExclusionCount == 0
			? L("Terminal.Tui.NoneAvailable")
			: pathExclusionCount.ToString("N0", CultureInfo.CurrentCulture);
	}

	private string FormatSelectionCount(int selected, int available) =>
		selected == available
			? $"{L("Settings.All")} ({available:N0})"
			: $"{selected:N0}/{available:N0}";

	private (TerminalParameterListView? List, ObservableCollection<TerminalParameterRow>? Rows)
		GetAggregateControlSection(TerminalControlSection section) =>
		section switch
		{
			TerminalControlSection.Exclusions => (_exclusionAllControl, _exclusionAllControlRows),
			TerminalControlSection.Extensions => (_extensionAllControl, _extensionAllControlRows),
			_ => (null, null)
		};

	private void ActivateAggregateControl(TerminalControlSection section)
	{
		var (_, rows) = GetAggregateControlSection(section);
		if (rows is not { Count: > 0 })
			return;
		ActivateControlRow(section, rows[0]);
	}

	private void ActivateSelectedControl(TerminalControlSection section)
	{
		var (list, rows) = GetControlSection(section);
		if (rows is null || list?.SelectedItem is not { } selected ||
			selected < 0 || selected >= rows.Count)
		{
			return;
		}

		var row = rows[selected];
		SetSelectedControlKey(section, row.Key);
		ActivateControlRow(section, row);
	}

	private void ActivateControlRow(TerminalControlSection section, TerminalParameterRow row)
	{
		switch (row.Kind)
		{
			case TerminalParameterRowKind.Information:
				return;
			case TerminalParameterRowKind.GitMode when row.GitMode is { } mode:
				ApplyGitMode(mode);
				return;
			case TerminalParameterRowKind.ToggleAllExclusions:
				ApplyAllExclusions(row.IsSelected != true);
				return;
			case TerminalParameterRowKind.ContentTransformation
				when row.ContentTransformation is { } transformation:
				ApplyContentTransformation(transformation, row.IsSelected != true);
				return;
			case TerminalParameterRowKind.Exclusion when row.Exclusion is { } exclusion:
				{
					var values = (_state?.Plan.Selection.Exclusions ?? []).ToHashSet();
					if (!values.Add(exclusion))
						values.Remove(exclusion);
					ApplyExclusions(values);
					return;
				}
			case TerminalParameterRowKind.ToggleAllExtensions:
				ApplyExtensions(
					row.IsSelected == true
						? []
						: _state?.Plan.AvailableExtensions ?? []);
				return;
			case TerminalParameterRowKind.Extension when row.Value is { } extension:
				{
					var values = (_state?.Plan.SelectedExtensions ?? [])
						.ToHashSet(StringComparer.OrdinalIgnoreCase);
					if (!values.Add(extension))
						values.Remove(extension);
					ApplyExtensions(values);
					return;
				}
		}
	}

	private void ApplyGitMode(GitFilteringMode mode)
	{
		if (_state is null)
			return;
		var target = _state.Plan.GitReadiness.Mode == mode
			? GitFilteringMode.None
			: mode;
		if (target != GitFilteringMode.None)
			_preferredGitMode = target;
		ApplyPathFilters(target, _state.Plan.Selection.Exclusions ?? []);
	}

	private void ApplyAllExclusions(bool enabled)
	{
		if (_state is null)
			return;
		var mode = enabled
			? _state.Plan.GitReadiness.Mode == GitFilteringMode.None
				? _preferredGitMode
				: _state.Plan.GitReadiness.Mode
			: GitFilteringMode.None;
		var exclusions = enabled
			? ProjectPresentationCatalog.Exclusions
				.Select(static descriptor => descriptor.RequireId())
				.ToArray()
			: [];
		ApplyPathFilters(mode, exclusions);
	}

	private void ApplyExclusions(IReadOnlyCollection<ProjectExclusion> exclusions)
	{
		if (_state is null)
			return;
		ApplyPathFilters(_state.Plan.GitReadiness.Mode, exclusions);
	}

	private void ApplyPathFilters(
		GitFilteringMode mode,
		IReadOnlyCollection<ProjectExclusion> exclusions)
	{
		if (_state is null)
			return;
		PreserveControlFocusForOperation(TerminalControlSection.Exclusions);
		var state = _state;
		_activeOperationTask = RunOperationAsync(
			L("Terminal.Tui.Exclusions"),
			async token =>
			{
				await _controller.SetPathFilteringAsync(state, mode, exclusions, token)
					.ConfigureAwait(false);
				return null;
			});
	}

	private void ApplyContentTransformation(IgnoreOptionId optionId, bool enabled)
	{
		if (_state is null)
			return;
		PreserveControlFocusForOperation(TerminalControlSection.Content);
		var state = _state;
		_activeOperationTask = RunOperationAsync(
			L("Settings.Secrets.Title"),
			token =>
			{
				_controller.SetContentTransformation(state, optionId, enabled, token);
				return Task.FromResult<string?>(null);
			});
	}

	private void ApplyExtensions(IReadOnlyCollection<string> extensions)
	{
		if (_state is null)
			return;
		PreserveControlFocusForOperation(TerminalControlSection.Extensions);
		var state = _state;
		_activeOperationTask = RunOperationAsync(
			L("Terminal.Tui.FileTypes"),
			async token =>
			{
				await _controller.SetExtensionsAsync(state, extensions, token).ConfigureAwait(false);
				return null;
			});
	}

	private void PreserveControlFocusForOperation(TerminalControlSection section)
	{
		_activePane = TerminalWorkspacePane.Controls;
		_activeControlSection = section;
		_activePaneBeforeBusy = TerminalWorkspacePane.Controls;
		_activeControlSectionBeforeBusy = section;
		_aggregateControlSectionBeforeBusy = IsAggregateControlFocused(section) ||
			_activeAggregateControlSection == section
			? section
			: null;
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
				FocusControlSection(TerminalControlSection.Exclusions);
				break;
			case TerminalWorkspaceActionKind.Exclusions:
				FocusControlSection(TerminalControlSection.Exclusions);
				break;
			case TerminalWorkspaceActionKind.FileTypes:
				FocusControlSection(TerminalControlSection.Extensions);
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
			case TerminalWorkspaceActionKind.RecentWorkspaces:
				ShowWelcome();
				_application.Invoke(OpenRecentWorkspaces);
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
		var prompt = new TerminalLiteralLabel
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
		dialog.AddButton(CreateDialogButton(L("Terminal.Tui.Back")));
		var execute = CreateDialogButton(L("Terminal.Tui.ActionPalette.Run"));
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
			.Append(new TerminalPaletteItem(
				L("Terminal.Tui.Actions"),
				L("Terminal.Tui.Welcome.OpenProfile"),
				L("Terminal.Tui.Welcome.OpenProfile.Description"),
				string.Empty,
				null,
				OpenPortableProfile))
			.ToArray();
	}

	private string BuildPaletteDetail(TerminalPaletteItem item)
	{
		var value = string.IsNullOrWhiteSpace(item.Value)
			? string.Empty
			: $"{PanelSeparator}{item.Value}";
		return $"{item.Category}{PanelSeparator}{item.Title}{value}\n{item.Description}";
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

	private void SelectPreviewView()
	{
		var modes = ProjectPresentationCatalog.PreviewModes;
		var values = modes
			.Select(mode => L(mode.LabelKey))
			.ToArray();
		var selected = SelectFromList(
			L("Terminal.Tui.Action.PreviewView"),
			L("Terminal.Tui.Action.PreviewView.Description"),
			values,
			preferredWidth: 56);
		var index = Array.IndexOf(values, selected);
		if (index < 0)
			return;
		_previewView = modes[index].Id;
		RefreshWorkspace();
		SchedulePreviewRefresh();
	}

	private void SelectPreviewFormat()
	{
		var formats = ProjectPresentationCatalog.Formats;
		var values = formats.Select(static format => format.UserLabel).ToArray();
		var selected = SelectFromList(
			L("Terminal.Tui.Action.Format"),
			L("Terminal.Tui.Action.Format.Description"),
			values,
			preferredWidth: 56);
		var index = Array.IndexOf(values, selected);
		if (index < 0)
			return;
		_format = formats[index].Id;
		RefreshWorkspace();
		SchedulePreviewRefresh();
	}

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
				var warnings = plan.Diagnostics.Count(static diagnostic =>
					diagnostic.Severity == ContextDiagnosticSeverity.Warning);
				var errors = plan.Diagnostics.Count(static diagnostic =>
					diagnostic.Severity == ContextDiagnosticSeverity.Error);
				return $"{L("Terminal.Analysis.Files")}: {plan.IncludedFiles.Count}\n" +
					   $"{L("Terminal.Analysis.Folders")}: {plan.IncludedFolders.Count}\n" +
					   $"{L("Terminal.Analysis.Characters")}: {plan.Analysis.Metrics.Content.Chars:N0}\n" +
					   $"{L("Terminal.Analysis.Tokens")}: {plan.Analysis.Metrics.Content.Tokens:N0}\n" +
					   $"{L("Terminal.Tui.Warnings")}: {warnings:N0}\n" +
					   $"{L("Terminal.Label.Error")}: {errors:N0}";
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
			_ => ActiveControlView
		};
		view?.SetFocus();
		UpdateWorkspaceFocus();
	}
}

#pragma warning restore CS0618
