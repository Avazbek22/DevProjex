using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Execution;
using DevProjex.Terminal.Rendering;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DevProjex.Terminal.Tui;

#pragma warning disable CS0618

internal sealed partial class TerminalWorkspaceSession : IDisposable
{
	private const int WelcomeHorizontalMargin = 2;
	private const int WelcomeWideActionsWidth = 42;
	private const int SettingsRefreshDebounceMilliseconds = 200;
	private static readonly TimeSpan SettingsPersistenceShutdownBudget = TimeSpan.FromSeconds(1);

	private readonly IApplication _application;
	private readonly Window _root;
	private readonly TerminalServices _services;
	private readonly ITerminalEnvironment _environment;
	private readonly TerminalWorkspaceOptions _options;
	private readonly TerminalWorkspace _workspace;
	private readonly TerminalWorkspaceController _controller;
	private readonly TerminalParameterRowsBuilder _parameterRowsBuilder;
	private readonly TerminalWorkspacePresentation _presentation;
	private readonly TerminalWorkspaceCommandParser _commandParser = new();
	private readonly TerminalClipboardWriter _clipboardWriter;
	private readonly TerminalCommandHistory _commandHistory;
	private readonly TerminalCommandHistoryPersistenceQueue _commandHistoryPersistence;
	private readonly ITerminalOperationObserver _operationObserver;
	private readonly Action _prepareForShutdown;
	private readonly EventHandler<global::Terminal.Gui.App.EventArgs<System.Drawing.Rectangle>> _screenChangedHandler;
	private readonly EventHandler<SizeChangedEventArgs>? _driverSizeChangedHandler;
	private readonly global::Terminal.Gui.Drivers.IDriver? _subscribedDriver;
	private readonly CancellationTokenSource _sessionCts;
	private readonly CancellationTokenSource _settingsPersistenceCts = new();
	private readonly SemaphoreSlim _operationGate = new(1, 1);
	private readonly TerminalBackgroundTaskTracker _backgroundTasks = new();
	private readonly WorkspaceFocusModel _focus = new();
	private readonly AsyncOperationCoordinator _operations;
	private readonly TerminalExportDestinationHistory _exportDestinations = new();

	private TerminalWorkspaceScreen _screen;
	private TerminalWorkspaceLayoutMode _layoutMode;
	private int _terminalWidth;
	private int _terminalHeight;
	private bool _deferredInitialStart;
	private bool _gitCliAvailable;
	private bool _stopping;
	private bool _disposed;
	private Task? _openTask;
	private IRepositoryCacheSession? _ownedRepositorySession;
	private long _projectionRequestId;
	private long _previewRequestId;
	private long _settingsRefreshRequestId;
	private long _previewSearchRequestId;
	private long _treePreviewSyncRequestId;
	private long _workspacePersistenceRequestId;
	private int _workspacePersistencePending;
	private bool _previewSearchInProgress;

	private TerminalWelcomeContext? _welcomeContext;
	private RecentProjectsDb? _recentProjectsSnapshot;
	private string? _recentWorkspaceSelectionKey;
	private WelcomeViewGraph? _welcomeViews;
	private LoadingViewGraph? _loadingViews;
	private ObservableCollection<TerminalWelcomeActionRow>? _welcomeRows => _welcomeViews?.Rows;
	private ListView? _welcomeList => _welcomeViews?.List;
	private TextView? _welcomeDetail => _welcomeViews?.Detail;
	private FrameView? _welcomeActionsFrame => _welcomeViews?.ActionsFrame;
	private FrameView? _welcomeDetailFrame => _welcomeViews?.DetailFrame;
	private Label? _welcomeActionsHeading => _welcomeViews?.ActionsHeading;
	private Label? _welcomeDetailHeading => _welcomeViews?.DetailHeading;
	private Label? _welcomeHeading => _welcomeViews?.Heading;
	private Label? _welcomeVersion => _welcomeViews?.Version;
	private Label? _welcomeTagline => _welcomeViews?.Tagline;
	private Label? _welcomeCurrentTitle => _welcomeViews?.CurrentTitle;
	private Label? _welcomeCurrentPath => _welcomeViews?.CurrentPath;
	private Label? _welcomeCurrentStatus => _welcomeViews?.CurrentStatus;
	private Label? _welcomeQuickStart => _welcomeViews?.QuickStart;
	private Label? _welcomeFooter => _welcomeViews?.Footer;
	private Label? _tooSmall =>
		_workspaceViews?.TooSmall ?? _welcomeViews?.TooSmall ?? _loadingViews?.TooSmall;

	private TerminalWorkspaceState? _state;
	private WorkspaceViewGraph? _workspaceViews;
	private TerminalProjectTreeView? _tree => _workspaceViews?.Tree;
	private TerminalVirtualizedPreviewView? _preview => _workspaceViews?.Preview;
	private Label? _previewRange => _workspaceViews?.PreviewRange;
	private FrameView? _treeFrame => _workspaceViews?.TreeFrame;
	private FrameView? _previewFrame => _workspaceViews?.PreviewFrame;
	private Label? _treePanelHeading => _workspaceViews?.TreeHeading;
	private Label? _treeEmptyHint => _workspaceViews?.TreeEmptyHint;
	private Label? _previewPanelHeading => _workspaceViews?.PreviewHeading;
	private Label? _workspaceHeading => _workspaceViews?.WorkspaceHeading;
	private Label? _workspacePath => _workspaceViews?.WorkspacePath;
	private TerminalCornerProgressView? _cornerProgress => _workspaceViews?.CornerProgress;
	private Label? _status => _workspaceViews?.Status;
	private Label? _footer => _workspaceViews?.Footer;
	private TerminalWorkspaceCommandLineView? _commandLine =>
		_workspaceViews?.CommandLine ?? _welcomeViews?.CommandLine;
	private TerminalWorkspaceCommand? _activeCommandResult;
	private TerminalOperationProgressView? _operationProgress;
	private TerminalWorkspacePane _activePane { get => _focus.Pane; set => _focus.Pane = value; }
	private TerminalWorkspacePane _commandReturnPane { get => _focus.CommandReturnPane; set => _focus.CommandReturnPane = value; }
	private TerminalControlSection _activeControlSection { get => _focus.ControlSection; set => _focus.ControlSection = value; }
	private ProjectContextView _previewView = ProjectContextView.Tree;
	private ProjectContextDocumentFormat _format = ProjectContextDocumentFormat.Text;
	private string? _searchQuery;
	private string? _previewSearchQuery;
	private string? _selectedTreePath;
	private bool _suppressTreeSelectionTracking;
	private bool _suppressWorkspaceFocusTracking;
	private TerminalWorkspaceActionRegistry? _workspaceActionRegistry;
	private TerminalWorkspaceActionRegistryCacheKey? _workspaceActionRegistryKey;

	public TerminalWorkspaceSession(
		IApplication application,
		Window root,
		TerminalServices services,
		ITerminalEnvironment environment,
		TerminalWorkspaceOptions options,
		TerminalWorkspace workspace,
		ITerminalOperationObserver operationObserver,
		CancellationToken cancellationToken,
		Action prepareForShutdown)
	{
		_application = application;
		_root = root;
		_services = services;
		_environment = environment;
		_options = options;
		_workspace = workspace;
		_operationObserver = operationObserver ??
			throw new ArgumentNullException(nameof(operationObserver));
		_prepareForShutdown = prepareForShutdown ??
			throw new ArgumentNullException(nameof(prepareForShutdown));
		_controller = new TerminalWorkspaceController(services, environment);
		_clipboardWriter = new TerminalClipboardWriter(
			() => _application.Clipboard,
			sequence =>
			{
				var driver = _application.Driver;
				if (driver is null)
					return false;
				driver.WriteRaw(sequence);
				return true;
			},
			() => _environment.IsOutputInteractive && !_environment.IsTermDumb);
		_parameterRowsBuilder = new TerminalParameterRowsBuilder(
			L,
			FitControlLabel,
			services.IgnoreOptionsService.FormatContentRedactionLabel,
			environment.SupportsUnicode && !options.Plain);
		_presentation = TerminalWorkspacePresentationPolicy.Resolve(
			options.ColorMode,
			options.Plain,
			environment);
		_commandHistory = new TerminalCommandHistory(
			services.TerminalSettingsStore.LoadCommandHistory());
		_commandHistoryPersistence = new TerminalCommandHistoryPersistenceQueue(
			services.TerminalSettingsStore.SaveCommandStateAsync,
			_settingsPersistenceCts.Token);
		_sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		_operations = new AsyncOperationCoordinator(_sessionCts.Token);
		var initialScreen = _application.Driver?.Screen ?? _application.Screen;
		_terminalWidth = Math.Max(_environment.Width, initialScreen.Width);
		_terminalHeight = Math.Max(_environment.Height, initialScreen.Height);
		_application.Keyboard.KeyDown += OnRootKeyDown;
		_services.Localization.LanguageChanged += OnLanguageChanged;
		_screenChangedHandler = OnApplicationScreenChanged;
		_application.ScreenChanged += _screenChangedHandler;
		if (_application.Driver is { } driver)
		{
			_subscribedDriver = driver;
			_driverSizeChangedHandler = OnDriverSizeChanged;
			driver.SizeChanged += _driverSizeChangedHandler;
		}
	}

	private void OnLanguageChanged(object? sender, EventArgs args)
	{
		if (_disposed || _stopping)
			return;

		_application.Invoke(RefreshLocalizedSurface);
	}

	private void RefreshLocalizedSurface()
	{
		if (_disposed || _stopping)
			return;

		if (_tooSmall is not null)
			_tooSmall.Text = L("Terminal.Tui.Error.Resize");
		switch (_screen)
		{
			case TerminalWorkspaceScreen.Welcome:
				RefreshWelcomeLocalization();
				break;
			case TerminalWorkspaceScreen.Workspace when _state is not null:
				CancelTransientStatus();
				RefreshWorkspace();
				UpdateWorkspaceHeaderLayout();
				UpdatePreviewRange();
				_commandLine?.RefreshLayout();
				SchedulePreviewRefresh();
				break;
		}

		InvalidateViewHierarchy(_root);
		_application.LayoutAndDraw();
	}

	private void RefreshWelcomeLocalization()
	{
		if (_welcomeContext is null || _welcomeRows is null || _welcomeList is null)
			return;

		var selectedKind = _welcomeList.SelectedItem is { } selected &&
		                   selected >= 0 && selected < _welcomeRows.Count
			? _welcomeRows[selected].Action.Kind
			: (TerminalWelcomeActionKind?)null;
		var actions = BuildWelcomeActions(_welcomeContext);
		_welcomeRows.Clear();
		foreach (var action in actions)
			_welcomeRows.Add(new TerminalWelcomeActionRow(action));
		var selectedIndex = selectedKind is null
			? 0
			: actions
				.Select((action, index) => (action, index))
				.FirstOrDefault(pair => pair.action.Kind == selectedKind)
				.index;
		if (_welcomeRows.Count > 0)
			_welcomeList.SelectedItem = Math.Clamp(selectedIndex, 0, _welcomeRows.Count - 1);

		if (_welcomeTagline is not null)
			_welcomeTagline.Text = L("Terminal.Tui.Welcome.Description");
		if (_welcomeCurrentTitle is not null)
			_welcomeCurrentTitle.Text = L("Terminal.Tui.CurrentDirectory");
		if (_welcomeCurrentStatus is not null)
		{
			_welcomeCurrentStatus.Text = _welcomeContext.CanOpenCurrentDirectory
				? L("Terminal.Tui.WorkspaceDetected")
				: L("Terminal.Tui.WorkspaceNotDetected");
		}
		if (_welcomeActionsFrame is not null)
			_welcomeActionsFrame.Title = L("Terminal.Tui.Actions");
		if (_welcomeActionsHeading is not null)
			_welcomeActionsHeading.Text = $"> {L("Terminal.Tui.Actions")}";
		if (_welcomeDetailFrame is not null)
			_welcomeDetailFrame.Title = L("Terminal.Tui.Details");
		if (_welcomeDetailHeading is not null)
			_welcomeDetailHeading.Text = L("Terminal.Tui.Details");
		if (_welcomeQuickStart is not null)
			_welcomeQuickStart.Text = L("Terminal.Tui.Welcome.QuickStart");
		if (_welcomeFooter is not null)
			_welcomeFooter.Text = L("Terminal.Tui.Footer.Welcome");
		UpdateWelcomeSelection();
	}

	private void OnApplicationScreenChanged(
		object? sender,
		global::Terminal.Gui.App.EventArgs<System.Drawing.Rectangle> args)
	{
		if (_disposed)
			return;
		// Inline roots are resized in Driver.SizeChanged below. Applying the old
		// geometry here would draw a split frame into the already resized buffer.
		if (_application.AppModel == AppModel.Inline)
			return;

		ApplyTerminalResize((_application.Driver?.Screen ?? _application.Screen).Size);
	}

	private void OnDriverSizeChanged(object? sender, SizeChangedEventArgs args)
	{
		if (_disposed)
			return;

		_application.Invoke(() =>
		{
			if (_disposed)
				return;
			if (_application.AppModel == AppModel.Inline && args.Size is { } size)
			{
				_root.Width = size.Width;
				_root.Height = size.Height;
			}
			if (args.Size is { } terminalSize)
				ApplyTerminalResize(terminalSize);
			else
				ApplyTerminalResize((_application.Driver?.Screen ?? _application.Screen).Size);
		});
	}

	private void ApplyTerminalResize(System.Drawing.Size terminalSize)
	{
		var requestedFocus = _focus.Capture();
		var previousFocusSuppression = _suppressWorkspaceFocusTracking;
		_suppressWorkspaceFocusTracking = true;
		var sizeChanged = false;
		try
		{
			sizeChanged = UpdateTerminalSize(terminalSize.Width, terminalSize.Height);
			ApplyCurrentLayout();
		}
		finally
		{
			_focus.Restore(requestedFocus);
			_suppressWorkspaceFocusTracking = previousFocusSuppression;
		}
		UpdateWorkspaceFocus();
		if (sizeChanged)
			InvalidateAfterTerminalResize();
	}

	public bool ExitRequested { get; private set; }

	public void Start()
	{
		_layoutMode = ResolveLayout();
		if (_layoutMode == TerminalWorkspaceLayoutMode.TooSmall)
		{
			_deferredInitialStart = true;
			ShowInitialTooSmall();
			return;
		}

		StartCore();
	}

	public async Task CompleteAsync()
	{
		await FlushPendingWorkspacePersistenceAsync().ConfigureAwait(false);
		_stopping = true;
		await _commandHistoryPersistence.CompleteAsync().ConfigureAwait(false);
		_settingsPersistenceCts.CancelAfter(SettingsPersistenceShutdownBudget);
		_sessionCts.Cancel();
		_operations.Dispose();

		await _backgroundTasks.CompleteAsync().ConfigureAwait(false);
	}

	private void StartCore()
	{
		if (_options.ShowWelcome)
			ShowWelcome();
		else
			BeginOpenProject(_options.ProjectPath, _options.Profile);
	}

	private void ShowInitialTooSmall()
	{
		ClearRoot();
		_screen = TerminalWorkspaceScreen.TooSmall;
		var tooSmall = new TerminalLiteralLabel
		{
			X = Pos.Center(),
			Y = Pos.Center(),
			Width = Dim.Auto(),
			Text = L("Terminal.Tui.Error.Resize"),
			SchemeName = TerminalWorkspaceTheme.Warning
		};
		_loadingViews = new LoadingViewGraph(tooSmall);
		_root.Add(tooSmall);
	}

