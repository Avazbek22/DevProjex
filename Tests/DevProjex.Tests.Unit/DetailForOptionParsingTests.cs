using DevProjex.Application.Compression;
using DevProjex.Terminal.CommandLine;

namespace DevProjex.Tests.Unit;

public sealed class DetailForOptionParsingTests
{
	private static readonly CodeTransformKinds Compact =
		CodeTransformKinds.Comments | CodeTransformKinds.BlankLines;
	private static readonly CodeTransformKinds Signatures =
		CodeTransformKinds.Bodies | CodeTransformKinds.Comments | CodeTransformKinds.BlankLines;

	[Fact]
	public void AnAbsentOptionProducesNoOverrides()
	{
		Assert.Null(DetailForOption.Parse(null));
		Assert.Null(DetailForOption.Parse([]));
	}

	[Fact]
	public void EachValueBecomesItsOwnEntryInCallerOrder()
	{
		var overrides = DetailForOption.Parse(["src/**=signatures", "docs/**=full"])!;

		Assert.Equal(2, overrides.Count);
		Assert.Equal(["src/**"], overrides[0].Patterns.Patterns);
		Assert.Equal(Signatures, overrides[0].RequestedKinds);
		Assert.Equal(["docs/**"], overrides[1].Patterns.Patterns);
		Assert.Equal(CodeTransformKinds.None, overrides[1].RequestedKinds);
	}

	[Fact]
	public void TheValueSplitsOnItsLastEqualsSignSoAGlobMayContainOne()
	{
		var overrides = DetailForOption.Parse(["src/a=b/**=compact"])!;

		var entry = Assert.Single(overrides);
		Assert.Equal(["src/a=b/**"], entry.Patterns.Patterns);
		Assert.Equal(Compact, entry.RequestedKinds);
	}

	[Theory]
	[InlineData("src/**")]
	[InlineData("src/**=")]
	[InlineData("=compact")]
	[InlineData("src/**=verbose")]
	[InlineData("src/**=COMPACT")]
	[InlineData("../escape=compact")]
	[InlineData("!negated=compact")]
	[InlineData("cls[ab]=compact")]
	public void AnInvalidValueIsRejectedWithAReason(string value)
	{
		var failure = Assert.Throws<DetailForOptionException>(() => DetailForOption.Parse([value]));

		Assert.False(string.IsNullOrWhiteSpace(failure.Message));
	}

	[Fact]
	public void TheEntryCountIsBounded()
	{
		var values = Enumerable.Range(0, 17).Select(index => $"p{index}=compact").ToArray();

		var failure = Assert.Throws<DetailForOptionException>(() => DetailForOption.Parse(values));

		Assert.Equal(DetailForOptionFailure.TooManyValues, failure.Failure);
		Assert.Equal("16", failure.Value);
	}

	[Theory]
	[InlineData("src/**", DetailForOptionFailure.InvalidFormat, "src/**")]
	[InlineData("=compact", DetailForOptionFailure.InvalidFormat, "=compact")]
	[InlineData("src/**=verbose", DetailForOptionFailure.InvalidLevel, "verbose")]
	[InlineData("../escape=compact", DetailForOptionFailure.InvalidPattern, "../escape")]
	public void ARejectedValueNamesItsFailureForLocalizedPresentation(
		string value,
		DetailForOptionFailure expectedFailure,
		string expectedValue)
	{
		var failure = Assert.Throws<DetailForOptionException>(() => DetailForOption.Parse([value]));

		Assert.Equal(expectedFailure, failure.Failure);
		Assert.Equal(expectedValue, failure.Value);
	}
}
