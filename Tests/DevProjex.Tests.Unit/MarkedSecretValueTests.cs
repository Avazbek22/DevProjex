using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class MarkedSecretValueTests
{
	[Theory]
	[InlineData("  ExampleSecret  ", "ExampleSecret")]
	[InlineData("\"ExampleSecret\"", "ExampleSecret")]
	[InlineData("'ExampleSecret'", "ExampleSecret")]
	[InlineData("\"ExampleSecret'", "\"ExampleSecret'")]
	[InlineData("Example\r\nSecret", "Example\nSecret")]
	public void Normalization_PreservesTheStablePersistedContract(string source, string expected)
	{
		var normalized = MarkedSecretValueNormalizer.Normalize(source);

		Assert.Equal(expected, normalized);
	}

	[Fact]
	public void Normalization_PreservesCaseInValueAndHash()
	{
		Assert.True(MarkedSecretValueNormalizer.TryCreate(
			"CaseSensitive",
			out var upper,
			out _));
		Assert.True(MarkedSecretValueNormalizer.TryCreate(
			"casesensitive",
			out var lower,
			out _));

		Assert.Equal("CaseSensitive", upper.NormalizedValue);
		Assert.NotEqual(upper.Hash, lower.Hash);
	}

	[Fact]
	public void HashRepresentations_UseTheSinglePersistedLengthContract()
	{
		const string value = "ExampleSecret";
		Span<byte> bytes = stackalloc byte[MarkedSecretValueNormalizer.PersistedHashByteLength];

		MarkedSecretValueNormalizer.ComputeHash(value, bytes);
		var hex = MarkedSecretValueNormalizer.ComputeHash(value);

		Assert.Equal(
			MarkedSecretValueNormalizer.PersistedHashByteLength * 2,
			MarkedSecretValueNormalizer.PersistedHashLength);
		Assert.Equal(MarkedSecretValueNormalizer.PersistedHashLength, hex.Length);
		Assert.Equal(hex, Convert.ToHexString(bytes).ToLowerInvariant());
	}

	[Fact]
	public void Validation_RejectsMultilineValueAfterCrlfNormalization()
	{
		var success = MarkedSecretValueNormalizer.TryCreate(
			"\"Example\r\nSecret\"",
			out _,
			out var error);

		Assert.False(success);
		Assert.Equal(MarkedSecretValidationError.Multiline, error);
	}

	[Theory]
	[InlineData("STRIPE_SECRET_KEY = \"value\"", "STRIPE_SECRET_KEY")]
	[InlineData("\"STRIPE_SECRET_KEY\": \"value\"", "STRIPE_SECRET_KEY")]
	[InlineData("aws:access:key: value", "aws:access:key")]
	[InlineData("prefix value", null)]
	public void KeyExtraction_PreservesSupportedAssignmentForms(string line, string? expected)
	{
		var valueStart = line.IndexOf("value", StringComparison.Ordinal);

		var key = MarkedSecretValueNormalizer.ExtractKey(line, valueStart);

		Assert.Equal(expected, key);
	}

	[Theory]
	[InlineData(7, false, MarkedSecretValidationError.TooShort)]
	[InlineData(8, true, MarkedSecretValidationError.None)]
	[InlineData(513, false, MarkedSecretValidationError.TooLong)]
	public void Validation_EnforcesLengthLimits(
		int length,
		bool expectedSuccess,
		MarkedSecretValidationError expectedError)
	{
		var success = MarkedSecretValueNormalizer.TryCreate(
			new string('a', length),
			out _,
			out var error);

		Assert.Equal(expectedSuccess, success);
		Assert.Equal(expectedError, error);
	}

	[Fact]
	public void TokenBoundaries_DoNotMatchAdminInsideAdministrator()
	{
		Assert.False(SecretTokenBoundary.HasBoundaries("administrator", 0, "admin".Length));
		Assert.True(SecretTokenBoundary.HasBoundaries("admin = value", 0, "admin".Length));
	}
}
