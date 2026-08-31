namespace DevProjex.Kernel.Models;

public enum PersistentSecretMarkStoreStatus
{
	Success = 0,
	TemporarilyUnavailable = 1,
	InvalidStorage = 2,
	InvalidProjectPath = 3,
	WriteFailed = 4,
	UnsupportedFutureSchema = 5
}

public readonly record struct PersistentSecretMarkId(
	string Hash,
	int Length,
	string? RelativePath = null,
	int? SourceOffset = null,
	ManualRedactionClass Class = ManualRedactionClass.Secret)
{
	public bool Equals(PersistentSecretMarkId other) =>
		StringComparer.OrdinalIgnoreCase.Equals(Hash, other.Hash) &&
		Length == other.Length &&
		ProjectTreePathIdentity.CanonicalComparer.Equals(RelativePath, other.RelativePath) &&
		SourceOffset == other.SourceOffset &&
		Class == other.Class;

	public override int GetHashCode() => HashCode.Combine(
		StringComparer.OrdinalIgnoreCase.GetHashCode(Hash ?? string.Empty),
		Length,
		RelativePath is null ? 0 : ProjectTreePathIdentity.CanonicalComparer.GetHashCode(RelativePath),
		SourceOffset,
		Class);
}

public enum PersistentSecretMarkDeltaKind
{
	Add = 0,
	Remove = 1,
	Replace = 2
}

public readonly record struct PersistentMarkStageResult(
	bool Staged,
	bool EffectiveChanged);

public sealed record PersistentSecretMarkDelta(
	Guid OperationId,
	long IssuedUtcTicks,
	long ObservedRevision,
	PersistentSecretMarkDeltaKind Kind,
	PersistentSecretMarkId MarkId,
	MarkedSecretProfileEntry? Mark)
{
	private static long _lastIssuedUtcTicks = DateTime.UtcNow.Ticks;

	public static PersistentSecretMarkDelta Add(MarkedSecretProfileEntry mark) => Add(mark, 0);

	public static PersistentSecretMarkDelta Add(MarkedSecretProfileEntry mark, long observedRevision)
	{
		ArgumentNullException.ThrowIfNull(mark);
		return new PersistentSecretMarkDelta(
			Guid.NewGuid(),
			NextIssuedUtcTicks(),
			observedRevision,
			PersistentSecretMarkDeltaKind.Add,
			new PersistentSecretMarkId(
				mark.H,
				mark.Length,
				mark.RelativePath,
				mark.SourceOffset,
				mark.Class),
			mark);
	}

	public static PersistentSecretMarkDelta Remove(PersistentSecretMarkId markId) => Remove(markId, 0);

	public static PersistentSecretMarkDelta Remove(PersistentSecretMarkId markId, long observedRevision) => new(
		Guid.NewGuid(),
		NextIssuedUtcTicks(),
		observedRevision,
		PersistentSecretMarkDeltaKind.Remove,
		markId,
		null);

	public static PersistentSecretMarkDelta Replace(
		PersistentSecretMarkId existingMarkId,
		MarkedSecretProfileEntry replacement) => Replace(existingMarkId, replacement, 0);

	public static PersistentSecretMarkDelta Replace(
		PersistentSecretMarkId existingMarkId,
		MarkedSecretProfileEntry replacement,
		long observedRevision)
	{
		ArgumentNullException.ThrowIfNull(replacement);
		return new PersistentSecretMarkDelta(
			Guid.NewGuid(),
			NextIssuedUtcTicks(),
			observedRevision,
			PersistentSecretMarkDeltaKind.Replace,
			existingMarkId,
			replacement);
	}

	private static long NextIssuedUtcTicks()
	{
		while (true)
		{
			var observed = Volatile.Read(ref _lastIssuedUtcTicks);
			var candidate = CalculateNextIssuedUtcTicks(observed, DateTime.UtcNow.Ticks);
			if (Interlocked.CompareExchange(ref _lastIssuedUtcTicks, candidate, observed) == observed)
				return candidate;
		}
	}

	internal static long CalculateNextIssuedUtcTicks(long observed, long utcNowTicks)
	{
		if (observed == long.MaxValue)
			throw new InvalidOperationException("The persistent mark operation clock is exhausted.");

		return Math.Max(utcNowTicks, observed + 1);
	}
}

public sealed record PersistentSecretMarksSnapshot(
	long Revision,
	IReadOnlyCollection<MarkedSecretProfileEntry> Marks,
	IReadOnlyDictionary<PersistentSecretMarkId, long>? StateAppliedRevisions = null)
{
	public static PersistentSecretMarksSnapshot Empty { get; } = new(
		0,
		[],
		new Dictionary<PersistentSecretMarkId, long>());
}

public sealed record PersistentSecretMarksLoadResult(
	PersistentSecretMarkStoreStatus Status,
	PersistentSecretMarksSnapshot? Snapshot)
{
	public bool Succeeded => Status == PersistentSecretMarkStoreStatus.Success && Snapshot is not null;
}

public sealed record PersistentSecretMarkWriteResult(
	PersistentSecretMarkStoreStatus Status,
	PersistentSecretMarksSnapshot? Snapshot)
{
	public bool Succeeded => Status == PersistentSecretMarkStoreStatus.Success && Snapshot is not null;
}
