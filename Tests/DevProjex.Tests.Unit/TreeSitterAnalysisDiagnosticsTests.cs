using System.Diagnostics;
using DevProjex.Application.Compression;
using DevProjex.Infrastructure.Compression;

namespace DevProjex.Tests.Unit;

public sealed class TreeSitterAnalysisDiagnosticsTests
{
	[Fact]
	public void BlankLineCollection_RemainsInsideTheExistingEditShapingPhaseSchema()
	{
		using var compressor = CodeCompressionTestHarness.CreateCompressor();
		using var diagnostics = compressor.BeginAnalysisDiagnostics(topCapacity: 3);
		using var scope = compressor.CreateScope(Path.GetTempPath(), CodeTransformKinds.BlankLines);
		const string source = "internal sealed class Sample\n{\n\n    internal int Value => 42;\n}\n";

		var analysis = scope.Analyze(
			"Sample.cs",
			"Sample.cs",
			source,
			TestContext.Current.CancellationToken);
		var snapshot = diagnostics.Capture();

		Assert.Equal("internal sealed class Sample\n{\n    internal int Value => 42;\n}\n", analysis.GetResult(source).Text);
		Assert.Equal(13, TreeSitterFileAnalysisTiming.ReportedPhases.Length);
		Assert.All(
			TreeSitterFileAnalysisTiming.ReportedPhases,
			phase => Assert.Equal(1, snapshot.Phases.Single(item => item.Phase == phase).Count));
		Assert.Equal(1, snapshot.Phases.Single(
			static phase => phase.Phase == TreeSitterAnalysisPhase.EditShaping).Count);
		var file = Assert.Single(snapshot.SlowestFiles);
		Assert.True(file.Work.RawEdits > 0);
		Assert.True(file.Work.FinalEdits > 0);
		Assert.True(file.Work.OriginalVisitedNodes > 0);
		Assert.Equal(0, file.Work.PreserveCaptures);
	}

	[Fact]
	public void Analyze_ReportsAllRequiredPhasesAndPerFileWork()
	{
		using var compressor = CodeCompressionTestHarness.CreateCompressor();
		using var diagnostics = compressor.BeginAnalysisDiagnostics(topCapacity: 3);
		using var scope = compressor.CreateScope(
			Path.GetTempPath(),
			CodeTransformKinds.Bodies | CodeTransformKinds.Comments);
		const string source = """
			/// Documentation.
			public sealed class Widget
			{
			    public int Value { get; } = 42;

			    public void Run()
			    {
			        // Implementation.
			        System.Console.WriteLine(Value);
			    }
			}
			""";

		var analysis = scope.Analyze(
			"Widget.cs",
			"Widget.cs",
			source,
			TestContext.Current.CancellationToken);
		var snapshot = diagnostics.Capture();

		Assert.Equal(CodeCompressionOutcome.Compressed, analysis.Plan.Outcome);
		Assert.Equal(1, snapshot.CompletedFiles);
		Assert.Equal(0, snapshot.CancelledFiles);
		Assert.All(
			TreeSitterFileAnalysisTiming.ReportedPhases,
			phase => Assert.Equal(1, snapshot.Phases.Single(item => item.Phase == phase).Count));
		var file = Assert.Single(snapshot.SlowestFiles);
		Assert.Equal("Widget.cs", file.RelativePath);
		Assert.Equal(13, file.Phases.Count);
		Assert.True(file.Work.PreserveCaptures > 0);
		Assert.True(file.Work.BodyCaptures > 0);
		Assert.True(file.Work.CommentCaptures > 0);
		Assert.True(file.Work.OriginalDeclarations > 0);
		Assert.True(file.Work.OriginalVisitedNodes > 0);
		Assert.True(file.Work.RawEdits > 0);
		Assert.True(file.Work.FinalEdits > 0);
		Assert.True(file.Work.ReverseVisitedNodes > 0);
	}

