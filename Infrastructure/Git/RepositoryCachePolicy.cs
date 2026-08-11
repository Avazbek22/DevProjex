namespace DevProjex.Infrastructure.Git;

internal sealed record RepositoryCachePolicy(
	long MaximumSizeBytes,
	TimeSpan MaximumUnusedAge)
{
	// Ten GiB bounds unattended growth while leaving room for several large shallow clones.
	public const long DefaultMaximumSizeBytes = 10L * 1024 * 1024 * 1024;

	// Sixty days preserves normal revisits while aging out abandoned project snapshots.
	public static readonly TimeSpan DefaultMaximumUnusedAge = TimeSpan.FromDays(60);

	public static RepositoryCachePolicy Default { get; } = new(
		DefaultMaximumSizeBytes,
		DefaultMaximumUnusedAge);

	public RepositoryCachePolicy Validate()
	{
		if (MaximumSizeBytes <= 0)
			throw new ArgumentOutOfRangeException(nameof(MaximumSizeBytes));
		if (MaximumUnusedAge <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(MaximumUnusedAge));
		return this;
	}
}
