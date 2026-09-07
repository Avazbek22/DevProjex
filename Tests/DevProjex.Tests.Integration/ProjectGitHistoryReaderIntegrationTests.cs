using DevProjex.Application.Ranking;

namespace DevProjex.Tests.Integration;

public sealed class ProjectGitHistoryReaderIntegrationTests
{
	[Fact]
	public async Task ReadAsync_ReturnsCommitCountsAndWindowRelativeRecency()
	{
		using var fixture = new HistoryRepositoryFixture();
		fixture.Write("src/first.cs", "one\n");
		fixture.Write("src/second.cs", "one\n");
		fixture.Commit("initial");
		fixture.Write("src/first.cs", "two\n");
		fixture.Commit("update first");
		fixture.Write("src/second.cs", "two\n");
		fixture.Commit("update second");

		var first = Path.Combine(fixture.RepositoryPath, "src", "first.cs");
		var second = Path.Combine(fixture.RepositoryPath, "src", "second.cs");
		var history = await new ProjectGitHistoryReader().ReadAsync(
			fixture.RepositoryPath,
			[first, second],
			TestContext.Current.CancellationToken);

		Assert.True(history.IsAvailable, history.Detail);
		Assert.Equal(3, history.CommitCount);
		Assert.Equal(new ProjectGitFileActivity(2, 2), history.Files[first]);
		Assert.Equal(new ProjectGitFileActivity(2, 1), history.Files[second]);
	}

	[Fact]
	public async Task ReadAsync_ReportsNoRepositoryWithoutStartingGit()
	{
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("ordinary-folder");
		var file = Path.Combine(sourceRoot, "source.cs");
		await File.WriteAllTextAsync(file, "class Source;", TestContext.Current.CancellationToken);

		var history = await new ProjectGitHistoryReader().ReadAsync(
			sourceRoot,
			[file],
			TestContext.Current.CancellationToken);

		Assert.False(history.IsAvailable);
		Assert.Equal(ProjectGitHistoryUnavailableReason.NotRepository, history.UnavailableReason);
		Assert.Empty(history.Files);
	}

	[Fact]
	public async Task ReadAsync_UsesLocalReadAndDoesNotInvokeHostileRepositoryPrograms()
	{
		using var fixture = new HistoryRepositoryFixture();
		fixture.Write("tracked.txt", "one\n");
		fixture.Commit("initial");
		var markerPath = Path.Combine(fixture.RepositoryPath, "hostile.marker");
		var markerCommand = fixture.CreateMarkerCommand(markerPath);
		fixture.Write(".gitattributes", "tracked.txt filter=hostile\n");
		fixture.RunGit("config", "filter.hostile.clean", markerCommand);
		fixture.Write("tracked.txt", "positive control\n");
		fixture.RunGit("add", "tracked.txt");
		Assert.True(File.Exists(markerPath),
			"Positive control failed: unprotected git did not execute the hostile marker program.");
		File.Delete(markerPath);
		fixture.RunGit("config", "core.pager", markerCommand);
		fixture.RunGit("config", "log.showSignature", "true");
		fixture.RunGit("config", "gpg.program", markerCommand);

		var tracked = Path.Combine(fixture.RepositoryPath, "tracked.txt");
		var history = await new ProjectGitHistoryReader().ReadAsync(
			fixture.RepositoryPath,
			[tracked],
			TestContext.Current.CancellationToken);

		Assert.True(history.IsAvailable, history.Detail);
		Assert.False(File.Exists(markerPath), "Protected history read executed a repository program.");
	}

	private sealed class HistoryRepositoryFixture : IDisposable
	{
		private readonly TemporaryDirectory _temporary = new();

		public HistoryRepositoryFixture()
		{
			RepositoryPath = _temporary.CreateDirectory("repository");
			RunGit("init", "--initial-branch=main");
			RunGit("config", "user.name", "DevProjex History Tests");
			RunGit("config", "user.email", "history@devprojex.local");
		}

		public string RepositoryPath { get; }

		public void Write(string relativePath, string content)
		{
			var path = Path.Combine(RepositoryPath, relativePath);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, content, new UTF8Encoding(false));
		}

		public void Commit(string message)
		{
			RunGit("add", "--all");
			RunGit("commit", "-m", message);
		}

		public string CreateMarkerCommand(string markerPath)
		{
			var directory = _temporary.CreateDirectory("programs");
			if (OperatingSystem.IsWindows())
			{
				var path = Path.Combine(directory, "pager.ps1");
				File.WriteAllText(
					path,
					$"Set-Content -LiteralPath '{markerPath.Replace("'", "''", StringComparison.Ordinal)}' -Value invoked\r\n$input | Out-Null\r\nexit 0\r\n",
					new UTF8Encoding(false));
				return $"powershell.exe -NoLogo -NoProfile -NonInteractive -File '{path.Replace('\\', '/')}'";
			}

			var script = Path.Combine(directory, "pager.sh");
			File.WriteAllText(
				script,
				$"#!/bin/sh\nprintf invoked > '{markerPath.Replace("'", "'\\''", StringComparison.Ordinal)}'\ncat >/dev/null\n",
				new UTF8Encoding(false));
			File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			return script;
		}

		public void RunGit(params string[] arguments)
		{
			var startInfo = new ProcessStartInfo(GitRuntime.GitExecutable)
			{
				WorkingDirectory = RepositoryPath,
				UseShellExecute = false,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			foreach (var argument in arguments)
				startInfo.ArgumentList.Add(argument);
			using var process = Process.Start(startInfo);
			Assert.NotNull(process);
			process.StandardInput.Close();
			var output = process.StandardOutput.ReadToEnd();
			var error = process.StandardError.ReadToEnd();
			Assert.True(process.WaitForExit(30_000), "Fixture Git command timed out.");
			Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}{output}");
		}

		public void Dispose() => _temporary.Dispose();
	}
}
