using System.ComponentModel;
using DevProjex.Application.Services;

namespace DevProjex.Infrastructure.Git;

public sealed class GitConfigPathComparisonSemanticsResolver
	: IGitPathComparisonSemanticsResolver
{
	private const int CacheLimit = 128;
	private const int CommandTimeoutMilliseconds = 5000;
	private const int KillWaitMilliseconds = 1000;
	// Values on an unavailable result are intentionally non-contractual. Consumers must
	// reject non-authoritative semantics instead of guessing across escaped patterns,
	// character classes, negations, or tracked membership.
	private static readonly GitPathComparisonSemantics UnavailableRepositorySemantics = new(
		IgnoreCase: true,
		NormalizeUnicode: true,
		IsAuthoritative: false);
	private static readonly string GitExecutable = OperatingSystem.IsWindows() ? "git.exe" : "git";
	private readonly object _cacheSync = new();
	private readonly Dictionary<string, GitPathComparisonSemantics> _repositoryCache =
		new(PathComparer.Default);

	public static GitConfigPathComparisonSemanticsResolver Instance { get; } = new();

	public GitPathComparisonSemantics Resolve(string scopeRootPath)
	{
		if (!TryFindNearestRepositoryBoundary(scopeRootPath, out var repositoryRoot, out _))
			return ResolveFileSystemFallback(scopeRootPath, gitMetadataPath: null);

		lock (_cacheSync)
		{
			if (_repositoryCache.TryGetValue(repositoryRoot, out var cached))
				return cached;
		}

		var resolved = ResolveRepositorySemantics(repositoryRoot);
		lock (_cacheSync)
		{
			if (_repositoryCache.Count >= CacheLimit)
				_repositoryCache.Clear();
			_repositoryCache[repositoryRoot] = resolved;
		}

		return resolved;
	}

	public void Invalidate(string rootPath)
	{
		if (!TryNormalizePath(rootPath, out var normalizedRootPath))
			return;

		lock (_cacheSync)
		{
			foreach (var repositoryRoot in _repositoryCache.Keys.ToArray())
			{
				if (PathsOverlap(repositoryRoot, normalizedRootPath))
					_repositoryCache.Remove(repositoryRoot);
			}
		}
	}

	private static GitPathComparisonSemantics ResolveRepositorySemantics(string repositoryRoot)
	{
		return TryReadEffectiveSemantics(repositoryRoot, out var semantics)
			? semantics
			: UnavailableRepositorySemantics;
	}

	private static bool TryReadEffectiveSemantics(
		string repositoryRoot,
		out GitPathComparisonSemantics semantics)
	{
		semantics = default;
		if (!TryRunGit(
				repositoryRoot,
				[
					"config",
					"--show-scope",
					"--type=bool",
					"--get-regexp",
					"^core\\.(repositoryformatversion|ignorecase|precomposeunicode)$"
				],
				out var output,
				out var exitCode))
		{
			return false;
		}

		if (exitCode != 0)
			return false;

		var ignoreCase = false;
		var normalizeUnicode = false;
		var hasLocalRepositoryConfiguration = false;
		foreach (var line in output.Split(
			         ['\r', '\n'],
			         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var scopeSeparatorIndex = line.IndexOfAny([' ', '\t']);
			if (scopeSeparatorIndex <= 0 || scopeSeparatorIndex >= line.Length - 1)
				return false;
			var keyStartIndex = scopeSeparatorIndex + 1;
			while (keyStartIndex < line.Length && line[keyStartIndex] is ' ' or '\t')
				keyStartIndex++;
			var valueSeparatorIndex = line.IndexOfAny([' ', '\t'], keyStartIndex);
			if (valueSeparatorIndex <= keyStartIndex || valueSeparatorIndex >= line.Length - 1)
				return false;

			var valueStartIndex = valueSeparatorIndex + 1;
			while (valueStartIndex < line.Length && line[valueStartIndex] is ' ' or '\t')
				valueStartIndex++;
			if (valueStartIndex >= line.Length)
				return false;

			var scope = line[..scopeSeparatorIndex];
			var key = line[keyStartIndex..valueSeparatorIndex];
			if (!bool.TryParse(line[valueStartIndex..].Trim(), out var value))
				return false;

			if (key.Equals("core.repositoryformatversion", StringComparison.OrdinalIgnoreCase))
			{
				hasLocalRepositoryConfiguration |=
					scope.Equals("local", StringComparison.OrdinalIgnoreCase) ||
					scope.Equals("worktree", StringComparison.OrdinalIgnoreCase);
			}
			else if (key.Equals("core.ignorecase", StringComparison.OrdinalIgnoreCase))
				ignoreCase = value;
			else if (OperatingSystem.IsMacOS() &&
			         key.Equals("core.precomposeunicode", StringComparison.OrdinalIgnoreCase))
				normalizeUnicode = value;
		}

		if (!hasLocalRepositoryConfiguration)
			return false;

		semantics = new GitPathComparisonSemantics(ignoreCase, normalizeUnicode);
		return true;
	}

	private static bool TryRunGit(
		string repositoryRoot,
		IReadOnlyList<string> arguments,
		out string standardOutput,
		out int exitCode)
	{
		standardOutput = string.Empty;
		exitCode = -1;
		try
		{
			using var process = new Process
			{
				StartInfo = CreateGitStartInfo(repositoryRoot, arguments)
			};
			if (!process.Start())
				return false;

			process.StandardInput.Close();
			var outputTask = process.StandardOutput.ReadToEndAsync();
			var errorTask = process.StandardError.ReadToEndAsync();
			if (!process.WaitForExit(CommandTimeoutMilliseconds))
			{
				TryKill(process);
				if (process.WaitForExit(KillWaitMilliseconds))
				{
					_ = outputTask.GetAwaiter().GetResult();
					_ = errorTask.GetAwaiter().GetResult();
				}
				return false;
			}

			standardOutput = outputTask.GetAwaiter().GetResult();
			_ = errorTask.GetAwaiter().GetResult();
			exitCode = process.ExitCode;
			return true;
		}
		catch (Exception exception) when (exception is
		       Win32Exception or
		       IOException or
		       UnauthorizedAccessException or
		       InvalidOperationException or
		       NotSupportedException)
		{
			return false;
		}
	}

	private static ProcessStartInfo CreateGitStartInfo(
		string repositoryRoot,
		IReadOnlyList<string> arguments)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = GitExecutable,
			WorkingDirectory = repositoryRoot,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		GitProcessEnvironmentSanitizer.RemoveRepositoryOverrides(startInfo);
		startInfo.ArgumentList.Add("-C");
		startInfo.ArgumentList.Add(repositoryRoot);
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
		startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
		return startInfo;
	}

	private static bool TryFindNearestRepositoryBoundary(
		string scopeRootPath,
		out string repositoryRoot,
		out string gitMetadataPath)
	{
		repositoryRoot = string.Empty;
		gitMetadataPath = string.Empty;
		if (!TryNormalizePath(scopeRootPath, out var currentPath))
			return false;

		while (!string.IsNullOrWhiteSpace(currentPath))
		{
			var candidate = Path.Combine(currentPath, ".git");
			try
			{
				var attributes = File.GetAttributes(candidate);
				// A reparse metadata boundary is not safe repository ownership evidence.
				if (attributes.HasFlag(FileAttributes.ReparsePoint))
					return false;

				repositoryRoot = currentPath;
				gitMetadataPath = candidate;
				return true;
			}
			catch (FileNotFoundException)
			{
			}
			catch (DirectoryNotFoundException)
			{
			}
			catch
			{
				// An unreadable nested boundary must not inherit an ancestor repository.
				return false;
			}

			var parentPath = Path.GetDirectoryName(currentPath);
			if (string.IsNullOrWhiteSpace(parentPath) || PathComparer.Default.Equals(parentPath, currentPath))
				break;
			currentPath = parentPath;
		}

		return false;
	}

	private static GitPathComparisonSemantics ResolveFileSystemFallback(
		string scopeRootPath,
		string? gitMetadataPath)
	{
		var fallback = GitPathComparisonSemantics.PlatformDefault;
		var probePath = gitMetadataPath ?? Path.Combine(scopeRootPath, ".gitignore");
		try
		{
			var parentPath = Path.GetDirectoryName(probePath);
			var storedName = Path.GetFileName(probePath);
			var alternateName = ToggleFirstAsciiLetterCase(storedName);
			if (string.IsNullOrWhiteSpace(parentPath) || alternateName is null)
				return fallback;

			var alternatePath = Path.Combine(parentPath, alternateName);
			try
			{
				_ = File.GetAttributes(alternatePath);
			}
			catch (FileNotFoundException)
			{
				return fallback with { IgnoreCase = false };
			}
			catch (DirectoryNotFoundException)
			{
				return fallback with { IgnoreCase = false };
			}

			var hasStoredName = false;
			var hasAlternateName = false;
			foreach (var entryPath in Directory.EnumerateFileSystemEntries(parentPath))
			{
				var entryName = Path.GetFileName(entryPath);
				hasStoredName |= string.Equals(entryName, storedName, StringComparison.Ordinal);
				hasAlternateName |= string.Equals(entryName, alternateName, StringComparison.Ordinal);
			}

			return fallback with { IgnoreCase = !(hasStoredName && hasAlternateName) };
		}
		catch
		{
			return fallback;
		}
	}

	private static string? ToggleFirstAsciiLetterCase(string value)
	{
		for (var index = 0; index < value.Length; index++)
		{
			var character = value[index];
			char replacement;
			if (character is >= 'a' and <= 'z')
				replacement = (char)(character - ('a' - 'A'));
			else if (character is >= 'A' and <= 'Z')
				replacement = (char)(character + ('a' - 'A'));
			else
				continue;

			var characters = value.ToCharArray();
			characters[index] = replacement;
			return new string(characters);
		}

		return null;
	}

	private static bool TryNormalizePath(string path, out string normalizedPath)
	{
		try
		{
			normalizedPath = PathUtility.Normalize(path);
			return true;
		}
		catch
		{
			normalizedPath = string.Empty;
			return false;
		}
	}

	private static bool PathsOverlap(string leftPath, string rightPath) =>
		IsSameOrDescendantPath(leftPath, rightPath) ||
		IsSameOrDescendantPath(rightPath, leftPath);

	private static bool IsSameOrDescendantPath(string candidatePath, string rootPath)
	{
		if (PathComparer.Default.Equals(candidatePath, rootPath))
			return true;
		if (!candidatePath.StartsWith(rootPath, PathComparer.Comparison) ||
		    candidatePath.Length <= rootPath.Length)
		{
			return false;
		}

		return candidatePath[rootPath.Length] is '\\' or '/';
	}

	private static void TryKill(Process process)
	{
		try
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
		}
		catch
		{
			// Timeout cleanup is best-effort; no process output reaches a user surface.
		}
	}

}
