using System.Diagnostics;
using System.Text;
using Xunit;

namespace DevProjex.Tests.Shared.Git;

internal sealed class GitModeRepository : IDisposable
{
	private readonly string _container = Path.Combine(Path.GetTempPath(), "devprojex-git-modes-" + Guid.NewGuid().ToString("N"));
	public string Root { get; }
	public string UserConfiguration { get; }

	public GitModeRepository(string? root = null)
	{
		Root = Directory.CreateDirectory(root ?? Path.Combine(_container, "repository")).FullName;
		Directory.CreateDirectory(_container);
		UserConfiguration = Path.Combine(_container, "gitconfig");
		File.WriteAllText(UserConfiguration, string.Empty);
		Git("init", "--quiet");
		Git("config", "user.name", "DevProjex Tests");
		Git("config", "user.email", "tests@devprojex.invalid");
		Git("config", "core.autocrlf", "false");
		Git("config", "core.eol", "lf");
		Git("config", "core.fileMode", "false");
		Git("config", "core.longpaths", "true");
		Git("config", "commit.gpgSign", "false");
	}

	public string Write(string relativePath, string contents)
	{
		var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, contents, new UTF8Encoding(false));
		return path;
	}

	public void Commit(string message = "fixture")
	{
		Git("add", "--all");
		Git("commit", "--quiet", "-m", message);
	}

	public void SetProcessEnvironment(ProcessStartInfo start)
	{
		foreach (var key in start.Environment.Keys.Where(static key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
			start.Environment.Remove(key);
		start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
		start.Environment["GIT_CONFIG_GLOBAL"] = UserConfiguration;
		start.Environment["GIT_TERMINAL_PROMPT"] = "0";
		start.Environment["HOME"] = _container;
		start.Environment["XDG_CONFIG_HOME"] = Path.Combine(_container, "config");
	}

	public void WriteScopeMatrix()
	{
		Write(".gitignore", "Ignored.cs\n");
		foreach (var name in new[] { "Stable", "RefChanged", "Staged", "Working" })
			Write(name + ".cs", $"public class {name} {{ }} // needle\n");
		Commit("baseline");
		Write("RefChanged.cs", "public class RefChanged { int Value; } // needle\n");
		Commit("reference change");
		Write("Staged.cs", "public class Staged { int Value; } // needle\n");
		Git("add", "Staged.cs");
		Write("Working.cs", "public class Working { int Value; } // needle\n");
		Write("Untracked.cs", "public class Untracked { } // needle\n");
		Write("Ignored.cs", "public class Ignored { } // needle\n");
	}

	public static string[] ExpectedScopeFiles(string mode) => mode switch
	{
		"none" => ["Ignored.cs", "RefChanged.cs", "Stable.cs", "Staged.cs", "Untracked.cs", "Working.cs"],
		"gitignore" => ["RefChanged.cs", "Stable.cs", "Staged.cs", "Untracked.cs", "Working.cs"],
		"tracked" => ["RefChanged.cs", "Stable.cs", "Staged.cs", "Working.cs"],
		"staged" => ["Staged.cs"],
		"changes" => ["Staged.cs", "Untracked.cs", "Working.cs"],
		"diff:HEAD~1..HEAD" => ["RefChanged.cs"],
		_ => throw new ArgumentOutOfRangeException(nameof(mode))
	};

	public string Git(params string[] arguments) => GitAt(Root, arguments);

	public string GitWithExpectedExitCode(int exitCode, params string[] arguments) => GitAtCore(Root, exitCode, arguments);

	public string GitAt(string directory, params string[] arguments) => GitAtCore(directory, 0, arguments);

	private string GitAtCore(string directory, int expectedExitCode, params string[] arguments)
	{
		var start = new ProcessStartInfo("git")
		{
			WorkingDirectory = directory,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = new UTF8Encoding(false)
		};
		SetProcessEnvironment(start);
		start.ArgumentList.Add("-c");
		start.ArgumentList.Add("core.hooksPath=" + Directory.CreateDirectory(Path.Combine(_container, "hooks")).FullName);
		foreach (var argument in arguments)
			start.ArgumentList.Add(argument);
		using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Git.");
		process.StandardInput.Close();
		var output = process.StandardOutput.ReadToEndAsync();
		var error = process.StandardError.ReadToEndAsync();
		if (!process.WaitForExit(20_000))
		{
			process.Kill(entireProcessTree: true);
			throw new TimeoutException("Git fixture command exceeded 20 seconds.");
		}
		Assert.True(process.ExitCode == expectedExitCode,
			$"git {string.Join(' ', arguments)}: expected exit {expectedExitCode}, actual {process.ExitCode}: {error.GetAwaiter().GetResult()}");
		return output.GetAwaiter().GetResult();
	}

	public string WriteMergeConflict()
	{
		Write("conflict.txt", "baseline\n");
		Write("stable.txt", "stable\n");
		Commit("baseline");
		Git("branch", "other-side");
		Write("conflict.txt", "first side\n");
		Commit("first side");
		var first = Git("rev-parse", "HEAD").Trim();
		Git("checkout", "--quiet", "other-side");
		Write("conflict.txt", "second side\n");
		Commit("second side");
		GitWithExpectedExitCode(1, "merge", "--no-edit", first);
		var contents = File.ReadAllText(Path.Combine(Root, "conflict.txt"));
		Assert.Contains("<<<<<<<", contents, StringComparison.Ordinal);
		Assert.Equal(3, Git("ls-files", "--unmerged", "-z").Split('\0', StringSplitOptions.RemoveEmptyEntries).Length);
		return contents;
	}

	public string WriteUntrustedGitMarker(string marker)
	{
		var executable = Write(OperatingSystem.IsWindows() ? "tools/git.exe" : "tools/git",
			"#!/bin/sh\nprintf started > '" + marker.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'\nexit 87\n");
		if (!OperatingSystem.IsWindows())
			File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		return executable;
	}

	public void Dispose()
	{
		foreach (var file in Directory.EnumerateFiles(_container, "*", SearchOption.AllDirectories))
			File.SetAttributes(file, FileAttributes.Normal);
		Directory.Delete(_container, recursive: true);
	}
}
