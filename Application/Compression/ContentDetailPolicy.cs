using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DevProjex.Application.Compression;

/// <summary>
/// One ordered detail override: the globs it claims and the transform kinds a matching file
/// requests. The compiled patterns come from the shared project-relative glob engine, so a mask
/// here means exactly what the same mask means in an include filter.
/// </summary>
public sealed class ContentDetailPatternSet
{
	/// <summary>
	/// Upper bound on the automata one entry may compile after brace expansion. Without it, an entry
	/// of 32 masks each expanding to the brace limit would build thousands of automata and then run
	/// every one of them against every selected file.
	/// </summary>
	public const int MaximumExpandedPatterns = 256;

	private const int MaximumCachedPatternSets = 64;
	private static readonly object CacheSync = new();
	private static readonly Dictionary<string, ContentDetailPatternSet> Cache = new(StringComparer.Ordinal);
	private static readonly Queue<string> CacheOrder = new();

	// Grouped by the caller's pattern rather than flattened, because a brace group expands to
	// several automata and the report has to say which of the *supplied* masks claimed nothing.
	private readonly IReadOnlyList<Regex[]> _compiled;

	private ContentDetailPatternSet(IReadOnlyList<string> patterns, IReadOnlyList<Regex[]> compiled)
	{
		Patterns = patterns;
		_compiled = compiled;
	}

	/// <summary>The caller's patterns, before brace expansion, in caller order.</summary>
	public IReadOnlyList<string> Patterns { get; }

	/// <summary>
	/// Validates and compiles one entry's patterns. A rejected mask throws
	/// <see cref="ProjectRelativeGlobException"/>; the caller maps the reason onto its own error
	/// vocabulary so every surface reports the same wording for the same mistake.
	/// </summary>
	public static ContentDetailPatternSet Create(IReadOnlyList<string> patterns)
	{
		ArgumentNullException.ThrowIfNull(patterns);
		if (patterns.Count == 0)
			throw new ProjectRelativeGlobException("patterns must not be empty");

		// Validate before consulting the cache so a rejected mask is rejected every time, not only
		// on the call that happened to compile it first.
		var expanded = new List<string>[patterns.Count];
		var total = 0;
		for (var index = 0; index < patterns.Count; index++)
		{
			ProjectRelativeGlob.Validate(patterns[index]);
			expanded[index] = [.. ProjectRelativeGlob.ExpandBraces(patterns[index])];
			total += expanded[index].Count;
			if (total > MaximumExpandedPatterns)
			{
				throw new ProjectRelativeGlobException(
					$"the patterns expand to at most {MaximumExpandedPatterns} alternatives in total");
			}
		}

		// Compiling a non-backtracking automaton is not cheap and the same override list recurs
		// across calls, so a bounded cache keyed on the whole ordered list is reused.
		// NUL-separated: a space would let "a b" and the pair "a","b" share one entry.
		var key = string.Join('\0', patterns);
		lock (CacheSync)
		{
			if (Cache.TryGetValue(key, out var cached))
				return cached;
		}

		var compiled = new Regex[patterns.Count][];
		for (var index = 0; index < patterns.Count; index++)
			compiled[index] = [.. expanded[index].Select(ProjectRelativeGlob.Compile)];
		var set = new ContentDetailPatternSet([.. patterns], compiled);
		lock (CacheSync)
		{
			if (Cache.TryAdd(key, set))
			{
				CacheOrder.Enqueue(key);
				while (CacheOrder.Count > MaximumCachedPatternSets)
					Cache.Remove(CacheOrder.Dequeue());
			}
		}
		return set;
	}

	public bool Matches(string relativePath)
	{
		ArgumentNullException.ThrowIfNull(relativePath);
		// Separators are normalised here, exactly as the include filter does, so a caller may hand
		// over either platform form and still get the same decision.
		var normalized = PathUtility.NormalizeSeparators(relativePath);
		for (var index = 0; index < _compiled.Count; index++)
		{
			if (MatchesNormalized(index, normalized))
				return true;
		}
		return false;
	}

	/// <summary>Whether the caller's pattern at <paramref name="patternIndex"/> claims this file.</summary>
	public bool MatchesPattern(int patternIndex, string relativePath)
	{
		ArgumentNullException.ThrowIfNull(relativePath);
		ArgumentOutOfRangeException.ThrowIfNegative(patternIndex);
		ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(patternIndex, _compiled.Count);
		return MatchesNormalized(patternIndex, PathUtility.NormalizeSeparators(relativePath));
	}

	private bool MatchesNormalized(int patternIndex, string normalizedPath)
	{
		foreach (var regex in _compiled[patternIndex])
		{
			if (regex.IsMatch(normalizedPath))
				return true;
		}
		return false;
	}
}

