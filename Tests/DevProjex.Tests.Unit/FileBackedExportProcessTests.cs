using System.Diagnostics;

namespace DevProjex.Tests.Unit;

public sealed class FileBackedExportProcessTests
{
	[Fact]
	public async Task LargeFileBackedExport_CancelsQuicklyWithoutProportionalWorkingSetGrowth()
	{
		using var temporary = new TemporaryDirectory();
		var sourcePath = Path.Combine(temporary.Path, "large.preview");
		var destinationPath = Path.Combine(temporary.Path, "export.txt");
		var readyPath = Path.Combine(temporary.Path, "ready.txt");
		var cancelPath = Path.Combine(temporary.Path, "cancel");
		var outcomePath = Path.Combine(temporary.Path, "outcome.txt");
		const long sourceLength = 300L * 1024 * 1024;
		await using (var source = new FileStream(sourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			source.SetLength(sourceLength);

		using var process = Process.Start(CreateStartInfo(
			sourcePath,
			destinationPath,
			readyPath,
			cancelPath,
			outcomePath));
		Assert.NotNull(process);
		try
		{
			await WaitForFileAsync(readyPath, process!, TestContext.Current.CancellationToken);
			var baselineWorkingSet = long.Parse(
				await File.ReadAllTextAsync(readyPath, TestContext.Current.CancellationToken),
				CultureInfo.InvariantCulture);
			var peakWorkingSet = baselineWorkingSet;
			await Task.Delay(150, TestContext.Current.CancellationToken);
			var cancellationStarted = Stopwatch.StartNew();
			await File.WriteAllTextAsync(cancelPath, string.Empty, TestContext.Current.CancellationToken);
			while (!process!.HasExited)
			{
				try
				{
					process.Refresh();
					peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
				}
				catch (InvalidOperationException) when (process.HasExited)
				{
					break;
				}
				await Task.Delay(10, TestContext.Current.CancellationToken);
			}
			cancellationStarted.Stop();

			Assert.Equal(0, process.ExitCode);
			Assert.Equal("canceled", await File.ReadAllTextAsync(
				outcomePath,
				TestContext.Current.CancellationToken));
			Assert.InRange(cancellationStarted.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
			Assert.InRange(peakWorkingSet - baselineWorkingSet, 0, 64L * 1024 * 1024);
			Assert.InRange(new FileInfo(destinationPath).Length, 1, sourceLength - 1);
		}
		finally
		{
			if (!File.Exists(cancelPath))
				await File.WriteAllTextAsync(
					cancelPath,
					string.Empty,
					TestContext.Current.CancellationToken);
			if (!process!.HasExited)
			{
				using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
				try
				{
					await process.WaitForExitAsync(exitTimeout.Token);
				}
				catch (OperationCanceledException)
				{
					process.Kill(entireProcessTree: true);
					await process.WaitForExitAsync(TestContext.Current.CancellationToken);
				}
			}
		}
	}

	private static ProcessStartInfo CreateStartInfo(
		string sourcePath,
		string destinationPath,
		string readyPath,
		string cancelPath,
		string outcomePath)
	{
		var executableName = OperatingSystem.IsWindows()
			? "DevProjex.Tests.Terminal.ProgressHost.exe"
			: "DevProjex.Tests.Terminal.ProgressHost";
		var executablePath = Path.Combine(
			FindRepositoryRoot(),
			"Tests",
			"DevProjex.Tests.Terminal.ProgressHost",
			"bin",
			ResolveConfiguration(),
			"net10.0",
			executableName);
		if (!File.Exists(executablePath))
			throw new FileNotFoundException("Build the process test host before running this test.", executablePath);

		var startInfo = new ProcessStartInfo
		{
			FileName = executablePath,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		startInfo.ArgumentList.Add("--file-backed-export");
		startInfo.ArgumentList.Add(sourcePath);
		startInfo.ArgumentList.Add(destinationPath);
		startInfo.ArgumentList.Add(readyPath);
		startInfo.ArgumentList.Add(cancelPath);
		startInfo.ArgumentList.Add(outcomePath);
		return startInfo;
	}

	private static string ResolveConfiguration()
	{
		var segments = AppContext.BaseDirectory
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
			.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return segments.Contains("Release", StringComparer.OrdinalIgnoreCase)
			? "Release"
			: "Debug";
	}

	private static string FindRepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "DevProjex.sln")))
				return directory.FullName;
			directory = directory.Parent;
		}
		throw new DirectoryNotFoundException("The repository root could not be resolved.");
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
				throw new InvalidOperationException($"Export helper exited with code {process.ExitCode}.");
			await Task.Delay(20, timeout.Token);
		}
	}
}
