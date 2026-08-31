namespace DevProjex.Tests.Unit;

public sealed class ProjectRootFactsIdentityTests
{
	[Fact]
	public void RegularEntriesKeepExactIdentityAndRejectAmbiguousAliases()
	{
		var facts = CreateFacts(
			files:
			[
				new ProjectRootFileFact("Config", ".txt"),
				new ProjectRootFileFact("config", ".txt")
			],
			directories:
			[
				new ProjectRootDirectoryFact("Foo", "/project/Foo", IsReparsePoint: false),
				new ProjectRootDirectoryFact("foo", "/project/foo", IsReparsePoint: false)
			]);

		Assert.True(facts.HasFile("Config"));
		Assert.True(facts.HasFile("config"));
		Assert.False(facts.HasFile("CONFIG"));
		Assert.True(facts.TryGetDirectory("Foo", out var upper));
		Assert.True(facts.TryGetDirectory("foo", out var lower));
		Assert.Equal("/project/Foo", upper.FullPath);
		Assert.Equal("/project/foo", lower.FullPath);
		Assert.False(facts.TryGetDirectory("FOO", out _));
	}

	[Fact]
	public void UniqueRegularEntryAliasRetainsWindowsCompatibility()
	{
		var facts = CreateFacts(
			files: [new ProjectRootFileFact("Config", ".txt")],
			directories: [new ProjectRootDirectoryFact("Foo", "/project/Foo", IsReparsePoint: false)]);

		Assert.Equal(OperatingSystem.IsWindows(), facts.HasFile("config"));
		Assert.Equal(OperatingSystem.IsWindows(), facts.TryGetDirectory("foo", out _));
	}

	[Fact]
	public void MarkerLookupsRemainCaseInsensitiveAndRespectReparsePolicy()
	{
		var facts = CreateFacts(
			files: [new ProjectRootFileFact("PACKAGE.JSON", ".JSON")],
			directories:
			[
				new ProjectRootDirectoryFact("NODE_MODULES", "/project/NODE_MODULES", IsReparsePoint: false),
				new ProjectRootDirectoryFact("VENDOR", "/project/VENDOR", IsReparsePoint: true)
			]);

		Assert.True(facts.HasMarkerFile("package.json"));
		Assert.True(facts.HasAnyDirectoryName(["node_modules"]));
		Assert.False(facts.HasAnyDirectoryName(["vendor"]));
		Assert.True(facts.HasAnyDirectoryName(["vendor"], includeReparsePoints: true));
	}

	[Fact]
	public void IndexedLookupsPreserveCaseDistinctIdentity()
	{
		var files = Enumerable.Range(0, 128)
			.Select(static index => new ProjectRootFileFact($"File{index:D3}", ".txt"))
			.Append(new ProjectRootFileFact("FILE000", ".txt"))
			.ToArray();
		var directories = Enumerable.Range(0, 128)
			.Select(static index => new ProjectRootDirectoryFact(
				$"Folder{index:D3}",
				$"/project/Folder{index:D3}",
				IsReparsePoint: false))
			.Append(new ProjectRootDirectoryFact("FOLDER000", "/project/FOLDER000", IsReparsePoint: false))
			.ToArray();
		var facts = CreateFacts(files, directories);

		Assert.True(facts.HasFile("File000"));
		Assert.True(facts.HasFile("FILE000"));
		Assert.False(facts.HasFile("file000"));
		Assert.True(facts.TryGetDirectory("Folder000", out var lower));
		Assert.True(facts.TryGetDirectory("FOLDER000", out var upper));
		Assert.Equal("/project/Folder000", lower.FullPath);
		Assert.Equal("/project/FOLDER000", upper.FullPath);
		Assert.False(facts.TryGetDirectory("folder000", out _));
	}

	private static ProjectRootFacts CreateFacts(
		IReadOnlyList<ProjectRootFileFact> files,
		IReadOnlyList<ProjectRootDirectoryFact> directories) =>
		new(
			"/project",
			exists: true,
			isAccessible: true,
			files,
			directories,
			gitIgnoreSignature: null);
}
