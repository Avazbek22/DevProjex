using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class ProjectProfilePersistenceCoordinatorTests
{
	[Fact]
	public async Task SelectionPersist_WritesTheFrontierWithoutPublishingDraftFilters()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			viewModel.Extensions.Add(new SelectionOptionViewModel(".md", false));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);
			viewModel.Extensions[0].IsChecked = false;
			viewModel.Extensions[1].IsChecked = true;
			var store = new RetryProfileStore(projectPath, failures: 0);
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);

			await persistence.PersistSelectedPathsAsync(
				projectPath,
				["src", "docs/api"],
				TestContext.Current.CancellationToken);

			var saved = store.SavedProfiles[Path.GetFullPath(projectPath)];
			Assert.Equal([".cs"], saved.SelectedExtensions);
			Assert.Equal(["src", "docs/api"], saved.SelectedPaths);
		}
	}

	[Fact]
	public async Task SelectionPersist_ExplicitFullTreeDoesNotRecaptureANewerProviderValue()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);
			var store = new RetryProfileStore(projectPath, failures: 0);
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession,
				selectedPathsProvider: static () => ["newer-selection"]);

			await persistence.PersistSelectedPathsAsync(
				projectPath,
				selectedPaths: null,
				TestContext.Current.CancellationToken);

			Assert.Null(store.SavedProfiles[Path.GetFullPath(projectPath)].SelectedPaths);
		}
	}

	[Theory]
	[InlineData(ProjectProfileLookupStatus.InvalidStorage)]
	[InlineData(ProjectProfileLookupStatus.UnsupportedFutureSchema)]
	public async Task SelectionPersistReportsDeferredWhenLoadedStorageCannotBeWritten(
		ProjectProfileLookupStatus status)
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		{
			var store = new StatusProfileStore(new ProjectProfileLookupResult(status, null));
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);

			_ = await persistence.LoadSnapshotAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			var result = await persistence.PersistSelectedPathsAsync(
				projectPath,
				["src"],
				TestContext.Current.CancellationToken);

			Assert.Equal(ProjectProfilePersistenceDisposition.Deferred, result.Disposition);
			Assert.Contains(status.ToString(), result.Reason, StringComparison.Ordinal);
			Assert.Equal(0, store.SaveCount);
		}
	}

	[Fact]
	public async Task SelectionPersistReportsDeferredWhileStorageIsTemporarilyUnavailable()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		{
			var unavailable = new ProjectProfileLookupResult(
				ProjectProfileLookupStatus.TemporarilyUnavailable,
				null);
			var store = new StatusProfileStore(unavailable, unavailable);
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession,
				profileLoadRetryDelays: []);

			_ = await persistence.LoadSnapshotAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			var result = await persistence.PersistSelectedPathsAsync(
				projectPath,
				["src"],
				TestContext.Current.CancellationToken);

			Assert.Equal(ProjectProfilePersistenceDisposition.Deferred, result.Disposition);
			Assert.Equal(0, store.SaveCount);
		}
	}

	[Fact]
	public async Task SelectionPersistReportsDeferredWhenSelectionStateCannotBeCaptured()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		{
			typeof(SelectionSyncCoordinator)
				.GetField(
					"_selectionPersistenceBlockedByIncompleteScan",
					BindingFlags.Instance | BindingFlags.NonPublic)!
				.SetValue(selectionCoordinator, true);
			var store = new StatusProfileStore(
				new ProjectProfileLookupResult(ProjectProfileLookupStatus.Missing, null));
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);

			var result = await persistence.PersistSelectedPathsAsync(
				projectPath,
				["src"],
				TestContext.Current.CancellationToken);

			Assert.Equal(ProjectProfilePersistenceDisposition.Deferred, result.Disposition);
			Assert.Contains("incomplete", result.Reason, StringComparison.OrdinalIgnoreCase);
			Assert.Equal(0, store.SaveCount);
		}
	}

	[Fact]
	public async Task Persist_UsesAppliedSelectionsWithoutWritingCurrentMarkedSecrets()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			viewModel.Extensions.Add(new SelectionOptionViewModel(".md", false));
			viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(
				IgnoreOptionId.HiddenFiles,
				"hidden files",
				true));
			viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(
				IgnoreOptionId.HideSecrets,
				"hide secrets",
				false));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);

			viewModel.Extensions[0].IsChecked = false;
			viewModel.Extensions[1].IsChecked = true;
			viewModel.IgnoreOptions[0].IsChecked = false;
			using var secretSession = new SecretRedactionSession(new EmptySecretDetector());
			Assert.True(secretSession.AddMarkedSecret(new MarkedSecretProfileEntry(
				"001122334455",
				"TOKEN",
				16)));
			var store = new RetryProfileStore(projectPath, failures: 0);
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);

			await persistence.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);

			var saved = store.SavedProfiles[Path.GetFullPath(projectPath)];
			Assert.Equal([".cs"], saved.SelectedExtensions.ToArray());
			Assert.Contains(IgnoreOptionId.HiddenFiles, saved.SelectedIgnoreOptions);
			Assert.Empty(saved.MarkedSecrets!);
		}
	}

	[Fact]
	public async Task Persist_DoesNotUseAppliedSnapshotFromAnotherProject()
	{
		const string projectA = @"C:\ProjectA";
		const string projectB = @"C:\ProjectB";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectA);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			viewModel.Extensions.Add(new SelectionOptionViewModel(".md", false));
			viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(
				IgnoreOptionId.HiddenFiles,
				"hidden files",
				true));
			viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(
				IgnoreOptionId.HideSecrets,
				"hide secrets",
				false));
			selectionCoordinator.UpdateExtensionsSelectionCache();
			selectionCoordinator.UpdateIgnoreSelectionCache();
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectA);

			viewModel.Extensions[0].IsChecked = false;
			viewModel.Extensions[1].IsChecked = true;
			viewModel.IgnoreOptions[0].IsChecked = false;
			viewModel.IgnoreOptions[1].IsChecked = true;
			selectionCoordinator.UpdateExtensionsSelectionCache();
			selectionCoordinator.UpdateIgnoreSelectionCache();
			var store = new RetryProfileStore(projectB, failures: 0);
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);

			await persistence.PersistIfNeededAsync(projectB, TestContext.Current.CancellationToken);

			var saved = store.SavedProfiles[Path.GetFullPath(projectB)];
			Assert.Equal([".md"], saved.SelectedExtensions);
			Assert.DoesNotContain(IgnoreOptionId.HiddenFiles, saved.SelectedIgnoreOptions);
		}
	}

	[Fact]
	public async Task Persist_AfterEnsuringHideSecrets_MergesImmediateAppliedOptionWithoutDrafts()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			viewModel.Extensions.Add(new SelectionOptionViewModel(".md", false));
			viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(
				IgnoreOptionId.HideSecrets,
				"hide secrets",
				false));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);

			viewModel.Extensions[0].IsChecked = false;
			viewModel.Extensions[1].IsChecked = true;
			Assert.True(selectionCoordinator.ApplyHideSecretsOverride(true));
			Assert.False(selectionCoordinator.ApplyHideSecretsOverride(true));
			selectionCoordinator.AcceptHideSecretsOverrideAsApplied(projectPath);
			var store = new RetryProfileStore(projectPath, failures: 0);
			using var secretSession = new SecretRedactionSession(new EmptySecretDetector());
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);

			await persistence.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);

			var saved = store.SavedProfiles[Path.GetFullPath(projectPath)];
			Assert.Equal([".cs"], saved.SelectedExtensions.ToArray());
			Assert.Contains(IgnoreOptionId.HideSecrets, saved.SelectedIgnoreOptions);
		}
	}

	[Fact]
	public async Task FailedMergedFullProfileWrite_IsQueuedForTheTransitionFlush()
	{
		using var workspace = new TemporaryDirectory();
		var projectPath = workspace.CreateFolder("project");
		var appDataPath = workspace.CreateFolder("app-data");
		var store = new ProjectProfileStore(() => appDataPath);
		Assert.True(store.TrySaveProfile(
			projectPath,
			CreateProfile(".cs") with
			{
				ExtensionStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
				{
					[".cs"] = true,
					[".md"] = false
				}
			}));
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector(), store))
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			viewModel.Extensions.Add(new SelectionOptionViewModel(".md", false));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);
			var loaded = await persistence.LoadSnapshotAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			Assert.Equal(ProjectProfileLookupStatus.Found, loaded.Status);

			viewModel.Extensions[0].IsChecked = false;
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);
			var lockPath = Path.Combine(
				appDataPath,
				"DevProjex",
				"project-profiles.json.lock");
			await using (var heldLock = new FileStream(
							 lockPath,
							 FileMode.OpenOrCreate,
							 FileAccess.ReadWrite,
							 FileShare.None))
			{
				await persistence.PersistIfNeededAsync(
					projectPath,
					TestContext.Current.CancellationToken);
			}
			Assert.True(store.TrySaveProfile(
				projectPath,
				CreateProfile(".md") with
				{
					ExtensionStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
					{
						[".cs"] = true,
						[".md"] = true
					}
				}));

			var flush = persistence.FlushPending(TimeSpan.FromSeconds(1));

			Assert.True(flush.Succeeded);
			Assert.Equal(1, flush.Attempted);
			Assert.True(store.TryLoadProfile(projectPath, out var persisted));
			Assert.Equal([".md"], persisted.SelectedExtensions);
			Assert.False(persisted.ExtensionStates![".cs"]);
			Assert.True(persisted.ExtensionStates[".md"]);
		}
	}

	[Fact]
	public async Task SuccessfulFullProfilePersistRemovesOlderPendingWriteForTheSameProject()
	{
		using var workspace = new TemporaryDirectory();
		var projectPath = workspace.CreateFolder("project");
		var appDataPath = workspace.CreateFolder("app-data");
		var store = new ProjectProfileStore(() => appDataPath);
		Assert.True(store.TrySaveProfile(
			projectPath,
			CreateProfile(".cs") with
			{
				ExtensionStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
				{
					[".cs"] = true,
					[".md"] = false
				}
			}));
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector(), store))
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			viewModel.Extensions.Add(new SelectionOptionViewModel(".md", false));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);
			Assert.Equal(
				ProjectProfileLookupStatus.Found,
				(await persistence.LoadSnapshotAsync(
					projectPath,
					TestContext.Current.CancellationToken)).Status);

			viewModel.Extensions[0].IsChecked = false;
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);
			var lockPath = Path.Combine(
				appDataPath,
				"DevProjex",
				"project-profiles.json.lock");
			await using (var heldLock = new FileStream(
							 lockPath,
							 FileMode.OpenOrCreate,
							 FileAccess.ReadWrite,
							 FileShare.None))
			{
				await persistence.PersistIfNeededAsync(
					projectPath,
					TestContext.Current.CancellationToken);
			}
			Assert.True(persistence.HasPendingWrites);

			viewModel.Extensions[0].IsChecked = true;
			viewModel.Extensions[1].IsChecked = true;
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);
			await persistence.PersistIfNeededAsync(
				projectPath,
				TestContext.Current.CancellationToken);

			Assert.False(persistence.HasPendingWrites);
			var flush = persistence.FlushPending(TimeSpan.FromSeconds(1));
			Assert.Equal(0, flush.Attempted);
			Assert.True(store.TryLoadProfile(projectPath, out var persisted));
			Assert.Equal([".cs", ".md"], persisted.SelectedExtensions);
			Assert.True(persisted.ExtensionStates![".cs"]);
			Assert.True(persisted.ExtensionStates[".md"]);
		}
	}

	[Fact]
	public async Task PendingFullProfileFlushPreservesNewerSelectedPathFrontier()
	{
		using var workspace = new TemporaryDirectory();
		var projectPath = workspace.CreateFolder("project");
		var appDataPath = workspace.CreateFolder("app-data");
		var store = new ProjectProfileStore(() => appDataPath);
		Assert.True(store.TrySaveProfile(
			projectPath,
			CreateProfile(".cs") with
			{
				ExtensionStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
				{
					[".cs"] = true
				},
				SelectedPaths = ["src/baseline.cs"]
			}));
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector(), store))
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);
			IReadOnlyCollection<string>? selectionFrontier = ["src/apply.cs"];
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession,
				selectedPathsProvider: () => selectionFrontier);
			Assert.Equal(
				ProjectProfileLookupStatus.Found,
				(await persistence.LoadSnapshotAsync(
					projectPath,
					TestContext.Current.CancellationToken)).Status);

			viewModel.Extensions[0].IsChecked = false;
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);
			var lockPath = Path.Combine(
				appDataPath,
				"DevProjex",
				"project-profiles.json.lock");
			await using (var heldLock = new FileStream(
							 lockPath,
							 FileMode.OpenOrCreate,
							 FileAccess.ReadWrite,
							 FileShare.None))
			{
				await persistence.PersistIfNeededAsync(
					projectPath,
					TestContext.Current.CancellationToken);
			}
			Assert.True(persistence.HasPendingWrites);

			selectionFrontier = ["src/checkbox.cs"];
			var frontierWrite = await persistence.PersistSelectedPathsAsync(
				projectPath,
				selectionFrontier,
				TestContext.Current.CancellationToken);
			Assert.True(frontierWrite.Completed);

			var flush = persistence.FlushPending(TimeSpan.FromSeconds(1));

			Assert.True(flush.Succeeded);
			Assert.Equal(1, flush.Attempted);
			Assert.True(store.TryLoadProfile(projectPath, out var persisted));
			Assert.False(persisted.ExtensionStates![".cs"]);
			Assert.Equal(["src/checkbox.cs"], persisted.SelectedPaths);
		}
	}

	[Theory]
	[InlineData(ProjectProfileLookupStatus.TemporarilyUnavailable)]
	[InlineData(ProjectProfileLookupStatus.UnsupportedFutureSchema)]
	public async Task AppliedFullProfileIsQueuedWhileStorageIsUnavailable(
		ProjectProfileLookupStatus status)
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);
			var unavailable = new ProjectProfileLookupResult(status, null);
			var store = status == ProjectProfileLookupStatus.TemporarilyUnavailable
				? new StatusProfileStore(unavailable, unavailable)
				: new StatusProfileStore(unavailable);
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession,
				profileLoadRetryDelays: []);

			Assert.Equal(
				status,
				(await persistence.LoadSnapshotAsync(
					projectPath,
					TestContext.Current.CancellationToken)).Status);
			await persistence.PersistIfNeededAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			var flush = persistence.FlushPending(TimeSpan.FromMilliseconds(100));

			Assert.True(persistence.HasPendingWrites);
			Assert.False(flush.Succeeded);
			Assert.Equal(0, flush.Attempted);
			Assert.Equal(1, flush.Remaining);
			Assert.Equal(0, store.SaveCount);
		}
	}

	[Theory]
	[InlineData(ProjectProfileLookupStatus.TemporarilyUnavailable)]
	[InlineData(ProjectProfileLookupStatus.InvalidStorage)]
	public async Task PendingFullProfileRetryRefreshesBlockedReadinessBeforeFlush(
		ProjectProfileLookupStatus blockedStatus)
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);
			var blocked = new ProjectProfileLookupResult(blockedStatus, null);
			var store = new StatusProfileStore(
				blocked,
				blocked,
				new ProjectProfileLookupResult(ProjectProfileLookupStatus.Missing, null));
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession,
				profileLoadRetryDelays: []);

			Assert.Equal(
				blockedStatus,
				(await persistence.LoadSnapshotAsync(
					projectPath,
					TestContext.Current.CancellationToken)).Status);
			await persistence.PersistIfNeededAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			Assert.True(persistence.HasPendingWrites);

			var retry = await persistence.FlushPendingAsync(
				TimeSpan.FromSeconds(1),
				TestContext.Current.CancellationToken);

			Assert.True(retry.Succeeded);
			Assert.Equal(1, retry.Attempted);
			Assert.Equal(1, retry.Saved);
			Assert.False(persistence.HasPendingWrites);
			Assert.Equal(1, store.SaveCount);
		}
	}

	[Fact]
	public async Task PendingWriteDiscardStopsAtItsBoundedGateTimeout()
	{
		using var store = new BlockingProfileSaveStore();
		var queue = new PendingProjectProfileWriteQueue(store);
		var persist = queue.PersistAsync(
			@"C:\Project",
			CreateProfile(".cs"),
			DateTimeOffset.UtcNow,
			canPersist: null,
			TestContext.Current.CancellationToken);
		Assert.True(store.Entered.Wait(
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken));
		var discardMethod = typeof(PendingProjectProfileWriteQueue).GetMethod(
			"DiscardPendingAsync",
			BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
		Assert.NotNull(discardMethod);

		var started = System.Diagnostics.Stopwatch.GetTimestamp();
		var discard = Assert.IsAssignableFrom<Task<bool>>(discardMethod!.Invoke(
			queue,
			[TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken]));
		Assert.False(await discard);
		Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(1));

		store.Release.Set();
		await persist;
	}

	[Fact]
	public async Task InFlightProfilePersistenceParticipatesInTheTransitionBarrierBeforeEnqueue()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);
			var unavailable = new ProjectProfileLookupResult(
				ProjectProfileLookupStatus.TemporarilyUnavailable,
				null);
			var store = new StatusProfileStore(
				unavailable,
				unavailable,
				new ProjectProfileLookupResult(ProjectProfileLookupStatus.Missing, null));
			var delayStarted = new TaskCompletionSource(
				TaskCreationOptions.RunContinuationsAsynchronously);
			var releaseDelay = new TaskCompletionSource(
				TaskCreationOptions.RunContinuationsAsynchronously);
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession,
				profileLoadDelay: async (_, cancellationToken) =>
				{
					delayStarted.TrySetResult();
					await releaseDelay.Task.WaitAsync(cancellationToken);
				},
				profileLoadRetryDelays: [TimeSpan.Zero]);
			Assert.Equal(
				ProjectProfileLookupStatus.TemporarilyUnavailable,
				(await persistence.LoadSnapshotAsync(
					projectPath,
					TestContext.Current.CancellationToken)).Status);

			var persist = persistence.PersistIfNeededAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			await delayStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
			Task<ProjectProfileFlushResult>? flush = null;
			try
			{
				Assert.True(persistence.HasPendingWrites);
				flush = persistence.FlushPendingAsync(
					TimeSpan.FromSeconds(2),
					TestContext.Current.CancellationToken);
				await Task.Yield();
				Assert.False(flush.IsCompleted);
			}
			finally
			{
				releaseDelay.TrySetResult();
				await persist;
			}

			var result = await flush!;
			Assert.True(result.Succeeded);
			Assert.False(persistence.HasPendingWrites);
			Assert.Equal(1, store.SaveCount);
		}
	}

	[Fact]
	public async Task DiscardPendingWritesStopsAtTheInFlightPersistenceBarrier()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);
			var unavailable = new ProjectProfileLookupResult(
				ProjectProfileLookupStatus.TemporarilyUnavailable,
				null);
			var store = new StatusProfileStore(
				unavailable,
				unavailable,
				new ProjectProfileLookupResult(ProjectProfileLookupStatus.Missing, null));
			var delayStarted = new TaskCompletionSource(
				TaskCreationOptions.RunContinuationsAsynchronously);
			var releaseDelay = new TaskCompletionSource(
				TaskCreationOptions.RunContinuationsAsynchronously);
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession,
				profileLoadDelay: async (_, cancellationToken) =>
				{
					delayStarted.TrySetResult();
					await releaseDelay.Task.WaitAsync(cancellationToken);
				},
				profileLoadRetryDelays: [TimeSpan.Zero]);
			Assert.Equal(
				ProjectProfileLookupStatus.TemporarilyUnavailable,
				(await persistence.LoadSnapshotAsync(
					projectPath,
					TestContext.Current.CancellationToken)).Status);

			var persist = persistence.PersistIfNeededAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			await delayStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
			try
			{
				var flush = await persistence.FlushPendingAsync(
					TimeSpan.FromMilliseconds(50),
					TestContext.Current.CancellationToken);
				Assert.False(flush.GateAcquired);
				Assert.False(await persistence.DiscardPendingWritesAsync(
					TimeSpan.FromMilliseconds(50),
					TestContext.Current.CancellationToken));
				Assert.True(persistence.HasPendingWrites);
			}
			finally
			{
				releaseDelay.TrySetResult();
				await persist;
			}

			Assert.Equal(1, store.SaveCount);
			Assert.True(await persistence.DiscardPendingWritesAsync(
				TimeSpan.FromSeconds(1),
				TestContext.Current.CancellationToken));
		}
	}

	[Fact]
	public async Task ClearAllProfilesReturnsBusyWhileProfilePersistenceIsInFlight()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);
			var unavailable = new ProjectProfileLookupResult(
				ProjectProfileLookupStatus.TemporarilyUnavailable,
				null);
			var store = new StatusProfileStore(
				unavailable,
				unavailable,
				new ProjectProfileLookupResult(ProjectProfileLookupStatus.Missing, null));
			var delayStarted = new TaskCompletionSource(
				TaskCreationOptions.RunContinuationsAsynchronously);
			var releaseDelay = new TaskCompletionSource(
				TaskCreationOptions.RunContinuationsAsynchronously);
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession,
				profileLoadDelay: async (_, cancellationToken) =>
				{
					delayStarted.TrySetResult();
					await releaseDelay.Task.WaitAsync(cancellationToken);
				},
				profileLoadRetryDelays: [TimeSpan.Zero]);
			Assert.Equal(
				ProjectProfileLookupStatus.TemporarilyUnavailable,
				(await persistence.LoadSnapshotAsync(
					projectPath,
					TestContext.Current.CancellationToken)).Status);

			var persist = persistence.PersistIfNeededAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			await delayStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
			try
			{
				Assert.Equal(ProjectProfileClearStatus.Busy, persistence.ClearAllProfiles());
				Assert.Equal(0, store.ClearCount);
			}
			finally
			{
				releaseDelay.TrySetResult();
				await persist;
			}

			Assert.NotNull(store.SavedProfile);
			Assert.Equal(ProjectProfileClearStatus.Cleared, persistence.ClearAllProfiles());
			Assert.Equal(1, store.ClearCount);
			Assert.Null(store.SavedProfile);
		}
	}

	[Fact]
	public void PendingWrites_AreRetriedPerProjectWithoutCrossProjectReplacement()
	{
		using var workspace = new TemporaryDirectory();
		var firstProject = workspace.CreateFolder("first");
		var secondProject = workspace.CreateFolder("second");
		var store = new RetryProfileStore(firstProject, failures: 2);
		var queue = new PendingProjectProfileWriteQueue(store);
		var firstProfile = CreateProfile(".cs");
		var secondProfile = CreateProfile(".json");

		queue.Persist(firstProject, firstProfile, DateTimeOffset.UtcNow.AddMinutes(-1));
		Assert.Equal(1, queue.Count);

		queue.Persist(secondProject, secondProfile, DateTimeOffset.UtcNow);

		Assert.Equal(1, queue.Count);
		Assert.DoesNotContain(Path.GetFullPath(firstProject), store.SavedProfiles.Keys);
		Assert.Equal([".json"], store.SavedProfiles[Path.GetFullPath(secondProject)].SelectedExtensions.ToArray());

		queue.Flush();

		Assert.Equal(0, queue.Count);
		Assert.Equal([".cs"], store.SavedProfiles[Path.GetFullPath(firstProject)].SelectedExtensions.ToArray());
	}

	[Fact]
	public void ClearAllProfiles_DropsPendingWritesBeforeTheShutdownFlush()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var store = new RetryProfileStore(project, failures: 1);
		var queue = new PendingProjectProfileWriteQueue(store);
		queue.Persist(project, CreateProfile(".cs"), DateTimeOffset.UtcNow);
		Assert.Equal(1, queue.Count);

		var result = queue.ClearAllProfiles();
		var flush = queue.Flush(TimeSpan.FromSeconds(1));

		Assert.Equal(ProjectProfileClearStatus.Cleared, result);
		Assert.Equal(0, queue.Count);
		Assert.Equal(0, flush.Attempted);
		Assert.Empty(store.SavedProfiles);
	}

	[Fact]
	public async Task PendingWrites_DoNotFlushAProjectBlockedByProfileLoading()
	{
		using var workspace = new TemporaryDirectory();
		var blockedProject = workspace.CreateFolder("blocked");
		var writableProject = workspace.CreateFolder("writable");
		var store = new RetryProfileStore(blockedProject, failures: 1);
		var queue = new PendingProjectProfileWriteQueue(store);
		queue.Persist(blockedProject, CreateProfile(".cs"), DateTimeOffset.UtcNow);
		Assert.Equal(1, queue.Count);

		await queue.PersistAsync(
			writableProject,
			CreateProfile(".json"),
			DateTimeOffset.UtcNow,
			path => !PathComparer.Default.Equals(path, blockedProject),
			TestContext.Current.CancellationToken);

		Assert.Equal(1, queue.Count);
		Assert.DoesNotContain(Path.GetFullPath(blockedProject), store.SavedProfiles.Keys);
		Assert.Equal(
			[".json"],
			store.SavedProfiles[Path.GetFullPath(writableProject)].SelectedExtensions.ToArray());
	}

	[Fact]
	public async Task PersistentMarkWriter_RetriesTheSameTypedDeltaUntilDurableSuccess()
	{
		var snapshot = new PersistentSecretMarksSnapshot(
			7,
			[new MarkedSecretProfileEntry("001122334455", "TOKEN", 12)]);
		var store = new RetryMarkStore(
			new PersistentSecretMarkWriteResult(PersistentSecretMarkStoreStatus.TemporarilyUnavailable, null),
			new PersistentSecretMarkWriteResult(PersistentSecretMarkStoreStatus.WriteFailed, null),
			new PersistentSecretMarkWriteResult(PersistentSecretMarkStoreStatus.Success, snapshot));
		var writer = new PersistentSecretMarkDeltaWriter(
			store,
			static (_, _) => Task.CompletedTask,
			[TimeSpan.Zero, TimeSpan.Zero]);
		var delta = PersistentSecretMarkDelta.Add(snapshot.Marks.Single());

		var result = await writer.ApplyAsync(
			@"C:\Project",
			delta,
			TestContext.Current.CancellationToken);

		Assert.True(result.Succeeded);
		Assert.Equal(3, store.AppliedDeltas.Count);
		Assert.All(store.AppliedDeltas, applied => Assert.Equal(delta, applied));
	}

	[Fact]
	public async Task PersistentMarkWriter_CancellationDuringBackoffStopsBeforeAnotherWrite()
	{
		var store = new RetryMarkStore(
			new PersistentSecretMarkWriteResult(
				PersistentSecretMarkStoreStatus.TemporarilyUnavailable,
				null));
		var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var writer = new PersistentSecretMarkDeltaWriter(
			store,
			async (_, cancellationToken) =>
			{
				delayStarted.TrySetResult();
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			},
			[TimeSpan.FromSeconds(1)]);
		using var cancellation = new CancellationTokenSource();
		var write = writer.ApplyAsync(
			@"C:\Project",
			PersistentSecretMarkDelta.Add(
				new MarkedSecretProfileEntry("001122334455", "TOKEN", 12)),
			cancellation.Token);
		await delayStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
		Assert.Single(store.AppliedDeltas);
	}

	[Fact]
	public async Task PersistentMarkWriter_SerializesDeltasFromOneWindowInIssueOrder()
	{
		var store = new BlockingOrderedMarkStore();
		var writer = new PersistentSecretMarkDeltaWriter(store, retryDelays: []);
		var mark = new MarkedSecretProfileEntry("001122334455", "TOKEN", 12);
		var add = PersistentSecretMarkDelta.Add(mark);
		var remove = PersistentSecretMarkDelta.Remove(
			new PersistentSecretMarkId(mark.H, mark.Length));

		var first = writer.ApplyAsync("project", add, TestContext.Current.CancellationToken);
		await store.FirstEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
		var second = writer.ApplyAsync("project", remove, TestContext.Current.CancellationToken);
		await Task.Delay(50, TestContext.Current.CancellationToken);

		Assert.Equal(1, store.CallCount);
		store.ReleaseFirst.TrySetResult();
		await Task.WhenAll(first, second);
		Assert.Equal([add, remove], store.AppliedDeltas);
	}

	[Fact]
	public async Task MarkWriteCompletingAfterProjectSwitch_DoesNotReplaceTheNewProjectSnapshot()
	{
		using var workspace = new TemporaryDirectory();
		var firstProject = workspace.CreateFolder("first");
		var secondProject = workspace.CreateFolder("second");
		var activeProject = firstProject;
		var store = new BlockingOrderedMarkStore();
		var (viewModel, selection) = CreateSelectionCoordinator(firstProject);
		using (selection)
		using (var session = new SecretRedactionSession(new EmptySecretDetector()))
		{
			var coordinator = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selection,
				store,
				session,
				() => activeProject);
			var firstMark = new MarkedSecretProfileEntry("001122334455", "FIRST", 12);
			var secondMark = new MarkedSecretProfileEntry("66778899aabb", "SECOND", 16);
			var write = coordinator.ApplyMarkDeltaAsync(
				firstProject,
				PersistentSecretMarkDelta.Add(firstMark),
				TestContext.Current.CancellationToken);
			await store.FirstEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

			activeProject = secondProject;
			session.ReplacePersistentMarks(
				secondProject,
				new PersistentSecretMarksSnapshot(4, [secondMark]));
			store.ReleaseFirst.TrySetResult();
			Assert.True((await write).Succeeded);

			Assert.Equal(secondMark, Assert.Single(session.GetMarkedSecrets()));
		}
	}

	[Fact]
	public async Task TemporaryProfileLookup_ReloadsBeforePersisting()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);
			var store = new StatusProfileStore(
				new ProjectProfileLookupResult(ProjectProfileLookupStatus.TemporarilyUnavailable, null),
				new ProjectProfileLookupResult(
					ProjectProfileLookupStatus.Found,
					new ProjectSelectionProfile([], [".json"], [])));
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);

			var unavailable = await persistence.LoadSnapshotAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			await persistence.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);

			Assert.Equal(ProjectProfileLookupStatus.TemporarilyUnavailable, unavailable.Status);
			Assert.Equal(2, store.LookupCount);
			Assert.Equal(1, store.SaveCount);
		}
	}

	[Fact]
	public async Task ProfileLoadRetry_StopsAfterTheConfiguredAttempts()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		{
			var store = new StatusProfileStore(
				new ProjectProfileLookupResult(ProjectProfileLookupStatus.TemporarilyUnavailable, null),
				new ProjectProfileLookupResult(ProjectProfileLookupStatus.TemporarilyUnavailable, null),
				new ProjectProfileLookupResult(ProjectProfileLookupStatus.TemporarilyUnavailable, null));
			var observedDelays = new List<TimeSpan>();
			var retryDelays = new[]
			{
				TimeSpan.FromMilliseconds(10),
				TimeSpan.FromMilliseconds(20)
			};
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession,
				profileLoadDelay: (delay, _) =>
				{
					observedDelays.Add(delay);
					return Task.CompletedTask;
				},
				profileLoadRetryDelays: retryDelays);

			var snapshot = await persistence.LoadSnapshotWithRetryAsync(
				projectPath,
				TestContext.Current.CancellationToken);

			Assert.Equal(ProjectProfileLookupStatus.TemporarilyUnavailable, snapshot.Status);
			Assert.Equal(3, store.LookupCount);
			Assert.Equal(retryDelays, observedDelays.ToArray());
		}
	}

	[Fact]
	public async Task ProfileLoadRetry_ReturnsTheFirstAvailableSnapshot()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		{
			var profile = new ProjectSelectionProfile([], [".cs"], [], SelectedPaths: ["src"]);
			var store = new StatusProfileStore(
				new ProjectProfileLookupResult(ProjectProfileLookupStatus.TemporarilyUnavailable, null),
				new ProjectProfileLookupResult(ProjectProfileLookupStatus.Found, profile));
			var delayCount = 0;
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession,
				profileLoadDelay: (_, _) =>
				{
					Interlocked.Increment(ref delayCount);
					return Task.CompletedTask;
				},
				profileLoadRetryDelays: [TimeSpan.FromMilliseconds(10)]);

			var snapshot = await persistence.LoadSnapshotWithRetryAsync(
				projectPath,
				TestContext.Current.CancellationToken);

			Assert.True(snapshot.HasProfile);
			Assert.Equal(["src"], snapshot.Profile!.SelectedPaths);
			Assert.Equal(2, store.LookupCount);
			Assert.Equal(1, delayCount);
		}
	}

	[Fact]
	public async Task SelectionPersist_RecoversAfterTheInitialProfileLockClears()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);
			var existing = new ProjectSelectionProfile([], [".json"], [], SelectedPaths: ["old"]);
			var store = new StatusProfileStore(
				new ProjectProfileLookupResult(ProjectProfileLookupStatus.TemporarilyUnavailable, null),
				new ProjectProfileLookupResult(ProjectProfileLookupStatus.TemporarilyUnavailable, null),
				new ProjectProfileLookupResult(ProjectProfileLookupStatus.Found, existing));
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession,
				profileLoadDelay: static (_, _) => Task.CompletedTask,
				profileLoadRetryDelays: [TimeSpan.Zero]);

			var unavailable = await persistence.LoadSnapshotWithRetryAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			await persistence.PersistSelectedPathsAsync(
				projectPath,
				["src"],
				TestContext.Current.CancellationToken);

			Assert.Equal(ProjectProfileLookupStatus.TemporarilyUnavailable, unavailable.Status);
			Assert.Equal(3, store.LookupCount);
			Assert.Equal(1, store.SaveCount);
			Assert.Equal([".json"], store.SavedProfile!.SelectedExtensions);
			Assert.Equal(["src"], store.SavedProfile.SelectedPaths);
		}
	}

	[Fact]
	public async Task FirstBackupReadLoadsTheSnapshotAndKeepsWindowPersistenceAvailable()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);
			var profile = new ProjectSelectionProfile([], [".cs"], [], SelectedPaths: ["src"]);
			var recovered = new ProjectProfileLookupResult(ProjectProfileLookupStatus.Found, profile)
			{
				RecoveryStatus = ProjectProfileLookupStatus.InvalidStorage
			};
			var store = new StatusProfileStore(
				recovered,
				new ProjectProfileLookupResult(ProjectProfileLookupStatus.Found, profile));
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);

			var backup = await persistence.LoadSnapshotAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			await persistence.PersistSelectedPathsAsync(
				projectPath,
				["src/Inside.cs"],
				TestContext.Current.CancellationToken);
			Assert.Equal(1, store.SaveCount);
			var primary = await persistence.LoadSnapshotAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			await persistence.PersistSelectedPathsAsync(
				projectPath,
				["src/New.cs"],
				TestContext.Current.CancellationToken);

			Assert.True(backup.HasProfile);
			Assert.Equal(["src"], backup.Profile!.SelectedPaths);
			Assert.True(primary.HasProfile);
			Assert.Equal(2, store.SaveCount);
		}
	}

	[Fact]
	public async Task BackupReadAfterASuccessfulLoadReusesTheCurrentWindowSnapshot()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);
			var current = new ProjectSelectionProfile([], [".cs"], [], SelectedPaths: ["src"]);
			var stale = new ProjectSelectionProfile([], [".md"], [], SelectedPaths: ["docs"]);
			var recovered = new ProjectProfileLookupResult(ProjectProfileLookupStatus.Found, stale)
			{
				RecoveryStatus = ProjectProfileLookupStatus.InvalidStorage
			};
			var store = new StatusProfileStore(
				new ProjectProfileLookupResult(ProjectProfileLookupStatus.Found, current),
				recovered,
				recovered,
				new ProjectProfileLookupResult(ProjectProfileLookupStatus.Found, current));
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);

			var initial = await persistence.LoadSnapshotAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			var firstFailure = await persistence.LoadSnapshotAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			var repeatedFailure = await persistence.LoadSnapshotAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			await persistence.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);
			Assert.Equal(1, store.SaveCount);
			var repaired = await persistence.LoadSnapshotAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			await persistence.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);

			Assert.Equal(["src"], initial.Profile!.SelectedPaths);
			Assert.Equal(["src"], firstFailure.Profile!.SelectedPaths);
			Assert.Equal(["src"], repeatedFailure.Profile!.SelectedPaths);
			Assert.True(repaired.HasProfile);
			Assert.Equal(2, store.SaveCount);
		}
	}

	[Fact]
	public async Task MissingBackupEntryAfterASuccessfulLoadReusesTheCurrentWindowSnapshot()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);
			var current = new ProjectSelectionProfile([], [".cs"], [], SelectedPaths: ["src"]);
			var recoveredMissing = new ProjectProfileLookupResult(ProjectProfileLookupStatus.Missing, null)
			{
				RecoveryStatus = ProjectProfileLookupStatus.InvalidStorage
			};
			var store = new StatusProfileStore(
				new ProjectProfileLookupResult(ProjectProfileLookupStatus.Found, current),
				recoveredMissing);
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);

			_ = await persistence.LoadSnapshotAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			var degraded = await persistence.LoadSnapshotAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			await persistence.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);

			Assert.Equal(ProjectProfileLookupStatus.Found, degraded.Status);
			Assert.Equal(["src"], degraded.Profile!.SelectedPaths);
			Assert.Equal(1, store.SaveCount);
		}
	}

	[Fact]
	public async Task ProfileLookupInProgress_BlocksPersistBeforeTheStoreReturnsAStatus()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		using (var store = new BlockingLookupProfileStore())
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);
			var load = persistence.LoadSnapshotAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			Assert.True(store.Entered.Wait(
				TimeSpan.FromSeconds(5),
				TestContext.Current.CancellationToken));

			await persistence.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);
			Assert.Equal(0, store.SaveCount);

			store.Release.Set();
			Assert.Equal(ProjectProfileLookupStatus.Missing, (await load).Status);
			await persistence.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);
			Assert.Equal(1, store.SaveCount);
		}
	}

	[Fact]
	public async Task CanceledMarksReload_RestoresThePreviousPersistableLoadState()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		using (var cancellation = new CancellationTokenSource())
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);
			var store = new CancelingMarksReloadStore();
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);
			Assert.Equal(
				ProjectProfileLookupStatus.Missing,
				(await persistence.LoadSnapshotAsync(
					projectPath,
					TestContext.Current.CancellationToken)).Status);

			var canceledReload = persistence.LoadSnapshotAsync(projectPath, cancellation.Token);
			await store.SecondMarksLoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
			cancellation.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledReload);
			await persistence.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);

			Assert.Equal(1, store.SaveCount);
		}
	}

	[Fact]
	public async Task InvalidProfileStorage_BlocksAutomaticPersist()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		{
			var store = new StatusProfileStore(
				new ProjectProfileLookupResult(ProjectProfileLookupStatus.InvalidStorage, null));
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);

			var snapshot = await persistence.LoadSnapshotAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			await persistence.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);

			Assert.Equal(ProjectProfileLookupStatus.InvalidStorage, snapshot.Status);
			Assert.Equal(0, store.SaveCount);
		}
	}

	[Fact]
	public async Task MissingSelectionProfile_LoadsIndependentPersistentMarks()
	{
		using var workspace = new TemporaryDirectory();
		var projectPath = workspace.CreateFolder("project");
		var appDataPath = workspace.CreateFolder("app-data");
		var store = new ProjectProfileStore(() => appDataPath);
		var mark = new MarkedSecretProfileEntry("001122334455", "TOKEN", 12);
		var write = await store.AddMarkAsync(
			projectPath,
			mark,
			TestContext.Current.CancellationToken);
		Assert.True(write.Succeeded);
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector(), store))
		{
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);

			var snapshot = await persistence.LoadSnapshotAsync(
				projectPath,
				TestContext.Current.CancellationToken);

			Assert.Equal(ProjectProfileLookupStatus.Missing, snapshot.Status);
			Assert.Null(snapshot.Profile);
			Assert.Equal(mark, Assert.Single(snapshot.PersistentMarks!.Marks));
		}
	}

	[Fact]
	public async Task UnavailableMarkStore_BlocksSelectionPersistUntilMarksLoad()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);
			var mark = new MarkedSecretProfileEntry("001122334455", "TOKEN", 12);
			var store = new StatusProfileAndMarkStore(
				new PersistentSecretMarksLoadResult(
					PersistentSecretMarkStoreStatus.TemporarilyUnavailable,
					null),
				new PersistentSecretMarksLoadResult(
					PersistentSecretMarkStoreStatus.Success,
					new PersistentSecretMarksSnapshot(4, [mark])));
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);

			var unavailable = await persistence.LoadSnapshotAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			await persistence.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);
			var loaded = await persistence.LoadSnapshotAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			await persistence.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);

			Assert.Equal(ProjectProfileLookupStatus.TemporarilyUnavailable, unavailable.Status);
			Assert.Equal(ProjectProfileLookupStatus.Missing, loaded.Status);
			Assert.Equal(mark, Assert.Single(loaded.PersistentMarks!.Marks));
			Assert.Equal(1, store.SaveCount);
		}
	}

	[Fact]
	public async Task IndependentGuiCoordinators_MergeMarkDeltasWithoutSelectionSaveResurrection()
	{
		using var workspace = new TemporaryDirectory();
		var projectPath = workspace.CreateFolder("project");
		var appDataPath = workspace.CreateFolder("app-data");
		var storeA = new ProjectProfileStore(() => appDataPath);
		var storeB = new ProjectProfileStore(() => appDataPath);
		var (viewModelA, selectionA) = CreateSelectionCoordinator(projectPath);
		var (viewModelB, selectionB) = CreateSelectionCoordinator(projectPath);
		using (selectionA)
		using (selectionB)
		using (var sessionA = new SecretRedactionSession(new EmptySecretDetector(), storeA))
		using (var sessionB = new SecretRedactionSession(new EmptySecretDetector(), storeB))
		{
			viewModelA.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			viewModelB.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			selectionA.AcceptCurrentSelectionsAsApplied(projectPath);
			selectionB.AcceptCurrentSelectionsAsApplied(projectPath);
			var coordinatorA = new ProjectProfilePersistenceCoordinator(
				viewModelA,
				selectionA,
				storeA,
				sessionA);
			var coordinatorB = new ProjectProfilePersistenceCoordinator(
				viewModelB,
				selectionB,
				storeB,
				sessionB);
			await coordinatorA.LoadSnapshotAsync(projectPath, TestContext.Current.CancellationToken);
			await coordinatorB.LoadSnapshotAsync(projectPath, TestContext.Current.CancellationToken);

			var markA = new MarkedSecretProfileEntry("001122334455", "A", 12);
			var markB = new MarkedSecretProfileEntry("66778899aabb", "B", 16);
			var staleAddA = PersistentSecretMarkDelta.Add(
				markA,
				sessionA.PersistentMarksStoreRevision);
			var addedA = await coordinatorA.ApplyMarkDeltaAsync(
				projectPath,
				staleAddA,
				TestContext.Current.CancellationToken);
			Assert.True(addedA.Succeeded);
			Assert.True((await coordinatorB.ApplyMarkDeltaAsync(
				projectPath,
				PersistentSecretMarkDelta.Add(
					markB,
					sessionB.PersistentMarksStoreRevision),
				TestContext.Current.CancellationToken)).Succeeded);
			Assert.True((await coordinatorA.ApplyMarkDeltaAsync(
				projectPath,
				PersistentSecretMarkDelta.Remove(
					new PersistentSecretMarkId(markA.H, markA.Length),
					addedA.Snapshot!.Revision),
				TestContext.Current.CancellationToken)).Succeeded);

			await coordinatorB.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);
			Assert.True((await coordinatorB.ApplyMarkDeltaAsync(
				projectPath,
				staleAddA,
				TestContext.Current.CancellationToken)).Succeeded);

			var reopened = await new ProjectProfileStore(() => appDataPath)
				.LoadMarksAsync(projectPath, TestContext.Current.CancellationToken);
			Assert.True(reopened.Succeeded);
			Assert.Equal(markB, Assert.Single(reopened.Snapshot!.Marks));
		}
	}

	[Fact]
	public async Task ConcurrentExternalOptionValueDoesNotBecomeTheLocalInteractionBaseline()
	{
		using var workspace = new TemporaryDirectory();
		var projectPath = workspace.CreateFolder("project");
		var appDataPath = workspace.CreateFolder("app-data");
		var storeA = new ProjectProfileStore(() => appDataPath);
		var storeB = new ProjectProfileStore(() => appDataPath);
		var initial = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [".cs"],
			SelectedIgnoreOptions: [],
			IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.HidePrivateData] = false,
				[IgnoreOptionId.EmptyFolders] = false
			});
		Assert.True(storeA.TrySaveProfile(projectPath, initial));
		var (viewModelA, selectionA) = CreateSelectionCoordinator(projectPath);
		var (viewModelB, selectionB) = CreateSelectionCoordinator(projectPath);
		AddIgnoreOptions(viewModelA);
		AddIgnoreOptions(viewModelB);
		selectionA.AcceptCurrentSelectionsAsApplied(projectPath);
		selectionB.AcceptCurrentSelectionsAsApplied(projectPath);
		using (selectionA)
		using (selectionB)
		using (var sessionA = new SecretRedactionSession(new EmptySecretDetector(), storeA))
		using (var sessionB = new SecretRedactionSession(new EmptySecretDetector(), storeB))
		{
			var coordinatorA = new ProjectProfilePersistenceCoordinator(viewModelA, selectionA, storeA, sessionA);
			var coordinatorB = new ProjectProfilePersistenceCoordinator(viewModelB, selectionB, storeB, sessionB);
			await coordinatorA.LoadSnapshotAsync(projectPath, TestContext.Current.CancellationToken);
			await coordinatorB.LoadSnapshotAsync(projectPath, TestContext.Current.CancellationToken);

			viewModelA.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.HidePrivateData).IsChecked = true;
			selectionA.AcceptCurrentSelectionsAsApplied(projectPath);
			await coordinatorA.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);

			viewModelB.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFolders).IsChecked = true;
			selectionB.AcceptCurrentSelectionsAsApplied(projectPath);
			await coordinatorB.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);
			await coordinatorB.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);

			Assert.True(storeA.TryLoadProfile(projectPath, out var loaded));
			Assert.True(loaded.IgnoreOptionStates![IgnoreOptionId.HidePrivateData]);
			Assert.True(loaded.IgnoreOptionStates[IgnoreOptionId.EmptyFolders]);
		}
	}

	private static void AddIgnoreOptions(MainWindowViewModel viewModel)
	{
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
		viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(
			IgnoreOptionId.HidePrivateData,
			"private data",
			false));
		viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(
			IgnoreOptionId.EmptyFolders,
			"empty folders",
			false));
	}

	private static ProjectSelectionProfile CreateProfile(string extension) => new(
		SelectedRootFolders: [],
		SelectedExtensions: [extension],
		SelectedIgnoreOptions: []);

	private static (MainWindowViewModel ViewModel, SelectionSyncCoordinator Coordinator)
		CreateSelectionCoordinator(string projectPath)
	{
		var catalog = new StubLocalizationCatalog(
			new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
			{
				[AppLanguage.En] = new Dictionary<string, string>()
			});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		var viewModel = new MainWindowViewModel(localization, new HelpContentProvider());
		var coordinator = new SelectionSyncCoordinator(
			viewModel,
			new ScanOptionsUseCase(LegacyWorkspaceScannerTestAdapter.Adapt(new StubFileSystemScanner())),
			new FilterOptionSelectionService(),
			new IgnoreOptionsService(localization),
			_ => new IgnoreRules(
				false,
				false,
				false,
				false,
				new HashSet<string>(),
				new HashSet<string>()),
			_ => false,
			() => projectPath);
		return (viewModel, coordinator);
	}

	private sealed class RetryProfileStore(string failingPath, int failures) : IProjectProfileStore
	{
		private readonly string _failingPath = Path.GetFullPath(failingPath);
		private int _remainingFailures = failures;

		public Dictionary<string, ProjectSelectionProfile> SavedProfiles { get; } =
			new(PathComparer.Default);

		public bool TrySaveProfile(
			string localProjectPath,
			ProjectSelectionProfile profile,
			DateTimeOffset updatedUtc)
		{
			var path = Path.GetFullPath(localProjectPath);
			if (PathComparer.Default.Equals(path, _failingPath) && _remainingFailures-- > 0)
				return false;

			SavedProfiles[path] = ProjectSelectionProfileBuilder.Clone(profile);
			return true;
		}

		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile) =>
			TrySaveProfile(localProjectPath, profile, DateTimeOffset.UtcNow);

		public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile) =>
			_ = TrySaveProfile(localProjectPath, profile);

		public bool EnsureStorageExists() => true;

		public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile) =>
			SavedProfiles.TryGetValue(Path.GetFullPath(localProjectPath), out profile!);

		public ProjectProfileClearStatus ClearAllProfiles()
		{
			SavedProfiles.Clear();
			return ProjectProfileClearStatus.Cleared;
		}
	}

	private sealed class EmptySecretDetector : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) => [];
	}

	private sealed class RetryMarkStore(params PersistentSecretMarkWriteResult[] results) :
		IPersistentSecretMarkStore
	{
		private readonly Queue<PersistentSecretMarkWriteResult> _results = new(results);

		public List<PersistentSecretMarkDelta> AppliedDeltas { get; } = [];

		public ValueTask<PersistentSecretMarksLoadResult> LoadMarksAsync(
			string localProjectPath,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(new PersistentSecretMarksLoadResult(
				PersistentSecretMarkStoreStatus.Success,
				PersistentSecretMarksSnapshot.Empty));

		public ValueTask<PersistentSecretMarkWriteResult> AddMarkAsync(
			string localProjectPath,
			MarkedSecretProfileEntry mark,
			CancellationToken cancellationToken = default) =>
			ApplyMarkDeltaAsync(localProjectPath, PersistentSecretMarkDelta.Add(mark), cancellationToken);

		public ValueTask<PersistentSecretMarkWriteResult> RemoveMarkAsync(
			string localProjectPath,
			PersistentSecretMarkId markId,
			CancellationToken cancellationToken = default) =>
			ApplyMarkDeltaAsync(localProjectPath, PersistentSecretMarkDelta.Remove(markId), cancellationToken);

		public ValueTask<PersistentSecretMarkWriteResult> ApplyMarkDeltaAsync(
			string localProjectPath,
			PersistentSecretMarkDelta delta,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			AppliedDeltas.Add(delta);
			return ValueTask.FromResult(_results.Dequeue());
		}
	}

	private sealed class BlockingOrderedMarkStore : IProjectProfileStore, IPersistentSecretMarkStore
	{
		private int _callCount;

		public TaskCompletionSource FirstEntered { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseFirst { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public List<PersistentSecretMarkDelta> AppliedDeltas { get; } = [];
		public int CallCount => Volatile.Read(ref _callCount);

		public ProjectProfileLookupResult LookupProfile(string localProjectPath, TimeSpan lockTimeout) =>
			new(ProjectProfileLookupStatus.Missing, null);

		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile) => true;

		public bool TrySaveProfile(
			string localProjectPath,
			ProjectSelectionProfile profile,
			DateTimeOffset updatedUtc) => true;

		public bool EnsureStorageExists() => true;

		public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile)
		{
			profile = null!;
			return false;
		}

		public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile)
		{
		}

		public ProjectProfileClearStatus ClearAllProfiles() => ProjectProfileClearStatus.Cleared;

		public ValueTask<PersistentSecretMarksLoadResult> LoadMarksAsync(
			string localProjectPath,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<PersistentSecretMarkWriteResult> AddMarkAsync(
			string localProjectPath,
			MarkedSecretProfileEntry mark,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<PersistentSecretMarkWriteResult> RemoveMarkAsync(
			string localProjectPath,
			PersistentSecretMarkId markId,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public async ValueTask<PersistentSecretMarkWriteResult> ApplyMarkDeltaAsync(
			string localProjectPath,
			PersistentSecretMarkDelta delta,
			CancellationToken cancellationToken = default)
		{
			var call = Interlocked.Increment(ref _callCount);
			lock (AppliedDeltas)
				AppliedDeltas.Add(delta);
			if (call == 1)
			{
				FirstEntered.TrySetResult();
				await ReleaseFirst.Task.WaitAsync(cancellationToken);
			}

			return new PersistentSecretMarkWriteResult(
				PersistentSecretMarkStoreStatus.Success,
				PersistentSecretMarksSnapshot.Empty);
		}
	}

	private sealed class StatusProfileStore(params ProjectProfileLookupResult[] lookups) :
		IProjectProfileStore
	{
		private readonly Queue<ProjectProfileLookupResult> _lookups = new(lookups);

		public int LookupCount { get; private set; }
		public int SaveCount { get; private set; }
		public int ClearCount { get; private set; }
		public ProjectSelectionProfile? SavedProfile { get; private set; }

		public ProjectProfileLookupResult LookupProfile(string localProjectPath, TimeSpan lockTimeout)
		{
			LookupCount++;
			return _lookups.Dequeue();
		}

		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile)
		{
			SaveCount++;
			SavedProfile = ProjectSelectionProfileBuilder.Clone(profile);
			return true;
		}

		public bool TrySaveProfile(
			string localProjectPath,
			ProjectSelectionProfile profile,
			DateTimeOffset updatedUtc) =>
			TrySaveProfile(localProjectPath, profile);

		public bool EnsureStorageExists() => true;

		public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile)
		{
			var result = LookupProfile(localProjectPath, TimeSpan.Zero);
			profile = result.Profile!;
			return result.Status == ProjectProfileLookupStatus.Found;
		}

		public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile) =>
			TrySaveProfile(localProjectPath, profile);

		public ProjectProfileClearStatus ClearAllProfiles()
		{
			ClearCount++;
			SavedProfile = null;
			return ProjectProfileClearStatus.Cleared;
		}
	}

	private sealed class BlockingProfileSaveStore : IProjectProfileStore, IDisposable
	{
		public ManualResetEventSlim Entered { get; } = new();
		public ManualResetEventSlim Release { get; } = new();

		public bool TrySaveProfile(
			string localProjectPath,
			ProjectSelectionProfile profile,
			DateTimeOffset updatedUtc)
		{
			Entered.Set();
			if (!Release.Wait(TimeSpan.FromSeconds(5)))
				throw new TimeoutException("The controlled profile save was not released.");
			return true;
		}

		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile) =>
			TrySaveProfile(localProjectPath, profile, DateTimeOffset.UtcNow);

		public bool EnsureStorageExists() => true;

		public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile)
		{
			profile = null!;
			return false;
		}

		public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile) =>
			_ = TrySaveProfile(localProjectPath, profile);

		public ProjectProfileClearStatus ClearAllProfiles() => ProjectProfileClearStatus.Cleared;

		public void Dispose()
		{
			Release.Set();
			Entered.Dispose();
			Release.Dispose();
		}
	}

	private sealed class CancelingMarksReloadStore : IProjectProfileStore, IPersistentSecretMarkStore
	{
		private int _marksLoadCount;

		public TaskCompletionSource SecondMarksLoadStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public int SaveCount { get; private set; }

		public ProjectProfileLookupResult LookupProfile(string localProjectPath, TimeSpan lockTimeout) =>
			new(ProjectProfileLookupStatus.Missing, null);

		public async ValueTask<PersistentSecretMarksLoadResult> LoadMarksAsync(
			string localProjectPath,
			CancellationToken cancellationToken = default)
		{
			if (Interlocked.Increment(ref _marksLoadCount) == 1)
			{
				return new PersistentSecretMarksLoadResult(
					PersistentSecretMarkStoreStatus.Success,
					PersistentSecretMarksSnapshot.Empty);
			}

			SecondMarksLoadStarted.TrySetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			throw new InvalidOperationException("The canceled mark load unexpectedly resumed.");
		}

		public ValueTask<PersistentSecretMarkWriteResult> AddMarkAsync(
			string localProjectPath,
			MarkedSecretProfileEntry mark,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<PersistentSecretMarkWriteResult> RemoveMarkAsync(
			string localProjectPath,
			PersistentSecretMarkId markId,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<PersistentSecretMarkWriteResult> ApplyMarkDeltaAsync(
			string localProjectPath,
			PersistentSecretMarkDelta delta,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile)
		{
			SaveCount++;
			return true;
		}

		public bool TrySaveProfile(
			string localProjectPath,
			ProjectSelectionProfile profile,
			DateTimeOffset updatedUtc) =>
			TrySaveProfile(localProjectPath, profile);

		public bool EnsureStorageExists() => true;

		public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile)
		{
			profile = null!;
			return false;
		}

		public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile) =>
			TrySaveProfile(localProjectPath, profile);

		public ProjectProfileClearStatus ClearAllProfiles() => ProjectProfileClearStatus.Cleared;
	}

	private sealed class BlockingLookupProfileStore : IProjectProfileStore, IDisposable
	{
		public ManualResetEventSlim Entered { get; } = new();
		public ManualResetEventSlim Release { get; } = new();
		public int SaveCount { get; private set; }

		public ProjectProfileLookupResult LookupProfile(string localProjectPath, TimeSpan lockTimeout)
		{
			Entered.Set();
			if (!Release.Wait(TimeSpan.FromSeconds(5)))
				throw new TimeoutException("The controlled profile lookup was not released.");
			return new ProjectProfileLookupResult(ProjectProfileLookupStatus.Missing, null);
		}

		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile)
		{
			SaveCount++;
			return true;
		}

		public bool TrySaveProfile(
			string localProjectPath,
			ProjectSelectionProfile profile,
			DateTimeOffset updatedUtc) =>
			TrySaveProfile(localProjectPath, profile);

		public bool EnsureStorageExists() => true;

		public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile)
		{
			profile = null!;
			return false;
		}

		public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile) =>
			TrySaveProfile(localProjectPath, profile);

		public ProjectProfileClearStatus ClearAllProfiles() => ProjectProfileClearStatus.Cleared;

		public void Dispose()
		{
			Release.Set();
			Entered.Dispose();
			Release.Dispose();
		}
	}

	private sealed class StatusProfileAndMarkStore(
		params PersistentSecretMarksLoadResult[] markLookups) :
		IProjectProfileStore,
		IPersistentSecretMarkStore
	{
		private readonly Queue<PersistentSecretMarksLoadResult> _markLookups = new(markLookups);

		public int SaveCount { get; private set; }

		public ProjectProfileLookupResult LookupProfile(string localProjectPath, TimeSpan lockTimeout) =>
			new(ProjectProfileLookupStatus.Missing, null);

		public ValueTask<PersistentSecretMarksLoadResult> LoadMarksAsync(
			string localProjectPath,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.FromResult(_markLookups.Dequeue());
		}

		public ValueTask<PersistentSecretMarkWriteResult> AddMarkAsync(
			string localProjectPath,
			MarkedSecretProfileEntry mark,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<PersistentSecretMarkWriteResult> RemoveMarkAsync(
			string localProjectPath,
			PersistentSecretMarkId markId,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<PersistentSecretMarkWriteResult> ApplyMarkDeltaAsync(
			string localProjectPath,
			PersistentSecretMarkDelta delta,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile)
		{
			SaveCount++;
			return true;
		}

		public bool TrySaveProfile(
			string localProjectPath,
			ProjectSelectionProfile profile,
			DateTimeOffset updatedUtc) =>
			TrySaveProfile(localProjectPath, profile);

		public bool EnsureStorageExists() => true;

		public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile)
		{
			profile = null!;
			return false;
		}

		public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile) =>
			TrySaveProfile(localProjectPath, profile);

		public ProjectProfileClearStatus ClearAllProfiles() => ProjectProfileClearStatus.Cleared;
	}
}
