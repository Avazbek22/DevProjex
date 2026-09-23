using System.Diagnostics;
using DevProjex.Infrastructure.Git;

namespace DevProjex.Tests.Unit;

public sealed class GitExecutableLocatorTests(ITestOutputHelper output)
{
	[Fact]
	public void ExecutableDirectoryAliasIntoRepositoryIsRejected()
	{
		using var workspace = new TemporaryDirectory();
		var repository = workspace.CreateFolder("repository");
		var repositoryBin = workspace.CreateFolder("repository/bin");
		var currentDirectory = workspace.CreateFolder("current");
		var executableName = OperatingSystem.IsWindows() ? "git.exe" : "git";
		workspace.CreateFile($"repository/bin/{executableName}", "placeholder");
		var alias = Path.Combine(workspace.Path, "path-alias");
		if (!TryCreateDirectoryAlias(alias, repositoryBin))
			Assert.Skip("Directory links are unavailable in this environment.");
		try
		{
			var aliasedExecutable = Path.Combine(alias, executableName);
			Assert.False(GitExecutableLocator.IsSafeForRepository(aliasedExecutable, repository));
			var resolved = GitExecutableLocator.TryResolveFromPath(
				executableName,
				alias,
				currentDirectory);
			Assert.True(PathComparer.Default.Equals(
				Path.Combine(repository, "bin", executableName),
				resolved));
			Assert.False(GitExecutableLocator.IsSafeForRepository(resolved!, repository));
		}
		finally
		{
			Directory.Delete(alias);
		}
	}

	[Fact]
	public void ExecutableFileLinkIntoRepositoryIsRejected()
	{
		using var workspace = new TemporaryDirectory();
		var repository = workspace.CreateFolder("repository");
		var externalBin = workspace.CreateFolder("external-bin");
		var currentDirectory = workspace.CreateFolder("current");
		var executableName = OperatingSystem.IsWindows() ? "git.exe" : "git";
		var target = workspace.CreateFile($"repository/{executableName}", "placeholder");
		var link = Path.Combine(externalBin, executableName);
		try
		{
			File.CreateSymbolicLink(link, target);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			Assert.Skip("File links are unavailable in this environment.");
		}

		Assert.False(GitExecutableLocator.IsSafeForRepository(link, repository));
		Assert.True(PathComparer.Default.Equals(
			Path.Combine(repository, executableName),
			GitExecutableLocator.TryResolveFromPath(executableName, externalBin, currentDirectory)));
	}

	[Fact]
	public void ExternalExecutableDirectoryAliasResolvesToTrustedPhysicalFile()
	{
		using var workspace = new TemporaryDirectory();
		var repository = workspace.CreateFolder("repository");
		var trustedBin = workspace.CreateFolder("trusted/bin");
		var currentDirectory = workspace.CreateFolder("current");
		var executableName = OperatingSystem.IsWindows() ? "git.exe" : "git";
		workspace.CreateFile($"trusted/bin/{executableName}", "placeholder");
		var alias = Path.Combine(workspace.Path, "trusted-alias");
		if (!TryCreateDirectoryAlias(alias, trustedBin))
			Assert.Skip("Directory links are unavailable in this environment.");
		try
		{
			var aliasedExecutable = Path.Combine(alias, executableName);
			Assert.True(GitExecutableLocator.IsSafeForRepository(aliasedExecutable, repository));
			Assert.True(PathComparer.Default.Equals(
				Path.Combine(workspace.Path, "trusted", "bin", executableName),
				GitExecutableLocator.TryResolveFromPath(executableName, alias, currentDirectory)));
		}
		finally
		{
			Directory.Delete(alias);
		}
	}

	[Fact]
	public void AliasedRepositoryAndCurrentDirectoryStillRejectContainedExecutable()
	{
		using var workspace = new TemporaryDirectory();
		var repository = workspace.CreateFolder("repository");
		var repositoryBin = workspace.CreateFolder("repository/bin");
		var executableName = OperatingSystem.IsWindows() ? "git.exe" : "git";
		var executable = workspace.CreateFile($"repository/bin/{executableName}", "placeholder");
		var repositoryAlias = Path.Combine(workspace.Path, "repository-alias");
		if (!TryCreateDirectoryAlias(repositoryAlias, repository))
			Assert.Skip("Directory links are unavailable in this environment.");
		try
		{
			Assert.False(GitExecutableLocator.IsSafeForRepository(executable, repositoryAlias));
			Assert.Null(GitExecutableLocator.TryResolveFromPath(
				executableName,
				repositoryBin,
				repositoryAlias));
		}
		finally
		{
			Directory.Delete(repositoryAlias);
		}
	}

	[Fact]
	[Trait("Category", "LocalPerformance")]
	public void NormalPathGitResolutionRecordsInitialAndWarmCost()
	{
		var executableName = OperatingSystem.IsWindows() ? "git.exe" : "git";
		var elapsedMilliseconds = new double[6];
		var allocatedBytes = new long[6];
		string? resolved = null;
		for (var index = 0; index < elapsedMilliseconds.Length; index++)
		{
			var beforeAllocation = GC.GetAllocatedBytesForCurrentThread();
			var started = Stopwatch.GetTimestamp();
			resolved = GitExecutableLocator.TryResolve(executableName);
			elapsedMilliseconds[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
			allocatedBytes[index] = GC.GetAllocatedBytesForCurrentThread() - beforeAllocation;
		}
		if (resolved is null)
			Assert.Skip("A trusted Git executable is unavailable on PATH.");
		Assert.True(Path.IsPathFullyQualified(resolved));
		output.WriteLine($"git_locator_first_ms={elapsedMilliseconds[0].ToString("F3", CultureInfo.InvariantCulture)}; warm_ms={string.Join(',', elapsedMilliseconds[1..].Select(static value => value.ToString("F3", CultureInfo.InvariantCulture)))}; first_alloc_bytes={allocatedBytes[0]}; warm_alloc_bytes={string.Join(',', allocatedBytes[1..])}");
	}

	private static bool TryCreateDirectoryAlias(string alias, string target)
	{
		try
		{
			Directory.CreateSymbolicLink(alias, target);
			return Directory.Exists(alias) &&
			       File.GetAttributes(alias).HasFlag(FileAttributes.ReparsePoint);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			if (!OperatingSystem.IsWindows())
				return false;
		}

		try
		{
			var command = Path.Combine(Environment.SystemDirectory, "cmd.exe");
			if (!Path.IsPathFullyQualified(command) || !File.Exists(command))
				return false;
			using var process = new Process
			{
				StartInfo = new ProcessStartInfo(
					command,
					$"/c mklink /J \"{alias}\" \"{target}\"")
				{
					CreateNoWindow = true,
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true
				}
			};
			if (!process.Start())
				return false;
			if (!process.WaitForExit(5_000))
			{
				process.Kill(entireProcessTree: true);
				return false;
			}
			return process.ExitCode == 0 && Directory.Exists(alias) &&
			       File.GetAttributes(alias).HasFlag(FileAttributes.ReparsePoint);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			return false;
		}
	}
}
