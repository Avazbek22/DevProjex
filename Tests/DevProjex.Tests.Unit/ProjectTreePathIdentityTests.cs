namespace DevProjex.Tests.Unit;

public sealed class ProjectTreePathIdentityTests
{
	[Fact]
	public void ExactCaseWinsWhenCaseDistinctSiblingsExist()
	{
		string[] available = ["Foo", "foo"];

		Assert.True(ProjectTreePathIdentity.TryResolveAvailableName(available, "Foo", out var upper));
		Assert.True(ProjectTreePathIdentity.TryResolveAvailableName(available, "foo", out var lower));
		Assert.Equal("Foo", upper);
		Assert.Equal("foo", lower);
	}

	[Fact]
	public void AmbiguousCompatibilityAliasFailsClosed()
	{
		string[] available = ["Foo", "foo"];

		Assert.False(ProjectTreePathIdentity.TryResolveAvailableName(available, "FOO", out _));
	}

	[Fact]
	public void UniqueCompatibilityAliasIsWindowsOnly()
	{
		string[] available = ["Foo"];

		var resolved = ProjectTreePathIdentity.TryResolveAvailableName(available, "foo", out var value);

		Assert.Equal(OperatingSystem.IsWindows(), resolved);
		if (resolved)
			Assert.Equal("Foo", value);
	}

	[Fact]
	public void NameStatesPreserveExactSiblingsAndFailClosedForAmbiguousAlias()
	{
		var states = ProjectTreePathIdentity.ResolveAvailableNameStates(
			["Foo", "foo"],
			new Dictionary<string, bool>(ProjectTreePathIdentity.CanonicalComparer)
			{
				["Foo"] = true,
				["FOO"] = true
			},
			retainUnmatched: false);

		Assert.True(states["Foo"]);
		if (OperatingSystem.IsWindows())
			Assert.False(states["foo"]);
		else
			Assert.DoesNotContain("foo", states.Keys);
		Assert.DoesNotContain("FOO", states.Keys);
	}

	[Fact]
	public void NameStatesMigrateUniqueWindowsAliasAndCanRetainMissingIntent()
	{
		var states = ProjectTreePathIdentity.ResolveAvailableNameStates(
			["Source"],
			new Dictionary<string, bool>(ProjectTreePathIdentity.CanonicalComparer)
			{
				["source"] = true,
				["removed"] = true
			},
			retainUnmatched: true);

		Assert.True(states[OperatingSystem.IsWindows() ? "Source" : "source"]);
		Assert.True(states["removed"]);
		Assert.DoesNotContain(
			OperatingSystem.IsWindows() ? "source" : "Source",
			states.Keys);
	}
}
