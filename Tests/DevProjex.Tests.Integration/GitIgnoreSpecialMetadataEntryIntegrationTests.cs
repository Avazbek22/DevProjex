using System.Net.Sockets;

namespace DevProjex.Tests.Integration;

[Trait("Category", "IgnoreContract")]
public sealed class GitIgnoreSpecialMetadataEntryIntegrationTests
{
	[Theory(Timeout = 10_000)]
	[InlineData(false)]
	[InlineData(true)]
	public void FullRefresh_UnixSpecialGitEntryDoesNotExposeGitModes(bool useSocket)
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip("Unix special filesystem entries are not available on Windows.");
			return;
		}

		using var workspace = new TemporaryDirectory("/tmp");
		workspace.CreateFile("src/App.cs", "class App {}\n");
		var gitMetadataPath = Path.Combine(workspace.Path, ".git");
		Socket? socket = null;
		try
		{
			socket = CreateSpecialEntryOrSkip(gitMetadataPath, useSocket);
			var entries = FileSystemEntryEnumerator.EnumerateEntries(workspace.Path).ToArray();
			var files = FileSystemEntryEnumerator.EnumerateFiles(workspace.Path).ToArray();
			var batch = FileSystemEntryEnumerator.ReadDirectoriesAndGitIgnore(
				workspace.Path,
				relativeDirectory: string.Empty,
				TestContext.Current.CancellationToken,
				captureFiles: true);
			var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
			var facts = new ProjectRootFactsProvider().Get(workspace.Path);
			var availability = services.IgnoreRulesService.GetIgnoreOptionsAvailability(
				workspace.Path,
				selectedRootFolders: []);
			var snapshot = services.Engine.ComputeFullRefreshSnapshot(
				ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(workspace.Path),
				TestContext.Current.CancellationToken);

			Assert.False(facts.HasGitMetadataEntry);
			Assert.False(GitRepositoryBoundaryProbe.ExistsAt(workspace.Path));
			Assert.DoesNotContain(entries, static entry => entry.Name == ".git");
			Assert.DoesNotContain(files, static entry => entry.Name == ".git");
			Assert.DoesNotContain(batch.Files, static entry => entry.Name == ".git");
			Assert.Null(batch.GitMetadataPath);
			Assert.False(availability.IncludeTrackedGitFilesOnly);
			Assert.False(snapshot.GitEvidence.HasRepositoryBoundary);
			Assert.False(snapshot.HadScanFailure);
			Assert.Equal(0, snapshot.ControllerImpactCounts.GitIgnore);
			Assert.DoesNotContain(snapshot.IgnoreOptions, static option =>
				option.Id is IgnoreOptionId.UseGitIgnore or IgnoreOptionId.TrackedGitFilesOnly);
		}
		finally
		{
			socket?.Dispose();
			DeleteSpecialEntry(gitMetadataPath);
		}
	}

	[Theory(Timeout = 10_000)]
	[InlineData(false)]
	[InlineData(true)]
	public void Resolve_UnixSpecialGitEntryFallsThroughToPhysicalAncestor(bool useSocket)
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip("Unix special filesystem entries are not available on Windows.");
			return;
		}

		using var workspace = new TemporaryDirectory("/tmp");
		var repositoryRoot = workspace.CreateDirectory("repository");
		workspace.CreateDirectory("repository/.git");
		var scopeRoot = workspace.CreateDirectory("repository/src");
		var specialMetadataPath = Path.Combine(scopeRoot, ".git");
		Socket? socket = null;
		try
		{
			socket = CreateSpecialEntryOrSkip(specialMetadataPath, useSocket);
			var resolutionCount = 0;
			string? resolvedRepositoryRoot = null;
			string? resolvedMetadataPath = null;
			var expected = new GitPathComparisonSemantics(IgnoreCase: false, NormalizeUnicode: false);
			var resolver = new GitConfigPathComparisonSemanticsResolver(
				(rootPath, metadataPath) =>
				{
					resolutionCount++;
					resolvedRepositoryRoot = rootPath;
					resolvedMetadataPath = metadataPath;
					return expected;
				},
				static () => new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
				TimeSpan.FromMinutes(1));

			Assert.Equal(expected, resolver.Resolve(scopeRoot));
			Assert.Equal(1, resolutionCount);
			Assert.Equal(repositoryRoot, resolvedRepositoryRoot);
			Assert.Equal(Path.Combine(repositoryRoot, ".git"), resolvedMetadataPath);
		}
		finally
		{
			socket?.Dispose();
			DeleteSpecialEntry(specialMetadataPath);
		}
	}

	private static Socket? CreateSpecialEntryOrSkip(string path, bool useSocket)
	{
		if (!useSocket)
		{
			CreateFifoOrSkip(path);
			return null;
		}

		var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
		try
		{
			socket.Bind(new UnixDomainSocketEndPoint(path));
			return socket;
		}
		catch (Exception exception) when (exception is SocketException or PlatformNotSupportedException)
		{
			socket.Dispose();
			Assert.Skip($"Unix sockets are unavailable in this environment: {exception.GetType().Name}");
			return null;
		}
	}

	private static void CreateFifoOrSkip(string path)
	{
		var startInfo = new ProcessStartInfo("mkfifo")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		startInfo.ArgumentList.Add(path);
		Process? process;
		try
		{
			process = Process.Start(startInfo);
		}
		catch (System.ComponentModel.Win32Exception)
		{
			Assert.Skip("mkfifo is not available in this environment.");
			return;
		}

		using (process)
		{
			if (process is null)
			{
				Assert.Skip("mkfifo could not be started.");
				return;
			}

			if (!process.WaitForExit(5_000))
			{
				process.Kill(entireProcessTree: true);
				Assert.Skip("mkfifo did not complete within five seconds.");
				return;
			}

			if (process.ExitCode != 0)
			{
				var error = process.StandardError.ReadToEnd();
				Assert.Skip($"mkfifo is unavailable: {error}");
			}
		}
	}

	private static void DeleteSpecialEntry(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
		{
		}
	}
}
