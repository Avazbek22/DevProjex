using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace DevProjex.Tests.Unit;

public sealed class ProjectProfileShutdownProcessTests
{
	[Fact]
	public async Task BatchFlush_WhenAnotherProcessHoldsLock_StaysWithinBudgetAndPreservesPendingData()
	{
		using var temporary = new TemporaryDirectory();
		var firstProject = temporary.CreateFolder("already-saved");
		var pendingProject = temporary.CreateFolder("pending");
		var store = new ProjectProfileStore(() => temporary.Path);
		store.SaveProfile(
			firstProject,
			new ProjectSelectionProfile([], [".cs"], []));
		var persistedBytesBeforeContention = File.ReadAllBytes(store.GetPath());
		var queue = new PendingProjectProfileWriteQueue(new FailFirstProfileStore(store));
		queue.Persist(
			pendingProject,
			new ProjectSelectionProfile([], [".json"], []),
			DateTimeOffset.UtcNow);
		Assert.Equal(1, queue.Count);

		var readyPath = Path.Combine(temporary.Path, "lock.ready");
		var releasePath = Path.Combine(temporary.Path, "lock.release");
		using var process = Process.Start(ProfilePersistenceLockProcessHost.CreateStartInfo(
			store.GetPath() + ".lock",
			readyPath,
			releasePath));
		Assert.NotNull(process);
		await WaitForFileAsync(readyPath, process!, TestContext.Current.CancellationToken);

		try
		{
			var stopwatch = Stopwatch.StartNew();
			var result = queue.Flush(TimeSpan.FromMilliseconds(300));
			stopwatch.Stop();

			Assert.True(result.GateAcquired);
			Assert.Equal(1, result.Attempted);
			Assert.Equal(0, result.Saved);
			Assert.Equal(1, result.Remaining);
			Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(1));
			Assert.Equal(persistedBytesBeforeContention, File.ReadAllBytes(store.GetPath()));
		}
		finally
		{
			await File.WriteAllTextAsync(
				releasePath,
				string.Empty,
				TestContext.Current.CancellationToken);
			await process!.WaitForExitAsync(TestContext.Current.CancellationToken);
		}

		Assert.True(store.TryLoadProfile(firstProject, out var alreadySaved));
		Assert.Equal([".cs"], alreadySaved.SelectedExtensions);
		Assert.False(store.TryLoadProfile(pendingProject, out _));
		var retry = queue.Flush(TimeSpan.FromSeconds(1));
		Assert.True(retry.Succeeded);
		Assert.True(store.TryLoadProfile(pendingProject, out var persisted));
		Assert.Equal([".json"], persisted.SelectedExtensions);
	}

	private static async Task WaitForFileAsync(
		string path,
		Process process,
		CancellationToken cancellationToken)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(10));
		while (!File.Exists(path))
		{
			if (process.HasExited)
			{
				throw new InvalidOperationException(
					$"Profile lock helper exited with code {process.ExitCode} before acquiring the lock.");
			}

			await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
		}
	}

	private sealed class FailFirstProfileStore(ProjectProfileStore inner) : IProjectProfileStore
	{
		private int _failNextSingleWrite = 1;

		public bool EnsureStorageExists() => inner.EnsureStorageExists();

		public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile) =>
			inner.TryLoadProfile(localProjectPath, out profile);

		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile) =>
			TrySaveProfile(localProjectPath, profile, DateTimeOffset.UtcNow);

		public bool TrySaveProfile(
			string localProjectPath,
			ProjectSelectionProfile profile,
			DateTimeOffset updatedUtc)
		{
			if (Interlocked.Exchange(ref _failNextSingleWrite, 0) == 1)
				return false;

			return inner.TrySaveProfile(localProjectPath, profile, updatedUtc);
		}

		public ProjectProfileBatchSaveResult TrySaveProfilesWithResult(
			IReadOnlyList<ProjectProfileSaveRequest> requests,
			TimeSpan lockTimeout) =>
			inner.TrySaveProfilesWithResult(requests, lockTimeout);

		public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile) =>
			_ = TrySaveProfile(localProjectPath, profile);

		public void ClearAllProfiles() => inner.ClearAllProfiles();
	}
}

internal static class ProfilePersistenceLockProcessHost
{
	private const string LockPathVariable = "DEVPROJEX_TEST_PROFILE_LOCK_PATH";
	private const string ReadyPathVariable = "DEVPROJEX_TEST_PROFILE_LOCK_READY";
	private const string ReleasePathVariable = "DEVPROJEX_TEST_PROFILE_LOCK_RELEASE";

	[ModuleInitializer]
	internal static void Run()
	{
		var lockPath = Environment.GetEnvironmentVariable(LockPathVariable);
		var readyPath = Environment.GetEnvironmentVariable(ReadyPathVariable);
		var releasePath = Environment.GetEnvironmentVariable(ReleasePathVariable);
		if (string.IsNullOrWhiteSpace(lockPath) ||
		    string.IsNullOrWhiteSpace(readyPath) ||
		    string.IsNullOrWhiteSpace(releasePath))
		{
			return;
		}

		using (new FileStream(
			       lockPath,
			       FileMode.OpenOrCreate,
			       FileAccess.ReadWrite,
			       FileShare.None))
		{
			File.WriteAllText(readyPath, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
			while (!File.Exists(releasePath))
				Thread.Sleep(20);
		}

		Environment.Exit(0);
	}

	internal static ProcessStartInfo CreateStartInfo(
		string lockPath,
		string readyPath,
		string releasePath)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet",
			UseShellExecute = false,
			CreateNoWindow = true
		};
		startInfo.ArgumentList.Add(typeof(ProfilePersistenceLockProcessHost).Assembly.Location);
		startInfo.Environment[LockPathVariable] = lockPath;
		startInfo.Environment[ReadyPathVariable] = readyPath;
		startInfo.Environment[ReleasePathVariable] = releasePath;
		return startInfo;
	}
}
