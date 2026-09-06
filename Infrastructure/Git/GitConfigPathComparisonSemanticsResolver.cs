using System.ComponentModel;
using DevProjex.Application.Services;

namespace DevProjex.Infrastructure.Git;

public sealed class GitConfigPathComparisonSemanticsResolver
	: IGitPathComparisonSemanticsResolver
{
	private const int CacheLimit = 128;
	private const int CommandTimeoutMilliseconds = 5000;
	private const int KillWaitMilliseconds = 1000;
	private static readonly TimeSpan UnavailableRetryDelay = TimeSpan.FromSeconds(5);
	// Values on an unavailable result are intentionally non-contractual. Consumers must
	// reject non-authoritative semantics instead of guessing across escaped patterns,
	// character classes, negations, or tracked membership.
	private static readonly GitPathComparisonSemantics UnavailableRepositorySemantics = new(
		IgnoreCase: true,
		NormalizeUnicode: true,
		IsAuthoritative: false);
	private readonly Func<string, string, GitPathComparisonSemantics> _repositorySemanticsResolver;
	private readonly Func<DateTime> _utcNowProvider;
	private readonly TimeSpan _unavailableRetryDelay;
	private readonly object _cacheSync = new();
	private readonly Dictionary<string, RepositorySemanticsCacheEntry> _repositoryCache =
		new(StringComparer.Ordinal);
	private readonly Dictionary<string, long> _latestResolutionSequences =
		new(StringComparer.Ordinal);
	private long _cacheGeneration;
	private long _nextResolutionSequence;

	public static GitConfigPathComparisonSemanticsResolver Instance { get; } = new();

	public GitConfigPathComparisonSemanticsResolver()
		: this(ResolveRepositorySemantics, static () => DateTime.UtcNow, UnavailableRetryDelay)
	{
	}

	internal GitConfigPathComparisonSemanticsResolver(
		Func<string, string, GitPathComparisonSemantics> repositorySemanticsResolver,
		Func<DateTime> utcNowProvider,
		TimeSpan unavailableRetryDelay)
	{
		ArgumentNullException.ThrowIfNull(repositorySemanticsResolver);
		ArgumentNullException.ThrowIfNull(utcNowProvider);
		if (unavailableRetryDelay < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(unavailableRetryDelay));

		_repositorySemanticsResolver = repositorySemanticsResolver;
		_utcNowProvider = utcNowProvider;
		_unavailableRetryDelay = unavailableRetryDelay;
	}

	public GitPathComparisonSemantics Resolve(string scopeRootPath)
	{
		if (!TryFindNearestRepositoryBoundary(
			    scopeRootPath,
			    out var repositoryRoot,
			    out var gitMetadataPath))
		{
			return ResolveFileSystemFallback(scopeRootPath, gitMetadataPath: null);
		}

		var now = _utcNowProvider();
		long cacheGeneration;
		long resolutionSequence;
		lock (_cacheSync)
		{
			if (_repositoryCache.TryGetValue(repositoryRoot, out var cached))
			{
				var elapsed = now - cached.CapturedUtc;
				if (cached.Semantics.IsAuthoritative ||
				    elapsed >= TimeSpan.Zero && elapsed < _unavailableRetryDelay)
			{
					return cached.Semantics;
				}
			}

			if (!_latestResolutionSequences.ContainsKey(repositoryRoot) &&
			    _latestResolutionSequences.Count >= CacheLimit)
			{
				_cacheGeneration++;
				_repositoryCache.Clear();
				_latestResolutionSequences.Clear();
			}

			cacheGeneration = _cacheGeneration;
			resolutionSequence = ++_nextResolutionSequence;
			_latestResolutionSequences[repositoryRoot] = resolutionSequence;
		}

		var resolved = _repositorySemanticsResolver(repositoryRoot, gitMetadataPath);
		lock (_cacheSync)
		{
			if (cacheGeneration != _cacheGeneration ||
			    !_latestResolutionSequences.TryGetValue(repositoryRoot, out var latestResolutionSequence) ||
			    latestResolutionSequence != resolutionSequence)
			{
				return resolved;
			}

			_repositoryCache[repositoryRoot] = new RepositorySemanticsCacheEntry(
				resolved,
				_utcNowProvider());
		}

		return resolved;
	}

	public void Invalidate(string rootPath)
	{
		if (!TryNormalizePath(rootPath, out var normalizedRootPath))
			return;

		lock (_cacheSync)
		{
			_cacheGeneration++;
			foreach (var repositoryRoot in _repositoryCache.Keys.ToArray())
			{
				if (PathsOverlap(repositoryRoot, normalizedRootPath))
					_repositoryCache.Remove(repositoryRoot);
			}

			foreach (var repositoryRoot in _latestResolutionSequences.Keys.ToArray())
			{
				if (PathsOverlap(repositoryRoot, normalizedRootPath))
					_latestResolutionSequences.Remove(repositoryRoot);
			}
		}
	}

	private static GitPathComparisonSemantics ResolveRepositorySemantics(
		string repositoryRoot,
		string gitMetadataPath)
	{
		if (GitLocalConfigSemanticsReader.TryRead(
			    repositoryRoot,
			    gitMetadataPath,
			    out var localSemantics))
		{
			return localSemantics;
		}

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

	internal static bool TryRunGit(
		string repositoryRoot,
		out string standardOutput,
		out int exitCode,
		string? executable = null)
	{
		standardOutput = string.Empty;
		exitCode = -1;
		try
		{
			using var process = new Process
			{
				StartInfo = CreateGitStartInfo(repositoryRoot, executable)
			};
			if (!process.Start())
				return false;

			process.StandardInput.Close();
			using var readerCancellation = new CancellationTokenSource();
			var outputTask = GitProcessOutputReader.ReadAsync(
				process.StandardOutput,
				GitProcessOutputReader.MaximumOutputCharacters,
				readerCancellation.Token);
			var errorTask = GitProcessOutputReader.ReadAsync(
				process.StandardError,
				GitProcessOutputReader.MaximumOutputCharacters,
				readerCancellation.Token);
			if (!process.WaitForExit(CommandTimeoutMilliseconds))
			{
				TryKill(process);
				_ = process.WaitForExit(KillWaitMilliseconds);
				StopReaders(process, readerCancellation, outputTask, errorTask);
				return false;
			}

			var readers = Task.WhenAll(outputTask, errorTask);
			if (!WaitForCompletion(readers, KillWaitMilliseconds))
			{
				StopReaders(process, readerCancellation, outputTask, errorTask);
				return false;
			}

			var output = outputTask.GetAwaiter().GetResult();
			var error = errorTask.GetAwaiter().GetResult();
			if (output.ExceededLimit || error.ExceededLimit)
				return false;
			standardOutput = output.Text;
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

	private static bool WaitForCompletion(Task task, int timeoutMilliseconds) =>
		task.IsCompleted || ReferenceEquals(
			Task.WhenAny(task, Task.Delay(timeoutMilliseconds)).GetAwaiter().GetResult(),
			task);

	private static void StopReaders(
		Process process,
		CancellationTokenSource cancellation,
		Task output,
		Task error)
	{
		cancellation.Cancel();
		try
		{
			process.StandardOutput.Dispose();
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException)
		{
		}
		try
		{
			process.StandardError.Dispose();
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException)
		{
		}
		GitProcessOutputReader
			.ObserveAfterTerminationAsync(process, output, error)
			.GetAwaiter()
			.GetResult();
	}

	private static ProcessStartInfo CreateGitStartInfo(
		string repositoryRoot,
		string? executable)
	{
		return executable is null
			? GitProcessStartInfoFactory.Create(
				repositoryRoot,
				GitProcessOperation.ReadConfigValue(GitConfigReadKind.PathComparisonSemantics))
			: GitProcessStartInfoFactory.CreateForTesting(
				repositoryRoot,
				GitProcessOperation.ReadConfigValue(GitConfigReadKind.PathComparisonSemantics),
				executable);
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
				if (!UnixFileTypeInspector.IsPhysicalDirectoryOrRegularFile(candidate, attributes))
				{
					currentPath = Path.GetDirectoryName(currentPath);
					continue;
				}

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
		PathUtility.IsPathInside(leftPath, rightPath) ||
		PathUtility.IsPathInside(rightPath, leftPath);

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

	private readonly record struct RepositorySemanticsCacheEntry(
		GitPathComparisonSemantics Semantics,
		DateTime CapturedUtc);

}
