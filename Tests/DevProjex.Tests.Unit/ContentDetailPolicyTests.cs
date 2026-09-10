using DevProjex.Application.Compression;

namespace DevProjex.Tests.Unit;

public sealed class ContentDetailPolicyTests
{
	private static readonly CodeTransformKinds None = CodeTransformKinds.None;
	private static readonly CodeTransformKinds Compact =
		CodeTransformKinds.Comments | CodeTransformKinds.BlankLines;
	private static readonly CodeTransformKinds Signatures =
		CodeTransformKinds.Bodies | CodeTransformKinds.Comments | CodeTransformKinds.BlankLines;

	private static ContentDetailOverride Override(CodeTransformKinds kinds, params string[] patterns) =>
		new(ContentDetailPatternSet.Create(patterns), kinds);

	[Fact]
	public void OverridesApplyInOrderAndTheLastMatchWins()
	{
		var policy = new ContentDetailPolicy(
			profileKinds: None,
			baseKinds: None,
			[
				Override(Signatures, "src/**"),
				Override(Compact, "src/api/**")
			]);

		Assert.Equal(Signatures, policy.KindsFor("src/core/engine.cs"));
		// Both entries claim this file; the later, more specific one decides.
		Assert.Equal(Compact, policy.KindsFor("src/api/client.cs"));
		Assert.Equal(None, policy.KindsFor("docs/readme.md"));
	}

	[Fact]
	public void EveryFileUnionsTheProfileKindsSoFullCannotEscapeASavedProfile()
	{
		var policy = new ContentDetailPolicy(
			profileKinds: CodeTransformKinds.Bodies,
			baseKinds: CodeTransformKinds.Bodies | Compact,
			[Override(None, "docs/**")]);

		// "full" adds nothing, so the file keeps exactly what the profile mandates - never less.
		Assert.Equal(CodeTransformKinds.Bodies, policy.KindsFor("docs/guide.md"));
		Assert.Equal(CodeTransformKinds.Bodies | Compact, policy.KindsFor("src/engine.cs"));
	}

	[Fact]
	public void UnionCoversOverridesWhenTheCallLevelRequestsNothing()
	{
		var policy = new ContentDetailPolicy(
			profileKinds: None,
			baseKinds: None,
			[Override(Signatures, "src/**")]);

		// The gate for creating a compression context reads this, not the base kinds: a "full"
		// default with a "signatures" override has no base kinds at all.
		Assert.Equal(None, policy.BaseKinds);
		Assert.Equal(Signatures, policy.UnionKinds);
		Assert.False(policy.IsUniform);
	}

	[Fact]
	public void AUniformPolicyReportsTheSameKindsForEveryPath()
	{
		var policy = new ContentDetailPolicy(Compact, Compact, []);

		Assert.True(policy.IsUniform);
		Assert.Equal(Compact, policy.UnionKinds);
		Assert.Equal(Compact, policy.KindsFor("src/engine.cs"));
		Assert.Equal(Compact, policy.KindsFor("docs/readme.md"));
	}

	[Fact]
	public void IdentitySeparatesCallsThatDifferOnlyInWhichFilesAnOverrideClaims()
	{
		var claimsFirst = new ContentDetailPolicy(None, Compact, [Override(Signatures, "a.cs")]);
		var claimsSecond = new ContentDetailPolicy(None, Compact, [Override(Signatures, "b.cs")]);
		var sameAsFirst = new ContentDetailPolicy(None, Compact, [Override(Signatures, "a.cs")]);

		// Both calls share a base level, so without the override list in the identity they would
		// share cached scans of differently transformed text.
		Assert.NotEqual(claimsFirst.ComputeIdentity("engine"), claimsSecond.ComputeIdentity("engine"));
		Assert.Equal(claimsFirst.ComputeIdentity("engine"), sameAsFirst.ComputeIdentity("engine"));
		Assert.StartsWith("engine+detail:", claimsFirst.ComputeIdentity("engine"));
	}

	[Fact]
	public void IdentitySeparatesOverrideOrderAndEntryGrouping()
	{
		var ordered = new ContentDetailPolicy(
			None,
			None,
			[Override(Signatures, "src/**"), Override(Compact, "src/api/**")]);
		var reversed = new ContentDetailPolicy(
			None,
			None,
			[Override(Compact, "src/api/**"), Override(Signatures, "src/**")]);

		Assert.NotEqual(ordered.ComputeIdentity("engine"), reversed.ComputeIdentity("engine"));
	}

	[Fact]
	public void PatternsUseTheSharedProjectRelativeGlobSyntax()
	{
		var set = ContentDetailPatternSet.Create(["**/*.{ts,tsx}", "src/*.cs"]);

		Assert.True(set.Matches("app/ui/view.tsx"));
		Assert.True(set.Matches("view.ts"));
		Assert.True(set.Matches("src/engine.cs"));
		Assert.False(set.Matches("src/nested/engine.cs"));
		// Matching is case-sensitive on every platform, exactly as include filters are.
		Assert.False(set.Matches("SRC/engine.cs"));
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("..\\/escape.cs")]
	[InlineData("../escape.cs")]
	[InlineData("/absolute.cs")]
	[InlineData("!negated.cs")]
	[InlineData("class[abc].cs")]
	[InlineData("unbalanced{a,b.cs")]
	public void PatternsRejectSyntaxTheIncludeFilterAlsoRejects(string pattern)
	{
		var failure = Assert.Throws<ProjectRelativeGlobException>(
			() => ContentDetailPatternSet.Create([pattern]));

		Assert.False(string.IsNullOrWhiteSpace(failure.Reason));
	}

	[Fact]
	public void AnEmptyPatternListIsRejected()
	{
		Assert.Throws<ProjectRelativeGlobException>(() => ContentDetailPatternSet.Create([]));
	}
}
