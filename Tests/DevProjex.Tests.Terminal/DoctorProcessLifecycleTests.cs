using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

public sealed class DoctorProcessLifecycleTests
{
	[Fact]
	public async Task GitProbeCancellationReapsProcessTreeBeforeReturning()
	{
		using var workspace = new TemporaryDirectory();
		var lockPath = Path.Combine(workspace.Path, "doctor-git.lock");
		var readyPath = Path.Combine(workspace.Path, "doctor-git.ready");
		var startInfo = new ProcessStartInfo
		{
			FileName = PublishedApplicationLocator.FindProgressCheckpointHostExecutable(),
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		startInfo.ArgumentList.Add("--hold-process-tree");
		startInfo.ArgumentList.Add(lockPath);
		startInfo.ArgumentList.Add(readyPath);

		using var cancellation = new CancellationTokenSource();
		var probe = DoctorCommandHandler.TryReadGitVersionAsync(
			startInfo,
			TimeSpan.FromSeconds(30),
			cancellation.Token);
		await WaitForFileAsync(readyPath, TestContext.Current.CancellationToken);

		Assert.Throws<IOException>(() => OpenExclusive(lockPath).Dispose());
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await probe);

		using var releasedHandle = OpenExclusive(lockPath);
	}

	private static FileStream OpenExclusive(string path) =>
		new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

	private static async Task WaitForFileAsync(string path, CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
		while (!File.Exists(path))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (DateTime.UtcNow >= deadline)
				throw new TimeoutException($"Test process did not create '{path}'.");
			await Task.Delay(25, cancellationToken);
		}
	}
}
