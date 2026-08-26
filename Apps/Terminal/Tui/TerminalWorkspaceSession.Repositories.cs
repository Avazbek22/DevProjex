using System.Collections.ObjectModel;
using System.Globalization;
using DevProjex.Terminal.Execution;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DevProjex.Terminal.Tui;

#pragma warning disable CS0618

internal sealed partial class TerminalWorkspaceSession
{
	private void OpenRecentWorkspaces()
	{
		while (!_stopping)
		{
			var loadResult = _services.RecentProjectsStore.LoadForStartupWithStatus(
				TimeSpan.FromMilliseconds(200));
			_recentProjectsSnapshot = loadResult.Database;
			if (loadResult.Status != RecentProjectsLoadStatus.Success)
			{
				var retry = ShowChoice(
					L("Terminal.Tui.Welcome.Recent"),
					L("Terminal.Tui.Recent.StorageUnavailable"),
					L("Terminal.Tui.Back"),
					L("Terminal.Tui.Retry"));
				if (retry != 1)
					return;
				continue;
			}

			var workspaces = BuildRecentWorkspaces(loadResult.Database);
			if (workspaces.Count == 0)
			{
				ShowNotice(
					L("Terminal.Tui.Welcome.Recent"),
					L("Terminal.Tui.NoneAvailable"),
					TerminalWorkspaceTheme.Warning);
				return;
			}

			var decision = SelectRecentWorkspace(workspaces);
			if (decision.Kind == TerminalRecentWorkspaceDecisionKind.Back ||
			    decision.Workspace is null)
			{
				return;
			}

			var workspace = decision.Workspace;
			_recentWorkspaceSelectionKey = workspace.IdentityKey;
			if (decision.Kind == TerminalRecentWorkspaceDecisionKind.Remove)
			{
				var confirmed = Confirm(
					L("Terminal.Tui.Recent.Remove"),
					L("Terminal.Tui.RecentRepositories.RemoveHistoryOnly"));
				if (!confirmed)
					continue;
				_recentProjectsSnapshot = workspace.Kind == RecentWorkspaceKind.Repository
					? _services.RecentProjectsStore.RemoveRepository(
						_recentProjectsSnapshot,
						workspace.Source)
					: _services.RecentProjectsStore.RemoveFolder(
						_recentProjectsSnapshot,
						workspace.Source);
				_recentWorkspaceSelectionKey = null;
				continue;
			}

			if (workspace.Kind == RecentWorkspaceKind.Folder)
			{
				if (!TryResolveDirectory(workspace.Source, out var project))
				{
					var remove = ShowChoice(
						L("Terminal.Tui.Error"),
						$"{L("Terminal.Tui.Error.ProjectUnavailable")}\n\n" +
						TerminalRecentWorkspacePresentation.DisplaySource(workspace),
						L("Terminal.Tui.Back"),
						L("Terminal.Tui.Recent.Remove"));
					if (remove == 1)
					{
						_recentProjectsSnapshot = _services.RecentProjectsStore.RemoveFolder(
							_recentProjectsSnapshot,
							workspace.Source);
						_recentWorkspaceSelectionKey = null;
					}
					continue;
				}
				if (!TryResolveAutomaticProfileInteractively(project, out var profile))
					continue;
				BeginOpenProject(project, profile, TerminalProjectOpenSource.Recent);
				return;
			}

			BeginResolveRecentRepository(workspace);
			return;
		}
	}

	private IReadOnlyList<RecentWorkspaceDescriptor> BuildRecentWorkspaces(
		RecentProjectsDb database) =>
		_services.RecentWorkspacesService.Project(
			database.RecentFolders
				.Select(static entry => new RecentWorkspaceSource(
					RecentWorkspaceKind.Folder,
					entry.Path,
					entry.OpenedUtc))
				.Concat(database.RecentRepositories.Select(static entry =>
					new RecentWorkspaceSource(
						RecentWorkspaceKind.Repository,
						entry.Url,
						entry.OpenedUtc))));

