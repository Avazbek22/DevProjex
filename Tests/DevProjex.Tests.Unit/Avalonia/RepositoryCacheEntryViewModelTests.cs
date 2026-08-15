namespace DevProjex.Tests.Unit.Avalonia;

public sealed class RepositoryCacheEntryViewModelTests
{
	[Fact]
	public void Create_UsesOwnerAndRepositoryNameWithBranchOnlyDetailsAndMetadataToolTip()
	{
		var culture = CultureInfo.GetCultureInfo("en-US");
		var lastOpenedUtc = new DateTimeOffset(2026, 8, 14, 10, 30, 0, TimeSpan.Zero);
		var entry = CreateEntry(
			"https://github.com/acme/widgets.git",
			"widgets",
			"feature/cache",
			lastOpenedUtc,
			1536,
			RepositoryCacheContentKind.Git);

		var item = RepositoryCacheEntryViewModel.Create(
			entry,
			culture,
			"ZIP",
			"Remove",
			canDelete: true,
			"Active repository");

		Assert.Equal("acme / widgets", item.DisplayName);
		Assert.Equal("feature/cache", item.DetailsText);
		Assert.Equal(
			string.Join(
				Environment.NewLine,
				entry.RepositoryUrl,
				RepositoryCacheEntryViewModel.FormatByteSize(entry.ApproximateSizeBytes, culture),
				lastOpenedUtc.ToLocalTime().ToString("g", culture)),
			item.ToolTipText);
	}

	[Fact]
	public void Create_FallsBackToIndexedNameWhenRepositoryPresentationIsEmpty()
	{
		var entry = CreateEntry(
			string.Empty,
			"fallback-name",
			branch: null,
			DateTimeOffset.UtcNow,
			0,
			RepositoryCacheContentKind.Git);

		var item = RepositoryCacheEntryViewModel.Create(
			entry,
			CultureInfo.InvariantCulture,
			"ZIP",
			"Remove",
			canDelete: true,
			"Active repository");

		Assert.Equal("fallback-name", item.DisplayName);
		Assert.Equal("-", item.DetailsText);
	}

	[Fact]
	public void Create_PreservesZipMarkerAfterRepositoryPresentation()
	{
		var entry = CreateEntry(
			"https://github.com/acme/archive.git",
			"archive",
			"main",
			DateTimeOffset.UtcNow,
			1024,
			RepositoryCacheContentKind.Zip);

		var item = RepositoryCacheEntryViewModel.Create(
			entry,
			CultureInfo.InvariantCulture,
			"ZIP",
			"Remove",
			canDelete: true,
			"Active repository");

		Assert.Equal("acme / archive (ZIP)", item.DisplayName);
		Assert.Equal("main", item.DetailsText);
	}

	private static RepositoryCacheCatalogEntry CreateEntry(
		string repositoryUrl,
		string repositoryName,
		string? branch,
		DateTimeOffset lastOpenedUtc,
		long approximateSizeBytes,
		RepositoryCacheContentKind contentKind) => new(
			repositoryUrl,
			repositoryName,
			branch,
			lastOpenedUtc,
			approximateSizeBytes,
			contentKind,
			"c:/cache/repository");
}
