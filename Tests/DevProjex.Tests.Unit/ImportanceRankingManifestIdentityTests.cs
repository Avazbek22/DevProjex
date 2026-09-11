using DevProjex.Application.Dependencies;
using DevProjex.Application.Diagnostics;
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

	[Fact]
	public async Task RankingReadsEachSelectedFileOnce()
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
		using var measurement = ContentPipelineDiagnostics.BeginMeasurement();

		var report = await ranking.RankAsync(
			root,
			[project, target, source],
			TestContext.Current.CancellationToken);
		var diagnostics = measurement.Capture();

		// One pass over each candidate. The second observation of a file is the digest of the
		// bytes indexing decoded, so it costs no further read.
		Assert.Equal(3, diagnostics.SourceVersionHashPasses);
		Assert.Equal(
			new FileInfo(project).Length + new FileInfo(target).Length + new FileInfo(source).Length,
			diagnostics.SourceVersionHashBytes);
		Assert.Equal(
			DependencySourceObservationKind.Read,
			report.SourceObservations[Path.GetFullPath(source)].Kind);
	}

	[Fact]
	public async Task ContentRewrittenUnderAnUnchangedStampBeforeEveryReadIsRejected()
	{
		using var workspace = new TemporaryDirectory();
		var root = workspace.CreateFolder("project");
		var project = workspace.CreateFile("project/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var target = workspace.CreateFile("project/Target.cs", "public class Target { }");
		var source = workspace.CreateFile("project/Use.cs", Revision(0));
		var rewriter = new SameStampRewritingOpener(source, int.MaxValue);
		using var engine = new DependencyFactsEngine(
			new TreeSitterDependencyFactExtractor(rewriter.Open),
			new FileDependencyConfigurationProvider());
		var ranking = new ImportanceRankingService(engine, new UnavailableHistoryReader());

		var failure = await Assert.ThrowsAsync<IOException>(async () => await ranking.RankAsync(
			root,
			[project, target, source],
			TestContext.Current.CancellationToken));

		// Length, write time and creation time are unchanged across every rewrite, so only a
		// comparison of content against content can see them.
		rewriter.SkipUnlessStampPreserved();
		Assert.Equal(
			"Selected source files changed while dependency facts were being indexed.",
			failure.Message);
	}

	[Fact]
	public async Task ContentRewrittenUnderAnUnchangedStampBeforeTheFirstReadIsRetried()
	{
		using var workspace = new TemporaryDirectory();
		var root = workspace.CreateFolder("project");
		var project = workspace.CreateFile("project/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var target = workspace.CreateFile("project/Target.cs", "public class Target { }");
		var source = workspace.CreateFile("project/Use.cs", Revision(0));
		var rewriter = new SameStampRewritingOpener(source, 1);
		using var engine = new DependencyFactsEngine(
			new TreeSitterDependencyFactExtractor(rewriter.Open),
			new FileDependencyConfigurationProvider());
		var ranking = new ImportanceRankingService(engine, new UnavailableHistoryReader());

		var report = await ranking.RankAsync(
			root,
			[project, target, source],
			TestContext.Current.CancellationToken);

		rewriter.SkipUnlessStampPreserved();
		Assert.Equal(1, rewriter.Applied);
		var key = Path.GetFullPath(source);
		var observation = report.SourceObservations[key];
		Assert.Equal(DependencySourceObservationKind.Read, observation.Kind);
		Assert.Equal(report.SourceVersions[key].ContentHash, observation.ContentDigest);
		Assert.Equal(Revision(1), await File.ReadAllTextAsync(source, TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task AnUnreadableSelectedFileLeavesAnUnchangedSelectionRankable()
	{
		using var workspace = new TemporaryDirectory();
		var root = workspace.CreateFolder("project");
		var project = workspace.CreateFile("project/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var target = workspace.CreateFile("project/Target.cs", "public class Target { }");
		var locked = workspace.CreateFile("project/Locked.cs", "public class Locked { Target Value; }");
		using var engine = new DependencyFactsEngine(
			new TreeSitterDependencyFactExtractor(),
			new FileDependencyConfigurationProvider());
		var ranking = new ImportanceRankingService(engine, new UnavailableHistoryReader());
		using var exclusive = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

		var report = await ranking.RankAsync(
			root,
			[project, target, locked],
			TestContext.Current.CancellationToken);

		// Neither side could look at the bytes, so there is nothing to disagree about and the
		// selection ranks as it did before.
		var key = Path.GetFullPath(locked);
		Assert.Equal(3, report.Entries.Count);
		Assert.Null(report.SourceVersions[key].ContentHash);
		Assert.Equal(DependencySourceObservationKind.None, report.SourceObservations[key].Kind);
	}

	[Fact]
	public async Task ASameStampRewriteBetweenRankingsReplacesThePreparedEntryAndIsReadAgain()
	{
		using var workspace = new TemporaryDirectory();
		var root = workspace.CreateFolder("project");
		var project = workspace.CreateFile("project/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var target = workspace.CreateFile("project/Target.cs", "public class Target { }");
		var source = workspace.CreateFile("project/Use.cs", Revision(0));
		using var engine = new DependencyFactsEngine(
			new TreeSitterDependencyFactExtractor(),
			new FileDependencyConfigurationProvider());
		var ranking = new ImportanceRankingService(engine, new UnavailableHistoryReader());

		var first = await ranking.RankAsync(
			root,
			[project, target, source],
			TestContext.Current.CancellationToken);
		ReplaceWithSameFileStampOrSkip(source, Revision(1));
		var second = await ranking.RankAsync(
			root,
			[project, target, source],
			TestContext.Current.CancellationToken);

		// The cache key still matches the file stamp, but the supplied identity no longer matches
		// the entry, so the entry goes and the file is read again instead of answered from it.
		var key = Path.GetFullPath(source);
		Assert.NotEqual(first.SourceVersions[key].ContentHash, second.SourceVersions[key].ContentHash);
		Assert.Equal(DependencySourceObservationKind.Read, second.SourceObservations[key].Kind);
		Assert.Equal(second.SourceVersions[key].ContentHash, second.SourceObservations[key].ContentDigest);
	}

	[Fact]
	public async Task AnUnchangedSecondRankingReportsNoSecondObservation()
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

		var cold = await ranking.RankAsync(
			root,
			[project, target, source],
			TestContext.Current.CancellationToken);
		var warm = await ranking.RankAsync(
			root,
			[project, target, source],
			TestContext.Current.CancellationToken);

		// A repeated ranking of an unchanged selection reads nothing, so it has no second
		// observation to offer and does not pretend otherwise. Only a later currency check, which
		// does read, can speak for these files.
		Assert.Equal(
			DependencySourceObservationKind.Read,
			cold.SourceObservations[Path.GetFullPath(source)].Kind);
		Assert.DoesNotContain(
			warm.SourceObservations.Values,
			static observation => observation.Kind == DependencySourceObservationKind.Read);
	}

	[Fact]
	public async Task APreparationReusedForAWiderSelectionIsReportedAsReuse()
	{
		using var workspace = new TemporaryDirectory();
		var root = workspace.CreateFolder("project");
		var project = workspace.CreateFile("project/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var target = workspace.CreateFile("project/Target.cs", "public class Target { }");
		var source = workspace.CreateFile("project/Use.cs", "public class Use { Target Value; }");
		var added = workspace.CreateFile("project/Added.cs", "public class Added { Target Value; }");
		using var engine = new DependencyFactsEngine(
			new TreeSitterDependencyFactExtractor(),
			new FileDependencyConfigurationProvider());
		var ranking = new ImportanceRankingService(engine, new UnavailableHistoryReader());

		_ = await ranking.RankAsync(
			root,
			[project, target, source],
			TestContext.Current.CancellationToken);
		var wider = await ranking.RankAsync(
			root,
			[project, target, source, added],
			TestContext.Current.CancellationToken);

		// The file whose preparation was reused carries no digest: what the cache holds came from
		// an earlier read, and repeating it here would be an echo, not an observation.
		var reused = wider.SourceObservations[Path.GetFullPath(source)];
		Assert.Equal(DependencySourceObservationKind.ReusedPreparation, reused.Kind);
		Assert.Null(reused.ContentDigest);
		Assert.Equal(
			DependencySourceObservationKind.Read,
			wider.SourceObservations[Path.GetFullPath(added)].Kind);
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

	private static string Revision(int index) =>
		string.Create(CultureInfo.InvariantCulture, $"public class Use {{ Target Value; }} /*{index:D4}*/");

	/// <summary>
	/// Rewrites one file with fresh content of the same length and restores its stamps, in the
	/// moment between a caller hashing the file and the extractor opening it.
	/// </summary>
	private sealed class SameStampRewritingOpener(string target, int rewrites)
	{
		private string? _stampFailure;

		public int Applied { get; private set; }

		public FileStream Open(string path, int bufferSize, FileShare fileShare, bool asynchronous)
		{
			if (Applied < rewrites &&
			    PathComparer.Default.Equals(Path.GetFullPath(path), Path.GetFullPath(target)))
				Rewrite();
			return new FileStream(
				path,
				new FileStreamOptions
				{
					Mode = FileMode.Open,
					Access = FileAccess.Read,
					Share = fileShare,
					BufferSize = Math.Max(bufferSize, 1),
					Options = asynchronous ? FileOptions.Asynchronous : FileOptions.None
				});
		}

		public void SkipUnlessStampPreserved()
		{
			if (_stampFailure is not null)
				Assert.Skip(_stampFailure);
		}

		private void Rewrite()
		{
			var before = new FileInfo(target);
			var length = before.Length;
			var lastWrite = before.LastWriteTimeUtc;
			var creation = before.CreationTimeUtc;
			Applied++;
			try
			{
				File.WriteAllText(target, Revision(Applied), new UTF8Encoding(false));
				File.SetLastWriteTimeUtc(target, lastWrite);
				File.SetCreationTimeUtc(target, creation);
			}
			catch (Exception exception) when (
				exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
			{
				_stampFailure ??= $"The file system cannot restore write timestamps: {exception.Message}";
				return;
			}

			var after = new FileInfo(target);
			if (after.Length != length || after.LastWriteTimeUtc != lastWrite || after.CreationTimeUtc != creation)
			{
				_stampFailure ??=
					$"The file system did not preserve the complete file stamp: " +
					$"length {length}/{after.Length}, mtime {lastWrite:o}/{after.LastWriteTimeUtc:o}, " +
					$"creation {creation:o}/{after.CreationTimeUtc:o}.";
			}
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
