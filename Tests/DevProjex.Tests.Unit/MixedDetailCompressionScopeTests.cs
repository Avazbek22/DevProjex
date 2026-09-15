using System.Collections.Concurrent;
using DevProjex.Application.Compression;

namespace DevProjex.Tests.Unit;

public sealed class MixedDetailCompressionScopeTests
{
	private const string Root = "project";
	private static readonly CodeTransformKinds Compact =
		CodeTransformKinds.Comments | CodeTransformKinds.BlankLines;
	private static readonly CodeTransformKinds Signatures =
		CodeTransformKinds.Bodies | CodeTransformKinds.Comments | CodeTransformKinds.BlankLines;

	private static ContentDetailPolicy MixedPolicy() =>
		new(
			profileKinds: CodeTransformKinds.None,
			baseKinds: Compact,
			[
				new ContentDetailOverride(
					ContentDetailPatternSet.Create(["src/reduced.cs"]),
					Signatures),
				new ContentDetailOverride(
					ContentDetailPatternSet.Create(["src/verbatim.cs"]),
					CodeTransformKinds.None)
			]);

	[Fact]
	public void OneScopeAnalysesEachFileWithItsOwnEffectiveKinds()
	{
		var policy = MixedPolicy();
		using var compressor = new KindsRecordingCompressor();
		using var session = new CodeCompressionSession(compressor);
		var context = new CodeCompressionContext(Root, session, policy.UnionKinds) { Policy = policy };

		using var scope = context.BeginOutput(["src/base.cs", "src/reduced.cs", "src/verbatim.cs"]);
		scope.Transform("src/base.cs", "src/base.cs", "0123456789abcdef", CancellationToken.None);
		scope.Transform("src/reduced.cs", "src/reduced.cs", "0123456789abcdef", CancellationToken.None);
		scope.Transform("src/verbatim.cs", "src/verbatim.cs", "0123456789abcdef", CancellationToken.None);

		Assert.Equal(Compact, compressor.KindsAnalysing("src/base.cs"));
		Assert.Equal(Signatures, compressor.KindsAnalysing("src/reduced.cs"));
		// Effective kinds of None means no transformation was requested for that file at all, so it
		// must never reach the compressor: every kinds-taking entry point rejects None.
		Assert.DoesNotContain("src/verbatim.cs", compressor.AnalysedPaths);
	}

	[Fact]
	public void PlansCarryThePerFileIdentitySoTwoDetailLevelsAreSeparateCacheEntries()
	{
		var policy = MixedPolicy();
		using var compressor = new KindsRecordingCompressor();
		using var session = new CodeCompressionSession(compressor);
		var context = new CodeCompressionContext(Root, session, policy.UnionKinds) { Policy = policy };

		using var scope = context.BeginOutput(["src/base.cs", "src/reduced.cs"]);
		var basePlan = scope.ResolvePlan("src/base.cs", "src/base.cs", "0123456789abcdef", CancellationToken.None);
		var reducedPlan = scope.ResolvePlan(
			"src/reduced.cs",
			"src/reduced.cs",
			"0123456789abcdef",
			CancellationToken.None);

		Assert.Equal(session.GetTransformIdentity(Compact), basePlan.TransformIdentity);
		Assert.Equal(session.GetTransformIdentity(Signatures), reducedPlan.TransformIdentity);
		Assert.NotEqual(basePlan.TransformIdentity, reducedPlan.TransformIdentity);
	}

	[Fact]
	public void EveryInnerScopeSharesOneOperationIdentifier()
	{
		var policy = MixedPolicy();
		using var compressor = new KindsRecordingCompressor();
		using var session = new CodeCompressionSession(compressor);
		var context = new CodeCompressionContext(Root, session, policy.UnionKinds) { Policy = policy };

		using var scope = context.BeginOutput(["src/base.cs", "src/reduced.cs"]);
		scope.Transform("src/base.cs", "src/base.cs", "0123456789abcdef", CancellationToken.None);
		scope.Transform("src/reduced.cs", "src/reduced.cs", "0123456789abcdef", CancellationToken.None);

		// A transient grammar-load failure is memoised per operation identifier. Separate
		// identifiers would let one detail group short-circuit while another retries, so the same
		// pack over the same files could report different compression availability.
		Assert.Equal(2, compressor.CreatedScopeKinds.Count);
		Assert.Single(compressor.OperationIdentifiers);
	}

	[Fact]
	public void DisposingTheOuterScopeDisposesEveryInnerScopeItMaterialised()
	{
		var policy = MixedPolicy();
		using var compressor = new KindsRecordingCompressor();
		using var session = new CodeCompressionSession(compressor);
		var context = new CodeCompressionContext(Root, session, policy.UnionKinds) { Policy = policy };

		var scope = context.BeginOutput(["src/base.cs", "src/reduced.cs"]);
		scope.Transform("src/base.cs", "src/base.cs", "0123456789abcdef", CancellationToken.None);
		scope.Transform("src/reduced.cs", "src/reduced.cs", "0123456789abcdef", CancellationToken.None);
		scope.Dispose();

		Assert.Equal(compressor.CreatedScopeKinds.Count, compressor.DisposedScopeCount);
	}

