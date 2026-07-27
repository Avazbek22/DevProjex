using System.Collections.ObjectModel;
using System.Globalization;
using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Execution;
using DevProjex.Terminal.Rendering;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drivers;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DevProjex.Terminal.Tui;

public sealed record TerminalWorkspaceOptions(
	string ProjectPath,
	ProjectProfileReference Profile,
	TerminalScreenMode ScreenMode,
	bool MouseEnabled,
	TerminalColorMode ColorMode,
	bool Plain,
	bool ShowWelcome = false);

public sealed class TerminalWorkspace(
	TerminalServices services,
	ITerminalEnvironment environment)
{
	public async Task<int> RunAsync(
		TerminalWorkspaceOptions options,
		CancellationToken cancellationToken)
	{
		if (!environment.IsInputInteractive ||
		    !environment.IsOutputInteractive ||
		    environment.IsTermDumb)
		{
			environment.Error.WriteLine("error[DPX-TUI-NOT-INTERACTIVE]:");
			environment.Error.WriteLine(L("Terminal.Tui.Error.NotInteractive"));
			environment.Error.WriteLine(L("Terminal.Tui.Hint.DirectCommands"));
			return CommandLineExitCodes.UsageError;
		}

		var mode = TerminalWorkspaceLayout.Resolve(environment.Width, environment.Height);
		if (mode == TerminalWorkspaceLayoutMode.TooSmall)
		{
			environment.Error.WriteLine("error[DPX-TUI-TERMINAL-TOO-SMALL]:");
			environment.Error.WriteLine(L("Terminal.Tui.Error.TooSmall"));
			return CommandLineExitCodes.UsageError;
		}

		using IApplication application = global::Terminal.Gui.App.Application.Create();
		var initialized = false;
		try
		{
			application.AppModel = TerminalScreenModeResolver.Resolve(options.ScreenMode, environment) ==
			                       TerminalScreenMode.Inline
				? AppModel.Inline
				: AppModel.FullScreen;
			application.Init();
			initialized = true;
			application.Mouse.IsMouseDisabled = !options.MouseEnabled;
			var presentation = TerminalWorkspacePresentationPolicy.Resolve(
				options.ColorMode,
				options.Plain,
				environment);
			if (presentation.UseMonochromeScheme)
				RegisterMonochromeScheme();

			var effectiveOptions = options.ShowWelcome
				? await ResolveWelcomeAsync(application, options, cancellationToken).ConfigureAwait(false)
				: options;
			if (effectiveOptions is null)
				return CommandLineExitCodes.Success;

			var controller = new TerminalWorkspaceController(services, environment);
			var state = await controller
				.OpenAsync(effectiveOptions.ProjectPath, effectiveOptions.Profile, cancellationToken)
				.ConfigureAwait(false);

			using var window = BuildWindow(
				application,
				controller,
				state,
				mode,
				presentation,
				cancellationToken);
			await application.RunAsync(window, cancellationToken).ConfigureAwait(false);
			return CommandLineExitCodes.Success;
		}
		finally
		{
			if (initialized)
			{
				try
				{
					application.RequestStop();
				}
				catch
				{
					// Disposal still restores the driver after a failed stop request.
				}
			}
		}
	}

	private async Task<TerminalWorkspaceOptions?> ResolveWelcomeAsync(
		IApplication application,
		TerminalWorkspaceOptions options,
		CancellationToken cancellationToken)
	{
		var recent = services.RecentProjectsStore.LoadForStartup(TimeSpan.FromMilliseconds(200))
			.RecentFolders
			.Select(static entry => entry.Path);
		var context = TerminalWelcomePolicy.Create(options.ProjectPath, recent);
		while (true)
		{
			var actions = new List<string>();
			if (context.CanOpenCurrentDirectory)
				actions.Add($"{L("Terminal.Tui.Welcome.OpenCurrent")}  {context.CurrentDirectory}");
			if (context.RecentProjects.Count > 0)
				actions.Add(L("Terminal.Tui.Welcome.Recent"));
			actions.AddRange(
			[
				L("Terminal.Tui.Welcome.Browse"),
				L("Terminal.Tui.Welcome.Clone"),
				L("Terminal.Tui.Welcome.OpenProfile"),
				L("Terminal.Tui.Welcome.OpenDesktop"),
				L("Terminal.Tui.Help"),
				L("Terminal.Tui.Exit")
			]);

			var action = SelectFromList(
				application,
				"DevProjex Terminal",
				L("Terminal.Tui.Welcome.Description"),
				actions);
			if (action is null || action == L("Terminal.Tui.Exit"))
				return null;
			if (action.StartsWith(L("Terminal.Tui.Welcome.OpenCurrent"), StringComparison.Ordinal))
				return options with { ProjectPath = context.CurrentDirectory, ShowWelcome = false };
			if (action == L("Terminal.Tui.Welcome.Recent"))
			{
				var project = SelectFromList(
					application,
					L("Terminal.Tui.Welcome.Recent"),
					L("Terminal.Tui.Welcome.RecentDescription"),
					context.RecentProjects);
				if (project is not null)
					return options with { ProjectPath = project, ShowWelcome = false };
				continue;
			}
			if (action == L("Terminal.Tui.Welcome.Browse"))
			{
				var path = Prompt(
					application,
					L("Terminal.Tui.Welcome.Browse"),
					L("Terminal.Tui.ProjectDirectory"),
					context.CurrentDirectory);
				if (TryResolveDirectory(path, out var project))
					return options with { ProjectPath = project, ShowWelcome = false };
				ShowInvalidPath(application, L("Terminal.Tui.Error.ProjectUnavailable"));
				continue;
			}
			if (action == L("Terminal.Tui.Welcome.OpenProfile"))
			{
				var profilePath = Prompt(
					application,
					L("Terminal.Tui.Welcome.OpenProfile"),
					L("Terminal.Tui.ProfileFile"),
					Path.Combine(context.CurrentDirectory, "devprojex-profile.json"));
				if (string.IsNullOrWhiteSpace(profilePath) || !File.Exists(profilePath))
				continue;
				var projectPath = Prompt(
					application,
					L("Terminal.Tui.Welcome.OpenProfile"),
					L("Terminal.Tui.ProjectDirectory"),
					context.CurrentDirectory);
				if (!TryResolveDirectory(projectPath, out var project))
				{
					ShowInvalidPath(application, L("Terminal.Tui.Error.ProjectUnavailable"));
					continue;
				}
				return options with
				{
					ProjectPath = project,
					Profile = new ProjectProfileReference(
						ProjectProfileSourceKind.Portable,
						Path.GetFullPath(profilePath)),
					ShowWelcome = false
				};
			}
			if (action == L("Terminal.Tui.Welcome.Clone"))
			{
				var url = Prompt(
					application,
					L("Terminal.Tui.Welcome.Clone"),
					L("Terminal.Tui.RepositoryUrl"),
					string.Empty);
				if (string.IsNullOrWhiteSpace(url))
					continue;
				var target = services.RepoCacheService.CreateRepositoryDirectory(url);
				var result = await services.GitRepositoryService
					.CloneAsync(url, target, cancellationToken: cancellationToken)
					.ConfigureAwait(false);
				if (result.Success && Directory.Exists(result.LocalPath))
					return options with { ProjectPath = result.LocalPath, ShowWelcome = false };
				services.RepoCacheService.DeleteRepositoryDirectory(target);
				ShowInvalidPath(application, L("Terminal.Tui.Error.CloneFailed"));
				continue;
			}
			if (action == L("Terminal.Tui.Welcome.OpenDesktop"))
			{
				await new DesktopCommandHandler(environment, writeOutput: false)
					.OpenAsync(new DesktopOpenRequest(), cancellationToken)
					.ConfigureAwait(false);
				return null;
			}
			if (action == L("Terminal.Tui.Help"))
				ShowWelcomeHelp(application);
		}
	}

	private Window BuildWindow(
		IApplication application,
		TerminalWorkspaceController controller,
		TerminalWorkspaceState state,
		TerminalWorkspaceLayoutMode layoutMode,
		TerminalWorkspacePresentation presentation,
		CancellationToken cancellationToken)
	{
		var window = new Window
		{
			Title = BuildTitle(state),
			Width = Dim.Fill(),
			Height = Dim.Fill(),
			SchemeName = presentation.SchemeName
		};
		var treeFrame = new FrameView
		{
			Title = L("Terminal.Tui.Tree"),
			X = 0,
			Y = 0,
			Height = Dim.Fill(2)
		};
		var previewFrame = new FrameView
		{
			Title = L("Terminal.Tui.Preview"),
			Y = 0,
			Height = Dim.Fill(2)
		};
		ApplyPaneLayout(layoutMode, treeFrame, previewFrame);

		var tree = new ListView
		{
			X = 0,
			Y = 0,
			Width = Dim.Fill(),
			Height = Dim.Fill(),
			ShowMarks = false
		};
		tree.SetSource(state.VisibleRows);
#pragma warning disable CS0618
		var preview = new TextView
		{
			X = 0,
			Y = 0,
			Width = Dim.Fill(),
			Height = Dim.Fill(),
			ReadOnly = true,
			WordWrap = false,
			Text = state.PreviewText
		};
#pragma warning restore CS0618
		treeFrame.Add(tree);
		previewFrame.Add(preview);

		var status = new Label
		{
			X = 1,
			Y = Pos.AnchorEnd(2),
			Width = Dim.Fill(1),
			Text = BuildStatus(state)
		};
		var footer = new Label
		{
			X = 1,
			Y = Pos.AnchorEnd(1),
			Width = Dim.Fill(1),
			Text = L("Terminal.Tui.Footer")
		};
		window.Add(treeFrame, previewFrame, status, footer);
		tree.SetFocus();

		var activePane = TerminalWorkspacePane.Tree;
		var currentLayoutMode = layoutMode;
		string? searchQuery = null;
		ProjectContextView previewView = ProjectContextView.TreeContent;
		ProjectContextDocumentFormat format = ProjectContextDocumentFormat.Markdown;
		var operationGate = new SemaphoreSlim(1, 1);
		var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		CancellationTokenSource? projectionCts = null;
		CancellationTokenSource? previewCts = null;

		void Refresh()
		{
			tree.SetSource(state.VisibleRows);
			preview.Text = state.PreviewText;
			status.Text = currentLayoutMode == TerminalWorkspaceLayoutMode.TooSmall
				? L("Terminal.Tui.Error.Resize")
				: BuildStatus(state);
			window.Title = BuildTitle(state);
		}

		void ApplyCurrentLayout()
		{
			currentLayoutMode = TerminalWorkspaceLayout.Resolve(
				application.Screen.Width,
				application.Screen.Height);
			if (currentLayoutMode == TerminalWorkspaceLayoutMode.TooSmall)
			{
				treeFrame.Visible = false;
				previewFrame.Visible = false;
			}
			else
			{
				treeFrame.Visible = true;
				ApplyPaneLayout(currentLayoutMode, treeFrame, previewFrame);
				if (currentLayoutMode != TerminalWorkspaceLayoutMode.Split)
					ShowSinglePane(activePane, treeFrame, previewFrame);
			}
			Refresh();
		}

		void SetStatus(string value) =>
			application.Invoke(() => status.Text = value);

		void ShowFailure(string code, string message) =>
			application.Invoke(() => MessageBox.ErrorQuery(
				application,
				$"{L("Terminal.Tui.Error")} [{code}]",
				message,
				L("Terminal.Tui.Close")));

		async Task RunOperationAsync(
			string operationName,
			Func<CancellationToken, Task<string?>> operation,
			Func<string, string>? equivalentCommand = null)
		{
			var operationToken = sessionCts.Token;
			if (!await operationGate.WaitAsync(0, operationToken).ConfigureAwait(false))
				return;

			try
			{
				SetStatus($"{operationName}...");
				var result = await operation(operationToken).ConfigureAwait(false);
				application.Invoke(() =>
				{
					Refresh();
					SchedulePreviewRefresh();
					if (!string.IsNullOrWhiteSpace(result))
					{
						var message = equivalentCommand is null
							? result
							: $"{result}\n\n{L("Terminal.Tui.EquivalentCommand")}:\n{equivalentCommand(result)}";
						MessageBox.Query(application, operationName, message, L("Terminal.Tui.Close"));
					}
				});
			}
			catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
			{
				SetStatus(L("Terminal.Tui.OperationCanceled"));
			}
			catch (OutputDestinationConflictException)
			{
				ShowFailure(
					"DPX-EXPORT-DESTINATION-EXISTS",
					L("Terminal.Tui.Error.DestinationExists"));
			}
			catch (ProjectCopyExportException exception)
			{
				var error = ProjectCopyTerminalErrorMapper.Map(exception, services.Localization);
				ShowFailure(error.Code, error.Message);
			}
			catch (ProjectContextValidationException exception)
			{
				ShowFailure(exception.Code, L("Terminal.Tui.Error.InvalidOperation"));
			}
			catch
			{
				ShowFailure("DPX-TUI-OPERATION-FAILED", L("Terminal.Tui.Error.OperationFailed"));
			}
			finally
			{
				operationGate.Release();
			}
		}

		async Task RunExportWorkflowAsync(
			string operationName,
			Func<CancellationToken, Task<TerminalExportSummary>> prepare,
			Func<IProgress<ProjectCopyExportProgress>, CancellationToken, Task<string>> export,
			Func<string, string> equivalentCommand)
		{
			var operationToken = sessionCts.Token;
			if (!await operationGate.WaitAsync(0, operationToken).ConfigureAwait(false))
				return;

			try
			{
				SetStatus($"{operationName}...");
				var summary = await prepare(operationToken).ConfigureAwait(false);
				var decision = TerminalExportDecision.Cancel;
				application.Invoke(() => decision = ShowExportSummary(application, summary));
				if (decision == TerminalExportDecision.Cancel)
				{
					application.Invoke(Refresh);
					return;
				}

				var command = equivalentCommand(summary.Destination);
				if (decision == TerminalExportDecision.DryRun)
				{
					application.Invoke(() =>
					{
						Refresh();
						MessageBox.Query(
							application,
							operationName,
							$"{L("Terminal.Tui.DryRunReady")}\n\n" +
							$"{L("Terminal.Tui.EquivalentCommand")}:\n{command} --dry-run",
							L("Terminal.Tui.Close"));
					});
					return;
				}

				var progress = new Progress<ProjectCopyExportProgress>(value =>
					application.Invoke(() =>
						status.Text = string.Format(
							CultureInfo.CurrentCulture,
							L("Status.Operation.ExportingProjectCopy.Progress"),
							value.ProcessedEntryCount,
							value.TotalEntryCount)));
				var result = await export(progress, operationToken).ConfigureAwait(false);
				application.Invoke(() =>
				{
					Refresh();
					SchedulePreviewRefresh();
					MessageBox.Query(
						application,
						operationName,
						$"{result}\n\n{L("Terminal.Tui.EquivalentCommand")}:\n{command}",
						L("Terminal.Tui.Close"));
				});
			}
			catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
			{
				SetStatus(L("Terminal.Tui.OperationCanceled"));
			}
			catch (OutputDestinationConflictException)
			{
				ShowFailure(
					"DPX-EXPORT-DESTINATION-EXISTS",
					L("Terminal.Tui.Error.DestinationExists"));
			}
			catch (ProjectCopyExportException exception)
			{
				var error = ProjectCopyTerminalErrorMapper.Map(exception, services.Localization);
				ShowFailure(error.Code, error.Message);
			}
			catch (ProjectContextValidationException exception)
			{
				ShowFailure(exception.Code, L("Terminal.Tui.Error.InvalidOperation"));
			}
			catch
			{
				ShowFailure("DPX-TUI-OPERATION-FAILED", L("Terminal.Tui.Error.OperationFailed"));
			}
			finally
			{
				operationGate.Release();
			}
		}

		void ScheduleSelectionProjection()
		{
			projectionCts?.Cancel();
			projectionCts?.Dispose();
			projectionCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
			var token = projectionCts.Token;
			_ = Task.Run(async () =>
			{
				try
				{
					await Task.Delay(180, token).ConfigureAwait(false);
					await controller.ReprojectSelectionAsync(state, token).ConfigureAwait(false);
					await controller.RefreshPreviewAsync(state, previewView, format, token)
						.ConfigureAwait(false);
					application.Invoke(Refresh);
				}
				catch (OperationCanceledException)
				{
					// A newer selection owns the next projection.
				}
				catch
				{
					ShowFailure(
						"DPX-TUI-PREVIEW-FAILED",
						L("Terminal.Tui.Error.PreviewFailed"));
				}
			}, token);
		}

		void SchedulePreviewRefresh()
		{
			previewCts?.Cancel();
			previewCts?.Dispose();
			previewCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
			var token = previewCts.Token;
			_ = Task.Run(async () =>
			{
				try
				{
					await controller.RefreshPreviewAsync(state, previewView, format, token)
						.ConfigureAwait(false);
					application.Invoke(Refresh);
				}
				catch (OperationCanceledException)
				{
					// A newer view, format, or selection owns the preview surface.
				}
				catch
				{
					ShowFailure(
						"DPX-TUI-PREVIEW-FAILED",
						L("Terminal.Tui.Error.PreviewFailed"));
				}
			}, token);
		}

		SchedulePreviewRefresh();

		tree.Accepted += (_, _) =>
		{
			if (!TryToggleTreeRow(state, tree.SelectedItem))
				return;
			Refresh();
			ScheduleSelectionProjection();
		};

		application.Keyboard.KeyDown += (_, key) =>
		{
			if (!ReferenceEquals(application.TopRunnableView, window))
				return;

			if (key == Key.Q)
			{
				key.Handled = true;
				sessionCts.Cancel();
				projectionCts?.Cancel();
				previewCts?.Cancel();
				application.RequestStop(window);
				return;
			}

			if (key == Key.Space && tree.HasFocus)
			{
				key.Handled = true;
				if (TryToggleTreeRow(state, tree.SelectedItem))
				{
					Refresh();
					ScheduleSelectionProjection();
				}
				return;
			}

			if (key == Key.CursorRight && tree.HasFocus)
			{
				key.Handled = true;
				state.Expand(tree.SelectedItem ?? 0);
				Refresh();
				return;
			}

			if (key == Key.CursorLeft && tree.HasFocus)
			{
				key.Handled = true;
				state.Collapse(tree.SelectedItem ?? 0);
				Refresh();
				return;
			}

			if (key == Key.Tab)
			{
				key.Handled = true;
				activePane = activePane == TerminalWorkspacePane.Tree
					? TerminalWorkspacePane.Preview
					: TerminalWorkspacePane.Tree;
				if (currentLayoutMode != TerminalWorkspaceLayoutMode.Split)
					ShowSinglePane(activePane, treeFrame, previewFrame);
				(activePane == TerminalWorkspacePane.Tree ? (View)tree : preview).SetFocus();
				return;
			}

			if (key == Key.D1 || key == Key.D2 || key == Key.D3)
			{
				key.Handled = true;
				previewView = key == Key.D1
					? ProjectContextView.Tree
					: key == Key.D2
						? ProjectContextView.Content
						: ProjectContextView.TreeContent;
				previewFrame.Title = $"{L("Terminal.Tui.Preview")} - {previewView}";
				SchedulePreviewRefresh();
				return;
			}

			if (key == Key.F)
			{
				key.Handled = true;
				format = NextFormat(format);
				previewFrame.Title = $"{L("Terminal.Tui.Preview")} - {format}";
				SchedulePreviewRefresh();
				return;
			}

			if (key == new Key('/'))
			{
				key.Handled = true;
				searchQuery = Prompt(
					application,
					L("Terminal.Tui.Search"),
					L("Terminal.Tui.SearchPrompt"),
					searchQuery);
				if (!string.IsNullOrWhiteSpace(searchQuery))
				{
					var match = state.FindNext(searchQuery, tree.SelectedItem ?? -1);
					if (match >= 0)
						tree.SelectedItem = match;
				}
				return;
			}

			if (key == Key.E)
			{
				key.Handled = true;
				var defaultPath = Path.Combine(
					Directory.GetCurrentDirectory(),
					format switch
					{
						ProjectContextDocumentFormat.Json => "context.json",
						ProjectContextDocumentFormat.Xml => "context.xml",
						ProjectContextDocumentFormat.Text => "context.txt",
						_ => "context.md"
					});
				var destination = Prompt(
					application,
					L("Terminal.Tui.ExportContext"),
					L("Terminal.Tui.Destination"),
					defaultPath);
				if (!string.IsNullOrWhiteSpace(destination))
				{
					_ = RunExportWorkflowAsync(
						L("Terminal.Tui.ExportContext"),
						token => controller.PrepareContextExportAsync(
							state,
							previewView,
							format,
							destination,
							overwrite: false,
							token),
						async (_, token) => await controller.ExportContextAsync(
							state,
							previewView,
							format,
							destination,
							overwrite: false,
							token).ConfigureAwait(false),
						exactDestination => TerminalWorkspaceController.BuildEquivalentContextCommand(
								state,
								previewView,
								format) +
						     $" -o {QuoteForDisplay(exactDestination)}");
				}
				return;
			}

			if (key == Key.Z)
			{
				key.Handled = true;
				var kind = SelectProjectExportFormat(application);
				if (kind is null)
					return;
				var defaultPath = Path.Combine(
					Directory.GetCurrentDirectory(),
					kind == ProjectCopyExportFormat.Zip
						? $"{Path.GetFileName(state.Plan.SourceRoot)}.zip"
						: $"{Path.GetFileName(state.Plan.SourceRoot)}-export");
				var destination = Prompt(
					application,
					L("Terminal.Tui.ExportProject"),
					L("Terminal.Tui.ExactDestination"),
					defaultPath);
				if (!string.IsNullOrWhiteSpace(destination))
				{
					_ = RunExportWorkflowAsync(
						L("Terminal.Tui.ExportProject"),
						token => controller.PrepareProjectExportAsync(
							state,
							kind.Value,
							destination,
							token),
						async (progress, token) => await controller.ExportProjectAsync(
							state,
							kind.Value,
							destination,
							token,
							progress).ConfigureAwait(false),
						exactDestination => TerminalWorkspaceController.BuildEquivalentProjectCommand(
							state,
							kind.Value,
							exactDestination));
				}
				return;
			}

			if (key == Key.A)
			{
				key.Handled = true;
				_ = RunOperationAsync(
					L("Terminal.Tui.Analyze"),
					async token =>
					{
						var plan = await controller.BuildCurrentPlanAsync(state, token)
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

			if (key == Key.G)
			{
				key.Handled = true;
				_ = RunOperationAsync(
					L("Terminal.Tui.Welcome.OpenDesktop"),
					async token =>
					{
						var exitCode = await controller.OpenDesktopAsync(state, token)
							.ConfigureAwait(false);
						return exitCode == CommandLineExitCodes.Success
							? L("Terminal.Tui.DesktopAccepted")
							: throw new InvalidOperationException();
					});
				return;
			}

			if (key == Key.P)
			{
				key.Handled = true;
				var destination = Prompt(
					application,
					L("Terminal.Tui.SaveProfile"),
					L("Terminal.Tui.ProfileDestination"),
					Path.Combine(Directory.GetCurrentDirectory(), "devprojex-profile.json"));
				if (!string.IsNullOrWhiteSpace(destination))
				{
					_ = RunOperationAsync(
						L("Terminal.Tui.SaveProfile"),
						async token => await controller.SavePortableProfileAsync(
							state,
							destination,
							overwrite: false,
							token).ConfigureAwait(false));
				}
				return;
			}

			if (key == Key.M)
			{
				key.Handled = true;
				var mode = SelectGitMode(application, state.Plan.GitReadiness.Mode);
				if (mode is not null && mode != state.Plan.GitReadiness.Mode)
				{
					_ = RunOperationAsync(
						L("Terminal.Tui.GitFiltering"),
						async token =>
						{
							await controller.SetGitModeAsync(state, mode.Value, token)
								.ConfigureAwait(false);
							return null;
						});
				}
				return;
			}

			if (key == Key.X)
			{
				key.Handled = true;
				var exclusions = SelectExclusions(application, state.Plan.Selection.Exclusions ?? []);
				if (exclusions is not null)
				{
					_ = RunOperationAsync(
						L("Terminal.Tui.Exclusions"),
						async token =>
						{
							await controller.SetExclusionsAsync(state, exclusions, token)
								.ConfigureAwait(false);
							return null;
						});
				}
				return;
			}

			if (key == Key.R || key == Key.T)
			{
				key.Handled = true;
				var roots = key == Key.R;
				var available = roots
					? state.Plan.AvailableRoots
					: state.Plan.AvailableExtensions;
				var selected = roots
					? state.Plan.SelectedRoots
					: state.Plan.SelectedExtensions;
				var values = SelectValues(
					application,
					roots ? L("Terminal.Tui.RootFolders") : L("Terminal.Tui.FileTypes"),
					available,
					selected);
				if (values is not null)
				{
					_ = RunOperationAsync(
						roots ? L("Terminal.Tui.RootFolders") : L("Terminal.Tui.FileTypes"),
						async token =>
						{
							if (roots)
								await controller.SetRootsAsync(state, values, token).ConfigureAwait(false);
							else
								await controller.SetExtensionsAsync(state, values, token).ConfigureAwait(false);
							return null;
						});
				}
				return;
			}

			if (key == Key.F1 || key == new Key('?'))
			{
				key.Handled = true;
				ShowHelp(application);
			}
		};

		application.ScreenChanged += (_, _) => ApplyCurrentLayout();
		window.Disposing += (_, _) =>
		{
			sessionCts.Cancel();
			projectionCts?.Cancel();
			projectionCts?.Dispose();
			previewCts?.Cancel();
			previewCts?.Dispose();
			sessionCts.Dispose();
		};

		return window;
	}

	private static void ApplyPaneLayout(
		TerminalWorkspaceLayoutMode layoutMode,
		FrameView tree,
		FrameView preview)
	{
		if (layoutMode == TerminalWorkspaceLayoutMode.Split)
		{
			tree.Width = Dim.Percent(50);
			preview.X = Pos.Right(tree);
			preview.Width = Dim.Fill();
			preview.Visible = true;
			return;
		}

		tree.Width = Dim.Fill();
		preview.X = 0;
		preview.Width = Dim.Fill();
		preview.Visible = false;
	}

	private static void ShowSinglePane(
		TerminalWorkspacePane pane,
		View tree,
		View preview)
	{
		tree.Visible = pane == TerminalWorkspacePane.Tree;
		preview.Visible = pane == TerminalWorkspacePane.Preview;
	}

	private string BuildTitle(TerminalWorkspaceState state)
	{
		var name = Path.GetFileName(state.Plan.SourceRoot);
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
		return $" DevProjex Terminal - {name} - {gitMode} - {profile} ";
	}

	private string BuildStatus(TerminalWorkspaceState state) =>
		$"{L("Terminal.Analysis.Files")} {state.SelectedFileCount} | " +
		$"{L("Terminal.Analysis.Folders")} {state.SelectedFolderCount} | " +
		$"{L("Terminal.Analysis.Size")} {state.Plan.IncludedBytes:N0} B | " +
		$"~{state.Plan.Analysis.Metrics.Content.Tokens:N0} {L("Terminal.Tui.TokensShort")} | " +
		$"{L("Terminal.Tui.Warnings")} {state.Plan.Diagnostics.Count}";

	private static ProjectContextDocumentFormat NextFormat(ProjectContextDocumentFormat current) =>
		current switch
		{
			ProjectContextDocumentFormat.Text => ProjectContextDocumentFormat.Markdown,
			ProjectContextDocumentFormat.Markdown => ProjectContextDocumentFormat.Json,
			ProjectContextDocumentFormat.Json => ProjectContextDocumentFormat.Xml,
			_ => ProjectContextDocumentFormat.Text
		};

	private string? Prompt(
		IApplication application,
		string title,
		string label,
		string? initialValue)
	{
		using var dialog = new Dialog
		{
			Title = title,
			Width = Dim.Percent(70),
			Height = 7
		};
		var prompt = new Label { X = 1, Y = 0, Text = label };
		var input = new TextField
		{
			X = 1,
			Y = 1,
			Width = Dim.Fill(1),
			Text = initialValue ?? string.Empty
		};
		var accept = new Button
		{
			X = Pos.Center() - 10,
			Y = 3,
			Text = L("Terminal.Tui.Accept"),
			IsDefault = true
		};
		var cancel = new Button
		{
			X = Pos.Right(accept) + 2,
			Y = 3,
			Text = L("Terminal.Tui.Cancel")
		};
		var accepted = false;
		accept.Accepted += (_, _) =>
		{
			accepted = true;
			application.RequestStop(dialog);
		};
		cancel.Accepted += (_, _) => application.RequestStop(dialog);
		dialog.Add(prompt, input, accept, cancel);
		input.SetFocus();
		application.Run(dialog);
		return CompletePrompt(accepted, input.Text);
	}

	internal static string? CompletePrompt(bool accepted, string text) =>
		accepted ? text : null;

	internal static bool TryToggleTreeRow(TerminalWorkspaceState state, int? selectedRow)
	{
		if (selectedRow is null || selectedRow < 0 || selectedRow >= state.VisibleRows.Count)
			return false;

		state.ToggleSelection(selectedRow.Value);
		return true;
	}

	private string? SelectFromList(
		IApplication application,
		string title,
		string description,
		IReadOnlyList<string> values)
	{
		using var dialog = new Dialog
		{
			Title = title,
			Width = Dim.Percent(80),
			Height = Dim.Percent(75)
		};
		var label = new Label
		{
			X = 1,
			Y = 0,
			Width = Dim.Fill(1),
			Text = description
		};
		var source = new ObservableCollection<string>(values);
		var list = new ListView
		{
			X = 1,
			Y = 2,
			Width = Dim.Fill(1),
			Height = Dim.Fill(2)
		};
		list.SetSource(source);
		string? selected = null;
		list.Accepted += (_, _) =>
		{
			if (list.SelectedItem is { } index && index >= 0 && index < source.Count)
				selected = source[index];
			application.RequestStop(dialog);
		};
		dialog.Add(label, list);
		list.SetFocus();
		application.Run(dialog);
		return selected;
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

	private void ShowInvalidPath(IApplication application, string message) =>
		MessageBox.ErrorQuery(application, "DevProjex Terminal", message, L("Terminal.Tui.Close"));

	private void ShowWelcomeHelp(IApplication application) =>
		MessageBox.Query(
			application,
			"DevProjex Terminal",
			L("Terminal.Tui.Welcome.HelpBody"),
			L("Terminal.Tui.Close"));

	private void ShowHelp(IApplication application) =>
		MessageBox.Query(
			application,
			"DevProjex Terminal",
			L("Terminal.Tui.HelpBody"),
			L("Terminal.Tui.Close"));

	private GitFilteringMode? SelectGitMode(
		IApplication application,
		GitFilteringMode current)
	{
		var selected = MessageBox.Query(
			application,
			L("Terminal.Tui.GitFiltering"),
			$"{L("Terminal.Tui.Current")}: {ProjectSelectionTokens.ToToken(current)}\n\n" +
			L("Terminal.Tui.GitModePrompt"),
			L("Terminal.Tui.GitNone"),
			".gitignore",
			L("Terminal.Tui.GitTracked"),
			L("Terminal.Tui.Cancel"));
		return selected switch
		{
			0 => GitFilteringMode.None,
			1 => GitFilteringMode.RespectGitIgnore,
			2 => GitFilteringMode.TrackedFilesOnly,
			_ => null
		};
	}

	private IReadOnlyCollection<ProjectExclusion>? SelectExclusions(
		IApplication application,
		IReadOnlyCollection<ProjectExclusion> current)
	{
		var available = Enum.GetValues<ProjectExclusion>();
		var result = SelectValues(
			application,
			L("Terminal.Tui.Exclusions"),
			available.Select(ProjectSelectionTokens.ToToken).ToArray(),
			current.Select(ProjectSelectionTokens.ToToken).ToArray());
		return result?.Select(value =>
				ProjectSelectionTokens.TryParseExclusion(value, out var exclusion)
					? exclusion
					: throw new InvalidOperationException())
			.ToArray();
	}

	private IReadOnlyCollection<string>? SelectValues(
		IApplication application,
		string title,
		IReadOnlyList<string> available,
		IReadOnlyList<string> selected)
	{
		if (available.Count == 0)
		{
			MessageBox.Query(
				application,
				title,
				L("Terminal.Tui.NoneAvailable"),
				L("Terminal.Tui.Close"));
			return null;
		}

		using var dialog = new Dialog
		{
			Title = title,
			Width = Dim.Percent(72),
			Height = Dim.Percent(72)
		};
		var source = new ObservableCollection<string>(
			available.Distinct(StringComparer.OrdinalIgnoreCase));
		var list = new ListView
		{
			X = 1,
			Y = 1,
			Width = Dim.Fill(1),
			Height = Dim.Fill(3),
			ShowMarks = true,
			MarkMultiple = true
		};
		list.SetSource(source);
		var selectedSet = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
		for (var index = 0; index < source.Count; index++)
			list.Source?.SetMark(index, selectedSet.Contains(source[index]));

		var apply = new Button
		{
			X = Pos.Center() - 18,
			Y = Pos.AnchorEnd(2),
			Text = L("Terminal.Tui.Accept"),
			IsDefault = true
		};
		var toggleAll = new Button
		{
			X = Pos.Right(apply) + 2,
			Y = Pos.AnchorEnd(2),
			Text = L("Terminal.Tui.ToggleAll")
		};
		var cancel = new Button
		{
			X = Pos.Right(toggleAll) + 2,
			Y = Pos.AnchorEnd(2),
			Text = L("Terminal.Tui.Cancel")
		};
		IReadOnlyCollection<string>? result = null;
		apply.Accepted += (_, _) =>
		{
			result = list.GetAllMarkedItems()
				.Where(index => index >= 0 && index < source.Count)
				.Select(index => source[index])
				.ToArray();
			application.RequestStop(dialog);
		};
		toggleAll.Accepted += (_, _) =>
		{
			var markAll = list.GetAllMarkedItems().Count() != source.Count;
			list.MarkAll(markAll);
			list.SetNeedsDraw();
		};
		cancel.Accepted += (_, _) => application.RequestStop(dialog);
		dialog.Add(list, apply, toggleAll, cancel);
		list.SetFocus();
		application.Run(dialog);
		return result;
	}

	private ProjectCopyExportFormat? SelectProjectExportFormat(IApplication application)
	{
		var selected = MessageBox.Query(
			application,
			L("Terminal.Tui.ExportProject"),
			L("Terminal.Tui.OutputKindPrompt"),
			L("Terminal.Tui.Folder"),
			"ZIP",
			L("Terminal.Tui.Cancel"));
		return selected switch
		{
			0 => ProjectCopyExportFormat.Folder,
			1 => ProjectCopyExportFormat.Zip,
			_ => null
		};
	}

	private TerminalExportDecision ShowExportSummary(
		IApplication application,
		TerminalExportSummary summary)
	{
		var text = BuildExportSummaryText(summary);
		if (summary.DestinationState == TerminalExportDestinationState.Conflict)
		{
			MessageBox.ErrorQuery(
				application,
				L("Terminal.Tui.ExportSummary"),
				text,
				L("Terminal.Tui.Close"));
			return TerminalExportDecision.Cancel;
		}

		var selected = MessageBox.Query(
			application,
			L("Terminal.Tui.ExportSummary"),
			text,
			L("Terminal.Tui.Export"),
			L("Terminal.Tui.DryRun"),
			L("Terminal.Tui.Cancel"));
		return selected switch
		{
			0 => TerminalExportDecision.Export,
			1 => TerminalExportDecision.DryRun,
			_ => TerminalExportDecision.Cancel
		};
	}

	internal string BuildExportSummaryText(TerminalExportSummary summary)
	{
		var outputKind = summary.Kind switch
		{
			TerminalExportKind.Context => L("Terminal.Tui.ExportContext"),
			TerminalExportKind.Folder => L("Terminal.Tui.Folder"),
			_ => "ZIP"
		};
		var destinationState = summary.DestinationState == TerminalExportDestinationState.Ready
			? L("Terminal.Tui.DestinationReady")
			: L("Terminal.Tui.DestinationConflict");
		var gitMode = summary.GitMode switch
		{
			GitFilteringMode.RespectGitIgnore => ".gitignore",
			GitFilteringMode.TrackedFilesOnly => L("Terminal.Tui.GitTracked"),
			_ => L("Terminal.Tui.GitNone")
		};
		var exclusions = summary.Exclusions.Count == 0
			? L("Terminal.Tui.NoneAvailable")
			: string.Join(", ", summary.Exclusions.Select(LocalizeExclusion));
		var lines = new List<string>
		{
			$"{L("Terminal.Tui.OutputKind")}: {outputKind}"
		};
		if (summary.View is { } view)
			lines.Add($"{L("Terminal.Tui.View")}: {LocalizeView(view)}");
		if (summary.DocumentFormat is { } format)
			lines.Add($"{L("Terminal.Tui.Format")}: {format}");
		lines.AddRange(
		[
			$"{L("Terminal.Analysis.Files")}: {summary.FileCount:N0}",
			$"{L("Terminal.Analysis.Folders")}: {summary.FolderCount:N0}",
			$"{L("Terminal.Analysis.Size")}: {FormatBytes(summary.Bytes)}",
			$"{L("Terminal.Analysis.Characters")}: {summary.Characters:N0}",
			$"{L("Terminal.Analysis.Tokens")}: {summary.EstimatedTokens:N0}",
			$"{L("Terminal.Tui.Destination")}: {summary.Destination}",
			$"{L("Terminal.Tui.DestinationState")}: {destinationState}",
			$"{L("Terminal.Tui.GitFiltering")}: {gitMode}",
			$"{L("Terminal.Tui.Exclusions")}: {exclusions}",
			$"{L("Terminal.Tui.Warnings")}: {summary.DiagnosticCount:N0}"
		]);
		return string.Join(Environment.NewLine, lines);
	}

	private string LocalizeView(ProjectContextView view) =>
		view switch
		{
			ProjectContextView.Tree => L("Preview.Mode.Tree"),
			ProjectContextView.Content => L("Preview.Mode.Content"),
			_ => L("Preview.Mode.TreeAndContent")
		};

	private string LocalizeExclusion(ProjectExclusion exclusion) =>
		L(exclusion switch
		{
			ProjectExclusion.SmartIgnore => "Settings.Ignore.SmartIgnore",
			ProjectExclusion.HiddenFolders => "Settings.Ignore.HiddenFolders",
			ProjectExclusion.HiddenFiles => "Settings.Ignore.HiddenFiles",
			ProjectExclusion.DotFolders => "Settings.Ignore.DotFolders",
			ProjectExclusion.DotFiles => "Settings.Ignore.DotFiles",
			ProjectExclusion.EmptyFolders => "Settings.Ignore.EmptyFolders",
			ProjectExclusion.EmptyFiles => "Settings.Ignore.EmptyFiles",
			ProjectExclusion.ExtensionlessFiles => "Settings.Ignore.ExtensionlessFiles",
			_ => throw new ArgumentOutOfRangeException(nameof(exclusion), exclusion, null)
		});

	private static string FormatBytes(long bytes)
	{
		string[] units = ["B", "KB", "MB", "GB", "TB"];
		var value = Math.Max(0, bytes);
		var display = (double)value;
		var unit = 0;
		while (display >= 1024 && unit < units.Length - 1)
		{
			display /= 1024;
			unit++;
		}

		return unit == 0
			? $"{value} {units[unit]}"
			: $"{display:0.##} {units[unit]}";
	}

	private static string QuoteForDisplay(string value) =>
		value.Any(char.IsWhiteSpace)
			? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
			: value;

	private string L(string key) => services.Localization[key];

	private static void RegisterMonochromeScheme()
	{
		var terminalDefault = new global::Terminal.Gui.Drawing.Attribute(Color.None, Color.None);
		SchemeManager.AddScheme(
			TerminalWorkspacePresentationPolicy.MonochromeSchemeName,
			new Scheme(terminalDefault));
	}

	private enum TerminalWorkspacePane
	{
		Tree,
		Preview
	}
}
