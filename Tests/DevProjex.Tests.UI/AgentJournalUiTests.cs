using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DevProjex.Avalonia.Views;
using DevProjex.Application.Services;
using DevProjex.Infrastructure.LiveContext;
using DevProjex.Infrastructure.ResourceStore;
using DevProjex.Kernel.Abstractions;
using DevProjex.Kernel.Models;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class AgentJournalUiTests(UiWorkspaceFixture workspace)
{
	[AvaloniaFact]
	public async Task JournalMenuOpensOneModelessWindowAndLiveChangesRefreshCalls()
	{
		var fixture = JournalFixture.Create(workspace.Project.RootPath);
		var reader = new RecordingJournalReader(fixture.Sessions, fixture.Calls);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			workspace.Project,
			configureServices: services => services with
			{
				AgentJournalReader = reader,
				AgentJournalReceiptFormatter = new RecordingFormatter()
			});

		try
		{
			var mcpMenu = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "McpMenuItem");
			var itemNames = mcpMenu.Items.OfType<MenuItem>().Select(static item => item.Name ?? string.Empty).ToArray();
			Assert.Equal(
				["McpLiveContextMenuItem", "McpStandardMenuItem", "McpJournalMenuItem", "McpDocumentationMenuItem"],
				itemNames);

			var journalItem = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "McpJournalMenuItem");
			Assert.True(journalItem.IsEnabled);
			await UiTestDriver.RaiseMenuItemClickAsync(journalItem);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => window.OwnedWindows.OfType<AgentJournalWindow>().Count() == 1,
				"agent journal window to open");

			var journal = Assert.Single(window.OwnedWindows.OfType<AgentJournalWindow>());
			Assert.False(journal.ShowInTaskbar);
			Assert.Equal("Agent journal", journal.Title);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => journal.ViewModel.Sessions.Count == 2 && journal.ViewModel.Calls.Count == 1,
				"journal fixture to load");
			Assert.Contains("outside selection", journal.ViewModel.Calls[0].Notices, StringComparison.Ordinal);
			Assert.Contains(
				$"≈{AgentJournalPresentation.FormatNumber(1_200)} tokens",
				journal.ViewModel.FooterText,
				StringComparison.Ordinal);

			await UiTestDriver.RaiseMenuItemClickAsync(journalItem);
			Assert.Single(window.OwnedWindows.OfType<AgentJournalWindow>());

			reader.AppendCall(fixture.LiveSession.Id, fixture.SecondCall);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => journal.ViewModel.Calls.Count == 2,
				"live journal call event to refresh the selected session");
			Assert.Equal("get_file", journal.ViewModel.Calls[1].Tool);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task JournalWithoutProjectShowsAllSessionsAndExplainsAnEmptyJournal()
	{
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
		var fixture = JournalFixture.Create(workspace.Project.RootPath);
		var journal = new AgentJournalWindow(
			new RecordingJournalReader(fixture.Sessions, fixture.Calls),
			new RecordingFormatter(),
			localization,
			currentProjectRoot: null);
		UiTestDriver.TrackTopLevelWindow(journal);
		journal.Show();
		try
		{
			await journal.RefreshAsync();
			Assert.False(journal.ViewModel.HasCurrentProject);
			Assert.False(journal.ViewModel.CurrentProjectOnly);
			Assert.Equal(2, journal.ViewModel.Sessions.Count);
		}
		finally
		{
			await UiTestDriver.CloseTopLevelWindowAsync(journal);
		}

		var empty = new AgentJournalWindow(
			new RecordingJournalReader(
				[],
				new Dictionary<string, IReadOnlyList<AgentJournalCall>>(StringComparer.Ordinal)),
			new RecordingFormatter(),
			localization,
			currentProjectRoot: null);
		UiTestDriver.TrackTopLevelWindow(empty);
		empty.Show();
		try
		{
			await empty.RefreshAsync();
			Assert.True(empty.ViewModel.IsEmpty);
			Assert.Contains("MCP menu", empty.ViewModel.EmptyText, StringComparison.Ordinal);
		}
		finally
		{
			await UiTestDriver.CloseTopLevelWindowAsync(empty);
		}
	}

	[AvaloniaFact]
	public async Task JournalExportsBothFormatsAndClearsOnlyTheActiveFilter()
	{
		var fixture = JournalFixture.Create(workspace.Project.RootPath);
		var reader = new RecordingJournalReader(fixture.Sessions, fixture.Calls);
		var formatter = new RecordingFormatter();
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
		var journal = new AgentJournalWindow(reader, formatter, localization, workspace.Project.RootPath)
		{
			Width = 1120,
			Height = 720
		};
		UiTestDriver.TrackTopLevelWindow(journal);
		journal.Show();
		try
		{
			await journal.RefreshAsync();
			var outputRoot = Path.Combine(Path.GetTempPath(), "devprojex-journal-b", "exports");
			Directory.CreateDirectory(outputRoot);
			var markdownPath = Path.Combine(outputRoot, "receipt.md");
			var jsonPath = Path.Combine(outputRoot, "receipt.json");
			await journal.ExportSelectedToPathAsync(markdownPath, json: false);
			await journal.ExportSelectedToPathAsync(jsonPath, json: true);

			Assert.Equal("markdown:live-session", await File.ReadAllTextAsync(markdownPath));
			Assert.Equal("json:live-session", await File.ReadAllTextAsync(jsonPath));
			Assert.Equal(1, formatter.MarkdownCalls);
			Assert.Equal(1, formatter.JsonCalls);

			var removed = await journal.ClearCurrentScopeAsync();
			Assert.Equal(2, removed);
			Assert.Equal(Path.GetFullPath(workspace.Project.RootPath), Assert.Single(reader.ClearRoots));
			Assert.Empty(journal.ViewModel.Sessions);
		}
		finally
		{
			await UiTestDriver.CloseTopLevelWindowAsync(journal);
		}
	}

	[AvaloniaFact]
	public async Task AgentActivityShowsLiveStatusMarksDeliveredFilesAndClearsWithoutChangingTreeState()
	{
		var fixture = JournalFixture.Create(workspace.Project.RootPath);
		var reader = new RecordingJournalReader(fixture.Sessions, fixture.Calls);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			workspace.Project,
			configureServices: services => services with { AgentJournalReader = reader });

		try
		{
			var activity = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "AgentActivityMenuItem");
			await UiTestDriver.RaiseMenuItemClickAsync(activity);
			var viewModel = UiTestDriver.GetViewModel(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => viewModel.AgentActivityVisible &&
					  viewModel.AgentActivityText.Contains(
						  "Codex · search_project «Configure»",
						  StringComparison.Ordinal),
				"live agent activity status");

			var deliveredPath = Path.GetFullPath(Path.Combine(
				workspace.Project.RootPath,
				"src",
				"AppHost",
				"Program.cs"));
			var deliveredNode = Assert.Single(
				viewModel.TreeNodes.SelectMany(static root => root.Flatten()),
				node => PathComparer.Default.Equals(node.FullPath, deliveredPath));
			Assert.Equal(1, deliveredNode.AgentDeliveryCount);
			Assert.Contains("1", deliveredNode.AgentDeliveryToolTip, StringComparison.Ordinal);
			var checkedBefore = deliveredNode.IsChecked;

			var newerSession = fixture.LiveSession with
			{
				Id = "new-live-session",
				StartedUtc = fixture.LiveSession.StartedUtc.AddMinutes(1)
			};
			var newerCall = fixture.SecondCall with
			{
				Sequence = 1,
				Tool = "get_file",
				Arguments = new Dictionary<string, string> { ["path"] = "README.md" },
				DeliveredPaths = ["README.md"]
			};
			reader.AddSession(newerSession, [newerCall], fixture.LiveSession.Id);
			var readmeNode = Assert.Single(
				viewModel.TreeNodes.SelectMany(static root => root.Flatten()),
				node => PathComparer.Default.Equals(
					node.FullPath,
					Path.Combine(workspace.Project.RootPath, "README.md")));
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => deliveredNode.AgentDeliveryCount == 0 && readmeNode.AgentDeliveryCount == 1,
				"a new live session to replace the previous delivery trace");

			viewModel.IsCompactMode = true;
			Assert.False(viewModel.AgentActivityVisible);
			viewModel.IsCompactMode = false;
			Assert.True(viewModel.AgentActivityVisible);

			await UiTestDriver.RaiseMenuItemClickAsync(activity);
			Assert.False(viewModel.IsAgentActivityEnabled);
			Assert.False(viewModel.AgentActivityVisible);
			Assert.Equal(0, deliveredNode.AgentDeliveryCount);
			Assert.Equal(checkedBefore, deliveredNode.IsChecked);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[Fact]
	public void AgentActivityPreferenceDefaultsOffAndPersistsExplicitChoice()
	{
		var root = Path.Combine(Path.GetTempPath(), "devprojex-journal-b", "preference", Guid.NewGuid().ToString("N"));
		var store = new AgentActivityPreferenceStore(() => root);
		Assert.False(store.Load());
		Assert.True(store.TrySave(enabled: true));
		Assert.True(new AgentActivityPreferenceStore(() => root).Load());
		Assert.True(store.TrySave(enabled: false));
		Assert.False(new AgentActivityPreferenceStore(() => root).Load());
	}

	[Fact]
	public void AgentJournalStringsExistInEveryInterfaceLanguage()
	{
		var requiredKeys = new[]
		{
			"Menu.Mcp.Journal",
			"Menu.View.AgentActivity",
			"AgentJournal.Title",
			"AgentJournal.Empty",
			"AgentJournal.Footer",
			"AgentJournal.Notice.OutsideSelection",
			"AgentJournal.Notice.Unavailable",
			"AgentActivity.Status.Files",
			"AgentActivity.Status.Tokens",
			"AgentActivity.Status.Calls",
			"AgentActivity.Tree.ToolTip"
		};
		var catalog = new JsonLocalizationCatalog();
		foreach (var language in Enum.GetValues<AppLanguage>())
		{
			var localized = catalog.Get(language);
			Assert.All(requiredKeys, key =>
			{
				Assert.True(localized.TryGetValue(key, out var value), $"Missing {language}/{key}.");
				Assert.False(string.IsNullOrWhiteSpace(value), $"Empty {language}/{key}.");
			});
		}
	}

	[AvaloniaFact]
	public async Task ReopeningProjectClearsPriorDeliveriesAndTracksOnlyLaterCalls()
	{
		var fixture = JournalFixture.Create(workspace.Project.RootPath);
		var reader = new RecordingJournalReader(fixture.Sessions, fixture.Calls);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			workspace.Project,
			configureServices: services => services with { AgentJournalReader = reader });

		try
		{
			var activity = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "AgentActivityMenuItem");
			await UiTestDriver.RaiseMenuItemClickAsync(activity);
			var viewModel = UiTestDriver.GetViewModel(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => viewModel.TreeNodes.SelectMany(static root => root.Flatten())
					.Any(static node => node.AgentDeliveryCount > 0),
				"the initial delivery trace");

			await UiTestDriver.OpenFolderAsync(
				window,
				workspace.Project.RootPath,
				fromDialog: false,
				recordRecentFolder: false);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => viewModel.AgentActivityVisible &&
				      viewModel.TreeNodes.SelectMany(static root => root.Flatten())
					      .All(static node => node.AgentDeliveryCount == 0),
				"project reopen to clear the previous delivery trace");

			reader.AppendCall(fixture.LiveSession.Id, fixture.SecondCall);
			var deliveredPath = Path.GetFullPath(Path.Combine(
				workspace.Project.RootPath,
				"src",
				"AppHost",
				"Program.cs"));
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => viewModel.TreeNodes.SelectMany(static root => root.Flatten())
					.Any(node => PathComparer.Default.Equals(node.FullPath, deliveredPath) &&
					             node.AgentDeliveryCount == 1),
				"a post-reopen call to create a new delivery trace");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task StandardSessionDoesNotAddLiveContextTitleSuffix()
	{
		var appDataPath = Path.Combine(
			workspace.Project.AppDataPath,
			"standard-title",
			Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(appDataPath);
		var registry = new LiveSessionRegistry(() => appDataPath);
		await using var session = registry.Start(
			[workspace.Project.RootPath],
			AgentJournalMode.Standard);
		session.UpdateClient("codex", "5.2");

		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			workspace.Project,
			appDataPathOverride: appDataPath);
		try
		{
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
			Assert.DoesNotContain("Live context", window.Title ?? string.Empty, StringComparison.Ordinal);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}
	}

	[AvaloniaFact]
	public async Task ResetDataClearsTheEntireJournalAfterConfirmation()
	{
		var fixture = JournalFixture.Create(workspace.Project.RootPath);
		var reader = new RecordingJournalReader(fixture.Sessions, fixture.Calls);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			workspace.Project,
			configureServices: services => services with { AgentJournalReader = reader });
		try
		{
			var reset = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "ResetDataMenuItem");
			var resetTask = UiTestDriver.RaiseMenuItemClickAsync(reset);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => window.OwnedWindows.Count == 1,
				"reset data confirmation");
			var confirmation = Assert.Single(window.OwnedWindows);
			Assert.Contains(
				"agent journal",
				string.Join(
					' ',
					confirmation.GetVisualDescendants().OfType<TextBlock>().Select(static text => text.Text)),
				StringComparison.OrdinalIgnoreCase);
			var confirm = Assert.Single(
				confirmation.GetVisualDescendants().OfType<Button>(),
				static button => Equals(button.Content, "Delete"));
			await UiTestDriver.RaiseButtonClickAsync(confirm);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => reader.ClearRoots.Count == 1,
				"reset data to clear the journal");
			await resetTask;
			Assert.Null(Assert.Single(reader.ClearRoots));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task JournalAndDeliveryTraceSnapshotsCoverEnglishRussianLightAndDark()
	{
		if (!string.Equals(
			    Environment.GetEnvironmentVariable("DEVPROJEX_CAPTURE_AGENT_JOURNAL"),
			    "1",
			    StringComparison.Ordinal))
		{
			return;
		}

		var outputRoot = Path.Combine(
			Path.GetTempPath(),
			"devprojex-journal-b",
			"snapshots");
		Directory.CreateDirectory(outputRoot);
		var application = Assert.IsType<App>(global::Avalonia.Application.Current);
		var originalTheme = application.RequestedThemeVariant;
		try
		{
			foreach (var (language, languageCode) in new[]
			{
				(AppLanguage.En, "en"),
				(AppLanguage.Ru, "ru")
			})
			{
				foreach (var (theme, themeCode) in new[]
				{
					(ThemeVariant.Light, "light"),
					(ThemeVariant.Dark, "dark")
				})
				{
					application.RequestedThemeVariant = theme;
					var fixture = JournalFixture.Create(workspace.Project.RootPath);
					var reader = new RecordingJournalReader(fixture.Sessions, fixture.Calls);
					LocalizationService? localization = null;
					var window = await UiTestDriver.CreateLoadedMainWindowAsync(
						workspace.Project,
						configureServices: services =>
						{
							localization = services.Localization;
							return services with { AgentJournalReader = reader };
						});
					try
					{
						application.RequestedThemeVariant = theme;
						localization!.SetLanguage(language);
						await UiTestDriver.WaitForSettledFramesAsync(frameCount: 12);
						var activity = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(
							window,
							"AgentActivityMenuItem");
						await UiTestDriver.RaiseMenuItemClickAsync(activity);
						var viewModel = UiTestDriver.GetViewModel(window);
						await UiTestDriver.WaitForConditionAsync(
							window,
							() => viewModel.AgentActivityVisible &&
							      viewModel.TreeNodes.SelectMany(static root => root.Flatten())
								      .Any(static node => node.AgentDeliveryCount > 0),
							"agent activity snapshot state");
						foreach (var node in viewModel.TreeNodes.SelectMany(static root => root.Flatten()))
							node.IsExpanded = true;
						await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
						await SaveSnapshotAsync(
							window,
							Path.Combine(outputRoot, $"tree-{languageCode}-{themeCode}.png"));

						var journal = new AgentJournalWindow(
							reader,
							new RecordingFormatter(),
							localization,
							workspace.Project.RootPath);
						UiTestDriver.TrackTopLevelWindow(journal);
						journal.Show(window);
						try
						{
							await journal.RefreshAsync();
							await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
							await SaveSnapshotAsync(
								journal,
								Path.Combine(outputRoot, $"journal-{languageCode}-{themeCode}.png"));
						}
						finally
						{
							await UiTestDriver.CloseTopLevelWindowAsync(journal);
						}
					}
					finally
					{
						await UiTestDriver.CloseWindowAsync(window);
					}
				}
			}
		}
		finally
		{
			application.RequestedThemeVariant = originalTheme;
		}

		var snapshots = Directory.GetFiles(outputRoot, "*.png", SearchOption.TopDirectoryOnly);
		Assert.Equal(8, snapshots.Length);
		Assert.All(snapshots, path => Assert.True(new FileInfo(path).Length > 1_000, path));
	}

	private static async Task SaveSnapshotAsync(TopLevel topLevel, string path)
	{
		await topLevel.Dispatcher.InvokeAsync(() =>
		{
			using var bitmap = topLevel.CaptureRenderedFrame();
			Assert.NotNull(bitmap);
			bitmap.Save(path, PngBitmapEncoderOptions.Default);
		}, DispatcherPriority.Render);
	}

	private sealed class RecordingFormatter : IAgentJournalReceiptFormatter
	{
		public int MarkdownCalls { get; private set; }
		public int JsonCalls { get; private set; }

		public string FormatMarkdown(AgentJournalReceipt receipt)
		{
			MarkdownCalls++;
			return $"markdown:{receipt.Session.Id}";
		}

		public string FormatJson(AgentJournalReceipt receipt)
		{
			JsonCalls++;
			return $"json:{receipt.Session.Id}";
		}
	}

	private sealed class RecordingJournalReader(
		IEnumerable<AgentJournalSession> sessions,
		IReadOnlyDictionary<string, IReadOnlyList<AgentJournalCall>> calls) : IAgentJournalReader
	{
		private readonly List<AgentJournalSession> _sessions = [.. sessions];
		private readonly Dictionary<string, List<AgentJournalCall>> _calls = calls.ToDictionary(
			static pair => pair.Key,
			static pair => pair.Value.ToList(),
			StringComparer.Ordinal);
		private readonly Dictionary<string, Channel<AgentJournalChange>> _changes = new(StringComparer.Ordinal);

		public AgentJournalRetentionPolicy Retention => AgentJournalRetentionPolicy.Default;
		public List<string?> ClearRoots { get; } = [];

		public ValueTask<IReadOnlyList<AgentJournalSession>> ListSessionsAsync(
			string? projectRoot = null,
			int limit = 200,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			IReadOnlyList<AgentJournalSession> result = _sessions
				.Where(session => projectRoot is null || session.Roots.Any(root =>
					PathComparer.Default.Equals(Path.GetFullPath(root.ConfiguredPath), Path.GetFullPath(projectRoot))))
				.Take(limit)
				.ToArray();
			return ValueTask.FromResult(result);
		}

		public ValueTask<IReadOnlyList<AgentJournalCall>> ReadCallsAsync(
			string sessionId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.FromResult<IReadOnlyList<AgentJournalCall>>(
				_calls.TryGetValue(sessionId, out var sessionCalls) ? sessionCalls.ToArray() : []);
		}

		public ValueTask<AgentJournalReceipt?> ReadReceiptAsync(
			string sessionId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var session = _sessions.FirstOrDefault(candidate =>
				string.Equals(candidate.Id, sessionId, StringComparison.Ordinal));
			if (session is null)
				return ValueTask.FromResult<AgentJournalReceipt?>(null);
			var sessionCalls = _calls.TryGetValue(sessionId, out var values) ? values.ToArray() : [];
			var deliveredPaths = sessionCalls
				.SelectMany(static call => call.DeliveredPaths)
				.GroupBy(static path => path, PathComparer.Default)
				.Select(static group => new AgentJournalDeliveredPath(group.Key, group.LongCount()))
				.ToArray();
			return ValueTask.FromResult<AgentJournalReceipt?>(new AgentJournalReceipt(
				session,
				session.Totals,
				deliveredPaths,
				sessionCalls));
		}

		public async IAsyncEnumerable<AgentJournalChange> WatchChangesAsync(
			string sessionId,
			[EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			var channel = GetChannel(sessionId);
			await foreach (var change in channel.Reader.ReadAllAsync(cancellationToken))
				yield return change;
		}

		public ValueTask<int> ClearAsync(
			string? projectRoot = null,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ClearRoots.Add(projectRoot);
			var removed = _sessions.RemoveAll(session => projectRoot is null || session.Roots.Any(root =>
				PathComparer.Default.Equals(Path.GetFullPath(root.ConfiguredPath), Path.GetFullPath(projectRoot))));
			return ValueTask.FromResult(removed);
		}

		public void AppendCall(string sessionId, AgentJournalCall call)
		{
			_calls[sessionId].Add(call);
			GetChannel(sessionId).Writer.TryWrite(new AgentJournalChange(
				sessionId,
				AgentJournalChangeKind.CallAppended,
				call.Sequence));
		}

		public void AddSession(
			AgentJournalSession session,
			IReadOnlyList<AgentJournalCall> calls,
			string notifySessionId)
		{
			_sessions.Add(session);
			_calls.Add(session.Id, calls.ToList());
			GetChannel(notifySessionId).Writer.TryWrite(new AgentJournalChange(
				notifySessionId,
				AgentJournalChangeKind.CallAppended));
		}

		private Channel<AgentJournalChange> GetChannel(string sessionId)
		{
			if (!_changes.TryGetValue(sessionId, out var channel))
			{
				channel = Channel.CreateUnbounded<AgentJournalChange>();
				_changes.Add(sessionId, channel);
			}
			return channel;
		}
	}

	private sealed record JournalFixture(
		AgentJournalSession LiveSession,
		IReadOnlyList<AgentJournalSession> Sessions,
		IReadOnlyDictionary<string, IReadOnlyList<AgentJournalCall>> Calls,
		AgentJournalCall SecondCall)
	{
		public static JournalFixture Create(string root)
		{
			var started = new DateTimeOffset(2026, 9, 20, 9, 30, 0, TimeSpan.Zero);
			var liveTotals = new AgentJournalTotals(14, 4_800, 1_200, 3, 2, 1, 0);
			var live = new AgentJournalSession(
				"live-session",
				started,
				null,
				123,
				started,
				"Codex",
				"5.2",
				AgentJournalMode.Live,
				[new AgentJournalRoot(root, "DevProjex")],
				AgentJournalToolSet.Full,
				"5.2.0",
				true,
				liveTotals,
				true);
			var standard = live with
			{
				Id = "standard-session",
				StartedUtc = started.AddMinutes(-10),
				EndedUtc = started.AddMinutes(-5),
				Mode = AgentJournalMode.Standard,
				IsLive = false,
				Totals = new AgentJournalTotals(2, 240, 60, 1, 0, 0, 1)
			};
			var firstCall = new AgentJournalCall(
				1,
				started.AddSeconds(2),
				"search_project",
				0,
				new Dictionary<string, string> { ["query"] = "Configure" },
				7,
				25,
				1_200,
				300,
				2,
				["src/AppHost/Program.cs"],
				0,
				2,
				1,
				[AgentJournalNoticeCodes.OutsideSelection],
				null);
			var secondCall = firstCall with
			{
				Sequence = 2,
				Tool = "get_file",
				Arguments = new Dictionary<string, string> { ["path"] = "src/AppHost/Program.cs" },
				DeliveredPaths = ["src/AppHost/Program.cs"]
			};
			return new JournalFixture(
				live,
				[live, standard],
				new Dictionary<string, IReadOnlyList<AgentJournalCall>>
				{
					[live.Id] = [firstCall],
					[standard.Id] = []
				},
				secondCall);
		}
	}
}
