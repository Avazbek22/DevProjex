namespace DevProjex.Application.Context;

/// <summary>
/// What a mixed-detail call actually did: how many files landed on each level, and which supplied
/// patterns claimed nothing. Computed in one pass over the effective selection using the same policy
/// the transformation uses, so the reported mix cannot describe a different decision than the one
/// applied.
///
/// Selection is never widened by these patterns; a pattern that matches nothing is reported rather
/// than adding files.
/// </summary>
public sealed record ContentDetailMix(
	int FullFileCount,
	int CompactFileCount,
	int SignaturesFileCount,
	int MatchedPatternCount,
	int TotalPatternCount,
	IReadOnlyList<string> UnmatchedPatterns)
{
	public static ContentDetailMix Create(
		ContentDetailPolicy policy,
		string sourceRoot,
		IReadOnlyList<string> orderedFilePaths,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(policy);
		ArgumentNullException.ThrowIfNull(orderedFilePaths);

		var patterns = policy.Overrides.SelectMany(entry => entry.Patterns.Patterns).ToArray();
		var matched = new bool[patterns.Length];
		var full = 0;
		var compact = 0;
		var signatures = 0;
		foreach (var path in orderedFilePaths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var relativePath = ContentDetailPolicy.ToProjectRelativePath(sourceRoot, path);
			switch (ContentDetailLevelTokens.FromKinds(policy.KindsFor(relativePath)))
			{
				case ContentDetailLevel.Full:
					full++;
					break;
				case ContentDetailLevel.Compact:
					compact++;
					break;
				default:
					signatures++;
					break;
			}

			// Every pattern is probed, not just the winning entry: the report says which supplied
			// masks claimed nothing, and the last-match rule means an earlier match is still a match.
			var patternIndex = 0;
			foreach (var entry in policy.Overrides)
			{
				for (var withinEntry = 0; withinEntry < entry.Patterns.Patterns.Count; withinEntry++)
				{
					if (!matched[patternIndex] && entry.Patterns.MatchesPattern(withinEntry, relativePath))
						matched[patternIndex] = true;
					patternIndex++;
				}
			}
		}

		var unmatched = new List<string>();
		for (var index = 0; index < patterns.Length; index++)
		{
			if (!matched[index])
				unmatched.Add(patterns[index]);
		}

		return new ContentDetailMix(
			full,
			compact,
			signatures,
			patterns.Length - unmatched.Count,
			patterns.Length,
			unmatched);
	}
}

/// <summary>The reported detail tier of one file, derived from its effective transform kinds.</summary>
public enum ContentDetailLevel
{
	Full,
	Compact,
	Signatures
}

public static class ContentDetailLevelTokens
{
	public static string ToToken(ContentDetailLevel level) => level switch
	{
		ContentDetailLevel.Full => "full",
		ContentDetailLevel.Compact => "compact",
		ContentDetailLevel.Signatures => "signatures",
		_ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
	};

	/// <summary>The tier a set of effective kinds corresponds to.</summary>
	public static ContentDetailLevel FromKinds(CodeTransformKinds kinds) =>
		kinds.HasFlag(CodeTransformKinds.Bodies)
			? ContentDetailLevel.Signatures
			: kinds != CodeTransformKinds.None
				? ContentDetailLevel.Compact
				: ContentDetailLevel.Full;
}
