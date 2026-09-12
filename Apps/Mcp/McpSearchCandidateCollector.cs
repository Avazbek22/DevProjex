namespace DevProjex.Mcp;

/// <summary>
/// Keeps the strongest bounded set of matches seen so far. Every comparison is derived from the
/// candidate itself, so replacing a weak early match with a stronger late one is independent of
/// directory traversal order.
/// </summary>
internal sealed class McpSearchCandidateCollector(int capacity, int characterCapacity)
{
	internal const int ExplicitScopeWeight = 1024;
	internal const int FileDiversityWeight = 512;
	internal const int FullyQualifiedDeclarationWeight = 160;
	internal const int SimpleDeclarationWeight = 128;
	internal const int DeclarationHintWeight = 64;
	internal const int OwnerDiversityWeight = 32;
	internal const int MaximumRepetitionPenalty = 31;

	private readonly SortedSet<McpSearchCandidate> candidates = new(McpSearchCandidateComparer.Instance);
	private int retainedCharacters;

	public int Count => candidates.Count;
	public int RetainedCharacters => retainedCharacters;
	public bool MatchCapacityReached { get; private set; }
	public bool CharacterCapacityReached { get; private set; }

	public void Consider(McpSearchCandidate candidate)
	{
		ArgumentNullException.ThrowIfNull(candidate);
		if (capacity <= 0)
		{
			MatchCapacityReached = true;
			return;
		}
		if (candidate.StorageCharacters > characterCapacity)
		{
			CharacterCapacityReached = true;
			return;
		}
		if (!candidates.Add(candidate))
			return;
		retainedCharacters += candidate.StorageCharacters;
		while (candidates.Count > capacity || retainedCharacters > characterCapacity)
		{
			MatchCapacityReached |= candidates.Count > capacity;
			CharacterCapacityReached |= retainedCharacters > characterCapacity;
			var removed = candidates.Max!;
			candidates.Remove(removed);
			retainedCharacters -= removed.StorageCharacters;
		}
	}

	public IReadOnlyList<McpSearchCandidate> Snapshot() => candidates.ToArray();

	public IReadOnlySet<string> SelectFilesForAnnotation(int maximumFiles)
	{
		if (maximumFiles <= 0 || candidates.Count == 0)
			return new HashSet<string>(StringComparer.Ordinal);

		var selected = new HashSet<string>(StringComparer.Ordinal);
		foreach (var candidate in candidates)
		{
			if (!selected.Add(candidate.Group.RelativePath))
				continue;
			if (selected.Count == maximumFiles)
				break;
		}
		return selected;
	}

	public static int Score(
		bool explicitScope,
		int fileOccurrence,
		McpDeclarationMatchQuality declarationQuality,
		bool declarationHint,
		int ownerOccurrence,
		int repeatedContentOccurrence)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(fileOccurrence);
		ArgumentOutOfRangeException.ThrowIfNegative(ownerOccurrence);
		ArgumentOutOfRangeException.ThrowIfNegative(repeatedContentOccurrence);
		var fileDiversity = FileDiversityWeight / checked(fileOccurrence + 1);
		var ownerDiversity = OwnerDiversityWeight / checked(ownerOccurrence + 1);
		var declaration = declarationQuality switch
		{
			McpDeclarationMatchQuality.FullyQualified => FullyQualifiedDeclarationWeight,
			McpDeclarationMatchQuality.SimpleName => SimpleDeclarationWeight,
			_ => 0
		};
		var repetitionPenalty = Math.Min(MaximumRepetitionPenalty, repeatedContentOccurrence);
		return (explicitScope ? ExplicitScopeWeight : 0) +
		       fileDiversity + declaration + (declarationHint ? DeclarationHintWeight : 0) +
		       ownerDiversity - repetitionPenalty;
	}

	private sealed class McpSearchCandidateComparer : IComparer<McpSearchCandidate>
	{
		public static readonly McpSearchCandidateComparer Instance = new();

		public int Compare(McpSearchCandidate? left, McpSearchCandidate? right)
		{
			if (ReferenceEquals(left, right))
				return 0;
			if (left is null)
				return 1;
			if (right is null)
				return -1;

			var score = right.Score.CompareTo(left.Score);
			if (score != 0)
				return score;
			var path = StringComparer.Ordinal.Compare(left.Group.RelativePath, right.Group.RelativePath);
			if (path != 0)
				return path;
			var line = left.MatchLine.CompareTo(right.MatchLine);
			if (line != 0)
				return line;
			return StringComparer.Ordinal.Compare(left.StableText, right.StableText);
		}
	}
}

internal enum McpDeclarationMatchQuality
{
	None,
	SimpleName,
	FullyQualified
}

internal sealed record McpSearchCandidate(
	McpSearchRenderedGroup Group,
	int MatchLine,
	int Score,
	string StableText,
	int StorageCharacters);

internal readonly record struct McpSearchBoundary(
	int EligibleSources,
	int InspectedSources,
	int EncounteredMatches,
	int RetainedMatches,
	int WrittenMatches,
	int NamedDeclarationFiles,
	bool InspectionByteLimitReached,
	bool RetainedMatchLimitReached,
	bool AnnotationFileLimitReached,
	bool ResponseCharacterLimitReached,
	bool RequestResultLimitReached,
	bool RetainedCharacterLimitReached,
	bool StoredCharacterLimitReached,
	int UnscannableSources)
{
	public bool IsComplete =>
		!InspectionByteLimitReached &&
		!RetainedMatchLimitReached &&
		!AnnotationFileLimitReached &&
		!ResponseCharacterLimitReached &&
		!RequestResultLimitReached &&
		!RetainedCharacterLimitReached &&
		!StoredCharacterLimitReached &&
		UnscannableSources == 0;
}
