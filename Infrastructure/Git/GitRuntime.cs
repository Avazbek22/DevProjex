using System.ComponentModel;
using System.Runtime.InteropServices;
using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Infrastructure.Git;

internal sealed record GitIsolationPaths(
	string RootDirectory,
	string EmptyHooksDirectory,
	string EmptyTemplateDirectory,
	string EmptyGlobalConfigFile,
	string EmptyAttributesFile,
	string EmptyExcludesFile);

internal static class GitRuntime
{
	private static readonly Lazy<string> GitPath = new(
		() => GitExecutableLocator.Resolve(OperatingSystem.IsWindows() ? "git.exe" : "git"),
		LazyThreadSafetyMode.ExecutionAndPublication);
	private static readonly Lazy<string?> SshPath = new(
		() => GitExecutableLocator.TryResolve(OperatingSystem.IsWindows() ? "ssh.exe" : "ssh"),
		LazyThreadSafetyMode.ExecutionAndPublication);
	private static readonly Lazy<GitIsolationPaths> Isolation = new(
		CreateIsolationPaths,
		LazyThreadSafetyMode.ExecutionAndPublication);
	private static readonly Lazy<string> Version = new(ReadVersion, LazyThreadSafetyMode.ExecutionAndPublication);

	public static string GitExecutable => GitPath.Value;
	public static string? SshExecutable => SshPath.Value;
	public static GitIsolationPaths IsolationPaths => Isolation.Value;
	public static string VersionDisplay => Version.Value;

	internal static bool IsAtLeastVersion(int major, int minor)
	{
		var version = VersionDisplay;
		const string prefix = "git version ";
		if (!version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			return false;
		var components = version[prefix.Length..].Split(['.', ' ', '-'], StringSplitOptions.RemoveEmptyEntries);
		return components.Length >= 2 &&
		       int.TryParse(components[0], out var actualMajor) &&
		       int.TryParse(components[1], out var actualMinor) &&
		       (actualMajor > major || actualMajor == major && actualMinor >= minor);
	}

	private static GitIsolationPaths CreateIsolationPaths()
	{
		var root = Path.Combine(UserDataPathResolver.GetCacheRoot(), "DevProjex", "git-safety");
		var hooks = Path.Combine(root, "empty-hooks");
		var template = Path.Combine(root, "empty-template");
		Directory.CreateDirectory(hooks);
		Directory.CreateDirectory(template);
		if (!OperatingSystem.IsWindows())
		{
			var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
			File.SetUnixFileMode(root, mode);
			File.SetUnixFileMode(hooks, mode);
			File.SetUnixFileMode(template, mode);
		}

		var global = EnsureEmptyFile(root, "empty-global.gitconfig");
		var attributes = EnsureEmptyFile(root, "empty-attributes");
		var excludes = EnsureEmptyFile(root, "empty-excludes");
		return new GitIsolationPaths(root, hooks, template, global, attributes, excludes);
	}

	private static string EnsureEmptyFile(string root, string name)
	{
		var path = Path.Combine(root, name);
		using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
		{
		}
		if (new FileInfo(path).Length != 0)
			throw new InvalidOperationException($"The Git isolation file is not empty: {path}");
		if (!OperatingSystem.IsWindows())
			File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		return path;
	}

	private static string ReadVersion()
	{
		try
		{
			var startInfo = GitProcessStartInfoFactory.CreateVersionProbe();
			using var process = Process.Start(startInfo);
			if (process is null)
				return "unavailable";
			process.StandardInput.Close();
			if (!process.WaitForExit(3000))
			{
				process.Kill(entireProcessTree: true);
				return "unavailable";
			}
			var output = process.StandardOutput.ReadToEnd().Trim();
			return process.ExitCode == 0 && output.Length <= 256 ? output : "unavailable";
		}
		catch
		{
			return "unavailable";
		}
	}
}

public static class GitRuntimeInformation
{
	public static string VersionDisplay => GitRuntime.VersionDisplay;

	public static ProcessStartInfo CreateVersionProbeStartInfo() =>
		GitProcessStartInfoFactory.CreateVersionProbe();
}

internal static class GitExecutableLocator
{
	public static string Resolve(string executableName) =>
		TryResolve(executableName) ?? throw new Win32Exception($"{executableName} was not found on a safe PATH entry.");

	public static string? TryResolve(string executableName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executableName);
		var pathValue = Environment.GetEnvironmentVariable("PATH");
		if (string.IsNullOrWhiteSpace(pathValue))
			return null;

		var current = Path.GetFullPath(Environment.CurrentDirectory);
		foreach (var rawEntry in pathValue.Split(Path.PathSeparator))
		{
			var entry = rawEntry.Trim().Trim('"');
			if (!Path.IsPathFullyQualified(entry))
				continue;
			string directory;
			string candidate;
			try
			{
				directory = Path.GetFullPath(entry);
				if (PathsAreNested(directory, current))
					continue;
				candidate = Path.GetFullPath(Path.Combine(directory, executableName));
			}
			catch
			{
				continue;
			}
			if (TryResolvePhysicalFile(candidate, out var resolved) &&
			    !PathsAreNested(Path.GetDirectoryName(resolved)!, current))
			{
				return resolved;
			}
		}
		return null;
	}

	internal static bool IsSafeForRepository(string executable, string? repositoryPath)
	{
		if (string.IsNullOrWhiteSpace(repositoryPath))
			return true;
		var executableDirectory = Path.GetDirectoryName(Path.GetFullPath(executable));
		var repository = Path.GetFullPath(repositoryPath);
		return executableDirectory is not null && !PathsAreNested(executableDirectory, repository);
	}

	private static bool PathsAreNested(string left, string right) =>
		IsDirectoryAtOrAbove(left, right) || IsDirectoryAtOrAbove(right, left);

	private static bool IsDirectoryAtOrAbove(string candidateDirectory, string path)
	{
		if (PathComparer.Default.Equals(candidateDirectory, path))
			return true;
		var relative = Path.GetRelativePath(candidateDirectory, path);
		return relative.Length > 0 &&
		       !Path.IsPathFullyQualified(relative) &&
		       relative != ".." &&
		       !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
		       !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
	}

	private static bool TryResolvePhysicalFile(string path, out string resolved)
	{
		resolved = string.Empty;
		try
		{
			var info = new FileInfo(path);
			if (!info.Exists || info.Attributes.HasFlag(FileAttributes.Directory))
				return false;
			var target = info.LinkTarget is null ? info : info.ResolveLinkTarget(returnFinalTarget: true);
			if (target is null || !target.Exists || target.Attributes.HasFlag(FileAttributes.Directory))
				return false;
			resolved = Path.GetFullPath(target.FullName);
			return true;
		}
		catch
		{
			return false;
		}
	}
}
