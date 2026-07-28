using System.Collections.ObjectModel;
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

internal sealed class TerminalWorkspaceSession : IDisposable
{
	private readonly IApplication _application;
	private readonly Window _root;
	private readonly TerminalServices _services;
	private readonly ITerminalEnvironment _environment;
	private readonly TerminalWorkspaceOptions _options;
	private readonly TerminalWorkspace _workspace;
	private readonly TerminalWorkspaceController _controller;
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
	private Task? _openTask;
	private Task? _activeOperationTask;
	private Task? _projectionTask;
	private Task? _previewTask;

	private TerminalWelcomeContext? _welcomeContext;
	private ObservableCollection<TerminalWelcomeActionRow>? _welcomeRows;
	private ListView? _welcomeList;
	private TextView? _welcomeDetail;
	private FrameView? _welcomeActionsFrame;
	private FrameView? _welcomeDetailFrame;
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
	private ListView? _tree;
	private TextView? _preview;
	private FrameView? _treeFrame;
	private FrameView? _previewFrame;
	private Label? _workspaceHeading;
	private Label? _workspacePath;
	private Label? _workspaceContext;
	private Label? _status;
	private Label? _footer;
	private TerminalWorkspacePane _activePane = TerminalWorkspacePane.Tree;
	private ProjectContextView _previewView = ProjectContextView.TreeContent;
	private ProjectContextDocumentFormat _format = ProjectContextDocumentFormat.Markdown;
	private string? _searchQuery;
	private string? _selectedTreePath;