	private void ShowWelcome(TerminalWelcomeActionKind? selectedAction = null)
	{
		ReleaseOwnedRepositorySession();
		CancelWorkspaceRefreshes();
		ClearRoot();
		_screen = TerminalWorkspaceScreen.Welcome;
		_layoutMode = ResolveLayout();
		_welcomeContext = LoadWelcomeContext();
		var welcomeRows = new ObservableCollection<TerminalWelcomeActionRow>(
			BuildWelcomeActions(_welcomeContext).Select(static action => new TerminalWelcomeActionRow(action)));
		var selectedActionIndex = selectedAction is null
			? 0
			: welcomeRows
				.Select((row, index) => (row, index))
				.FirstOrDefault(pair => pair.row.Action.Kind == selectedAction)
				.index;
		selectedActionIndex = Math.Clamp(selectedActionIndex, 0, Math.Max(0, welcomeRows.Count - 1));
		if (welcomeRows.Count > 0)
			welcomeRows[selectedActionIndex].IsSelected = true;

		var welcomeHeading = new TerminalLiteralLabel
		{
			X = 2,
			Y = 1,
			Text = "DevProjex Terminal",
			SchemeName = TerminalWorkspaceTheme.Accent
		};
		var versionText = $"v{GetProductVersion()}";
		var welcomeVersion = new TerminalLiteralLabel
		{
			X = Pos.AnchorEnd(versionText.Length + 2),
			Y = 1,
			Width = versionText.Length,
			Text = versionText,
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		var welcomeTagline = new TerminalLiteralLabel
		{
			X = 2,
			Y = 2,
			Width = Dim.Fill(2),
			Text = L("Terminal.Tui.Welcome.Description"),
			SchemeName = TerminalWorkspaceTheme.Base
		};
		var welcomeCurrentTitle = new TerminalLiteralLabel
		{
			X = 2,
			Y = 4,
			Text = L("Terminal.Tui.CurrentDirectory"),
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		var welcomeCurrentPath = new TerminalLiteralLabel
		{
			X = 2,
			Y = 5,
			Width = Dim.Fill(33),
			Height = 1,
			Text = _welcomeContext.CurrentDirectory,
			SchemeName = TerminalWorkspaceTheme.Base
		};
		var welcomeCurrentStatus = new TerminalLiteralLabel
		{
			X = Pos.AnchorEnd(31),
			Y = 4,
			Width = 29,
			Height = 1,
			Text = _welcomeContext.CanOpenCurrentDirectory
				? L("Terminal.Tui.WorkspaceDetected")
				: L("Terminal.Tui.WorkspaceNotDetected"),
			SchemeName = _welcomeContext.CanOpenCurrentDirectory
				? TerminalWorkspaceTheme.Success
				: TerminalWorkspaceTheme.Secondary
		};

		var welcomeActionsFrame = new TerminalLiteralFrameView
		{
			Title = L("Terminal.Tui.Actions"),
			SchemeName = TerminalWorkspaceTheme.FocusedPanel,
			BorderStyle = _presentation.BorderStyle
		};
		var welcomeActionsHeading = new TerminalLiteralLabel
		{
			X = 0,
			Y = 0,
			Width = Dim.Fill(),
			Height = 1,
			Text = $"> {L("Terminal.Tui.Actions")}",
			Visible = _options.Plain,
			SchemeName = TerminalWorkspaceTheme.Accent
		};
		var welcomeList = new ListView
		{
			X = 0,
			Y = _options.Plain ? 1 : 0,
			Width = Dim.Fill(),
			Height = Dim.Fill(),
			ShowMarks = false,
			SchemeName = TerminalWorkspaceTheme.List
		};
		welcomeList.SetSource(welcomeRows);
		if (welcomeRows.Count > 0)
			welcomeList.SelectedItem = selectedActionIndex;
		welcomeList.ValueChanged += (_, _) => UpdateWelcomeSelection();
		// Defer nested workflows until the Enter key that accepted the row has finished dispatching.
		welcomeList.Accepted += (_, _) => _application.Invoke(ActivateWelcomeSelection);
		welcomeActionsFrame.Add(welcomeActionsHeading, welcomeList);

		var welcomeDetailFrame = new TerminalLiteralFrameView
		{
			Title = L("Terminal.Tui.Details"),
			SchemeName = TerminalWorkspaceTheme.Panel,
			BorderStyle = _presentation.BorderStyle
		};
		var welcomeDetailHeading = new TerminalLiteralLabel
		{
			X = 0,
			Y = 0,
			Width = Dim.Fill(),
			Height = 1,
			Text = L("Terminal.Tui.Details"),
			Visible = _options.Plain,
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		var welcomeDetail = new TextView
		{
			X = 1,
			Y = _options.Plain ? 1 : 0,
			Width = Dim.Fill(1),
			Height = Dim.Fill(),
			ReadOnly = true,
			WordWrap = true,
			CanFocus = false,
			SchemeName = TerminalWorkspaceTheme.Base
		};
		welcomeDetailFrame.Add(welcomeDetailHeading, welcomeDetail);
		var welcomeQuickStart = new TerminalLiteralLabel
		{
			X = 2,
			Width = Dim.Fill(2),
			SchemeName = TerminalWorkspaceTheme.Secondary,
			Text = L("Terminal.Tui.Welcome.QuickStart")
		};
		var welcomeFooter = new TerminalLiteralLabel
		{
			X = 2,
			Y = Pos.AnchorEnd(1),
			Width = Dim.Fill(2),
			Text = L("Terminal.Tui.Footer.Welcome"),
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		var commandLine = new TerminalWorkspaceCommandLineView(
			_application,
			(text, cursor) => _commandParser.GetCompletion(text, cursor, BuildCommandParseContext()),
			(text, cursor) => _commandParser.GetGhostCompletion(text, cursor, BuildCommandParseContext()),
			L,
			_commandHistory,
			_options.Plain,
			_environment.SupportsUnicode)
		{
			X = 2,
			Y = Pos.AnchorEnd(1),
			Width = Dim.Fill(2)
		};
		commandLine.Submitted += (_, text) => SubmitCommandLine(text);
		commandLine.Canceled += (_, _) => CancelCommandLine();
		var tooSmall = CreateTooSmallLabel();
		_welcomeViews = new WelcomeViewGraph(
			welcomeRows,
			welcomeList,
			welcomeDetail,
			welcomeActionsFrame,
			welcomeDetailFrame,
			welcomeActionsHeading,
			welcomeDetailHeading,
			welcomeHeading,
			welcomeVersion,
			welcomeTagline,
			welcomeCurrentTitle,
			welcomeCurrentPath,
			welcomeCurrentStatus,
			welcomeQuickStart,
			welcomeFooter,
			commandLine,
			tooSmall);

		_root.Add(
			welcomeHeading,
			welcomeVersion,
			welcomeTagline,
			welcomeCurrentTitle,
			welcomeCurrentPath,
			welcomeCurrentStatus,
			welcomeActionsFrame,
			welcomeDetailFrame,
			welcomeQuickStart,
			welcomeFooter,
			commandLine,
			tooSmall);
		UpdateWelcomeSelection();
		ApplyWelcomeLayout();
		welcomeList.SetFocus();
		CompleteRootTransition();
	}

	private TerminalWelcomeContext LoadWelcomeContext()
	{
		var loadResult = _services.RecentProjectsStore.LoadForStartupWithStatus(
			TimeSpan.FromMilliseconds(200));
		_recentProjectsSnapshot = loadResult.Database;
		var recent = loadResult.Database.RecentFolders
			.Select(static entry => entry.Path);
		return TerminalWelcomePolicy.Create(_options.ProjectPath, recent);
	}

	private IReadOnlyList<TerminalWelcomeAction> BuildWelcomeActions(TerminalWelcomeContext context)
	{
		var actions = new List<TerminalWelcomeAction>();
		if (context.CanOpenCurrentDirectory)
		{
			actions.Add(new TerminalWelcomeAction(
				TerminalWelcomeActionKind.OpenCurrent,
				L("Terminal.Tui.Welcome.OpenCurrent"),
				L("Terminal.Tui.Welcome.OpenCurrent.Description")));
		}
		for (var index = 0; index < Math.Min(9, context.RecentProjects.Count); index++)
		{
			var path = context.RecentProjects[index];
			actions.Add(new TerminalWelcomeAction(
				TerminalWelcomeActionKind.RecentProject,
				TerminalTextEscaping.EscapeSingleLine(path),
				L("Terminal.Tui.Welcome.RecentProject.Description"),
				path,
				index + 1));
		}
		actions.Add(new TerminalWelcomeAction(
			TerminalWelcomeActionKind.RecentWorkspaces,
			L("Terminal.Tui.Welcome.Recent"),
			L("Terminal.Tui.Welcome.Recent.Description")));

		actions.AddRange(
		[
			new TerminalWelcomeAction(
				TerminalWelcomeActionKind.BrowseFolder,
				L("Terminal.Tui.Welcome.Browse"),
				L("Terminal.Tui.Welcome.Browse.Description")),
			new TerminalWelcomeAction(
				TerminalWelcomeActionKind.OpenPortableProfile,
				L("Terminal.Tui.Welcome.OpenProfile"),
				L("Terminal.Tui.Welcome.OpenProfile.Description")),
			new TerminalWelcomeAction(
				TerminalWelcomeActionKind.CloneRepository,
				L("Terminal.Tui.Welcome.Clone"),
				L("Terminal.Tui.Welcome.Clone.Description")),
			new TerminalWelcomeAction(
				TerminalWelcomeActionKind.OpenDesktop,
				L("Terminal.Tui.Welcome.OpenDesktop"),
				L("Terminal.Tui.Welcome.OpenDesktop.Description")),
			new TerminalWelcomeAction(
				TerminalWelcomeActionKind.Help,
				L("Terminal.Tui.Help"),
				L("Terminal.Tui.Welcome.Help.Description")),
			new TerminalWelcomeAction(
				TerminalWelcomeActionKind.Exit,
				L("Terminal.Tui.Exit"),
				L("Terminal.Tui.Welcome.Exit.Description"))
		]);
		return actions;
	}

	private void UpdateWelcomeSelection()
	{
		if (_welcomeRows is null || _welcomeList is null || _welcomeDetail is null)
			return;

		var selected = Math.Clamp(_welcomeList.SelectedItem ?? 0, 0, Math.Max(0, _welcomeRows.Count - 1));
		for (var index = 0; index < _welcomeRows.Count; index++)
			_welcomeRows[index].IsSelected = index == selected;
		_welcomeList.SetNeedsDraw();

		var action = _welcomeRows[selected].Action;
		_welcomeDetail.Text = action.Description;
	}

	private void ActivateWelcomeSelection()
	{
		if (_welcomeRows is null || _welcomeList?.SelectedItem is not { } selected ||
			selected < 0 || selected >= _welcomeRows.Count)
		{
			return;
		}

		var action = _welcomeRows[selected].Action;
		switch (action.Kind)
		{
			case TerminalWelcomeActionKind.OpenCurrent:
				if (_welcomeContext is not null)
					BeginOpenProject(_welcomeContext.CurrentDirectory, _options.Profile);
				break;
			case TerminalWelcomeActionKind.RecentWorkspaces:
				OpenRecentWorkspaces();
				break;
			case TerminalWelcomeActionKind.RecentProject:
				if (action.Value is { } recentProject)
					BeginOpenProject(recentProject, _options.Profile, TerminalProjectOpenSource.Recent);
				break;
			case TerminalWelcomeActionKind.BrowseFolder:
				BrowseForProject();
				break;
			case TerminalWelcomeActionKind.OpenPortableProfile:
				OpenPortableProfile();
				break;
			case TerminalWelcomeActionKind.CloneRepository:
				BeginCloneRepository();
				break;
			case TerminalWelcomeActionKind.OpenDesktop:
				BeginOpenDesktopFromWelcome();
				break;
			case TerminalWelcomeActionKind.Help:
				ShowHelp(welcome: true);
				break;
			case TerminalWelcomeActionKind.Exit:
				RequestExit();
				break;
			default:
				throw new ArgumentOutOfRangeException();
		}
	}

	private void BrowseForProject()
	{
		var path = SelectPath(
			L("Terminal.Tui.Welcome.Browse"),
			TerminalPathPickerMode.Directory,
			_welcomeContext?.CurrentDirectory);
		if (path is null)
			return;
		if (!TryResolveDirectory(path, out var project))
		{
			ShowError("DPX-TUI-PROJECT-UNAVAILABLE", L("Terminal.Tui.Error.ProjectUnavailable"));
			return;
		}

		if (TryResolveAutomaticProfileInteractively(project, out var profile))
			BeginOpenProject(project, profile);
	}

	private void OpenPortableProfile()
	{
		var profilePath = SelectPath(
			L("Terminal.Tui.Welcome.OpenProfile"),
			TerminalPathPickerMode.JsonFile,
			_welcomeContext?.CurrentDirectory);
		if (profilePath is null)
			return;
		if (!File.Exists(profilePath))
		{
			ShowError("DPX-CLI-PROFILE-INVALID", L("Terminal.Tui.Error.ProfileUnavailable"));
			return;
		}

		var projectPath = SelectPath(
			L("Terminal.Tui.ProjectDirectory"),
			TerminalPathPickerMode.Directory,
			_welcomeContext?.CurrentDirectory);
		if (projectPath is null)
			return;
		if (!TryResolveDirectory(projectPath, out var project))
		{
			ShowError("DPX-TUI-PROJECT-UNAVAILABLE", L("Terminal.Tui.Error.ProjectUnavailable"));
			return;
		}

		BeginOpenProject(
			project,
			new ProjectProfileReference(
				ProjectProfileSourceKind.Portable,
				Path.GetFullPath(profilePath)));
	}

	private void BeginCloneRepository(
		string? repositoryUrl = null,
		bool returnToRepositoryHistory = false)
	{
		var url = repositoryUrl ?? Prompt(
			L("Terminal.Tui.Welcome.Clone"),
			L("Terminal.Tui.RepositoryUrl"),
			string.Empty);
		if (string.IsNullOrWhiteSpace(url))
			return;

		if (!RepositoryUrlUtility.IsSupportedCloneSource(url))
		{
			ShowError("DPX-TUI-GIT-URL-INVALID", L("Git.Error.InvalidUrl"));
			return;
		}

		var safeUrl = RepositoryUrlUtility.ToSafeDisplay(url);
		var repositoryName = RepositoryUrlUtility.GetRepositoryName(safeUrl);
		ShowCloneProgress(repositoryName, safeUrl);
		var operationCts = ReplaceActiveOperation();
		TrackActiveOperation(Task.Run(async () =>
		{
			try
			{
				var progress = new SynchronousProgress<string>(UpdateCloneProgressSafe);
				var cloneCoordinator = new TerminalRepositoryCloneCoordinator(
					_services.GitRepositoryService,
					_services.RepoCacheService);
				using var cloneLease = await cloneCoordinator
					.AcquireAsync(
						url,
						progress,
						async phase =>
						{
							UpdateClonePhaseSafe(
								phase == TerminalRepositoryClonePhase.SwitchingBranch
									? "Terminal.Tui.Clone.CheckingOut"
									: "Terminal.Tui.Clone.Connecting",
								phase == TerminalRepositoryClonePhase.Cloning
									? L("Terminal.Tui.Clone.StartingGit")
									: L("Terminal.Tui.Action.GetUpdates"));
							if (phase == TerminalRepositoryClonePhase.Cloning)
							{
								await _operationObserver
									.ObservePhaseAsync(
										TerminalOperationPhase.CloneConnecting,
										operationCts.Token)
									.ConfigureAwait(false);
							}
						},
						operationCts.Token)
					.ConfigureAwait(false);
				var result = cloneLease.Result;

				UpdateClonePhaseSafe(
					"Terminal.Tui.Clone.PreparingWorkspace",
					L("Terminal.Tui.Clone.ResolvingIdentity"));
				var identity = ProjectSourceIdentityResolver.CreateCloneIdentity(
					result.RepositoryUrl ?? url,
					result.RepositoryName,
					result.DefaultBranch);
				UpdateClonePhaseSafe(
					"Terminal.Tui.Clone.LoadingContext",
					L("Terminal.Tui.Clone.ScanningProject"));
				var preparedSession = cloneLease.DetachSession();
				await OpenProjectCoreAsync(
						result.LocalPath,
						ResolveAutomaticProfile(result.LocalPath).Profile,
						operationCts,
						returnToRepositoryHistory
							? TerminalProjectOpenSource.RecentRepository
							: TerminalProjectOpenSource.Clone,
						releaseOperation: false,
						identity,
						preparedSession)
					.ConfigureAwait(false);
				if (cloneLease.UpdateFailed)
				{
					await InvokeAsync(() =>
					{
						if (_operations.IsCurrent(WorkspaceOperationKind.Active, operationCts) &&
						    _screen == TerminalWorkspaceScreen.Workspace)
						{
							SetOperationStatus(
								L("Toast.Git.CachedUpdateFailed"),
								TerminalWorkspaceTheme.Warning);
						}
						return true;
					}).ConfigureAwait(false);
				}
			}
			catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
			{
				if (returnToRepositoryHistory)
					ReturnToRepositoryHistoryAfterCancellation(operationCts);
				else
					ReturnToWelcomeAfterCancellation(operationCts);
			}
			catch
			{
				if (returnToRepositoryHistory)
					ReturnToRepositoryHistoryWithError(
						operationCts,
						"DPX-TUI-CLONE-FAILED",
						L("Terminal.Tui.Error.CloneFailed"));
				else
					ReturnToWelcomeWithError(
						operationCts,
						"DPX-TUI-CLONE-FAILED",
						L("Terminal.Tui.Error.CloneFailed"));
			}
			finally
			{
				ReleaseActiveOperation(operationCts);
			}
		}, CancellationToken.None));
	}

	private AutomaticProfileResolution ResolveAutomaticProfile(string projectPath)
	{
		if (_services.LocalProfileStore is ProjectProfileStore store)
		{
			var lookup = store.LookupProfile(projectPath, TimeSpan.FromMilliseconds(200));
			return lookup.Status switch
			{
				ProjectProfileLookupStatus.Found => new AutomaticProfileResolution(
					ProjectProfileReference.Local,
					lookup.Status),
				_ => new AutomaticProfileResolution(
					ProjectProfileReference.Standard,
					lookup.Status)
			};
		}

		return _services.LocalProfileStore.TryLoadProfile(projectPath, out _)
			? new AutomaticProfileResolution(
				ProjectProfileReference.Local,
				ProjectProfileLookupStatus.Found)
			: new AutomaticProfileResolution(
				ProjectProfileReference.Standard,
				ProjectProfileLookupStatus.Missing);
	}

	private bool TryResolveAutomaticProfileInteractively(
		string projectPath,
		out ProjectProfileReference profile)
	{
		while (true)
		{
			var resolution = ResolveAutomaticProfile(projectPath);
			profile = resolution.Profile;
			if (resolution.Status is
				ProjectProfileLookupStatus.Found or
				ProjectProfileLookupStatus.Missing)
			{
				return true;
			}

			var action = ShowChoice(
				L("Terminal.Tui.ProfileRecovery"),
				L(resolution.Status == ProjectProfileLookupStatus.InvalidStorage
					? "Terminal.Tui.ProfileInvalidRecovery"
					: "Terminal.Tui.ProfileUnavailableRecovery"),
				L("Terminal.Tui.Back"),
				L("Terminal.Tui.Retry"),
				L("Terminal.Tui.UseStandardProfile"));
			switch (action)
			{
				case 1:
					continue;
				case 2:
					profile = ProjectProfileReference.Standard;
					return true;
				default:
					return false;
			}
		}
	}

	private void BeginOpenDesktopFromWelcome()
	{
		ShowWelcomeStatus(L("Terminal.Tui.OpeningDesktop"), TerminalWorkspaceTheme.Accent);
		var operationCts = ReplaceActiveOperation();
		TrackActiveOperation(Task.Run(async () =>
		{
			try
			{
				var exitCode = await new DesktopCommandHandler(_environment, writeOutput: false)
					.OpenAsync(new DesktopOpenRequest(), operationCts.Token)
					.ConfigureAwait(false);
				if (exitCode != CommandLineExitCodes.Success)
					throw new TerminalWorkspaceOperationException("DPX-DESKTOP-REQUEST-FAILED");
				await InvokeAsync(() =>
				{
					ShowWelcomeStatus(L("Terminal.Tui.DesktopAccepted"), TerminalWorkspaceTheme.Success);
					ShowNotice(
						L("Terminal.Tui.Welcome.OpenDesktop"),
						L("Terminal.Tui.DesktopAccepted"),
						TerminalWorkspaceTheme.Success);
					_welcomeList?.SetFocus();
					return true;
				}).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
			{
				ShowWelcomeStatusSafe(L("Terminal.Tui.OperationCanceled"), TerminalWorkspaceTheme.Warning);
			}
			catch
			{
				await InvokeAsync(() =>
				{
					ShowError(
						"DPX-DESKTOP-REQUEST-FAILED",
						L("Terminal.Error.DesktopRequestFailed"));
					ShowWelcomeStatus(string.Empty, TerminalWorkspaceTheme.Secondary);
					return true;
				}).ConfigureAwait(false);
			}
			finally
			{
				ReleaseActiveOperation(operationCts);
			}
		}, CancellationToken.None));
	}

	private void BeginOpenProject(
		string projectPath,
		ProjectProfileReference profile,
		TerminalProjectOpenSource source = TerminalProjectOpenSource.Other,
		ProjectSourceIdentity? sourceIdentity = null,
		IRepositoryCacheSession? preparedRepositorySession = null)
	{
		ShowLoading(
			L("Terminal.Tui.LoadingProject"),
			sourceIdentity?.SourceReference ?? projectPath);
		var operationCts = ReplaceActiveOperation();
		_openTask = TrackBackgroundTask(Task.Run(
			() => OpenProjectCoreAsync(
				projectPath,
				profile,
				operationCts,
				source,
				sourceIdentity: sourceIdentity,
				preparedRepositorySession: preparedRepositorySession),
			CancellationToken.None));
	}

	private async Task OpenProjectCoreAsync(
		string projectPath,
		ProjectProfileReference profile,
		CancellationTokenSource operationCts,
		TerminalProjectOpenSource source = TerminalProjectOpenSource.Other,
		bool releaseOperation = true,
		ProjectSourceIdentity? sourceIdentity = null,
		IRepositoryCacheSession? preparedRepositorySession = null)
	{
		var sessionAccepted = false;
		try
		{
			await _operationObserver
				.ObservePhaseAsync(
					TerminalOperationPhase.ProjectLoading,
					operationCts.Token)
				.ConfigureAwait(false);
			var state = await _controller
				.OpenAsync(projectPath, profile, operationCts.Token, sourceIdentity)
				.ConfigureAwait(false);
			var gitCliAvailable = await ResolveGitCliAvailabilityAsync(
					state.Plan,
					operationCts.Token)
				.ConfigureAwait(false);
			if (_stopping || operationCts.IsCancellationRequested)
				return;
			sessionAccepted = await InvokeAsync(() =>
				TerminalRepositorySessionOwnership.TryPublishAndReplace(
					_operations.IsCurrent(WorkspaceOperationKind.Active, operationCts),
					() =>
					{
						_gitCliAvailable = gitCliAvailable;
						ShowWorkspace(state);
					},
					ref _ownedRepositorySession,
					preparedRepositorySession)).ConfigureAwait(false);
			if (!sessionAccepted)
				return;

			if (state.Plan.SourceIdentity?.RepositoryUrl is { Length: > 0 } repositoryUrl)
			{
				_recentProjectsSnapshot = _services.RecentProjectsStore.AddRepository(
					_recentProjectsSnapshot,
					repositoryUrl);
			}
			else
			{
				_recentProjectsSnapshot = _services.RecentProjectsStore.AddFolder(
					_recentProjectsSnapshot,
					state.Plan.SourceRoot);
			}
		}
		catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
		{
			ReturnToWelcomeAfterCancellation(operationCts);
		}
		catch (PortableProjectProfileException exception)
		{
			ReturnFromProjectOpenError(
				operationCts,
				exception.Code,
				L("Terminal.Error.ProfileInvalid"),
				projectPath,
				source,
				sourceIdentity);
		}
		catch (ProjectContextValidationException exception)
		{
			ReturnFromProjectOpenError(
				operationCts,
				exception.Code,
				ResolveValidationErrorMessage(exception.Code),
				projectPath,
				source,
				sourceIdentity);
		}
		catch
		{
			ReturnFromProjectOpenError(
				operationCts,
				"DPX-TUI-PROJECT-OPEN-FAILED",
				L("Terminal.Tui.Error.ProjectUnavailable"),
				projectPath,
				source,
				sourceIdentity);
		}
		finally
		{
			if (!sessionAccepted)
				preparedRepositorySession?.Dispose();
			if (releaseOperation)
				ReleaseActiveOperation(operationCts);
		}
	}

	private void ReturnFromProjectOpenError(
		CancellationTokenSource operationCts,
		string code,
		string message,
		string projectPath,
		TerminalProjectOpenSource source,
		ProjectSourceIdentity? sourceIdentity)
	{
		if (source is TerminalProjectOpenSource.Recent or
			TerminalProjectOpenSource.RecentRepository)
		{
			var detail = ResolveProjectOpenErrorDetail(projectPath, sourceIdentity);
			ReturnToRepositoryHistoryWithError(operationCts, code, $"{message}\n\n{detail}");
			return;
		}
		ReturnToWelcomeWithError(operationCts, code, message);
	}

	internal static string ResolveProjectOpenErrorDetail(
		string projectPath,
		ProjectSourceIdentity? sourceIdentity)
	{
		var displaySource = sourceIdentity is { SourceType: ProjectSourceType.GitClone } identity
			? RepositoryUrlUtility.ToSafeDisplay(identity.RepositoryUrl ?? identity.SourceReference)
			: projectPath;
		return TerminalTextEscaping.EscapeSingleLine(displaySource);
	}

	private void ReturnToWelcomeAfterCancellation(CancellationTokenSource operationCts)
	{
		if (_stopping || !_operations.IsCurrent(WorkspaceOperationKind.Active, operationCts))
			return;
		_application.Invoke(() =>
		{
			ShowWelcome();
			ShowWelcomeStatus(L("Terminal.Tui.OperationCanceled"), TerminalWorkspaceTheme.Warning);
		});
	}

	private void ReturnToWelcomeWithError(
		CancellationTokenSource operationCts,
		string code,
		string message)
	{
		if (_stopping || !_operations.IsCurrent(WorkspaceOperationKind.Active, operationCts))
			return;
		_application.Invoke(() =>
		{
			ShowWelcome();
			// Inline mode must paint the restored root before a nested modal starts drawing.
			_application.Invoke(() => ShowError(code, message));
		});
	}

	private void ShowLoading(string title, string detail)
	{
		CancelWorkspaceRefreshes();
		ClearRoot();
		_screen = TerminalWorkspaceScreen.Loading;
		_layoutMode = ResolveLayout();
		var heading = new TerminalLiteralLabel
		{
			X = 2,
			Y = 1,
			Text = "DevProjex Terminal",
			SchemeName = TerminalWorkspaceTheme.Accent
		};
		var spinner = new SpinnerView
		{
			X = 2,
			Y = 4,
			AutoSpin = _presentation.AllowMotion,
			Visible = _presentation.AllowMotion,
			SchemeName = TerminalWorkspaceTheme.Accent
		};
		var operation = new TerminalLiteralLabel
		{
			X = _presentation.AllowMotion ? 6 : 2,
			Y = 4,
			Width = Dim.Fill(2),
			Text = title,
			SchemeName = TerminalWorkspaceTheme.Base
		};
		var details = new TextView
		{
			X = _presentation.AllowMotion ? 6 : 2,
			Y = 6,
			Width = Dim.Fill(4),
			Height = 4,
			ReadOnly = true,
			WordWrap = true,
			CanFocus = false,
			Text = TerminalTextEscaping.EscapeSingleLine(detail),
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		var footer = new TerminalLiteralLabel
		{
			X = 2,
			Y = Pos.AnchorEnd(1),
			Width = Dim.Fill(2),
			Text = L("Terminal.Tui.Footer.Loading"),
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		var tooSmall = CreateTooSmallLabel();
		_loadingViews = new LoadingViewGraph(tooSmall);
		_root.Add(heading, spinner, operation, details, footer, tooSmall);
		ApplyLoadingLayout();
		CompleteRootTransition();
	}

	private void ShowWorkspace(TerminalWorkspaceState state)
	{
		CancelWorkspaceRefreshes();
		ClearRoot();
		_screen = TerminalWorkspaceScreen.Workspace;
		var persisted = _services.TerminalSettingsStore.LoadProjectSettings(state.Plan.SourceRoot);
		if (persisted is not null)
		{
			state.RestoreSelectedRelativePaths(persisted.SelectedPaths);
			state.RestoreExpandedRelativePaths(persisted.ExpandedPaths);
			_previewView = Enum.IsDefined(persisted.PreviewView)
				? persisted.PreviewView
				: ProjectContextView.Tree;
			_format = Enum.IsDefined(persisted.Format)
				? persisted.Format
				: ProjectContextDocumentFormat.Text;
			if (!string.IsNullOrWhiteSpace(persisted.FocusedPath))
			{
				_selectedTreePath = Path.GetFullPath(Path.Combine(
					state.Plan.SourceRoot,
					persisted.FocusedPath.Replace('/', Path.DirectorySeparatorChar)));
				state.Reveal(_selectedTreePath);
			}
		}
		_state = state;
		_layoutMode = ResolveLayout();
		_activePane = TerminalWorkspacePane.Tree;

		var workspaceHeading = new TerminalLiteralLabel
		{
			X = 1,
			Y = 0,
			Width = Dim.Fill(1),
			Height = 1,
			Text = BuildWorkspaceHeading(state.Plan),
			SchemeName = TerminalWorkspaceTheme.Accent
		};
		var workspacePath = new TerminalLiteralLabel
		{
			X = 1,
			Y = 1,
			Width = Dim.Fill(1),
			Height = 1,
			Text = GetProjectDisplaySource(state.Plan),
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		var treeFrame = new TerminalLiteralFrameView
		{
			Title = L("Terminal.Tui.Tree"),
			BorderStyle = _presentation.BorderStyle,
			SchemeName = TerminalWorkspaceTheme.FocusedPanel
		};
		var previewFrame = new TerminalLiteralFrameView
		{
			BorderStyle = _presentation.BorderStyle,
			SchemeName = TerminalWorkspaceTheme.Panel
		};
		var treePanelHeading = new TerminalLiteralLabel
		{
			X = 0,
			Y = 0,
			Width = Dim.Fill(),
			Height = 1,
			Visible = _options.Plain,
			SchemeName = TerminalWorkspaceTheme.Accent
		};
		var previewPanelHeading = new TerminalLiteralLabel
		{
			X = 0,
			Y = 0,
			Width = Dim.Fill(),
			Height = 1,
			Visible = _options.Plain,
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		var tree = new TerminalProjectTreeView(
			index => index >= 0 && index < state.VisibleRows.Count
				? state.VisibleRows[index]
				: null,
			useUnicode: _environment.SupportsUnicode && !_options.Plain,
			showScrollBars: !_options.Plain)
		{
			X = 0,
			Y = _options.Plain ? 1 : 0,
			Width = Dim.Fill(),
			Height = Dim.Fill(),
			ShowMarks = false,
			SchemeName = TerminalWorkspaceTheme.List
		};
		tree.SetSource(state.VisibleRows);
		tree.UpdateContentMetrics(state.VisibleRowWidth, state.VisibleRows.Count);
		tree.ValueChanged += (_, _) => TrackTreeSelection();
		tree.KeyBindings.ReplaceCommands(Key.Space, Command.Activate);
		tree.Activated += (_, _) => ToggleCurrentTreeSelection();
		tree.Accepted += (_, _) => ToggleCurrentTreeExpansion();
		tree.SelectionToggleRequested += (_, _) => ToggleCurrentTreeSelection();
		tree.ExpansionToggleRequested += (_, _) => ToggleCurrentTreeExpansion();
		tree.CommandLineRequested += (_, _) => OpenCommandLine();
		tree.HasFocusChanged += (_, _) => UpdateWorkspaceFocus();
		var treeEmptyHint = new TerminalLiteralLabel
		{
			X = 2,
			Y = _options.Plain ? 2 : 1,
			Width = Dim.Fill(2),
			Height = 1,
			CanFocus = false,
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		var cornerProgress = new TerminalCornerProgressView(
			_application,
			_options.Plain,
			_environment.SupportsUnicode,
			UpdateWorkspaceHeaderLayout);

		var preview = new TerminalVirtualizedPreviewView(
			_environment.SupportsUnicode && !_options.Plain,
			showScrollBars: !_options.Plain)
		{
			X = 0,
			Y = _options.Plain ? 1 : 0,
			Width = Dim.Fill(),
			Height = Dim.Fill(1)
		};
		preview.SetDocument(state.PreviewDocument, preserveViewport: false);
		preview.RedactionToggleRequested += OnPreviewRedactionToggleRequested;
		preview.CommandLineRequested += (_, _) => OpenCommandLine();
		var previewRange = new TerminalLiteralLabel
		{
			X = 1,
			Y = Pos.AnchorEnd(1),
			Width = Dim.Fill(1),
			Height = 1,
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		preview.VisibleRangeChanged += (_, _) => UpdatePreviewRange();
		preview.HasFocusChanged += (_, _) => UpdateWorkspaceFocus();
		treeFrame.Add(treePanelHeading, tree, treeEmptyHint);
		previewFrame.Add(previewPanelHeading, preview, previewRange);
		var controls = CreateContextControls();
		if (state.VisibleRows.Count > 0)
		{
			tree.SelectedItem = FindSelectedTreeRow();
			_selectedTreePath = state.VisibleRows[tree.SelectedItem ?? 0].Node.FullPath;
		}

		var status = new TerminalLiteralLabel
		{
			X = 1,
			Y = Pos.AnchorEnd(2),
			Width = Dim.Fill(1),
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		var footer = new TerminalLiteralLabel
		{
			X = 1,
			Y = Pos.AnchorEnd(1),
			Width = Dim.Fill(1),
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		var commandLine = new TerminalWorkspaceCommandLineView(
			_application,
			(text, cursor) => _commandParser.GetCompletion(
				text,
				cursor,
				BuildCommandParseContext()),
			(text, cursor) => _commandParser.GetGhostCompletion(
				text,
				cursor,
				BuildCommandParseContext()),
			L,
			_commandHistory,
			_options.Plain,
			_environment.SupportsUnicode)
		{
			X = 1,
			Y = Pos.AnchorEnd(1),
			Width = Dim.Fill(1)
		};
		commandLine.Submitted += (_, text) => SubmitCommandLine(text);
		commandLine.Canceled += (_, _) => CancelCommandLine();
		var tooSmall = CreateTooSmallLabel();
		_workspaceViews = new WorkspaceViewBuilder()
			.WithHeader(workspaceHeading, workspacePath, cornerProgress)
			.WithTree(tree, treeFrame, treePanelHeading, treeEmptyHint)
			.WithPreview(preview, previewFrame, previewPanelHeading, previewRange)
			.WithControls(controls)
			.WithChrome(status, footer, commandLine, tooSmall)
			.Build();
		_root.Add(
			workspaceHeading,
			workspacePath,
			cornerProgress.View,
			treeFrame,
			previewFrame,
			controls.Frame,
			status,
			footer,
			commandLine,
			tooSmall);

		RefreshContextControls();
		tree.SetFocus();
		RefreshWorkspace();
		ApplyWorkspaceLayout();
		UpdateWorkspaceFocus();
		CompleteRootTransition();
		SchedulePreviewRefresh();
		if (persisted is not null)
			ScheduleSelectionProjection();
	}

	private void RefreshWorkspace()
	{
		if (_state is null || _tree is null || _preview is null)
			return;

		var selected = FindSelectedTreeRow();
		var treeHadFocus = _tree.HasFocus;
		var previewHadFocus = _preview.HasFocus;
		var controlsWereActive = _activePane == TerminalWorkspacePane.Controls;
		var controlsHadFocus = ControlsHaveFocus;
		var focusedControlSection = _activeControlSection;
		var aggregateControlWasActive = _activeAggregateControlSection == focusedControlSection;
		var previewRow = _preview.FirstVisibleLine;
		var previewColumn = _preview.HorizontalOffset;
		if (_state.VisibleRows.Count > 0)
			_tree.SelectedItem = Math.Clamp(selected, 0, _state.VisibleRows.Count - 1);
		var previewDocumentChanged = _preview.SetDocument(
			_state.PreviewDocument,
			preserveViewport: true);
		RestorePreviewViewport(previewRow, previewColumn);
		_state.ReleaseRetiredPreviewDocuments();
		if (treeHadFocus)
			_tree.SetFocus();
		else if (previewHadFocus)
			_preview.SetFocus();
		if (_status is not null && !_operations.IsRunning(WorkspaceOperationKind.TransientStatus))
			_status.Text = BuildStatus(_state, _application.Screen.Width);
		var previousFocusSuppression = _suppressWorkspaceFocusTracking;
		_suppressWorkspaceFocusTracking = controlsHadFocus || previousFocusSuppression;
		try
		{
			RefreshContextControls();
		}
		finally
		{
			_suppressWorkspaceFocusTracking = previousFocusSuppression;
		}
		_tree.UpdateContentMetrics(_state.VisibleRowWidth, _state.VisibleRows.Count);
		UpdateTreeEmptyHint();
		View? controlToRestore = aggregateControlWasActive
			? GetAggregateControlSection(focusedControlSection).List
			: GetControlSection(focusedControlSection).List;
		if (controlsHadFocus ||
			(controlsWereActive && !HasActiveOperation && controlToRestore?.Enabled == true))
		{
			_activeControlSection = focusedControlSection;
			_activeAggregateControlSection = aggregateControlWasActive
				? focusedControlSection
				: null;
			controlToRestore?.SetFocus();
			_activeControlSection = focusedControlSection;
		}
		UpdateWorkspaceFocus();
		if (previewDocumentChanged && !string.IsNullOrWhiteSpace(_previewSearchQuery))
			SchedulePreviewSearch(_previewSearchQuery, showNoResults: false);
		ScheduleWorkspacePersistence();
	}

	private void ScheduleWorkspacePersistence()
	{
		if (_state is null || _screen != TerminalWorkspaceScreen.Workspace || _stopping)
			return;
		Interlocked.Exchange(ref _workspacePersistencePending, 1);
		var requestId = Interlocked.Increment(ref _workspacePersistenceRequestId);
		_application.AddTimeout(TimeSpan.FromMilliseconds(250), () =>
		{
			if (_stopping || requestId != Volatile.Read(ref _workspacePersistenceRequestId) || _state is null)
				return false;
			var settings = CaptureWorkspaceSettings();
			if (settings is not null && Interlocked.Exchange(ref _workspacePersistencePending, 0) != 0)
				TrackBackgroundTask(PersistWorkspaceSettingsAsync(settings));
			return false;
		});
	}

	private TerminalProjectSettings? CaptureWorkspaceSettings()
	{
		if (_state is not { } state || _screen != TerminalWorkspaceScreen.Workspace)
			return null;
		var focusedPath = string.IsNullOrWhiteSpace(_selectedTreePath)
			? null
			: PathUtility.NormalizeSeparators(Path.GetRelativePath(state.Plan.SourceRoot, _selectedTreePath));
		return new TerminalProjectSettings(
			state.Plan.SourceRoot,
			state.BuildPersistedSelectedRelativePaths(),
			state.BuildExpandedRelativePaths(),
			focusedPath,
			_previewView,
			_format,
			DateTimeOffset.UtcNow);
	}

	private async Task FlushPendingWorkspacePersistenceAsync()
	{
		Interlocked.Increment(ref _workspacePersistenceRequestId);
		if (Interlocked.Exchange(ref _workspacePersistencePending, 0) == 0)
			return;
		if (CaptureWorkspaceSettings() is { } settings)
			await PersistWorkspaceSettingsAsync(settings).ConfigureAwait(false);
	}

	private void FlushPendingWorkspacePersistence() =>
		FlushPendingWorkspacePersistenceAsync().GetAwaiter().GetResult();

	private async Task PersistWorkspaceSettingsAsync(TerminalProjectSettings settings)
	{
		try
		{
			await _services.TerminalSettingsStore.SaveProjectSettingsAsync(settings).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Trace.TraceWarning("Terminal workspace settings could not be persisted: {0}", exception.Message);
		}
	}

	private void TrackTreeSelection()
	{
		if (_suppressTreeSelectionTracking)
			return;
		if (_state is null || _tree?.SelectedItem is not { } selected ||
			selected < 0 || selected >= _state.VisibleRows.Count)
		{
			return;
		}

		_selectedTreePath = _state.VisibleRows[selected].Node.FullPath;
		ScheduleSelectedFilePreviewSync(_selectedTreePath);
	}

	private void ScheduleSelectedFilePreviewSync(string path)
	{
		if (_state is null || _preview is null || Directory.Exists(path))
			return;
		var requestId = Interlocked.Increment(ref _treePreviewSyncRequestId);
		_application.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
		{
			if (_stopping || requestId != Volatile.Read(ref _treePreviewSyncRequestId) ||
				_preview is null || !PathComparer.Default.Equals(_selectedTreePath, path))
			{
				return false;
			}
			var section = _preview.Sections.FirstOrDefault(candidate =>
				candidate.SourcePath is not null && PathComparer.Default.Equals(candidate.SourcePath, path));
			if (section is not null)
			{
				_preview.ScrollTo(Math.Max(0, section.StartLine - 1), 0);
				UpdatePreviewRange();
			}
			return false;
		});
	}

	private int FindSelectedTreeRow()
	{
		if (_state is null || _state.VisibleRows.Count == 0)
			return 0;
		if (!string.IsNullOrWhiteSpace(_selectedTreePath))
		{
			for (var index = 0; index < _state.VisibleRows.Count; index++)
			{
				if (PathComparer.Default.Equals(
						_state.VisibleRows[index].Node.FullPath,
						_selectedTreePath))
				{
					return index;
				}
			}
		}

		return Math.Clamp(_tree?.SelectedItem ?? 0, 0, _state.VisibleRows.Count - 1);
	}

	private string BuildStatus(TerminalWorkspaceState state, int width)
	{
		var warningCount = state.Plan.Diagnostics.Count(static diagnostic =>
			diagnostic.Severity == ContextDiagnosticSeverity.Warning);
		var errorCount = state.Plan.Diagnostics.Count(static diagnostic =>
			diagnostic.Severity == ContextDiagnosticSeverity.Error);
		var tokens = ResolveDisplayedTokenCount(state);
		var folders = state.HasVisibleTreeItems ? state.SelectedFolderCount : 0;
		if (width < 80)
		{
			return $"{state.SelectedFileCount:N0} F  {folders:N0} D  " +
				   $"~{tokens:N0} tok  " +
				   $"{warningCount:N0} W  {errorCount:N0} E";
		}

		var separator = _environment.SupportsUnicode ? PanelSeparator : " | ";
		return string.Join(
			separator,
			$"{L("Terminal.Analysis.Files")} {state.SelectedFileCount:N0}",
			$"{L("Terminal.Analysis.Folders")} {folders:N0}",
			TerminalWorkspace.FormatBytes(state.Plan.IncludedBytes),
			$"~{tokens:N0} {L("Terminal.Tui.TokensShort")}",
			$"{L("Terminal.Tui.Warnings")} {warningCount:N0}",
			$"{L("Terminal.Tui.Errors")} {errorCount:N0}");
	}

	internal static long ResolveDisplayedTokenCount(TerminalWorkspaceState state)
	{
		ArgumentNullException.ThrowIfNull(state);
		return state.PreviewOutputMetrics.Tokens;
	}

	private void UpdateWorkspaceHeaderLayout()
	{
		if (_workspaceHeading is null || _cornerProgress is null || _state is null)
			return;

		var reservedWidth = _cornerProgress.ReservedWidth;
		var headingWidth = Math.Max(1, _terminalWidth - 2 - reservedWidth);
		_workspaceHeading.Width = headingWidth;
		var heading = FitEndToWidth(
			BuildWorkspaceHeading(_state.Plan),
			headingWidth);
		_workspaceHeading.Text = heading + new string(
			' ',
			Math.Max(0, headingWidth - heading.GetColumns()));
		_workspaceHeading.SetNeedsDraw();
	}

	private void UpdateTreeEmptyHint()
	{
		if (_treeEmptyHint is null || _tree is null || _state is null)
			return;

		var visible = !_state.HasVisibleTreeItems && !_state.HasTreeFilter;
		_treeEmptyHint.Visible = visible;
		_treeEmptyHint.Text = visible
			? FitEndToWidth(
				L("Terminal.Tui.Tree.Empty"),
				Math.Max(1, _tree.Viewport.Width - 3))
			: string.Empty;
		_treeEmptyHint.SetNeedsDraw();
	}

	private void UpdatePreviewRange()
	{
		if (_preview is null || _previewRange is null)
			return;

		var firstLine = Math.Min(_preview.LineCount, _preview.FirstVisibleLine + 1);
		var lastLine = Math.Max(firstLine, _preview.VisibleLastLine);
		var totalFiles = _state?.Plan.IncludedFiles.Count ?? _preview.Sections.Count;
		var visibleFileRange = $"{totalFiles:N0}";
		var sectionText = $"{L("Terminal.Analysis.Files")} {totalFiles:N0}  |  ";
		var firstSectionIndex = PreviewDocumentSectionLookup.FindFirstIntersectingSectionIndex(
			_preview.Sections,
			firstLine);
		if (firstSectionIndex >= 0 &&
			_preview.Sections[firstSectionIndex].StartLine <= lastLine)
		{
			var lastSectionIndex = firstSectionIndex;
			while (lastSectionIndex + 1 < _preview.Sections.Count &&
				   _preview.Sections[lastSectionIndex + 1].StartLine <= lastLine)
			{
				lastSectionIndex++;
			}

			visibleFileRange = firstSectionIndex == lastSectionIndex
				? $"{firstSectionIndex + 1:N0}/{totalFiles:N0}"
				: $"{firstSectionIndex + 1:N0}-{lastSectionIndex + 1:N0}/{totalFiles:N0}";
			var displayPath = firstSectionIndex == lastSectionIndex
				? PanelSeparator + FitEndToWidth(
					_preview.Sections[firstSectionIndex].DisplayPath,
					Math.Max(8, _preview.Viewport.Width / 3))
				: string.Empty;
			sectionText =
				$"{L("Terminal.Analysis.Files")} {visibleFileRange}{displayPath}  |  ";
		}

		var lastVisibleColumn = Math.Min(
			_preview.MaxLineLength,
			_preview.HorizontalOffset + _preview.VisibleTextWidth);
		var columns = _preview.HasHorizontalOverflow
			? $"  |  {L("Terminal.Tui.Preview.Columns")} " +
			  $"{_preview.HorizontalOffset + 1:N0}-" +
			  $"{lastVisibleColumn:N0}/" +
			  $"{_preview.MaxLineLength:N0}"
			: string.Empty;
		var fullRange =
			$"{sectionText}{L("Terminal.Tui.Preview.Lines")} " +
			$"{firstLine:N0}-{lastLine:N0}/{_preview.LineCount:N0}{columns}";
		var compactColumns = _preview.HasHorizontalOverflow
			? $"  |  C {_preview.HorizontalOffset + 1:N0}-{lastVisibleColumn:N0}/{_preview.MaxLineLength:N0}"
			: string.Empty;
		var compactRange =
			$"F {visibleFileRange}  |  L {firstLine:N0}-{lastLine:N0}/{_preview.LineCount:N0}" +
			compactColumns;
		if (_previewSearchInProgress)
		{
			var searchRange = $"{L("Terminal.Tui.Search")}...  |  ";
			fullRange = searchRange + fullRange;
			compactRange = $"/ ...  |  {compactRange}";
		}
		else if (_preview.SearchQuery.Length > 0)
		{
			var matchCount = _preview.IsSearchCapped
				? $"{_preview.SearchMatchCount:N0}+"
				: $"{_preview.SearchMatchCount:N0}";
			var matchRange =
				$"{L("Terminal.Tui.Search")} " +
				$"{_preview.CurrentSearchMatchOrdinal:N0}/{matchCount}  |  ";
			fullRange = matchRange + fullRange;
			compactRange =
				$"/ {_preview.CurrentSearchMatchOrdinal:N0}/{matchCount}  |  " +
				compactRange;
		}
		var availableWidth = Math.Max(1, _preview.Viewport.Width - 2);
		_previewRange.Text = fullRange.GetColumns() <= availableWidth
			? fullRange
			: compactRange;
	}

	private void UpdatePanelTitles()
	{
		if (_treeFrame is null || _previewFrame is null || _controlsFrame is null)
			return;
		var treeMarker = _activePane == TerminalWorkspacePane.Tree ? "> " : "  ";
		var treeTitle = $"{treeMarker}{L("Terminal.Tui.Tree")}";
		if (_state?.HasTreeFilter == true)
		{
			treeTitle +=
				$"{PanelSeparator}/{_state.TreeFilterQuery} ({_state.TreeFilterMatchCount:N0})";
		}
		var renderedTreeTitle = FitEndToWidth(
			treeTitle,
			Math.Max(8, _treeFrame.Viewport.Width - 2));
		_treeFrame.Title = _options.Plain ? string.Empty : renderedTreeTitle;
		if (_treePanelHeading is not null)
			_treePanelHeading.Text = renderedTreeTitle;
		var previewMarker = _activePane == TerminalWorkspacePane.Preview ? "> " : "  ";
		var previewTitle =
			$"{previewMarker}{L("Terminal.Tui.Preview")}{PanelSeparator}" +
			$"{_workspace.LocalizeView(_previewView)}{PanelSeparator}{TerminalWorkspace.FormatContextFormat(_format)}";
		if (_previewSearchInProgress)
		{
			previewTitle += $"{PanelSeparator}{L("Terminal.Tui.Search")}...";
		}
		else if (_preview?.SearchQuery.Length > 0)
		{
			var matchCount = _preview.IsSearchCapped
				? $"{_preview.SearchMatchCount:N0}+"
				: $"{_preview.SearchMatchCount:N0}";
			previewTitle +=
				$"{PanelSeparator}/{_preview.SearchQuery} " +
				$"({_preview.CurrentSearchMatchOrdinal:N0}/{matchCount})";
		}
		var previewTitleWidth = Math.Max(8, _previewFrame.Viewport.Width - 2);
		var renderedPreviewTitle = previewTitle.GetColumns() <= previewTitleWidth
			? previewTitle
			: $"{previewMarker}{L("Terminal.Tui.Preview")}{PanelSeparator}" +
			  $"{_workspace.LocalizeView(_previewView)}{PanelSeparator}{GetFormatToken()}";
		_previewFrame.Title = _options.Plain ? string.Empty : renderedPreviewTitle;
		if (_previewPanelHeading is not null)
			_previewPanelHeading.Text = renderedPreviewTitle;
		var controlsTitle =
			$"{(_activePane == TerminalWorkspacePane.Controls ? "> " : "  ")}" +
			L("Terminal.Tui.ContextControls");
		_controlsFrame.Title = _options.Plain ? string.Empty : controlsTitle;
		if (_controlsPanelHeading is not null)
			_controlsPanelHeading.Text = controlsTitle;
	}

	private void UpdateFooter()
	{
		if (_footer is null || _commandLine?.Visible == true)
			return;
		var wide = _application.Screen.Width >= 110;
		_footer.Text = _activePane switch
		{
			TerminalWorkspacePane.Tree =>
				L(wide ? "Terminal.Tui.Footer.Tree.Wide" : "Terminal.Tui.Footer.Tree"),
			TerminalWorkspacePane.Preview =>
				L(wide ? "Terminal.Tui.Footer.Preview.Wide" : "Terminal.Tui.Footer.Preview"),
			_ => L(wide ? "Terminal.Tui.Footer.Controls.Wide" : "Terminal.Tui.Footer.Controls")
		};
	}

	private string GetFormatToken() =>
		TerminalWorkspace.FormatContextFormat(_format);

	private void UpdateWorkspaceFocus()
	{
		if (_tree is null || _preview is null || ActiveControlView is null ||
			_treeFrame is null || _previewFrame is null || _controlsFrame is null)
			return;

		if (!_suppressWorkspaceFocusTracking)
		{
			if (_tree.HasFocus)
				_activePane = TerminalWorkspacePane.Tree;
			else if (_preview.HasFocus)
				_activePane = TerminalWorkspacePane.Preview;
			else if (ControlsHaveFocus)
				_activePane = TerminalWorkspacePane.Controls;
		}
		_treeFrame.SchemeName = _activePane == TerminalWorkspacePane.Tree
			? TerminalWorkspaceTheme.FocusedPanel
			: TerminalWorkspaceTheme.Panel;
		_previewFrame.SchemeName = _activePane == TerminalWorkspacePane.Preview
			? TerminalWorkspaceTheme.FocusedPanel
			: TerminalWorkspaceTheme.Panel;
		_controlsFrame.SchemeName = _activePane == TerminalWorkspacePane.Controls
			? TerminalWorkspaceTheme.FocusedPanel
			: TerminalWorkspaceTheme.Panel;
		foreach (var section in Enum.GetValues<TerminalControlSection>())
		{
			var frame = GetControlSectionFrame(section);
			if (frame is null)
				continue;
			frame.SchemeName = _activePane == TerminalWorkspacePane.Controls &&
							   section == _activeControlSection
				? TerminalWorkspaceTheme.FocusedPanel
				: TerminalWorkspaceTheme.Panel;
		}
		UpdateControlSelectionSchemes();
		UpdatePanelTitles();
		UpdateFooter();
	}

	private void UpdateControlSelectionSchemes()
	{
		foreach (var section in Enum.GetValues<TerminalControlSection>())
		{
			var list = GetControlSection(section).List;
			var aggregate = GetAggregateControlSection(section).List;
			var sectionIsActive = _activePane == TerminalWorkspacePane.Controls &&
			                      section == _activeControlSection;
			var aggregateIsActive = sectionIsActive && aggregate is not null &&
			                        (aggregate.HasFocus ||
			                         _activeAggregateControlSection == section);
			if (list is not null)
			{
				list.SchemeName = sectionIsActive && !aggregateIsActive
					? TerminalWorkspaceTheme.List
					: TerminalWorkspaceTheme.InactiveList;
				list.SetNeedsDraw();
			}
			if (aggregate is not null)
			{
				aggregate.SetActive(aggregateIsActive);
				aggregate.SchemeName = aggregateIsActive
					? TerminalWorkspaceTheme.List
					: TerminalWorkspaceTheme.InactiveList;
				aggregate.SetNeedsDraw();
			}
		}
	}

	private void ApplyCurrentLayout()
	{
		if (_disposed)
			return;
		_layoutMode = ResolveLayout();
		if (_deferredInitialStart)
		{
			if (_layoutMode == TerminalWorkspaceLayoutMode.TooSmall)
				return;
			_deferredInitialStart = false;
			StartCore();
			return;
		}

		switch (_screen)
		{
			case TerminalWorkspaceScreen.Welcome:
				ApplyWelcomeLayout();
				break;
			case TerminalWorkspaceScreen.Loading:
				ApplyLoadingLayout();
				break;
			case TerminalWorkspaceScreen.Workspace:
				ApplyWorkspaceLayout();
				break;
			case TerminalWorkspaceScreen.TooSmall:
				break;
			default:
				throw new ArgumentOutOfRangeException();
		}
	}

	private void ApplyWelcomeLayout()
	{
		if (_welcomeHeading is null || _welcomeVersion is null || _welcomeTagline is null ||
			_welcomeCurrentTitle is null || _welcomeCurrentPath is null || _welcomeCurrentStatus is null ||
			_welcomeActionsFrame is null || _welcomeDetailFrame is null ||
			_welcomeQuickStart is null || _welcomeFooter is null || _tooSmall is null)
		{
			return;
		}

		var tooSmall = _layoutMode == TerminalWorkspaceLayoutMode.TooSmall;
		SetVisible(
			!tooSmall,
			_welcomeHeading,
			_welcomeVersion,
			_welcomeTagline,
			_welcomeCurrentTitle,
			_welcomeCurrentPath,
			_welcomeCurrentStatus,
			_welcomeActionsFrame,
			_welcomeDetailFrame,
			_welcomeQuickStart,
			_welcomeFooter);
		_tooSmall.Visible = tooSmall;
		if (_operationProgress is not null)
			_operationProgress.View.Visible = !tooSmall;
		if (tooSmall)
			return;

		var actionHeight = Math.Min((_welcomeRows?.Count ?? 6) + 2, 13);
		var contentWidth = Math.Max(1, _terminalWidth - 4);
		_welcomeCurrentPath.Text = FitPathToWidth(
			_welcomeContext?.CurrentDirectory ?? string.Empty,
			contentWidth);
		var currentStatus = _welcomeContext?.CanOpenCurrentDirectory == true
			? L("Terminal.Tui.WorkspaceDetected")
			: L("Terminal.Tui.WorkspaceNotDetected");
		var titleWidth = L("Terminal.Tui.CurrentDirectory").GetColumns();
		var availableStatusWidth = Math.Max(0, contentWidth - titleWidth - 4);
		var statusWidth = Math.Min(currentStatus.GetColumns(), availableStatusWidth);
		_welcomeCurrentStatus.Width = Math.Max(1, statusWidth);
		_welcomeCurrentStatus.X = Pos.AnchorEnd(statusWidth + 2);
		_welcomeCurrentStatus.Text = FitEndToWidth(currentStatus, statusWidth);
		_welcomeCurrentStatus.Visible = statusWidth >= currentStatus.GetColumns();
		if (_layoutMode is TerminalWorkspaceLayoutMode.Split or TerminalWorkspaceLayoutMode.Wide)
		{
			_welcomeActionsFrame.X = WelcomeHorizontalMargin;
			_welcomeActionsFrame.Y = 7;
			_welcomeActionsFrame.Width = WelcomeWideActionsWidth;
			_welcomeActionsFrame.Height = actionHeight;
			_welcomeDetailFrame.X = Pos.Right(_welcomeActionsFrame) + 2;
			_welcomeDetailFrame.Y = 7;
			_welcomeDetailFrame.Width = Dim.Fill(WelcomeHorizontalMargin);
			_welcomeDetailFrame.Height = actionHeight;
			_welcomeQuickStart.Y = 7 + actionHeight + 1;
			_welcomeQuickStart.Height = 3;
			_welcomeCurrentPath.Width = Dim.Fill(WelcomeHorizontalMargin);
			return;
		}

		_welcomeActionsFrame.X = 2;
		_welcomeActionsFrame.Y = 7;
		_welcomeActionsFrame.Width = Dim.Fill(2);
		_welcomeActionsFrame.Height = actionHeight;
		_welcomeDetailFrame.X = 2;
		_welcomeDetailFrame.Y = 7 + actionHeight;
		_welcomeDetailFrame.Width = Dim.Fill(2);
		_welcomeDetailFrame.Height = Math.Max(3, _application.Screen.Height - (9 + actionHeight));
		_welcomeQuickStart.Visible = false;
		_welcomeCurrentPath.Width = Dim.Fill(2);
	}

	private void ApplyLoadingLayout()
	{
		if (_tooSmall is null)
			return;
		var tooSmall = _layoutMode == TerminalWorkspaceLayoutMode.TooSmall;
		foreach (var view in _root.SubViews.Where(view => !ReferenceEquals(view, _tooSmall)))
			view.Visible = !tooSmall;
		_tooSmall.Visible = tooSmall;
	}

	private void ApplyWorkspaceLayout()
	{
		if (_treeFrame is null || _previewFrame is null || _controlsFrame is null ||
			_tooSmall is null ||
			_workspaceHeading is null || _workspacePath is null ||
			_status is null || _footer is null || _commandLine is null)
		{
			return;
		}

		var requestedFocus = _focus.Capture();
		var commandWasEditing = _commandLine.IsEditing;
		var tooSmall = _layoutMode == TerminalWorkspaceLayoutMode.TooSmall;
		var previousFocusSuppression = _suppressWorkspaceFocusTracking;
		_suppressWorkspaceFocusTracking = true;
		try
		{
			SetVisible(
				!tooSmall,
				_workspaceHeading,
				_workspacePath,
				_treeFrame,
				_previewFrame,
				_controlsFrame,
				_status);
			_footer.Visible = !tooSmall && !_commandLine.Visible;
			if (tooSmall)
			{
				_commandLine.Close();
				CancelCommandResult();
			}
			else
			{
				_commandLine.RefreshLayout();
			}
			_cornerProgress?.ApplyLayout(tooSmall);
			_tooSmall.Visible = tooSmall;
			if (tooSmall)
				return;

			var contentWidth = Math.Max(1, _terminalWidth - 2);
			UpdateWorkspaceHeaderLayout();
			_workspacePath.Text = FitPathToWidth(
				_state is null ? string.Empty : GetProjectDisplaySource(_state.Plan),
				contentWidth);
			_treeFrame.X = 0;
			_treeFrame.Y = 2;
			_treeFrame.Height = Dim.Fill(3);
			_previewFrame.Y = 2;
			_previewFrame.Height = Dim.Fill(3);
			if (_collapsedControls is not null)
				_collapsedControls.Visible = false;
			if (_contentControlsFrame is not null)
				_contentControlsFrame.Visible = true;
			if (_filterControlsHost is not null)
				_filterControlsHost.Visible = true;
			if (_layoutMode == TerminalWorkspaceLayoutMode.Wide)
			{
				_treeFrame.Width = Dim.Percent(27);
				_previewFrame.X = Pos.Right(_treeFrame);
				_previewFrame.Width = Dim.Fill(WideControlsWidth);
				_controlsFrame.X = Pos.AnchorEnd(WideControlsWidth);
				_controlsFrame.Y = 2;
				_controlsFrame.Width = WideControlsWidth;
				_controlsFrame.Height = Dim.Fill(3);
				_treeFrame.Visible = true;
				_previewFrame.Visible = true;
				_controlsFrame.Visible = true;
			}
			else if (_layoutMode == TerminalWorkspaceLayoutMode.Split &&
					 requestedFocus.Pane != TerminalWorkspacePane.Controls)
			{
				_treeFrame.Height = Dim.Fill(6);
				_previewFrame.Height = Dim.Fill(6);
				_treeFrame.Width = Dim.Percent(46);
				_previewFrame.X = Pos.Right(_treeFrame);
				_previewFrame.Width = Dim.Fill();
				_treeFrame.Visible = true;
				_previewFrame.Visible = true;
				_controlsFrame.X = 0;
				_controlsFrame.Y = Pos.AnchorEnd(5);
				_controlsFrame.Width = Dim.Fill();
				_controlsFrame.Height = 3;
				_controlsFrame.Visible = true;
				if (_collapsedControls is not null)
					_collapsedControls.Visible = true;
				if (_contentControlsFrame is not null)
					_contentControlsFrame.Visible = false;
				if (_filterControlsHost is not null)
					_filterControlsHost.Visible = false;
			}
			else
			{
				_treeFrame.Width = Dim.Fill();
				_previewFrame.X = 0;
				_previewFrame.Width = Dim.Fill();
				_controlsFrame.X = 0;
				_controlsFrame.Y = 2;
				_controlsFrame.Width = Dim.Fill();
				_controlsFrame.Height = Dim.Fill(3);
				ShowSinglePane(requestedFocus.Pane);
			}

			RefreshWorkspace();
			_focus.Restore(requestedFocus);
			if (commandWasEditing)
			{
				_commandLine.RestoreInputFocus();
			}
			else switch (requestedFocus.Pane)
			{
				case TerminalWorkspacePane.Tree:
					_tree?.SetFocus();
					break;
				case TerminalWorkspacePane.Preview:
					_preview?.SetFocus();
					break;
				case TerminalWorkspacePane.Controls:
					ActiveControlView?.SetFocus();
					break;
				default:
					throw new ArgumentOutOfRangeException();
			}
			_focus.Restore(requestedFocus);
		}
		finally
		{
			_focus.Restore(requestedFocus);
			_suppressWorkspaceFocusTracking = previousFocusSuppression;
		}
		UpdateWorkspaceFocus();
		_operationProgress?.ApplyLayout(_terminalWidth, _terminalHeight);
	}

	private void ShowSinglePane(TerminalWorkspacePane pane)
	{
		if (_treeFrame is null || _previewFrame is null || _controlsFrame is null)
			return;
		_treeFrame.Visible = pane == TerminalWorkspacePane.Tree;
		_previewFrame.Visible = pane == TerminalWorkspacePane.Preview;
		_controlsFrame.Visible = pane == TerminalWorkspacePane.Controls;
	}

	private TerminalWorkspaceLayoutMode ResolveLayout()
	{
		return TerminalWorkspaceLayout.Resolve(_terminalWidth, _terminalHeight);
	}

	private bool UpdateTerminalSize(int width, int height)
	{
		var previousWidth = _terminalWidth;
		var previousHeight = _terminalHeight;
		if (width > 0)
			_terminalWidth = width;
		if (height > 0)
			_terminalHeight = height;
		return previousWidth != _terminalWidth || previousHeight != _terminalHeight;
	}

	private void InvalidateAfterTerminalResize()
	{
		_application.Invoke(() =>
		{
			if (_disposed)
				return;
			// Terminal.Gui keeps untouched cells clean in inline mode to preserve scrollback.
			// Clear and redraw the complete hierarchy in one callback so a resize cannot expose
			// either stale cells or an intermediate empty frame.
			_application.ClearScreenNextIteration = true;
			_root.SetNeedsLayout();
			InvalidateViewHierarchy(_root);
			_application.LayoutAndDraw(forceRedraw: true);
		});
	}

	private static void InvalidateViewHierarchy(View view)
	{
		if (!view.Visible)
			return;
		view.SetNeedsDraw();
		foreach (var child in view.SubViews)
			InvalidateViewHierarchy(child);
	}

	private Label CreateTooSmallLabel() =>
		new()
		{
			X = Pos.Center(),
			Y = Pos.Center(),
			Width = Dim.Auto(),
			Text = L("Terminal.Tui.Error.Resize"),
			SchemeName = TerminalWorkspaceTheme.Warning,
			Visible = false
		};

	private void OnRootKeyDown(object? sender, Key key)
	{
		if (!ReferenceEquals(_application.TopRunnableView, _root))
			return;
		if (_commandLine?.IsEditing == true)
			return;
		if (_commandLine?.IsShowingResult == true)
		{
			key.Handled = true;
			CancelCommandResult();
			_commandLine.Close();
			RestoreCommandFooterAndFocus();
			if (!TerminalWorkspaceCommandKey.IsActivation(key))
				return;
		}
		if (_operationProgress is not null && IsOverlayActivationKey(key))
		{
			key.Handled = true;
			return;
		}
		if ((_screen == TerminalWorkspaceScreen.Workspace || _screen == TerminalWorkspaceScreen.Welcome) &&
			_layoutMode != TerminalWorkspaceLayoutMode.TooSmall &&
			TerminalWorkspaceCommandKey.IsActivation(key))
		{
			key.Handled = true;
			OpenCommandLine();
			return;
		}

		if (key == Key.C.WithCtrl)
		{
			key.Handled = true;
			TryExitWorkspace();
			return;
		}

		if (key.NoShift == Key.Q)
		{
			key.Handled = true;
			TryExitWorkspace();
			return;
		}

		if (key == Key.Esc && HasActiveOperation)
		{
			key.Handled = true;
			CancelActiveOperation();
			ShowCancelingOperation();
			return;
		}
		if (HasActiveOperation)
		{
			key.Handled = true;
			ShowTransientStatus(string.Format(
				CultureInfo.CurrentCulture,
				L("Terminal.Tui.Operation.BusyHint"),
				_operationProgress?.OperationName ?? L("Terminal.Tui.Progress.Working")));
			return;
		}

		if (key == Key.F1 || key == new Key('?'))
		{
			key.Handled = true;
			ShowHelp(_screen == TerminalWorkspaceScreen.Welcome);
			return;
		}

		if (key == Key.P.WithCtrl)
		{
			key.Handled = true;
			ShowActionPalette();
			return;
		}

		if (_screen == TerminalWorkspaceScreen.Loading)
		{
			if (key == Key.Esc)
			{
				key.Handled = true;
				CancelActiveOperation();
			}
			return;
		}

		if (_screen == TerminalWorkspaceScreen.Welcome)
		{
			HandleWelcomeKey(key);
			return;
		}

		if (_screen == TerminalWorkspaceScreen.Workspace)
			HandleWorkspaceKey(key);
	}

	private static bool IsOverlayActivationKey(Key key) =>
		TerminalWorkspaceCommandKey.IsActivation(key) ||
		key == Key.F1 ||
		key == new Key('?') ||
		key == Key.P.WithCtrl;

	private void HandleWelcomeKey(Key key)
	{
		if (_welcomeList is null)
			return;
		if (key.NoShift == Key.J)
		{
			key.Handled = true;
			MoveListSelection(_welcomeList, 1);
		}
		else if (key.NoShift == Key.K)
		{
			key.Handled = true;
			MoveListSelection(_welcomeList, -1);
		}
		else if (key.AsRune.Value is >= '1' and <= '9')
		{
			var number = key.AsRune.Value - '0';
			var row = _welcomeRows?.Select((item, index) => (item, index))
				.FirstOrDefault(pair => pair.item.Action.Number == number);
			if (row is { item: not null })
			{
				key.Handled = true;
				_welcomeList.SelectedItem = row.Value.index;
				ActivateWelcomeSelection();
			}
		}
	}

	private void HandleWorkspaceKey(Key key)
	{
		if (_state is null || _tree is null || _preview is null)
			return;
		var controlsAreActive = _activePane == TerminalWorkspacePane.Controls || ControlsHaveFocus;

		if (key == Key.Esc)
		{
			key.Handled = true;
			if (_activePane == TerminalWorkspacePane.Tree && _state.HasTreeFilter)
			{
				ClearTreeFilter();
				return;
			}
			if (_activePane == TerminalWorkspacePane.Preview &&
				_preview.SearchQuery.Length > 0)
			{
				CancelPreviewSearch(clearQuery: true);
				_preview.ClearSearch();
				_previewSearchQuery = null;
				UpdatePanelTitles();
				UpdateFooter();
				return;
			}
			TryLeaveWorkspace(() => ShowWelcome());
			return;
		}
		if (key == Key.Tab.WithShift || key == Key.F6.WithShift)
		{
			key.Handled = true;
			MovePane(TerminalPaneNavigation.Previous);
			return;
		}
		if (key == Key.Tab || key == Key.F6)
		{
			key.Handled = true;
			MovePane(TerminalPaneNavigation.Next);
			return;
		}
		if (key.NoShift == Key.J || key.NoShift == Key.K)
		{
			key.Handled = true;
			if (_tree.HasFocus)
				MoveListSelection(_tree, key.NoShift == Key.J ? 1 : -1);
			else if (controlsAreActive)
				MoveControlSelection(key.NoShift == Key.J ? 1 : -1);
			else
				ScrollPreview(
					key.NoShift == Key.J
						? TerminalPreviewScroll.LineDown
						: TerminalPreviewScroll.LineUp);
			return;
		}
		if (controlsAreActive && (key == Key.CursorUp || key == Key.CursorDown))
		{
			key.Handled = true;
			MoveControlSelection(key == Key.CursorDown ? 1 : -1);
			return;
		}
		if (controlsAreActive && (key == Key.Home || key == Key.End))
		{
			key.Handled = true;
			FocusControlBoundary(_activeControlSection, first: key == Key.Home);
			return;
		}
		if (controlsAreActive && (key == Key.Enter || key == Key.Space))
		{
			key.Handled = true;
			if (IsAggregateControlFocused(_activeControlSection) ||
				_activeAggregateControlSection == _activeControlSection)
				ActivateAggregateControl(_activeControlSection);
			else
				ActivateSelectedControl(_activeControlSection);
			return;
		}
		if (_preview.HasFocus && TryHandlePreviewNavigation(key))
			return;
		if (_preview.HasFocus && (key == Key.Enter || key == Key.Space))
		{
			if (_preview.TryToggleActiveRedaction())
				key.Handled = true;
			return;
		}
		if (_preview.HasFocus && (key == new Key('[') || key == new Key(']')))
		{
			key.Handled = _preview.MoveActiveRedaction(reverse: key == new Key('['));
			return;
		}
		if (_tree.HasFocus && TryHandleTreeNavigation(key))
			return;
		if (key == Key.Enter && _tree.HasFocus)
		{
			key.Handled = true;
			ToggleCurrentTreeExpansion();
			return;
		}
		if (key == Key.CursorRight && _tree.HasFocus)
		{
			key.Handled = true;
			var selectedPath = CaptureCurrentTreePath();
			_state.Expand(_tree.SelectedItem ?? 0);
			_selectedTreePath = selectedPath;
			RefreshWorkspace();
			return;
		}
		if (key == Key.CursorLeft && _tree.HasFocus)
		{
			key.Handled = true;
			var selectedPath = CaptureCurrentTreePath();
			_state.Collapse(_tree.SelectedItem ?? 0);
			_selectedTreePath = selectedPath;
			RefreshWorkspace();
			return;
		}
		if (_tree.HasFocus && key == Key.CursorRight.WithShift)
		{
			key.Handled = true;
			_state.ExpandAll();
			RefreshWorkspace();
			return;
		}
		if (_tree.HasFocus && key == Key.CursorLeft.WithShift)
		{
			key.Handled = true;
			_state.CollapseAll();
			_selectedTreePath = _state.Plan.EffectiveTree.FullPath;
			RefreshWorkspace();
			return;
		}
		if (_tree.HasFocus && key == Key.A.WithCtrl)
		{
			key.Handled = true;
			_state.SelectAll();
			RefreshWorkspace();
			ScheduleSelectionProjection();
			return;
		}
		if (_tree.HasFocus && key == Key.U.WithCtrl)
		{
			key.Handled = true;
			_state.SelectNone();
			RefreshWorkspace();
			ScheduleSelectionProjection();
			return;
		}
		if (_tree.HasFocus && key.NoShift == Key.R)
		{
			key.Handled = true;
			RevealTreePath();
			return;
		}
		if (key == Key.D1 || key == Key.D2 || key == Key.D3)
		{
			key.Handled = true;
			_previewView = key == Key.D1
				? ProjectContextView.Tree
				: key == Key.D2
					? ProjectContextView.Content
					: ProjectContextView.TreeContent;
			RefreshWorkspace();
			SchedulePreviewRefresh();
			return;
		}
		if (key.NoShift == Key.F)
		{
			key.Handled = true;
			SelectPreviewFormat();
			return;
		}
		if (key == new Key('/'))
		{
			key.Handled = true;
			SearchTree();
			return;
		}
		if (_preview.HasFocus && (key == Key.N || key == Key.N.WithShift))
		{
			key.Handled = true;
			MovePreviewSearch(reverse: key == Key.N.WithShift);
			return;
		}
		if (_preview.HasFocus && (key == new Key('{') || key == new Key('}')))
		{
			key.Handled = true;
			MovePreviewSection(reverse: key == new Key('{'));
			return;
		}
		if (_preview.HasFocus && key == Key.G.WithCtrl)
		{
			key.Handled = true;
			GoToPreviewLine();
			return;
		}
		if (_preview.HasFocus && key.NoShift == Key.W)
		{
			key.Handled = true;
			var enabled = _preview.ToggleWordWrap();
			ShowTransientStatus(L(enabled
				? "Terminal.Tui.Preview.WrapEnabled"
				: "Terminal.Tui.Preview.WrapDisabled"));
			return;
		}
		if (key.NoShift == Key.E)
		{
			key.Handled = true;
			ExportContext();
			return;
		}
		if (key == Key.Z.WithShift)
		{
			key.Handled = true;
			ExportProject(ProjectCopyExportFormat.Zip);
			return;
		}
		if (key.NoShift == Key.D)
		{
			key.Handled = true;
			ShowDiagnostics();
			return;
		}
		if (key.NoShift == Key.Z)
		{
			key.Handled = true;
			ExportProject(ProjectCopyExportFormat.Folder);
			return;
		}
		if (key.NoShift == Key.A)
		{
			key.Handled = true;
			AnalyzeCurrentContext();
			return;
		}
		if (key.NoShift == Key.G)
		{
			key.Handled = true;
			OpenCurrentStateInDesktop();
			return;
		}
		if (key.NoShift == Key.P)
		{
			key.Handled = true;
			SaveProfile();
			return;
		}
		if (key.NoShift == Key.C)
		{
			key.Handled = true;
			FocusControlSection(TerminalControlSection.Content);
			return;
		}
		if (key.NoShift == Key.M)
		{
			key.Handled = true;
			CycleGitMode();
			return;
		}
		if (key.NoShift == Key.X)
		{
			key.Handled = true;
			FocusControlSection(TerminalControlSection.Exclusions);
			return;
		}
		if (key.NoShift == Key.T)
		{
			key.Handled = true;
			FocusControlSection(TerminalControlSection.Extensions);
			return;
		}
	}

	private void RevealTreePath()
	{
		if (_state is null || _tree is null)
			return;
		var path = Prompt(L("Terminal.Tui.Tree.Reveal"), L("Terminal.Tui.Tree.RevealPrompt"), null);
		if (path is null)
			return;
		var row = _state.Reveal(path);
		if (row < 0)
		{
			ShowTransientStatus(L("Terminal.Tui.Tree.RevealNotFound"));
			return;
		}
		_tree.SelectedItem = row;
		_selectedTreePath = _state.VisibleRows[row].Node.FullPath;
		RefreshWorkspace();
	}

	private void MovePreviewSection(bool reverse)
	{
		if (_preview is null || _preview.Sections.Count == 0)
			return;
		var line = _preview.FirstVisibleLine + 1;
		var index = PreviewDocumentSectionLookup.FindFirstIntersectingSectionIndex(_preview.Sections, line);
		if (index < 0)
			index = reverse ? _preview.Sections.Count : -1;
		index = Math.Clamp(index + (reverse ? -1 : 1), 0, _preview.Sections.Count - 1);
		_preview.ScrollTo(Math.Max(0, _preview.Sections[index].StartLine - 1), 0);
		UpdatePreviewRange();
	}

	private void GoToPreviewLine()
	{
		if (_preview is null)
			return;
		var value = Prompt(L("Terminal.Tui.Preview.GoToLine"), L("Terminal.Tui.Preview.GoToLinePrompt"), null);
		if (value is null)
			return;
		if (!int.TryParse(value, NumberStyles.None, CultureInfo.CurrentCulture, out var line) ||
			line < 1 || line > _preview.LineCount)
		{
			ShowTransientStatus(L("Terminal.Tui.Preview.InvalidLine"));
			return;
		}
		_preview.ScrollTo(line - 1, 0);
		UpdatePreviewRange();
	}

	private void OnPreviewRedactionToggleRequested(
		object? sender,
		TerminalPreviewRedactionToggleRequestedEventArgs eventArgs)
	{
		var kept = _services.SecretRedactionSession.ToggleKeepAsIs(eventArgs.OccurrenceId);
		SetOperationStatus(
			L(kept
				? "Terminal.Tui.Secret.Kept"
				: "Terminal.Tui.Secret.Redacted"),
			kept ? TerminalWorkspaceTheme.Warning : TerminalWorkspaceTheme.Accent);
		SchedulePreviewRefresh();
	}

	private void SearchTree()
	{
		if (_state is null || _tree is null)
			return;
		if (_preview?.HasFocus == true)
		{
			SearchPreview();
			return;
		}

		var query = Prompt(
			L("Terminal.Tui.Search"),
			L("Terminal.Tui.SearchPrompt"),
			_searchQuery);
		if (query is null)
			return;
		_searchQuery = query;
		var selectedPath = CaptureCurrentTreePath();
		_state.ApplyTreeFilter(query);
		_selectedTreePath = selectedPath;
		RefreshWorkspace();
		if (string.IsNullOrWhiteSpace(query))
		{
			_tree.SetFocus();
			return;
		}
		var match = _state.FindNext(query, _tree.SelectedItem ?? -1);
		if (match < 0)
		{
			ShowNotice(
				L("Terminal.Tui.Search"),
				L("Terminal.Tui.SearchNoResults"),
				TerminalWorkspaceTheme.Warning);
			return;
		}
		_tree.SelectedItem = match;
		TrackTreeSelection();
		_tree.SetFocus();
	}

	private void SearchPreview()
	{
		if (_preview is null)
			return;
		var query = Prompt(
			L("Terminal.Tui.Search"),
			L("Terminal.Tui.Preview.SearchPrompt"),
			_previewSearchQuery);
		if (query is null)
			return;
		_previewSearchQuery = query;
		if (string.IsNullOrWhiteSpace(query))
		{
			_preview.ClearSearch();
			UpdatePanelTitles();
			return;
		}
		if (!PreviewTextDocumentSearch.CanSearch(query.Trim()))
		{
			_previewSearchQuery = null;
			CancelPreviewSearch(clearQuery: true);
			_preview.ClearSearch();
			UpdatePanelTitles();
			ShowNotice(
				L("Terminal.Tui.Search"),
				L("Terminal.Tui.Preview.SearchTooShort"),
				TerminalWorkspaceTheme.Warning);
			return;
		}

		SchedulePreviewSearch(query, showNoResults: true);
	}

	private void SchedulePreviewSearch(
		string query,
		bool showNoResults,
		bool originatedFromCommandLine = false)
	{
		if (_preview is null || _state is null)
			return;
		var normalizedQuery = query.Trim();
		if (normalizedQuery.Length == 0)
		{
			CancelPreviewSearch(clearQuery: true);
			_preview.ClearSearch();
			UpdatePanelTitles();
			return;
		}
		if (!PreviewTextDocumentSearch.CanSearch(normalizedQuery))
		{
			CancelPreviewSearch(clearQuery: true);
			_preview.ClearSearch();
			UpdatePanelTitles();
			return;
		}

		CancelPreviewSearch(clearQuery: false);
		var state = _state;
		var document = state.PreviewDocument;
		var revision = state.Revision;
		var startLine = _preview.FirstVisibleLine;
		var requestId = Interlocked.Increment(ref _previewSearchRequestId);
		var operationCts = _operations.Start(WorkspaceOperationKind.PreviewSearch);
		var cancellationToken = operationCts.Token;
		_previewSearchQuery = normalizedQuery;
		_previewSearchInProgress = true;
		_preview.BeginSearch(normalizedQuery);
		UpdatePanelTitles();

		TrackOperation(WorkspaceOperationKind.PreviewSearch, operationCts, Task.Run(async () =>
		{
			try
			{
				var searchResult = PreviewTextDocumentSearch.Find(
					document,
					normalizedQuery,
					cancellationToken);
				await InvokeAsync(() =>
				{
					if (_stopping ||
						!ReferenceEquals(_state, state) ||
						!ReferenceEquals(state.PreviewDocument, document) ||
						state.Revision != revision ||
						!_operations.IsCurrent(WorkspaceOperationKind.PreviewSearch, operationCts) ||
						Volatile.Read(ref _previewSearchRequestId) != requestId ||
						_preview is null)
					{
						return false;
					}

					_previewSearchInProgress = false;
					var match = _preview.ApplySearchResults(
						normalizedQuery,
						searchResult,
						startLine);
					if (match is not null)
					{
						ScrollPreviewToMatch(match.Value);
						_preview.SetFocus();
						UpdateWorkspaceFocus();
					}
					else
					{
						UpdatePanelTitles();
						UpdatePreviewRange();
						if (showNoResults)
						{
							ShowNotice(
								L("Terminal.Tui.Search"),
								L("Terminal.Tui.Preview.SearchNoResults"),
								TerminalWorkspaceTheme.Warning);
						}
					}
					return true;
				}).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				// A newer query or preview document owns the visible results.
			}
			catch (ObjectDisposedException)
			{
				// Preview replacement may retire a file-backed document after cancellation.
			}
			catch
			{
				if (!_stopping &&
					_operations.IsCurrent(WorkspaceOperationKind.PreviewSearch, operationCts) &&
					Volatile.Read(ref _previewSearchRequestId) == requestId)
				{
					if (originatedFromCommandLine)
					{
						await ShowCommandFailureAsync(
							"DPX-TUI-PREVIEW-SEARCH-FAILED",
							L("Terminal.Tui.Error.PreviewFailed")).ConfigureAwait(false);
					}
					else
					{
						_application.Invoke(() =>
						{
							_previewSearchInProgress = false;
							ShowError(
								"DPX-TUI-PREVIEW-SEARCH-FAILED",
								L("Terminal.Tui.Error.PreviewFailed"));
						});
					}
				}
			}
			finally
			{
				_operations.Complete(WorkspaceOperationKind.PreviewSearch, operationCts);
			}
		}, CancellationToken.None));
	}

	private void CancelPreviewSearch(bool clearQuery)
	{
		Interlocked.Increment(ref _previewSearchRequestId);
		_operations.Cancel(WorkspaceOperationKind.PreviewSearch);
		_previewSearchInProgress = false;
		if (clearQuery)
			_previewSearchQuery = null;
	}

	private void MovePreviewSearch(bool reverse)
	{
		if (_preview is null || _preview.SearchQuery.Length == 0)
		{
			SearchPreview();
			return;
		}

		var current = _preview.CurrentSearchMatch ??
					  new PreviewTextSearchMatch(
						  _preview.FirstVisibleLine,
						  _preview.HorizontalOffset);
		var match = _preview.FindNextSearchMatch(
			current.Line,
			current.Column,
			reverse);
		if (match is null)
			return;

		ScrollPreviewToMatch(match.Value);
	}

	private void ScrollPreviewToMatch(PreviewTextSearchMatch match)
	{
		if (_preview is null)
			return;
		var displayColumn = _preview.GetDisplayColumn(match.Line, match.Column);
		var horizontalOffset = displayColumn < _preview.HorizontalOffset ||
							   displayColumn >=
							   _preview.HorizontalOffset + _preview.VisibleTextWidth
			? Math.Max(0, displayColumn - 4)
			: _preview.HorizontalOffset;
		_preview.ScrollTo(match.Line, horizontalOffset);
		UpdatePanelTitles();
	}

	private void ClearTreeFilter()
	{
		if (_state is null || _tree is null)
			return;

		var selectedPath = CaptureCurrentTreePath();
		_state.ApplyTreeFilter(null);
		_searchQuery = null;
		_selectedTreePath = selectedPath;
		RefreshWorkspace();
		_tree.SetFocus();
	}

	private void ToggleCurrentTreeSelection()
	{
		if (_state is null || _tree is null)
			return;

		var selectedPath = CaptureCurrentTreePath();
		if (TerminalWorkspace.TryToggleTreeRow(_state, _tree.SelectedItem))
		{
			_selectedTreePath = selectedPath;
			RefreshWorkspace();
			ScheduleSelectionProjection();
		}
	}

	private void ToggleCurrentTreeExpansion()
	{
		if (_state is null || _tree?.SelectedItem is not { } selected)
			return;

		var selectedPath = CaptureCurrentTreePath();
		_state.ToggleExpansion(selected);
		_selectedTreePath = selectedPath;
		RefreshWorkspace();
		_tree.SetFocus();
		UpdateWorkspaceFocus();
	}

	private string? CaptureCurrentTreePath()
	{
		if (_state is null || _tree?.SelectedItem is not { } selected ||
			selected < 0 || selected >= _state.VisibleRows.Count)
		{
			return _selectedTreePath;
		}

		return _state.VisibleRows[selected].Node.FullPath;
	}

	private void ExportContext(
		ProjectContextDocumentFormat? requestedFormat = null,
		string? requestedDestination = null,
		bool originatedFromCommandLine = false)
	{
		if (_state is null)
			return;
		var selectedFormat = requestedFormat ?? _format;
		var defaultPath = BuildDefaultExportPath(
			_state.Plan.SourceRoot,
			Directory.GetCurrentDirectory(),
			$"{GetProjectDisplayName(_state.Plan)}-context" +
			(selectedFormat switch
			{
				ProjectContextDocumentFormat.Json => ".json",
				ProjectContextDocumentFormat.Xml => ".xml",
				ProjectContextDocumentFormat.Text => ".txt",
				_ => ".md"
			}));
		var destination = requestedDestination ?? Prompt(
			L("Terminal.Tui.ExportContext"),
			L("Terminal.Tui.Destination"),
			_exportDestinations.Resolve(TerminalExportKind.Context, defaultPath));
		if (string.IsNullOrWhiteSpace(destination))
		{
			ShowTransientStatus(L("Terminal.Tui.Export.DestinationRequired"));
			return;
		}
		_exportDestinations.Remember(TerminalExportKind.Context, destination);
		var pendingSettingsRefresh = _operations.GetTask(WorkspaceOperationKind.SettingsRefresh);

		TrackActiveOperation(RunExportWorkflowAsync(
			L("Terminal.Tui.ExportContext"),
			async token =>
			{
				await AwaitPendingSettingsRefreshAsync(pendingSettingsRefresh, token).ConfigureAwait(false);
				return await _controller.PrepareContextExportAsync(
					_state,
					_previewView,
					selectedFormat,
					destination,
					overwrite: false,
					token,
					plain: _options.Plain).ConfigureAwait(false);
			},
			async (_, overwrite, token) => await _controller.ExportContextAsync(
				_state,
				_previewView,
				selectedFormat,
				destination,
				overwrite,
				token,
				plain: _options.Plain).ConfigureAwait(false),
			(exactDestination, dryRun) =>
				TerminalWorkspaceController.BuildEquivalentContextCommand(
					_state,
					_previewView,
					selectedFormat,
					exactDestination,
					dryRun),
			originatedFromCommandLine));
	}

	private void ExportProject(
		ProjectCopyExportFormat? requestedFormat = null,
		string? requestedDestination = null,
		bool originatedFromCommandLine = false)
	{
		if (_state is null)
			return;
		var selectedKind = requestedFormat ?? ProjectCopyExportFormat.Folder;
		var exportKind = selectedKind == ProjectCopyExportFormat.Zip
			? TerminalExportKind.Zip
			: TerminalExportKind.Folder;
		var defaultPath = BuildDefaultExportPath(
			_state.Plan.SourceRoot,
			Directory.GetCurrentDirectory(),
			selectedKind == ProjectCopyExportFormat.Zip
				? $"{GetProjectDisplayName(_state.Plan)}.zip"
				: $"{GetProjectDisplayName(_state.Plan)}-export");
		var destination = requestedDestination ?? Prompt(
			L("Terminal.Tui.ExportProject"),
			L("Terminal.Tui.ExactDestination"),
			_exportDestinations.Resolve(exportKind, defaultPath));
		if (string.IsNullOrWhiteSpace(destination))
		{
			ShowTransientStatus(L("Terminal.Tui.Export.DestinationRequired"));
			return;
		}
		_exportDestinations.Remember(exportKind, destination);
		var pendingSettingsRefresh = _operations.GetTask(WorkspaceOperationKind.SettingsRefresh);

		TrackActiveOperation(RunExportWorkflowAsync(
			L("Terminal.Tui.ExportProject"),
			async token =>
			{
				await AwaitPendingSettingsRefreshAsync(pendingSettingsRefresh, token).ConfigureAwait(false);
				return await _controller.PrepareProjectExportAsync(
					_state,
					selectedKind,
					destination,
					token).ConfigureAwait(false);
			},
			async (progress, overwrite, token) => await _controller.ExportProjectAsync(
				_state,
				selectedKind,
				destination,
				overwrite,
				token,
				progress).ConfigureAwait(false),
			(exactDestination, dryRun) => TerminalWorkspaceController.BuildEquivalentProjectCommand(
				_state,
				selectedKind,
					exactDestination,
					dryRun),
			originatedFromCommandLine));
	}

	private static async Task AwaitPendingSettingsRefreshAsync(
		Task? pendingSettingsRefresh,
		CancellationToken cancellationToken)
	{
		if (pendingSettingsRefresh is not null)
			await pendingSettingsRefresh.WaitAsync(cancellationToken).ConfigureAwait(false);
	}

	internal static string BuildDefaultExportPath(
		string sourceRoot,
		string currentDirectory,
		string fileName)
	{
		var normalizedSource = PathUtility.Normalize(sourceRoot);
		var normalizedCurrent = PathUtility.Normalize(currentDirectory);
		if (TryResolveSafeDefaultExportPath(
				normalizedSource,
				Path.Combine(normalizedCurrent, fileName),
				out var currentDirectoryCandidate))
		{
			return currentDirectoryCandidate;
		}

		var parent = Directory.GetParent(normalizedSource)?.FullName;
		return parent is not null &&
			   TryResolveSafeDefaultExportPath(
				   normalizedSource,
				   Path.Combine(parent, fileName),
				   out var siblingCandidate)
			? siblingCandidate
			: string.Empty;
	}

	private static bool TryResolveSafeDefaultExportPath(
		string sourceRoot,
		string candidate,
		out string resolvedCandidate)
	{
		try
		{
			var requestedCandidate = Path.GetFullPath(candidate);
			var physicalCandidate = ProjectCopyExportService.ResolveDestinationOutsideProject(
				sourceRoot,
				requestedCandidate);
			resolvedCandidate = ProjectCopyExportService.ResolveReportedDestinationPath(
				requestedCandidate,
				physicalCandidate);
			return true;
		}
		catch (ProjectCopyExportException)
		{
			resolvedCandidate = string.Empty;
			return false;
		}
	}

	private void SaveProfile(string? name = null, bool originatedFromCommandLine = false)
	{
		if (_state is null)
			return;
		var destination = string.IsNullOrWhiteSpace(name)
			? Prompt(
				L("Terminal.Tui.SaveProfile"),
				L("Terminal.Tui.ProfileDestination"),
				BuildDefaultExportPath(
					_state.Plan.SourceRoot,
					Directory.GetCurrentDirectory(),
					"devprojex-profile.json"))
			: BuildDefaultExportPath(
				_state.Plan.SourceRoot,
				Directory.GetCurrentDirectory(),
				name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
					? name
					: name + ".json");
		if (string.IsNullOrWhiteSpace(destination))
			return;
		TrackActiveOperation(RunOperationAsync(
			L("Terminal.Tui.SaveProfile"),
			async token => await _controller.SavePortableProfileAsync(
				_state,
				destination,
				overwrite: false,
				token).ConfigureAwait(false),
			originatedFromCommandLine: originatedFromCommandLine));
	}

	private async Task RunOperationAsync(
		string operationName,
		Func<CancellationToken, Task<string?>> operation,
		Func<string, string>? equivalentCommand = null,
		bool modalProgress = false,
		bool originatedFromCommandLine = false,
		string? cornerProgressLabel = null)
	{
		if (!await _operationGate.WaitAsync(0, _sessionCts.Token).ConfigureAwait(false))
		{
			_application.Invoke(() => ShowTransientStatus(string.Format(
				CultureInfo.CurrentCulture,
				L("Terminal.Tui.Operation.BusyHint"),
				_operationProgress?.OperationName ?? L("Terminal.Tui.Progress.Working"))));
			return;
		}
		var operationCts = ReplaceActiveOperation();
		var cornerProgressId = 0L;
		try
		{
			if (modalProgress)
			{
				SetWorkspaceBusy(
					operationName,
					L("Terminal.Tui.Progress.Working"));
			}
			else
			{
				cornerProgressId = BeginCornerProgress(
					cornerProgressLabel ?? L("Terminal.Tui.Progress.Refreshing"));
				await _operationObserver
					.ObservePhaseAsync(
						TerminalOperationPhase.BackgroundRefresh,
						operationCts.Token)
					.ConfigureAwait(false);
			}
			var result = await operation(operationCts.Token).ConfigureAwait(false);
			if (_stopping)
				return;
			await CompleteCornerProgressAsync(cornerProgressId).ConfigureAwait(false);
			cornerProgressId = 0;
			await InvokeAsync(() =>
			{
				MarkActiveOperationFinished(operationCts);
				RefreshWorkspace();
				if (modalProgress)
					SetWorkspaceBusy(null);
				if (!string.IsNullOrWhiteSpace(result))
				{
					var message = equivalentCommand is null
						? result
						: $"{result}\n\n{L("Terminal.Tui.EquivalentCommand")}:\n{equivalentCommand(result)}";
					if (originatedFromCommandLine)
						ShowCommandResult(message, success: true);
					else
						ShowNotice(operationName, message, TerminalWorkspaceTheme.Success);
				}
				SchedulePreviewRefresh();
				return true;
			}).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
		{
			await CompleteCornerProgressAsync(cornerProgressId).ConfigureAwait(false);
			cornerProgressId = 0;
			if (!_stopping)
			{
				await InvokeAsync(() =>
				{
					if (modalProgress)
						SetWorkspaceBusy(null);
					SetOperationStatus(L("Terminal.Tui.OperationCanceled"), TerminalWorkspaceTheme.Warning);
					return true;
				}).ConfigureAwait(false);
			}
		}
		catch (OutputDestinationConflictException)
		{
			await CompleteCornerProgressAsync(cornerProgressId).ConfigureAwait(false);
			cornerProgressId = 0;
			await ShowOperationFailureAsync(
				"DPX-EXPORT-DESTINATION-EXISTS",
				L("Terminal.Tui.Error.DestinationExists"),
				originatedFromCommandLine).ConfigureAwait(false);
		}
		catch (ProjectCopyExportException exception)
		{
			await CompleteCornerProgressAsync(cornerProgressId).ConfigureAwait(false);
			cornerProgressId = 0;
			var error = ProjectCopyTerminalErrorMapper.Map(exception, _services.Localization);
			await ShowOperationFailureAsync(
				error.Code,
				error.Message,
				originatedFromCommandLine).ConfigureAwait(false);
		}
		catch (ProjectContextValidationException exception)
		{
			await CompleteCornerProgressAsync(cornerProgressId).ConfigureAwait(false);
			cornerProgressId = 0;
			await ShowOperationFailureAsync(
				exception.Code,
				ResolveValidationErrorMessage(exception.Code),
				originatedFromCommandLine).ConfigureAwait(false);
		}
		catch (PortableProjectProfileException exception)
		{
			await CompleteCornerProgressAsync(cornerProgressId).ConfigureAwait(false);
			cornerProgressId = 0;
			var message = exception.Code == "DPX-PROFILE-DESTINATION-EXISTS"
				? L("Terminal.Error.ProfileDestinationExists")
				: L("Terminal.Error.ProfileWriteFailed");
			await ShowOperationFailureAsync(
				exception.Code,
				message,
				originatedFromCommandLine).ConfigureAwait(false);
		}
		catch (TerminalWorkspaceOperationException exception)
		{
			await CompleteCornerProgressAsync(cornerProgressId).ConfigureAwait(false);
			cornerProgressId = 0;
			var messageKey = exception.Code switch
			{
				"DPX-TUI-CLIPBOARD-PAYLOAD-TOO-LARGE" =>
					"Terminal.Tui.Command.Copy.Error.PayloadTooLarge",
				"DPX-TUI-CLIPBOARD-UNAVAILABLE" =>
					"Terminal.Tui.Command.Copy.Error.Unavailable",
				"DPX-TUI-GIT-BRANCH-NOT-FOUND" =>
					"Terminal.Tui.Command.Branch.Error.NotFound",
				_ => "Terminal.Tui.Error.OperationFailed"
			};
			await ShowOperationFailureAsync(
				exception.Code,
				L(messageKey),
				originatedFromCommandLine).ConfigureAwait(false);
		}
		catch
		{
			await CompleteCornerProgressAsync(cornerProgressId).ConfigureAwait(false);
			cornerProgressId = 0;
			await ShowOperationFailureAsync(
				"DPX-TUI-OPERATION-FAILED",
				L("Terminal.Tui.Error.OperationFailed"),
				originatedFromCommandLine).ConfigureAwait(false);
		}
		finally
		{
			await CompleteCornerProgressAsync(cornerProgressId).ConfigureAwait(false);
			ReleaseActiveOperation(operationCts);
			_operationGate.Release();
		}
	}

	private Task ShowOperationFailureAsync(
		string code,
		string message,
		bool originatedFromCommandLine) =>
		originatedFromCommandLine
			? ShowCommandFailureAsync(code, message)
			: ShowFailureAsync(code, message);

	private long BeginCornerProgress(string label) =>
		_screen == TerminalWorkspaceScreen.Workspace && _cornerProgress is not null
			? _cornerProgress.Begin(label)
			: 0;

	private void UpdateCornerProgress(long operationId, string label)
	{
		if (operationId != 0)
			_cornerProgress?.Update(operationId, label);
	}

	private async Task CompleteCornerProgressAsync(long operationId)
	{
		if (operationId == 0 || _stopping || _sessionCts.IsCancellationRequested)
			return;
		try
		{
			await InvokeAsync(() =>
			{
				var wasVisible = _cornerProgress?.IsVisible == true;
				_cornerProgress?.Complete(operationId);
				if (wasVisible)
				{
					_root.SetNeedsLayout();
					_root.SetNeedsDraw();
					_application.LayoutAndDraw();
				}
				return true;
			}).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (_sessionCts.IsCancellationRequested)
		{
			// Session shutdown owns disposal of the progress view and its timers.
		}
	}

	private async Task RunExportWorkflowAsync(
		string operationName,
		Func<CancellationToken, Task<TerminalExportSummary>> prepare,
		Func<IProgress<ProjectCopyExportProgress>, bool, CancellationToken, Task<string>> export,
		Func<string, bool, string> equivalentCommand,
		bool originatedFromCommandLine = false)
	{
		if (!await _operationGate.WaitAsync(0, _sessionCts.Token).ConfigureAwait(false))
		{
			_application.Invoke(() => ShowTransientStatus(string.Format(
				CultureInfo.CurrentCulture,
				L("Terminal.Tui.Operation.BusyHint"),
				_operationProgress?.OperationName ?? L("Terminal.Tui.Progress.Working"))));
			return;
		}
		var operationCts = ReplaceActiveOperation();
		try
		{
			SetWorkspaceBusy(
				operationName,
				L("Terminal.Tui.Progress.Preparing"));
			await _operationObserver
				.ObservePhaseAsync(
					TerminalOperationPhase.Preparing,
					operationCts.Token)
				.ConfigureAwait(false);
			var summary = await prepare(operationCts.Token).ConfigureAwait(false);
			var decision = await InvokeAsync(() =>
			{
				SetWorkspaceBusy(null);
				return ShowExportSummary(summary);
			}).ConfigureAwait(false);
			if (decision == TerminalExportDecision.Cancel)
			{
				await InvokeAsync(() =>
				{
					RefreshWorkspace();
					return true;
				}).ConfigureAwait(false);
				return;
			}

			if (decision == TerminalExportDecision.DryRun)
			{
				var command = equivalentCommand(summary.Destination, true);
				await InvokeAsync(() =>
				{
					RefreshWorkspace();
					if (originatedFromCommandLine)
					{
						ShowCommandResult(
							$"{L("Terminal.Tui.DryRunReady")} {command}",
							success: true);
					}
					else
					{
						ShowNotice(
							operationName,
							$"{L("Terminal.Tui.DryRunReady")}\n\n" +
							$"{L("Terminal.Tui.EquivalentCommand")}:\n{command}",
							TerminalWorkspaceTheme.Success);
					}
					return true;
				}).ConfigureAwait(false);
				return;
			}

			var progressPhase = summary.Kind switch
			{
				TerminalExportKind.Context => L("Terminal.Tui.Progress.WritingContext"),
				TerminalExportKind.Zip => L("Terminal.Tui.Progress.BuildingZip"),
				_ => L("Terminal.Tui.Progress.CopyingFiles")
			};
			SetWorkspaceBusy(operationName, progressPhase);
			if (summary.Kind == TerminalExportKind.Context)
			{
				await _operationObserver
					.ObservePhaseAsync(
						TerminalOperationPhase.WritingContext,
						operationCts.Token)
					.ConfigureAwait(false);
			}
			using var renderedProgress = new LatestValueProgressRelay<ProjectCopyExportProgress>(
				action => _application.Invoke(action),
				value => UpdateMeasuredProgress(
					value,
					summary.Kind,
					progressPhase));
			var progress = new SynchronousProgress<ProjectCopyExportProgress>(value =>
			{
				renderedProgress.Report(value);
				_operationObserver.ObserveProgress(value, operationCts.Token);
			});
			var result = await export(
				progress,
				decision == TerminalExportDecision.Overwrite,
				operationCts.Token).ConfigureAwait(false);
			if (_stopping)
				return;
			await renderedProgress.CompleteAsync().ConfigureAwait(false);
			await InvokeAsync(() =>
			{
				MarkActiveOperationFinished(operationCts);
				SetWorkspaceBusy(null);
				RefreshWorkspace();
				SchedulePreviewRefresh();
				var completed = string.Format(
					CultureInfo.CurrentCulture,
					L("Terminal.Tui.ExportCompletedStatus"),
					result);
				if (originatedFromCommandLine)
					ShowCommandResult(completed, success: true);
				else
					ShowTransientStatus(completed);
				return true;
			}).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
		{
			if (!_stopping)
			{
				await InvokeAsync(() =>
				{
					SetWorkspaceBusy(null);
					SetOperationStatus(L("Terminal.Tui.OperationCanceled"), TerminalWorkspaceTheme.Warning);
					return true;
				}).ConfigureAwait(false);
			}
		}
		catch (OutputDestinationConflictException)
		{
			await ShowExportFailureAsync(
				"DPX-EXPORT-DESTINATION-EXISTS",
				L("Terminal.Tui.Error.DestinationExists"),
				originatedFromCommandLine).ConfigureAwait(false);
		}
		catch (ProjectCopyExportException exception)
		{
			var error = ProjectCopyTerminalErrorMapper.Map(exception, _services.Localization);
			await ShowExportFailureAsync(
				error.Code,
				error.Message,
				originatedFromCommandLine).ConfigureAwait(false);
		}
		catch (ProjectContextValidationException exception)
		{
			await ShowExportFailureAsync(
				exception.Code,
				ResolveValidationErrorMessage(exception.Code),
				originatedFromCommandLine).ConfigureAwait(false);
		}
		catch
		{
			await ShowExportFailureAsync(
				"DPX-TUI-OPERATION-FAILED",
				L("Terminal.Tui.Error.OperationFailed"),
				originatedFromCommandLine).ConfigureAwait(false);
		}
		finally
		{
			ReleaseActiveOperation(operationCts);
			_operationGate.Release();
		}
	}

	private async Task ShowFailureAsync(string code, string message)
	{
		if (_stopping)
			return;
		await InvokeAsync(() =>
		{
			SetWorkspaceBusy(null);
			ShowError(code, message);
			RefreshWorkspace();
			return true;
		}).ConfigureAwait(false);
	}

	private string ResolveValidationErrorMessage(string code) =>
		code switch
		{
			"DPX-GIT-TRACKED-INDEX-UNAVAILABLE" =>
				L("Terminal.Diagnostic.TrackedIndexUnavailable"),
			GitScopeFilter.UnavailableDiagnosticCode =>
				L("Terminal.Diagnostic.GitStateUnavailable"),
			GitScopeFilter.DeletedDiagnosticCode =>
				L("Terminal.Diagnostic.GitStateDeleted"),
			"DPX-PROJECT-NOT-FOUND" or "DPX-PROJECT-PATH-INVALID" =>
				L("Terminal.Tui.Error.ProjectUnavailable"),
			_ => L("Terminal.Tui.Error.InvalidOperation")
		};

	private void SetWorkspaceBusy(string? operationName, string? phase = null)
	{
		_application.Invoke(() =>
		{
			var busy = !string.IsNullOrWhiteSpace(operationName);
			if (busy)
			{
				_activeControlSection = ResolveFocusedControlSection();
				_activeAggregateControlSection = IsAggregateControlFocused(_activeControlSection)
					? _activeControlSection
					: _activeAggregateControlSection;
				_focus.SaveBeforeBusy();
			}
			if (busy)
				CancelTransientStatus();
			var previousFocusSuppression = _suppressWorkspaceFocusTracking;
			_suppressWorkspaceFocusTracking = true;
			try
			{
				if (_tree is not null)
					_tree.Enabled = !busy;
				if (_preview is not null)
					_preview.Enabled = !busy;
				if (_contentControls is not null)
					_contentControls.Enabled = !busy;
				if (_contentAllControl is not null)
					_contentAllControl.Enabled = !busy;
				if (_exclusionAllControl is not null)
					_exclusionAllControl.Enabled = !busy;
				if (_exclusionControls is not null)
					_exclusionControls.Enabled = !busy;
				if (_extensionAllControl is not null)
					_extensionAllControl.Enabled = !busy;
				if (_extensionControls is not null)
					_extensionControls.Enabled = !busy;
				if (busy && _screen == TerminalWorkspaceScreen.Workspace)
				{
					DismissOperationProgress();
					_operationProgress = new TerminalOperationProgressView(
						_application,
						operationName!,
						phase ?? L("Terminal.Tui.Progress.Working"),
						L("Terminal.Tui.Progress.CancelHint"),
						FormatElapsed,
						useTextProgress: UseTextProgress,
						plain: _options.Plain);
					_operationProgress.ApplyLayout(_terminalWidth, _terminalHeight);
					_root.Add(_operationProgress.View);
				}
				else if (!busy)
				{
					DismissOperationProgress();
				}
				if (_status is not null &&
					(busy || !_operations.IsRunning(WorkspaceOperationKind.TransientStatus)))
				{
					_status.Text = busy
						? operationName!
						: _state is null
							? string.Empty
							: BuildStatus(_state, _application.Screen.Width);
					_status.SchemeName = busy
						? TerminalWorkspaceTheme.Accent
						: TerminalWorkspaceTheme.Secondary;
				}
				if (!busy && _screen == TerminalWorkspaceScreen.Workspace)
				{
					var focusToRestore = _focus.RestoreAfterBusy();
					RestoreScreenFocus();
					_application.Invoke(_ =>
					{
						if (_stopping || _screen != TerminalWorkspaceScreen.Workspace)
							return;
						_focus.Restore(focusToRestore);
						if (focusToRestore.Pane == TerminalWorkspacePane.Controls)
						{
							ApplyWorkspaceLayout();
							_focus.Restore(focusToRestore);
							ActiveControlView?.SetFocus();
							UpdateWorkspaceFocus();
						}
						else
						{
							RestoreScreenFocus();
						}
					});
				}
			}
			finally
			{
				_suppressWorkspaceFocusTracking = previousFocusSuppression;
			}
			if (!busy)
			{
				var restoreFocusSuppression = _suppressWorkspaceFocusTracking;
				_suppressWorkspaceFocusTracking = true;
				try
				{
					UpdateWorkspaceFocus();
				}
				finally
				{
					_suppressWorkspaceFocusTracking = restoreFocusSuppression;
				}
			}
		});
	}

	private void UpdateMeasuredProgress(
		ProjectCopyExportProgress progress,
		TerminalExportKind kind,
		string activePhase)
	{
		if (_operationProgress is null)
			return;

		var total = Math.Max(0, progress.TotalEntryCount);
		var processed = Math.Clamp(progress.ProcessedEntryCount, 0, total);
		var fraction = total == 0 ? 1d : processed / (double)total;
		var phase = total > 0 && processed >= total
			? L("Terminal.Tui.Progress.Finalizing")
			: activePhase;
		var operationKind = kind == TerminalExportKind.Zip
			? "ZIP"
			: L("Terminal.Tui.Folder");
		var metrics =
			$"{operationKind}  |  {processed:N0} / {total:N0}  |  " +
			$"{TerminalWorkspace.FormatBytes(progress.BytesWritten)}";
		_operationProgress.SetMeasured(phase, fraction, metrics);
	}

	private string FormatElapsed(TimeSpan elapsed) =>
		$"{L("Terminal.Tui.Progress.Elapsed")}: " +
		$"{(elapsed.TotalHours >= 1
			? elapsed.ToString(@"h\:mm\:ss", CultureInfo.CurrentCulture)
			: elapsed.ToString(@"m\:ss", CultureInfo.CurrentCulture))}";

	private bool UseTextProgress =>
		_options.Plain ||
		_options.ColorMode == TerminalColorMode.Never ||
		_options.ColorMode == TerminalColorMode.Auto && _environment.IsNoColor;

	private void ShowCancelingOperation()
	{
		_operationProgress?.SetIndeterminate(L("Terminal.Tui.CancelingOperation"));
		SetOperationStatus(L("Terminal.Tui.CancelingOperation"), TerminalWorkspaceTheme.Warning);
	}

	private void DismissOperationProgress()
	{
		if (_operationProgress is null)
			return;
		_root.Remove(_operationProgress.View);
		_operationProgress.Dispose();
		_operationProgress = null;
	}

	private void SetOperationStatus(string text, string schemeName)
	{
		var safeText = TerminalTextEscaping.EscapeSingleLine(text);
		if (_screen == TerminalWorkspaceScreen.Workspace && _status is not null)
		{
			_status.Text = safeText;
			_status.SchemeName = schemeName;
		}
		else if (_screen == TerminalWorkspaceScreen.Welcome)
		{
			ShowWelcomeStatus(safeText, schemeName);
		}
	}

	private void ShowWelcomeStatus(string text, string schemeName)
	{
		if (_welcomeQuickStart is null)
			return;
		_welcomeQuickStart.Text = string.IsNullOrWhiteSpace(text)
			? L("Terminal.Tui.Welcome.QuickStart")
			: TerminalTextEscaping.EscapeSingleLine(text);
		_welcomeQuickStart.SchemeName = schemeName;
	}

	private void ShowWelcomeStatusSafe(string text, string schemeName)
	{
		if (_stopping)
			return;
		_application.Invoke(() => ShowWelcomeStatus(text, schemeName));
	}

	private Task ShowExportFailureAsync(
		string code,
		string message,
		bool originatedFromCommandLine) =>
		originatedFromCommandLine
			? ShowCommandFailureAsync(code, message)
			: ShowFailureAsync(code, message);

	private void ScheduleSettingsRefresh()
	{
		if (_state is null || _settingsDraftSelection is null)
			return;

		var state = _state;
		var baseline = state.Plan;
		var selection = _settingsDraftSelection;
		var extensionStates = new Dictionary<string, bool>(
			_settingsDraftExtensionStates ?? state.ExtensionOptionStates,
			StringComparer.OrdinalIgnoreCase);
		var previousPaths = state.BuildSelectedItemRelativePaths();
		var pathStates = new Dictionary<string, bool>(
			state.PathOptionStates,
			PathComparer.Default);
		var originatedFromCommandLine = _settingsDraftOriginatedFromCommandLine;
		var requestId = Interlocked.Increment(ref _settingsRefreshRequestId);
		var operationCts = _operations.Start(WorkspaceOperationKind.SettingsRefresh);
		var cancellationToken = operationCts.Token;
		var cornerProgressId = BeginCornerProgress(L("Terminal.Tui.Progress.UpdatingOptions"));
		TrackOperation(WorkspaceOperationKind.SettingsRefresh, operationCts, Task.Run(
			() => RunSettingsRefreshAsync(
				state,
				baseline,
				selection,
				extensionStates,
				previousPaths,
				pathStates,
				originatedFromCommandLine,
				requestId,
				operationCts,
				cancellationToken,
				cornerProgressId),
			CancellationToken.None));
	}

	private async Task RunSettingsRefreshAsync(
		TerminalWorkspaceState state,
		ProjectContextPlan baseline,
		ProjectSelectionSpec selection,
		IReadOnlyDictionary<string, bool> extensionStates,
		IReadOnlySet<string> previousPaths,
		IReadOnlyDictionary<string, bool> pathStates,
		bool originatedFromCommandLine,
		long requestId,
		CancellationTokenSource operationCts,
		CancellationToken cancellationToken,
		long cornerProgressId)
	{
		try
		{
			await Task.Delay(
				SettingsRefreshDebounceMilliseconds,
				cancellationToken).ConfigureAwait(false);
			if (TerminalWorkspaceController.RequiresStructuralRefresh(
				    baseline,
				    selection,
				    extensionStates))
			{
				await InvokeAsync(() =>
				{
					UpdateCornerProgress(
						cornerProgressId,
						L("Terminal.Tui.Progress.BuildingTree"));
					return true;
				}).ConfigureAwait(false);
			}
			await _operationObserver
				.ObservePhaseAsync(
					TerminalOperationPhase.BackgroundRefresh,
					cancellationToken)
				.ConfigureAwait(false);

			var result = await _controller.BuildSettingsPlanAsync(
					baseline,
					selection,
					extensionStates,
					previousPaths,
					pathStates,
					cancellationToken)
				.ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			await InvokeAsync(() =>
			{
				if (!IsCurrentSettingsRefresh(state, operationCts, requestId))
					return false;

				_controller.ApplySettingsPlan(state, result);
				ClearSettingsDraft();
				RefreshWorkspace();
				if (originatedFromCommandLine)
					RefreshAppliedCommandResult();
				SchedulePreviewRefresh();
				return true;
			}).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			if (IsCurrentSettingsRefresh(state, operationCts, requestId) && !_stopping)
			{
				await InvokeAsync(() =>
				{
					ClearSettingsDraft();
					RefreshWorkspace();
					return true;
				}).ConfigureAwait(false);
			}
		}
		catch (ProjectContextValidationException exception)
		{
			if (IsCurrentSettingsRefresh(state, operationCts, requestId))
			{
				await CompleteCornerProgressAsync(cornerProgressId).ConfigureAwait(false);
				cornerProgressId = 0;
				await RollbackFailedSettingsRefreshAsync().ConfigureAwait(false);
				await ShowSettingsFailureAsync(
					exception.Code,
					ResolveValidationErrorMessage(exception.Code),
					originatedFromCommandLine).ConfigureAwait(false);
			}
		}
		catch
		{
			if (IsCurrentSettingsRefresh(state, operationCts, requestId))
			{
				await CompleteCornerProgressAsync(cornerProgressId).ConfigureAwait(false);
				cornerProgressId = 0;
				await RollbackFailedSettingsRefreshAsync().ConfigureAwait(false);
				await ShowSettingsFailureAsync(
					"DPX-TUI-OPERATION-FAILED",
					L("Terminal.Tui.Error.OperationFailed"),
					originatedFromCommandLine).ConfigureAwait(false);
			}
		}
		finally
		{
			await CompleteCornerProgressAsync(cornerProgressId).ConfigureAwait(false);
			_operations.Complete(WorkspaceOperationKind.SettingsRefresh, operationCts);
		}
	}

	private Task ShowSettingsFailureAsync(
		string code,
		string message,
		bool originatedFromCommandLine) =>
		originatedFromCommandLine
			? ShowCommandFailureAsync(code, message)
			: ShowFailureAsync(code, message);

	private async Task RollbackFailedSettingsRefreshAsync()
	{
		await InvokeAsync(() =>
		{
			ClearSettingsDraft();
			RefreshWorkspace();
			return true;
		}).ConfigureAwait(false);
	}

	private bool IsCurrentSettingsRefresh(
		TerminalWorkspaceState state,
		CancellationTokenSource operationCts,
		long requestId) =>
		!_stopping &&
		ReferenceEquals(_state, state) &&
		_operations.IsCurrent(WorkspaceOperationKind.SettingsRefresh, operationCts) &&
		Volatile.Read(ref _settingsRefreshRequestId) == requestId;

	private void ScheduleSelectionProjection()
	{
		if (_state is null)
			return;
		var state = _state;
		var sourcePlan = state.Plan;
		var selectedPaths = state.BuildSelectedRelativePaths();
		var forceEmptySelection = state.IsEffectiveRootUnchecked;
		var expectedRevision = state.Revision;
		_operations.Cancel(WorkspaceOperationKind.Preview);
		var projectionRequestId = Interlocked.Increment(ref _projectionRequestId);
		Interlocked.Increment(ref _previewRequestId);
		var operationCts = _operations.Start(WorkspaceOperationKind.Projection);
		var cancellationToken = operationCts.Token;
		TrackOperation(WorkspaceOperationKind.Projection, operationCts, Task.Run(async () =>
		{
			var cornerProgressId = 0L;
			try
			{
				await Task.Delay(180, cancellationToken).ConfigureAwait(false);
				cornerProgressId = await InvokeAsync(() =>
					BeginCornerProgress(L("Terminal.Tui.Progress.BuildingTree"))).ConfigureAwait(false);
				var plan = await _controller.BuildReprojectedPlanAsync(
						sourcePlan,
						selectedPaths,
						forceEmptySelection,
						cancellationToken)
					.ConfigureAwait(false);
				var applied = await InvokeAsync(() =>
				{
					var selectedTreePath = CaptureCurrentTreePath();
					var treeVerticalOffset = _tree?.VerticalOffset ?? 0;
					if (!IsCurrentProjectionRequest(
							state,
							operationCts,
							projectionRequestId))
					{
						return false;
					}

					_suppressTreeSelectionTracking = true;
					try
					{
						if (!state.TryReplacePlan(plan, expectedRevision))
							return false;
						_selectedTreePath = selectedTreePath;
						RefreshWorkspace();
						_tree?.RestoreVerticalOffset(
							treeVerticalOffset,
							state.VisibleRows.Count);
					}
					finally
					{
						_suppressTreeSelectionTracking = false;
					}

					// Reprojection replaces the preview with a temporary tree document. Re-enter
					// the shared scheduler afterwards so it captures the latest plan revision,
					// view and format instead of publishing a document built from stale state.
					SchedulePreviewRefresh();
					return true;
				}).ConfigureAwait(false);
				if (!applied)
					return;
			}
			catch (OperationCanceledException)
			{
				// The newest selection owns the next projection.
			}
			catch
			{
				if (!_stopping &&
					ReferenceEquals(_state, state) &&
					_operations.IsCurrent(WorkspaceOperationKind.Projection, operationCts))
				{
					await CompleteCornerProgressAsync(cornerProgressId).ConfigureAwait(false);
					cornerProgressId = 0;
					_application.Invoke(() => ShowError(
						"DPX-TUI-PREVIEW-FAILED",
						L("Terminal.Tui.Error.PreviewFailed")));
				}
			}
			finally
			{
				await CompleteCornerProgressAsync(cornerProgressId).ConfigureAwait(false);
				_operations.Complete(WorkspaceOperationKind.Projection, operationCts);
			}
		}, CancellationToken.None));
	}

	private void SchedulePreviewRefresh()
	{
		if (_state is null)
			return;
		var state = _state;
		var view = _previewView;
		var format = _format;
		var expectedRevision = state.Revision;
		CancelPreviewSearch(clearQuery: false);
		var requestId = Interlocked.Increment(ref _previewRequestId);
		var operationCts = _operations.Start(WorkspaceOperationKind.Preview);
		var cancellationToken = operationCts.Token;
		var cornerProgressId = BeginCornerProgress(L("Terminal.Tui.Progress.BuildingPreview"));
		TrackOperation(WorkspaceOperationKind.Preview, operationCts, Task.Run(async () =>
		{
			IPreviewTextDocument? pendingDocument = null;
			try
			{
				var build = await _controller.BuildPreviewDocumentWithMetricsAsync(
						state,
						view,
						format,
						cancellationToken,
						plain: _options.Plain)
					.ConfigureAwait(false);
				pendingDocument = build.Document;
				var outputMetrics = build.Metrics;
				var document = pendingDocument;
				var applied = await InvokeAsync(() =>
				{
					if (!IsCurrentPreviewRequest(state, operationCts, requestId) ||
						!state.TrySetPreviewDocument(document, outputMetrics, expectedRevision))
					{
						return false;
					}

					RefreshWorkspace();
					RefreshAppliedCommandResult();
					return true;
				}).ConfigureAwait(false);
				if (applied)
					pendingDocument = null;
			}
			catch (OperationCanceledException)
			{
				// A newer view, format, or selection owns the preview surface.
			}
			catch
			{
				if (!_stopping &&
					ReferenceEquals(_state, state) &&
					_operations.IsCurrent(WorkspaceOperationKind.Preview, operationCts))
				{
					await CompleteCornerProgressAsync(cornerProgressId).ConfigureAwait(false);
					cornerProgressId = 0;
					_application.Invoke(() => ShowError(
						"DPX-TUI-PREVIEW-FAILED",
						L("Terminal.Tui.Error.PreviewFailed")));
				}
			}
			finally
			{
				pendingDocument?.Dispose();
				await CompleteCornerProgressAsync(cornerProgressId).ConfigureAwait(false);
				_operations.Complete(WorkspaceOperationKind.Preview, operationCts);
			}
		}, CancellationToken.None));
	}

	private void CancelWorkspaceRefreshes()
	{
		Interlocked.Increment(ref _settingsRefreshRequestId);
		Interlocked.Increment(ref _projectionRequestId);
		Interlocked.Increment(ref _previewRequestId);
		_operations.Cancel(WorkspaceOperationKind.SettingsRefresh);
		_operations.Cancel(WorkspaceOperationKind.Projection);
		_operations.Cancel(WorkspaceOperationKind.Preview);
		CancelPreviewSearch(clearQuery: false);
		ClearSettingsDraft();
	}

	private bool IsCurrentProjectionRequest(
		TerminalWorkspaceState state,
		CancellationTokenSource operationCts,
		long requestId) =>
		!_stopping &&
		ReferenceEquals(_state, state) &&
		_operations.IsCurrent(WorkspaceOperationKind.Projection, operationCts) &&
		Volatile.Read(ref _projectionRequestId) == requestId;

	private bool IsCurrentPreviewRequest(
		TerminalWorkspaceState state,
		CancellationTokenSource operationCts,
		long requestId) =>
		!_stopping &&
		ReferenceEquals(_state, state) &&
		_operations.IsCurrent(WorkspaceOperationKind.Preview, operationCts) &&
		Volatile.Read(ref _previewRequestId) == requestId;

	private void MovePane(TerminalPaneNavigation navigation)
	{
		if (_tree is null || _preview is null || ActiveControlList is null)
			return;
		var panes = new[]
		{
			TerminalWorkspacePane.Tree,
			TerminalWorkspacePane.Preview,
			TerminalWorkspacePane.Controls
		};
		var currentIndex = Array.IndexOf(panes, _activePane);
		var offset = navigation == TerminalPaneNavigation.Next ? 1 : -1;
		var nextIndex = (currentIndex + offset + panes.Length) % panes.Length;
		FocusPane(panes[nextIndex]);
	}

	private void ShowTransientStatus(string text)
	{
		CancelTransientStatus();
		var statusCts = _operations.Start(WorkspaceOperationKind.TransientStatus);
		SetOperationStatus(text, TerminalWorkspaceTheme.Success);
		TrackOperation(
			WorkspaceOperationKind.TransientStatus,
			statusCts,
			RestoreStatusAfterDelayAsync(statusCts));
	}

	private async Task RestoreStatusAfterDelayAsync(CancellationTokenSource statusCts)
	{
		try
		{
			await Task.Delay(TimeSpan.FromSeconds(3), statusCts.Token).ConfigureAwait(false);
			if (_stopping)
				return;
			await InvokeAsync(() =>
			{
				if (!_operations.IsCurrent(WorkspaceOperationKind.TransientStatus, statusCts))
					return false;
				if (_screen == TerminalWorkspaceScreen.Workspace && _status is not null && _state is not null)
				{
					_status.Text = BuildStatus(_state, _application.Screen.Width);
					_status.SchemeName = TerminalWorkspaceTheme.Secondary;
				}
				return true;
			}).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (statusCts.IsCancellationRequested)
		{
		}
		finally
		{
			_operations.Complete(WorkspaceOperationKind.TransientStatus, statusCts);
		}
	}

	private void CancelTransientStatus()
		=> _operations.Cancel(WorkspaceOperationKind.TransientStatus);

	private void MoveControlSelection(int delta)
	{
		if (delta == 0)
			return;
		var sections = Enum.GetValues<TerminalControlSection>();
		var currentIndex = Array.IndexOf(sections, _activeControlSection);
		var (list, rows) = GetControlSection(_activeControlSection);
		var aggregate = GetAggregateControlSection(_activeControlSection).List;
		if (list is null || rows is null)
			return;
		var aggregateOffset = aggregate is null ? 0 : 1;
		var logicalCount = rows.Count + aggregateOffset;
		if (logicalCount == 0)
			return;
		var selected = IsAggregateControlFocused(_activeControlSection) ||
			_activeAggregateControlSection == _activeControlSection
			? 0
			: Math.Clamp(list.SelectedItem ?? 0, 0, Math.Max(0, rows.Count - 1)) + aggregateOffset;
		var direction = Math.Sign(delta);
		var next = FindEnabledControlPosition(
			_activeControlSection,
			selected + direction,
			direction);
		if (next >= 0)
		{
			FocusControlPosition(_activeControlSection, next);
			return;
		}

		var targetIndex = currentIndex + direction;
		if (targetIndex < 0 || targetIndex >= sections.Length)
			return;
		var targetSection = sections[targetIndex];
		var (_, targetRows) = GetControlSection(targetSection);
		var targetAggregate = GetAggregateControlSection(targetSection).List;
		var targetCount = (targetRows?.Count ?? 0) + (targetAggregate is null ? 0 : 1);
		if (targetRows is null || targetCount == 0)
			return;
		var targetPosition = FindEnabledControlPosition(
			targetSection,
			direction > 0 ? 0 : targetCount - 1,
			direction);
		if (targetPosition >= 0)
			FocusControlPosition(targetSection, targetPosition);
	}

	private void FocusControlBoundary(TerminalControlSection section, bool first)
	{
		var (_, rows) = GetControlSection(section);
		var aggregate = GetAggregateControlSection(section).List;
		var count = (rows?.Count ?? 0) + (aggregate is null ? 0 : 1);
		if (count > 0)
		{
			var direction = first ? 1 : -1;
			var position = FindEnabledControlPosition(
				section,
				first ? 0 : count - 1,
				direction);
			if (position >= 0)
				FocusControlPosition(section, position);
		}
	}

	private int FindEnabledControlPosition(
		TerminalControlSection section,
		int start,
		int direction)
	{
		var (_, rows) = GetControlSection(section);
		var aggregateOffset = GetAggregateControlSection(section).List is null ? 0 : 1;
		var count = (rows?.Count ?? 0) + aggregateOffset;
		for (var position = start; position >= 0 && position < count; position += direction)
		{
			if (position < aggregateOffset || rows![position - aggregateOffset].IsEnabled)
				return position;
		}
		return -1;
	}

	private void FocusControlPosition(TerminalControlSection section, int logicalIndex)
	{
		var (list, rows) = GetControlSection(section);
		var aggregate = GetAggregateControlSection(section).List;
		if (list is null || rows is null)
			return;

		_activePane = TerminalWorkspacePane.Controls;
		_activeControlSection = section;
		if (aggregate is not null && logicalIndex == 0)
		{
			_activeAggregateControlSection = section;
			aggregate.SetFocus();
		}
		else
		{
			var rowIndex = logicalIndex - (aggregate is null ? 0 : 1);
			if (rowIndex < 0 || rowIndex >= rows.Count || !rows[rowIndex].IsEnabled)
				return;
			_activeAggregateControlSection = null;
			list.SelectedItem = rowIndex;
			list.EnsureSelectedItemVisible();
			list.SetFocus();
			TrackSelectedControl(section);
		}
		UpdateWorkspaceFocus();
	}

	private bool TryHandleTreeNavigation(Key key)
	{
		if (_tree is null || _state is null)
			return false;

		if (key == Key.CursorUp || key == Key.CursorDown)
		{
			key.Handled = true;
			MoveListSelection(_tree, key == Key.CursorDown ? 1 : -1);
			return true;
		}
		if (key == Key.PageUp || key == Key.PageDown)
		{
			key.Handled = true;
			if (key == Key.PageDown)
				_tree.MovePageDown(false);
			else
				_tree.MovePageUp(false);
			TrackTreeSelection();
			return true;
		}
		if (key == Key.Home || key == Key.Home.WithCtrl)
		{
			key.Handled = true;
			_tree.SelectedItem = 0;
			TrackTreeSelection();
			return true;
		}
		if (key == Key.End || key == Key.End.WithCtrl)
		{
			key.Handled = true;
			_tree.SelectedItem = Math.Max(0, _state.VisibleRows.Count - 1);
			TrackTreeSelection();
			return true;
		}

		return false;
	}

	private bool TryHandlePreviewNavigation(Key key)
	{
		var scroll = key switch
		{
			_ when key == Key.CursorUp => TerminalPreviewScroll.LineUp,
			_ when key == Key.CursorDown => TerminalPreviewScroll.LineDown,
			_ when key == Key.PageUp => TerminalPreviewScroll.PageUp,
			_ when key == Key.PageDown => TerminalPreviewScroll.PageDown,
			_ when key == Key.Home || key == Key.Home.WithCtrl => TerminalPreviewScroll.Start,
			_ when key == Key.End || key == Key.End.WithCtrl => TerminalPreviewScroll.End,
			_ when key == Key.CursorLeft => TerminalPreviewScroll.ColumnLeft,
			_ when key == Key.CursorRight => TerminalPreviewScroll.ColumnRight,
			_ => (TerminalPreviewScroll?)null
		};
		if (scroll is null)
			return false;

		key.Handled = true;
		ScrollPreview(scroll.Value);
		return true;
	}

	private void ScrollPreview(TerminalPreviewScroll scroll)
	{
		if (_preview is null)
			return;
		var lastRow = Math.Max(0, _preview.ContentRowCount - 1);
		var pageSize = Math.Max(1, _preview.PageSize - 1);
		var row = Math.Clamp(_preview.FirstVisibleContentRow, 0, lastRow);
		var column = Math.Max(0, _preview.HorizontalOffset);
		switch (scroll)
		{
			case TerminalPreviewScroll.LineUp:
				row--;
				break;
			case TerminalPreviewScroll.LineDown:
				row++;
				break;
			case TerminalPreviewScroll.PageUp:
				row -= pageSize;
				break;
			case TerminalPreviewScroll.PageDown:
				row += pageSize;
				break;
			case TerminalPreviewScroll.Start:
				row = 0;
				column = 0;
				break;
			case TerminalPreviewScroll.End:
				row = lastRow;
				column = 0;
				break;
			case TerminalPreviewScroll.ColumnLeft:
				column -= 4;
				break;
			case TerminalPreviewScroll.ColumnRight:
				column += 4;
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(scroll), scroll, null);
		}

		_preview.ScrollToContentRow(row, column);
		UpdatePreviewRange();
	}

	private void RestorePreviewViewport(int row, int column)
	{
		if (_preview is null)
			return;
		var clampedRow = Math.Clamp(row, 0, Math.Max(0, _preview.LineCount - 1));
		var clampedColumn = Math.Max(0, column);
		_preview.ScrollTo(clampedRow, clampedColumn);
	}

	private string? Prompt(string title, string label, string? initialValue)
	{
		using var dialog = CreateDialog(title, 74, 9);
		var prompt = new TextView
		{
			X = 1,
			Y = 0,
			Width = Dim.Fill(1),
			Height = 2,
			ReadOnly = true,
			WordWrap = true,
			CanFocus = false,
			Text = label,
			SchemeName = TerminalWorkspaceTheme.Base
		};
		var input = new TextField
		{
			X = 1,
			Y = 2,
			Width = Dim.Fill(1),
			Text = initialValue ?? string.Empty,
			SchemeName = TerminalWorkspaceTheme.List
		};
		dialog.Add(prompt, input);
		dialog.AddButton(CreateDialogButton(L("Terminal.Tui.Cancel")));
		dialog.AddButton(CreateDialogButton(L("Terminal.Tui.Accept")));
		RunOverlay(dialog, input);
		return TerminalWorkspace.CompletePrompt(dialog.Result == 1, input.Text);
	}

	private string? SelectFromList(
		string title,
		string description,
		IReadOnlyList<string> values,
		int preferredWidth = 78)
	{
		if (values.Count == 0)
			return null;
		var height = Math.Clamp(values.Count + 9, 12, Math.Max(12, _application.Screen.Height - 4));
		using var dialog = CreateDialog(title, preferredWidth, height);
		var label = new TextView
		{
			X = 1,
			Y = 0,
			Width = Dim.Fill(1),
			Height = 2,
			ReadOnly = true,
			WordWrap = true,
			CanFocus = false,
			Text = description,
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		var source = new ObservableCollection<string>(values);
		var list = new ListView
		{
			X = 1,
			Y = 2,
			Width = Dim.Fill(1),
			// Dialog button layout can collapse Dim.Fill() to one row in Terminal.Gui v2.
			Height = values.Count,
			SchemeName = TerminalWorkspaceTheme.List
		};
		list.SetSource(source);
		list.SelectedItem = 0;
		string? selected = null;
		void AcceptSelection()
		{
			if (list.SelectedItem is { } index && index >= 0 && index < source.Count)
				selected = source[index];
			_application.RequestStop(dialog);
		}

		list.Accepted += (_, _) => AcceptSelection();
		list.KeyDown += (_, key) =>
		{
			if (key != Key.Enter)
				return;
			key.Handled = true;
			AcceptSelection();
		};
		dialog.Add(label, list);
		dialog.AddButton(CreateDialogButton(L("Terminal.Tui.Back")));
		RunOverlay(dialog, list);
		return selected;
	}

	private string? SelectPath(
		string title,
		TerminalPathPickerMode mode,
		string? initialPath)
	{
		var model = new TerminalPathPickerModel(mode, initialPath);
		var dialogWidth = ResolveDialogWidth(104);
		var dialogHeight = Math.Clamp(
			_terminalHeight - 4,
			16,
			Math.Max(16, _terminalHeight - 2));
		using var dialog = CreateDialog(title, dialogWidth, dialogHeight);
		var locationTitle = new TerminalLiteralLabel
		{
			X = 1,
			Y = 0,
			Text = L("Terminal.Tui.Picker.CurrentFolder"),
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		var location = new TerminalLiteralLabel
		{
			X = 1,
			Y = 1,
			Width = Dim.Fill(1),
			SchemeName = TerminalWorkspaceTheme.Base
		};
		var pathTitle = new TerminalLiteralLabel
		{
			X = 1,
			Y = 3,
			Text = L("Terminal.Tui.Picker.Path"),
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		var pathInput = new TextField
		{
			X = 1,
			Y = 4,
			Width = Dim.Fill(1),
			Text = initialPath ?? string.Empty,
			SchemeName = TerminalWorkspaceTheme.List
		};
		var rows = new ObservableCollection<TerminalPathPickerEntry>();
		var list = new ListView
		{
			X = 1,
			Y = 6,
			Width = Dim.Fill(1),
			Height = Dim.Fill(3),
			SchemeName = TerminalWorkspaceTheme.List
		};
		list.SetSource(rows);
		var status = new TerminalLiteralLabel
		{
			X = 1,
			Y = Pos.AnchorEnd(2),
			Width = Dim.Fill(1),
			Height = 1,
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		string? selectedPath = null;

		void Refresh()
		{
			location.Text = FitPathToWidth(model.CurrentDirectory, Math.Max(8, dialogWidth - 6));
			rows.Clear();
			foreach (var entry in model.Entries)
				rows.Add(entry);
			if (rows.Count > 0)
				list.SelectedItem = 0;
			status.Text = model.Error switch
			{
				TerminalPathPickerError.AccessDenied => L("Terminal.Tui.Picker.AccessDenied"),
				TerminalPathPickerError.Unavailable => L("Terminal.Tui.Picker.Unavailable"),
				_ when model.IsTruncated => L("Terminal.Tui.Picker.Limit"),
				_ when rows.Count == 0 => L("Terminal.Tui.Picker.Empty"),
				_ => mode == TerminalPathPickerMode.JsonFile
					? L("Terminal.Tui.Picker.JsonOnly")
					: L("Terminal.Tui.Picker.DirectoryHint")
			};
			status.SchemeName = model.Error == TerminalPathPickerError.None
				? TerminalWorkspaceTheme.Secondary
				: TerminalWorkspaceTheme.Warning;
			list.SetNeedsDraw();
		}

		void ActivateCurrent()
		{
			var index = list.SelectedItem ?? -1;
			if (!model.TryOpenEntry(index, out var file))
				return;
			if (file is not null)
			{
				selectedPath = file;
				_application.RequestStop(dialog);
				return;
			}

			Refresh();
			pathInput.Text = model.CurrentDirectory;
			list.SetFocus();
		}

		void OpenSelection()
		{
			var typed = pathInput.Text?.ToString() ?? string.Empty;
			var selection = model.ResolveSelection(typed, list.SelectedItem ?? -1);
			if (selection.InvalidTypedPath)
			{
				status.Text = L("Terminal.Tui.Picker.PathNotFound");
				status.SchemeName = TerminalWorkspaceTheme.Warning;
				pathInput.SetFocus();
				return;
			}
			if (selection.Path is { } resolvedPath)
			{
				selectedPath = resolvedPath;
				_application.RequestStop(dialog);
				return;
			}

			ActivateCurrent();
		}

		list.Accepted += (_, _) => ActivateCurrent();
		pathInput.KeyDown += (_, key) =>
		{
			if (key == Key.Tab)
			{
				key.Handled = true;
				pathInput.Text = model.CompleteInputPath(pathInput.Text?.ToString() ?? string.Empty);
			}
			else if (key == Key.Enter)
			{
				key.Handled = true;
				OpenSelection();
			}
		};
		list.KeyDown += (_, key) =>
		{
			if (key == Key.Esc)
			{
				key.Handled = true;
				_application.RequestStop(dialog);
				return;
			}
			if (key != Key.Backspace)
				return;
			key.Handled = true;
			var parent = model.Entries.FirstOrDefault(static entry => entry.IsParent);
			if (parent is null)
				return;
			model.Open(parent.Path);
			Refresh();
			pathInput.Text = model.CurrentDirectory;
		};
		dialog.Add(locationTitle, location, pathTitle, pathInput, list, status);
		var back = CreateDialogButton(L("Terminal.Tui.Back"));
		back.Accepted += (_, _) =>
		{
			var parent = model.Entries.FirstOrDefault(static entry => entry.IsParent);
			if (parent is null)
				return;
			model.Open(parent.Path);
			Refresh();
			pathInput.Text = model.CurrentDirectory;
			list.SetFocus();
		};
		dialog.AddButton(back);
		dialog.AddButton(CreateDialogButton(L("Terminal.Tui.Cancel")));
		var open = CreateDialogButton(L("Terminal.Tui.Open"));
		open.Accepted += (_, _) => OpenSelection();
		dialog.AddButton(open);
		dialog.KeyDown += (_, key) =>
		{
			if (key != Key.Esc)
				return;
			key.Handled = true;
			_application.RequestStop(dialog);
		};
		Refresh();
		RunOverlay(dialog, list);
		return selectedPath;
	}

	private TerminalExportDecision ShowExportSummary(TerminalExportSummary summary)
	{
		var title = L("Terminal.Tui.ExportConfirmTitle");
		var dialogWidth = ResolveDialogWidth(86);
		var text = _workspace.BuildExportSummaryText(summary, Math.Max(12, dialogWidth - 26));
		if (_options.Plain)
			text = $"{title}\n\n{text}";
		var dialogHeight = text.Count(static character => character == '\n') + 8;
		using var dialog = CreateDialog(title, dialogWidth, dialogHeight);
		var body = new TextView
		{
			X = 1,
			Y = 0,
			Width = Dim.Fill(1),
			Height = Dim.Fill(1),
			ReadOnly = true,
			WordWrap = false,
			ScrollBars = false,
			Text = text,
			SchemeName = TerminalWorkspaceTheme.Base
		};
		dialog.Add(body);
		dialog.AddButton(CreateDialogButton(L("Terminal.Tui.Cancel")));
		dialog.AddButton(CreateDialogButton(L("Terminal.Tui.DryRun")));
		var export = CreateDialogButton(summary.DestinationState == TerminalExportDestinationState.Conflict
			? L("Terminal.Tui.Overwrite")
			: L("Terminal.Tui.Export"));
		export.IsDefault = true;
		dialog.AddButton(export);
		RunOverlay(dialog, export);
		return dialog.Result switch
		{
			1 => TerminalExportDecision.DryRun,
			2 => summary.DestinationState == TerminalExportDestinationState.Conflict
				? TerminalExportDecision.Overwrite
				: TerminalExportDecision.Export,
			_ => TerminalExportDecision.Cancel
		};
	}

	private int? ShowChoice(string title, string message, params string[] choices)
	{
		if (_options.Plain)
			message = $"{title}\n\n{message}";
		var dialogWidth = ResolveDialogWidth(82);
		var lines = EstimateWrappedLineCount(message, Math.Max(20, dialogWidth - 6));
		using var dialog = CreateDialog(title, 82, Math.Clamp(lines + 7, 10, 22));
		var body = new TextView
		{
			X = 1,
			Y = 0,
			Width = Dim.Fill(1),
			Height = Dim.Fill(1),
			ReadOnly = true,
			WordWrap = true,
			ScrollBars = !_options.Plain,
			Text = message,
			SchemeName = TerminalWorkspaceTheme.Base
		};
		dialog.Add(body);
		foreach (var choice in choices)
			dialog.AddButton(CreateDialogButton(choice));
		body.SetFocus();
		RunOverlay(dialog);
		return dialog.Result;
	}

	private bool Confirm(string title, string message) =>
		ShowChoice(
			title,
			message,
			L("Terminal.Tui.Cancel"),
			L("Terminal.Tui.Accept")) == 1;

	private void ShowHelp(bool welcome)
	{
		if (_operationProgress is not null)
			return;

		var contextualBody = _activePane switch
		{
			TerminalWorkspacePane.Preview => L("Terminal.Tui.Help.Preview"),
			TerminalWorkspacePane.Controls => L("Terminal.Tui.Help.Controls"),
			_ => L("Terminal.Tui.Help.Tree")
		};
		var body = welcome
			? L("Terminal.Tui.Welcome.HelpBody")
			: $"{contextualBody}\n\n{L("Terminal.Tui.HelpBody")}";
		ShowScrollableOverlay(
			L("Terminal.Tui.Help"),
			body,
			TerminalWorkspaceTheme.Dialog,
			preferredWidth: 92,
			preferredHeight: 25);
	}

	private void ShowNotice(
		string title,
		string message,
		string schemeName,
		int preferredWidth = 82,
		int preferredHeight = 18) =>
		ShowScrollableOverlay(title, message, schemeName, preferredWidth, preferredHeight);

	private void ShowError(string code, string message)
	{
		var cornerWasVisible = _cornerProgress?.IsVisible == true;
		_cornerProgress?.Clear();
		if (cornerWasVisible)
		{
			_root.SetNeedsLayout();
			_root.SetNeedsDraw();
			_application.LayoutAndDraw();
		}
		var title = L("Terminal.Tui.Error");
		var displayMessage = _options.Plain ? $"{title}\n\n{message}" : message;
		var contentWidth = displayMessage
			.Split('\n')
			.Append(code)
			.Max(static line => line.GetColumns());
		var preferredWidth = Math.Clamp(contentWidth + 6, 36, 82);
		var dialogWidth = ResolveDialogWidth(preferredWidth);
		var lines = EstimateWrappedLineCount(displayMessage, Math.Max(20, dialogWidth - 6));
		using var dialog = CreateDialog(title, dialogWidth, Math.Clamp(lines + 8, 9, 18));
		var body = new TextView
		{
			X = 1,
			Y = 0,
			Width = Dim.Fill(1),
			Height = Dim.Fill(2),
			ReadOnly = true,
			WordWrap = true,
			ScrollBars = false,
			Text = displayMessage,
			SchemeName = TerminalWorkspaceTheme.Base
		};
		var codeLabel = new TerminalLiteralLabel
		{
			X = 1,
			Y = Pos.AnchorEnd(1),
			Width = Dim.Fill(1),
			Height = 1,
			Text = code,
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		dialog.Add(body, codeLabel);
		var close = CreateDialogButton(L("Terminal.Tui.Close"));
		dialog.AddButton(close);
		RunOverlay(dialog, close);
	}

	private void ShowScrollableOverlay(
		string title,
		string message,
		string schemeName,
		int preferredWidth,
		int preferredHeight)
	{
		if (_options.Plain)
			message = $"{title}\n\n{message}";
		var dialogWidth = ResolveDialogWidth(preferredWidth);
		var lines = EstimateWrappedLineCount(message, Math.Max(20, dialogWidth - 6));
		using var dialog = CreateDialog(
			title,
			preferredWidth,
			Math.Min(preferredHeight, Math.Max(9, lines + 7)));
		dialog.SchemeName = schemeName;
		var body = new TextView
		{
			X = 1,
			Y = 0,
			Width = Dim.Fill(1),
			Height = Dim.Fill(1),
			ReadOnly = true,
			WordWrap = true,
			ScrollBars = !_options.Plain,
			Text = message,
			SchemeName = TerminalWorkspaceTheme.Base
		};
		dialog.Add(body);
		dialog.AddButton(CreateDialogButton(L("Terminal.Tui.Close")));
		RunOverlay(dialog, body);
	}

	private Button CreateDialogButton(string text)
	{
		var button = new Button { Text = text };
		TerminalWorkspacePresentationPolicy.ConfigureOverlayButton(
			button,
			_options.Plain);
		return button;
	}

	private static int EstimateWrappedLineCount(string message, int width)
	{
		var lineCount = 0;
		foreach (var line in message.Split('\n'))
			lineCount += Math.Max(1, (line.GetColumns() + width - 1) / width);
		return lineCount;
	}

	internal static string FitPathToWidth(string value, int width)
	{
		value = TerminalTextEscaping.EscapeSingleLine(value);
		if (string.IsNullOrEmpty(value) || width <= 0)
			return string.Empty;
		if (value.GetColumns() <= width)
			return value;
		if (width <= 3)
			return new string('.', width);

		var stablePrefix = GetStableSourcePrefix(value, width);
		var remaining = width - stablePrefix.GetColumns() - 3;
		if (remaining <= 0)
		{
			stablePrefix = string.Empty;
			remaining = width - 3;
		}
		var runes = value.EnumerateRunes().ToArray();
		var start = runes.Length;
		while (start > 0)
		{
			var columns = runes[start - 1].GetColumns();
			if (columns > remaining)
				break;
			remaining -= columns;
			start--;
		}

		return stablePrefix + "..." + string.Concat(runes.AsSpan(start).ToArray());
	}

	private static string GetStableSourcePrefix(string value, int width)
	{
		if (!value.Contains("://", StringComparison.Ordinal) ||
			!Uri.TryCreate(value, UriKind.Absolute, out var uri))
		{
			return string.Empty;
		}

		var prefix = uri.IsFile
			? "file:///"
			: $"{uri.Scheme}://{uri.Host}/";
		return prefix.GetColumns() + 4 <= width
			? prefix
			: string.Empty;
	}

	private static string FitEndToWidth(string value, int width)
	{
		if (string.IsNullOrEmpty(value) || width <= 0)
			return string.Empty;
		if (value.GetColumns() <= width)
			return value;
		if (width <= 3)
			return value.EnumerateRunes().Take(width).Aggregate(
				new StringBuilder(),
				static (builder, rune) => builder.Append(rune),
				static builder => builder.ToString());

		var remaining = width - 3;
		var builder = new StringBuilder();
		foreach (var rune in value.EnumerateRunes())
		{
			var columns = rune.GetColumns();
			if (columns > remaining)
				break;
			builder.Append(rune);
			remaining -= columns;
		}
		return builder.Append("...").ToString();
	}

	private Dialog CreateDialog(string title, int preferredWidth, int preferredHeight)
	{
		var width = ResolveDialogWidth(preferredWidth);
		var maximumHeight = Math.Max(5, _application.Screen.Height - 2);
		var minimumHeight = Math.Min(7, maximumHeight);
		var height = Math.Clamp(preferredHeight, minimumHeight, maximumHeight);
		var dialog = new Dialog
		{
			Title = _options.Plain ? string.Empty : title,
			Width = width,
			Height = height,
			BorderStyle = _presentation.BorderStyle,
			SchemeName = TerminalWorkspaceTheme.Dialog,
			ButtonAlignment = Alignment.Center
		};
		if (_options.Plain)
			dialog.ShadowStyle = ShadowStyles.None;
		dialog.KeyBindings.Add(Key.Esc, Command.Quit);
		return dialog;
	}

	private void AlignWelcomeDialogAfterActions(
		Dialog dialog,
		int dialogWidth)
	{
		var minimumDialogX =
			WelcomeHorizontalMargin + WelcomeWideActionsWidth + 2;
		var availableWidth =
			_terminalWidth - minimumDialogX - WelcomeHorizontalMargin;
		if (_layoutMode is TerminalWorkspaceLayoutMode.Split or TerminalWorkspaceLayoutMode.Wide &&
			availableWidth >= dialogWidth)
		{
			dialog.X = minimumDialogX;
		}
	}

	private int ResolveDialogWidth(int preferredWidth)
	{
		var maximumWidth = Math.Max(12, _application.Screen.Width - 4);
		var minimumWidth = Math.Min(40, maximumWidth);
		return Math.Clamp(preferredWidth, minimumWidth, maximumWidth);
	}

	private void RunOverlay(IRunnable overlay, View? initialFocus = null)
	{
		var previousFocus = _root.MostFocused;
		// Focused lists and dialog buttons may consume Esc before it reaches the
		// runnable. The application-level lease keeps Back behavior consistent.
		void CloseOverlayOnEscape(object? _, Key key)
		{
			if (key == Key.Esc && ReferenceEquals(_application.TopRunnableView, overlay))
			{
				key.Handled = true;
				_application.RequestStop(overlay);
			}
		}
		try
		{
			_application.Keyboard.KeyDown += CloseOverlayOnEscape;
			if (initialFocus is not null)
				_application.Invoke(_ => initialFocus.SetFocus());
			_application.Run(overlay);
		}
		finally
		{
			_application.Keyboard.KeyDown -= CloseOverlayOnEscape;
			if (!_stopping)
			{
				if (previousFocus is { Visible: true, Enabled: true, CanFocus: true })
					previousFocus.SetFocus();
				else
					RestoreScreenFocus();
			}
		}
	}

	private void RestoreScreenFocus()
	{
		switch (_screen)
		{
			case TerminalWorkspaceScreen.Welcome:
				_welcomeList?.SetFocus();
				break;
			case TerminalWorkspaceScreen.Workspace when _activePane == TerminalWorkspacePane.Preview:
				_preview?.SetFocus();
				break;
			case TerminalWorkspaceScreen.Workspace when _activePane == TerminalWorkspacePane.Controls:
				ActiveControlView?.SetFocus();
				break;
			case TerminalWorkspaceScreen.Workspace:
				_tree?.SetFocus();
				break;
		}
	}

	private void SetOperationStatusSafe(string text, string schemeName)
	{
		if (_stopping)
			return;
		_application.Invoke(() => SetOperationStatus(text, schemeName));
	}

	private CancellationTokenSource ReplaceActiveOperation()
		=> _operations.Start(WorkspaceOperationKind.Active);

	private void TrackActiveOperation(Task task)
	{
		var tracked = TrackBackgroundTask(task);
		_operations.TryTrackCurrent(WorkspaceOperationKind.Active, tracked);
	}

	private void TrackOperation(
		WorkspaceOperationKind kind,
		CancellationTokenSource source,
		Task task)
	{
		var tracked = TrackBackgroundTask(task);
		_operations.TryTrack(kind, source, tracked);
	}

	private void ReleaseActiveOperation(CancellationTokenSource operationCts)
		=> _operations.Complete(WorkspaceOperationKind.Active, operationCts);

	private void MarkActiveOperationFinished(CancellationTokenSource operationCts)
		=> _operations.Complete(
			WorkspaceOperationKind.Active,
			operationCts,
			dispose: false);

	private void CancelActiveOperation()
		=> _operations.Cancel(WorkspaceOperationKind.Active);

	private bool HasActiveOperation =>
		_operations.IsRunning(WorkspaceOperationKind.Active);

	private async Task<T> InvokeAsync<T>(Func<T> action)
	{
		if (_stopping || _sessionCts.IsCancellationRequested)
			return await Task.FromCanceled<T>(_sessionCts.Token).ConfigureAwait(false);

		var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
		// A callback queued just before RequestStop is not guaranteed to run after the
		// Terminal.Gui event loop exits. Session cancellation must release the worker
		// that is awaiting it, otherwise shutdown can deadlock in CompleteAsync.
		using var cancellationRegistration = _sessionCts.Token.Register(
			() => completion.TrySetCanceled(_sessionCts.Token));
		_application.Invoke(() =>
		{
			if (_stopping || _sessionCts.IsCancellationRequested)
			{
				completion.TrySetCanceled(_sessionCts.Token);
				return;
			}
			try
			{
				completion.TrySetResult(action());
			}
			catch (Exception exception)
			{
				completion.TrySetException(exception);
			}
		});
		return await completion.Task.ConfigureAwait(false);
	}

	private void RequestExit()
	{
		ExitRequested = true;
		_prepareForShutdown();
		_sessionCts.Cancel();
		CancelActiveOperation();
		CancelWorkspaceRefreshes();
		_application.RequestStop(_root);
	}

	private void ClearRoot()
	{
		DismissOperationProgress();
		_workspaceViews?.CornerProgress.Dispose();
		CancelTransientStatus();
		CancelCommandResult();
		var previousState = _state;
		_state = null;
		_workspaceViews = null;
		_welcomeViews = null;
		_loadingViews = null;
		_workspaceActionRegistry = null;
		_workspaceActionRegistryKey = null;
		_controlSourceStamp = null;
		_redactionLabelStamp = null;
		_activeAggregateControlSection = null;
		previousState?.Dispose();
		foreach (var view in _root.RemoveAll())
			view.Dispose();
		_application.ClearScreenNextIteration = true;
		_root.SetNeedsLayout();
		_root.SetNeedsDraw();
	}

	private void DrawTransitionedRoot()
	{
		// Paint blank cells through the normal output buffer so ANSI drivers erase
		// content from the previous root before a nested overlay starts drawing.
		_application.ClearScreenNextIteration = false;
		_root.SetAttributeForRole(VisualRole.Normal);
		_root.FillRect(
			new System.Drawing.Rectangle(
				0,
				0,
				_root.Viewport.Width,
				_root.Viewport.Height),
			new Rune(' '));
		_root.SetNeedsLayout();
		_root.SetNeedsDraw();
		_application.LayoutAndDraw();
	}

	private void CompleteRootTransition()
	{
		// Initial content is rendered by Application.RunAsync. Runtime root changes
		// must paint immediately so smaller screens cannot expose stale ANSI cells.
		if (ReferenceEquals(_application.TopRunnableView, _root))
			DrawTransitionedRoot();
	}

	private static void SetVisible(bool visible, params View[] views)
	{
		foreach (var view in views)
			view.Visible = visible;
	}

	private static void MoveListSelection(ListView list, int delta)
	{
		var count = list.Source?.Count ?? 0;
		if (count == 0)
			return;
		list.SelectedItem = Math.Clamp((list.SelectedItem ?? 0) + delta, 0, count - 1);
	}

	private static bool TryResolveDirectory(string? path, out string normalized)
	{
		normalized = string.Empty;
		if (string.IsNullOrWhiteSpace(path))
			return false;
		try
		{
			normalized = PathUtility.Normalize(path);
			return Directory.Exists(normalized);
		}
		catch
		{
			return false;
		}
	}

	private static string GetProductVersion()
	{
		var assembly = Assembly.GetEntryAssembly() ?? typeof(TerminalWorkspaceSession).Assembly;
		var informational = assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
			.InformationalVersion;
		var version = informational?.Split('+', 2)[0] ?? assembly.GetName().Version?.ToString();
		return string.IsNullOrWhiteSpace(version) ? "dev" : version;
	}

	private static string GetProjectDisplayName(ProjectContextPlan? plan)
	{
		if (plan?.SourceIdentity?.DisplayName is { Length: > 0 } displayName)
			return TerminalTextEscaping.EscapeSingleLine(displayName);
		if (plan is null)
			return string.Empty;
		var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(plan.SourceRoot));
		return TerminalTextEscaping.EscapeSingleLine(
			string.IsNullOrEmpty(name) ? plan.SourceRoot : name);
	}

	private string BuildWorkspaceHeading(ProjectContextPlan plan)
	{
		var branch = plan.SourceIdentity?.Branch is { Length: > 0 } value
			? $" [{TerminalTextEscaping.EscapeSingleLine(value)}]"
			: string.Empty;
		return $"DevProjex Terminal{PanelSeparator}{GetProjectDisplayName(plan)}{branch}";
	}

	private static string GetProjectDisplaySource(ProjectContextPlan plan) =>
		TerminalTextEscaping.EscapeSingleLine(
			plan.SourceIdentity?.SourceReference is { Length: > 0 } sourceReference
			? sourceReference
			: plan.SourceRoot);

	internal static void CancelAndDispose(ref CancellationTokenSource? source)
	{
		var current = Interlocked.Exchange(ref source, null);
		if (current is null)
			return;
		try
		{
			current.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// A completing background task can release the same source during shutdown.
		}
		current.Dispose();
	}

	private Task TrackBackgroundTask(Task task) => _backgroundTasks.Track(task);

	private void ReleaseOwnedRepositorySession() =>
		Interlocked.Exchange(ref _ownedRepositorySession, null)?.Dispose();

	private string PanelSeparator => _options.Plain ? " | " : " · ";

	private string L(string key)
	{
		var value = _workspace.L(key);
		return NormalizeLocalizedText(value, _options.Plain, _environment.SupportsUnicode);
	}

	internal static string NormalizeLocalizedText(
		string value,
		bool plain,
		bool supportsUnicode) =>
		plain || !supportsUnicode ? TerminalPlainText.Normalize(value) : value;

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_application.Keyboard.KeyDown -= OnRootKeyDown;
		_services.Localization.LanguageChanged -= OnLanguageChanged;
		_application.ScreenChanged -= _screenChangedHandler;
		if (_subscribedDriver is not null && _driverSizeChangedHandler is not null)
			_subscribedDriver.SizeChanged -= _driverSizeChangedHandler;
		_sessionCts.Cancel();
		_operations.Dispose();
		DismissOperationProgress();
		_workspaceViews?.CornerProgress.Dispose();
		_workspaceViews = null;
		_welcomeViews = null;
		_loadingViews = null;
		var state = _state;
		_state = null;
		state?.Dispose();
		ReleaseOwnedRepositorySession();
		_sessionCts.Dispose();
		_settingsPersistenceCts.Cancel();
		_settingsPersistenceCts.Dispose();
		_operationGate.Dispose();
	}

	private void ShowDiagnostics()
	{
		if (_state is null)
			return;
		var diagnostics = _state.Plan.Diagnostics;
		var body = diagnostics.Count == 0
			? L("Terminal.Tui.Diagnostics.None")
			: string.Join("\n\n", diagnostics.Select(diagnostic =>
			{
				var severity = diagnostic.Severity switch
				{
					ContextDiagnosticSeverity.Error => L("Terminal.Label.Error"),
					ContextDiagnosticSeverity.Warning => L("Terminal.Label.Warning"),
					_ => L("Terminal.Label.Info")
				};
				var message = ContextDiagnosticRenderer.ResolveMessage(
					_services.Localization,
					diagnostic);
				var path = string.IsNullOrWhiteSpace(diagnostic.Path)
					? string.Empty
					: $"\n{L("Terminal.Label.Path")}: {TerminalTextEscaping.EscapeSingleLine(diagnostic.Path)}";
				return $"{severity} [{diagnostic.Code}]\n{message}{path}";
			}));
		ShowScrollableOverlay(
			L("Terminal.Tui.Command.Diagnostics.Title"),
			body,
			TerminalWorkspaceTheme.Dialog,
			preferredWidth: 92,
			preferredHeight: 27);
	}

	private bool TryLeaveWorkspace(Action leave)
	{
		ArgumentNullException.ThrowIfNull(leave);
		if (_screen == TerminalWorkspaceScreen.Workspace &&
			!Confirm(L("Terminal.Tui.BackToWelcome"), L("Terminal.Tui.ConfirmBackToWelcome")))
		{
			return false;
		}
		FlushPendingWorkspacePersistence();
		leave();
		return true;
	}

	private bool TryExitWorkspace()
	{
		if (HasActiveOperation)
		{
			CancelActiveOperation();
			ShowCancelingOperation();
			return false;
		}
		if (!Confirm(L("Terminal.Tui.Exit"), L("Terminal.Tui.ConfirmExit")))
			return false;
		FlushPendingWorkspacePersistence();
		RequestExit();
		return true;
	}

	private enum TerminalWorkspaceScreen
	{
		TooSmall,
		Welcome,
		Loading,
		Workspace
	}

	private enum TerminalPaneNavigation
	{
		Next,
		Previous
	}

	private enum TerminalPreviewScroll
	{
		LineUp,
		LineDown,
		PageUp,
		PageDown,
		Start,
		End,
		ColumnLeft,
		ColumnRight
	}

	private enum TerminalProjectOpenSource
	{
		Other,
		Recent,
		RecentRepository,
		Clone
	}

	private sealed record AutomaticProfileResolution(
		ProjectProfileReference Profile,
		ProjectProfileLookupStatus Status);

	private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
	{
		public void Report(T value) => report(value);
	}

	private sealed class TerminalWorkspaceOperationException(string code) : Exception
	{
		public string Code { get; } = code;
	}
}

#pragma warning restore CS0618
