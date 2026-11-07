using System.Diagnostics;
using DevProjex.Infrastructure.FileSystem;
using DevProjex.Infrastructure.Git;
using DevProjex.Kernel.Models;

namespace DevProjex.Tests.Integration;

public sealed partial class GitWorkingChangesLineEndingIntegrationTests
{
	[Fact]
	public async Task ChangesScope_InACrlfCheckout_ReportsOnlyTheEditedFile()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var clone = CreateCrlfCheckoutWithStaleIndex(temp, out var userConfiguration);
		File.WriteAllText(userConfiguration, "[core]\n\tautocrlf = true\n");
		var provider = new GitScopePathProvider(
			pathComparisonSemanticsResolver: null,
			(root, token) => GitLineEndingSettings.ReadAsync(root, [userConfiguration], readSystemConfiguration: false, token));

		var result = await provider.ResolveAsync(
			clone,
			GitFilteringMode.Changes,
			diffRange: null,
			TestContext.Current.CancellationToken);

		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.Equal(["first.py"], result.IncludedPaths.Select(Path.GetFileName));
	}

	[Fact]
	public async Task ChangesScope_WithoutTheUsersLineEndingSettings_SeesEveryRecheckedFileAsModified()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var clone = CreateCrlfCheckoutWithStaleIndex(temp, out _);
		var provider = new GitScopePathProvider(
			pathComparisonSemanticsResolver: null,
			static (_, _) => Task.FromResult<IReadOnlyList<string>>([]));

		var result = await provider.ResolveAsync(
			clone,
			GitFilteringMode.Changes,
			diffRange: null,
			TestContext.Current.CancellationToken);

		// The fixture really reproduces the defect: an isolated comparison of a CRLF checkout whose
		// index must be rechecked reports the untouched files too.
		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.Equal(
			new[] { "first.py", "second.py", "third.py" },
			result.IncludedPaths.Select(Path.GetFileName).Order(StringComparer.Ordinal));
	}

	[Fact]
	public async Task LineEndingSettings_FollowGitPrecedenceAndAcceptOnlyGitValues()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repository = temp.CreateDirectory("repository");
		RunGit(repository, "init", "--quiet");
		var lower = temp.CreateFile("xdg-config", "[core]\n\tautocrlf = input\n\teol = crlf\n");
		var higher = temp.CreateFile("gitconfig", "[core]\n\tautocrlf = yes\n");
		var token = TestContext.Current.CancellationToken;

		var fromUserFiles = await GitLineEndingSettings.ReadAsync(repository, [lower, higher], false, token);
		RunGit(repository, "config", "core.autocrlf", "false");
		var repositoryWins = await GitLineEndingSettings.ReadAsync(repository, [lower, higher], false, token);
		var bareKey = temp.CreateFile("bare", "[core]\n\tautocrlf\n\teol = unknown\n");
		RunGit(repository, "config", "--unset", "core.autocrlf");
		var bare = await GitLineEndingSettings.ReadAsync(repository, [bareKey], false, token);
		var missing = await GitLineEndingSettings.ReadAsync(
			repository,
			[Path.Combine(temp.Path, "absent")],
			false,
			token);

		Assert.Equal(["core.autocrlf=true", "core.eol=crlf"], fromUserFiles);
		Assert.Equal(["core.autocrlf=false", "core.eol=crlf"], repositoryWins);
		Assert.Equal(["core.autocrlf=true"], bare);
		Assert.Empty(missing);
	}

	// A clone checked out with core.autocrlf=true holds CRLF files over LF blobs. Moving every
	// file's write time makes Git recheck content instead of trusting the index stat data, which
	// is what happens after a fresh clone, a copy, or a restore.
	private static string CreateCrlfCheckoutWithStaleIndex(TemporaryDirectory temp, out string userConfiguration)
	{
		var source = temp.CreateDirectory("source");
		RunGit(source, "init", "--quiet");
		RunGit(source, "config", "core.autocrlf", "false");
		RunGit(source, "config", "user.email", "tests@devprojex.local");
		RunGit(source, "config", "user.name", "DevProjex Tests");
		foreach (var name in new[] { "first.py", "second.py", "third.py" })
			File.WriteAllText(Path.Combine(source, name), $"value = '{name}'\n");
		RunGit(source, "add", "--", "first.py", "second.py", "third.py");
		RunGit(source, "commit", "--quiet", "-m", "seed");
		var clone = Path.Combine(temp.Path, "clone");
		RunGit(temp.Path, "-c", "core.autocrlf=true", "clone", "--quiet", "--no-local", source, clone);
		Assert.Contains("\r\n", File.ReadAllText(Path.Combine(clone, "second.py")), StringComparison.Ordinal);
		var stale = DateTime.UtcNow.AddMinutes(10);
		foreach (var name in new[] { "first.py", "second.py", "third.py" })
			File.SetLastWriteTimeUtc(Path.Combine(clone, name), stale);
		File.AppendAllText(Path.Combine(clone, "first.py"), "edited = True\r\n");
		userConfiguration = Path.Combine(temp.Path, "user-gitconfig");
		return clone;
	}

	private static void EnsureGitAvailable()
	{
		try
		{
			RunGit(Environment.CurrentDirectory, "--version");
		}
		catch (System.ComponentModel.Win32Exception)
		{
			GitTestRequirement.ReportUnavailable("Git is not available in this test environment.");
		}
	}

	private static string RunGit(string workingDirectory, params string[] arguments)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
		process.StandardInput.Close();
		var output = process.StandardOutput.ReadToEnd();
		var error = process.StandardError.ReadToEnd();
		if (!process.WaitForExit(20_000))
		{
			process.Kill(entireProcessTree: true);
			throw GitTestRequirement.Timeout("Git command did not complete within 20 seconds.");
		}
		Assert.True(process.ExitCode == 0, $"git failed ({process.ExitCode}): {error}{output}");
		return output;
	}
}
