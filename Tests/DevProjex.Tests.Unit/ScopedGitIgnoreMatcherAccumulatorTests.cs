using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit;

public sealed class ScopedGitIgnoreMatcherAccumulatorTests
{
	[Fact]
	public void Add_FiveThousandUniqueScopesAndDuplicates_PreservesFirstValueAndOrder()
	{
		const int scopeCount = 5_000;
		var accumulator = new ScopedGitIgnoreMatcherAccumulator();
		var expected = new ScopedGitIgnoreMatcher[scopeCount];

		for (var index = 0; index < scopeCount; index++)
		{
			var matcher = new ScopedGitIgnoreMatcher(
				Path.Combine("repo", $"scope-{index:D4}"),
				GitIgnoreMatcher.Empty);
			expected[index] = matcher;
			Assert.True(accumulator.Add(matcher));
		}

		for (var index = scopeCount - 1; index >= 0; index--)
		{
			Assert.False(accumulator.Add(new ScopedGitIgnoreMatcher(
				expected[index].ScopeRootPath,
				GitIgnoreMatcher.Empty)));
		}

		Assert.Equal(scopeCount, accumulator.Items.Count);
		for (var index = 0; index < scopeCount; index++)
			Assert.Same(expected[index], accumulator.Items[index]);
	}

	[Fact]
	public void Add_PreservesCaseDistinctPhysicalScopes()
	{
		var accumulator = new ScopedGitIgnoreMatcherAccumulator();
		var lowerCasePath = Path.Combine("repo", "scope");
		var upperCasePath = Path.Combine("repo", "SCOPE");
		Assert.True(accumulator.Add(new ScopedGitIgnoreMatcher(lowerCasePath, GitIgnoreMatcher.Empty)));

		var added = accumulator.Add(new ScopedGitIgnoreMatcher(upperCasePath, GitIgnoreMatcher.Empty));

		Assert.True(added);
		Assert.Equal(2, accumulator.Items.Count);
	}
}
