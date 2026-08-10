using DevProjex.Application.Compression;

namespace DevProjex.Tests.Unit;

public sealed class ContentTransformMapTests
{
	private const string Source = "void A() { body(); }\nvoid B() { other(); }\n";

	private static CodeCompressionPlan PlanFor(params CodeCompressionEdit[] edits) =>
		CodeCompressionPlan.Create("a.cs", "csharp", edits, Source.Length, "test-v1");

	[Fact]
	public void Identity_MapsEveryOffsetToItself()
	{
		var map = ContentTransformMap.Identity;

		Assert.True(map.IsIdentity);
		foreach (var offset in new[] { 0, 1, 17, Source.Length })
		{
			Assert.True(map.TryToTransformed(offset, out var forward));
			Assert.Equal(offset, forward);
			Assert.True(map.TryToSource(offset, out var backward));
			Assert.Equal(offset, backward);
		}
	}

	[Fact]
	public void OffsetBeforeAnyEdit_IsUnshifted()
	{
		var map = PlanFor(new CodeCompressionEdit(11, 8, " … ")).Apply(Source).Map;

		Assert.True(map.TryToTransformed(5, out var transformed));
		Assert.Equal(5, transformed);
		Assert.True(map.TryToSource(5, out var source));
		Assert.Equal(5, source);
	}

	[Fact]
	public void RegionStart_MapsToReplacementStart_InBothDirections()
	{
		var edit = new CodeCompressionEdit(11, 8, " … ");
		var map = PlanFor(edit).Apply(Source).Map;

		Assert.True(map.TryToTransformed(edit.SourceStart, out var transformed));
		Assert.Equal(edit.SourceStart, transformed);
		Assert.True(map.TryToSource(transformed, out var source));
		Assert.Equal(edit.SourceStart, source);
	}

	[Fact]
	public void OffsetInsideRemovedRegion_IsUnmappable()
	{
		var edit = new CodeCompressionEdit(11, 8, " … ");
		var map = PlanFor(edit).Apply(Source).Map;

		// A session secret mark recorded inside a body that no longer ships must vanish,
		// not silently slide onto the neighbouring code.
		for (var offset = edit.SourceStart + 1; offset < edit.SourceEnd; offset++)
			Assert.False(map.TryToTransformed(offset, out _));
	}

	[Fact]
	public void OffsetInsideReplacement_HasNoSourceCounterpart()
	{
		var map = PlanFor(new CodeCompressionEdit(11, 8, " … ")).Apply(Source).Map;

		Assert.True(map.TryToTransformed(11, out var replacementStart));
		Assert.False(map.TryToSource(replacementStart + 1, out _));
	}

	[Fact]
	public void OffsetAfterEdit_ShiftsByTheAccumulatedDelta()
	{
		var edit = new CodeCompressionEdit(11, 8, " … ");
		var result = PlanFor(edit).Apply(Source);
		var delta = edit.Replacement.Length - edit.SourceLength;

		Assert.True(result.Map.TryToTransformed(edit.SourceEnd, out var transformed));
		Assert.Equal(edit.SourceEnd + delta, transformed);
		Assert.True(result.Map.TryToSource(transformed, out var source));
		Assert.Equal(edit.SourceEnd, source);
	}

	[Fact]
	public void MultipleEdits_AccumulateDeltasIndependently()
	{
		var first = new CodeCompressionEdit(11, 8, " … ");
		var second = new CodeCompressionEdit(32, 9, " … ");
		var result = PlanFor(first, second).Apply(Source);

		Assert.Equal(result.Text.Length, result.Map.TransformedLength);
		Assert.True(result.Map.TryToTransformed(Source.Length, out var end));
		Assert.Equal(result.Text.Length, end);
	}

	[Fact]
	public void EveryMappableOffset_RoundTrips()
	{
		var result = PlanFor(
			new CodeCompressionEdit(11, 8, " … "),
			new CodeCompressionEdit(32, 9, " … ")).Apply(Source);

		var mapped = 0;
		for (var offset = 0; offset <= Source.Length; offset++)
		{
			if (!result.Map.TryToTransformed(offset, out var transformed))
				continue;
			mapped++;
			Assert.True(result.Map.TryToSource(transformed, out var roundTripped));
			Assert.Equal(offset, roundTripped);
		}

		Assert.True(mapped > 0);
	}

	[Fact]
	public void MappedOffsetsOutsideEditedRegions_PointAtTheSameCharacter()
	{
		var edits = new[]
		{
			new CodeCompressionEdit(11, 8, " … "),
			new CodeCompressionEdit(32, 9, " … ")
		};
		var result = PlanFor(edits).Apply(Source);

		var compared = 0;
		for (var offset = 0; offset < Source.Length; offset++)
		{
			// A region start maps to the start of its replacement. That is a POSITION
			// correspondence - where the body used to begin - not a character one, so it is
			// excluded here and covered by RegionStart_MapsToReplacementStart instead.
			if (edits.Any(edit => offset >= edit.SourceStart && offset < edit.SourceEnd))
				continue;
			Assert.True(result.Map.TryToTransformed(offset, out var transformed));
			Assert.Equal(Source[offset], result.Text[transformed]);
			compared++;
		}

		Assert.Equal(Source.Length - edits.Sum(static edit => edit.SourceLength), compared);
	}