	[Fact]
	public void Analyze_InstrumentedCaptureSpill_PreservesExactPlanOutputAndMap()
	{
		var source = CreateCaptureSpillSource();
		var cancellationToken = TestContext.Current.CancellationToken;
		using var baselineCompressor = CodeCompressionTestHarness.CreateCompressor();
		using var baselineScope = baselineCompressor.CreateScope(
			Path.GetTempPath(),
			CodeTransformKinds.Bodies | CodeTransformKinds.Comments);
		var baseline = baselineScope.Analyze(
			"Widget.cs",
			"Widget.cs",
			source,
			cancellationToken);

		using var instrumentedCompressor = CodeCompressionTestHarness.CreateCompressor();
		using var diagnostics = instrumentedCompressor.BeginAnalysisDiagnostics();
		CodeCompressionAnalysis instrumented;
		using (var instrumentedScope = instrumentedCompressor.CreateScope(
		       Path.GetTempPath(),
		       CodeTransformKinds.Bodies | CodeTransformKinds.Comments))
		{
			instrumented = instrumentedScope.Analyze(
				"Widget.cs",
				"Widget.cs",
				source,
				cancellationToken);
		}

		var snapshot = diagnostics.Capture();
		diagnostics.Dispose();
		AssertEquivalentAnalysis(source, baseline, instrumented);
		Assert.Equal(CodeCompressionOutcome.Compressed, instrumented.Plan.Outcome);
		Assert.Equal(
			CodeTransformKinds.Bodies | CodeTransformKinds.Comments,
			instrumented.Plan.AffectedKinds);
		Assert.Contains(
			instrumented.Plan.Edits,
			static edit => edit.Kinds == (CodeTransformKinds.Bodies | CodeTransformKinds.Comments));
		Assert.True(snapshot.Work.CommentCaptures > 1_500);
		Assert.True(snapshot.Work.RawEdits > snapshot.Work.FinalEdits);
		Assert.Equal(
			snapshot.Work.CommentCaptures,
			Assert.Single(snapshot.SlowestFiles).Work.CommentCaptures);

		using var afterDiagnosticsScope = instrumentedCompressor.CreateScope(
			Path.GetTempPath(),
			CodeTransformKinds.Bodies | CodeTransformKinds.Comments);
		var afterDiagnostics = afterDiagnosticsScope.Analyze(
			"Widget.cs",
			"Widget.cs",
			source,
			cancellationToken);
		AssertEquivalentAnalysis(source, baseline, afterDiagnostics);
	}

	[Fact]
	public void CancellationAfterOriginalParse_ReleasesTreeAndDoesNotPollutePhaseStatistics()
	{
		using var harness = CodeCompressionTestHarness.For("csharp");
		using var cancellation = new CancellationTokenSource();
		var observer = new CancelOnPhaseObserver(TreeSitterAnalysisPhase.OriginalParse, cancellation);
		using var compressor = new TreeSitterCodeCompressor(
			CodeCompressionTestHarness.CreateLocator(),
			[harness.Pack],
			analysisPhaseObserver: observer);
		using var diagnostics = compressor.BeginAnalysisDiagnostics();
		using var scope = compressor.CreateScope(
			Path.GetTempPath(),
			CodeTransformKinds.Bodies | CodeTransformKinds.Comments);

		Assert.ThrowsAny<OperationCanceledException>(() => scope.Analyze(
			"Widget.cs",
			"Widget.cs",
			"public sealed class Widget { public void Run() { System.Console.WriteLine(1); } }",
			cancellation.Token));

		var snapshot = diagnostics.Capture();
		Assert.Equal(0, snapshot.CompletedFiles);
		Assert.Equal(1, snapshot.CancelledFiles);
		Assert.All(snapshot.Phases, static phase => Assert.Equal(0, phase.Count));
		Assert.Empty(snapshot.SlowestFiles);
		Assert.Equal(0, compressor.RuntimeDiagnostics.LeasedWorkers);
		Assert.Equal(0, compressor.RuntimeDiagnostics.GlobalActiveWorkers);
	}

