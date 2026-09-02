using System.Collections.Concurrent;
using System.ComponentModel;
using DevProjex.Application.Secrets;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Tests.UI;

[Collection("AvaloniaUI")]
public sealed class MainWindowApplySettingsMemoryCleanupUiTests
{
	[AvaloniaFact]
	public async Task ApplySettings_ContentTransformationsOnly_UsesSequencedFastPath()
	{
		using var project = UiTestProject.CreateDefault();
		var profileStore = new RecordingProjectProfileStore();
		var (window, sessionMetrics) = await CreateMeasuredWindowAsync(
			project,
			services => services with { ProjectProfileStore = profileStore });
		try
		{
			await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);
			await UiTestDriver.WaitForMemoryCleanupIdleAsync(window);
			var treeIdentity = UiTestDriver.GetCurrentTreeIdentity(window);

			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.CompressCode);
			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.StripComments);
			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.StripBlankLines);
			await UiTestDriver.ClickApplySettingsAsync(window);
			await WaitForApplyCleanupCountAsync(window, sessionMetrics, expectedCount: 1);

			Assert.Same(treeIdentity, UiTestDriver.GetCurrentTreeIdentity(window));
			Assert.Equal((true, true, true), UiTestDriver.GetAppliedContentTransformationState(window));
			Assert.Equal(0, UiTestDriver.GetRetainedReadFactBytes(window));
			Assert.True(profileStore.TryLoadProfile(project.RootPath, out var savedProfile));
			Assert.Contains(IgnoreOptionId.CompressCode, savedProfile.SelectedIgnoreOptions);
			Assert.Contains(IgnoreOptionId.StripComments, savedProfile.SelectedIgnoreOptions);
			Assert.Contains(IgnoreOptionId.StripBlankLines, savedProfile.SelectedIgnoreOptions);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

    [AvaloniaFact]
    public async Task ApplySettings_DisablingLastTransformationSchedulesCleanup()
    {
        using var project = UiTestProject.CreateDefault();
        var (window, sessionMetrics) = await CreateMeasuredWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);
            await UiTestDriver.WaitForMemoryCleanupIdleAsync(window);
            Assert.Equal(0, CountApplyCleanupRequests(sessionMetrics));
			var treeIdentity = UiTestDriver.GetCurrentTreeIdentity(window);

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(
                window,
                IgnoreOptionId.CompressCode);
            Assert.Equal(0, CountApplyCleanupRequests(sessionMetrics));

            await UiTestDriver.ClickApplySettingsAsync(window);
            await WaitForApplyCleanupCountAsync(window, sessionMetrics, expectedCount: 1);
            await UiTestDriver.WaitForMemoryCleanupIdleAsync(window);
			Assert.Same(treeIdentity, UiTestDriver.GetCurrentTreeIdentity(window));
			Assert.Equal((true, false, false), UiTestDriver.GetAppliedContentTransformationState(window));
			Assert.Equal(0, UiTestDriver.GetRetainedReadFactBytes(window));

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(
                window,
                IgnoreOptionId.CompressCode);
            Assert.Equal(1, CountApplyCleanupRequests(sessionMetrics));

            await UiTestDriver.ClickApplySettingsAsync(window);
            await WaitForApplyCleanupCountAsync(window, sessionMetrics, expectedCount: 2);
			Assert.Same(treeIdentity, UiTestDriver.GetCurrentTreeIdentity(window));
			Assert.Equal((false, false, false), UiTestDriver.GetAppliedContentTransformationState(window));
			Assert.Equal(0, UiTestDriver.GetRetainedReadFactBytes(window));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ApplySettings_StructuralRefreshSchedulesCleanupAfterPostLoadWork()
    {
        using var project = UiTestProject.CreateWithDynamicIgnoreEntries();
        var (window, sessionMetrics) = await CreateMeasuredWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);
            await UiTestDriver.WaitForMemoryCleanupIdleAsync(window);
            Assert.Equal(0, CountApplyCleanupRequests(sessionMetrics));

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(
                window,
                IgnoreOptionId.EmptyFiles);
            Assert.Equal(0, CountApplyCleanupRequests(sessionMetrics));

            await UiTestDriver.ClickApplySettingsAsync(window);
            await WaitForApplyCleanupCountAsync(window, sessionMetrics, expectedCount: 1);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ApplySettings_KeepsPublishedStatusMetricsVisibleAcrossFastAndStructuralPaths()
    {
        using var project = UiTestProject.CreateWithDynamicIgnoreEntries();
        var (window, _) = await CreateMeasuredWindowAsync(project);
        var viewModel = UiTestDriver.GetViewModel(window);
        var visibilityChanges = new ConcurrentQueue<bool>();
        PropertyChangedEventHandler recorder = (_, args) =>
        {
            if (string.Equals(
                    args.PropertyName,
                    nameof(viewModel.StatusMetricsVisible),
                    StringComparison.Ordinal))
            {
                visibilityChanges.Enqueue(viewModel.StatusMetricsVisible);
            }
        };

        try
        {
            await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);
            Assert.True(viewModel.StatusMetricsVisible);
            viewModel.PropertyChanged += recorder;

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(
                window,
                IgnoreOptionId.CompressCode);
            await UiTestDriver.ClickApplySettingsAsync(window);
            await UiTestDriver.WaitForStatusMetricsReadyAsync(window);

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(
                window,
                IgnoreOptionId.EmptyFiles);
            await UiTestDriver.ClickApplySettingsAsync(window);
            await UiTestDriver.WaitForStatusMetricsReadyAsync(window);

            Assert.True(viewModel.StatusMetricsVisible);
            Assert.DoesNotContain(false, visibilityChanges);
        }
        finally
        {
            viewModel.PropertyChanged -= recorder;
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SecretDiscovery_CompletedScanSchedulesCleanup()
    {
        using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
        var (window, sessionMetrics) = await CreateMeasuredWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);
            await UiTestDriver.WaitForMemoryCleanupIdleAsync(window);

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(
                window,
                IgnoreOptionId.HideSecrets);
            await UiTestDriver.ClickApplySettingsAsync(window);
            await WaitForCompletedSecretScanAsync(window);
            await WaitForApplyCleanupCountAsync(window, sessionMetrics, expectedCount: 1);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SecretDiscovery_SupersededScanSchedulesOnlyForLatestCompletion()
    {
        using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
        var analyzer = new FirstSecretScanCancellationAnalyzer();
        var (window, sessionMetrics) = await CreateMeasuredWindowAsync(
            project,
            services => services with
            {
                FileContentAnalyzer = analyzer.Attach(services.FileContentAnalyzer)
            });

        try
        {
            await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);
            await UiTestDriver.WaitForMemoryCleanupIdleAsync(window);

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(
                window,
                IgnoreOptionId.HideSecrets);
            await StartApplySettingsWithoutWaitingForBackgroundWorkAsync(window);
            await analyzer.FirstScanStarted.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(
                window,
                IgnoreOptionId.HidePrivateData);
            await StartApplySettingsWithoutWaitingForBackgroundWorkAsync(window);
            await analyzer.FirstScanCanceled.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            await analyzer.SecondScanStarted.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.Equal(0, CountApplyCleanupRequests(sessionMetrics));

            analyzer.ReleaseSecondScan();
            await WaitForCompletedSecretScanAsync(window);
            await WaitForApplyCleanupCountAsync(window, sessionMetrics, expectedCount: 1);
            Assert.Equal(1, CountApplyCleanupRequests(sessionMetrics));
        }
        finally
        {
            analyzer.ReleaseFirstScan();
            analyzer.ReleaseSecondScan();
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

	[AvaloniaFact]
	public async Task SecretDiscovery_FullRefreshCancelsObsoleteGenerationWithoutPublishingFailure()
	{
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		var analyzer = new FirstSecretScanCancellationAnalyzer();
		var (window, _) = await CreateMeasuredWindowAsync(
			project,
			services => services with
			{
				FileContentAnalyzer = analyzer.Attach(services.FileContentAnalyzer)
			});
		var notices = new ConcurrentQueue<string>();
		var viewModel = UiTestDriver.GetViewModel(window);
		PropertyChangedEventHandler noticeRecorder = (_, args) =>
		{
			if (string.Equals(args.PropertyName, nameof(viewModel.SettingsSecretsNotice), StringComparison.Ordinal))
				notices.Enqueue(viewModel.SettingsSecretsNotice);
		};
		viewModel.PropertyChanged += noticeRecorder;

		try
		{
			await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);
			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HideSecrets);
			await StartApplySettingsWithoutWaitingForBackgroundWorkAsync(window);
			await analyzer.FirstScanStarted.WaitAsync(
				TimeSpan.FromSeconds(5),
				TestContext.Current.CancellationToken);

			await Dispatcher.UIThread.InvokeAsync(
				() => ((IRefreshTreePipelineHost)window).BeforeFullTreeRefresh(
					preserveStatusMetrics: false));
			await analyzer.FirstScanCanceled.WaitAsync(
				TimeSpan.FromSeconds(5),
				TestContext.Current.CancellationToken);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);

			Assert.DoesNotContain(
				notices,
				static notice => notice.Contains("The analysis could not be completed.", StringComparison.Ordinal));
		}
		finally
		{
			viewModel.PropertyChanged -= noticeRecorder;
			analyzer.ReleaseFirstScan();
			analyzer.ReleaseSecondScan();
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	private static async Task<(MainWindow Window, SessionMetricsRecorder SessionMetrics)>
		CreateMeasuredWindowAsync(
            UiTestProject project,
            Func<AvaloniaAppServices, AvaloniaAppServices>? configureServices = null)
    {
        SessionMetricsRecorder? sessionMetrics = null;
        var outputPath = Path.Combine(
            project.AppDataPath,
            "memory-cleanup",
            $"{Guid.NewGuid():N}.json");
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(
            project,
            configureServices: services =>
            {
                sessionMetrics = services.SessionMetricsRecorder;
                return configureServices?.Invoke(services) ?? services;
            },
            sessionMetrics: new SessionMetricsOptions(
                Enabled: true,
                ProjectPath: project.RootPath,
                OutputPath: outputPath));

		return (window, Assert.IsType<SessionMetricsRecorder>(sessionMetrics));
	}

	private static async Task StartApplySettingsWithoutWaitingForBackgroundWorkAsync(MainWindow window)
	{
		var previousApplyTask = window.LatestApplySettingsTask;
		var applyButton = UiTestDriver.GetRequiredApplySettingsButton(window);
		Assert.True(UiTestDriver.GetViewModel(window).HasPendingFilterSettingsChanges);
		// This lifecycle scenario intentionally supersedes a long-running background scan.
		// Route the command without coupling it to the delayed visual busy state.
		applyButton.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
		await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
		await UiTestDriver.WaitForConditionAsync(
			window,
			() => !ReferenceEquals(window.LatestApplySettingsTask, previousApplyTask),
			"the routed Apply command to publish its owned operation");
		await window.LatestApplySettingsTask.WaitAsync(TimeSpan.FromSeconds(30));
	}

    private static async Task WaitForCompletedSecretScanAsync(MainWindow window) =>
        await UiTestDriver.WaitForConditionAsync(
            window,
            () => string.Equals(
                UiTestDriver.GetViewModel(window).SettingsSecretsNotice,
                "Found: 1. Hidden: 1.",
                StringComparison.Ordinal),
            "secret discovery to publish its terminal result");

    private static async Task WaitForApplyCleanupCountAsync(
        MainWindow window,
        SessionMetricsRecorder sessionMetrics,
        int expectedCount) =>
        await UiTestDriver.WaitForConditionAsync(
            window,
            () => CountApplyCleanupRequests(sessionMetrics) == expectedCount,
            $"{expectedCount} Apply-settings cleanup request(s) to be scheduled");

    private static int CountApplyCleanupRequests(SessionMetricsRecorder sessionMetrics) =>
        sessionMetrics
            .BuildCurrentReportForTests("memory-cleanup-test.json")
            .Events
            .Count(static metricEvent =>
                string.Equals(
                    metricEvent.Name,
                    "memory.cleanup.scheduled",
                    StringComparison.Ordinal) &&
                string.Equals(
                    metricEvent.CleanupReason,
                nameof(MemoryCleanupReason.ApplySettingsWorkCompleted),
                StringComparison.Ordinal));

    private sealed class FirstSecretScanCancellationAnalyzer : IFileContentAnalyzer
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource _firstScanStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstScanCanceled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _releaseFirstScan =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondScanStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSecondScan =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IFileContentAnalyzer? _inner;
        private CancellationToken _firstScanToken;
        private CancellationToken _secondScanToken;
        private bool _hasFirstScanToken;
        private bool _hasSecondScanToken;

        public Task FirstScanStarted => _firstScanStarted.Task;
        public Task FirstScanCanceled => _firstScanCanceled.Task;
        public Task SecondScanStarted => _secondScanStarted.Task;

		public void ReleaseFirstScan() => _releaseFirstScan.TrySetResult();
        public void ReleaseSecondScan() => _releaseSecondScan.TrySetResult();

        public IFileContentAnalyzer Attach(IFileContentAnalyzer inner)
        {
            _inner = inner;
            return this;
        }

        public FileContentClassification? ClassifyWithoutReading(string path) =>
            Inner.ClassifyWithoutReading(path);

        public ValueTask<FileContentReadResult> ReadClassifiedAsync(
            string path,
            long maxSizeForFullRead,
            CancellationToken cancellationToken = default) =>
            Inner.ReadClassifiedAsync(path, maxSizeForFullRead, cancellationToken);

        public ValueTask<bool> IsTextFileAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            Inner.IsTextFileAsync(path, cancellationToken);

        public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            Inner.GetTextFileMetricsAsync(path, cancellationToken);

        public ValueTask<FileContentMetricsResult> GetClassifiedMetricsAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            Inner.GetClassifiedMetricsAsync(path, cancellationToken);

        public ValueTask<IFileContentSnapshot> OpenCompleteSnapshotAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            Inner.OpenCompleteSnapshotAsync(path, cancellationToken);

        public async ValueTask<ICompleteTextFileBuffer> OpenCompleteTextBufferAsync(
            string path,
            long maximumBytes,
            CancellationToken cancellationToken = default)
        {
            if (maximumBytes == SecretRedactionOutputPreparer.MaximumScannableFileBytes)
            {
                var scanGeneration = ResolveScanGeneration(cancellationToken);
                if (scanGeneration == 1)
                {
                    _firstScanStarted.TrySetResult();
                    try
                    {
						await _releaseFirstScan.Task.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        _firstScanCanceled.TrySetResult();
                        throw;
                    }
                }
                else if (scanGeneration == 2)
                {
                    _secondScanStarted.TrySetResult();
                    await _releaseSecondScan.Task.WaitAsync(cancellationToken);
                }
            }

            return await Inner.OpenCompleteTextBufferAsync(
                path,
                maximumBytes,
                cancellationToken);
        }

        private int ResolveScanGeneration(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (!_hasFirstScanToken)
                {
                    _firstScanToken = cancellationToken;
                    _hasFirstScanToken = true;
                    return 1;
                }

                if (_firstScanToken == cancellationToken)
                    return 1;

                if (!_hasSecondScanToken)
                {
                    _secondScanToken = cancellationToken;
                    _hasSecondScanToken = true;
                    return 2;
                }

                return _secondScanToken == cancellationToken ? 2 : 0;
            }
        }

        public ValueTask<TextFileContent?> TryReadAsTextAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            Inner.TryReadAsTextAsync(path, cancellationToken);

        public ValueTask<TextFileContent?> TryReadAsTextAsync(
            string path,
            long maxSizeForFullRead,
            CancellationToken cancellationToken = default) =>
            Inner.TryReadAsTextAsync(path, maxSizeForFullRead, cancellationToken);

        private IFileContentAnalyzer Inner =>
            _inner ?? throw new InvalidOperationException("The analyzer is not attached.");
    }

	private sealed class RecordingProjectProfileStore : IProjectProfileStore
	{
		private readonly Dictionary<string, ProjectSelectionProfile> _profiles =
			new(PathComparer.Default);

		public bool EnsureStorageExists() => true;

		public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile) =>
			_profiles.TryGetValue(Path.GetFullPath(localProjectPath), out profile!);

		public bool TrySaveProfile(
			string localProjectPath,
			ProjectSelectionProfile profile,
			DateTimeOffset updatedUtc)
		{
			_profiles[Path.GetFullPath(localProjectPath)] =
				ProjectSelectionProfileBuilder.Clone(profile);
			return true;
		}

		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile) =>
			TrySaveProfile(localProjectPath, profile, DateTimeOffset.UtcNow);

		public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile) =>
			_ = TrySaveProfile(localProjectPath, profile);

		public ProjectProfileClearStatus ClearAllProfiles()
		{
			_profiles.Clear();
			return ProjectProfileClearStatus.Cleared;
		}
	}
}