	[Fact]
	public void Map_HandlesAReplacementLongerThanTheRemovedRegion()
	{
		// The policy never emits a growing edit, but the map must not depend on that: it is also
		// built for language packs whose placeholder is longer, and for round-tripping in tests.
		var edit = new CodeCompressionEdit(11, 2, " /* a much longer placeholder */ ");
		var map = ContentTransformMap.Create([edit], Source.Length);

		Assert.True(map.TransformedLength > Source.Length);
		Assert.True(map.TryToTransformed(edit.SourceEnd, out var transformed));
		Assert.True(map.TryToSource(transformed, out var source));
		Assert.Equal(edit.SourceEnd, source);
	}

	[Fact]
	public void OneLineBodyShorterThanThePlaceholder_IsRefusedBeforeAnythingIsApplied()
	{
		// int F(int a) { return a; } - the body is 13 characters, the placeholder is 11, but a
		// one-liner like this is exactly where compression stops paying for itself. The refusal
		// has to happen while building the plan: enabling the checkbox must never grow a file.
		const string oneLiner = "int F(int a) => a;\n";
		var body = new CodeCompressionEdit(13, 4, "=> /* … */");

		var plan = CodeCompressionPlan.Create("a.cs", "csharp", [body], oneLiner.Length, "test-v1");

		Assert.Equal(CodeCompressionOutcome.UnchangedNoBenefit, plan.Outcome);
		Assert.Empty(plan.Edits);
		Assert.Equal(oneLiner.Length, plan.TransformedLength);
		Assert.Equal(0, plan.SavedCharacters);
		Assert.Equal(oneLiner, plan.Apply(oneLiner).Text);
	}

	[Fact]
	public void FileWhoseEditsCancelOut_IsRefusedAsNoBenefit()
	{
		// Every individual edit shrinks by one character, but a language pack could still produce
		// a set whose total does not pay for itself. The file-level rule is the backstop.
		var plan = CodeCompressionPlan.Create(
			"a.cs",
			"csharp",
			[new CodeCompressionEdit(11, 8, "1234567")],
			Source.Length,
			"test-v1");

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Equal(1, plan.SavedCharacters);
	}

	[Fact]
	public void GrowingEditsAreDroppedIndividually()
	{
		var plan = CodeCompressionPlan.Create(
			"a.cs",
			"csharp",
			[
				new CodeCompressionEdit(11, 8, " … "),
				new CodeCompressionEdit(32, 2, " a much longer replacement ")
			],
			Source.Length,
			"test-v1");

		Assert.Equal([11], plan.Edits.Select(static edit => edit.SourceStart));
	}

	[Fact]
	public void PureDeletion_MapsTheBoundariesTogether()
	{
		var edit = new CodeCompressionEdit(11, 8, string.Empty);
		var result = PlanFor(edit).Apply(Source);

		Assert.True(result.Map.TryToTransformed(edit.SourceStart, out var start));
		Assert.True(result.Map.TryToTransformed(edit.SourceEnd, out var end));
		Assert.Equal(start, end);
	}

	[Fact]
	public void OffsetOutsideTheText_IsRejected()
	{
		var map = PlanFor(new CodeCompressionEdit(11, 8, " … ")).Apply(Source).Map;

		Assert.False(map.TryToTransformed(-1, out _));
		Assert.False(map.TryToTransformed(Source.Length + 1, out _));
		Assert.False(map.TryToSource(-1, out _));
		Assert.False(map.TryToSource(map.TransformedLength + 1, out _));
	}

	[Fact]
	public void Apply_ProducesTextMatchingTheDeclaredLength()
	{
		var plan = PlanFor(
			new CodeCompressionEdit(11, 8, " … "),
			new CodeCompressionEdit(32, 9, " … "));

		var result = plan.Apply(Source);

		Assert.Equal(plan.TransformedLength, result.Text.Length);
		Assert.Equal("void A() {  … }\nvoid B() {  … }\n", result.Text);
	}

	[Fact]
	public void Apply_RejectsTextOfADifferentLength()
	{
		var plan = PlanFor(new CodeCompressionEdit(11, 8, " … "));

		Assert.Throws<ArgumentException>(() => plan.Apply("short"));
	}

	[Fact]
	public void Create_RejectsOverlappingEdits()
	{
		Assert.Throws<ArgumentException>(() => PlanFor(
			new CodeCompressionEdit(11, 8, " … "),
			new CodeCompressionEdit(15, 4, " … ")));
	}

	[Fact]
	public void Create_RejectsEditsLeavingTheFile()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			PlanFor(new CodeCompressionEdit(Source.Length - 2, 10, " … ")));
	}

	[Fact]
	public void Create_SortsEditsSoCallersNeedNot()
	{
		var plan = PlanFor(
			new CodeCompressionEdit(32, 9, " … "),
			new CodeCompressionEdit(11, 8, " … "));

		Assert.Equal([11, 32], plan.Edits.Select(static edit => edit.SourceStart));
	}

	[Fact]
	public void UnchangedPlan_AppliesAsAnExactCopyWithAnIdentityMap()
	{
		var plan = CodeCompressionPlan.Unchanged(
			"a.cs",
			"csharp",
			CodeCompressionOutcome.UnchangedTooLarge,
			Source.Length,
			"test-v1");

		var result = plan.Apply(Source);

		Assert.Equal(Source, result.Text);
		Assert.True(result.Map.IsIdentity);
		Assert.Equal(0, plan.SavedCharacters);
	}
}