	[Fact]
	public void DeclarationQueryLimitRefusal_PreservesElapsedPhaseAndPartialCount()
	{
		using var harness = CodeCompressionTestHarness.For("javascript");
		var boundedPack = harness.Pack with
		{
			DeclarationsQuery = harness.Pack.DeclarationsQuery + """

				(array
				  (identifier) @limit_pre
				  (identifier) @limit_post)
				"""
		};
		const uint matchLimit = 32;
		using var compressor = new TreeSitterCodeCompressor(
			CodeCompressionTestHarness.CreateLocator(),
			[boundedPack],
			matchLimit);
		using var diagnostics = compressor.BeginAnalysisDiagnostics();
		using var scope = compressor.CreateScope(Path.GetTempPath(), CodeTransformKinds.Bodies);
		var identifiers = string.Join(", ", Enumerable.Range(0, 64).Select(static index => $"value{index}"));
		var source = $$"""
			const values = [{{identifiers}}];
			function work() {
			    return 42;
			}
			""";

		var analysis = scope.Analyze(
			"sample.js",
			"sample.js",
			source,
			TestContext.Current.CancellationToken);
		var snapshot = diagnostics.Capture();

		Assert.Equal(CodeCompressionOutcome.UnchangedGateRejected, analysis.Plan.Outcome);
		Assert.Equal(1, snapshot.CompletedFiles);
		Assert.Equal(
			1,
			snapshot.Phases.Single(
				static phase => phase.Phase == TreeSitterAnalysisPhase.OriginalDeclarations).Count);
		Assert.True(snapshot.Work.OriginalDeclarations > 0);
		Assert.Equal(
			1,
			snapshot.Phases.Single(
				static phase => phase.Phase == TreeSitterAnalysisPhase.OriginalDefectWalk).Count);
		Assert.Equal(
			0,
			snapshot.Phases.Single(
				static phase => phase.Phase == TreeSitterAnalysisPhase.ReverseDeclarations).Count);
		Assert.Equal(0, snapshot.Work.ReverseDeclarations);
		Assert.Equal(
			0,
			snapshot.Phases.Single(
				static phase => phase.Phase == TreeSitterAnalysisPhase.ReverseDefectWalk).Count);
	}

	[Fact]
	public void DefectLimitRefusal_PreservesElapsedPhaseAndPartialCounts()
	{
		using var compressor = CodeCompressionTestHarness.CreateCompressor();
		using var diagnostics = compressor.BeginAnalysisDiagnostics();
		using var scope = compressor.CreateScope(Path.GetTempPath(), CodeTransformKinds.Bodies);
		var sourceBuilder = new StringBuilder("class Widget {\n");
		for (var index = 0; index <= TreeSitterCodeCompressor.MaximumDefectsPerFile; index++)
		{
			sourceBuilder.Append("] void M")
				.Append(index)
				.Append("() { System.Console.WriteLine(1); }\n");
		}
		sourceBuilder.Append("}\n");
		var source = sourceBuilder.ToString();

		var analysis = scope.Analyze(
			"Widget.cs",
			"Widget.cs",
			source,
			TestContext.Current.CancellationToken);
		var snapshot = diagnostics.Capture();

		Assert.Equal(CodeCompressionOutcome.UnchangedGateRejected, analysis.Plan.Outcome);
		Assert.Equal(1, snapshot.CompletedFiles);
		Assert.Equal(
			1,
			snapshot.Phases.Single(
				static phase => phase.Phase == TreeSitterAnalysisPhase.OriginalDefectWalk).Count);
		Assert.Equal(TreeSitterCodeCompressor.MaximumDefectsPerFile, snapshot.Work.OriginalDefects);
		Assert.True(snapshot.Work.OriginalVisitedNodes > snapshot.Work.OriginalDefects);
		Assert.Equal(
			0,
			snapshot.Phases.Single(
				static phase => phase.Phase == TreeSitterAnalysisPhase.ReverseParse).Count);
	}

