namespace DevProjex.Application.Context;

public static class GitScopeSelection
{
	public const string DiffPrefix = "diff:";
	public const int MaximumTokenLength = 4096;

	public static ProjectSelectionSpec WithMode(
		ProjectSelectionSpec selection,
		GitFilteringMode mode,
		string? diffRange = null)
	{
		ArgumentNullException.ThrowIfNull(selection);
		if (!Enum.IsDefined(mode))
			throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
		if (mode == GitFilteringMode.Diff && !IsValidDiffRange(diffRange))
		{
			throw new ArgumentException(
				"A diff Git scope requires two non-empty references separated by '..'.",
				nameof(diffRange));
		}

		return selection with
		{
			GitMode = mode,
			GitDiffRange = mode == GitFilteringMode.Diff ? diffRange : null
		};
	}

	public static bool IsMomentary(GitFilteringMode mode) =>
		mode is GitFilteringMode.Staged or GitFilteringMode.Changes or GitFilteringMode.Diff;

	public static bool IsPersistent(GitFilteringMode mode) =>
		mode is GitFilteringMode.None or
			GitFilteringMode.RespectGitIgnore or
			GitFilteringMode.TrackedFilesOnly;

	public static GitFilteringMode ToUnderlayMode(GitFilteringMode mode) => mode switch
	{
		GitFilteringMode.Staged => GitFilteringMode.None,
		GitFilteringMode.Changes => GitFilteringMode.RespectGitIgnore,
		GitFilteringMode.Diff => GitFilteringMode.None,
		_ => mode
	};

	public static GitFilteringMode ComposeNarrowingUnderlay(
		GitFilteringMode baselineMode,
		GitFilteringMode scopeMode)
	{
		if (!IsPersistent(baselineMode))
			throw new ArgumentOutOfRangeException(nameof(baselineMode), baselineMode, "A persistent baseline is required.");
		if (!IsMomentary(scopeMode))
			throw new ArgumentOutOfRangeException(nameof(scopeMode), scopeMode, "A momentary Git scope is required.");

		var scopeUnderlay = ToUnderlayMode(scopeMode);
		if (baselineMode == GitFilteringMode.TrackedFilesOnly ||
		    scopeUnderlay == GitFilteringMode.TrackedFilesOnly)
		{
			return GitFilteringMode.TrackedFilesOnly;
		}

		return baselineMode == GitFilteringMode.RespectGitIgnore ||
		       scopeUnderlay == GitFilteringMode.RespectGitIgnore
			? GitFilteringMode.RespectGitIgnore
			: GitFilteringMode.None;
	}

	public static string ToToken(GitFilteringMode mode, string? diffRange = null) => mode switch
	{
		GitFilteringMode.None => "none",
		GitFilteringMode.RespectGitIgnore => "gitignore",
		GitFilteringMode.TrackedFilesOnly => "tracked",
		GitFilteringMode.Staged => "staged",
		GitFilteringMode.Changes => "changes",
		GitFilteringMode.Diff when IsValidDiffRange(diffRange) => DiffPrefix + diffRange,
		GitFilteringMode.Diff => throw new ArgumentException(
			"A diff Git scope requires two non-empty references separated by '..'.",
			nameof(diffRange)),
		_ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
	};

	public static bool TryParse(
		string? token,
		out GitFilteringMode mode,
		out string? diffRange)
	{
		mode = GitFilteringMode.None;
		diffRange = null;
		if (string.IsNullOrWhiteSpace(token))
			return false;

		var normalized = token.Trim();
		if (normalized.Length > MaximumTokenLength)
			return false;
		if (normalized.Equals("none", StringComparison.OrdinalIgnoreCase) ||
		    normalized.Equals("off", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		if (normalized.Equals("gitignore", StringComparison.OrdinalIgnoreCase))
		{
			mode = GitFilteringMode.RespectGitIgnore;
			return true;
		}
		if (normalized.Equals("tracked", StringComparison.OrdinalIgnoreCase))
		{
			mode = GitFilteringMode.TrackedFilesOnly;
			return true;
		}
		if (normalized.Equals("staged", StringComparison.OrdinalIgnoreCase))
		{
			mode = GitFilteringMode.Staged;
			return true;
		}
		if (normalized.Equals("changes", StringComparison.OrdinalIgnoreCase))
		{
			mode = GitFilteringMode.Changes;
			return true;
		}
		if (!normalized.StartsWith(DiffPrefix, StringComparison.OrdinalIgnoreCase))
			return false;

		var candidateRange = normalized[DiffPrefix.Length..];
		if (!IsValidDiffRange(candidateRange))
			return false;
		mode = GitFilteringMode.Diff;
		diffRange = candidateRange;
		return true;
	}

	public static bool IsValidDiffRange(string? range)
	{
		if (string.IsNullOrWhiteSpace(range) || range.IndexOf('\0') >= 0)
			return false;

		var separator = range.IndexOf("..", StringComparison.Ordinal);
		if (separator <= 0 || separator + 2 >= range.Length ||
		    range.IndexOf("..", separator + 1, StringComparison.Ordinal) >= 0)
		{
			return false;
		}

		var left = range[..separator];
		var right = range[(separator + 2)..];
		return IsValidReference(left) && IsValidReference(right);
	}

	public static (string Left, string Right) SplitDiffRange(string range)
	{
		if (!IsValidDiffRange(range))
			throw new ArgumentException("The Git diff range is invalid.", nameof(range));

		var separator = range.IndexOf("..", StringComparison.Ordinal);
		return (range[..separator], range[(separator + 2)..]);
	}

	private static bool IsValidReference(string value) =>
		!string.IsNullOrWhiteSpace(value) &&
		value[0] != '-' &&
		!value.Any(char.IsWhiteSpace);
}
