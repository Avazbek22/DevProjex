using DevProjex.Application.Dependencies;
using DevProjex.Application.Ranking;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Unit;

public sealed class ImportanceRankingManifestIdentityTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task SameStampReplacementRefreshesEdgesWithAndWithoutFocus(bool focus)
	{
		using var workspace = new TemporaryDirectory();
		var root = workspace.CreateFolder("project");
		var project = workspace.CreateFile("project/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var alpha = workspace.CreateFile("project/Alpha.cs", "public class Alpha { }");
		var bravo = workspace.CreateFile("project/Bravo.cs", "public class Bravo { }");
		const string original = "public class Use { Alpha Value; }";
		const string replacement = "public class Use { Bravo Value; }";
		Assert.Equal(Encoding.UTF8.GetByteCount(original), Encoding.UTF8.GetByteCount(replacement));
		var source = workspace.CreateFile("project/Use.cs", original);
		var candidates = new[] { project, alpha, bravo, source };
		using var engine = new DependencyFactsEngine(
			new TreeSitterDependencyFactExtractor(),
			new FileDependencyConfigurationProvider());
		var ranking = new ImportanceRankingService(engine, new UnavailableHistoryReader());

		var first = await RankAsync(ranking, root, candidates, source, focus);
		ReplaceWithSameFileStampOrSkip(source, replacement);
		var second = await RankAsync(ranking, root, candidates, source, focus);

		Assert.Equal(1, Entry(first, "Alpha.cs").Dependents);
		Assert.Equal(0, Entry(first, "Bravo.cs").Dependents);
		Assert.Equal(0, Entry(second, "Alpha.cs").Dependents);
		Assert.Equal(1, Entry(second, "Bravo.cs").Dependents);
		if (focus)
		{
			Assert.Equal(1, Entry(first, "Alpha.cs").Hop);
			Assert.Null(Entry(first, "Bravo.cs").Hop);
			Assert.Null(Entry(second, "Alpha.cs").Hop);
			Assert.Equal(1, Entry(second, "Bravo.cs").Hop);
		}
	}

	[Fact]
	public async Task UnchangedSecondRankingRequestHitsResolutionCache()
	{
		using var workspace = new TemporaryDirectory();
		var root = workspace.CreateFolder("project");
		var project = workspace.CreateFile("project/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var target = workspace.CreateFile("project/Target.cs", "public class Target { }");
		var source = workspace.CreateFile("project/Use.cs", "public class Use { Target Value; }");
		using var engine = new DependencyFactsEngine(
			new TreeSitterDependencyFactExtractor(),
			new FileDependencyConfigurationProvider());
		var ranking = new ImportanceRankingService(engine, new UnavailableHistoryReader());

		_ = await ranking.RankAsync(
			root,
			[project, target, source],
			TestContext.Current.CancellationToken);
		var warm = await ranking.RankAsync(
			root,
			[project, target, source],
			TestContext.Current.CancellationToken);

		Assert.NotNull(warm.DependencyMetrics);
		Assert.True(warm.DependencyMetrics.ResolutionCacheHit);
	}

	private static Task<ImportanceRankingReport> RankAsync(
		ImportanceRankingService ranking,
		string root,
		IReadOnlyList<string> candidates,
		string source,
		bool focus) =>
		focus
			? ranking.RankAsync(
				root,
				candidates,
				new FocusRankingRequest([new FocusRankingSeedRequest("Use.cs", source)]),
				cancellationToken: TestContext.Current.CancellationToken)
			: ranking.RankAsync(root, candidates, TestContext.Current.CancellationToken);

	private static ImportanceRankingEntry Entry(ImportanceRankingReport report, string path) =>
		Assert.Single(report.Entries, entry => entry.Path == path);

	private static void ReplaceWithSameFileStampOrSkip(string path, string replacement)
	{
		var before = new FileInfo(path);
		var length = before.Length;
		var lastWrite = before.LastWriteTimeUtc;
		var creation = before.CreationTimeUtc;
		try
		{
			File.WriteAllText(path, replacement, new UTF8Encoding(false));
			File.SetLastWriteTimeUtc(path, lastWrite);
			File.SetCreationTimeUtc(path, creation);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
			Assert.Skip($"The file system cannot restore creation and write timestamps: {exception.Message}");
			return;
		}

		var after = new FileInfo(path);
		if (after.Length != length || after.LastWriteTimeUtc != lastWrite || after.CreationTimeUtc != creation)
		{
			Assert.Skip(
				$"The file system did not preserve the complete file stamp: " +
				$"length {length}/{after.Length}, mtime {lastWrite:o}/{after.LastWriteTimeUtc:o}, " +
				$"creation {creation:o}/{after.CreationTimeUtc:o}.");
		}
	}

	private sealed class UnavailableHistoryReader : IProjectGitHistoryReader
	{
		public Task<ProjectGitHistorySnapshot> ReadAsync(
			string sourceRoot,
			IReadOnlyList<string> candidateFiles,
			CancellationToken cancellationToken = default) =>
			Task.FromResult(new ProjectGitHistorySnapshot(
				200,
				0,
				new Dictionary<string, ProjectGitFileActivity>(PathComparer.Default),
				candidateFiles.ToDictionary(
					Path.GetFullPath,
					static _ => ProjectGitHistoryUnavailableReason.NotRepository,
					PathComparer.Default),
				ProjectGitHistoryUnavailableReason.NotRepository));
	}
}
