using System.Collections.ObjectModel;
using System.Globalization;
using DevProjex.Application.Secrets;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DevProjex.Terminal.Tui;

#pragma warning disable CS0618

internal readonly record struct TerminalControlSourceStamp(
	TerminalWorkspaceState State,
	long Revision,
	ProjectSelectionSpec? DraftSelection,
	AppLanguage Language,
	TerminalWorkspaceLayoutMode LayoutMode,
	int LabelWidth);

internal readonly record struct TerminalRedactionLabelStamp(
	bool HasSnapshot,
	string? SelectionKey,
	int SecretDetectedCount,
	int SecretRedactedCount,
	int PrivateDataDetectedCount,
	int PrivateDataRedactedCount,
	bool IsComplete)
{
	public static TerminalRedactionLabelStamp From(SecretRedactionSnapshot? snapshot) =>
		snapshot is null
			? default
			: new TerminalRedactionLabelStamp(
				true,
				snapshot.SelectionKey,
				snapshot.SecretDetectedCount,
				snapshot.SecretRedactedCount,
				snapshot.PrivateDataDetectedCount,
				snapshot.PrivateDataRedactedCount,
				snapshot.IsComplete);
}

internal enum TerminalControlRefreshKind
{
	None,
	RedactionOnly,
	Full
}

internal sealed partial class TerminalWorkspaceSession
{
	private const int WideControlsWidth = 38;
	private const int ContentControlsFrameHeight = 7;
	private const int AggregateFramePaddingColumns = 3;
	private const int AggregateTrailingBorderColumns = 3;

	private WorkspaceControlViewGraph? ControlViews => _workspaceViews?.Controls;
	private FrameView? _controlsFrame => ControlViews?.Frame;
	private Label? _controlsPanelHeading => ControlViews?.PanelHeading;
	private Label? _collapsedControls => ControlViews?.CollapsedSummary;
	private FrameView? _contentControlsFrame => ControlViews?.ContentFrame;
	private FrameView? _exclusionControlsFrame => ControlViews?.ExclusionFrame;
	private FrameView? _extensionControlsFrame => ControlViews?.ExtensionFrame;
	private View? _filterControlsHost => ControlViews?.FilterHost;
	private TerminalParameterListView? _contentControls => ControlViews?.ContentList;
	private TerminalAggregateControl? _contentAllControl => ControlViews?.ContentAll;
	private TerminalAggregateControl? _exclusionAllControl => ControlViews?.ExclusionAll;
	private TerminalParameterListView? _exclusionControls => ControlViews?.ExclusionList;
	private TerminalAggregateControl? _extensionAllControl => ControlViews?.ExtensionAll;
	private TerminalParameterListView? _extensionControls => ControlViews?.ExtensionList;
	private ResettableObservableCollection<TerminalParameterRow>? _contentControlRows
	{
		get => ControlViews?.ContentRows;
		set { if (ControlViews is { } views) views.ContentRows = value; }
	}
	private ObservableCollection<TerminalParameterRow>? _contentAllControlRows
	{
		get => ControlViews?.ContentAllRows;
		set { if (ControlViews is { } views) views.ContentAllRows = value; }
	}
	private ObservableCollection<TerminalParameterRow>? _exclusionAllControlRows
	{
		get => ControlViews?.ExclusionAllRows;
		set { if (ControlViews is { } views) views.ExclusionAllRows = value; }
	}
	private ResettableObservableCollection<TerminalParameterRow>? _exclusionControlRows
	{
		get => ControlViews?.ExclusionRows;
		set { if (ControlViews is { } views) views.ExclusionRows = value; }
	}
	private ObservableCollection<TerminalParameterRow>? _extensionAllControlRows
	{
		get => ControlViews?.ExtensionAllRows;
		set { if (ControlViews is { } views) views.ExtensionAllRows = value; }
	}
	private ResettableObservableCollection<TerminalParameterRow>? _extensionControlRows
	{
		get => ControlViews?.ExtensionRows;
		set { if (ControlViews is { } views) views.ExtensionRows = value; }
	}
	private string? _selectedContentControlKey;
	private string? _selectedExclusionControlKey;
	private string? _selectedExtensionControlKey;
	private TerminalControlSection? _activeAggregateControlSection
	{
		get => _focus.AggregateSection;
		set => _focus.AggregateSection = value;
	}
	private GitFilteringMode _preferredGitMode = GitFilteringMode.RespectGitIgnore;
	private ProjectSelectionSpec? _settingsDraftSelection;
	private Dictionary<string, bool>? _settingsDraftExtensionStates;
	private GitFilteringMode? _settingsDraftPreferredGitMode;
	private bool _settingsDraftOriginatedFromCommandLine;
	private TerminalControlSourceStamp? _controlSourceStamp;
	private TerminalRedactionLabelStamp? _redactionLabelStamp;