	[Fact]
	public void OneSnapshotIsPublishedAndItCarriesThePolicyIdentity()
	{
		var policy = MixedPolicy();
		using var compressor = new KindsRecordingCompressor();
		using var session = new CodeCompressionSession(compressor);
		var context = new CodeCompressionContext(Root, session, policy.UnionKinds) { Policy = policy };
		var published = 0;
		session.SnapshotPublished += (_, _) => published++;

		using var scope = context.BeginOutput(["src/base.cs", "src/reduced.cs"]);
		scope.Transform("src/base.cs", "src/base.cs", "0123456789abcdef", CancellationToken.None);
		scope.Transform("src/reduced.cs", "src/reduced.cs", "0123456789abcdef", CancellationToken.None);
		var snapshot = scope.Complete();

		Assert.Equal(1, published);
		Assert.Equal(context.TransformIdentity, snapshot.TransformIdentity);
		Assert.Equal(policy.ComputeIdentity(compressor.TransformIdentity), snapshot.TransformIdentity);
	}

	[Fact]
	public void AUniformPolicyKeepsTheSingleKindsIdentity()
	{
		var policy = new ContentDetailPolicy(CodeTransformKinds.None, Compact, []);
		using var compressor = new KindsRecordingCompressor();
		using var session = new CodeCompressionSession(compressor);
		var context = new CodeCompressionContext(Root, session, Compact) { Policy = policy };

		// A policy without overrides must be indistinguishable from no policy at all, so every
		// existing response stays byte-for-byte unchanged.
		Assert.Equal(session.GetTransformIdentity(Compact), context.TransformIdentity);
	}

	[Fact]
	public void SupportIsDecidedAgainstThePerFileKinds()
	{
		var policy = MixedPolicy();
		// A language pack that can only replace bodies: a comments-and-blank-lines request has
		// nothing to do in it, while a signatures request does.
		using var compressor = new KindsRecordingCompressor { SupportedKinds = CodeTransformKinds.Bodies };
		using var session = new CodeCompressionSession(compressor);
		var context = new CodeCompressionContext(Root, session, policy.UnionKinds) { Policy = policy };

		Assert.True(context.IsSupported("src/reduced.cs"));
		Assert.False(context.IsSupported("src/base.cs"));
		Assert.False(context.IsSupported("src/verbatim.cs"));
	}

	private sealed class KindsRecordingCompressor : ICodeCompressor, IDisposable
	{
		private readonly ConcurrentDictionary<string, CodeTransformKinds> _analysed = new(StringComparer.Ordinal);
		private int _disposedScopes;

		public string TransformIdentity => "kinds-recording:v1";

		public CodeTransformKinds SupportedKinds { get; init; } =
			CodeTransformKinds.Bodies | CodeTransformKinds.Comments | CodeTransformKinds.BlankLines;

		public ConcurrentBag<CodeTransformKinds> CreatedScopeKinds { get; } = [];

		public ConcurrentDictionary<long, byte> OperationIdentifiers { get; } = new();

		public IReadOnlyCollection<string> AnalysedPaths => _analysed.Keys.ToArray();

		public int DisposedScopeCount => Volatile.Read(ref _disposedScopes);

		public CodeTransformKinds KindsAnalysing(string relativePath) =>
			_analysed.TryGetValue(relativePath, out var kinds) ? kinds : CodeTransformKinds.None;

		public bool IsSupported(string relativePath) => IsSupported(relativePath, CodeTransformKinds.Bodies);

		public bool IsSupported(string relativePath, CodeTransformKinds kinds) =>
			GetEffectiveTransformKinds(relativePath, kinds) != CodeTransformKinds.None;

		public CodeTransformKinds GetEffectiveTransformKinds(string relativePath, CodeTransformKinds kinds) =>
			kinds & SupportedKinds;

		public ICodeCompressionScope CreateScope(string projectRoot) =>
			CreateScope(projectRoot, CodeTransformKinds.Bodies, operationId: 0);

		public ICodeCompressionScope CreateScope(string projectRoot, CodeTransformKinds kinds) =>
			CreateScope(projectRoot, kinds, operationId: 0);

		public ICodeCompressionScope CreateScope(string projectRoot, CodeTransformKinds kinds, long operationId)
		{
			CreatedScopeKinds.Add(kinds);
			OperationIdentifiers.TryAdd(operationId, 0);
			return new Scope(this, kinds);
		}

		public void Dispose()
		{
		}

		private sealed class Scope(KindsRecordingCompressor owner, CodeTransformKinds kinds) : ICodeCompressionScope
		{
			public CodeCompressionAnalysis Analyze(
				string fullPath,
				string relativePath,
				string content,
				CancellationToken cancellationToken)
			{
				owner._analysed[relativePath] = kinds;
				return new CodeCompressionAnalysis(
					CodeCompressionPlan.Create(
						relativePath,
						"csharp",
						[new CodeCompressionEdit(7, 4, "...")],
						content.Length,
						owner.TransformIdentity),
					null);
			}

			public void Dispose() => Interlocked.Increment(ref owner._disposedScopes);
		}
	}
}
