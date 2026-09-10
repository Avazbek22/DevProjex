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
	private readonly IReadOnlyList<Regex> _compiled;

	private ContentDetailPatternSet(IReadOnlyList<string> patterns, IReadOnlyList<Regex> compiled)
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

		var expanded = new List<string>(patterns.Count);
		foreach (var pattern in patterns)
		{
			ProjectRelativeGlob.Validate(pattern);
			expanded.AddRange(ProjectRelativeGlob.ExpandBraces(pattern));
		}

		var compiled = new Regex[expanded.Count];
		for (var index = 0; index < expanded.Count; index++)
			compiled[index] = ProjectRelativeGlob.Compile(expanded[index]);
		return new ContentDetailPatternSet(patterns.ToArray(), compiled);
	}

	public bool Matches(string relativePath)
	{
		ArgumentNullException.ThrowIfNull(relativePath);
		for (var index = 0; index < _compiled.Count; index++)
		{
			if (_compiled[index].IsMatch(relativePath))
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
/// <see cref="KindsFor"/> is pure and allocation-free: it is called from the preparation workers,
/// where nothing serializes access to redaction state.
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
