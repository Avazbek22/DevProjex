using DevProjex.Application.Compression;
using DevProjex.Application.Context;

namespace DevProjex.Tests.Unit;

public sealed class ContentDetailSelectionTests
{
	private static readonly CodeTransformKinds Compact =
		CodeTransformKinds.Comments | CodeTransformKinds.BlankLines;
	private static readonly CodeTransformKinds Signatures =
		CodeTransformKinds.Bodies | CodeTransformKinds.Comments | CodeTransformKinds.BlankLines;

	private static ContentDetailOverride Override(CodeTransformKinds kinds, params string[] patterns) =>
		new(ContentDetailPatternSet.Create(patterns), kinds);

	private static ProjectSelectionSpec Selection(
		CodeTransformKinds selectionKinds,
		CodeTransformKinds? profileKinds = null,
		IReadOnlyList<ContentDetailOverride>? overrides = null) =>
		ProjectSelectionSpec.Standard with
		{
			CompressCode = selectionKinds.HasFlag(CodeTransformKinds.Bodies),
			StripComments = selectionKinds.HasFlag(CodeTransformKinds.Comments),
			StripBlankLines = selectionKinds.HasFlag(CodeTransformKinds.BlankLines),
			ProfileContentKinds = profileKinds,
			ContentDetailOverrides = overrides
		};

	[Fact]
	public void ASelectionWithoutOverridesHasNoPolicy()
	{
		Assert.Null(ContentDetailSelection.Resolve(Selection(Compact)));
		Assert.Equal(Compact, ContentDetailSelection.ResolveContextKinds(Selection(Compact)));
	}

	[Fact]
	public void ADefaultOfFullStillCreatesACompressionContextForItsOverrides()
	{
		var selection = Selection(
			CodeTransformKinds.None,
			CodeTransformKinds.None,
			[Override(Signatures, "src/**")]);

		// Gating on the default level would leave this call with no compression context at all: no
		// scope would open, every file would ship whole, and the reported mix would be a fiction.
		Assert.Equal(Signatures, ContentDetailSelection.ResolveContextKinds(selection));
		Assert.Equal(CodeTransformKinds.None, ContentDetailSelection.Resolve(selection)!.BaseKinds);
	}

	[Fact]
	public void TheCallLevelIsUnionedIntoTheDefaultButNotIntoTheProfileShare()
	{
		var selection = Selection(
			CodeTransformKinds.None,
			CodeTransformKinds.None,
			[Override(CodeTransformKinds.None, "docs/**")]);

		var policy = ContentDetailSelection.Resolve(selection, Signatures)!;

		Assert.Equal(Signatures, policy.BaseKinds);
		Assert.Equal(CodeTransformKinds.None, policy.ProfileKinds);
		Assert.Equal(Signatures, policy.KindsFor("src/engine.cs"));
		// An override back to full keeps only what the profile mandates, which here is nothing.
		Assert.Equal(CodeTransformKinds.None, policy.KindsFor("docs/guide.md"));
	}

	[Fact]
	public void AProfileMandateSurvivesAnOverrideBackToFull()
	{
		var selection = Selection(
			CodeTransformKinds.Bodies,
			CodeTransformKinds.Bodies,
			[Override(CodeTransformKinds.None, "docs/**")]);

		var policy = ContentDetailSelection.Resolve(selection, Compact)!;

		Assert.Equal(CodeTransformKinds.Bodies | Compact, policy.BaseKinds);
		Assert.Equal(CodeTransformKinds.Bodies, policy.KindsFor("docs/guide.md"));
	}

	[Fact]
	public void AnExplicitCallToggleIsNotTreatedAsAProfileMandate()
	{
		// The command line has no detail level: its three toggles are the call. With the profile's
		// own share recorded as empty, an override back to full really does mean full.
		var selection = Selection(CodeTransformKinds.Bodies, CodeTransformKinds.None,
			[Override(CodeTransformKinds.None, "docs/**")]);

		var policy = ContentDetailSelection.Resolve(selection)!;

		Assert.Equal(CodeTransformKinds.Bodies, policy.BaseKinds);
		Assert.Equal(CodeTransformKinds.None, policy.KindsFor("docs/guide.md"));
		Assert.Equal(CodeTransformKinds.Bodies, policy.KindsFor("src/engine.cs"));
	}

	[Fact]
	public void ASelectionThatNeverCrossedTheResolverFallsBackToItsOwnKinds()
	{
		var selection = Selection(
			CodeTransformKinds.Bodies,
			profileKinds: null,
			[Override(CodeTransformKinds.None, "docs/**")]);

		var policy = ContentDetailSelection.Resolve(selection)!;

		Assert.Equal(CodeTransformKinds.Bodies, policy.ProfileKinds);
	}

	[Fact]
	public void AnEmptyOverrideListIsTreatedAsNoMix()
	{
		Assert.Null(ContentDetailSelection.Resolve(Selection(Compact, Compact, [])));
	}
}
