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

	private readonly IApplication _application;
	private readonly Window _root;
	private readonly TerminalServices _services;
	private readonly ITerminalEnvironment _environment;
	private readonly TerminalWorkspaceOptions _options;
	private readonly TerminalWorkspace _workspace;
	private readonly TerminalWorkspaceController _controller;
	private readonly TerminalWorkspacePresentation _presentation;
	private readonly ITerminalOperationObserver _operationObserver;
	private readonly CancellationTokenSource _sessionCts;
	private readonly SemaphoreSlim _operationGate = new(1, 1);

	private TerminalWorkspaceScreen _screen;
	private TerminalWorkspaceLayoutMode _layoutMode;
	private int _terminalWidth;
	private int _terminalHeight;
	private bool _deferredInitialStart;
	private bool _stopping;
	private bool _disposed;
	private CancellationTokenSource? _activeOperationCts;
	private CancellationTokenSource? _projectionCts;
	private CancellationTokenSource? _previewCts;
	private CancellationTokenSource? _previewSearchCts;
	private Task? _openTask;
	private Task? _activeOperationTask;
	private Task? _projectionTask;
	private Task? _previewTask;
	private Task? _previewSearchTask;
	private long _projectionRequestId;
	private long _previewRequestId;
	private long _previewSearchRequestId;
	private bool _previewSearchInProgress;

	private TerminalWelcomeContext? _welcomeContext;
	private RecentProjectsDb? _recentProjectsSnapshot;
	private string? _recentWorkspaceSelectionKey;
	private ObservableCollection<TerminalWelcomeActionRow>? _welcomeRows;
	private ListView? _welcomeList;
	private TextView? _welcomeDetail;
	private FrameView? _welcomeActionsFrame;
	private FrameView? _welcomeDetailFrame;
	private Label? _welcomeActionsHeading;
	private Label? _welcomeDetailHeading;
	private Label? _welcomeHeading;
	private Label? _welcomeVersion;
	private Label? _welcomeTagline;
	private Label? _welcomeCurrentTitle;
	private Label? _welcomeCurrentPath;
	private Label? _welcomeCurrentStatus;
	private Label? _welcomeQuickStart;
	private Label? _welcomeFooter;
	private Label? _tooSmall;

	private TerminalWorkspaceState? _state;
	private TerminalProjectTreeView? _tree;
	private TerminalVirtualizedPreviewView? _preview;
	private Label? _previewRange;
	private FrameView? _treeFrame;
	private FrameView? _previewFrame;
	private Label? _treePanelHeading;
	private Label? _previewPanelHeading;
	private Label? _workspaceHeading;
	private Label? _workspacePath;
	private Label? _status;
	private Label? _footer;
	private TerminalOperationProgressView? _operationProgress;
	private TerminalWorkspacePane _activePane = TerminalWorkspacePane.Tree;
	private ProjectContextView _previewView = ProjectContextView.Tree;
	private ProjectContextDocumentFormat _format = ProjectContextDocumentFormat.Text;
	private string? _searchQuery;
	private string? _previewSearchQuery;
	private string? _selectedTreePath;
	private bool _suppressTreeSelectionTracking;

	public TerminalWorkspaceSession(
		IApplication application,
		Window root,
		TerminalServices services,
		ITerminalEnvironment environment,
		TerminalWorkspaceOptions options,
		TerminalWorkspace workspace,
		ITerminalOperationObserver operationObserver,
		CancellationToken cancellationToken)
	{
		_application = application;
		_root = root;
		_services = services;
		_environment = environment;
		_options = options;
		_workspace = workspace;
		_operationObserver = operationObserver ??
			throw new ArgumentNullException(nameof(operationObserver));
		_controller = new TerminalWorkspaceController(services, environment);
		_presentation = TerminalWorkspacePresentationPolicy.Resolve(
			options.ColorMode,
			options.Plain,
			environment);
		_sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var initialScreen = _application.Driver?.Screen ?? _application.Screen;
		_terminalWidth = Math.Max(_environment.Width, initialScreen.Width);
		_terminalHeight = Math.Max(_environment.Height, initialScreen.Height);
		_application.Keyboard.KeyDown += OnRootKeyDown;
		_application.ScreenChanged += (_, _) =>
		{
			// Inline roots are resized in Driver.SizeChanged below. Applying the old
			// geometry here would draw a split frame into the already resized buffer.
			if (_application.AppModel == AppModel.Inline)
				return;
			var sizeChanged = false;
			var screen = _application.Driver?.Screen ?? _application.Screen;
			sizeChanged = UpdateTerminalSize(screen.Width, screen.Height);
			ApplyCurrentLayout();
			if (sizeChanged)
				InvalidateAfterTerminalResize();
		};
		if (_application.Driver is { } driver)
		{
			driver.SizeChanged += (_, args) => _application.Invoke(() =>
			{
				var sizeChanged = false;
				if (_application.AppModel == AppModel.Inline && args.Size is { } size)
				{
					_root.Width = size.Width;
					_root.Height = size.Height;
				}
				if (args.Size is { } terminalSize)
					sizeChanged = UpdateTerminalSize(terminalSize.Width, terminalSize.Height);
				ApplyCurrentLayout();
				if (sizeChanged)
					InvalidateAfterTerminalResize();
			});
		}
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
		_stopping = true;
		_sessionCts.Cancel();
		CancelAndDispose(ref _activeOperationCts);
		CancelAndDispose(ref _projectionCts);
		CancelAndDispose(ref _previewCts);
		CancelAndDispose(ref _previewSearchCts);

		var pending = new[]
			{
				_openTask,
				_activeOperationTask,
				_projectionTask,
				_previewTask,
				_previewSearchTask
			}
			.Where(static task => task is not null)
			.Cast<Task>()
			.ToArray();
		try
		{
			await Task.WhenAll(pending).ConfigureAwait(false);
		}
		catch
		{
			// Every background workflow already converts failures into a stable TUI state.
		}
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
		_tooSmall = new TerminalLiteralLabel
		{
			X = Pos.Center(),
			Y = Pos.Center(),
			Width = Dim.Auto(),
			Text = L("Terminal.Tui.Error.Resize"),
			SchemeName = TerminalWorkspaceTheme.Warning
		};
		_root.Add(_tooSmall);
	}

	private void ShowWelcome(TerminalWelcomeActionKind? selectedAction = null)
	{
		CancelWorkspaceRefreshes();
		ClearRoot();
		_screen = TerminalWorkspaceScreen.Welcome;
		_layoutMode = ResolveLayout();
		_welcomeContext = LoadWelcomeContext();
		_welcomeRows = new ObservableCollection<TerminalWelcomeActionRow>(
			BuildWelcomeActions(_welcomeContext).Select(static action => new TerminalWelcomeActionRow(action)));
		var selectedActionIndex = selectedAction is null
			? 0
			: _welcomeRows
				.Select((row, index) => (row, index))
				.FirstOrDefault(pair => pair.row.Action.Kind == selectedAction)
				.index;
		selectedActionIndex = Math.Clamp(selectedActionIndex, 0, Math.Max(0, _welcomeRows.Count - 1));
		if (_welcomeRows.Count > 0)
			_welcomeRows[selectedActionIndex].IsSelected = true;

		_welcomeHeading = new TerminalLiteralLabel
		{
			X = 2,
			Y = 1,
			Text = "DevProjex Terminal",
			SchemeName = TerminalWorkspaceTheme.Accent
		};
		var versionText = $"v{GetProductVersion()}";
		_welcomeVersion = new TerminalLiteralLabel
		{
			X = Pos.AnchorEnd(versionText.Length + 2),
			Y = 1,
			Width = versionText.Length,
			Text = versionText,
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		_welcomeTagline = new TerminalLiteralLabel
		{
			X = 2,
			Y = 2,
			Width = Dim.Fill(2),
			Text = L("Terminal.Tui.Welcome.Description"),
			SchemeName = TerminalWorkspaceTheme.Base
		};
		_welcomeCurrentTitle = new TerminalLiteralLabel
		{
			X = 2,
			Y = 4,
			Text = L("Terminal.Tui.CurrentDirectory"),
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		_welcomeCurrentPath = new TerminalLiteralLabel
		{
			X = 2,
			Y = 5,
			Width = Dim.Fill(33),
			Height = 1,
			Text = _welcomeContext.CurrentDirectory,
			SchemeName = TerminalWorkspaceTheme.Base
		};
		_welcomeCurrentStatus = new TerminalLiteralLabel
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

		_welcomeActionsFrame = new TerminalLiteralFrameView
		{
			Title = L("Terminal.Tui.Actions"),
			SchemeName = TerminalWorkspaceTheme.FocusedPanel,
			BorderStyle = _presentation.BorderStyle
		};
		_welcomeActionsHeading = new TerminalLiteralLabel
		{
			X = 0,
			Y = 0,
			Width = Dim.Fill(),
			Height = 1,
			Text = $"> {L("Terminal.Tui.Actions")}",
			Visible = _options.Plain,
			SchemeName = TerminalWorkspaceTheme.Accent
		};
		_welcomeList = new ListView
		{
			X = 0,
			Y = _options.Plain ? 1 : 0,
			Width = Dim.Fill(),
			Height = Dim.Fill(),
			ShowMarks = false,
			SchemeName = TerminalWorkspaceTheme.List
		};
		_welcomeList.SetSource(_welcomeRows);
		if (_welcomeRows.Count > 0)
			_welcomeList.SelectedItem = selectedActionIndex;
		_welcomeList.ValueChanged += (_, _) => UpdateWelcomeSelection();
		// Defer nested workflows until the Enter key that accepted the row has finished dispatching.
		_welcomeList.Accepted += (_, _) => _application.Invoke(ActivateWelcomeSelection);
		_welcomeActionsFrame.Add(_welcomeActionsHeading, _welcomeList);

		_welcomeDetailFrame = new TerminalLiteralFrameView
		{
			Title = L("Terminal.Tui.Details"),
			SchemeName = TerminalWorkspaceTheme.Panel,
			BorderStyle = _presentation.BorderStyle
		};
		_welcomeDetailHeading = new TerminalLiteralLabel
		{
			X = 0,
			Y = 0,
			Width = Dim.Fill(),
			Height = 1,
			Text = L("Terminal.Tui.Details"),
			Visible = _options.Plain,
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		_welcomeDetail = new TextView
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
		_welcomeDetailFrame.Add(_welcomeDetailHeading, _welcomeDetail);
		_welcomeQuickStart = new TerminalLiteralLabel
		{
			X = 2,
			Width = Dim.Fill(2),
			SchemeName = TerminalWorkspaceTheme.Secondary,
			Text = L("Terminal.Tui.Welcome.QuickStart")
		};
		_welcomeFooter = new TerminalLiteralLabel
		{
			X = 2,
			Y = Pos.AnchorEnd(1),
			Width = Dim.Fill(2),
			Text = L("Terminal.Tui.Footer.Welcome"),
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		_tooSmall = CreateTooSmallLabel();

		_root.Add(
			_welcomeHeading,
			_welcomeVersion,
			_welcomeTagline,
			_welcomeCurrentTitle,
			_welcomeCurrentPath,
			_welcomeCurrentStatus,
			_welcomeActionsFrame,
			_welcomeDetailFrame,
			_welcomeQuickStart,
			_welcomeFooter,
			_tooSmall);
		UpdateWelcomeSelection();
		ApplyWelcomeLayout();
		_welcomeList.SetFocus();
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
			case TerminalWelcomeActionKind.BrowseFolder:
				BrowseForProject();
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
		_activeOperationTask = Task.Run(async () =>
		{
			string? stagingPath = null;
			try
			{
				stagingPath = _services.RepoCacheService.CreateRepositoryStagingDirectory(url);
				UpdateClonePhaseSafe(
					"Terminal.Tui.Clone.Connecting",
					L("Terminal.Tui.Clone.StartingGit"));
				await _operationObserver
					.ObservePhaseAsync(
						TerminalOperationPhase.CloneConnecting,
						operationCts.Token)
					.ConfigureAwait(false);
				var progress = new SynchronousProgress<string>(UpdateCloneProgressSafe);
				var result = await _services.GitRepositoryService
					.CloneAsync(url, stagingPath, progress, operationCts.Token)
					.ConfigureAwait(false);
				if (!result.Success || !Directory.Exists(result.LocalPath))
					throw new TerminalWorkspaceOperationException("DPX-TUI-CLONE-FAILED");
				var cachePath = _services.RepoCacheService.PublishRepositoryDirectory(
					stagingPath,
					result.RepositoryUrl ?? url);
				stagingPath = null;
				result = result with { LocalPath = cachePath };

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
				await OpenProjectCoreAsync(
						result.LocalPath,
						ResolveAutomaticProfile(result.LocalPath).Profile,
						operationCts,
						returnToRepositoryHistory
							? TerminalProjectOpenSource.RecentRepository
							: TerminalProjectOpenSource.Clone,
						releaseOperation: false,
						identity)
					.ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
			{
				if (stagingPath is not null)
					_services.RepoCacheService.DeleteRepositoryDirectory(stagingPath);
				if (returnToRepositoryHistory)
					ReturnToRepositoryHistoryAfterCancellation(operationCts);
				else
					ReturnToWelcomeAfterCancellation(operationCts);
			}
			catch
			{
				if (stagingPath is not null)
					_services.RepoCacheService.DeleteRepositoryDirectory(stagingPath);
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
		}, CancellationToken.None);
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
		_activeOperationTask = Task.Run(async () =>
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
		}, CancellationToken.None);
	}

	private void BeginOpenProject(
		string projectPath,
		ProjectProfileReference profile,
		TerminalProjectOpenSource source = TerminalProjectOpenSource.Other,
		ProjectSourceIdentity? sourceIdentity = null)
	{
		ShowLoading(
			L("Terminal.Tui.LoadingProject"),
			sourceIdentity?.SourceReference ?? projectPath);
		var operationCts = ReplaceActiveOperation();
		_openTask = Task.Run(
			() => OpenProjectCoreAsync(
				projectPath,
				profile,
				operationCts,
				source,
				sourceIdentity: sourceIdentity),
			CancellationToken.None);
	}

	private async Task OpenProjectCoreAsync(
		string projectPath,
		ProjectProfileReference profile,
		CancellationTokenSource operationCts,
		TerminalProjectOpenSource source = TerminalProjectOpenSource.Other,
		bool releaseOperation = true,
		ProjectSourceIdentity? sourceIdentity = null)
	{
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
			if (_stopping || operationCts.IsCancellationRequested)
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
			await InvokeAsync(() =>
			{
				if (ReferenceEquals(_activeOperationCts, operationCts))
					ShowWorkspace(state);
				return true;
			}).ConfigureAwait(false);
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
				source);
		}
		catch (ProjectContextValidationException exception)
		{
			ReturnFromProjectOpenError(
				operationCts,
				exception.Code,
				ResolveValidationErrorMessage(exception.Code),
				projectPath,
				source);
		}
		catch
		{
			ReturnFromProjectOpenError(
				operationCts,
				"DPX-TUI-PROJECT-OPEN-FAILED",
				L("Terminal.Tui.Error.ProjectUnavailable"),
				projectPath,
				source);
		}
		finally
		{
			if (releaseOperation)
				ReleaseActiveOperation(operationCts);
		}
	}

	private void ReturnFromProjectOpenError(
		CancellationTokenSource operationCts,
		string code,
		string message,
		string projectPath,
		TerminalProjectOpenSource source)
	{
		if (source is TerminalProjectOpenSource.Recent or
		    TerminalProjectOpenSource.RecentRepository)
		{
			ReturnToRepositoryHistoryWithError(operationCts, code, $"{message}\n\n{projectPath}");
			return;
		}
		ReturnToWelcomeWithError(operationCts, code, message);
	}

	private void ReturnToWelcomeAfterCancellation(CancellationTokenSource operationCts)
	{
		if (_stopping || !ReferenceEquals(_activeOperationCts, operationCts))
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
		if (_stopping || !ReferenceEquals(_activeOperationCts, operationCts))
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
			Text = detail,
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
		_tooSmall = CreateTooSmallLabel();
		_root.Add(heading, spinner, operation, details, footer, _tooSmall);
		ApplyLoadingLayout();
		CompleteRootTransition();
	}

	private void ShowWorkspace(TerminalWorkspaceState state)
	{
		CancelWorkspaceRefreshes();
		ClearRoot();
		_screen = TerminalWorkspaceScreen.Workspace;
		_state = state;
		_layoutMode = ResolveLayout();
		_activePane = TerminalWorkspacePane.Tree;

		_workspaceHeading = new TerminalLiteralLabel
		{
			X = 1,
			Y = 0,
			Width = Dim.Fill(1),
			Height = 1,
			Text = BuildWorkspaceHeading(state.Plan),
			SchemeName = TerminalWorkspaceTheme.Accent
		};
		_workspacePath = new TerminalLiteralLabel
		{
			X = 1,
			Y = 1,
			Width = Dim.Fill(1),
			Height = 1,
			Text = GetProjectDisplaySource(state.Plan),
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		_treeFrame = new TerminalLiteralFrameView
		{
			Title = L("Terminal.Tui.Tree"),
			BorderStyle = _presentation.BorderStyle,
			SchemeName = TerminalWorkspaceTheme.FocusedPanel
		};
		_previewFrame = new TerminalLiteralFrameView
		{
			BorderStyle = _presentation.BorderStyle,
			SchemeName = TerminalWorkspaceTheme.Panel
		};
		_treePanelHeading = new TerminalLiteralLabel
		{
			X = 0,
			Y = 0,
			Width = Dim.Fill(),
			Height = 1,
			Visible = _options.Plain,
			SchemeName = TerminalWorkspaceTheme.Accent
		};
		_previewPanelHeading = new TerminalLiteralLabel
		{
			X = 0,
			Y = 0,
			Width = Dim.Fill(),
			Height = 1,
			Visible = _options.Plain,
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		_tree = new TerminalProjectTreeView(index =>
			index >= 0 && index < state.VisibleRows.Count
				? state.VisibleRows[index]
				: null)
		{
			X = 0,
			Y = _options.Plain ? 1 : 0,
			Width = Dim.Fill(),
			Height = Dim.Fill(),
			ShowMarks = false,
			SchemeName = TerminalWorkspaceTheme.List
		};
		_tree.SetSource(state.VisibleRows);
		_tree.ValueChanged += (_, _) => TrackTreeSelection();
		_tree.KeyBindings.ReplaceCommands(Key.Space, Command.Activate);
		_tree.Activated += (_, _) => ToggleCurrentTreeSelection();
		_tree.Accepted += (_, _) => ToggleCurrentTreeExpansion();
		_tree.SelectionToggleRequested += (_, _) => ToggleCurrentTreeSelection();
		_tree.ExpansionToggleRequested += (_, _) => ToggleCurrentTreeExpansion();
		_tree.HasFocusChanged += (_, _) => UpdateWorkspaceFocus();

		_preview = new TerminalVirtualizedPreviewView(
			_environment.SupportsUnicode && !_options.Plain,
			showScrollBars: !_options.Plain)
		{
			X = 0,
			Y = _options.Plain ? 1 : 0,
			Width = Dim.Fill(),
			Height = Dim.Fill(1)
		};
		_preview.SetDocument(state.PreviewDocument, preserveViewport: false);
		_preview.RedactionToggleRequested += OnPreviewRedactionToggleRequested;
		_previewRange = new TerminalLiteralLabel
		{
			X = 1,
			Y = Pos.AnchorEnd(1),
			Width = Dim.Fill(1),
			Height = 1,
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		_preview.VisibleRangeChanged += (_, _) => UpdatePreviewRange();
		_preview.HasFocusChanged += (_, _) => UpdateWorkspaceFocus();
		_treeFrame.Add(_treePanelHeading, _tree);
		_previewFrame.Add(_previewPanelHeading, _preview, _previewRange);
		CreateContextControls();
		if (state.VisibleRows.Count > 0)
		{
			_tree.SelectedItem = 0;
			_selectedTreePath = state.VisibleRows[0].Node.FullPath;
		}

		_status = new TerminalLiteralLabel
		{
			X = 1,
			Y = Pos.AnchorEnd(2),
			Width = Dim.Fill(1),
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		_footer = new TerminalLiteralLabel
		{
			X = 1,
			Y = Pos.AnchorEnd(1),
			Width = Dim.Fill(1),
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		_tooSmall = CreateTooSmallLabel();
		_root.Add(
			_workspaceHeading,
			_workspacePath,
			_treeFrame,
			_previewFrame,
			_controlsFrame!,
			_status,
			_footer,
			_tooSmall);

		_tree.SetFocus();
		RefreshWorkspace();
		ApplyWorkspaceLayout();
		UpdateWorkspaceFocus();
		CompleteRootTransition();
		SchedulePreviewRefresh();
	}

	private void RefreshWorkspace()
	{
		if (_state is null || _tree is null || _preview is null)
			return;

		var selected = FindSelectedTreeRow();
		var treeHadFocus = _tree.HasFocus;
		var previewHadFocus = _preview.HasFocus;
		var controlsHadFocus = _controls?.HasFocus == true;
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
		else if (controlsHadFocus)
			_controls?.SetFocus();
		if (_status is not null)
			_status.Text = BuildStatus(_state, _application.Screen.Width);
		RefreshContextControls();
		UpdatePreviewRange();
		UpdatePanelTitles();
		UpdateFooter();
		if (previewDocumentChanged && !string.IsNullOrWhiteSpace(_previewSearchQuery))
			SchedulePreviewSearch(_previewSearchQuery, showNoResults: false);
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
		if (width < 80)
		{
			return $"{state.SelectedFileCount:N0} F  {state.SelectedFolderCount:N0} D  " +
			       $"~{tokens:N0} tok  " +
			       $"{warningCount:N0} W  {errorCount:N0} E";
		}

		var separator = _environment.SupportsUnicode ? PanelSeparator : " | ";
		return string.Join(
			separator,
			$"{L("Terminal.Analysis.Files")} {state.SelectedFileCount:N0}",
			$"{L("Terminal.Analysis.Folders")} {state.SelectedFolderCount:N0}",
			TerminalWorkspace.FormatBytes(state.Plan.IncludedBytes),
			$"~{tokens:N0} {L("Terminal.Tui.TokensShort")}",
			$"{L("Terminal.Tui.Warnings")} {warningCount:N0}",
			$"{L("Terminal.Tui.Errors")} {errorCount:N0}");
	}

	private static long ResolveDisplayedTokenCount(TerminalWorkspaceState state)
	{
		var reported = state.Plan.Analysis.Metrics.Content.Tokens;
		if (reported > 0 || state.PreviewDocument.CharacterCount == 0)
			return reported;
		return Math.Max(1, (state.PreviewDocument.CharacterCount + 3) / 4);
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
			var matchRange =
				$"{L("Terminal.Tui.Search")} " +
				$"{_preview.CurrentSearchMatchOrdinal:N0}/{_preview.SearchMatchCount:N0}  |  ";
			fullRange = matchRange + fullRange;
			compactRange =
				$"/ {_preview.CurrentSearchMatchOrdinal:N0}/{_preview.SearchMatchCount:N0}  |  " +
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
			previewTitle +=
				$"{PanelSeparator}/{_preview.SearchQuery} " +
				$"({_preview.CurrentSearchMatchOrdinal:N0}/{_preview.SearchMatchCount:N0})";
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
		if (_footer is null)
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
		if (_tree is null || _preview is null || _controls is null ||
		    _treeFrame is null || _previewFrame is null || _controlsFrame is null)
			return;

		if (_tree.HasFocus)
			_activePane = TerminalWorkspacePane.Tree;
		else if (_preview.HasFocus)
			_activePane = TerminalWorkspacePane.Preview;
		else if (_controls.HasFocus)
			_activePane = TerminalWorkspacePane.Controls;
		_treeFrame.SchemeName = _activePane == TerminalWorkspacePane.Tree
			? TerminalWorkspaceTheme.FocusedPanel
			: TerminalWorkspaceTheme.Panel;
		_previewFrame.SchemeName = _activePane == TerminalWorkspacePane.Preview
			? TerminalWorkspaceTheme.FocusedPanel
			: TerminalWorkspaceTheme.Panel;
		_controlsFrame.SchemeName = _activePane == TerminalWorkspacePane.Controls
			? TerminalWorkspaceTheme.FocusedPanel
			: TerminalWorkspaceTheme.Panel;
		UpdatePanelTitles();
		UpdateFooter();
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
		    _status is null || _footer is null)
		{
			return;
		}

		var tooSmall = _layoutMode == TerminalWorkspaceLayoutMode.TooSmall;
		SetVisible(
			!tooSmall,
			_workspaceHeading,
			_workspacePath,
			_treeFrame,
			_previewFrame,
			_controlsFrame,
			_status,
			_footer);
		_tooSmall.Visible = tooSmall;
		if (tooSmall)
			return;

		var contentWidth = Math.Max(1, _terminalWidth - 2);
		_workspaceHeading.Text = FitEndToWidth(
			_state is null ? "DevProjex Terminal" : BuildWorkspaceHeading(_state.Plan),
			contentWidth);
		_workspacePath.Text = FitPathToWidth(
			_state is null ? string.Empty : GetProjectDisplaySource(_state.Plan),
			contentWidth);
		_treeFrame.X = 0;
		_treeFrame.Y = 2;
		_treeFrame.Height = Dim.Fill(3);
		_previewFrame.Y = 2;
		_previewFrame.Height = Dim.Fill(3);
		if (_layoutMode == TerminalWorkspaceLayoutMode.Wide)
		{
			_treeFrame.Width = Dim.Percent(27);
			_previewFrame.X = Pos.Right(_treeFrame);
			_previewFrame.Width = Dim.Percent(40);
			_controlsFrame.X = Pos.Right(_previewFrame);
			_controlsFrame.Y = 2;
			_controlsFrame.Width = Dim.Fill();
			_controlsFrame.Height = Dim.Fill(3);
			_treeFrame.Visible = true;
			_previewFrame.Visible = true;
			_controlsFrame.Visible = true;
		}
		else if (_layoutMode == TerminalWorkspaceLayoutMode.Split &&
		         _activePane != TerminalWorkspacePane.Controls)
		{
			_treeFrame.Width = Dim.Percent(46);
			_previewFrame.X = Pos.Right(_treeFrame);
			_previewFrame.Width = Dim.Fill();
			_treeFrame.Visible = true;
			_previewFrame.Visible = true;
			_controlsFrame.Visible = false;
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
			ShowSinglePane(_activePane);
		}

		RefreshWorkspace();
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

		if (key == Key.C.WithCtrl)
		{
			key.Handled = true;
			if (HasActiveOperation)
			{
				CancelActiveOperation();
				ShowCancelingOperation();
			}
			else if (Confirm(
					L("Terminal.Tui.Exit"),
					L("Terminal.Tui.ConfirmExit")))
			{
				RequestExit();
			}
			return;
		}

		if (key.NoShift == Key.Q)
		{
			key.Handled = true;
			if (HasActiveOperation)
			{
				CancelActiveOperation();
				ShowCancelingOperation();
			}
			else
			{
				RequestExit();
			}
			return;
		}

		if (key == Key.Esc && HasActiveOperation)
		{
			key.Handled = true;
			CancelActiveOperation();
			ShowCancelingOperation();
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
	}

	private void HandleWorkspaceKey(Key key)
	{
		if (_state is null || _tree is null || _preview is null)
			return;
		if (HasActiveOperation)
			return;

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
				UpdatePreviewRange();
				UpdateFooter();
				return;
			}
			if (Confirm(L("Terminal.Tui.BackToWelcome"), L("Terminal.Tui.ConfirmBackToWelcome")))
				ShowWelcome();
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
			else if (_controls?.HasFocus == true)
				MoveListSelection(_controls, key.NoShift == Key.J ? 1 : -1);
			else
				ScrollPreview(
					key.NoShift == Key.J
						? TerminalPreviewScroll.LineDown
						: TerminalPreviewScroll.LineUp);
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
		if (key.NoShift == Key.E)
		{
			key.Handled = true;
			ExportContext();
			return;
		}
		if (key.NoShift == Key.Z)
		{
			key.Handled = true;
			ExportProject();
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
		if (key.NoShift == Key.M)
		{
			key.Handled = true;
			ChangeGitMode();
			return;
		}
		if (key.NoShift == Key.X)
		{
			key.Handled = true;
			ChangeExclusions();
			return;
		}
		if (key.NoShift == Key.R || key.NoShift == Key.T)
		{
			key.Handled = true;
			ChangeRootsOrTypes(key.NoShift == Key.R);
		}
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
			UpdatePreviewRange();
			return;
		}

		SchedulePreviewSearch(query, showNoResults: true);
	}

	private void SchedulePreviewSearch(string query, bool showNoResults)
	{
		if (_preview is null || _state is null)
			return;
		var normalizedQuery = query.Trim();
		if (normalizedQuery.Length == 0)
		{
			CancelPreviewSearch(clearQuery: true);
			_preview.ClearSearch();
			UpdatePanelTitles();
			UpdatePreviewRange();
			return;
		}

		CancelPreviewSearch(clearQuery: false);
		var state = _state;
		var document = state.PreviewDocument;
		var revision = state.Revision;
		var startLine = _preview.FirstVisibleLine;
		var requestId = Interlocked.Increment(ref _previewSearchRequestId);
		_previewSearchCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
		var operationCts = _previewSearchCts;
		var cancellationToken = operationCts.Token;
		_previewSearchQuery = normalizedQuery;
		_previewSearchInProgress = true;
		_preview.BeginSearch(normalizedQuery);
		UpdatePanelTitles();
		UpdatePreviewRange();

		_previewSearchTask = Task.Run(async () =>
		{
			try
			{
				var matches = PreviewTextDocumentSearch.FindAll(
					document,
					normalizedQuery,
					cancellationToken);
				await InvokeAsync(() =>
				{
					if (_stopping ||
					    !ReferenceEquals(_state, state) ||
					    !ReferenceEquals(state.PreviewDocument, document) ||
					    state.Revision != revision ||
					    !ReferenceEquals(_previewSearchCts, operationCts) ||
					    Volatile.Read(ref _previewSearchRequestId) != requestId ||
					    _preview is null)
					{
						return false;
					}

					_previewSearchInProgress = false;
					var match = _preview.ApplySearchResults(
						normalizedQuery,
						matches,
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
				    ReferenceEquals(_previewSearchCts, operationCts) &&
				    Volatile.Read(ref _previewSearchRequestId) == requestId)
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
		}, CancellationToken.None);
	}

	private void CancelPreviewSearch(bool clearQuery)
	{
		Interlocked.Increment(ref _previewSearchRequestId);
		CancelAndDispose(ref _previewSearchCts);
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
		var horizontalOffset = match.Column < _preview.HorizontalOffset ||
		                       match.Column >=
		                       _preview.HorizontalOffset + _preview.VisibleTextWidth
			? Math.Max(0, match.Column - 4)
			: _preview.HorizontalOffset;
		_preview.ScrollTo(match.Line, horizontalOffset);
		UpdatePanelTitles();
		UpdatePreviewRange();
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

	private void ExportContext()
	{
		if (_state is null)
			return;
		var defaultPath = BuildDefaultExportPath(
			_state.Plan.SourceRoot,
			Directory.GetCurrentDirectory(),
			$"{GetProjectDisplayName(_state.Plan)}-context" +
			(_format switch
			{
				ProjectContextDocumentFormat.Json => ".json",
				ProjectContextDocumentFormat.Xml => ".xml",
				ProjectContextDocumentFormat.Text => ".txt",
				_ => ".md"
			}));
		var destination = Prompt(
			L("Terminal.Tui.ExportContext"),
			L("Terminal.Tui.Destination"),
			defaultPath);
		if (string.IsNullOrWhiteSpace(destination))
			return;

		_activeOperationTask = RunExportWorkflowAsync(
			L("Terminal.Tui.ExportContext"),
			token => _controller.PrepareContextExportAsync(
				_state,
				_previewView,
				_format,
				destination,
				overwrite: false,
				token),
			async (_, token) => await _controller.ExportContextAsync(
				_state,
				_previewView,
				_format,
				destination,
				overwrite: false,
				token,
				plain: _options.Plain).ConfigureAwait(false),
			(exactDestination, dryRun) =>
				TerminalWorkspaceController.BuildEquivalentContextCommand(
					_state,
					_previewView,
					_format,
					exactDestination,
					dryRun));
	}

	private void ExportProject(ProjectCopyExportFormat? requestedFormat = null)
	{
		if (_state is null)
			return;
		var kind = requestedFormat ?? SelectProjectExportFormat();
		if (kind is null)
			return;
		var selectedKind = kind.Value;
		if (_state.Plan.Selection.HideSecrets == true &&
		    !Confirm(
			    L("Dialog.ProjectCopy.Redaction.Title"),
			    L("Dialog.ProjectCopy.Redaction.Message")))
		{
			return;
		}
		var defaultPath = BuildDefaultExportPath(
			_state.Plan.SourceRoot,
			Directory.GetCurrentDirectory(),
			selectedKind == ProjectCopyExportFormat.Zip
				? $"{GetProjectDisplayName(_state.Plan)}.zip"
				: $"{GetProjectDisplayName(_state.Plan)}-export");
		var destination = Prompt(
			L("Terminal.Tui.ExportProject"),
			L("Terminal.Tui.ExactDestination"),
			defaultPath);
		if (string.IsNullOrWhiteSpace(destination))
			return;

		_activeOperationTask = RunExportWorkflowAsync(
			L("Terminal.Tui.ExportProject"),
			token => _controller.PrepareProjectExportAsync(
				_state,
				selectedKind,
				destination,
				token),
			async (progress, token) => await _controller.ExportProjectAsync(
				_state,
				selectedKind,
				destination,
				token,
				progress).ConfigureAwait(false),
			(exactDestination, dryRun) => TerminalWorkspaceController.BuildEquivalentProjectCommand(
				_state,
				selectedKind,
				exactDestination,
				dryRun));
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

	private void SaveProfile()
	{
		if (_state is null)
			return;
		var defaultPath = BuildDefaultExportPath(
			_state.Plan.SourceRoot,
			Directory.GetCurrentDirectory(),
			"devprojex-profile.json");
		var destination = Prompt(
			L("Terminal.Tui.SaveProfile"),
			L("Terminal.Tui.ProfileDestination"),
			defaultPath);
		if (string.IsNullOrWhiteSpace(destination))
			return;
		_activeOperationTask = RunOperationAsync(
			L("Terminal.Tui.SaveProfile"),
			async token => await _controller.SavePortableProfileAsync(
				_state,
				destination,
				overwrite: false,
				token).ConfigureAwait(false));
	}

	private void ChangeGitMode()
	{
		if (_state is null)
			return;
		var mode = SelectGitMode(_state.Plan.GitReadiness.Mode);
		if (mode is null || mode == _state.Plan.GitReadiness.Mode)
			return;
		_activeOperationTask = RunOperationAsync(
			L("Terminal.Tui.GitFiltering"),
			async token =>
			{
				await _controller.SetGitModeAsync(_state, mode.Value, token).ConfigureAwait(false);
				return null;
			});
	}

	private void ChangeExclusions()
	{
		if (_state is null)
			return;
		var exclusions = SelectExclusions(_state.Plan.Selection.Exclusions ?? []);
		if (exclusions is null)
			return;
		_activeOperationTask = RunOperationAsync(
			L("Terminal.Tui.Exclusions"),
			async token =>
			{
				await _controller.SetExclusionsAsync(_state, exclusions, token).ConfigureAwait(false);
				return null;
			});
	}

	private void ChangeRootsOrTypes(bool roots)
	{
		if (_state is null)
			return;
		var available = roots
			? _state.Plan.AvailableRoots
			: _state.Plan.AvailableExtensions;
		var selected = roots
			? _state.Plan.SelectedRoots
			: _state.Plan.SelectedExtensions;
		var values = SelectValues(
			roots ? L("Terminal.Tui.RootFolders") : L("Terminal.Tui.FileTypes"),
			available,
			selected);
		if (values is null)
			return;

		_activeOperationTask = RunOperationAsync(
			roots ? L("Terminal.Tui.RootFolders") : L("Terminal.Tui.FileTypes"),
			async token =>
			{
				if (roots)
					await _controller.SetRootsAsync(_state, values, token).ConfigureAwait(false);
				else
					await _controller.SetExtensionsAsync(_state, values, token).ConfigureAwait(false);
				return null;
			});
	}

	private async Task RunOperationAsync(
		string operationName,
		Func<CancellationToken, Task<string?>> operation,
		Func<string, string>? equivalentCommand = null)
	{
		if (!await _operationGate.WaitAsync(0, _sessionCts.Token).ConfigureAwait(false))
			return;
		var operationCts = ReplaceActiveOperation();
		try
		{
			SetWorkspaceBusy(
				operationName,
				L("Terminal.Tui.Progress.Working"));
			var result = await operation(operationCts.Token).ConfigureAwait(false);
			if (_stopping)
				return;
			await InvokeAsync(() =>
			{
				MarkActiveOperationFinished(operationCts);
				RefreshWorkspace();
				SchedulePreviewRefresh();
				SetWorkspaceBusy(null);
				if (!string.IsNullOrWhiteSpace(result))
				{
					var message = equivalentCommand is null
						? result
						: $"{result}\n\n{L("Terminal.Tui.EquivalentCommand")}:\n{equivalentCommand(result)}";
					ShowNotice(operationName, message, TerminalWorkspaceTheme.Success);
				}
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
			await ShowFailureAsync(
				"DPX-EXPORT-DESTINATION-EXISTS",
				L("Terminal.Tui.Error.DestinationExists")).ConfigureAwait(false);
		}
		catch (ProjectCopyExportException exception)
		{
			var error = ProjectCopyTerminalErrorMapper.Map(exception, _services.Localization);
			await ShowFailureAsync(error.Code, error.Message).ConfigureAwait(false);
		}
		catch (ProjectContextValidationException exception)
		{
			await ShowFailureAsync(
				exception.Code,
				ResolveValidationErrorMessage(exception.Code)).ConfigureAwait(false);
		}
		catch (PortableProjectProfileException exception)
		{
			var message = exception.Code == "DPX-PROFILE-DESTINATION-EXISTS"
				? L("Terminal.Error.ProfileDestinationExists")
				: L("Terminal.Error.ProfileWriteFailed");
			await ShowFailureAsync(exception.Code, message).ConfigureAwait(false);
		}
		catch
		{
			await ShowFailureAsync(
				"DPX-TUI-OPERATION-FAILED",
				L("Terminal.Tui.Error.OperationFailed")).ConfigureAwait(false);
		}
		finally
		{
			ReleaseActiveOperation(operationCts);
			_operationGate.Release();
		}
	}

	private async Task RunExportWorkflowAsync(
		string operationName,
		Func<CancellationToken, Task<TerminalExportSummary>> prepare,
		Func<IProgress<ProjectCopyExportProgress>, CancellationToken, Task<string>> export,
		Func<string, bool, string> equivalentCommand)
	{
		if (!await _operationGate.WaitAsync(0, _sessionCts.Token).ConfigureAwait(false))
			return;
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

			var command = equivalentCommand(
				summary.Destination,
				decision == TerminalExportDecision.DryRun);
			if (decision == TerminalExportDecision.DryRun)
			{
				await InvokeAsync(() =>
				{
					RefreshWorkspace();
					ShowNotice(
						operationName,
						$"{L("Terminal.Tui.DryRunReady")}\n\n" +
						$"{L("Terminal.Tui.EquivalentCommand")}:\n{command}",
						TerminalWorkspaceTheme.Success);
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
			var exportStopwatch = Stopwatch.StartNew();
			var progress = new SynchronousProgress<ProjectCopyExportProgress>(value =>
			{
				_application.Invoke(() =>
				UpdateMeasuredProgress(
					value,
					summary.Kind,
					progressPhase));
				_operationObserver.ObserveProgress(value, operationCts.Token);
			});
			var result = await export(progress, operationCts.Token).ConfigureAwait(false);
			exportStopwatch.Stop();
			if (_stopping)
				return;
			await InvokeAsync(() =>
			{
				MarkActiveOperationFinished(operationCts);
				SetWorkspaceBusy(null);
				RefreshWorkspace();
				SchedulePreviewRefresh();
				ShowNotice(
					operationName,
					BuildExportCompletion(
						summary,
						result,
						command,
						exportStopwatch.Elapsed),
					TerminalWorkspaceTheme.Success,
					preferredWidth: 108,
					preferredHeight: 20);
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
			await ShowFailureAsync(
				"DPX-EXPORT-DESTINATION-EXISTS",
				L("Terminal.Tui.Error.DestinationExists")).ConfigureAwait(false);
		}
		catch (ProjectCopyExportException exception)
		{
			var error = ProjectCopyTerminalErrorMapper.Map(exception, _services.Localization);
			await ShowFailureAsync(error.Code, error.Message).ConfigureAwait(false);
		}
		catch (ProjectContextValidationException exception)
		{
			await ShowFailureAsync(
				exception.Code,
				ResolveValidationErrorMessage(exception.Code)).ConfigureAwait(false);
		}
		catch
		{
			await ShowFailureAsync(
				"DPX-TUI-OPERATION-FAILED",
				L("Terminal.Tui.Error.OperationFailed")).ConfigureAwait(false);
		}
		finally
		{
			ReleaseActiveOperation(operationCts);
			_operationGate.Release();
		}
	}

	private string BuildExportCompletion(
		TerminalExportSummary summary,
		string destination,
		string equivalentCommand,
		TimeSpan elapsed)
	{
		var destinationLabel = $"{L("Terminal.Tui.Destination").TrimEnd(':')}: ";
		var dialogWidth = Math.Min(108, Math.Max(12, _terminalWidth - 4));
		var bodyWidth = Math.Max(20, dialogWidth - 6);
		var destinationWidth = Math.Max(8, bodyWidth - destinationLabel.GetColumns());
		var displayDestination = FitPathToWidth(destination, destinationWidth);
		return
			$"{destinationLabel}{displayDestination}\n" +
			$"{L("Terminal.Analysis.Files")}: {summary.FileCount:N0}  |  " +
			$"{L("Terminal.Analysis.Folders")}: {summary.FolderCount:N0}  |  " +
			$"{L("Terminal.Analysis.Size")}: {TerminalWorkspace.FormatBytes(summary.Bytes)}\n" +
			$"{FormatElapsed(elapsed)}\n\n" +
			$"{L("Terminal.Tui.EquivalentCommand")}:\n{equivalentCommand}";
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
			"DPX-PROJECT-NOT-FOUND" or "DPX-PROJECT-PATH-INVALID" =>
				L("Terminal.Tui.Error.ProjectUnavailable"),
			_ => L("Terminal.Tui.Error.InvalidOperation")
		};

	private void SetWorkspaceBusy(string? operationName, string? phase = null)
	{
		_application.Invoke(() =>
		{
			var busy = !string.IsNullOrWhiteSpace(operationName);
			if (_tree is not null)
				_tree.Enabled = !busy;
			if (_preview is not null)
				_preview.Enabled = !busy;
			if (_controls is not null)
				_controls.Enabled = !busy;
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
			if (_status is not null)
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
				RestoreScreenFocus();
				UpdateWorkspaceFocus();
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
		if (_screen == TerminalWorkspaceScreen.Workspace && _status is not null)
		{
			_status.Text = text;
			_status.SchemeName = schemeName;
		}
		else if (_screen == TerminalWorkspaceScreen.Welcome)
		{
			ShowWelcomeStatus(text, schemeName);
		}
	}

	private void ShowWelcomeStatus(string text, string schemeName)
	{
		if (_welcomeQuickStart is null)
			return;
		_welcomeQuickStart.Text = string.IsNullOrWhiteSpace(text)
			? L("Terminal.Tui.Welcome.QuickStart")
			: text;
		_welcomeQuickStart.SchemeName = schemeName;
	}

	private void ShowWelcomeStatusSafe(string text, string schemeName)
	{
		if (_stopping)
			return;
		_application.Invoke(() => ShowWelcomeStatus(text, schemeName));
	}

	private void ScheduleSelectionProjection()
	{
		if (_state is null)
			return;
		var state = _state;
		var sourcePlan = state.Plan;
		var selectedPaths = state.BuildSelectedRelativePaths();
		var expectedRevision = state.Revision;
		CancelAndDispose(ref _projectionCts);
		CancelAndDispose(ref _previewCts);
		var projectionRequestId = Interlocked.Increment(ref _projectionRequestId);
		Interlocked.Increment(ref _previewRequestId);
		_projectionCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
		var operationCts = _projectionCts;
		_projectionTask = Task.Run(async () =>
		{
			try
			{
				await Task.Delay(180, operationCts.Token).ConfigureAwait(false);
				var plan = await _controller.BuildReprojectedPlanAsync(
						sourcePlan,
						selectedPaths,
						operationCts.Token)
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
				    ReferenceEquals(_projectionCts, operationCts))
				{
					_application.Invoke(() => ShowError(
						"DPX-TUI-PREVIEW-FAILED",
						L("Terminal.Tui.Error.PreviewFailed")));
				}
			}
		}, CancellationToken.None);
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
		CancelAndDispose(ref _previewCts);
		var requestId = Interlocked.Increment(ref _previewRequestId);
		_previewCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
		var operationCts = _previewCts;
		_previewTask = Task.Run(async () =>
		{
			IPreviewTextDocument? pendingDocument = null;
			try
			{
				pendingDocument = await _controller.BuildPreviewDocumentAsync(
						state,
						view,
						format,
						operationCts.Token,
						plain: _options.Plain)
					.ConfigureAwait(false);
				var document = pendingDocument;
				var applied = await InvokeAsync(() =>
				{
					if (!IsCurrentPreviewRequest(state, operationCts, requestId) ||
					    !state.TrySetPreviewDocument(document, expectedRevision))
					{
						return false;
					}

					RefreshWorkspace();
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
				    ReferenceEquals(_previewCts, operationCts))
				{
					_application.Invoke(() => ShowError(
						"DPX-TUI-PREVIEW-FAILED",
						L("Terminal.Tui.Error.PreviewFailed")));
				}
			}
			finally
			{
				pendingDocument?.Dispose();
			}
		}, CancellationToken.None);
	}

	private void CancelWorkspaceRefreshes()
	{
		Interlocked.Increment(ref _projectionRequestId);
		Interlocked.Increment(ref _previewRequestId);
		CancelAndDispose(ref _projectionCts);
		CancelAndDispose(ref _previewCts);
		CancelPreviewSearch(clearQuery: false);
	}

	private bool IsCurrentProjectionRequest(
		TerminalWorkspaceState state,
		CancellationTokenSource operationCts,
		long requestId) =>
		!_stopping &&
		ReferenceEquals(_state, state) &&
		ReferenceEquals(_projectionCts, operationCts) &&
		Volatile.Read(ref _projectionRequestId) == requestId;

	private bool IsCurrentPreviewRequest(
		TerminalWorkspaceState state,
		CancellationTokenSource operationCts,
		long requestId) =>
		!_stopping &&
		ReferenceEquals(_state, state) &&
		ReferenceEquals(_previewCts, operationCts) &&
		Volatile.Read(ref _previewRequestId) == requestId;

	private void MovePane(TerminalPaneNavigation navigation)
	{
		if (_tree is null || _preview is null || _controls is null)
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
		var lastRow = Math.Max(0, _preview.LineCount - 1);
		var pageSize = Math.Max(1, _preview.PageSize - 1);
		var row = Math.Clamp(_preview.FirstVisibleLine, 0, lastRow);
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

		RestorePreviewViewport(row, column);
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
		var rows = new ObservableCollection<TerminalPathPickerEntry>();
		var list = new ListView
		{
			X = 1,
			Y = 3,
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
			list.SetFocus();
		}

		void OpenSelection()
		{
			if (mode == TerminalPathPickerMode.Directory)
			{
				selectedPath = model.SelectCurrentDirectory();
				if (selectedPath is not null)
					_application.RequestStop(dialog);
				return;
			}

			var selected = model.SelectEntry(list.SelectedItem ?? -1);
			if (selected is not null && File.Exists(selected))
			{
				selectedPath = selected;
				_application.RequestStop(dialog);
				return;
			}

			ActivateCurrent();
		}

		list.Accepted += (_, _) => ActivateCurrent();
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
		};
		dialog.Add(locationTitle, location, list, status);
		var back = CreateDialogButton(L("Terminal.Tui.Back"));
		back.Accepted += (_, _) =>
		{
			var parent = model.Entries.FirstOrDefault(static entry => entry.IsParent);
			if (parent is null)
				return;
			model.Open(parent.Path);
			Refresh();
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

	private GitFilteringMode? SelectGitMode(GitFilteringMode current)
	{
		var modes = ProjectPresentationCatalog.GitFiltering
			.Select(descriptor => (descriptor.Id, Label: L(descriptor.LabelKey)))
			.ToArray();
		var rows = new ObservableCollection<string>(
			modes.Select(mode => $"{(mode.Id == current ? "(*)" : "( )")} {mode.Label}"));
		using var dialog = CreateDialog(L("Terminal.Tui.GitFiltering"), 66, 12);
		var description = new TerminalLiteralLabel
		{
			X = 1,
			Y = 0,
			Width = Dim.Fill(1),
			Text = L("Terminal.Tui.GitModePrompt"),
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		var list = new ListView
		{
			X = 1,
			Y = 2,
			Width = Dim.Fill(1),
			Height = 4,
			SchemeName = TerminalWorkspaceTheme.List
		};
		list.SetSource(rows);
		list.SelectedItem = Array.FindIndex(modes, mode => mode.Id == current);
		GitFilteringMode? selected = null;
		void SelectCurrent()
		{
			if (list.SelectedItem is not { } index || index < 0 || index >= modes.Length)
				return;
			selected = modes[index].Id;
			_application.RequestStop(dialog);
		}
		list.Accepted += (_, _) => SelectCurrent();
		dialog.Add(description, list);
		dialog.AddButton(CreateDialogButton(L("Terminal.Tui.Cancel")));
		var apply = CreateDialogButton(L("Terminal.Tui.Accept"));
		apply.Accepted += (_, _) =>
		{
			if (list.SelectedItem is { } index && index >= 0 && index < modes.Length)
				selected = modes[index].Id;
		};
		dialog.AddButton(apply);
		RunOverlay(dialog, list);
		return dialog.Result == 1 || selected is not null ? selected : null;
	}

	private IReadOnlyCollection<ProjectExclusion>? SelectExclusions(
		IReadOnlyCollection<ProjectExclusion> current)
	{
		var available = ProjectPresentationCatalog.Exclusions
			.Select(descriptor => (Id: descriptor.RequireId(), Label: L(descriptor.LabelKey)))
			.ToArray();
		var labelsById = available.ToDictionary(
			static item => item.Id,
			static item => item.Label);
		var idsByLabel = available.ToDictionary(
			static item => item.Label,
			static item => item.Id,
			StringComparer.CurrentCulture);
		var values = SelectValues(
			L("Terminal.Tui.Exclusions"),
			available.Select(static item => item.Label).ToArray(),
			current.Where(labelsById.ContainsKey).Select(exclusion => labelsById[exclusion]).ToArray());
		return values?.Select(value => idsByLabel[value]).ToArray();
	}

	private IReadOnlyCollection<string>? SelectValues(
		string title,
		IReadOnlyList<string> available,
		IReadOnlyList<string> selected)
	{
		if (available.Count == 0)
		{
			ShowNotice(title, L("Terminal.Tui.NoneAvailable"), TerminalWorkspaceTheme.Warning);
			return null;
		}

		var height = Math.Clamp(available.Count + 11, 12, Math.Max(12, _application.Screen.Height - 4));
		using var dialog = CreateDialog(title, 74, height);
		var hint = new TerminalLiteralLabel
		{
			X = 1,
			Y = 0,
			Width = Dim.Fill(1),
			Text = L("Terminal.Tui.MultiSelectHint"),
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		var source = new ObservableCollection<string>(
			available.Distinct(StringComparer.OrdinalIgnoreCase));
		var list = new ListView
		{
			X = 1,
			Y = 2,
			Width = Dim.Fill(1),
			Height = Dim.Fill(3),
			ShowMarks = true,
			MarkMultiple = true,
			SchemeName = TerminalWorkspaceTheme.List
		};
		list.SetSource(source);
		var selectedSet = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
		for (var index = 0; index < source.Count; index++)
			list.Source?.SetMark(index, selectedSet.Contains(source[index]));

		var toggleAll = CreateDialogButton(L("Terminal.Tui.ToggleAll"));
		toggleAll.X = 1;
		toggleAll.Y = Pos.AnchorEnd(2);
		toggleAll.SchemeName = TerminalWorkspaceTheme.List;
		toggleAll.Accepted += (_, _) =>
		{
			var markAll = list.GetAllMarkedItems().Count() != source.Count;
			list.MarkAll(markAll);
			list.SetNeedsDraw();
		};
		dialog.Add(hint, list, toggleAll);
		dialog.AddButton(CreateDialogButton(L("Terminal.Tui.Cancel")));
		dialog.AddButton(CreateDialogButton(L("Terminal.Tui.Accept")));
		RunOverlay(dialog, list);
		if (dialog.Result != 1)
			return null;
		return list.GetAllMarkedItems()
			.Where(index => index >= 0 && index < source.Count)
			.Select(index => source[index])
			.ToArray();
	}

	private ProjectCopyExportFormat? SelectProjectExportFormat()
	{
		var result = ShowChoice(
			L("Terminal.Tui.ExportProject"),
			L("Terminal.Tui.OutputKindPrompt"),
			L("Terminal.Tui.Cancel"),
			L("Terminal.Tui.Folder"),
			"ZIP");
		return result switch
		{
			1 => ProjectCopyExportFormat.Folder,
			2 => ProjectCopyExportFormat.Zip,
			_ => null
		};
	}

	private TerminalExportDecision ShowExportSummary(TerminalExportSummary summary)
	{
		var text = _workspace.BuildExportSummaryText(summary);
		if (summary.DestinationState == TerminalExportDestinationState.Conflict)
		{
			ShowError("DPX-EXPORT-DESTINATION-EXISTS", text);
			return TerminalExportDecision.Cancel;
		}

		var selected = ShowChoice(
			L("Terminal.Tui.ExportSummary"),
			text,
			L("Terminal.Tui.Cancel"),
			L("Terminal.Tui.DryRun"),
			L("Terminal.Tui.Export"));
		return selected switch
		{
			1 => TerminalExportDecision.DryRun,
			2 => TerminalExportDecision.Export,
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

	private void ShowError(string code, string message) =>
		ShowScrollableOverlay(
			$"{L("Terminal.Tui.Error")} [{code}]",
			message,
			TerminalWorkspaceTheme.Error,
			82,
			18);

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
				_controls?.SetFocus();
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
	{
		CancelActiveOperation();
		var operationCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
		_activeOperationCts = operationCts;
		return operationCts;
	}

	private void ReleaseActiveOperation(CancellationTokenSource operationCts)
	{
		if (ReferenceEquals(_activeOperationCts, operationCts))
			_activeOperationCts = null;
		operationCts.Dispose();
	}

	private void MarkActiveOperationFinished(CancellationTokenSource operationCts)
	{
		if (ReferenceEquals(_activeOperationCts, operationCts))
			_activeOperationCts = null;
	}

	private void CancelActiveOperation() =>
		_activeOperationCts?.Cancel();

	private bool HasActiveOperation =>
		_activeOperationCts is { IsCancellationRequested: false };

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
		_sessionCts.Cancel();
		CancelActiveOperation();
		CancelWorkspaceRefreshes();
		_application.RequestStop(_root);
	}

	private void ClearRoot()
	{
		DismissOperationProgress();
		var previousState = _state;
		_state = null;
		_tree = null;
		_preview = null;
		_previewRange = null;
		_treeFrame = null;
		_previewFrame = null;
		_controls = null;
		_controlsFrame = null;
		_controlRows = null;
		_welcomeList = null;
		_welcomeRows = null;
		_welcomeDetail = null;
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
			return displayName;
		if (plan is null)
			return string.Empty;
		var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(plan.SourceRoot));
		return string.IsNullOrWhiteSpace(name) ? plan.SourceRoot : name;
	}

	private string BuildWorkspaceHeading(ProjectContextPlan plan)
	{
		var branch = plan.SourceIdentity?.Branch is { Length: > 0 } value
			? $" [{value}]"
			: string.Empty;
		return $"DevProjex Terminal{PanelSeparator}{GetProjectDisplayName(plan)}{branch}";
	}

	private static string GetProjectDisplaySource(ProjectContextPlan plan) =>
		plan.SourceIdentity?.SourceReference is { Length: > 0 } sourceReference
			? sourceReference
			: plan.SourceRoot;

	private static void CancelAndDispose(ref CancellationTokenSource? source)
	{
		if (source is null)
			return;
		source.Cancel();
		source.Dispose();
		source = null;
	}

	private string PanelSeparator => _options.Plain ? " | " : " · ";

	private string L(string key)
	{
		var value = _workspace.L(key);
		return _options.Plain ? TerminalPlainText.Normalize(value) : value;
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_application.Keyboard.KeyDown -= OnRootKeyDown;
		_sessionCts.Cancel();
		CancelAndDispose(ref _activeOperationCts);
		CancelAndDispose(ref _projectionCts);
		CancelAndDispose(ref _previewCts);
		CancelAndDispose(ref _previewSearchCts);
		DismissOperationProgress();
		_sessionCts.Dispose();
		_operationGate.Dispose();
	}

	private enum TerminalWorkspaceScreen
	{
		TooSmall,
		Welcome,
		Loading,
		Workspace
	}

	private enum TerminalWorkspacePane
	{
		Tree,
		Preview,
		Controls
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