	[Fact]
	public void Capture_ReportsBoundedQuantilesTopFilesAndWorkCounters()
	{
		var releases = 0;
		using var diagnostics = new TreeSitterAnalysisDiagnosticsSession(
			_ => Interlocked.Increment(ref releases),
			topCapacity: 2);
		var samples = new[]
		{
			("one.cs", 1d),
			("two.cs", 2d),
			("three.cs", 3d),
			("four.cs", 4d),
			("slow.cs", 100d)
		};
		foreach (var (path, milliseconds) in samples)
		{
			var elapsedTicks = (long)Math.Ceiling(milliseconds * Stopwatch.Frequency / 1000d);
			var file = diagnostics.BeginFile(path, path.Length);
			file.RecordPhase(TreeSitterAnalysisPhase.OriginalParse, elapsedTicks);
			if (path == "slow.cs")
			{
				file.PreserveCaptures = 7;
				file.BodyCaptures = 5;
				file.CommentCaptures = 11;
				file.OriginalDeclarations = 3;
				file.OriginalDefects = 1;
				file.OriginalVisitedNodes = 120;
				file.RawEdits = 9;
				file.FinalEdits = 6;
				file.ReverseDeclarations = 3;
				file.ReverseDefects = 1;
				file.ReverseVisitedNodes = 44;
			}
			diagnostics.RecordFile(ref file);
		}
		var cancelled = diagnostics.BeginFile("cancelled.cs", 10);
		cancelled.IsCancelled = true;
		cancelled.CommentCaptures = 100;
		diagnostics.RecordFile(ref cancelled);

		var snapshot = diagnostics.Capture();
		var phase = snapshot.Phases.Single(value => value.Phase == TreeSitterAnalysisPhase.OriginalParse);

		Assert.Equal(samples.Length, phase.Count);
		Assert.InRange(phase.P50Milliseconds, 3, 3.5);
		Assert.InRange(phase.P95Milliseconds, 100, 113);
		Assert.Equal(["slow.cs", "four.cs"], phase.Top.Select(static value => value.RelativePath));
		Assert.Equal(5, snapshot.CompletedFiles);
		Assert.Equal(1, snapshot.CancelledFiles);
		Assert.Equal(["slow.cs", "four.cs"], snapshot.SlowestFiles.Select(static value => value.RelativePath));
		Assert.Equal(13, snapshot.SlowestFiles[0].Phases.Count);
		Assert.Equal(11, snapshot.SlowestFiles[0].Work.CommentCaptures);
		Assert.Equal(7, snapshot.Work.PreserveCaptures);
		Assert.Equal(5, snapshot.Work.BodyCaptures);
		Assert.Equal(11, snapshot.Work.CommentCaptures);
		Assert.Equal(3, snapshot.Work.OriginalDeclarations);
		Assert.Equal(1, snapshot.Work.OriginalDefects);
		Assert.Equal(120, snapshot.Work.OriginalVisitedNodes);
		Assert.Equal(9, snapshot.Work.RawEdits);
		Assert.Equal(6, snapshot.Work.FinalEdits);
		Assert.Equal(3, snapshot.Work.ReverseDeclarations);
		Assert.Equal(1, snapshot.Work.ReverseDefects);
		Assert.Equal(44, snapshot.Work.ReverseVisitedNodes);

		diagnostics.Dispose();
		diagnostics.Dispose();
		Assert.Equal(1, releases);
	}

