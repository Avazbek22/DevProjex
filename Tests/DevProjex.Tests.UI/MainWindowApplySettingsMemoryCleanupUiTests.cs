using DevProjex.Application.Secrets;
using DevProjex.Avalonia.Services;
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
            await analyzer.FirstScanStarted.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(
                window,
                IgnoreOptionId.HideSecrets);
            await analyzer.FirstScanCanceled.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.Equal(0, CountApplyCleanupRequests(sessionMetrics));

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(
                window,
                IgnoreOptionId.HideSecrets);
            await WaitForCompletedSecretScanAsync(window);
            await WaitForApplyCleanupCountAsync(window, sessionMetrics, expectedCount: 1);
            Assert.Equal(1, CountApplyCleanupRequests(sessionMetrics));
        }
        finally
        {
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
        private readonly TaskCompletionSource _firstScanStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstScanCanceled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IFileContentAnalyzer? _inner;
        private int _secretScanReads;

        public Task FirstScanStarted => _firstScanStarted.Task;
        public Task FirstScanCanceled => _firstScanCanceled.Task;

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
            if (maximumBytes == SecretRedactionOutputPreparer.MaximumScannableFileBytes &&
                Interlocked.Increment(ref _secretScanReads) == 1)
            {
                _firstScanStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    _firstScanCanceled.TrySetResult();
                    throw;
                }
            }

            return await Inner.OpenCompleteTextBufferAsync(
                path,
                maximumBytes,
                cancellationToken);
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

		public void ClearAllProfiles() => _profiles.Clear();
	}
}
