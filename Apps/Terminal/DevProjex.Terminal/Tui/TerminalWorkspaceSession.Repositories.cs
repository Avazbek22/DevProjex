using System.Collections.ObjectModel;
using System.Globalization;
using DevProjex.Terminal.Execution;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DevProjex.Terminal.Tui;

#pragma warning disable CS0618

internal sealed partial class TerminalWorkspaceSession
{
	private string? _recentRepositorySelectionUrl;

	private void BeginOpenRecentRepositories()
	{
		var loadResult = _services.RecentProjectsStore.LoadForStartupWithStatus(
			TimeSpan.FromMilliseconds(200));
		_recentProjectsSnapshot = loadResult.Database;
		_recentProjectsLoadStatus = loadResult.Status;
		if (loadResult.Status != RecentProjectsLoadStatus.Success)
		{
			var retry = ShowChoice(
				L("Terminal.Tui.Welcome.RecentRepositories"),
				L("Terminal.Tui.Recent.StorageUnavailable"),
				L("Terminal.Tui.Back"),
				L("Terminal.Tui.Retry"));
			if (retry == 1)
				BeginOpenRecentRepositories();
			return;
		}

		if (loadResult.Database.RecentRepositories.Count == 0)
		{
			ShowNotice(
				L("Terminal.Tui.Welcome.RecentRepositories"),
				L("Terminal.Tui.NoneAvailable"),
				TerminalWorkspaceTheme.Warning);
			return;
		}

		ShowLoading(
			L("Terminal.Tui.RecentRepositories.Loading"),
			L("Terminal.Tui.RecentRepositories.CacheLookup"));
		var operationCts = ReplaceActiveOperation();
		_activeOperationTask = Task.Run(async () =>
		{
			try
			{
				var repositories = new List<TerminalRecentRepository>(
					loadResult.Database.RecentRepositories.Count);
				foreach (var entry in loadResult.Database.RecentRepositories)
				{
					operationCts.Token.ThrowIfCancellationRequested();
					var cache = await _services.RepositoryCacheCatalog
						.FindAsync(entry.Url, operationCts.Token)
						.ConfigureAwait(false);
					repositories.Add(new TerminalRecentRepository(entry.Url, entry.OpenedUtc, cache));
				}

				await InvokeAsync(() =>
				{
					ShowWelcome();
					_application.Driver?.ClearContents();
					_application.LayoutAndDraw();
					OpenRecentRepositoryList(repositories);
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
		}, CancellationToken.None);
	}

	private void OpenRecentRepositoryList(
		IReadOnlyList<TerminalRecentRepository> initialRepositories)
	{
		var repositories = initialRepositories.ToList();
		while (!_stopping && repositories.Count > 0)
		{
			var decision = SelectRecentRepository(repositories);
			if (decision.Kind == TerminalRecentRepositoryDecisionKind.Back ||
			    decision.Repository is null)
			{
				return;
			}

			var repository = decision.Repository;
			_recentRepositorySelectionUrl = repository.Url;
			if (decision.Kind == TerminalRecentRepositoryDecisionKind.Remove)
			{
				var confirmed = ShowChoice(
					L("Terminal.Tui.RecentRepositories.Remove"),
					L("Terminal.Tui.RecentRepositories.RemoveHistoryOnly"),
					L("Terminal.Tui.Back"),
					L("Terminal.Tui.Recent.Remove"));
				if (confirmed == 1)
				{
					_recentProjectsSnapshot = _services.RecentProjectsStore.RemoveRepository(
						_recentProjectsSnapshot,
						repository.Url);
					repositories.RemoveAll(item =>
						RepositoryUrlUtility.AreEquivalent(item.Url, repository.Url));
					_recentRepositorySelectionUrl = null;
				}
				continue;
			}

			switch (repository.Cache.State)
			{
				case RepositoryCacheState.Ready
					when repository.Cache.LocalPath is { Length: > 0 } localPath &&
					     Directory.Exists(localPath):
				{
					if (!TryResolveAutomaticProfileInteractively(localPath, out var profile))
						continue;

					var identity = ProjectSourceIdentityResolver.CreateCloneIdentity(
						repository.Url,
						repository.Name,
						repository.Cache.Branch,
						repository.Cache.CommitHash);
					BeginOpenProject(
						localPath,
						profile,
						TerminalProjectOpenSource.RecentRepository,
						identity);
					return;
				}
				case RepositoryCacheState.Damaged:
				if (HandleUnavailableRepository(repository, damaged: true))
					return;
				break;
				default:
				if (HandleUnavailableRepository(repository, damaged: false))
					return;
				break;
			}
		}

		if (!_stopping && repositories.Count == 0)
		{
			ShowNotice(
				L("Terminal.Tui.Welcome.RecentRepositories"),
				L("Terminal.Tui.NoneAvailable"),
				TerminalWorkspaceTheme.Warning);
		}
	}

	private bool HandleUnavailableRepository(
		TerminalRecentRepository repository,
		bool damaged)
	{
		var decision = ShowChoice(
			L("Terminal.Tui.Welcome.RecentRepositories"),
			$"{L(damaged
				? "Terminal.Tui.RecentRepositories.CacheDamaged"
				: "Terminal.Tui.RecentRepositories.CacheMissing")}\n\n{repository.Url}",
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

	private TerminalRecentRepositoryDecision SelectRecentRepository(
		IReadOnlyList<TerminalRecentRepository> repositories)
	{
		var dialogWidth = ResolveDialogWidth(98);
		var detailsWidth = Math.Max(8, dialogWidth - 6);
		var height = Math.Clamp(
			repositories.Count + 18,
			19,
			Math.Max(19, _application.Screen.Height - 2));
		using var dialog = CreateDialog(
			L("Terminal.Tui.Welcome.RecentRepositories"),
			dialogWidth,
			height);
		var description = new Label
		{
			X = 1,
			Y = 0,
			Width = Dim.Fill(1),
			Text = L("Terminal.Tui.RecentRepositories.Description"),
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		var rows = new ObservableCollection<TerminalRecentRepositoryRow>(
			repositories.Select(static repository => new TerminalRecentRepositoryRow(repository)));
		var list = new ListView
		{
			X = 1,
			Y = 2,
			Width = Dim.Fill(1),
			Height = Math.Min(Math.Max(3, repositories.Count), 8),
			SchemeName = TerminalWorkspaceTheme.List
		};
		list.SetSource(rows);
		var selectedIndex = string.IsNullOrWhiteSpace(_recentRepositorySelectionUrl)
			? 0
			: repositories
				.Select((repository, index) => (repository, index))
				.FirstOrDefault(pair => RepositoryUrlUtility.AreEquivalent(
					pair.repository.Url,
					_recentRepositorySelectionUrl))
				.index;
		list.SelectedItem = Math.Clamp(selectedIndex, 0, repositories.Count - 1);
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
		var result = new TerminalRecentRepositoryDecision(
			TerminalRecentRepositoryDecisionKind.Back);

		void UpdateSelection()
		{
			var index = Math.Clamp(list.SelectedItem ?? 0, 0, repositories.Count - 1);
			for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
				rows[rowIndex].IsSelected = rowIndex == index;
			list.SetNeedsDraw();
			var repository = repositories[index];
			_recentRepositorySelectionUrl = repository.Url;
			details.Text = BuildRepositoryDetails(repository, detailsWidth);
		}

		void SelectCurrent(TerminalRecentRepositoryDecisionKind kind)
		{
			var index = Math.Clamp(list.SelectedItem ?? 0, 0, repositories.Count - 1);
			result = new TerminalRecentRepositoryDecision(kind, repositories[index]);
		}

		list.ValueChanged += (_, _) => UpdateSelection();
		list.Accepted += (_, _) =>
		{
			SelectCurrent(TerminalRecentRepositoryDecisionKind.Open);
			_application.RequestStop(dialog);
		};
		dialog.Add(description, list, details);
		dialog.AddButton(new Button { Text = L("Terminal.Tui.Back") });
		var remove = new Button { Text = L("Terminal.Tui.Recent.Remove") };
		remove.Accepted += (_, _) => SelectCurrent(TerminalRecentRepositoryDecisionKind.Remove);
		dialog.AddButton(remove);
		var open = new Button { Text = L("Terminal.Tui.Open") };
		open.Accepted += (_, _) => SelectCurrent(TerminalRecentRepositoryDecisionKind.Open);
		dialog.AddButton(open);
		UpdateSelection();
		RunOverlay(dialog, list);
		return result;
	}

	private string BuildRepositoryDetails(
		TerminalRecentRepository repository,
		int width)
	{
		var cacheState = repository.Cache.State switch
		{
			RepositoryCacheState.Ready => L("Terminal.Tui.RecentRepositories.Cached"),
			RepositoryCacheState.Damaged => L("Terminal.Tui.RecentRepositories.Damaged"),
			_ => L("Terminal.Tui.RecentRepositories.NotCached")
		};
		var owner = TryResolveRepositoryOwner(repository.Url);
		var output = new StringBuilder()
			.Append(L("Terminal.Tui.RecentRepositories.Repository")).Append(": ")
			.AppendLine(repository.Name);
		if (owner.Length > 0)
			output.Append(L("Terminal.Tui.RecentRepositories.Owner")).Append(": ").AppendLine(owner);
		output.Append(L("Terminal.Tui.RecentRepositories.Cache")).Append(": ").AppendLine(cacheState);
		if (repository.Cache.Branch is { Length: > 0 } branch)
			output.Append(L("Terminal.Tui.RecentRepositories.Branch")).Append(": ").AppendLine(branch);
		output.Append(L("Terminal.Tui.Recent.LastOpened")).Append(": ")
			.AppendLine(repository.OpenedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
		output.Append(L("Terminal.Tui.RepositoryUrl")).AppendLine()
			.Append(FitPathToWidth(repository.Url, width));
		return output.ToString();
	}

	private static string TryResolveRepositoryOwner(string repositoryUrl)
	{
		var normalized = RepositoryUrlUtility.Normalize(repositoryUrl);
		if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
		{
			var segments = uri.AbsolutePath
				.Split('/', StringSplitOptions.RemoveEmptyEntries);
			return segments.Length >= 2 ? $"{uri.Host}/{segments[^2]}" : uri.Host;
		}

		var colon = normalized.IndexOf(':');
		var slash = normalized.LastIndexOf('/');
		if (colon <= 0 || slash <= colon)
			return string.Empty;

		var authority = normalized[..colon];
		var at = authority.LastIndexOf('@');
		var host = at >= 0 ? authority[(at + 1)..] : authority;
		var ownerPath = normalized[(colon + 1)..slash].Trim('/');
		return ownerPath.Length > 0 ? $"{host}/{ownerPath}" : host;
	}

	private void ReturnToRepositoryHistoryAfterCancellation(
		CancellationTokenSource operationCts)
	{
		if (_stopping || !ReferenceEquals(_activeOperationCts, operationCts))
			return;
		_application.Invoke(() =>
		{
			ShowWelcome();
			ShowWelcomeStatus(L("Terminal.Tui.OperationCanceled"), TerminalWorkspaceTheme.Warning);
			_application.Invoke(BeginOpenRecentRepositories);
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
			ShowWelcome();
			_application.Invoke(() =>
			{
				ShowError(code, message);
				if (!_stopping)
					BeginOpenRecentRepositories();
			});
		});
	}
}

#pragma warning restore CS0618
