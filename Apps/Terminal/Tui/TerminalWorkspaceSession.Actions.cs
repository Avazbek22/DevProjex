using System.Collections.ObjectModel;
using System.Globalization;
using DevProjex.Application.Secrets;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DevProjex.Terminal.Tui;

#pragma warning disable CS0618

internal sealed partial class TerminalWorkspaceSession
{
	private const int WideControlsWidth = 38;
	private const int ContentControlsFrameHeight = 7;
	private const int AggregateFramePaddingColumns = 3;
	private const int AggregateTrailingBorderColumns = 3;

	private FrameView? _controlsFrame;
	private Label? _controlsPanelHeading;
	private FrameView? _contentControlsFrame;
	private FrameView? _exclusionControlsFrame;
	private FrameView? _extensionControlsFrame;
	private View? _filterControlsHost;
	private TerminalParameterListView? _contentControls;
	private TerminalAggregateControl? _contentAllControl;
	private TerminalAggregateControl? _exclusionAllControl;
	private TerminalParameterListView? _exclusionControls;
	private TerminalAggregateControl? _extensionAllControl;
	private TerminalParameterListView? _extensionControls;
	private ObservableCollection<TerminalParameterRow>? _contentControlRows;
	private ObservableCollection<TerminalParameterRow>? _contentAllControlRows;
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
	private ProjectSelectionSpec? _settingsDraftSelection;
	private Dictionary<string, bool>? _settingsDraftExtensionStates;
	private bool _settingsDraftOriginatedFromCommandLine;

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
		(_contentControlsFrame, _contentControls) = CreateControlSection(
			NormalizeControlTitle(L("Settings.Secrets.Title")),
			TerminalControlSection.Content,
			showVerticalScrollBar: false);
		_contentAllControl = AddAggregateControl(
			_contentControlsFrame,
			_contentControls,
			TerminalControlSection.Content);
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
			NormalizeControlTitle(L("Terminal.Tui.Exclusions")),
			TerminalControlSection.Exclusions,
			showVerticalScrollBar: true);
		_exclusionAllControl = AddAggregateControl(
			_exclusionControlsFrame,
			_exclusionControls,
			TerminalControlSection.Exclusions);
		_exclusionControlsFrame.Height = Dim.Percent(50);
		(_extensionControlsFrame, _extensionControls) = CreateControlSection(
			NormalizeControlTitle(L("Terminal.Tui.FileTypes")),
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
			SchemeName = TerminalWorkspaceTheme.InactiveList
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
		list.CommandLineRequested += (_, _) => OpenCommandLine();
		frame.Add(list);
		return (frame, list);
	}

	private TerminalAggregateControl AddAggregateControl(
		FrameView frame,
		TerminalParameterListView rows,
		TerminalControlSection section)
	{
		var onBorder = !_options.Plain && frame.Border.View is not null;
		rows.Y = onBorder ? 0 : 1;
		rows.Height = Dim.Fill();
		var aggregate = new TerminalAggregateControl(onBorder)
		{
			X = onBorder ? Pos.AnchorEnd(1) : 0,
			Y = 0,
			SchemeName = TerminalWorkspaceTheme.InactiveList
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
		aggregate.CommandLineRequested += (_, _) => OpenCommandLine();
		if (onBorder)
			frame.Border.View!.Add(aggregate);
		else
			frame.Add(aggregate);
		return aggregate;
	}

	private void RefreshContextControls()
	{
		if (_state is null || _contentAllControl is null || _contentControls is null ||
			_exclusionAllControl is null || _exclusionControls is null ||
			_extensionAllControl is null || _extensionControls is null)
			return;

		TrackSelectedControl(TerminalControlSection.Content);
		TrackSelectedControl(TerminalControlSection.Exclusions);
		TrackSelectedControl(TerminalControlSection.Extensions);
		var selection = GetDisplayedSettingsSelection();
		var selectedExtensions = selection.Extensions ?? _state.Plan.SelectedExtensions;
		_contentAllControlRows = ReplaceAggregateRow(
			_contentAllControl,
			_parameterRowsBuilder.BuildContentAggregate(selection));
		_contentControlRows = ReplaceControlRows(
			_contentControls,
			BuildContentParameterRows(),
			_selectedContentControlKey);
		_exclusionAllControlRows = ReplaceAggregateRow(
			_exclusionAllControl,
			_parameterRowsBuilder.BuildExclusionAggregate(_state.Plan, selection));
		_exclusionControlRows = ReplaceControlRows(
			_exclusionControls,
			BuildExclusionParameterRows(),
			_selectedExclusionControlKey);
		_extensionAllControlRows = ReplaceAggregateRow(
			_extensionAllControl,
			_parameterRowsBuilder.BuildExtensionAggregate(_state.Plan, selectedExtensions));
		_extensionControlRows = ReplaceControlRows(
			_extensionControls,
			BuildExtensionParameterRows(),
			_selectedExtensionControlKey);
		RefreshControlTitles();
		UpdateControlSelectionSchemes();
	}

	private ObservableCollection<TerminalParameterRow> ReplaceAggregateRow(
		TerminalAggregateControl control,
		TerminalParameterRow row)
	{
		control.SetRow(row);
		if (control.IsOnBorder)
			control.X = Pos.AnchorEnd(
				control.Text.GetColumns() + AggregateTrailingBorderColumns);
		return new ObservableCollection<TerminalParameterRow>([row]);
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
				GetContentRedactionSnapshot(_state.Plan),
				GetDisplayedSettingsSelection());

	private IReadOnlyList<TerminalParameterRow> BuildExclusionParameterRows() =>
		_state is null
			? []
			: _parameterRowsBuilder.BuildExclusions(
				_state.Plan,
				GetDisplayedSettingsSelection());

	private IReadOnlyList<TerminalParameterRow> BuildExtensionParameterRows() =>
		_state is null
			? []
			: _parameterRowsBuilder.BuildExtensions(
				_state.Plan,
				GetDisplayedSettingsSelection().Extensions ?? _state.Plan.SelectedExtensions);

	private ProjectSelectionSpec GetDisplayedSettingsSelection() =>
		_settingsDraftSelection ?? _state?.Plan.Selection ?? ProjectSelectionSpec.Standard;

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
			ResolveControlLabelWidth(markerColumns: 4),
			_environment.SupportsUnicode && !_options.Plain);

	private int ResolveControlLabelWidth(int markerColumns)
	{
		var panelWidth = _layoutMode == TerminalWorkspaceLayoutMode.Wide
			? WideControlsWidth
			: Math.Max(1, _terminalWidth);
		return Math.Max(4, panelWidth - markerColumns - 4);
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
			_contentControlsFrame.Title = ResolveAggregateFrameTitle(
				L("Settings.Secrets.Title"),
				_contentAllControl);
		if (_exclusionControlsFrame is not null)
			_exclusionControlsFrame.Title = ResolveAggregateFrameTitle(
				L("Terminal.Tui.Exclusions"),
				_exclusionAllControl);
		if (_extensionControlsFrame is not null)
			_extensionControlsFrame.Title = ResolveAggregateFrameTitle(
				L("Terminal.Tui.FileTypes"),
				_extensionAllControl);
	}

	private static string NormalizeControlTitle(string value) =>
		TerminalFrameTitle.Normalize(value);

	private string ResolveAggregateFrameTitle(
		string value,
		TerminalAggregateControl? aggregate)
	{
		var aggregateColumns = aggregate?.Text.GetColumns() ?? 0;
		var maxColumns = Math.Max(
			4,
			WideControlsWidth - aggregateColumns - AggregateFramePaddingColumns - 4);
		return TerminalFrameTitle.Fit(
			value,
			maxColumns,
			_environment.SupportsUnicode && !_options.Plain);
	}

	private bool ControlsHaveFocus =>
		_contentAllControl?.HasFocus == true || _contentControls?.HasFocus == true ||
		_exclusionAllControl?.HasFocus == true ||
		_exclusionControls?.HasFocus == true ||
		_extensionAllControl?.HasFocus == true ||
		_extensionControls?.HasFocus == true;

	private TerminalControlSection ResolveFocusedControlSection() =>
		_exclusionAllControl?.HasFocus == true || _exclusionControls?.HasFocus == true
			? TerminalControlSection.Exclusions
			: _extensionAllControl?.HasFocus == true || _extensionControls?.HasFocus == true
				? TerminalControlSection.Extensions
				: _contentAllControl?.HasFocus == true || _contentControls?.HasFocus == true
					? TerminalControlSection.Content
					: _activeControlSection;

	private TerminalParameterListView? ActiveControlList =>
		GetControlSection(_activeControlSection).List;

	private View? ActiveControlView =>
		IsAggregateControlFocused(_activeControlSection) ||
		_activeAggregateControlSection == _activeControlSection
			? (View?)GetAggregateControlSection(_activeControlSection).List ?? ActiveControlList
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
		var target = (View?)GetAggregateControlSection(section).List ??
		             GetControlSection(section).List;
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
		var selection = GetDisplayedSettingsSelection();
		var displayedExtensions = selection.Extensions ?? plan.SelectedExtensions;
		var displayedExtensionSet = displayedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var actions = new List<TerminalWorkspaceAction>
		{
			CreateAction(
				TerminalWorkspaceActionKind.Analyze,
				"Terminal.Tui.Diagnostics",
				"Terminal.Tui.Analyze",
				"Terminal.Command.Analyze",
				"A",
				commandSyntax: TerminalWorkspaceCommandCatalog.Get(
					TerminalWorkspaceCommandVerb.Analyze).Syntax,
				execute: () => AnalyzeCurrentContext()),
			CreateAction(
				TerminalWorkspaceActionKind.Search,
				"Terminal.Tui.Selection",
				"Terminal.Tui.Search",
				"Terminal.Tui.SearchPrompt",
				"/",
				commandSyntax: _activePane == TerminalWorkspacePane.Preview
					? TerminalWorkspaceCommandCatalog.Get(TerminalWorkspaceCommandVerb.Search).Syntax
					: TerminalWorkspaceCommandCatalog.Get(TerminalWorkspaceCommandVerb.Filter).Syntax,
				execute: () =>
				{
					if (_activePane == TerminalWorkspacePane.Preview)
						SearchPreview();
					else
						SearchTree();
				}),
			CreateAction(
				TerminalWorkspaceActionKind.PreviewView,
				"Terminal.Tui.Preview",
				"Terminal.Tui.Action.PreviewView",
				"Terminal.Tui.Action.PreviewView.Description",
				"1/2/3",
				_workspace.LocalizeView(_previewView),
				TerminalWorkspaceCommandCatalog.Get(TerminalWorkspaceCommandVerb.View).Syntax,
				execute: SelectPreviewView),
			CreateAction(
				TerminalWorkspaceActionKind.PreviewFormat,
				"Terminal.Tui.Preview",
				"Terminal.Tui.Action.Format",
				"Terminal.Tui.Action.Format.Description",
				"F",
				TerminalWorkspace.FormatContextFormat(_format),
				TerminalWorkspaceCommandCatalog.Get(TerminalWorkspaceCommandVerb.Format).Syntax,
				execute: SelectPreviewFormat),
			CreateAction(
				TerminalWorkspaceActionKind.Copy,
				"Terminal.Tui.Preview",
				"Terminal.Tui.Command.Copy.Title",
				"Terminal.Tui.Command.Copy.Description",
				string.Empty,
				commandSyntax: TerminalWorkspaceCommandCatalog.Get(
					TerminalWorkspaceCommandVerb.Copy).Syntax,
				execute: () => CopyCurrentContext(new TerminalWorkspaceCommand(
					TerminalWorkspaceCommandCatalog.Get(TerminalWorkspaceCommandVerb.Copy)))),
			CreateAction(
				TerminalWorkspaceActionKind.GitFiltering,
				"Terminal.Tui.Selection",
				"Terminal.Tui.GitFiltering",
				"Terminal.Tui.Action.FocusExclusions.Description",
				"M",
				FormatGitMode(selection.GitMode ?? plan.GitReadiness.Mode),
				TerminalWorkspaceCommandCatalog.Get(TerminalWorkspaceCommandVerb.Set).Syntax,
				execute: () => FocusControlSection(TerminalControlSection.Exclusions)),
			CreateAction(
				TerminalWorkspaceActionKind.Exclusions,
				"Terminal.Tui.Selection",
				"Terminal.Tui.Exclusions",
				"Terminal.Tui.Action.FocusExclusions.Description",
				"X",
				FormatExclusions(selection.Exclusions ?? []),
				TerminalWorkspaceCommandCatalog.Get(TerminalWorkspaceCommandVerb.Set).Syntax,
				execute: () => FocusControlSection(TerminalControlSection.Exclusions)),
			CreateAction(
				TerminalWorkspaceActionKind.FileTypes,
				"Terminal.Tui.Selection",
				"Terminal.Tui.FileTypes",
				"Terminal.Tui.Action.FocusFileTypes.Description",
				"T",
				FormatSelectionCount(
					plan.AvailableExtensions.Count(displayedExtensionSet.Contains),
					plan.AvailableExtensions.Count),
				TerminalWorkspaceCommandCatalog.Get(TerminalWorkspaceCommandVerb.Type).Syntax,
				execute: () => FocusControlSection(TerminalControlSection.Extensions)),
			CreateAction(
				TerminalWorkspaceActionKind.ExportContext,
				"Terminal.Tui.Export",
				"Terminal.Tui.ExportContext",
				"Terminal.Command.ExportContext",
				"E",
				commandSyntax: "export context [format] [path]",
				isAvailable: () => !HasActiveOperation,
				execute: () => ExportContext()),
			CreateAction(
				TerminalWorkspaceActionKind.ExportFolder,
				"Terminal.Tui.Export",
				"Menu.File.ExportProjectCopy.Folder",
				"Menu.File.ExportProjectCopy.Folder.Help",
				"Z",
				commandSyntax: "export folder <path>",
				isAvailable: () => !HasActiveOperation,
				execute: () => ExportProject(ProjectCopyExportFormat.Folder)),
			CreateAction(
				TerminalWorkspaceActionKind.ExportZip,
				"Terminal.Tui.Export",
				"Menu.File.ExportProjectCopy.Zip",
				"Menu.File.ExportProjectCopy.Zip.Help",
				"Z",
				commandSyntax: "export zip <path>",
				isAvailable: () => !HasActiveOperation,
				execute: () => ExportProject(ProjectCopyExportFormat.Zip)),
			CreateAction(
				TerminalWorkspaceActionKind.SaveProfile,
				"Terminal.Tui.Profile",
				"Terminal.Tui.SaveProfile",
				"Terminal.Command.ProfileExport",
				"P",
				commandSyntax: TerminalWorkspaceCommandCatalog.Get(
					TerminalWorkspaceCommandVerb.Profile).Syntax,
				execute: () => SaveProfile()),
			CreateAction(
				TerminalWorkspaceActionKind.OpenDesktop,
				"Terminal.Tui.Profile",
				"Terminal.Tui.Welcome.OpenDesktop",
				"Terminal.Tui.Welcome.OpenDesktop.Description",
				"G",
				execute: OpenCurrentStateInDesktop),
			CreateAction(
				TerminalWorkspaceActionKind.SourceDetails,
				"Terminal.Tui.Source",
				"Terminal.Tui.Details",
				"Terminal.Tui.Action.SourceDetails.Description",
				string.Empty,
				execute: ShowSourceDetails),
			CreateAction(
				TerminalWorkspaceActionKind.RecentWorkspaces,
				"Terminal.Tui.Source",
				"Terminal.Tui.Welcome.Recent",
				"Terminal.Tui.Welcome.Recent.Description",
				string.Empty,
				commandSyntax: TerminalWorkspaceCommandCatalog.Get(
					TerminalWorkspaceCommandVerb.Recent).Syntax,
				execute: () =>
				{
					ShowWelcome();
					_application.Invoke(OpenRecentWorkspaces);
				}),
			CreateAction(
				TerminalWorkspaceActionKind.Refresh,
				"Terminal.Tui.Source",
				"Terminal.Tui.Command.Refresh.Title",
				"Terminal.Tui.Command.Refresh.Description",
				string.Empty,
				commandSyntax: TerminalWorkspaceCommandCatalog.Get(
					TerminalWorkspaceCommandVerb.Refresh).Syntax,
				execute: () => RefreshCurrentProject())
		};
		actions.Add(CreateAction(
			TerminalWorkspaceActionKind.Language,
			"Terminal.Tui.Source",
			"Terminal.Tui.Command.Language.Title",
			"Terminal.Tui.Command.Language.Description",
			string.Empty,
			AppLanguageUtility.ToCode(_services.Localization.CurrentLanguage),
			TerminalWorkspaceCommandCatalog.Get(TerminalWorkspaceCommandVerb.Language).Syntax,
			execute: () => OpenCommandLine("language ")));

		if (plan.SourceIdentity?.SourceType == ProjectSourceType.GitClone)
		{
			actions.Add(CreateAction(
				TerminalWorkspaceActionKind.GetUpdates,
				"Terminal.Tui.RecentRepositories.Repository",
				"Terminal.Tui.Action.GetUpdates",
				"Terminal.Tui.Action.GetUpdates.Description",
				string.Empty,
				commandSyntax: TerminalWorkspaceCommandCatalog.Get(
					TerminalWorkspaceCommandVerb.Update).Syntax,
				execute: () => GetRepositoryUpdates()));
			actions.Add(CreateAction(
				TerminalWorkspaceActionKind.SwitchBranch,
				"Terminal.Tui.RecentRepositories.Repository",
				"Terminal.Tui.Action.SwitchBranch",
				"Terminal.Tui.Action.SwitchBranch.Description",
				string.Empty,
				commandSyntax: TerminalWorkspaceCommandCatalog.Get(
					TerminalWorkspaceCommandVerb.Branch).Syntax,
				execute: () => SwitchRepositoryBranch()));
		}

		return actions;
	}

	private TerminalWorkspaceAction CreateAction(
		TerminalWorkspaceActionKind kind,
		string categoryKey,
		string titleKey,
		string descriptionKey,
		string shortcut,
		string? value = null,
		string? commandSyntax = null,
		Func<bool>? isAvailable = null,
		Action? execute = null) =>
		new(
			kind,
			L(categoryKey),
			L(titleKey),
			L(descriptionKey),
			shortcut,
			value,
			commandSyntax,
			isAvailable,
			execute);

	private string FormatGitMode(GitFilteringMode mode) =>
		L(ProjectPresentationCatalog.Get(mode).LabelKey);

	private string FormatExclusions(IReadOnlyCollection<ProjectExclusion> exclusions)
	{
		var pathExclusionCount = ProjectPresentationCatalog.Exclusions.Count(
			descriptor => exclusions.Contains(descriptor.RequireId()));
		return pathExclusionCount.ToString("N0", CultureInfo.CurrentCulture);
	}

	private string FormatSelectionCount(int selected, int available) =>
		selected == available
			? $"{L("Settings.All")} ({available:N0})"
			: $"{selected:N0}/{available:N0}";

	private (TerminalAggregateControl? List, ObservableCollection<TerminalParameterRow>? Rows)
		GetAggregateControlSection(TerminalControlSection section) =>
		section switch
		{
			TerminalControlSection.Content => (_contentAllControl, _contentAllControlRows),
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
			case TerminalParameterRowKind.GitMode when row.GitMode is { } mode:
				ApplyGitMode(mode);
				return;
			case TerminalParameterRowKind.ToggleAllContent:
				ApplyAllContentTransformations(row.IsSelected != true);
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
					var values = (GetDisplayedSettingsSelection().Exclusions ?? []).ToHashSet();
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
					var values = (GetDisplayedSettingsSelection().Extensions ??
					              _state?.Plan.SelectedExtensions ?? [])
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
		var selection = GetDisplayedSettingsSelection();
		var target = (selection.GitMode ?? _state.Plan.GitReadiness.Mode) == mode
			? GitFilteringMode.None
			: mode;
		if (target != GitFilteringMode.None)
			_preferredGitMode = target;
		ApplyPathFilters(target, selection.Exclusions ?? []);
	}

	private void ApplyAllExclusions(bool enabled, bool originatedFromCommandLine = false)
	{
		if (_state is null)
			return;
		var selection = GetDisplayedSettingsSelection();
		var (mode, exclusions) = TerminalAggregateSelectionPolicy.ResolveExclusions(
			enabled,
			selection.GitMode ?? _state.Plan.GitReadiness.Mode,
			_preferredGitMode);
		ApplyPathFilters(mode, exclusions, originatedFromCommandLine);
	}

	private void ApplyExclusions(
		IReadOnlyCollection<ProjectExclusion> exclusions,
		bool originatedFromCommandLine = false)
	{
		if (_state is null)
			return;
		ApplyPathFilters(
			GetDisplayedSettingsSelection().GitMode ?? _state.Plan.GitReadiness.Mode,
			exclusions,
			originatedFromCommandLine);
	}

	private void ApplyPathFilters(
		GitFilteringMode mode,
		IReadOnlyCollection<ProjectExclusion> exclusions,
		bool originatedFromCommandLine = false)
	{
		if (_state is null)
			return;
		if (!originatedFromCommandLine)
			PreserveControlFocusForOperation(TerminalControlSection.Exclusions);
		var selection = EnsureSettingsDraft() with
		{
			GitMode = mode,
			Exclusions = exclusions.ToArray()
		};
		PublishOptimisticSettings(selection, originatedFromCommandLine);
	}

	private void ApplyContentTransformation(
		IgnoreOptionId optionId,
		bool enabled,
		bool originatedFromCommandLine = false)
	{
		if (_state is null)
			return;
		if (!originatedFromCommandLine)
			PreserveControlFocusForOperation(TerminalControlSection.Content);
		var selection = SetContentTransformation(EnsureSettingsDraft(), optionId, enabled);
		PublishOptimisticSettings(selection, originatedFromCommandLine);
	}

	private void ApplyAllContentTransformations(
		bool enabled,
		bool originatedFromCommandLine = false)
	{
		if (_state is null)
			return;
		if (!originatedFromCommandLine)
			PreserveControlFocusForOperation(TerminalControlSection.Content);
		var selection = EnsureSettingsDraft() with
		{
			HideSecrets = enabled,
			HidePrivateData = enabled,
			CompressCode = enabled,
			StripComments = enabled,
			StripBlankLines = enabled
		};
		PublishOptimisticSettings(selection, originatedFromCommandLine);
	}

	private void ApplyExtensions(
		IReadOnlyCollection<string> extensions,
		bool originatedFromCommandLine = false)
	{
		if (_state is null)
			return;
		if (!originatedFromCommandLine)
			PreserveControlFocusForOperation(TerminalControlSection.Extensions);
		var selection = EnsureSettingsDraft() with { Extensions = extensions.ToArray() };
		UpdateDraftExtensionStates(extensions);
		PublishOptimisticSettings(selection, originatedFromCommandLine);
	}

	private ProjectSelectionSpec EnsureSettingsDraft()
	{
		if (_state is null)
			return ProjectSelectionSpec.Standard;
		_settingsDraftSelection ??= _state.BuildSelection();
		_settingsDraftExtensionStates ??= new Dictionary<string, bool>(
			_state.ExtensionOptionStates,
			StringComparer.OrdinalIgnoreCase);
		return _settingsDraftSelection;
	}

	private void UpdateDraftExtensionStates(IReadOnlyCollection<string> selectedExtensions)
	{
		if (_state is null)
			return;
		_settingsDraftExtensionStates = new Dictionary<string, bool>(
			_state.BuildExtensionOptionStates(selectedExtensions),
			StringComparer.OrdinalIgnoreCase);
	}

	private void PublishOptimisticSettings(
		ProjectSelectionSpec selection,
		bool originatedFromCommandLine = false)
	{
		_settingsDraftSelection = selection;
		_settingsDraftOriginatedFromCommandLine = originatedFromCommandLine;
		RefreshContextControls();
		_controlsFrame?.SetNeedsDraw();
		_application.LayoutAndDraw();
		ScheduleSettingsRefresh();
	}

	private void ClearSettingsDraft()
	{
		_settingsDraftSelection = null;
		_settingsDraftExtensionStates = null;
		_settingsDraftOriginatedFromCommandLine = false;
	}

	private static ProjectSelectionSpec SetContentTransformation(
		ProjectSelectionSpec selection,
		IgnoreOptionId optionId,
		bool enabled) => optionId switch
		{
			IgnoreOptionId.HideSecrets => selection with { HideSecrets = enabled },
			IgnoreOptionId.HidePrivateData => selection with { HidePrivateData = enabled },
			IgnoreOptionId.CompressCode => selection with { CompressCode = enabled },
			IgnoreOptionId.StripComments => selection with { StripComments = enabled },
			IgnoreOptionId.StripBlankLines => selection with { StripBlankLines = enabled },
			_ => throw new ArgumentOutOfRangeException(nameof(optionId), optionId, null)
		};

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

	private void ShowActionPalette()
	{
		if (_operationProgress is not null)
			return;

		var registry = _screen == TerminalWorkspaceScreen.Welcome
			? null
			: BuildWorkspaceActionRegistry();
		var items = registry?.PaletteItems ?? BuildWelcomePaletteItems();
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
				.Where(item => item.IsAvailable())
				.Where(item => MatchesPaletteFilter(item, filter))
				.ToArray();
			var rowWidth = Math.Max(20, width - 6);
			var titleWidth = Math.Clamp(
				filtered.Select(item =>
					(item.Title + (string.IsNullOrWhiteSpace(item.Value) ? string.Empty : $": {item.Value}"))
					.GetColumns()).DefaultIfEmpty(24).Max(),
				24,
				Math.Max(24, rowWidth / 2));
			rows.Clear();
			foreach (var item in filtered)
				rows.Add(new TerminalPaletteRow(item, titleWidth, rowWidth));
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
		{
			_application.Invoke(() =>
			{
				if (registry is null)
					selectedItem.Execute();
				else
					registry.Execute(selectedItem);
			});
		}
	}

	private IReadOnlyList<TerminalPaletteItem> BuildWorkspacePaletteItems()
	{
		var actions = BuildWorkspaceActions().ToList();
		actions.Add(CreateAction(
			TerminalWorkspaceActionKind.OpenControls,
			"Terminal.Tui.Source",
			"Terminal.Tui.ContextControls",
			"Terminal.Tui.Action.OpenControls.Description",
			"Tab/F6",
			commandSyntax: TerminalWorkspaceCommandCatalog.Get(TerminalWorkspaceCommandVerb.All).Syntax,
			execute: () => FocusPane(TerminalWorkspacePane.Controls)));
		actions.Add(CreateAction(
			TerminalWorkspaceActionKind.ReturnToWelcome,
			"Terminal.Tui.Source",
			"Terminal.Tui.BackToWelcome",
			"Terminal.Tui.ConfirmBackToWelcome",
			"Esc",
			execute: () =>
			{
				if (Confirm(L("Terminal.Tui.BackToWelcome"), L("Terminal.Tui.ConfirmBackToWelcome")))
					ShowWelcome();
			}));
		actions.Add(CreateAction(
			TerminalWorkspaceActionKind.Help,
			"Terminal.Tui.Source",
			"Terminal.Tui.Help",
			"Terminal.Tui.Welcome.Help.Description",
			"?",
			commandSyntax: TerminalWorkspaceCommandCatalog.Get(TerminalWorkspaceCommandVerb.Help).Syntax,
			execute: () => ShowHelp(welcome: false)));
		return actions
			.Where(action => action.IsAvailable?.Invoke() != false)
			.Select(action => new TerminalPaletteItem(
				$"workspace.palette.{action.Kind}",
				action.Category,
				action.Title,
				action.Description,
					action.Shortcut,
					action.Value,
					action.CommandSyntax,
					ResolvePaletteCommandId(action.CommandSyntax),
					action.IsAvailable ?? (static () => true),
				action.Execute ?? throw new InvalidOperationException(
					$"Palette action '{action.Kind}' has no handler.")))
			.ToArray();
	}

	private IReadOnlyList<TerminalPaletteItem> BuildWelcomePaletteItems()
	{
		if (_welcomeContext is null)
			return [];
		return BuildWelcomeActions(_welcomeContext)
			.Select(action => new TerminalPaletteItem(
				$"welcome.palette.{action.Kind}",
				L("Terminal.Tui.Actions"),
				action.Title,
				action.Description,
				string.Empty,
				null,
				null,
				null,
				static () => true,
				() => ActivateWelcomeAction(action.Kind)))
			.Append(new TerminalPaletteItem(
				"welcome.palette.open-profile",
				L("Terminal.Tui.Actions"),
				L("Terminal.Tui.Welcome.OpenProfile"),
				L("Terminal.Tui.Welcome.OpenProfile.Description"),
				string.Empty,
				null,
				null,
				null,
				static () => true,
				OpenPortableProfile))
			.ToArray();
	}

	private static string? ResolvePaletteCommandId(string? syntax)
	{
		if (string.IsNullOrWhiteSpace(syntax))
			return null;
		var separator = syntax.IndexOf(' ');
		var token = separator < 0 ? syntax : syntax[..separator];
		if (TerminalWorkspaceCommandCatalog.TryGet(token, out var definition))
			return definition.Id;
		throw new InvalidOperationException(
			$"Palette command syntax '{syntax}' does not name a workspace command.");
	}

	private string BuildPaletteDetail(TerminalPaletteItem item)
	{
		var value = string.IsNullOrWhiteSpace(item.Value)
			? string.Empty
			: $"{PanelSeparator}{item.Value}";
		var syntax = string.IsNullOrWhiteSpace(item.CommandSyntax)
			? string.Empty
			: $"\n:{item.CommandSyntax}";
		return $"{item.Category}{PanelSeparator}{item.Title}{value}{syntax}\n{item.Description}";
	}

	private static bool MatchesPaletteFilter(TerminalPaletteItem item, string filter)
	{
		if (string.IsNullOrWhiteSpace(filter))
			return true;
		var searchable = string.Join(
			' ',
			item.Title,
			item.Description,
			item.CommandSyntax ?? string.Empty);
		var candidateIndex = 0;
		foreach (var character in filter.Where(static character => !char.IsWhiteSpace(character)))
		{
			candidateIndex = searchable.IndexOf(
				character.ToString(),
				candidateIndex,
				StringComparison.CurrentCultureIgnoreCase);
			if (candidateIndex < 0)
				return false;
			candidateIndex++;
		}
		return true;
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

	private void AnalyzeCurrentContext(bool originatedFromCommandLine = false)
	{
		if (_state is null)
			return;
		_activeOperationTask = TrackBackgroundTask(RunOperationAsync(
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
			},
			originatedFromCommandLine: originatedFromCommandLine));
	}

	private void CopyCurrentContext(TerminalWorkspaceCommand command)
	{
		if (_state is null)
			return;
		var view = command.View ?? _previewView;
		var format = command.Format ?? _format;
		_activeOperationTask = TrackBackgroundTask(RunOperationAsync(
			L("Terminal.Tui.Command.Copy.Title"),
			async token =>
			{
				var payload = await _controller.BuildCopyPayloadAsync(
						_state,
						view,
						format,
						token)
					.ConfigureAwait(false);
				if (payload is null)
				{
					throw new TerminalWorkspaceOperationException(
						"DPX-TUI-CLIPBOARD-PAYLOAD-TOO-LARGE");
				}
				var clipboardResult = await InvokeAsync(() => _clipboardWriter.Write(payload))
					.ConfigureAwait(false);
				if (!clipboardResult.IsSuccess)
				{
					throw new TerminalWorkspaceOperationException(
						clipboardResult.Status == TerminalClipboardWriteStatus.PayloadTooLarge
							? "DPX-TUI-CLIPBOARD-PAYLOAD-TOO-LARGE"
							: "DPX-TUI-CLIPBOARD-UNAVAILABLE");
				}
				return string.Format(
					CultureInfo.CurrentCulture,
					L("Terminal.Tui.Command.Copy.Result"),
					L(ProjectPresentationCatalog.Get(view).LabelKey),
					payload.Length);
			},
			originatedFromCommandLine: true,
			cornerProgressLabel: L("Terminal.Tui.Progress.BuildingPreview")));
	}

	private void RefreshCurrentProject(bool originatedFromCommandLine = false)
	{
		if (_state is null)
			return;
		_activeOperationTask = TrackBackgroundTask(RunOperationAsync(
			L("Terminal.Tui.Command.Refresh.Title"),
			async token =>
			{
				await _controller.RefreshProjectAsync(_state, token).ConfigureAwait(false);
				return L("Terminal.Tui.Command.Refresh.Result");
			},
			originatedFromCommandLine: originatedFromCommandLine,
			cornerProgressLabel: L("Terminal.Tui.Progress.RefreshingProject")));
	}

	private void OpenCurrentStateInDesktop()
	{
		if (_state is null)
			return;
		_activeOperationTask = TrackBackgroundTask(RunOperationAsync(
			L("Terminal.Tui.Welcome.OpenDesktop"),
			async token =>
			{
				var exitCode = await _controller.OpenDesktopAsync(_state, token)
					.ConfigureAwait(false);
				return exitCode == CommandLineExitCodes.Success
					? L("Terminal.Tui.DesktopAccepted")
					: throw new TerminalWorkspaceOperationException("DPX-DESKTOP-REQUEST-FAILED");
			}));
	}

	private void ShowSourceDetails()
	{
		if (_state is null)
			return;
		var identity = _state.Plan.SourceIdentity;
		var cacheEntry = identity?.RepositoryUrl is { Length: > 0 } repositoryUrl
			? _services.RepoCacheService.FindIndexedRepository(repositoryUrl)
			: null;
		var text = TerminalSourceDetailsFormatter.Format(
			_state.Plan.SourceRoot,
			identity,
			cacheEntry,
			L,
			CultureInfo.CurrentCulture);
		ShowNotice(
			L("Terminal.Tui.Details"),
			text,
			TerminalWorkspaceTheme.Dialog);
	}

	private void GetRepositoryUpdates(bool originatedFromCommandLine = false)
	{
		if (_state?.Plan.SourceIdentity?.SourceType != ProjectSourceType.GitClone)
			return;
		_activeOperationTask = TrackBackgroundTask(RunOperationAsync(
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
			},
			modalProgress: true,
			originatedFromCommandLine: originatedFromCommandLine));
	}

	private void SwitchRepositoryBranch(
		string? requestedBranch = null,
		bool originatedFromCommandLine = false)
	{
		if (_state?.Plan.SourceIdentity?.SourceType != ProjectSourceType.GitClone)
			return;
		_activeOperationTask = TrackBackgroundTask(RunOperationAsync(
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
				var selected = string.IsNullOrWhiteSpace(requestedBranch)
					? await InvokeAsync(() => SelectFromList(
						L("Terminal.Tui.Action.SwitchBranch"),
						L("Terminal.Tui.Action.SwitchBranch.Description"),
						branchNames)).ConfigureAwait(false)
					: branchNames.FirstOrDefault(branch =>
						string.Equals(branch, requestedBranch, StringComparison.Ordinal));
				if (string.IsNullOrWhiteSpace(selected))
				{
					if (!string.IsNullOrWhiteSpace(requestedBranch))
						throw new TerminalWorkspaceOperationException("DPX-TUI-GIT-BRANCH-NOT-FOUND");
					return null;
				}
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
			},
			modalProgress: true,
			originatedFromCommandLine: originatedFromCommandLine));
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
