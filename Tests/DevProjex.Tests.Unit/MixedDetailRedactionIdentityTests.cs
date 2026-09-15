using DevProjex.Application.Compression;
using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class MixedDetailRedactionIdentityTests
{
	private static readonly CodeTransformKinds Compact =
		CodeTransformKinds.Comments | CodeTransformKinds.BlankLines;
	private static readonly CodeTransformKinds Signatures =
		CodeTransformKinds.Bodies | CodeTransformKinds.Comments | CodeTransformKinds.BlankLines;

	private static ContentDetailPolicy MixedPolicy() =>
		new(
			profileKinds: CodeTransformKinds.None,
			baseKinds: Compact,
			[
				new ContentDetailOverride(ContentDetailPatternSet.Create(["src/reduced.cs"]), Signatures),
				new ContentDetailOverride(
					ContentDetailPatternSet.Create(["src/verbatim.cs"]),
					CodeTransformKinds.None)
			]);

	[Fact]
	public void ScanIdentityFollowsTheEffectiveKindsOfEachFile()
	{
		using var workspace = new TemporaryDirectory();
		var policy = MixedPolicy();
		using var compressionSession = new CodeCompressionSession(new NoEditCompressor());
		using var redactionSession = new SecretRedactionSession(new EmptyDetector());
		var context = ContentTransformationContext.For(
			new CodeCompressionContext(workspace.Path, compressionSession, policy.UnionKinds)
			{
				Policy = policy
			},
			new SecretRedactionContext(workspace.Path, redactionSession))!;

		using var scope = context.BeginOutput(
			[
				Path.Combine(workspace.Path, "src", "base.cs"),
				Path.Combine(workspace.Path, "src", "reduced.cs"),
				Path.Combine(workspace.Path, "src", "verbatim.cs")
			],
			TestContext.Current.CancellationToken);
		var redaction = scope.Redaction!;

		// Two calls that share a default level and differ only in which files an override claims
		// produce the same operation identity. Without a per-file value the metadata-only cache
		// lookup would hand one call the scan of the other call's differently transformed text.
		Assert.Equal(
			compressionSession.GetTransformIdentity(Compact),
			redaction.ResolveTransformIdentity(Path.Combine(workspace.Path, "src", "base.cs")));
		Assert.Equal(
			compressionSession.GetTransformIdentity(Signatures),
			redaction.ResolveTransformIdentity(Path.Combine(workspace.Path, "src", "reduced.cs")));
	}

	[Fact]
	public void AFileThatRequestsNoTransformUsesTheSameIdentityAsAPackWithoutCompression()
	{
		using var workspace = new TemporaryDirectory();
		var policy = MixedPolicy();
		using var compressionSession = new CodeCompressionSession(new NoEditCompressor());
		using var redactionSession = new SecretRedactionSession(new EmptyDetector());
		var mixed = ContentTransformationContext.For(
			new CodeCompressionContext(workspace.Path, compressionSession, policy.UnionKinds)
			{
				Policy = policy
			},
			new SecretRedactionContext(workspace.Path, redactionSession))!;
		var withoutCompression = ContentTransformationContext.For(
			null,
			new SecretRedactionContext(workspace.Path, redactionSession))!;

		using var mixedScope = mixed.BeginOutput(
			[Path.Combine(workspace.Path, "src", "verbatim.cs")],
			TestContext.Current.CancellationToken);
		using var plainScope = withoutCompression.BeginOutput(
			[Path.Combine(workspace.Path, "src", "verbatim.cs")],
			TestContext.Current.CancellationToken);

		// An untransformed file must land on exactly the value a pack with no compression uses, so
		// its scans and placeholder identities stay interchangeable between the two shapes - and the
		// identity-transform alias, which is skipped for an empty identity, stays skipped.
		var verbatim = Path.Combine(workspace.Path, "src", "verbatim.cs");
		Assert.Equal(string.Empty, mixedScope.Redaction!.ResolveTransformIdentity(verbatim));
		Assert.Equal(
			plainScope.Redaction!.ResolveTransformIdentity(verbatim),
			mixedScope.Redaction!.ResolveTransformIdentity(verbatim));
	}

	[Fact]
	public void AUniformPolicyKeepsOneIdentityForEveryFile()
	{
		using var workspace = new TemporaryDirectory();
		var policy = new ContentDetailPolicy(CodeTransformKinds.None, Compact, []);
		using var compressionSession = new CodeCompressionSession(new NoEditCompressor());
		using var redactionSession = new SecretRedactionSession(new EmptyDetector());
		var context = ContentTransformationContext.For(
			new CodeCompressionContext(workspace.Path, compressionSession, Compact) { Policy = policy },
			new SecretRedactionContext(workspace.Path, redactionSession))!;

		using var scope = context.BeginOutput(
			[
				Path.Combine(workspace.Path, "src", "base.cs"),
				Path.Combine(workspace.Path, "src", "other.cs")
			],
			TestContext.Current.CancellationToken);
		var expected = compressionSession.GetTransformIdentity(Compact);

		Assert.Equal(
			expected,
			scope.Redaction!.ResolveTransformIdentity(Path.Combine(workspace.Path, "src", "base.cs")));
		Assert.Equal(
			expected,
			scope.Redaction!.ResolveTransformIdentity(Path.Combine(workspace.Path, "src", "other.cs")));
	}

	private sealed class EmptyDetector : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) => [];
	}

	private sealed class NoEditCompressor : ICodeCompressor, IDisposable
	{
		public string TransformIdentity => "no-edit:v1";

		public bool IsSupported(string relativePath) => true;

		public bool IsSupported(string relativePath, CodeTransformKinds kinds) => true;

		public ICodeCompressionScope CreateScope(string projectRoot) => new Scope();

		public ICodeCompressionScope CreateScope(string projectRoot, CodeTransformKinds kinds) => new Scope();

		public ICodeCompressionScope CreateScope(string projectRoot, CodeTransformKinds kinds, long operationId) =>
			new Scope();

		public void Dispose()
		{
		}

		private sealed class Scope : ICodeCompressionScope
		{
			public CodeCompressionAnalysis Analyze(
				string fullPath,
				string relativePath,
				string content,
				CancellationToken cancellationToken) =>
				new(
					CodeCompressionPlan.Unchanged(
						relativePath,
						"csharp",
						CodeCompressionOutcome.UnchangedNoBenefit,
						content.Length,
						"no-edit:v1"),
					null);

			public void Dispose()
			{
			}
		}
	}
}