public sealed record ContentDetailOverride(
	ContentDetailPatternSet Patterns,
	CodeTransformKinds RequestedKinds);

/// <summary>
/// Resolves transform kinds per file instead of per selection.
///
/// The profile's kinds are kept separately from the call level on purpose. A pack bakes
/// "profile OR call level" into the three selection booleans, after which the profile's own kinds
/// can no longer be recovered from them - and without them an override back to <c>full</c> would
/// become a way to escape a saved profile. Keeping <see cref="ProfileKinds"/> explicit lets the
/// union run again for every single file.
///
/// <see cref="KindsFor"/> is pure: it reads only immutable state and precompiled matchers, which is
/// what makes it safe to call from the preparation workers, where nothing serializes access to
/// redaction state.
/// </summary>
public sealed class ContentDetailPolicy
{
	public ContentDetailPolicy(
		CodeTransformKinds profileKinds,
		CodeTransformKinds baseKinds,
		IReadOnlyList<ContentDetailOverride> overrides)
	{
		ArgumentNullException.ThrowIfNull(overrides);
		ProfileKinds = profileKinds;
		BaseKinds = baseKinds;
		Overrides = overrides;
		var union = baseKinds;
		foreach (var entry in overrides)
			union |= profileKinds | entry.RequestedKinds;
		UnionKinds = union;
	}

	/// <summary>Kinds the resolved profile mandates on its own, before any call-level request.</summary>
	public CodeTransformKinds ProfileKinds { get; }

	/// <summary>Profile kinds unioned with the call-level detail, used by every unmatched file.</summary>
	public CodeTransformKinds BaseKinds { get; }

	public IReadOnlyList<ContentDetailOverride> Overrides { get; }

	/// <summary>
	/// Every kind any file in this operation can request. Context creation gates on this, never on
	/// <see cref="BaseKinds"/>: a call whose default is <c>full</c> and whose overrides ask for
	/// <c>signatures</c> has no base kinds at all, and gating on those would silently skip
	/// compression for the whole operation.
	/// </summary>
	public CodeTransformKinds UnionKinds { get; }

	public bool IsUniform => Overrides.Count == 0;

	public CodeTransformKinds KindsFor(string relativePath)
	{
		ArgumentNullException.ThrowIfNull(relativePath);
		// Last match wins, so the scan runs to the end rather than stopping at the first hit.
		var kinds = BaseKinds;
		for (var index = 0; index < Overrides.Count; index++)
		{
			if (Overrides[index].Patterns.Matches(relativePath))
				kinds = ProfileKinds | Overrides[index].RequestedKinds;
		}
		return kinds;
	}

	/// <summary>
	/// The one path form patterns are matched against. Compression and redaction both go through
	/// this, so the two halves of the pipeline can never disagree about which override claims a
	/// file.
	/// </summary>
	public static string ToProjectRelativePath(string projectRoot, string fullPath)
	{
		ArgumentNullException.ThrowIfNull(fullPath);
		if (string.IsNullOrEmpty(projectRoot))
			return PathUtility.NormalizeSeparators(fullPath);
		try
		{
			return PathUtility.NormalizeSeparators(Path.GetRelativePath(projectRoot, fullPath));
		}
		catch (ArgumentException)
		{
			return PathUtility.NormalizeSeparators(fullPath);
		}
	}

	/// <summary>
	/// Identifies this operation's transformation shape. Unlike a single kinds value it has to
	/// cover the whole ordered override list, because two calls that differ only in which files an
	/// override claims must not share cached scans of differently transformed text.
	/// </summary>
	public string ComputeIdentity(string engineIdentity)
	{
		ArgumentNullException.ThrowIfNull(engineIdentity);
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		Append(hash, engineIdentity);
		Append(hash, (int)ProfileKinds);
		Append(hash, (int)BaseKinds);
		Append(hash, Overrides.Count);
		foreach (var entry in Overrides)
		{
			Append(hash, (int)entry.RequestedKinds);
			Append(hash, entry.Patterns.Patterns.Count);
			foreach (var pattern in entry.Patterns.Patterns)
				Append(hash, pattern);
		}
		return engineIdentity + "+detail:" + Convert.ToHexString(hash.GetHashAndReset());
	}

	private static void Append(IncrementalHash hash, string value)
	{
		var bytes = Encoding.UTF8.GetBytes(value);
		Append(hash, bytes.Length);
		hash.AppendData(bytes);
	}

	private static void Append(IncrementalHash hash, int value)
	{
		Span<byte> buffer = stackalloc byte[4];
		BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
		hash.AppendData(buffer);
	}
}
