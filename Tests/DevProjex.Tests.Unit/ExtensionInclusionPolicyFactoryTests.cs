namespace DevProjex.Tests.Unit;

public sealed class ExtensionInclusionPolicyFactoryTests
{
	[Fact]
	public void Create_ExplicitSelection_RemainsClosedForNewExtensions()
	{
		var policy = ExtensionInclusionPolicyFactory.Create(
			selectionIsExplicit: true,
			forceAllExtensionsChecked: true,
			selectionInitialized: true,
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
			new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
			{
				[".cs"] = true
			});

		Assert.NotNull(policy);
		Assert.True(policy.AllowsExtension(".cs"));
		Assert.False(policy.AllowsExtension(".md"));
	}

	[Fact]
	public void Create_SessionSelection_RemembersKnownStateAndEnablesNewExtensions()
	{
		var policy = ExtensionInclusionPolicyFactory.Create(
			selectionIsExplicit: false,
			forceAllExtensionsChecked: false,
			selectionInitialized: true,
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
			new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
			{
				[".cs"] = true,
				[".md"] = false
			});

		Assert.NotNull(policy);
		Assert.True(policy.AllowsExtension(".cs"));
		Assert.False(policy.AllowsExtension(".md"));
		Assert.True(policy.AllowsExtension(".json"));
	}

	[Fact]
	public void Create_UninitializedSelection_DoesNotFilterDiscovery()
	{
		var policy = ExtensionInclusionPolicyFactory.Create(
			selectionIsExplicit: false,
			forceAllExtensionsChecked: false,
			selectionInitialized: false,
			new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			knownStates: null);

		Assert.Null(policy);
	}
}
