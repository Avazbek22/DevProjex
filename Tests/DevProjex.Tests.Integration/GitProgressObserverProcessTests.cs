using System.Runtime.CompilerServices;

namespace DevProjex.Tests.Integration;

public sealed class GitProgressObserverProcessTests
{
	[Fact]
	public async Task CloneAsync_ThrowingProgressStaysOnAwaitedPathWhileLargeStderrIsDrained()
	{
		using var temp = new TemporaryDirectory();
		var executablePath = ResolveTestAppHostPath();
		var service = new GitRepositoryService(executablePath);
		var faultedProgress = new ThrowingProgress(new InvalidOperationException("observer failed"));

		var failedResult = await service
			.CloneAsync(
				GitProgressObserverProcessHost.ProbeUrl,
				Path.Combine(temp.Path, "observer-failure-clone"),
				faultedProgress,
				TestContext.Current.CancellationToken)
			.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

		Assert.False(failedResult.Success);
		Assert.Equal(1, faultedProgress.ReportCount);
		Assert.InRange(
			faultedProgress.MaximumValueLength,
			1,
			GitRepositoryService.MaximumProgressFrameCharacters);
		Assert.DoesNotContain('\u001b', faultedProgress.LastValue);
		Assert.Contains("\\u001B", faultedProgress.LastValue, StringComparison.Ordinal);
	}

	private static string ResolveTestAppHostPath()
	{
		var assemblyPath = typeof(GitProgressObserverProcessTests).Assembly.Location;
		var fileName = Path.GetFileNameWithoutExtension(assemblyPath) +
		               (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
		var appHostPath = Path.Combine(Path.GetDirectoryName(assemblyPath)!, fileName);
		Assert.True(File.Exists(appHostPath), $"Test apphost was not found: {appHostPath}");
		return appHostPath;
	}

	private sealed class ThrowingProgress(Exception failure) : IProgress<string>
	{
		private int _reportCount;
		private int _maximumValueLength;

		public int ReportCount => Volatile.Read(ref _reportCount);
		public int MaximumValueLength => Volatile.Read(ref _maximumValueLength);
		public string LastValue { get; private set; } = string.Empty;

		public void Report(string value)
		{
			Interlocked.Increment(ref _reportCount);
			_maximumValueLength = Math.Max(_maximumValueLength, value.Length);
			LastValue = value;
			throw failure;
		}
	}
}

internal static class GitProgressObserverProcessHost
{
	internal const string ProbeUrl = "https://example.test/devprojex-progress-observer.git";
	private const string MarkerFileName = "progress-observer-probe";
	private const int StderrPayloadCharacters = 2 * 1024 * 1024;
	private const int StderrWriteChunkCharacters = 4 * 1024;

	[ModuleInitializer]
	internal static void Run()
	{
		var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
		if (arguments.Contains(ProbeUrl, StringComparer.Ordinal) &&
		    arguments.Contains("clone", StringComparer.Ordinal))
		{
			RunCloneProbe(arguments[^1]);
			Environment.Exit(0);
		}

		var markerPath = Path.Combine(Environment.CurrentDirectory, ".git", MarkerFileName);
		if (!File.Exists(markerPath))
			return;

		if (arguments.Contains("symbolic-ref", StringComparer.Ordinal))
			Console.Out.WriteLine("refs/remotes/origin/main");
		Environment.Exit(0);
	}

	private static void RunCloneProbe(string targetPath)
	{
		var metadataPath = Path.Combine(targetPath, ".git");
		Directory.CreateDirectory(metadataPath);
		File.WriteAllText(Path.Combine(metadataPath, MarkerFileName), string.Empty);

		Console.Error.Write("Receiving objects: 50% \u001b");
		var payload = new string('x', StderrWriteChunkCharacters);
		for (var written = 0; written < StderrPayloadCharacters; written += payload.Length)
			Console.Error.Write(payload);
		Console.Error.Write('\r');
		Console.Error.Flush();
	}
}
