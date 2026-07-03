namespace DevProjex.Tests.Unit;

public sealed class ExtensionSetInclusionPolicyTests
{
	[Fact]
	public void AllowsExtension_SpanLookup_IsCaseInsensitiveForHashSetPolicies()
	{
		var policy = new ExtensionSetInclusionPolicy(
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".CS", ".json" });

		Assert.True(policy.AllowsExtension(".cs".AsSpan()));
		Assert.True(policy.AllowsExtension(".JSON".AsSpan()));
		Assert.False(policy.AllowsExtension(".md".AsSpan()));
	}

	[Fact]
	public void AllowsExtension_FallbackSet_UsesSameSemanticsAsStringLookup()
	{
		var policy = new ExtensionSetInclusionPolicy(new SortedSet<string>(
			[".txt"],
			StringComparer.OrdinalIgnoreCase));

		Assert.True(policy.AllowsExtension(".TXT".AsSpan()));
		Assert.Equal(policy.AllowsExtension(".txt"), policy.AllowsExtension(".txt".AsSpan()));
	}
}