	public TerminalWorkspaceSession(
		IApplication application,
		Window root,
		TerminalServices services,
		ITerminalEnvironment environment,
		TerminalWorkspaceOptions options,
		TerminalWorkspace workspace,
		CancellationToken cancellationToken)
	{
		_application = application;
		_root = root;
		_services = services;
		_environment = environment;
		_options = options;
		_workspace = workspace;
		_controller = new TerminalWorkspaceController(services, environment);
		_sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var initialScreen = _application.Driver?.Screen ?? _application.Screen;
		_terminalWidth = Math.Max(_environment.Width, initialScreen.Width);
		_terminalHeight = Math.Max(_environment.Height, initialScreen.Height);
		_application.Keyboard.KeyDown += OnRootKeyDown;
		_application.ScreenChanged += (_, _) =>
		{
			if (_application.AppModel != AppModel.Inline)
			{
				var screen = _application.Driver?.Screen ?? _application.Screen;
				UpdateTerminalSize(screen.Width, screen.Height);
			}
			ApplyCurrentLayout();
		};
		if (_application.Driver is { } driver)
		{
			driver.SizeChanged += (_, args) => _application.Invoke(() =>
			{
				if (_application.AppModel == AppModel.Inline && args.Size is { } size)
				{
					_root.Width = size.Width;
					_root.Height = size.Height;
				}
				if (args.Size is { } terminalSize)
					UpdateTerminalSize(terminalSize.Width, terminalSize.Height);
				ApplyCurrentLayout();
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

		var pending = new[] { _openTask, _activeOperationTask, _projectionTask, _previewTask }
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
		_tooSmall = new Label
		{
			X = Pos.Center(),
			Y = Pos.Center(),
			Width = Dim.Auto(),
			Text = L("Terminal.Tui.Error.Resize"),
			SchemeName = TerminalWorkspaceTheme.Warning
		};
		_root.Add(_tooSmall);
	}

	private void ShowWelcome()
	{
		CancelWorkspaceRefreshes();
		ClearRoot();
		_screen = TerminalWorkspaceScreen.Welcome;
		_layoutMode = ResolveLayout();
		_welcomeContext = LoadWelcomeContext();
		_welcomeRows = new ObservableCollection<TerminalWelcomeActionRow>(
			BuildWelcomeActions(_welcomeContext).Select(static action => new TerminalWelcomeActionRow(action)));
		if (_welcomeRows.Count > 0)
			_welcomeRows[0].IsSelected = true;

		_welcomeHeading = new Label
		{
			X = 2,
			Y = 1,
			Text = "DevProjex Terminal",
			SchemeName = TerminalWorkspaceTheme.Accent
		};
		var versionText = $"v{GetProductVersion()}";
		_welcomeVersion = new Label
		{
			X = Pos.AnchorEnd(versionText.Length + 2),
			Y = 1,
			Width = versionText.Length,
			Text = versionText,
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		_welcomeTagline = new Label
		{
			X = 2,
			Y = 2,
			Width = Dim.Fill(2),
			Text = L("Terminal.Tui.Welcome.Description"),
			SchemeName = TerminalWorkspaceTheme.Base
		};
		_welcomeCurrentTitle = new Label
		{
			X = 2,
			Y = 4,
			Text = L("Terminal.Tui.CurrentDirectory"),
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		_welcomeCurrentPath = new Label
		{
			X = 2,
			Y = 5,
			Width = Dim.Fill(33),
			Height = 1,
			Text = _welcomeContext.CurrentDirectory,
			SchemeName = TerminalWorkspaceTheme.Base
		};
		_welcomeCurrentStatus = new Label
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

		_welcomeActionsFrame = new FrameView
		{
			Title = L("Terminal.Tui.Actions"),
			SchemeName = TerminalWorkspaceTheme.FocusedPanel,
			BorderStyle = LineStyle.Single
		};
		_welcomeList = new ListView
		{
			X = 0,
			Y = 0,
			Width = Dim.Fill(),
			Height = Dim.Fill(),
			ShowMarks = false,
			SchemeName = TerminalWorkspaceTheme.List
		};
		_welcomeList.SetSource(_welcomeRows);
		if (_welcomeRows.Count > 0)
			_welcomeList.SelectedItem = 0;
		_welcomeList.ValueChanged += (_, _) => UpdateWelcomeSelection();
		// Defer nested workflows until the Enter key that accepted the row has finished dispatching.
		_welcomeList.Accepted += (_, _) => _application.Invoke(ActivateWelcomeSelection);
		_welcomeActionsFrame.Add(_welcomeList);

		_welcomeDetailFrame = new FrameView
		{
			Title = L("Terminal.Tui.Details"),
			SchemeName = TerminalWorkspaceTheme.Panel,
			BorderStyle = LineStyle.Single
		};
		_welcomeDetail = new TextView
		{
			X = 1,
			Y = 0,
			Width = Dim.Fill(1),
			Height = Dim.Fill(),
			ReadOnly = true,
			WordWrap = true,
			CanFocus = false,
			SchemeName = TerminalWorkspaceTheme.Base
		};
		_welcomeDetailFrame.Add(_welcomeDetail);
		_welcomeQuickStart = new Label
		{
			X = 2,
			Width = Dim.Fill(2),
			SchemeName = TerminalWorkspaceTheme.Secondary,
			Text = L("Terminal.Tui.Welcome.QuickStart")
		};
		_welcomeFooter = new Label
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
	}

	private TerminalWelcomeContext LoadWelcomeContext()
	{
		var recent = _services.RecentProjectsStore.LoadForStartup(TimeSpan.FromMilliseconds(200))
			.RecentFolders
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
			TerminalWelcomeActionKind.RecentProjects,
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
				TerminalWelcomeActionKind.OpenProfile,
				L("Terminal.Tui.Welcome.OpenProfile"),
				L("Terminal.Tui.Welcome.OpenProfile.Description")),
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
		var detail = action.Description;
		if (action.Kind == TerminalWelcomeActionKind.OpenCurrent && _welcomeContext is not null)
			detail = $"{detail}\n\n{_welcomeContext.CurrentDirectory}";
		_welcomeDetail.Text = detail;
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
			case TerminalWelcomeActionKind.RecentProjects:
				OpenRecentProject();
				break;
			case TerminalWelcomeActionKind.BrowseFolder:
				BrowseForProject();
				break;
			case TerminalWelcomeActionKind.CloneRepository:
				BeginCloneRepository();
				break;
			case TerminalWelcomeActionKind.OpenProfile:
				OpenPortableProfile();
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

	private void OpenRecentProject()
	{
		if (_welcomeContext is null)
			return;
		if (_welcomeContext.RecentProjects.Count == 0)
		{
			ShowNotice(
				L("Terminal.Tui.Welcome.Recent"),
				L("Terminal.Tui.NoneAvailable"),
				TerminalWorkspaceTheme.Warning);
			return;
		}

		var selected = SelectFromList(
			L("Terminal.Tui.Welcome.Recent"),
			L("Terminal.Tui.Welcome.RecentDescription"),
			_welcomeContext.RecentProjects);
		if (selected is not null)
			BeginOpenProject(selected, ResolveAutomaticProfile(selected));
	}

	private void BrowseForProject()
	{
		var path = SelectPath(
			L("Terminal.Tui.Welcome.Browse"),
			OpenMode.Directory,
			_welcomeContext?.CurrentDirectory);
		if (path is null)
			return;
		if (!TryResolveDirectory(path, out var project))
		{
			ShowError("DPX-TUI-PROJECT-UNAVAILABLE", L("Terminal.Tui.Error.ProjectUnavailable"));
			return;
		}

		BeginOpenProject(project, ResolveAutomaticProfile(project));
	}

	private void OpenPortableProfile()
	{
		var profilePath = SelectPath(
			L("Terminal.Tui.Welcome.OpenProfile"),
			OpenMode.File,
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
			OpenMode.Directory,
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

	private void BeginCloneRepository()
	{
		var url = Prompt(
			L("Terminal.Tui.Welcome.Clone"),
			L("Terminal.Tui.RepositoryUrl"),
			string.Empty);
		if (string.IsNullOrWhiteSpace(url))
			return;

		ShowLoading(L("Terminal.Tui.CloningRepository"), L("Terminal.Tui.CancelOperation"));
		var operationCts = ReplaceActiveOperation();
		_activeOperationTask = Task.Run(async () =>
		{
			string? target = null;
			try
			{
				target = _services.RepoCacheService.CreateRepositoryDirectory(url);
				var result = await _services.GitRepositoryService
					.CloneAsync(url, target, cancellationToken: operationCts.Token)
					.ConfigureAwait(false);
				if (!result.Success || !Directory.Exists(result.LocalPath))
				throw new TerminalWorkspaceOperationException("DPX-TUI-CLONE-FAILED");

				await OpenProjectCoreAsync(
						result.LocalPath,
						ResolveAutomaticProfile(result.LocalPath),
						operationCts,
						releaseOperation: false)
					.ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
			{
				if (target is not null)
					_services.RepoCacheService.DeleteRepositoryDirectory(target);
				ReturnToWelcomeAfterCancellation(operationCts);
			}
			catch
			{
				if (target is not null)
					_services.RepoCacheService.DeleteRepositoryDirectory(target);
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

	private ProjectProfileReference ResolveAutomaticProfile(string projectPath) =>
		_services.LocalProfileStore.TryLoadProfile(projectPath, out _)
			? ProjectProfileReference.Local
			: ProjectProfileReference.Standard;

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

	private void BeginOpenProject(string projectPath, ProjectProfileReference profile)
	{
		ShowLoading(L("Terminal.Tui.LoadingProject"), projectPath);
		var operationCts = ReplaceActiveOperation();
		_openTask = Task.Run(
			() => OpenProjectCoreAsync(projectPath, profile, operationCts),
			CancellationToken.None);
	}

	private async Task OpenProjectCoreAsync(
		string projectPath,
		ProjectProfileReference profile,
		CancellationTokenSource operationCts,
		bool releaseOperation = true)
	{
		try
		{
			var state = await _controller
				.OpenAsync(projectPath, profile, operationCts.Token)
				.ConfigureAwait(false);
			if (_stopping || operationCts.IsCancellationRequested)
				return;
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
			ReturnToWelcomeWithError(
				operationCts,
				exception.Code,
				L("Terminal.Error.ProfileInvalid"));
		}
		catch (ProjectContextValidationException exception)
		{
			ReturnToWelcomeWithError(
				operationCts,
				exception.Code,
				ResolveValidationErrorMessage(exception.Code));
		}
		catch
		{
			ReturnToWelcomeWithError(
				operationCts,
				"DPX-TUI-PROJECT-OPEN-FAILED",
				L("Terminal.Tui.Error.ProjectUnavailable"));
		}
		finally
		{
			if (releaseOperation)
				ReleaseActiveOperation(operationCts);
		}
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
		var heading = new Label
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
			AutoSpin = true,
			SchemeName = TerminalWorkspaceTheme.Accent
		};
		var operation = new Label
		{
			X = 6,
			Y = 4,
			Width = Dim.Fill(2),
			Text = title,
			SchemeName = TerminalWorkspaceTheme.Base
		};
		var details = new TextView
		{
			X = 6,
			Y = 6,
			Width = Dim.Fill(4),
			Height = 4,
			ReadOnly = true,
			WordWrap = true,
			CanFocus = false,
			Text = detail,
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		var footer = new Label
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
	}

	private void ShowWorkspace(TerminalWorkspaceState state)
	{
		CancelWorkspaceRefreshes();
		ClearRoot();
		_screen = TerminalWorkspaceScreen.Workspace;
		_state = state;
		_layoutMode = ResolveLayout();
		_activePane = TerminalWorkspacePane.Tree;

		_workspaceHeading = new Label
		{
			X = 1,
			Y = 0,
			Width = Dim.Fill(1),
			Height = 1,
			Text = $"DevProjex Terminal  {Path.GetFileName(state.Plan.SourceRoot)}",
			SchemeName = TerminalWorkspaceTheme.Accent
		};
		_workspacePath = new Label
		{
			X = 1,
			Y = 1,
			Width = Dim.Fill(1),
			Height = 1,
			Text = state.Plan.SourceRoot,
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		_workspaceContext = new Label
		{
			X = 1,
			Y = 2,
			Width = Dim.Fill(1),
			Height = 1,
			Text = BuildWorkspaceContext(state, _terminalWidth),
			SchemeName = TerminalWorkspaceTheme.Base
		};

		_treeFrame = new FrameView
		{
			Title = L("Terminal.Tui.Tree"),
			BorderStyle = LineStyle.Single,
			SchemeName = TerminalWorkspaceTheme.FocusedPanel
		};
		_previewFrame = new FrameView
		{
			BorderStyle = LineStyle.Single,
			SchemeName = TerminalWorkspaceTheme.Panel
		};
		_tree = new ListView
		{
			X = 0,
			Y = 0,
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
		_tree.HasFocusChanged += (_, _) => UpdateWorkspaceFocus();

		_preview = new TextView
		{
			X = 0,
			Y = 0,
			Width = Dim.Fill(),
			Height = Dim.Fill(),
			ReadOnly = true,
			WordWrap = false,
			Text = state.PreviewText,
			SchemeName = TerminalWorkspaceTheme.Base
		};
		_preview.HasFocusChanged += (_, _) => UpdateWorkspaceFocus();
		_treeFrame.Add(_tree);
		_previewFrame.Add(_preview);
		if (state.VisibleRows.Count > 0)
		{
			_tree.SelectedItem = 0;
			_selectedTreePath = state.VisibleRows[0].Node.FullPath;
		}

		_status = new Label
		{
			X = 1,
			Y = Pos.AnchorEnd(2),
			Width = Dim.Fill(1),
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		_footer = new Label
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
			_workspaceContext,
			_treeFrame,
			_previewFrame,
			_status,
			_footer,
			_tooSmall);

		_tree.SetFocus();
		RefreshWorkspace();
		ApplyWorkspaceLayout();
		UpdateWorkspaceFocus();
		SchedulePreviewRefresh();
	}

	private void RefreshWorkspace()
	{
		if (_state is null || _tree is null || _preview is null)
			return;

		var selected = FindSelectedTreeRow();
		var treeHadFocus = _tree.HasFocus;
		var previewHadFocus = _preview.HasFocus;
		if (_state.VisibleRows.Count > 0)
			_tree.SelectedItem = Math.Clamp(selected, 0, _state.VisibleRows.Count - 1);
		_preview.Text = _state.PreviewText;
		if (treeHadFocus)
			_tree.SetFocus();
		else if (previewHadFocus)
			_preview.SetFocus();
		if (_status is not null)
			_status.Text = BuildStatus(_state, _application.Screen.Width);
		if (_workspaceContext is not null)
			_workspaceContext.Text = BuildWorkspaceContext(_state, _terminalWidth);
		UpdatePreviewTitle();
		UpdateFooter();
	}

	private void TrackTreeSelection()
	{
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

	private string BuildWorkspaceContext(TerminalWorkspaceState state, int width)
	{
		var profile = state.Plan.Selection.ProfileSource?.Kind switch
		{
			ProjectProfileSourceKind.Local => L("Terminal.Profile.Local"),
			ProjectProfileSourceKind.Portable => L("Terminal.Profile.Portable"),
			_ => L("Terminal.Profile.Standard")
		};
		var gitMode = state.Plan.GitReadiness.Mode switch
		{
			GitFilteringMode.RespectGitIgnore => ".gitignore",
			GitFilteringMode.TrackedFilesOnly => L("Terminal.Tui.GitTracked"),
			_ => L("Terminal.Tui.GitNone")
		};
		if (width < 100)
		{
			var compactGitMode = state.Plan.GitReadiness.Mode switch
			{
				GitFilteringMode.RespectGitIgnore => "gitignore",
				GitFilteringMode.TrackedFilesOnly => "tracked",
				_ => "none"
			};
			var compactFormat = _format switch
			{
				ProjectContextDocumentFormat.Markdown => "MD",
				ProjectContextDocumentFormat.Text => "TXT",
				ProjectContextDocumentFormat.Json => "JSON",
				_ => "XML"
			};
			return $"{L("Terminal.Tui.Profile")}: {profile}  |  Git: {compactGitMode}  |  " +
			       $"{_workspace.LocalizeView(_previewView)} / {compactFormat}";
		}

		return $"{L("Terminal.Tui.Profile")}: {profile}  |  " +
		       $"{L("Terminal.Tui.GitFiltering")}: {gitMode}  |  " +
		       $"{L("Terminal.Tui.View")}: {_workspace.LocalizeView(_previewView)} / {_format}";
	}

	private string BuildStatus(TerminalWorkspaceState state, int width)
	{
		if (width < 80)
		{
			return $"{state.SelectedFileCount:N0} F  {state.SelectedFolderCount:N0} D  " +
			       $"~{state.Plan.Analysis.Metrics.Content.Tokens:N0} tok  " +
			       $"{state.Plan.Diagnostics.Count:N0} !";
		}

		return $"{L("Terminal.Analysis.Files")} {state.SelectedFileCount:N0}  |  " +
		       $"{L("Terminal.Analysis.Folders")} {state.SelectedFolderCount:N0}  |  " +
		       $"{TerminalWorkspace.FormatBytes(state.Plan.IncludedBytes)}  |  " +
		       $"~{state.Plan.Analysis.Metrics.Content.Tokens:N0} {L("Terminal.Tui.TokensShort")}  |  " +
		       $"{L("Terminal.Tui.Warnings")} {state.Plan.Diagnostics.Count:N0}";
	}

	private void UpdatePreviewTitle()
	{
		if (_previewFrame is null)
			return;
		_previewFrame.Title =
			$"{L("Terminal.Tui.Preview")} · {_workspace.LocalizeView(_previewView)} · {_format}";
	}

	private void UpdateFooter()
	{
		if (_footer is null)
			return;
		var wide = _application.Screen.Width >= 110;
		_footer.Text = _activePane == TerminalWorkspacePane.Tree
			? L(wide ? "Terminal.Tui.Footer.Tree.Wide" : "Terminal.Tui.Footer.Tree")
			: L(wide ? "Terminal.Tui.Footer.Preview.Wide" : "Terminal.Tui.Footer.Preview");
	}

	private void UpdateWorkspaceFocus()
	{
		if (_tree is null || _preview is null || _treeFrame is null || _previewFrame is null)
			return;

		if (_tree.HasFocus)
			_activePane = TerminalWorkspacePane.Tree;
		else if (_preview.HasFocus)
			_activePane = TerminalWorkspacePane.Preview;
		_treeFrame.SchemeName = _activePane == TerminalWorkspacePane.Tree
			? TerminalWorkspaceTheme.FocusedPanel
			: TerminalWorkspaceTheme.Panel;
		_previewFrame.SchemeName = _activePane == TerminalWorkspacePane.Preview
			? TerminalWorkspaceTheme.FocusedPanel
			: TerminalWorkspaceTheme.Panel;
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
		if (_layoutMode == TerminalWorkspaceLayoutMode.Split)
		{
			_welcomeActionsFrame.X = 2;
			_welcomeActionsFrame.Y = 7;
			_welcomeActionsFrame.Width = 42;
			_welcomeActionsFrame.Height = actionHeight;
			_welcomeDetailFrame.X = Pos.Right(_welcomeActionsFrame) + 2;
			_welcomeDetailFrame.Y = 7;
			_welcomeDetailFrame.Width = Dim.Fill(2);
			_welcomeDetailFrame.Height = actionHeight;
			_welcomeQuickStart.Y = 7 + actionHeight + 1;
			_welcomeQuickStart.Height = 3;
			_welcomeCurrentPath.Width = Dim.Fill(2);
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
		if (_treeFrame is null || _previewFrame is null || _tooSmall is null ||
		    _workspaceHeading is null || _workspacePath is null || _workspaceContext is null ||
		    _status is null || _footer is null)
		{
			return;
		}

		var tooSmall = _layoutMode == TerminalWorkspaceLayoutMode.TooSmall;
		SetVisible(
			!tooSmall,
			_workspaceHeading,
			_workspacePath,
			_workspaceContext,
			_treeFrame,
			_previewFrame,
			_status,
			_footer);
		_tooSmall.Visible = tooSmall;
		if (tooSmall)
			return;

		var contentWidth = Math.Max(1, _terminalWidth - 2);
		_workspaceHeading.Text = FitEndToWidth(
			$"DevProjex Terminal  {Path.GetFileName(_state?.Plan.SourceRoot)}",
			contentWidth);
		_workspacePath.Text = FitPathToWidth(_state?.Plan.SourceRoot ?? string.Empty, contentWidth);
		_treeFrame.X = 0;
		_treeFrame.Y = 3;
		_treeFrame.Height = Dim.Fill(3);
		_previewFrame.Y = 3;
		_previewFrame.Height = Dim.Fill(3);
		if (_layoutMode == TerminalWorkspaceLayoutMode.Split)
		{
			_treeFrame.Width = Dim.Percent(46);
			_previewFrame.X = Pos.Right(_treeFrame);
			_previewFrame.Width = Dim.Fill();
			_treeFrame.Visible = true;
			_previewFrame.Visible = true;
		}
		else
		{
			_treeFrame.Width = Dim.Fill();
			_previewFrame.X = 0;
			_previewFrame.Width = Dim.Fill();
			ShowSinglePane(_activePane);
		}

		RefreshWorkspace();
		UpdateWorkspaceFocus();
	}

	private void ShowSinglePane(TerminalWorkspacePane pane)
	{
		if (_treeFrame is null || _previewFrame is null)
			return;
		_treeFrame.Visible = pane == TerminalWorkspacePane.Tree;
		_previewFrame.Visible = pane == TerminalWorkspacePane.Preview;
	}

	private TerminalWorkspaceLayoutMode ResolveLayout()
	{
		return TerminalWorkspaceLayout.Resolve(_terminalWidth, _terminalHeight);
	}

	private void UpdateTerminalSize(int width, int height)
	{
		if (width > 0)
			_terminalWidth = width;
		if (height > 0)
			_terminalHeight = height;
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
				SetOperationStatus(L("Terminal.Tui.CancelingOperation"), TerminalWorkspaceTheme.Warning);
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
				SetOperationStatus(L("Terminal.Tui.CancelingOperation"), TerminalWorkspaceTheme.Warning);
			}
			else
			{
				RequestExit();
			}
			return;
		}

		if (key == Key.F1 || key == new Key('?'))
		{
			key.Handled = true;
			ShowHelp(_screen == TerminalWorkspaceScreen.Welcome);
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
			if (Confirm(L("Terminal.Tui.BackToWelcome"), L("Terminal.Tui.ConfirmBackToWelcome")))
				ShowWelcome();
			return;
		}
		if (key.NoShift == Key.J || key.NoShift == Key.K)
		{
			key.Handled = true;
			if (_tree.HasFocus)
				MoveListSelection(_tree, key.NoShift == Key.J ? 1 : -1);
			else
				ScrollPreview(key.NoShift == Key.J ? 1 : -1);
			return;
		}
		if (key == Key.Enter && _tree.HasFocus)
		{
			key.Handled = true;
			ToggleCurrentTreeExpansion();
			return;
		}
		if (key.NoShift == Key.Tab)
		{
			key.Handled = true;
			SwitchPane();
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
			_format = NextFormat(_format);
			RefreshWorkspace();
			SchedulePreviewRefresh();
			return;
		}
		if (key == new Key('/'))
		{
			key.Handled = true;
			SearchTree();
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
			return;
		}
		if (key.NoShift == Key.G)
		{
			key.Handled = true;
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

	private void SearchTree()
	{
		if (_state is null || _tree is null)
			return;
		var query = Prompt(
			L("Terminal.Tui.Search"),
			L("Terminal.Tui.SearchPrompt"),
			_searchQuery);
		if (query is null)
			return;
		_searchQuery = query;
		if (string.IsNullOrWhiteSpace(query))
			return;
		var match = _state.FindNext(query, _tree.SelectedItem ?? -1);
		if (match < 0)
		{
			ShowNotice(
				L("Terminal.Tui.Search"),
				L("Terminal.Tui.SearchNoResults"),
				TerminalWorkspaceTheme.Warning);
			return;
		}
		RefreshWorkspace();
		_tree.SelectedItem = match;
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
		var defaultPath = Path.Combine(
			Directory.GetCurrentDirectory(),
			_format switch
			{
				ProjectContextDocumentFormat.Json => "context.json",
				ProjectContextDocumentFormat.Xml => "context.xml",
				ProjectContextDocumentFormat.Text => "context.txt",
				_ => "context.md"
			});
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
				token).ConfigureAwait(false),
			exactDestination => TerminalWorkspaceController.BuildEquivalentContextCommand(
					_state,
					_previewView,
					_format) +
				$" -o {TerminalWorkspace.QuoteForDisplay(exactDestination)}");
	}

	private void ExportProject()
	{
		if (_state is null)
			return;
		var kind = SelectProjectExportFormat();
		if (kind is null)
			return;
		var defaultPath = Path.Combine(
			Directory.GetCurrentDirectory(),
			kind == ProjectCopyExportFormat.Zip
				? $"{Path.GetFileName(_state.Plan.SourceRoot)}.zip"
				: $"{Path.GetFileName(_state.Plan.SourceRoot)}-export");
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
				kind.Value,
				destination,
				token),
			async (progress, token) => await _controller.ExportProjectAsync(
				_state,
				kind.Value,
				destination,
				token,
				progress).ConfigureAwait(false),
			exactDestination => TerminalWorkspaceController.BuildEquivalentProjectCommand(
				_state,
				kind.Value,
				exactDestination));
	}

	private void SaveProfile()
	{
		if (_state is null)
			return;
		var destination = Prompt(
			L("Terminal.Tui.SaveProfile"),
			L("Terminal.Tui.ProfileDestination"),
			Path.Combine(Directory.GetCurrentDirectory(), "devprojex-profile.json"));
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
			SetWorkspaceBusy(operationName);
			var result = await operation(operationCts.Token).ConfigureAwait(false);
			if (_stopping)
				return;
			await InvokeAsync(() =>
			{
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
		Func<string, string> equivalentCommand)
	{
		if (!await _operationGate.WaitAsync(0, _sessionCts.Token).ConfigureAwait(false))
			return;
		var operationCts = ReplaceActiveOperation();
		try
		{
			SetWorkspaceBusy(operationName);
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

			var command = equivalentCommand(summary.Destination);
			if (decision == TerminalExportDecision.DryRun)
			{
				await InvokeAsync(() =>
				{
					RefreshWorkspace();
					ShowNotice(
						operationName,
						$"{L("Terminal.Tui.DryRunReady")}\n\n" +
						$"{L("Terminal.Tui.EquivalentCommand")}:\n{command} --dry-run",
						TerminalWorkspaceTheme.Success);
					return true;
				}).ConfigureAwait(false);
				return;
			}

			SetWorkspaceBusy(operationName);
			var progress = new Progress<ProjectCopyExportProgress>(value =>
				_application.Invoke(() =>
				{
					if (_status is not null)
					{
						_status.Text = string.Format(
							CultureInfo.CurrentCulture,
							L("Status.Operation.ExportingProjectCopy.Progress"),
							value.ProcessedEntryCount,
							value.TotalEntryCount);
						_status.SchemeName = TerminalWorkspaceTheme.Accent;
					}
				}));
			var result = await export(progress, operationCts.Token).ConfigureAwait(false);
			if (_stopping)
				return;
			await InvokeAsync(() =>
			{
				SetWorkspaceBusy(null);
				RefreshWorkspace();
				SchedulePreviewRefresh();
				ShowNotice(
					operationName,
					BuildExportCompletion(summary, result, command),
					TerminalWorkspaceTheme.Success);
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
		string equivalentCommand) =>
		$"{L("Terminal.Tui.Destination").TrimEnd(':')}: {destination}\n" +
		$"{L("Terminal.Analysis.Files")}: {summary.FileCount:N0}  |  " +
		$"{L("Terminal.Analysis.Folders")}: {summary.FolderCount:N0}  |  " +
		$"{L("Terminal.Analysis.Size")}: {TerminalWorkspace.FormatBytes(summary.Bytes)}\n\n" +
		$"{L("Terminal.Tui.EquivalentCommand")}:\n{equivalentCommand}";

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

	private void SetWorkspaceBusy(string? operationName)
	{
		_application.Invoke(() =>
		{
			var busy = !string.IsNullOrWhiteSpace(operationName);
			if (_tree is not null)
				_tree.Enabled = !busy;
			if (_preview is not null)
				_preview.Enabled = !busy;
			if (_status is not null)
			{
				_status.Text = busy
					? $"{operationName}...  {L("Terminal.Tui.CancelOperation")}"
					: _state is null
						? string.Empty
						: BuildStatus(_state, _application.Screen.Width);
				_status.SchemeName = busy
					? TerminalWorkspaceTheme.Accent
					: TerminalWorkspaceTheme.Secondary;
			}
		});
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
		CancelAndDispose(ref _projectionCts);
		_projectionCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
		var operationCts = _projectionCts;
		_projectionTask = Task.Run(async () =>
		{
			try
			{
				await Task.Delay(180, operationCts.Token).ConfigureAwait(false);
				await _controller.ReprojectSelectionAsync(_state, operationCts.Token)
					.ConfigureAwait(false);
				await _controller.RefreshPreviewAsync(_state, _previewView, _format, operationCts.Token)
					.ConfigureAwait(false);
				if (!_stopping)
					_application.Invoke(RefreshWorkspace);
			}
			catch (OperationCanceledException)
			{
				// The newest selection owns the next projection.
			}
			catch
			{
				if (!_stopping)
				_application.Invoke(() => ShowError(
					"DPX-TUI-PREVIEW-FAILED",
					L("Terminal.Tui.Error.PreviewFailed")));
			}
		}, CancellationToken.None);
	}

	private void SchedulePreviewRefresh()
	{
		if (_state is null)
			return;
		CancelAndDispose(ref _previewCts);
		_previewCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
		var operationCts = _previewCts;
		_previewTask = Task.Run(async () =>
		{
			try
			{
				await _controller.RefreshPreviewAsync(_state, _previewView, _format, operationCts.Token)
					.ConfigureAwait(false);
				if (!_stopping)
					_application.Invoke(RefreshWorkspace);
			}
			catch (OperationCanceledException)
			{
				// A newer view, format, or selection owns the preview surface.
			}
			catch
			{
				if (!_stopping)
					_application.Invoke(() => ShowError(
						"DPX-TUI-PREVIEW-FAILED",
						L("Terminal.Tui.Error.PreviewFailed")));
			}
		}, CancellationToken.None);
	}

	private void CancelWorkspaceRefreshes()
	{
		CancelAndDispose(ref _projectionCts);
		CancelAndDispose(ref _previewCts);
	}

	private void SwitchPane()
	{
		if (_tree is null || _preview is null)
			return;
		_activePane = _activePane == TerminalWorkspacePane.Tree
			? TerminalWorkspacePane.Preview
			: TerminalWorkspacePane.Tree;
		if (_layoutMode != TerminalWorkspaceLayoutMode.Split)
			ShowSinglePane(_activePane);
		(_activePane == TerminalWorkspacePane.Tree ? (View)_tree : _preview).SetFocus();
		UpdateWorkspaceFocus();
	}

	private void ScrollPreview(int delta)
	{
		if (_preview is null)
			return;
		var row = Math.Max(0, _preview.CurrentRow + delta);
		_preview.InsertionPoint = new System.Drawing.Point(0, row);
		_preview.ScrollTo(new System.Drawing.Point(0, row));
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
		dialog.AddButton(new Button { Text = L("Terminal.Tui.Cancel") });
		dialog.AddButton(new Button { Text = L("Terminal.Tui.Accept") });
		input.SetFocus();
		RunOverlay(dialog);
		return TerminalWorkspace.CompletePrompt(dialog.Result == 1, input.Text);
	}

	private string? SelectFromList(
		string title,
		string description,
		IReadOnlyList<string> values)
	{
		if (values.Count == 0)
			return null;
		var height = Math.Clamp(values.Count + 7, 10, Math.Max(10, _application.Screen.Height - 4));
		using var dialog = CreateDialog(title, 78, height);
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
			Height = Dim.Fill(1),
			SchemeName = TerminalWorkspaceTheme.List
		};
		list.SetSource(source);
		string? selected = null;
		list.Accepted += (_, _) =>
		{
			if (list.SelectedItem is { } index && index >= 0 && index < source.Count)
				selected = source[index];
			_application.RequestStop(dialog);
		};
		dialog.Add(label, list);
		dialog.AddButton(new Button { Text = L("Terminal.Tui.Back") });
		list.SetFocus();
		RunOverlay(dialog);
		return selected;
	}

	private string? SelectPath(string title, OpenMode mode, string? initialPath)
	{
		using var dialog = new OpenDialog
		{
			Title = title,
			OpenMode = mode,
			MustExist = true,
			Path = initialPath ?? Directory.GetCurrentDirectory(),
			Width = Dim.Percent(90),
			Height = Dim.Percent(85),
			BorderStyle = LineStyle.Single,
			SchemeName = TerminalWorkspaceTheme.Dialog
		};
		RunOverlay(dialog);
		return dialog.Canceled ? null : dialog.Path;
	}

	private GitFilteringMode? SelectGitMode(GitFilteringMode current)
	{
		var modes = new[]
		{
			(GitFilteringMode.None, L("Terminal.Tui.GitNone")),
			(GitFilteringMode.RespectGitIgnore, ".gitignore"),
			(GitFilteringMode.TrackedFilesOnly, L("Terminal.Tui.GitTracked"))
		};
		var rows = new ObservableCollection<string>(
			modes.Select(mode => $"{(mode.Item1 == current ? "(*)" : "( )")} {mode.Item2}"));
		using var dialog = CreateDialog(L("Terminal.Tui.GitFiltering"), 66, 12);
		var description = new Label
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
		list.SelectedItem = Array.FindIndex(modes, mode => mode.Item1 == current);
		GitFilteringMode? selected = null;
		void SelectCurrent()
		{
			if (list.SelectedItem is not { } index || index < 0 || index >= modes.Length)
				return;
			selected = modes[index].Item1;
			_application.RequestStop(dialog);
		}
		list.Accepted += (_, _) => SelectCurrent();
		dialog.Add(description, list);
		dialog.AddButton(new Button { Text = L("Terminal.Tui.Cancel") });
		var apply = new Button { Text = L("Terminal.Tui.Accept") };
		apply.Accepted += (_, _) =>
		{
			if (list.SelectedItem is { } index && index >= 0 && index < modes.Length)
				selected = modes[index].Item1;
		};
		dialog.AddButton(apply);
		list.SetFocus();
		RunOverlay(dialog);
		return dialog.Result == 1 || selected is not null ? selected : null;
	}

	private IReadOnlyCollection<ProjectExclusion>? SelectExclusions(
		IReadOnlyCollection<ProjectExclusion> current)
	{
		var available = Enum.GetValues<ProjectExclusion>();
		var values = SelectValues(
			L("Terminal.Tui.Exclusions"),
			available.Select(ProjectSelectionTokens.ToToken).ToArray(),
			current.Select(ProjectSelectionTokens.ToToken).ToArray());
		return values?.Select(value =>
				ProjectSelectionTokens.TryParseExclusion(value, out var exclusion)
					? exclusion
					: throw new InvalidOperationException())
			.ToArray();
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
		var hint = new Label
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

		var toggleAll = new Button
		{
			X = 1,
			Y = Pos.AnchorEnd(2),
			Text = L("Terminal.Tui.ToggleAll"),
			SchemeName = TerminalWorkspaceTheme.List
		};
		toggleAll.Accepted += (_, _) =>
		{
			var markAll = list.GetAllMarkedItems().Count() != source.Count;
			list.MarkAll(markAll);
			list.SetNeedsDraw();
		};
		dialog.Add(hint, list, toggleAll);
		dialog.AddButton(new Button { Text = L("Terminal.Tui.Cancel") });
		dialog.AddButton(new Button { Text = L("Terminal.Tui.Accept") });
		list.SetFocus();
		RunOverlay(dialog);
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
			ScrollBars = true,
			Text = message,
			SchemeName = TerminalWorkspaceTheme.Base
		};
		dialog.Add(body);
		foreach (var choice in choices)
			dialog.AddButton(new Button { Text = choice });
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
		var body = welcome
			? L("Terminal.Tui.Welcome.HelpBody")
			: L("Terminal.Tui.HelpBody");
		ShowScrollableOverlay(
			L("Terminal.Tui.Help"),
			body,
			TerminalWorkspaceTheme.Dialog,
			preferredWidth: 84,
			preferredHeight: 22);
	}

	private void ShowNotice(string title, string message, string schemeName) =>
		ShowScrollableOverlay(title, message, schemeName, 82, 18);

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
			ScrollBars = true,
			Text = message,
			SchemeName = TerminalWorkspaceTheme.Base
		};
		dialog.Add(body);
		dialog.AddButton(new Button { Text = L("Terminal.Tui.Close") });
		body.SetFocus();
		RunOverlay(dialog);
	}

	private static int EstimateWrappedLineCount(string message, int width)
	{
		var lineCount = 0;
		foreach (var line in message.Split('\n'))
			lineCount += Math.Max(1, (line.GetColumns() + width - 1) / width);
		return lineCount;
	}

	private static string FitPathToWidth(string value, int width)
	{
		if (string.IsNullOrEmpty(value) || width <= 0)
			return string.Empty;
		if (value.GetColumns() <= width)
			return value;
		if (width <= 3)
			return new string('.', width);

		var remaining = width - 3;
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

		return "..." + string.Concat(runes.AsSpan(start).ToArray());
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
		return new Dialog
		{
			Title = title,
			Width = width,
			Height = height,
			BorderStyle = LineStyle.Single,
			SchemeName = TerminalWorkspaceTheme.Dialog,
			ButtonAlignment = Alignment.Center
		};
	}

	private int ResolveDialogWidth(int preferredWidth)
	{
		var maximumWidth = Math.Max(12, _application.Screen.Width - 4);
		var minimumWidth = Math.Min(40, maximumWidth);
		return Math.Clamp(preferredWidth, minimumWidth, maximumWidth);
	}

	private void RunOverlay(IRunnable overlay)
	{
		var previousFocus = _root.MostFocused;
		try
		{
			_application.Run(overlay);
		}
		finally
		{
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

	private void CancelActiveOperation() =>
		_activeOperationCts?.Cancel();

	private bool HasActiveOperation =>
		_activeOperationCts is { IsCancellationRequested: false };

	private Task<T> InvokeAsync<T>(Func<T> action)
	{
		if (_stopping)
			return Task.FromCanceled<T>(new CancellationToken(canceled: true));
		var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
		_application.Invoke(() =>
		{
			if (_stopping)
			{
				completion.TrySetCanceled();
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
		return completion.Task;
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
		_state = null;
		_tree = null;
		_preview = null;
		_treeFrame = null;
		_previewFrame = null;
		_welcomeList = null;
		_welcomeRows = null;
		_welcomeDetail = null;
		foreach (var view in _root.RemoveAll())
			view.Dispose();
		_application.ClearScreenNextIteration = true;
		_root.SetNeedsLayout();
		_root.SetNeedsDraw();
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

	private static ProjectContextDocumentFormat NextFormat(ProjectContextDocumentFormat current) =>
		current switch
		{
			ProjectContextDocumentFormat.Text => ProjectContextDocumentFormat.Markdown,
			ProjectContextDocumentFormat.Markdown => ProjectContextDocumentFormat.Json,
			ProjectContextDocumentFormat.Json => ProjectContextDocumentFormat.Xml,
			_ => ProjectContextDocumentFormat.Text
		};

	private static string GetProductVersion()
	{
		var assembly = Assembly.GetEntryAssembly() ?? typeof(TerminalWorkspaceSession).Assembly;
		var informational = assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
			.InformationalVersion;
		var version = informational?.Split('+', 2)[0] ?? assembly.GetName().Version?.ToString();
		return string.IsNullOrWhiteSpace(version) ? "dev" : version;
	}

	private static void CancelAndDispose(ref CancellationTokenSource? source)
	{
		if (source is null)
			return;
		source.Cancel();
		source.Dispose();
		source = null;
	}

	private string L(string key) => _workspace.L(key);

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
		Preview
	}

	private sealed class TerminalWorkspaceOperationException(string code) : Exception
	{
		public string Code { get; } = code;
	}
}

#pragma warning restore CS0618
