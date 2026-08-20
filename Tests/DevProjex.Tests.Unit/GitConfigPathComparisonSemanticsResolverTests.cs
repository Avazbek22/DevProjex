namespace DevProjex.Tests.Unit;

public sealed class GitConfigPathComparisonSemanticsResolverTests
{
	[Fact]
	public void Resolve_RetriesUnavailableRepositorySemanticsAfterBackoff()
	{
		using var workspace = new TemporaryDirectory();
		var repositoryRoot = workspace.CreateFolder("repository");
		workspace.CreateFolder("repository/.git");
		var now = new DateTime(2026, 8, 20, 1, 0, 0, DateTimeKind.Utc);
		var resolutionCount = 0;
		var resolver = new GitConfigPathComparisonSemanticsResolver(
			(_, _) => ++resolutionCount == 1
				? new GitPathComparisonSemantics(
					IgnoreCase: true,
					NormalizeUnicode: true,
					IsAuthoritative: false)
				: new GitPathComparisonSemantics(
					IgnoreCase: false,
					NormalizeUnicode: false),
			() => now,
			TimeSpan.FromMinutes(1));

		var unavailable = resolver.Resolve(repositoryRoot);
		var coalesced = resolver.Resolve(repositoryRoot);
		now = now.AddMinutes(1);
		var recovered = resolver.Resolve(repositoryRoot);
		var cached = resolver.Resolve(repositoryRoot);

		Assert.False(unavailable.IsAuthoritative);
		Assert.False(coalesced.IsAuthoritative);
		Assert.True(recovered.IsAuthoritative);
		Assert.False(recovered.IgnoreCase);
		Assert.Equal(recovered, cached);
		Assert.Equal(2, resolutionCount);
	}

	[Fact]
	public void Resolve_KeepsAuthoritativeRepositorySemanticsCached()
	{
		using var workspace = new TemporaryDirectory();
		var repositoryRoot = workspace.CreateFolder("repository");
		workspace.CreateFolder("repository/.git");
		var now = new DateTime(2026, 8, 20, 1, 0, 0, DateTimeKind.Utc);
		var resolutionCount = 0;
		var expected = new GitPathComparisonSemantics(
			IgnoreCase: true,
			NormalizeUnicode: OperatingSystem.IsMacOS());
		var resolver = new GitConfigPathComparisonSemanticsResolver(
			(_, _) =>
			{
				resolutionCount++;
				return expected;
			},
			() => now,
			TimeSpan.FromSeconds(1));

		Assert.Equal(expected, resolver.Resolve(repositoryRoot));
		now = now.AddDays(1);
		Assert.Equal(expected, resolver.Resolve(repositoryRoot));
		Assert.Equal(1, resolutionCount);
	}
}
