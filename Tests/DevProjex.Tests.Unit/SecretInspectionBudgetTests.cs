using DevProjex.Application.Secrets;
using DevProjex.Application.Compression;
using DevProjex.Infrastructure.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class SecretInspectionBudgetTests
{
	[Fact]
	public void StructuredDetector_StopsBeforePublishingMoreThanPerFileLimit()
	{
		var content = string.Concat(
			Enumerable.Range(0, SecretInspectionLimits.MaximumFindingsPerFile + 1)
				.Select(index => $"PASSWORD=secret-{index:D8}\n"));

		var exception = Assert.Throws<SecretInspectionBudgetExceededException>(() =>
			StructuredSecretDetector.Detect(
				".env",
				content,
				SmartSecretStack.None,
				TestContext.Current.CancellationToken));

		Assert.Equal(
			nameof(SecretInspectionLimits.MaximumFindingsPerFile),
			exception.LimitName);
	}

	[Fact]
	public void DetectorDeadline_IsCheckedAcrossCandidateRuleWork()
	{
		var detector = new GitleaksSecretDetector();
		var budget = new SecretFileInspectionBudget(TimeSpan.Zero);

		var exception = Assert.Throws<SecretInspectionBudgetExceededException>(() =>
			detector.Detect(
				"config.txt",
				"token=value".AsSpan(),
				budget,
				TestContext.Current.CancellationToken));

		Assert.Equal(
			nameof(SecretInspectionLimits.MaximumDetectorTimePerFile),
			exception.LimitName);
	}

	[Fact]
	public void OutputBudget_AllowsTheLimitAndRejectsTheNextFinding()
	{
		var budget = new SecretOutputInspectionBudget();
		for (var index = 0;
		     index < SecretInspectionLimits.MaximumFindingsPerOutput /
		     SecretInspectionLimits.MaximumFindingsPerFile;
		     index++)
		{
			budget.RegisterFindings(SecretInspectionLimits.MaximumFindingsPerFile);
		}

		var exception = Assert.Throws<SecretInspectionBudgetExceededException>(() =>
			budget.RegisterFindings(1));

		Assert.Equal(
			nameof(SecretInspectionLimits.MaximumFindingsPerOutput),
			exception.LimitName);
	}

	[Fact]
	public void PersistentMatcher_RejectsExcessMarksAndDistinctLengthsBeforeScanning()
	{
		var excessiveCount = Enumerable
			.Range(0, SecretInspectionLimits.MaximumPersistentMarksPerProject + 1)
			.Select(index => new MarkedSecretProfileEntry($"{index:X12}", null, 12))
			.ToArray();
		var countException = Assert.Throws<SecretInspectionBudgetExceededException>(() =>
			new MarkedSecretsMatcher(excessiveCount, []));
		Assert.Equal(
			nameof(SecretInspectionLimits.MaximumPersistentMarksPerProject),
			countException.LimitName);

		var excessiveLengths = Enumerable
			.Range(
				MarkedSecretValueNormalizer.MinimumLength,
				SecretInspectionLimits.MaximumDistinctPersistentMarkLengths + 1)
			.Select((length, index) => new MarkedSecretProfileEntry($"{index:X12}", null, length))
			.ToArray();
		var lengthException = Assert.Throws<SecretInspectionBudgetExceededException>(() =>
			new MarkedSecretsMatcher(excessiveLengths, []));
		Assert.Equal(
			nameof(SecretInspectionLimits.MaximumDistinctPersistentMarkLengths),
			lengthException.LimitName);
	}

	[Fact]
	public void MatcherWorkBudget_IsDeterministicAndCancellationHasPriority()
	{
		var budget = new SecretFileInspectionBudget();
		budget.RegisterMatcherWork(
			SecretInspectionLimits.MaximumPersistentMatcherWorkUnits,
			TestContext.Current.CancellationToken);
		var exception = Assert.Throws<SecretInspectionBudgetExceededException>(() =>
			budget.RegisterMatcherWork(1, TestContext.Current.CancellationToken));
		Assert.Equal(
			nameof(SecretInspectionLimits.MaximumPersistentMatcherWorkUnits),
			exception.LimitName);

		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		Assert.Throws<OperationCanceledException>(() =>
			budget.RegisterMatcherWork(1, cancellation.Token));
	}

	[Fact]
	public void BudgetCounters_RejectOverflowWithoutWrappingBelowTheLimit()
	{
		var findings = new SecretFileInspectionBudget();
		findings.RegisterFinding(TestContext.Current.CancellationToken);
		var findingsException = Assert.Throws<SecretInspectionBudgetExceededException>(() =>
			findings.RegisterFindings(int.MaxValue, TestContext.Current.CancellationToken));
		Assert.Equal(
			nameof(SecretInspectionLimits.MaximumFindingsPerFile),
			findingsException.LimitName);

		var work = new SecretFileInspectionBudget();
		work.RegisterMatcherWork(1, TestContext.Current.CancellationToken);
		var workException = Assert.Throws<SecretInspectionBudgetExceededException>(() =>
			work.RegisterMatcherWork(long.MaxValue, TestContext.Current.CancellationToken));
		Assert.Equal(
			nameof(SecretInspectionLimits.MaximumPersistentMatcherWorkUnits),
			workException.LimitName);
	}

	[Fact]
	public async Task StrictPreparation_BudgetFailureReturnsNoPreparedOutputAndDeletesTemporaryFiles()
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("secrets.env", "content");
		using var session = new SecretRedactionSession(new ExcessiveFindingsDetector());
		var context = new ContentTransformationContext(
			Redaction: new SecretRedactionContext(workspace.Path, session),
			Compression: null);
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());
		var before = EnumeratePreparedDirectories();

		var exception = await Assert.ThrowsAsync<SecretInspectionBudgetExceededException>(() =>
			preparer.PrepareAsync(context, [path], TestContext.Current.CancellationToken));

		Assert.Equal(
			nameof(SecretInspectionLimits.MaximumFindingsPerFile),
			exception.LimitName);
		Assert.Equal(before, EnumeratePreparedDirectories());
	}

	[Fact]
	public async Task StrictPreparation_AggregateOutputBudgetFailureDeletesPreviouslyPreparedFiles()
	{
		using var workspace = new TemporaryDirectory();
		var content = new string('x', SecretInspectionLimits.MaximumFindingsPerFile * 2);
		var paths = Enumerable
			.Range(
				0,
				SecretInspectionLimits.MaximumFindingsPerOutput /
				SecretInspectionLimits.MaximumFindingsPerFile + 1)
			.Select(index => workspace.CreateFile($"secret-{index:D2}.txt", content))
			.ToArray();
		using var session = new SecretRedactionSession(new MaximumFindingsDetector());
		var context = new ContentTransformationContext(
			Redaction: new SecretRedactionContext(workspace.Path, session),
			Compression: null);
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());
		var before = EnumeratePreparedDirectories();

		var exception = await Assert.ThrowsAsync<SecretInspectionBudgetExceededException>(() =>
			preparer.PrepareAsync(context, paths, TestContext.Current.CancellationToken));

		Assert.Equal(
			nameof(SecretInspectionLimits.MaximumFindingsPerOutput),
			exception.LimitName);
		Assert.Equal(before, EnumeratePreparedDirectories());
	}

	[Fact]
	public async Task Discovery_BudgetFailurePublishesFailedCoverageWithoutPartialCounts()
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("secrets.env", "content");
		using var session = new SecretRedactionSession(new ExcessiveFindingsDetector());
		var context = new SecretRedactionContext(workspace.Path, session);
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());

		var snapshot = await preparer.DiscoverAsync(
			context,
			[path],
			TestContext.Current.CancellationToken);

		Assert.Equal(0, snapshot.DetectedCount);
		Assert.Equal(0, snapshot.RedactedCount);
		Assert.Equal(1, snapshot.FailedFileCount);
		Assert.True(snapshot.HasFailures);
		Assert.False(snapshot.IsComplete);
	}

	private static string[] EnumeratePreparedDirectories()
	{
		var temp = Path.GetTempPath();
		return Directory
			.EnumerateDirectories(temp, "DevProjex-SecretRedaction-*")
			.OrderBy(static path => path, StringComparer.Ordinal)
			.ToArray();
	}

	private sealed class ExcessiveFindingsDetector : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) =>
			Enumerable
				.Range(0, SecretInspectionLimits.MaximumFindingsPerFile + 1)
				.Select(index => new DetectedSecret("test", 0, 1, "c", index))
				.ToArray();
	}

	private sealed class MaximumFindingsDetector : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) =>
			Enumerable
				.Range(0, SecretInspectionLimits.MaximumFindingsPerFile)
				.Select(index => new DetectedSecret("test", index * 2, 1, "x", 0))
				.ToArray();
	}
}