	private WorkspaceControlViewGraph CreateContextControls()
	{
		var controlsFrame = new TerminalLiteralFrameView
		{
			BorderStyle = _presentation.BorderStyle,
			SchemeName = TerminalWorkspaceTheme.Panel
		};
		var controlsPanelHeading = new TerminalLiteralLabel
		{
			X = 0,
			Y = 0,
			Width = Dim.Fill(),
			Height = 1,
			Visible = _options.Plain,
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		var collapsedControls = new TerminalLiteralLabel
		{
			X = 1,
			Y = 0,
			Width = Dim.Fill(1),
			Height = 1,
			Visible = false,
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		var (contentControlsFrame, contentControls) = CreateControlSection(
			NormalizeControlTitle(L("Settings.Secrets.Title")),
			TerminalControlSection.Content,
			showVerticalScrollBar: false);
		var contentAllControl = AddAggregateControl(
			contentControlsFrame,
			contentControls,
			TerminalControlSection.Content);
		contentControlsFrame.Y = _options.Plain ? 1 : 0;
		contentControlsFrame.Height = ContentControlsFrameHeight;

		var filterControlsHost = new View
		{
			X = 0,
			Y = Pos.Bottom(contentControlsFrame),
			Width = Dim.Fill(),
			Height = Dim.Fill()
		};
		var (exclusionControlsFrame, exclusionControls) = CreateControlSection(
			NormalizeControlTitle(L("Terminal.Tui.Exclusions")),
			TerminalControlSection.Exclusions,
			showVerticalScrollBar: true);
		var exclusionAllControl = AddAggregateControl(
			exclusionControlsFrame,
			exclusionControls,
			TerminalControlSection.Exclusions);
		exclusionControlsFrame.Height = Dim.Percent(50);
		var (extensionControlsFrame, extensionControls) = CreateControlSection(
			NormalizeControlTitle(L("Terminal.Tui.FileTypes")),
			TerminalControlSection.Extensions,
			showVerticalScrollBar: true);
		var extensionAllControl = AddAggregateControl(
			extensionControlsFrame,
			extensionControls,
			TerminalControlSection.Extensions);
		extensionControlsFrame.Y = Pos.Bottom(exclusionControlsFrame);
		extensionControlsFrame.Height = Dim.Fill();
		filterControlsHost.Add(exclusionControlsFrame, extensionControlsFrame);
		controlsFrame.Add(
			controlsPanelHeading,
			collapsedControls,
			contentControlsFrame,
			filterControlsHost);
		return new WorkspaceControlViewGraph(
			controlsFrame,
			controlsPanelHeading,
			collapsedControls,
			contentControlsFrame,
			exclusionControlsFrame,
			extensionControlsFrame,
			filterControlsHost,
			contentControls,
			contentAllControl,
			exclusionAllControl,
			exclusionControls,
			extensionAllControl,
			extensionControls);
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

		var selection = GetDisplayedSettingsSelection();
		var snapshot = GetContentRedactionSnapshot(_state.Plan);
		var sourceStamp = new TerminalControlSourceStamp(
			_state,
			_state.Revision,
			_settingsDraftSelection,
			_services.Localization.CurrentLanguage,
			_layoutMode,
			ResolveControlLabelWidth(markerColumns: 4));
		var redactionStamp = TerminalRedactionLabelStamp.From(snapshot);
		var refreshKind = ResolveControlRefreshKind(
			_controlSourceStamp,
			sourceStamp,
			_redactionLabelStamp,
			redactionStamp);
		if (refreshKind != TerminalControlRefreshKind.Full)
		{
			if (refreshKind == TerminalControlRefreshKind.RedactionOnly)
			{
				RefreshContentRedactionRows(selection, snapshot);
				RefreshControlTitles();
			}
			_redactionLabelStamp = redactionStamp;
			UpdateControlSelectionSchemes();
			return;
		}

		TrackSelectedControl(TerminalControlSection.Content);
		TrackSelectedControl(TerminalControlSection.Exclusions);
		TrackSelectedControl(TerminalControlSection.Extensions);
		var selectedExtensions = selection.Extensions ?? _state.Plan.SelectedExtensions;
		_contentAllControlRows = UpdateAggregateRow(
			_contentAllControl,
			_contentAllControlRows,
			_parameterRowsBuilder.BuildContentAggregate(selection));
		_contentControlRows = UpdateControlRows(
			TerminalControlSection.Content,
			_contentControls,
			_contentControlRows,
			BuildContentParameterRows(selection, snapshot),
			_selectedContentControlKey);
		_exclusionAllControlRows = UpdateAggregateRow(
			_exclusionAllControl,
			_exclusionAllControlRows,
			_parameterRowsBuilder.BuildExclusionAggregate(_state.Plan, selection));
		_exclusionControlRows = UpdateControlRows(
			TerminalControlSection.Exclusions,
			_exclusionControls,
			_exclusionControlRows,
			BuildExclusionParameterRows(),
			_selectedExclusionControlKey);
		_extensionAllControlRows = UpdateAggregateRow(
			_extensionAllControl,
			_extensionAllControlRows,
			_parameterRowsBuilder.BuildExtensionAggregate(_state.Plan, selectedExtensions));
		_extensionControlRows = UpdateControlRows(
			TerminalControlSection.Extensions,
			_extensionControls,
			_extensionControlRows,
			BuildExtensionParameterRows(),
			_selectedExtensionControlKey);
		if (_collapsedControls is not null)
		{
			var exclusionCounts = CountExclusionAxis(_exclusionControlRows);
			_collapsedControls.Text = string.Join(
				PanelSeparator,
				$"{L("Preview.Mode.Content")} {_contentControlRows.Count(static row => row.IsSelected == true)}/{_contentControlRows.Count}",
				$"{L("Terminal.Tui.Exclusions")} {exclusionCounts.Selected}/{exclusionCounts.Total}",
				$"{L("Terminal.Tui.FileTypes")} {_extensionControlRows.Count(static row => row.IsSelected == true)}/{_extensionControlRows.Count}");
		}
		RefreshControlTitles();
		UpdateControlSelectionSchemes();
		_controlSourceStamp = sourceStamp;
		_redactionLabelStamp = redactionStamp;
	}

	internal static (int Selected, int Total) CountExclusionAxis(
		IReadOnlyCollection<TerminalParameterRow> rows)
	{
		ArgumentNullException.ThrowIfNull(rows);
		var hasGitAxis = rows.Any(static row => row.Kind == TerminalParameterRowKind.GitMode);
		var gitSelected = rows.Any(static row =>
			row.Kind == TerminalParameterRowKind.GitMode && row.IsSelected == true);
		return (
			rows.Count(static row =>
				row.Kind == TerminalParameterRowKind.Exclusion && row.IsSelected == true) +
			(gitSelected ? 1 : 0),
			rows.Count(static row => row.Kind == TerminalParameterRowKind.Exclusion) +
			(hasGitAxis ? 1 : 0));
	}

	internal static TerminalControlRefreshKind ResolveControlRefreshKind(
		TerminalControlSourceStamp? previousSource,
		TerminalControlSourceStamp currentSource,
		TerminalRedactionLabelStamp? previousRedaction,
		TerminalRedactionLabelStamp currentRedaction)
	{
		if (previousSource != currentSource)
			return TerminalControlRefreshKind.Full;
		return previousRedaction == currentRedaction
			? TerminalControlRefreshKind.None
			: TerminalControlRefreshKind.RedactionOnly;
	}

	private ObservableCollection<TerminalParameterRow> UpdateAggregateRow(
		TerminalAggregateControl control,
		ObservableCollection<TerminalParameterRow>? source,
		TerminalParameterRow row)
	{
		control.SetRow(row);
		if (control.IsOnBorder)
			control.X = Pos.AnchorEnd(
				control.Text.GetColumns() + AggregateTrailingBorderColumns);
		source ??= [row];
		if (source.Count == 0)
			source.Add(row);
		else if (source[0] != row)
			source[0] = row;
		return source;
	}

	private ResettableObservableCollection<TerminalParameterRow> UpdateControlRows(
		TerminalControlSection section,
		TerminalParameterListView list,
		ResettableObservableCollection<TerminalParameterRow>? source,
		IReadOnlyList<TerminalParameterRow> rows,
		string? selectedKey)
	{
		if (source is null)
		{
			source = [];
			source.Reset(rows);
			list.SetParameterSource(source);
		}
		else if (!TryUpdateRowsInPlace(source, rows))
			source.Reset(rows);
		if (source.Count > 0)
		{
			var selectedIndex = FindPreferredControlRowIndex(source, selectedKey);
			var clampedIndex = Math.Clamp(selectedIndex, 0, source.Count - 1);
			list.SelectedItem = clampedIndex;
			SetSelectedControlKey(section, source[clampedIndex].Key);
		}
		return source;
	}

	internal static int FindPreferredControlRowIndex(
		IReadOnlyList<TerminalParameterRow> rows,
		string? selectedKey)
	{
		if (selectedKey is not null)
		{
			for (var index = 0; index < rows.Count; index++)
			{
				if (rows[index].IsEnabled &&
				    string.Equals(rows[index].Key, selectedKey, StringComparison.Ordinal))
				{
					return index;
				}
			}
		}
		for (var index = 0; index < rows.Count; index++)
		{
			if (rows[index].IsEnabled && rows[index].IsSelected == true)
				return index;
		}
		for (var index = 0; index < rows.Count; index++)
		{
			if (rows[index].IsEnabled)
				return index;
		}
		return 0;
	}

	internal static bool TryUpdateRowsInPlace(
		ObservableCollection<TerminalParameterRow> source,
		IReadOnlyList<TerminalParameterRow> rows)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(rows);
		if (source.Count != rows.Count)
			return false;
		for (var index = 0; index < rows.Count; index++)
		{
			if (!string.Equals(source[index].Key, rows[index].Key, StringComparison.Ordinal))
				return false;
		}
		for (var index = 0; index < rows.Count; index++)
		{
			if (!HasSamePresentation(source[index], rows[index]))
				source[index] = rows[index];
		}
		return true;
	}

	private static bool HasSamePresentation(TerminalParameterRow left, TerminalParameterRow right) =>
		left.Kind == right.Kind &&
		string.Equals(left.Label, right.Label, StringComparison.Ordinal) &&
		left.IsSelected == right.IsSelected &&
		left.IsEnabled == right.IsEnabled &&
		left.UseUnicodeRadioMarker == right.UseUnicodeRadioMarker &&
		left.GitMode == right.GitMode &&
		left.Exclusion == right.Exclusion &&
		left.ContentTransformation == right.ContentTransformation &&
		string.Equals(left.Value, right.Value, StringComparison.Ordinal);

	private void RefreshContentRedactionRows(
		ProjectSelectionSpec selection,
		SecretRedactionSnapshot? snapshot)
	{
		if (_state is null || _contentAllControl is null || _contentControls is null)
			return;

		TrackSelectedControl(TerminalControlSection.Content);
		_contentAllControlRows = UpdateAggregateRow(
			_contentAllControl,
			_contentAllControlRows,
			_parameterRowsBuilder.BuildContentAggregate(selection));
		_contentControlRows = UpdateControlRows(
			TerminalControlSection.Content,
			_contentControls,
			_contentControlRows,
			BuildContentParameterRows(selection, snapshot),
			_selectedContentControlKey);
	}

	private IReadOnlyList<TerminalParameterRow> BuildContentParameterRows() =>
		_state is null
			? []
			: BuildContentParameterRows(
				GetDisplayedSettingsSelection(),
				GetContentRedactionSnapshot(_state.Plan));

	private IReadOnlyList<TerminalParameterRow> BuildContentParameterRows(
		ProjectSelectionSpec selection,
		SecretRedactionSnapshot? snapshot) =>
		_state is null
			? []
			: _parameterRowsBuilder.BuildContent(_state.Plan, snapshot, selection);

	private IReadOnlyList<TerminalParameterRow> BuildExclusionParameterRows() =>
		_state is null
			? []
			: _parameterRowsBuilder.BuildExclusions(
				_state.Plan,
				GetDisplayedSettingsSelection(),
				_gitCliAvailable);

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

	private void FocusGitFiltering()
	{
		const TerminalControlSection section = TerminalControlSection.Exclusions;
		FocusControlSection(section);
		_application.Invoke(() => FocusGitFilteringRow(section));
	}

	private void FocusGitFilteringRow(TerminalControlSection section)
	{
		var (_, rows) = GetControlSection(section);
		if (rows is null)
			return;
		var rowIndex = -1;
		for (var index = 0; index < rows.Count; index++)
		{
			var row = rows[index];
			if (row.Kind != TerminalParameterRowKind.GitMode)
				continue;
			if (rowIndex < 0)
				rowIndex = index;
			if (row.IsSelected == true)
			{
				rowIndex = index;
				break;
			}
		}
		if (rowIndex < 0)
		{
			FocusControlSection(section, movePane: false);
			return;
		}

		var aggregateOffset = GetAggregateControlSection(section).List is null ? 0 : 1;
		FocusControlPosition(section, rowIndex + aggregateOffset);
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
				"Shift+Z",
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
					TryLeaveWorkspace(() =>
					{
						ShowWelcome();
						_application.Invoke(OpenRecentWorkspaces);
					});
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
		if (IsGitFilteringApplicable())
		{
			var exclusionsActionIndex = actions.FindIndex(static action =>
				action.Kind == TerminalWorkspaceActionKind.Exclusions);
			actions.Insert(exclusionsActionIndex, CreateAction(
				TerminalWorkspaceActionKind.GitFiltering,
				"Terminal.Tui.Selection",
				"Terminal.Tui.GitFiltering",
				"Terminal.Tui.Action.FocusExclusions.Description",
				"M",
				FormatGitMode(selection.GitMode ?? plan.GitReadiness.Mode),
				TerminalWorkspaceCommandCatalog.Get(TerminalWorkspaceCommandVerb.Set).Syntax,
				execute: CycleGitMode));
		}
		actions.Add(CreateAction(
			TerminalWorkspaceActionKind.Diagnostics,
			"Terminal.Tui.Diagnostics",
			"Terminal.Tui.Command.Diagnostics.Title",
			"Terminal.Tui.Command.Diagnostics.Description",
			"D",
			commandSyntax: TerminalWorkspaceCommandCatalog.Get(
				TerminalWorkspaceCommandVerb.Diagnostics).Syntax,
			execute: ShowDiagnostics));
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
		mode == GitFilteringMode.Diff
			? GitScopeSelection.ToToken(mode, GetDisplayedSettingsSelection().GitDiffRange)
			: L(ProjectPresentationCatalog.Get(mode).LabelKey);

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
		if (!row.IsEnabled)
			return;
		switch (row.Kind)
		{
			case TerminalParameterRowKind.GitMode when row.GitMode is { } mode:
				ApplyGitMode(mode, row.Value);
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

	private void ApplyGitMode(GitFilteringMode mode, string? diffRange = null)
	{
		if (_state is null)
			return;
		var selection = EnsureSettingsDraft();
		UpdateDraftPreferredGitMode(mode);
		ApplyPathFilters(mode, selection.Exclusions ?? [], diffRange);
	}

	private void ApplyAllExclusions(bool enabled, bool originatedFromCommandLine = false)
	{
		if (_state is null)
			return;
		var selection = GetDisplayedSettingsSelection();
		var exclusions = TerminalAggregateSelectionPolicy.ResolveExclusions(enabled);
		ApplyPathFilters(
			selection.GitMode ?? _state.Plan.GitReadiness.Mode,
			exclusions,
			selection.GitDiffRange,
			originatedFromCommandLine: originatedFromCommandLine);
	}

	private void ApplyExclusions(
		IReadOnlyCollection<ProjectExclusion> exclusions,
		bool originatedFromCommandLine = false)
	{
		if (_state is null)
			return;
		var selection = GetDisplayedSettingsSelection();
		ApplyPathFilters(
			selection.GitMode ?? _state.Plan.GitReadiness.Mode,
			exclusions,
			selection.GitDiffRange,
			originatedFromCommandLine: originatedFromCommandLine);
	}

	private void ApplyPathFilters(
		GitFilteringMode mode,
		IReadOnlyCollection<ProjectExclusion> exclusions,
		string? diffRange = null,
		bool originatedFromCommandLine = false)
	{
		if (_state is null)
			return;
		if (!originatedFromCommandLine)
			PreserveControlFocusForOperation(TerminalControlSection.Exclusions);
		var selection = GitScopeSelection.WithMode(
			EnsureSettingsDraft(),
			mode,
			diffRange) with
		{
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
		_settingsDraftPreferredGitMode ??= _preferredGitMode;
		return _settingsDraftSelection;
	}

	private void UpdateDraftPreferredGitMode(GitFilteringMode mode)
	{
		EnsureSettingsDraft();
		_settingsDraftPreferredGitMode = GitScopeSelection.ResolvePreferredPersistentMode(
			_settingsDraftPreferredGitMode ?? _preferredGitMode,
			mode);
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
		_settingsDraftPreferredGitMode = null;
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
		_activeAggregateControlSection = IsAggregateControlFocused(section) ||
			_activeAggregateControlSection == section
			? section
			: null;
		_focus.SaveBeforeBusy();
	}

	private void CycleGitMode()
	{
		if (!IsGitFilteringApplicable())
			return;

		var selection = GetDisplayedSettingsSelection();
		var next = selection.GitMode switch
		{
			GitFilteringMode.None => GitFilteringMode.RespectGitIgnore,
			GitFilteringMode.RespectGitIgnore => GitFilteringMode.TrackedFilesOnly,
			GitFilteringMode.TrackedFilesOnly => GitFilteringMode.Staged,
			GitFilteringMode.Staged => GitFilteringMode.Changes,
			_ => GitFilteringMode.None
		};
		if (!HasGitRepository() && next is GitFilteringMode.TrackedFilesOnly or
		    GitFilteringMode.Staged or GitFilteringMode.Changes)
		{
			next = GitFilteringMode.None;
		}
		UpdateDraftPreferredGitMode(next);
		ApplyPathFilters(next, selection.Exclusions ?? [], originatedFromCommandLine: false);
	}

	private bool HasGitRepository() =>
		_gitCliAvailable &&
		_state?.Plan is { } plan &&
		(plan.GitReadiness.HasRepositoryBoundary ||
		 GitRepositoryBoundaryProbe.ExistsAtOrAbove(plan.SourceRoot));

	private bool IsGitFilteringApplicable() =>
		_state?.Plan is { } plan && TerminalParameterRowsBuilder.IsGitFilteringApplicable(plan);

	private bool IsGitModeAvailable(GitFilteringMode mode) =>
		mode is GitFilteringMode.None or GitFilteringMode.RespectGitIgnore ||
		HasGitRepository();

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
			"Preview.Mode.Content",
			"Terminal.Tui.Action.OpenControls.Description",
			"C",
			execute: () => FocusControlSection(TerminalControlSection.Content)));
		actions.Add(CreateAction(
			TerminalWorkspaceActionKind.FocusTree,
			"Terminal.Tui.Source",
			"Terminal.Tui.Tree",
			"Terminal.Tui.Action.FocusTree.Description",
			"Tab/F6",
			execute: () => FocusPane(TerminalWorkspacePane.Tree)));
		actions.Add(CreateAction(
			TerminalWorkspaceActionKind.FocusPreview,
			"Terminal.Tui.Source",
			"Terminal.Tui.Preview",
			"Terminal.Tui.Action.FocusPreview.Description",
			"Tab/F6",
			execute: () => FocusPane(TerminalWorkspacePane.Preview)));
		actions.Add(CreateAction(
			TerminalWorkspaceActionKind.ClearFilter,
			"Terminal.Tui.Selection",
			"Terminal.Tui.Action.ClearFilter",
			"Terminal.Tui.Action.ClearFilter.Description",
			"Esc",
			isAvailable: () => _state?.HasTreeFilter == true,
			execute: ClearTreeFilter));
		actions.Add(CreateAction(
			TerminalWorkspaceActionKind.ClearSearch,
			"Terminal.Tui.Preview",
			"Terminal.Tui.Action.ClearSearch",
			"Terminal.Tui.Action.ClearSearch.Description",
			"Esc",
			isAvailable: () => _preview?.SearchQuery.Length > 0,
			execute: () =>
			{
				CancelPreviewSearch(clearQuery: true);
				_preview?.ClearSearch();
				UpdatePanelTitles();
			}));
		actions.Add(CreateAction(
			TerminalWorkspaceActionKind.Quit,
			"Terminal.Tui.Source",
			"Terminal.Tui.Exit",
			"Terminal.Tui.Welcome.Exit.Description",
			"Q",
			commandSyntax: TerminalWorkspaceCommandCatalog.Get(TerminalWorkspaceCommandVerb.Quit).Syntax,
			execute: () => TryExitWorkspace()));
		actions.Add(CreateAction(
			TerminalWorkspaceActionKind.ReturnToWelcome,
			"Terminal.Tui.Source",
			"Terminal.Tui.BackToWelcome",
			"Terminal.Tui.ConfirmBackToWelcome",
			"Esc",
			execute: () => TryLeaveWorkspace(() => ShowWelcome())));
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
		TrackActiveOperation(RunOperationAsync(
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
		TrackActiveOperation(RunOperationAsync(
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
		var state = _state;
		var refreshRequest = _controller.CaptureStructuralRefresh(
			state,
			state.BuildSelection(),
			_preferredGitMode);
		TrackActiveOperation(RunOperationAsync(
			L("Terminal.Tui.Command.Refresh.Title"),
			async token =>
			{
				await BuildAndApplyStructuralRefreshAsync(state, refreshRequest, token)
					.ConfigureAwait(false);
				return L("Terminal.Tui.Command.Refresh.Result");
			},
			originatedFromCommandLine: originatedFromCommandLine,
			cornerProgressLabel: L("Terminal.Tui.Progress.RefreshingProject")));
	}

	private void OpenCurrentStateInDesktop()
	{
		if (_state is null)
			return;
		TrackActiveOperation(RunOperationAsync(
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
		var state = _state;
		var refreshRequest = _controller.CaptureStructuralRefresh(
			state,
			state.BuildSelection(),
			_preferredGitMode);
		TrackActiveOperation(RunOperationAsync(
			L("Terminal.Tui.Action.GetUpdates"),
			async token =>
			{
				var updated = await _services.GitRepositoryService
					.PullUpdatesAsync(state.Plan.SourceRoot, cancellationToken: token)
					.ConfigureAwait(false);
				if (!updated)
					throw new TerminalWorkspaceOperationException("DPX-TUI-GIT-UPDATE-FAILED");
				await BuildAndApplyStructuralRefreshAsync(state, refreshRequest, token)
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
		var state = _state;
		var refreshRequest = _controller.CaptureStructuralRefresh(
			state,
			state.BuildSelection(),
			_preferredGitMode);
		TrackActiveOperation(RunOperationAsync(
			L("Terminal.Tui.Action.SwitchBranch"),
			async token =>
			{
				var branches = await _services.GitRepositoryService
					.GetBranchesAsync(state.Plan.SourceRoot, token)
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
						state.Plan.SourceRoot,
						selected,
						cancellationToken: token)
					.ConfigureAwait(false);
				if (!switched)
					throw new TerminalWorkspaceOperationException("DPX-TUI-GIT-BRANCH-FAILED");
				await BuildAndApplyStructuralRefreshAsync(state, refreshRequest, token)
					.ConfigureAwait(false);
				return $"{L("Terminal.Tui.RecentRepositories.Branch")}: {selected}";
			},
			modalProgress: true,
			originatedFromCommandLine: originatedFromCommandLine));
	}

	private async Task BuildAndApplyStructuralRefreshAsync(
		TerminalWorkspaceState state,
		TerminalStructuralRefreshRequest request,
		CancellationToken cancellationToken)
	{
		var result = await _controller
			.BuildStructuralRefreshAsync(request, cancellationToken)
			.ConfigureAwait(false);
		var gitCliAvailable = await ResolveGitCliAvailabilityAsync(result.Plan, cancellationToken)
			.ConfigureAwait(false);
		cancellationToken.ThrowIfCancellationRequested();
		await InvokeAsync(() =>
		{
			if (_stopping || !ReferenceEquals(_state, state))
				return false;

			_gitCliAvailable = gitCliAvailable;
			TerminalWorkspaceController.ApplyStructuralRefresh(state, result);
			return true;
		}).ConfigureAwait(false);
	}

	private async Task<bool> ResolveGitCliAvailabilityAsync(
		ProjectContextPlan plan,
		CancellationToken cancellationToken)
	{
		if (!plan.GitReadiness.HasRepositoryBoundary &&
		    !GitRepositoryBoundaryProbe.ExistsAtOrAbove(plan.SourceRoot))
		{
			return false;
		}

		try
		{
			return await _services.GitRepositoryService
				.IsGitAvailableAsync(cancellationToken)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return false;
		}
	}

	private void FocusPane(TerminalWorkspacePane pane)
	{
		var redrawMovedFrames = _layoutMode == TerminalWorkspaceLayoutMode.Split &&
		                        (_activePane == TerminalWorkspacePane.Controls) !=
		                        (pane == TerminalWorkspacePane.Controls);
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
		if (redrawMovedFrames)
			CompleteRootTransition();
	}
}

#pragma warning restore CS0618
