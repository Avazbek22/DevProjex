using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Avalonia.Automation;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DevProjex.Avalonia.Views;
using DevProjex.Application.Services;
using DevProjex.Infrastructure.AgentJournal;
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
			var expectedSize = AgentJournalWindow.ResolveInitialSize(window.ClientSize);
			Assert.Equal(expectedSize.Width, journal.Width, precision: 3);
			Assert.Equal(expectedSize.Height, journal.Height, precision: 3);
			Assert.Equal(AgentJournalWindow.MinimumWindowWidth, journal.MinWidth);
			Assert.Equal(AgentJournalWindow.MinimumWindowHeight, journal.MinHeight);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
			AssertHeaderColumnsFit(journal, "JournalSessionsColumnsHeader");
			AssertHeaderColumnsFit(journal, "JournalCallsColumnsHeader");
			Assert.Equal(WindowStartupLocation.CenterOwner, journal.WindowStartupLocation);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => journal.ViewModel.Sessions.Count == 2 && journal.ViewModel.Calls.Count == 1,
				"journal fixture to load");
			Assert.Contains("outside selection", journal.ViewModel.Calls[0].Notices, StringComparison.Ordinal);
			Assert.Equal("sec. 2 · priv. 1", journal.ViewModel.Sessions[0].MaskedCompact);
			Assert.Equal("2 secrets · 1 private", journal.ViewModel.Sessions[0].Masked);
			var sessionsList = Assert.IsType<ListBox>(journal.FindControl<ListBox>("JournalSessionsList"));
			var compactMask = Assert.Single(
				sessionsList.GetVisualDescendants().OfType<TextBlock>(),
				static text => string.Equals(text.Text, "sec. 2 · priv. 1", StringComparison.Ordinal));
			Assert.Equal("2 secrets · 1 private", ToolTip.GetTip(compactMask));
			Assert.Contains(
				$"≈{AgentJournalPresentation.FormatNumber(1_200)} tokens",
				journal.ViewModel.FooterText,
				StringComparison.Ordinal);
			Assert.True(Assert.IsType<Border>(journal.FindControl<Border>("JournalFooterSurface")).IsVisible);

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
	public async Task JournalUsesAnOpaqueOwnerSurfaceAndTracksThemeChanges()
	{
		var application = Assert.IsType<App>(global::Avalonia.Application.Current);
		var originalTheme = application.RequestedThemeVariant;
		application.RequestedThemeVariant = ThemeVariant.Light;
		var fixture = JournalFixture.Create(workspace.Project.RootPath);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			workspace.Project,
			configureServices: services => services with
			{
				AgentJournalReader = new RecordingJournalReader(fixture.Sessions, fixture.Calls)
			});

		try
		{
			application.RequestedThemeVariant = ThemeVariant.Light;
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
			var journalItem = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "McpJournalMenuItem");
			await UiTestDriver.RaiseMenuItemClickAsync(journalItem);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => window.OwnedWindows.OfType<AgentJournalWindow>().Count() == 1,
				"opaque agent journal window");
			var journal = Assert.Single(window.OwnedWindows.OfType<AgentJournalWindow>());
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);

			AssertOpaqueJournalSurface(journal);
			var lightBackground = Assert.IsAssignableFrom<ISolidColorBrush>(journal.Background).Color;
			var lightSurfaceColors = CaptureJournalSurfaceColors(journal);

			application.RequestedThemeVariant = ThemeVariant.Dark;
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => window.ActualThemeVariant == ThemeVariant.Dark,
				"the journal owner to follow the application dark theme");
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => journal.RequestedThemeVariant == ThemeVariant.Dark &&
					  journal.Background is ISolidColorBrush background &&
					  background.Color != lightBackground,
				"agent journal brushes to follow the dark theme");
			AssertOpaqueJournalSurface(journal);
			var darkSurfaceColors = CaptureJournalSurfaceColors(journal);
			Assert.Equal(lightSurfaceColors.Length, darkSurfaceColors.Length);
			for (var index = 0; index < lightSurfaceColors.Length; index++)
				Assert.NotEqual(lightSurfaceColors[index], darkSurfaceColors[index]);
		}
		finally
		{
			application.RequestedThemeVariant = originalTheme;
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[Fact]
	public void JournalInitialSizeUsesSeventyPercentWithinTheOwnerAndMinimums()
	{
		Assert.Equal(new Size(1400, 700), AgentJournalWindow.ResolveInitialSize(new Size(2000, 1000)));
		Assert.Equal(new Size(900, 560), AgentJournalWindow.ResolveInitialSize(new Size(1200, 800)));
		Assert.Equal(new Size(900, 560), AgentJournalWindow.ResolveInitialSize(new Size(1063, 600)));
	}

	[AvaloniaFact]
	public async Task JournalUsesTheOwnersActualDarkThemeWhenTheApplicationThemeIsDefault()
	{
		var application = Assert.IsType<App>(global::Avalonia.Application.Current);
		var originalTheme = application.RequestedThemeVariant;
		application.RequestedThemeVariant = ThemeVariant.Default;
		var fixture = JournalFixture.Create(workspace.Project.RootPath);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			workspace.Project,
			configureServices: services => services with
			{
				AgentJournalReader = new RecordingJournalReader(fixture.Sessions, fixture.Calls)
			});

		try
		{
			window.RequestedThemeVariant = ThemeVariant.Dark;
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => window.ActualThemeVariant == ThemeVariant.Dark,
				"the owner to acquire its explicit dark theme");
			var background = Color.Parse("#15171B");
			var panel = Color.Parse("#1C1F24");
			var border = Color.Parse("#343942");
			var header = Color.Parse("#414854");
			window.Resources["AppBackgroundBrush"] = new SolidColorBrush(background);
			window.Resources["AppPanelBrush"] = new SolidColorBrush(panel);
			window.Resources["AppBorderBrush"] = new SolidColorBrush(border);
			window.Resources["MenuPressedBrush"] = new SolidColorBrush(header);

			var journalItem = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "McpJournalMenuItem");
			await UiTestDriver.RaiseMenuItemClickAsync(journalItem);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => window.OwnedWindows.OfType<AgentJournalWindow>().Count() == 1,
				"the dark owner journal window");
			var journal = Assert.Single(window.OwnedWindows.OfType<AgentJournalWindow>());
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);

			Assert.Equal(ThemeVariant.Default, application.RequestedThemeVariant);
			Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
			Assert.Equal(ThemeVariant.Dark, journal.RequestedThemeVariant);
			AssertJournalSurfaceColors(journal, background, panel, border, header);
		}
		finally
		{
			application.RequestedThemeVariant = originalTheme;
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public void McpConnectionDialogsUseTheSameOpaqueSurfaceContract()
	{
		var owner = new Window { Background = Brushes.White };
		var manual = McpManualConfigurationDialog.CreateDialogWindow(
			owner,
			new McpManualConfigurationDialogContent(
				"Configuration",
				"Reason",
				"JSON",
				"{}",
				"Paths",
				[],
				"Copy",
				"Close"));
		var completion = new TaskCompletionSource<McpConnectionPathAction>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var path = McpConnectionPathDialog.CreateDialogWindow(
			owner,
			new McpConnectionPathDialogContent(
				"PATH",
				"Body",
				string.Empty,
				"Copy",
				"Set up",
				"Close",
				McpConnectionPathAction.None),
			completion);

		try
		{
			AssertOpaqueWindow(manual);
			AssertOpaqueWindow(path);
		}
		finally
		{
			manual.Close();
			path.Close();
			owner.Close();
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
			Assert.False(empty.ViewModel.HasSessions);
			Assert.Contains("MCP menu", empty.ViewModel.EmptyText, StringComparison.Ordinal);
			Assert.Contains("not every action", empty.ViewModel.EmptyText, StringComparison.Ordinal);
			Assert.False(Assert.IsType<Border>(empty.FindControl<Border>("JournalSessionsSurface")).IsVisible);
			Assert.False(Assert.IsType<Border>(empty.FindControl<Border>("JournalCallsSurface")).IsVisible);
			Assert.False(Assert.IsType<Border>(empty.FindControl<Border>("JournalFooterSurface")).IsVisible);
			var emptySurface = Assert.IsType<Border>(empty.FindControl<Border>("JournalEmptySurface"));
			Assert.True(emptySurface.IsVisible);
			Assert.Equal(2, Grid.GetRowSpan(emptySurface));
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
	public async Task JournalRefreshDoesNotRestartTheSelectedSessionSubscription()
	{
		var fixture = JournalFixture.Create(workspace.Project.RootPath);
		var reader = new RecordingJournalReader(fixture.Sessions, fixture.Calls);
		var journal = new AgentJournalWindow(
			reader,
			new RecordingFormatter(),
			new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En),
			workspace.Project.RootPath);
		try
		{
			await journal.RefreshAsync();
			var reads = reader.ReadCallsCount;
			var watches = reader.WatchStarts;

			await journal.RefreshAsync();

			Assert.Equal(reads, reader.ReadCallsCount);
			Assert.Equal(watches, reader.WatchStarts);
		}
		finally
		{
			journal.Close();
		}
	}

	[AvaloniaFact]
	public async Task ActiveStandardSessionContinuesUpdatingItsCallRows()
	{
		var fixture = JournalFixture.Create(workspace.Project.RootPath);
		var standard = fixture.LiveSession with { Id = "active-standard", Mode = AgentJournalMode.Standard };
		var reader = new RecordingJournalReader(
			[standard],
			new Dictionary<string, IReadOnlyList<AgentJournalCall>>
			{
				[standard.Id] = [fixture.Calls[fixture.LiveSession.Id][0]]
			});
		var journal = new AgentJournalWindow(
			reader,
			new RecordingFormatter(),
			new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En),
			workspace.Project.RootPath);
		try
		{
			await journal.RefreshAsync();
			for (var attempt = 0; attempt < 20 && reader.WatchStarts == 0; attempt++)
				await UiTestDriver.WaitForSettledFramesAsync(2);
			Assert.Equal(1, reader.WatchStarts);

			for (var sequence = 2; sequence <= 26; sequence++)
				reader.AppendCall(standard.Id, fixture.SecondCall with { Sequence = sequence });

			for (var attempt = 0; attempt < 20 && journal.ViewModel.Calls.Count != 26; attempt++)
				await UiTestDriver.WaitForSettledFramesAsync(2);
			Assert.Equal(26, journal.ViewModel.Calls.Count);
			Assert.Equal("get_file", journal.ViewModel.Calls[^1].Tool);
			Assert.InRange(reader.ReadCallsCount, 2, 3);
		}
		finally
		{
			journal.Close();
		}
	}

	[AvaloniaFact]
	public async Task JournalClearKeepsAnActiveSessionVisible()
	{
		var fixture = JournalFixture.Create(workspace.Project.RootPath);
		var reader = new RecordingJournalReader(fixture.Sessions, fixture.Calls, preserveLiveOnClear: true);
		var journal = new AgentJournalWindow(
			reader,
			new RecordingFormatter(),
			new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En),
			workspace.Project.RootPath);
		try
		{
			await journal.RefreshAsync();

			var removed = await journal.ClearCurrentScopeAsync();

			Assert.Equal(1, removed);
			Assert.Equal(fixture.LiveSession.Id, Assert.Single(journal.ViewModel.Sessions).Session.Id);
		}
		finally
		{
			journal.Close();
		}
	}

	[AvaloniaFact]
	public async Task JournalNamesTheNumberOfEventsMissingFromIncompleteHistory()
	{
		var fixture = JournalFixture.Create(workspace.Project.RootPath);
		var marker = fixture.SecondCall with
		{
			Sequence = 3,
			Tool = "journal",
			Arguments = new Dictionary<string, string> { ["lost_events"] = "2" },
			DeliveredPaths = [],
			Notices = ["history-incomplete"]
		};
		var calls = fixture.Calls.ToDictionary(
			static pair => pair.Key,
			static pair => pair.Value,
			StringComparer.Ordinal);
		calls[fixture.LiveSession.Id] = [fixture.Calls[fixture.LiveSession.Id][0], marker];
		var reader = new RecordingJournalReader(fixture.Sessions, calls);
		var journal = new AgentJournalWindow(
			reader,
			new RecordingFormatter(),
			new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.Ru),
			workspace.Project.RootPath);
		try
		{
			await journal.RefreshAsync();

			Assert.Contains(
				journal.ViewModel.Calls,
				call => call.Notices.Contains("История неполна: 2 событий не записано.", StringComparison.Ordinal));
		}
		finally
		{
			journal.Close();
		}
	}

	[AvaloniaFact]
	public async Task JournalProjectContextCanFollowTheOwningWindow()
	{
		var secondProject = Path.Combine(workspace.Project.RootPath, "second-root");
		Directory.CreateDirectory(secondProject);
		var first = JournalFixture.Create(workspace.Project.RootPath).LiveSession;
		var second = first with
		{
			Id = "second-session",
			Roots = [new AgentJournalRoot(secondProject, "second")]
		};
		var reader = new RecordingJournalReader(
			[first, second],
			new Dictionary<string, IReadOnlyList<AgentJournalCall>>(StringComparer.Ordinal)
			{
				[first.Id] = [],
				[second.Id] = []
			});
		string? currentProject = workspace.Project.RootPath;
		var journal = new AgentJournalWindow(
			null,
			reader,
			new RecordingFormatter(),
			new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En),
			workspace.Project.RootPath,
			() => currentProject);
		UiTestDriver.TrackTopLevelWindow(journal);
		journal.Show();
		try
		{
			await journal.RefreshAsync();
			Assert.Equal(first.Id, Assert.Single(journal.ViewModel.Sessions).Session.Id);

			currentProject = secondProject;
			await journal.RefreshAsync();

			Assert.Equal(second.Id, Assert.Single(journal.ViewModel.Sessions).Session.Id);
		}
		finally
		{
			await UiTestDriver.CloseTopLevelWindowAsync(journal);
		}
	}

	[AvaloniaFact]
	public async Task ContextRulesAreExplainedBesideTheMcpMenuTreeAndSecretSetting()
	{
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);
		try
		{
			var menuNames = new[]
		{
				"McpConnectClaudeCodeMenuItem",
				"McpConnectCodexMenuItem",
				"McpConnectCursorMenuItem",
				"McpConnectVsCodeMenuItem",
				"McpOtherClientsMenuItem",
				"McpStandardConnectClaudeCodeMenuItem",
				"McpStandardConnectCodexMenuItem",
				"McpStandardConnectCursorMenuItem",
				"McpStandardConnectVsCodeMenuItem",
				"McpStandardOtherClientsMenuItem",
				"McpJournalMenuItem"
			};
			foreach (var name in menuNames)
			{
				var item = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, name);
				var tip = Assert.IsType<string>(ToolTip.GetTip(item));
				Assert.False(string.IsNullOrWhiteSpace(tip));
				Assert.Equal(tip, AutomationProperties.GetHelpText(item));
			}

			var projectTree = Assert.IsAssignableFrom<TreeView>(window.FindControl<Control>("ProjectTree"));
			var rootCheckBox = Assert.Single(
				projectTree.GetVisualDescendants().OfType<CheckBox>(),
				static checkBox => checkBox.DataContext is TreeNodeViewModel { Parent: null });
			var rootTip = Assert.IsType<string>(ToolTip.GetTip(rootCheckBox));
			Assert.Contains("focus", rootTip, StringComparison.OrdinalIgnoreCase);
			Assert.Equal(rootTip, AutomationProperties.GetHelpText(rootCheckBox));

			var secretCheckBox = UiTestDriver.GetRequiredIgnoreOptionCheckBox(
				window,
				IgnoreOptionId.HideSecrets);
			var secretTip = Assert.IsType<string>(ToolTip.GetTip(secretCheckBox));
			Assert.Contains("MCP", secretTip, StringComparison.Ordinal);
			Assert.Equal(secretTip, AutomationProperties.GetHelpText(secretCheckBox));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task JournalMinimumSizeKeepsLocalizedColumnsReadableAndWrapsNotices()
	{
		var outputRoot = Path.Combine(Path.GetTempPath(), "devprojex-tails", "screenshots");
		var capture = string.Equals(
			Environment.GetEnvironmentVariable("DEVPROJEX_CAPTURE_JOURNAL_TAILS"),
			"1",
			StringComparison.Ordinal);
		if (capture)
			Directory.CreateDirectory(outputRoot);
		foreach (var (language, code) in new[]
		{
			(AppLanguage.Ru, "ru"),
			(AppLanguage.De, "de")
		})
		{
			var fixture = JournalFixture.Create(workspace.Project.RootPath);
			LocalizationService? localization = null;
			var owner = await UiTestDriver.CreateLoadedMainWindowAsync(
				workspace.Project,
				configureServices: services =>
				{
					localization = services.Localization;
					return services with
					{
						AgentJournalReader = new RecordingJournalReader(fixture.Sessions, fixture.Calls)
					};
				});
			localization!.SetLanguage(language);
			var journal = new AgentJournalWindow(
				owner,
				new RecordingJournalReader(fixture.Sessions, fixture.Calls),
				new RecordingFormatter(),
				localization,
				workspace.Project.RootPath,
				() => workspace.Project.RootPath)
			{
				Width = AgentJournalWindow.MinimumWindowWidth,
				Height = AgentJournalWindow.MinimumWindowHeight
			};
			UiTestDriver.TrackTopLevelWindow(journal);
			journal.Show(owner);
			try
			{
				await journal.RefreshAsync();
				await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
				var sessionsHeader = Assert.IsType<Grid>(
					journal.FindControl<Grid>("JournalSessionsColumnsHeader"));
				var callsHeader = Assert.IsType<Grid>(
					journal.FindControl<Grid>("JournalCallsColumnsHeader"));
				Assert.True(sessionsHeader.ColumnDefinitions[2].ActualWidth >= 72);
				Assert.True(sessionsHeader.ColumnDefinitions[8].ActualWidth >= 104);
				Assert.True(callsHeader.ColumnDefinitions[2].ActualWidth >= 96);
				Assert.True(callsHeader.ColumnDefinitions[9].ActualWidth >= 104);
				Assert.True(callsHeader.ColumnDefinitions[10].ActualWidth >= 120);
				var notice = Assert.Single(
					journal.GetVisualDescendants().OfType<TextBlock>(),
					text => string.Equals(
						text.Text,
						journal.ViewModel.Calls[0].Notices,
						StringComparison.Ordinal));
				Assert.Equal(TextWrapping.Wrap, notice.TextWrapping);
				Assert.Equal(journal.ViewModel.Calls[0].Notices, ToolTip.GetTip(notice));
				if (capture)
				{
					var snapshotPath = Path.Combine(outputRoot, $"journal-minimum-{code}.png");
					await SaveSnapshotAsync(journal, snapshotPath);
					Assert.True(
						new FileInfo(snapshotPath).Length > 1_000,
						$"{snapshotPath}; bounds={journal.Bounds.Size}; client={journal.ClientSize}");
				}
			}
			finally
			{
				await UiTestDriver.CloseTopLevelWindowAsync(journal);
				await UiTestDriver.CloseWindowAsync(owner);
			}
		}
	}

	[AvaloniaFact]
	public async Task JournalExportFailureDoesNotEscapeTheWindow()
	{
		var fixture = JournalFixture.Create(workspace.Project.RootPath);
		var errors = new List<string>();
		var journal = new AgentJournalWindow(
			new FailingJournalReader(fixture.Sessions, fixture.Calls, failExport: true),
			new RecordingFormatter(),
			new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En),
			workspace.Project.RootPath)
		{
			OperationErrorPresenter = message =>
			{
				errors.Add(message);
				return Task.CompletedTask;
			}
		};
		try
		{
			await journal.RefreshAsync();

			var exception = await Record.ExceptionAsync(() => journal.ExportSelectedToPathAsync(
				Path.Combine(workspace.Project.RootPath, "journal.md"),
				json: false));

			Assert.Null(exception);
			Assert.Equal("Could not export the journal: read failed", Assert.Single(errors));
		}
		finally
		{
			journal.Close();
		}
	}

	[AvaloniaFact]
	public async Task JournalClearFailureDoesNotEscapeTheWindow()
	{
		var fixture = JournalFixture.Create(workspace.Project.RootPath);
		var errors = new List<string>();
		var journal = new AgentJournalWindow(
			new FailingJournalReader(fixture.Sessions, fixture.Calls, failClear: true),
			new RecordingFormatter(),
			new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En),
			workspace.Project.RootPath)
		{
			OperationErrorPresenter = message =>
			{
				errors.Add(message);
				return Task.CompletedTask;
			}
		};
		try
		{
			await journal.RefreshAsync();

			var exception = await Record.ExceptionAsync(() => journal.ClearCurrentScopeAsync());

			Assert.Null(exception);
			Assert.Equal("Could not clear the journal: clear failed", Assert.Single(errors));
		}
		finally
		{
			journal.Close();
		}
	}

	[AvaloniaFact]
	public async Task JournalRefreshKeepsTheNewestProjectFilterResult()
	{
		var fixture = JournalFixture.Create(workspace.Project.RootPath);
		var outside = fixture.LiveSession with
		{
			Id = "outside-session",
			Roots = [new AgentJournalRoot(Path.Combine(workspace.Project.RootPath, "..", "outside"), "Outside")]
		};
		var reader = new OverlappingRefreshJournalReader(fixture.LiveSession, outside);
		var journal = new AgentJournalWindow(
			reader,
			new RecordingFormatter(),
			new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En),
			workspace.Project.RootPath);
		UiTestDriver.TrackTopLevelWindow(journal);

		try
		{
			var filteredRefresh = journal.RefreshAsync();
			await reader.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
			journal.ViewModel.CurrentProjectOnly = false;
			var allProjectsRefresh = journal.RefreshAsync();
			reader.ReleaseFirstRequest.TrySetResult();
			await Task.WhenAll(filteredRefresh, allProjectsRefresh);

			Assert.Equal(
				["live-session", "outside-session"],
				journal.ViewModel.Sessions.Select(static row => row.Session.Id).Order().ToArray());
		}
		finally
		{
			reader.ReleaseFirstRequest.TrySetResult();
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
					  "Codex · search_project ·",
					  StringComparison.Ordinal),
				"live agent activity status");
			Assert.Equal(0, reader.ReadReceiptCount);

			var deliveredPath = Path.GetFullPath(Path.Combine(
				workspace.Project.RootPath,
				"src",
				"AppHost",
				"Program.cs"));
			var deliveredNode = Assert.Single(
				viewModel.TreeNodes.SelectMany(static root => root.Flatten()),
				node => PathComparer.Default.Equals(node.FullPath, deliveredPath));
			Assert.Equal(0, deliveredNode.AgentDeliveryCount);
			var checkedBefore = deliveredNode.IsChecked;
			reader.AppendCall(fixture.LiveSession.Id, fixture.SecondCall);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => deliveredNode.AgentDeliveryCount == 1,
				"a call after project opening to add the delivery trace");
			Assert.Contains("1", deliveredNode.AgentDeliveryToolTip, StringComparison.Ordinal);

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
			"Menu.Mcp.OpenTerminal.Live.Help",
			"Menu.Mcp.OpenProject.Live.Help",
			"Menu.Mcp.OpenTerminal.Standard.Help",
			"Menu.Mcp.OpenProject.Standard.Help",
			"Menu.Mcp.OtherClients.Help",
			"Menu.Mcp.Journal.Help",
			"Tree.Selection.Focus.Help",
			"Settings.HideSecrets.Help",
			"Menu.View.AgentActivity",
			"AgentJournal.Title",
			"AgentJournal.Empty",
			"AgentJournal.ExportFailed",
			"AgentJournal.ClearFailed",
			"AgentJournal.Empty.Tui",
			"AgentJournal.Footer",
			"AgentJournal.Masked.Short",
			"AgentJournal.Notice.OutsideSelection",
			"AgentJournal.Notice.Unavailable",
			"AgentJournal.Notice.HistoryRecovered",
			"AgentJournal.Notice.HistoryIncomplete",
			"AgentActivity.Status.Files",
			"AgentActivity.Status.Tokens",
			"AgentActivity.Status.Calls",
			"AgentActivity.Status.Files.One",
			"AgentActivity.Status.Files.Few",
			"AgentActivity.Status.Files.Many",
			"AgentActivity.Status.Files.Other",
			"AgentActivity.Status.Tokens.One",
			"AgentActivity.Status.Tokens.Few",
			"AgentActivity.Status.Tokens.Many",
			"AgentActivity.Status.Tokens.Other",
			"AgentActivity.Status.Calls.One",
			"AgentActivity.Status.Calls.Few",
			"AgentActivity.Status.Calls.Many",
			"AgentActivity.Status.Calls.Other",
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

	[Fact]
	public void RussianAgentActivityUsesLocalizedPluralFormsAndCompactNumbers()
	{
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.Ru);

		Assert.Equal("2 файла", AgentActivityPresentation.FormatCount(
			localization,
			"AgentActivity.Status.Files",
			2));
		Assert.Equal("5 файлов", AgentActivityPresentation.FormatCount(
			localization,
			"AgentActivity.Status.Files",
			5));
		Assert.Equal("21 файл", AgentActivityPresentation.FormatCount(
			localization,
			"AgentActivity.Status.Files",
			21));
		Assert.Equal("1,2K токенов", AgentActivityPresentation.FormatCount(
			localization,
			"AgentActivity.Status.Tokens",
			1_200));
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
			reader.AppendCall(fixture.LiveSession.Id, fixture.SecondCall);
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

			reader.AppendCall(fixture.LiveSession.Id, fixture.SecondCall with { Sequence = 3 });
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

	[AvaloniaFact]
	public async Task JournalIntegritySnapshotsShowPreservedAndIncompleteSessions()
	{
		if (!string.Equals(
				Environment.GetEnvironmentVariable("DEVPROJEX_CAPTURE_JOURNAL_INTEGRITY"),
				"1",
				StringComparison.Ordinal))
		{
			return;
		}

		var outputRoot = Path.Combine(Path.GetTempPath(), "devprojex-night-c", "screenshots");
		Directory.CreateDirectory(outputRoot);
		var fixture = JournalFixture.Create(workspace.Project.RootPath);
		var activeReader = new RecordingJournalReader(
			fixture.Sessions,
			fixture.Calls,
			preserveLiveOnClear: true);
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.Ru);
		var owner = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);
		var activeJournal = new AgentJournalWindow(
			owner,
			activeReader,
			new RecordingFormatter(),
			localization,
			workspace.Project.RootPath,
			() => workspace.Project.RootPath);
		UiTestDriver.TrackTopLevelWindow(activeJournal);
		activeJournal.Show(owner);
		try
		{
			await activeJournal.RefreshAsync();
			await activeJournal.ClearCurrentScopeAsync();
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
			Assert.Equal(fixture.LiveSession.Id, Assert.Single(activeJournal.ViewModel.Sessions).Session.Id);
			await SaveSnapshotAsync(
				activeJournal,
				Path.Combine(outputRoot, "journal-active-after-clear.png"));
		}
		finally
		{
			await UiTestDriver.CloseTopLevelWindowAsync(activeJournal);
		}

		var marker = fixture.SecondCall with
		{
			Sequence = 3,
			Tool = "journal",
			Arguments = new Dictionary<string, string> { ["lost_events"] = "2" },
			DeliveredPaths = [],
			Notices = ["history-incomplete"]
		};
		var incompleteCalls = fixture.Calls.ToDictionary(
			static pair => pair.Key,
			static pair => pair.Value,
			StringComparer.Ordinal);
		incompleteCalls[fixture.LiveSession.Id] = [fixture.Calls[fixture.LiveSession.Id][0], marker];
		var incompleteJournal = new AgentJournalWindow(
			owner,
			new RecordingJournalReader(fixture.Sessions, incompleteCalls),
			new RecordingFormatter(),
			localization,
			workspace.Project.RootPath,
			() => workspace.Project.RootPath);
		UiTestDriver.TrackTopLevelWindow(incompleteJournal);
		incompleteJournal.Show(owner);
		try
		{
			await incompleteJournal.RefreshAsync();
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
			Assert.Contains(
				incompleteJournal.ViewModel.Calls,
				call => call.Notices.Contains("История неполна: 2 событий не записано.", StringComparison.Ordinal));
			await SaveSnapshotAsync(
				incompleteJournal,
				Path.Combine(outputRoot, "journal-incomplete-history.png"));
		}
		finally
		{
			await UiTestDriver.CloseTopLevelWindowAsync(incompleteJournal);
		}

		var standard = fixture.LiveSession with { Id = "active-standard", Mode = AgentJournalMode.Standard };
		var standardReader = new RecordingJournalReader(
			[standard],
			new Dictionary<string, IReadOnlyList<AgentJournalCall>>
			{
				[standard.Id] = [fixture.Calls[fixture.LiveSession.Id][0]]
			});
		var standardJournal = new AgentJournalWindow(
			owner,
			standardReader,
			new RecordingFormatter(),
			localization,
			workspace.Project.RootPath,
			() => workspace.Project.RootPath);
		UiTestDriver.TrackTopLevelWindow(standardJournal);
		standardJournal.Show(owner);
		try
		{
			await standardJournal.RefreshAsync();
			standardReader.AppendCall(standard.Id, fixture.SecondCall);
			for (var attempt = 0; attempt < 20 && standardJournal.ViewModel.Calls.Count != 2; attempt++)
				await UiTestDriver.WaitForSettledFramesAsync(2);
			Assert.Equal(2, standardJournal.ViewModel.Calls.Count);
			await SaveSnapshotAsync(standardJournal, Path.Combine(outputRoot, "journal-standard-updating.png"));
		}
		finally
		{
			await UiTestDriver.CloseTopLevelWindowAsync(standardJournal);
		}

		var recoveredMarker = marker with
		{
			Arguments = new Dictionary<string, string>
			{
				["lost_events"] = "1",
				["lost_events_unknown"] = "true"
			},
			Notices = ["history-recovered", "history-incomplete"]
		};
		var recoveredCalls = fixture.Calls.ToDictionary(
			static pair => pair.Key,
			static pair => pair.Value,
			StringComparer.Ordinal);
		recoveredCalls[fixture.LiveSession.Id] = [fixture.Calls[fixture.LiveSession.Id][0], recoveredMarker];
		var recoveredJournal = new AgentJournalWindow(
			owner,
			new RecordingJournalReader(fixture.Sessions, recoveredCalls),
			new RecordingFormatter(),
			localization,
			workspace.Project.RootPath,
			() => workspace.Project.RootPath);
		UiTestDriver.TrackTopLevelWindow(recoveredJournal);
		recoveredJournal.Show(owner);
		try
		{
			await recoveredJournal.RefreshAsync();
			await UiTestDriver.WaitForSettledFramesAsync(8);
			await SaveSnapshotAsync(recoveredJournal, Path.Combine(outputRoot, "journal-recovered-history.png"));
		}
		finally
		{
			await UiTestDriver.CloseTopLevelWindowAsync(recoveredJournal);
		}

		var dead = fixture.LiveSession with { Id = "inactive-unknown-end", IsLive = false, EndedUtc = null };
		var deadCalls = new[] { fixture.Calls[fixture.LiveSession.Id][0] };
		AgentJournalSessionHistory.Attach(dead, AgentJournalSessionHistory.FromCalls(deadCalls));
		var deadJournal = new AgentJournalWindow(
			owner,
			new RecordingJournalReader(
				[dead],
				new Dictionary<string, IReadOnlyList<AgentJournalCall>> { [dead.Id] = deadCalls }),
			new RecordingFormatter(),
			localization,
			workspace.Project.RootPath,
			() => workspace.Project.RootPath);
		UiTestDriver.TrackTopLevelWindow(deadJournal);
		deadJournal.Show(owner);
		try
		{
			await deadJournal.RefreshAsync();
			await UiTestDriver.WaitForSettledFramesAsync(8);
			Assert.DoesNotContain("running", deadJournal.ViewModel.Sessions[0].Duration, StringComparison.OrdinalIgnoreCase);
			await SaveSnapshotAsync(deadJournal, Path.Combine(outputRoot, "journal-inactive-unknown-end.png"));
		}
		finally
		{
			await UiTestDriver.CloseTopLevelWindowAsync(deadJournal);
			await UiTestDriver.CloseWindowAsync(owner);
		}

		Assert.True(new FileInfo(Path.Combine(outputRoot, "journal-active-after-clear.png")).Length > 1_000);
		Assert.True(new FileInfo(Path.Combine(outputRoot, "journal-incomplete-history.png")).Length > 1_000);
		Assert.True(new FileInfo(Path.Combine(outputRoot, "journal-standard-updating.png")).Length > 1_000);
		Assert.True(new FileInfo(Path.Combine(outputRoot, "journal-recovered-history.png")).Length > 1_000);
		Assert.True(new FileInfo(Path.Combine(outputRoot, "journal-inactive-unknown-end.png")).Length > 1_000);
	}

	private static async Task SaveSnapshotAsync(TopLevel topLevel, string path)
	{
		if (File.Exists(path))
			File.Delete(path);
		await topLevel.Dispatcher.InvokeAsync(() =>
		{
			using var captured = topLevel.CaptureRenderedFrame();
			if (captured is not null)
			{
				using var output = new MemoryStream();
				captured.Save(output, PngBitmapEncoderOptions.Default);
				File.WriteAllBytes(path, output.ToArray());
				return;
			}
			var width = Math.Max(1, (int)Math.Ceiling(topLevel.Bounds.Width));
			var height = Math.Max(1, (int)Math.Ceiling(topLevel.Bounds.Height));
			using var rendered = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
			rendered.Render(topLevel);
			using var fallbackOutput = new MemoryStream();
			rendered.Save(fallbackOutput, PngBitmapEncoderOptions.Default);
			File.WriteAllBytes(path, fallbackOutput.ToArray());
		}, DispatcherPriority.Render);
	}

	private static void AssertOpaqueJournalSurface(AgentJournalWindow journal)
	{
		AssertOpaqueWindow(journal);
		AssertOpaqueBrush(journal.FindControl<Grid>("JournalSurface")?.Background);
		AssertOpaqueBrush(journal.FindControl<Border>("JournalSessionsSurface")?.Background);
		AssertOpaqueBrush(journal.FindControl<Border>("JournalSessionsHeader")?.Background);
		AssertOpaqueBrush(journal.FindControl<ListBox>("JournalSessionsList")?.Background);
		AssertOpaqueBrush(journal.FindControl<Border>("JournalCallsSurface")?.Background);
		AssertOpaqueBrush(journal.FindControl<Border>("JournalCallsHeader")?.Background);
		AssertOpaqueBrush(journal.FindControl<ListBox>("JournalCallsList")?.Background);
		AssertOpaqueBrush(journal.FindControl<Border>("JournalFooterSurface")?.Background);
		AssertOpaqueBrush(journal.FindControl<Border>("JournalEmptySurface")?.Background);
		Assert.Equal(2, Grid.GetRowSpan(Assert.IsType<Border>(
			journal.FindControl<Border>("JournalEmptySurface"))));
	}

	private static void AssertHeaderColumnsFit(AgentJournalWindow journal, string name)
	{
		var header = Assert.IsType<Grid>(journal.FindControl<Grid>(name));
		var rightmostEdge = header.Children
			.OfType<Control>()
			.Max(static control => control.Bounds.Right);

		Assert.True(
			rightmostEdge <= header.Bounds.Width + 0.5,
			$"{name} overflowed its visible width: content={rightmostEdge:F2}, width={header.Bounds.Width:F2}.");
	}

	private static void AssertJournalSurfaceColors(
		AgentJournalWindow journal,
		Color background,
		Color panel,
		Color border,
		Color header)
	{
		AssertBrushColor(journal.Background, background);
		AssertBrushColor(journal.FindControl<Grid>("JournalSurface")?.Background, background);
		AssertBrushColor(journal.FindControl<Border>("JournalSessionsSurface")?.Background, panel);
		AssertBrushColor(journal.FindControl<Border>("JournalSessionsSurface")?.BorderBrush, border);
		AssertBrushColor(journal.FindControl<Border>("JournalSessionsHeader")?.Background, header);
		AssertBrushColor(journal.FindControl<ListBox>("JournalSessionsList")?.Background, panel);
		AssertBrushColor(journal.FindControl<Border>("JournalCallsSurface")?.Background, panel);
		AssertBrushColor(journal.FindControl<Border>("JournalCallsSurface")?.BorderBrush, border);
		AssertBrushColor(journal.FindControl<Border>("JournalCallsHeader")?.Background, header);
		AssertBrushColor(journal.FindControl<ListBox>("JournalCallsList")?.Background, panel);
		AssertBrushColor(journal.FindControl<Border>("JournalFooterSurface")?.Background, panel);
		AssertBrushColor(journal.FindControl<Border>("JournalFooterSurface")?.BorderBrush, border);
		AssertBrushColor(journal.FindControl<Border>("JournalEmptySurface")?.Background, panel);
		AssertBrushColor(journal.FindControl<Border>("JournalEmptySurface")?.BorderBrush, border);
	}

	private static Color[] CaptureJournalSurfaceColors(AgentJournalWindow journal) =>
	[
		GetBrushColor(journal.Background),
		GetBrushColor(journal.FindControl<Grid>("JournalSurface")?.Background),
		GetBrushColor(journal.FindControl<Border>("JournalSessionsSurface")?.Background),
		GetBrushColor(journal.FindControl<Border>("JournalSessionsSurface")?.BorderBrush),
		GetBrushColor(journal.FindControl<Border>("JournalSessionsHeader")?.Background),
		GetBrushColor(journal.FindControl<ListBox>("JournalSessionsList")?.Background),
		GetBrushColor(journal.FindControl<Border>("JournalCallsSurface")?.Background),
		GetBrushColor(journal.FindControl<Border>("JournalCallsSurface")?.BorderBrush),
		GetBrushColor(journal.FindControl<Border>("JournalCallsHeader")?.Background),
		GetBrushColor(journal.FindControl<ListBox>("JournalCallsList")?.Background),
		GetBrushColor(journal.FindControl<Border>("JournalFooterSurface")?.Background),
		GetBrushColor(journal.FindControl<Border>("JournalFooterSurface")?.BorderBrush),
		GetBrushColor(journal.FindControl<Border>("JournalEmptySurface")?.Background),
		GetBrushColor(journal.FindControl<Border>("JournalEmptySurface")?.BorderBrush)
	];

	private static void AssertOpaqueWindow(Window window)
	{
		Assert.Equal([WindowTransparencyLevel.None], window.TransparencyLevelHint);
		AssertOpaqueBrush(window.Background);
	}

	private static void AssertOpaqueBrush(IBrush? brush)
	{
		var solid = Assert.IsAssignableFrom<ISolidColorBrush>(brush);
		Assert.Equal(byte.MaxValue, solid.Color.A);
	}

	private static void AssertBrushColor(IBrush? brush, Color expected)
	{
		Assert.Equal(expected, GetBrushColor(brush));
	}

	private static Color GetBrushColor(IBrush? brush) =>
		Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

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

	private sealed class OverlappingRefreshJournalReader(
		AgentJournalSession currentProjectSession,
		AgentJournalSession outsideSession) : IAgentJournalReader
	{
		private int _requestCount;

		public AgentJournalRetentionPolicy Retention => AgentJournalRetentionPolicy.Default;
		public TaskCompletionSource FirstRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseFirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async ValueTask<IReadOnlyList<AgentJournalSession>> ListSessionsAsync(
			string? projectRoot = null,
			int limit = 200,
			CancellationToken cancellationToken = default)
		{
			var request = Interlocked.Increment(ref _requestCount);
			if (request == 1)
			{
				FirstRequestStarted.TrySetResult();
				await ReleaseFirstRequest.Task.WaitAsync(cancellationToken);
				return [currentProjectSession];
			}

			return [currentProjectSession, outsideSession];
		}

		public ValueTask<IReadOnlyList<AgentJournalCall>> ReadCallsAsync(
			string sessionId,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<IReadOnlyList<AgentJournalCall>>([]);

		public ValueTask<AgentJournalReceipt?> ReadReceiptAsync(
			string sessionId,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<AgentJournalReceipt?>(null);

		public async IAsyncEnumerable<AgentJournalChange> WatchChangesAsync(
			string sessionId,
			[EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			await Task.CompletedTask;
			yield break;
		}

		public ValueTask<int> ClearAsync(
			string? projectRoot = null,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(0);
	}

	private sealed class RecordingJournalReader(
		IEnumerable<AgentJournalSession> sessions,
		IReadOnlyDictionary<string, IReadOnlyList<AgentJournalCall>> calls,
		bool preserveLiveOnClear = false) : IAgentJournalReader
	{
		private readonly List<AgentJournalSession> _sessions = [.. sessions];
		private readonly Dictionary<string, List<AgentJournalCall>> _calls = calls.ToDictionary(
			static pair => pair.Key,
			static pair => pair.Value.ToList(),
			StringComparer.Ordinal);
		private readonly Dictionary<string, Channel<AgentJournalChange>> _changes = new(StringComparer.Ordinal);

		public AgentJournalRetentionPolicy Retention => AgentJournalRetentionPolicy.Default;
		public List<string?> ClearRoots { get; } = [];
		public int ReadCallsCount { get; private set; }
		public int ReadReceiptCount { get; private set; }
		public int WatchStarts { get; private set; }

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
			ReadCallsCount++;
			return ValueTask.FromResult<IReadOnlyList<AgentJournalCall>>(
				_calls.TryGetValue(sessionId, out var sessionCalls) ? sessionCalls.ToArray() : []);
		}

		public ValueTask<AgentJournalReceipt?> ReadReceiptAsync(
			string sessionId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ReadReceiptCount++;
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
			WatchStarts++;
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
			var removed = _sessions.RemoveAll(session =>
				(!preserveLiveOnClear || !session.IsLive) &&
				(projectRoot is null || session.Roots.Any(root =>
					PathComparer.Default.Equals(Path.GetFullPath(root.ConfiguredPath), Path.GetFullPath(projectRoot)))));
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

	private sealed class FailingJournalReader(
		IEnumerable<AgentJournalSession> sessions,
		IReadOnlyDictionary<string, IReadOnlyList<AgentJournalCall>> calls,
		bool failExport = false,
		bool failClear = false) : IAgentJournalReader
	{
		private readonly RecordingJournalReader _inner = new(sessions, calls);

		public AgentJournalRetentionPolicy Retention => _inner.Retention;

		public ValueTask<IReadOnlyList<AgentJournalSession>> ListSessionsAsync(
			string? projectRoot = null,
			int limit = 200,
			CancellationToken cancellationToken = default) =>
			_inner.ListSessionsAsync(projectRoot, limit, cancellationToken);

		public ValueTask<IReadOnlyList<AgentJournalCall>> ReadCallsAsync(
			string sessionId,
			CancellationToken cancellationToken = default) =>
			_inner.ReadCallsAsync(sessionId, cancellationToken);

		public ValueTask<AgentJournalReceipt?> ReadReceiptAsync(
			string sessionId,
			CancellationToken cancellationToken = default) => failExport
			? ValueTask.FromException<AgentJournalReceipt?>(new IOException("read failed"))
			: _inner.ReadReceiptAsync(sessionId, cancellationToken);

		public IAsyncEnumerable<AgentJournalChange> WatchChangesAsync(
			string sessionId,
			CancellationToken cancellationToken = default) =>
			_inner.WatchChangesAsync(sessionId, cancellationToken);

		public ValueTask<int> ClearAsync(
			string? projectRoot = null,
			CancellationToken cancellationToken = default) => failClear
			? ValueTask.FromException<int>(new IOException("clear failed"))
			: _inner.ClearAsync(projectRoot, cancellationToken);
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
				new Dictionary<string, string>
				{
					["query_present"] = bool.TrueString.ToLowerInvariant(),
					["query_length"] = "9"
				},
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