	[Fact]
	public void ConcurrentRecording_PreservesExactCountsAndFixedTopCapacity()
	{
		using var diagnostics = new TreeSitterAnalysisDiagnosticsSession(_ => { }, topCapacity: 4);

		Parallel.For(0, 2_000, index =>
		{
			var elapsedTicks = Math.Max(1, index + 1L);
			var file = diagnostics.BeginFile($"file-{index:D4}.xml", index);
			file.RecordPhase(TreeSitterAnalysisPhase.CommentQuery, elapsedTicks);
			file.CommentCaptures = 1;
			diagnostics.RecordFile(ref file);
		});

		var snapshot = diagnostics.Capture();
		var phase = snapshot.Phases.Single(value => value.Phase == TreeSitterAnalysisPhase.CommentQuery);
		Assert.Equal(2_000, phase.Count);
		Assert.Equal(4, phase.Top.Count);
		Assert.Equal(4, snapshot.SlowestFiles.Count);
		Assert.Equal(2_000, snapshot.CompletedFiles);
		Assert.Equal(2_000, snapshot.Work.CommentCaptures);
		Assert.True(phase.P50Milliseconds <= phase.P95Milliseconds);
		Assert.True(phase.P95Milliseconds <= phase.P99Milliseconds);
		Assert.True(phase.P99Milliseconds <= phase.MaximumMilliseconds);
	}

	[Fact]
	public async Task Dispose_FreezesCaptureAndKeepsLateScopeDataOutOfTheNextSession()
	{
		using var harness = CodeCompressionTestHarness.For("csharp");
		using var observer = new BlockingPhaseObserver(TreeSitterAnalysisPhase.OriginalParse);
		using var compressor = new TreeSitterCodeCompressor(
			CodeCompressionTestHarness.CreateLocator(),
			[harness.Pack],
			analysisPhaseObserver: observer);
		var diagnosticsA = compressor.BeginAnalysisDiagnostics();
		using var scopeA = compressor.CreateScope(Path.GetTempPath(), CodeTransformKinds.Bodies);
		var analysisTask = Task.Run(
			() => scopeA.Analyze(
				"Widget.cs",
				"Widget.cs",
				"public sealed class Widget { public void Run() { System.Console.WriteLine(1); } }",
				TestContext.Current.CancellationToken),
			TestContext.Current.CancellationToken);
		await observer.Reached.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken);