	private TerminalRecentWorkspaceDecision SelectRecentWorkspace(
		IReadOnlyList<RecentWorkspaceDescriptor> workspaces)
	{
		var dialogWidth = ResolveDialogWidth(106);
		var height = Math.Clamp(
			workspaces.Count + 15,
			16,
			Math.Max(16, _application.Screen.Height - 2));
		using var dialog = CreateDialog(
			L("Terminal.Tui.Welcome.Recent"),
			dialogWidth,
			height);
		AlignWelcomeDialogAfterActions(dialog, dialogWidth);
		var description = new TerminalLiteralLabel
		{
			X = 1,
			Y = 0,
			Width = Dim.Fill(1),
			Text = L("Terminal.Tui.Welcome.Recent.Description"),
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		string KindLabel(RecentWorkspaceKind kind) =>
			kind == RecentWorkspaceKind.Repository
				? "Git"
				: L("Terminal.Tui.Folder");
		string OpenedLabel(DateTimeOffset openedUtc)
		{
			var localDate = openedUtc.ToLocalTime().Date;
			var today = DateTimeOffset.Now.Date;
			if (localDate == today)
				return L("Terminal.Tui.Recent.Today");
			if (localDate == today.AddDays(-1))
				return L("Terminal.Tui.Recent.Yesterday");
			return openedUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
		}
		var rows = new ObservableCollection<TerminalRecentWorkspaceRow>(
			workspaces.Select(workspace =>
				new TerminalRecentWorkspaceRow(workspace, KindLabel, OpenedLabel)));
		var list = new ListView
		{
			X = 1,
			Y = 2,
			Width = Dim.Fill(1),
			Height = Math.Clamp(workspaces.Count, 2, 10),
			SchemeName = TerminalWorkspaceTheme.List
		};
		list.SetSource(rows);
		var selectedIndex = string.IsNullOrWhiteSpace(_recentWorkspaceSelectionKey)
			? 0
			: workspaces
				.Select((workspace, index) => (workspace, index))
				.FirstOrDefault(pair => string.Equals(
					pair.workspace.IdentityKey,
					_recentWorkspaceSelectionKey,
					StringComparison.OrdinalIgnoreCase))
				.index;
		list.SelectedItem = Math.Clamp(selectedIndex, 0, workspaces.Count - 1);
		var details = new TextView
		{
			X = 1,
			Y = Pos.Bottom(list) + 1,
			Width = Dim.Fill(1),
			Height = Dim.Fill(1),
			ReadOnly = true,
			WordWrap = true,
			CanFocus = false,
			SchemeName = TerminalWorkspaceTheme.Base
		};
		var result = new TerminalRecentWorkspaceDecision(
			TerminalRecentWorkspaceDecisionKind.Back);

		void UpdateSelection()
		{
			var index = Math.Clamp(list.SelectedItem ?? 0, 0, workspaces.Count - 1);
			for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
				rows[rowIndex].IsSelected = rowIndex == index;
			list.SetNeedsDraw();
			var workspace = workspaces[index];
			_recentWorkspaceSelectionKey = workspace.IdentityKey;
			var sourceWidth = Math.Max(8, dialogWidth - 8);
			details.Text =
				$"{KindLabel(workspace.Kind)}{PanelSeparator}" +
				$"{TerminalRecentWorkspacePresentation.DisplayName(workspace)}\n" +
				$"{FitPathToWidth(workspace.DisplaySource, sourceWidth)}\n" +
				$"{L("Terminal.Tui.Recent.LastOpened")}: " +
				workspace.OpenedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
		}

		void SelectCurrent(TerminalRecentWorkspaceDecisionKind kind)
		{
			var index = Math.Clamp(list.SelectedItem ?? 0, 0, workspaces.Count - 1);
			result = new TerminalRecentWorkspaceDecision(kind, workspaces[index]);
			_application.RequestStop(dialog);
		}

		list.ValueChanged += (_, _) => UpdateSelection();
		list.Accepted += (_, _) => SelectCurrent(TerminalRecentWorkspaceDecisionKind.Open);
		dialog.Add(description, list, details);
		dialog.AddButton(CreateDialogButton(L("Terminal.Tui.Back")));
		var remove = CreateDialogButton(L("Terminal.Tui.Recent.Remove"));
		remove.Accepted += (_, _) => SelectCurrent(TerminalRecentWorkspaceDecisionKind.Remove);
		dialog.AddButton(remove);
		var open = CreateDialogButton(L("Terminal.Tui.Open"));
		open.Accepted += (_, _) => SelectCurrent(TerminalRecentWorkspaceDecisionKind.Open);
		dialog.AddButton(open);
		UpdateSelection();
		RunOverlay(dialog, list);
		return result;
	}

	private void BeginResolveRecentRepository(RecentWorkspaceDescriptor workspace)
	{
		ShowLoading(
			L("Terminal.Tui.LoadingProject"),
			workspace.DisplaySource);
		var operationCts = ReplaceActiveOperation();
		_activeOperationTask = TrackBackgroundTask(Task.Run(async () =>
		{
			try
			{
				var cache = await _services.RepositoryCacheCatalog
					.FindAsync(workspace.Source, operationCts.Token)
					.ConfigureAwait(false);
				await InvokeAsync(() =>
				{
					if (cache.State == RepositoryCacheState.Ready &&
					    cache.LocalPath is { Length: > 0 } localPath &&
					    Directory.Exists(localPath))
					{
						if (!TryResolveAutomaticProfileInteractively(localPath, out var profile))
						{
							ShowWelcome(TerminalWelcomeActionKind.RecentWorkspaces);
							_application.Invoke(OpenRecentWorkspaces);
							return true;
						}
						var identity = ProjectSourceIdentityResolver.CreateCloneIdentity(
							workspace.Source,
							cache.RepositoryName,
							cache.Branch,
							cache.CommitHash);
						BeginOpenProject(
							localPath,
							profile,
							TerminalProjectOpenSource.RecentRepository,
							identity);
						return true;
					}

					ShowWelcome(TerminalWelcomeActionKind.RecentWorkspaces);
					var repository = new TerminalRecentRepository(
						workspace.Source,
						workspace.OpenedUtc,
						cache);
					if (!HandleUnavailableRepository(
						    repository,
						    cache.State == RepositoryCacheState.Damaged))
					{
						_application.Invoke(OpenRecentWorkspaces);
					}
					return true;
				}).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
			{
				ReturnToWelcomeAfterCancellation(operationCts);
			}
			catch
			{
				ReturnToWelcomeWithError(
					operationCts,
					"DPX-TUI-RECENT-REPOSITORIES-UNAVAILABLE",
					L("Terminal.Tui.RecentRepositories.Error"));
			}
			finally
			{
				ReleaseActiveOperation(operationCts);
			}
		}, CancellationToken.None));
	}

	private bool HandleUnavailableRepository(
		TerminalRecentRepository repository,
		bool damaged)
	{
		var decision = ShowChoice(
			L("Terminal.Tui.Welcome.Recent"),
			$"{L(damaged
				? "Terminal.Tui.RecentRepositories.CacheDamaged"
				: "Terminal.Tui.RecentRepositories.CacheMissing")}\n\n{repository.SafeDisplayUrl}",
			L("Terminal.Tui.Back"),
			L("Terminal.Tui.Recent.Remove"),
			L("Terminal.Tui.RecentRepositories.CloneAgain"));
		if (decision == 1)
		{
			_recentProjectsSnapshot = _services.RecentProjectsStore.RemoveRepository(
				_recentProjectsSnapshot,
				repository.Url);
			return false;
		}
		if (decision != 2)
			return false;

		BeginCloneRepository(repository.Url, returnToRepositoryHistory: true);
		return true;
	}

	private void ReturnToRepositoryHistoryAfterCancellation(
		CancellationTokenSource operationCts)
	{
		if (_stopping || !ReferenceEquals(_activeOperationCts, operationCts))
			return;
		_application.Invoke(() =>
		{
			ShowWelcome(TerminalWelcomeActionKind.RecentWorkspaces);
			ShowWelcomeStatus(L("Terminal.Tui.OperationCanceled"), TerminalWorkspaceTheme.Warning);
			_application.Invoke(OpenRecentWorkspaces);
		});
	}

	private void ReturnToRepositoryHistoryWithError(
		CancellationTokenSource operationCts,
		string code,
		string message)
	{
		if (_stopping || !ReferenceEquals(_activeOperationCts, operationCts))
			return;
		_application.Invoke(() =>
		{
			ShowWelcome(TerminalWelcomeActionKind.RecentWorkspaces);
			_application.Invoke(() =>
			{
				ShowError(code, message);
				if (!_stopping)
					OpenRecentWorkspaces();
			});
		});
	}
}

#pragma warning restore CS0618