		diagnosticsA.Dispose();
		var frozenBeforeCompletion = diagnosticsA.Capture();
		using var diagnosticsB = compressor.BeginAnalysisDiagnostics();
		observer.Release();
		_ = await analysisTask.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken);
		var frozenAfterCompletion = diagnosticsA.Capture();

		Assert.Same(frozenBeforeCompletion, frozenAfterCompletion);
		Assert.Equal(1, frozenAfterCompletion.DroppedLateSamples);
		Assert.Equal(0, frozenAfterCompletion.CompletedFiles);
		Assert.All(frozenAfterCompletion.Phases, static phase => Assert.Equal(0, phase.Count));
		var nextSnapshot = diagnosticsB.Capture();
		Assert.Equal(0, nextSnapshot.CompletedFiles);
		Assert.Equal(0, nextSnapshot.DroppedLateSamples);
		Assert.All(nextSnapshot.Phases, static phase => Assert.Equal(0, phase.Count));
	}

	private static string CreateCaptureSpillSource()
	{
		var source = new StringBuilder();
		for (var index = 0; index < 1_400; index++)
			source.Append("// file comment ").Append(index).Append('\n');

		source.Append(
			"""
			public sealed class Widget
			{
			    public int Value { get; } = 42;

			    public void Run()
			    {
			""");
		for (var index = 0; index < 200; index++)
			source.Append("        // body comment ").Append(index).Append('\n');
		source.Append(
			"""
			        System.Console.WriteLine(Value);
			    }
			}
			""");
		return source.ToString();
	}

	private sealed class BlockingPhaseObserver(TreeSitterAnalysisPhase targetPhase) :
		ITreeSitterAnalysisPhaseObserver,
		IDisposable
	{
		private readonly ManualResetEventSlim _release = new(initialState: false);
		private readonly TaskCompletionSource _reached =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task Reached => _reached.Task;

		public void OnPhaseCompleted(TreeSitterAnalysisPhase phase)
		{
			if (phase != targetPhase)
				return;
			_reached.TrySetResult();
			if (!_release.Wait(TimeSpan.FromSeconds(5)))
				throw new TimeoutException("The diagnostics freeze test did not release analysis.");
		}

		public void Release() => _release.Set();

		public void Dispose()
		{
			_release.Set();
			_release.Dispose();
		}
	}

	private static void AssertEquivalentAnalysis(
		string source,
		CodeCompressionAnalysis expected,
		CodeCompressionAnalysis actual)
	{
		Assert.Equal(expected.Plan.RelativePath, actual.Plan.RelativePath);
		Assert.Equal(expected.Plan.LanguageId, actual.Plan.LanguageId);
		Assert.Equal(expected.Plan.Outcome, actual.Plan.Outcome);
		Assert.Equal(expected.Plan.SourceLength, actual.Plan.SourceLength);
		Assert.Equal(expected.Plan.TransformedLength, actual.Plan.TransformedLength);
		Assert.Equal(expected.Plan.TransformIdentity, actual.Plan.TransformIdentity);
		Assert.Equal(expected.Plan.AffectedKinds, actual.Plan.AffectedKinds);
		Assert.Equal(expected.Plan.Edits.Count, actual.Plan.Edits.Count);
		for (var index = 0; index < expected.Plan.Edits.Count; index++)
		{
			var expectedEdit = expected.Plan.Edits[index];
			var actualEdit = actual.Plan.Edits[index];
			Assert.Equal(expectedEdit.SourceStart, actualEdit.SourceStart);
			Assert.Equal(expectedEdit.SourceLength, actualEdit.SourceLength);
			Assert.Equal(expectedEdit.Kinds, actualEdit.Kinds);
			Assert.Equal(expectedEdit.Replacement, actualEdit.Replacement);
		}

		var expectedResult = expected.GetResult(source);
		var actualResult = actual.GetResult(source);
		Assert.Equal(expectedResult.Text, actualResult.Text);
		Assert.Equal(
			Encoding.UTF8.GetBytes(expectedResult.Text),
			Encoding.UTF8.GetBytes(actualResult.Text));
		AssertEquivalentMap(expectedResult.Map, actualResult.Map);
	}

	private static void AssertEquivalentMap(ContentTransformMap expected, ContentTransformMap actual)
	{
		Assert.Equal(expected.IsIdentity, actual.IsIdentity);
		Assert.Equal(expected.SourceLength, actual.SourceLength);
		Assert.Equal(expected.TransformedLength, actual.TransformedLength);
		for (var offset = -1; offset <= expected.SourceLength + 1; offset++)
		{
			var expectedMapped = expected.TryToTransformed(offset, out var expectedValue);
			var actualMapped = actual.TryToTransformed(offset, out var actualValue);
			Assert.Equal(expectedMapped, actualMapped);
			Assert.Equal(expectedValue, actualValue);
		}
		for (var offset = -1; offset <= expected.TransformedLength + 1; offset++)
		{
			var expectedMapped = expected.TryToSource(offset, out var expectedValue);
			var actualMapped = actual.TryToSource(offset, out var actualValue);
			Assert.Equal(expectedMapped, actualMapped);
			Assert.Equal(expectedValue, actualValue);
		}
	}

	private sealed class CancelOnPhaseObserver(
		TreeSitterAnalysisPhase target,
		CancellationTokenSource cancellation) : ITreeSitterAnalysisPhaseObserver
	{
		public void OnPhaseCompleted(TreeSitterAnalysisPhase phase)
		{
			if (phase == target)
				cancellation.Cancel();
		}
	}
}
