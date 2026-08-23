namespace DevProjex.Tests.Unit;

public sealed class SelectionEvolutionPolicyTests
{
	[Fact]
	public void Reconcile_DefaultsNewItemsAndPreservesKnownStates()
	{
		var result = SelectionEvolutionPolicy.Reconcile(
			["known-on", "known-off", "new"],
			new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
			{
				["known-on"] = true,
				["known-off"] = false
			},
			static _ => true,
			StringComparer.OrdinalIgnoreCase);

		Assert.Contains("known-on", result.SelectedItems);
		Assert.DoesNotContain("known-off", result.SelectedItems);
		Assert.Contains("new", result.SelectedItems);
		Assert.True(result.KnownStates["new"]);
	}

	[Fact]
	public void Reconcile_PreservesMissingStatesUntilItemsReturn()
	{
		var hidden = SelectionEvolutionPolicy.Reconcile(
			["visible"],
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "visible" },
			new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
			{
				["visible"] = true,
				["returning"] = false
			},
			static _ => true,
			StringComparer.OrdinalIgnoreCase);

		var returned = SelectionEvolutionPolicy.Reconcile(
			["visible", "returning"],
			hidden.SelectedItems,
			hidden.KnownStates,
			static _ => true,
			StringComparer.OrdinalIgnoreCase);

		Assert.DoesNotContain("returning", returned.SelectedItems);
		Assert.False(returned.KnownStates["returning"]);
	}

	[Fact]
	public void Reconcile_PromotesLegacySelectedItemsIntoKnownState()
	{
		var result = SelectionEvolutionPolicy.Reconcile(
			["current"],
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "temporarily-hidden" },
			new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
			static _ => false,
			StringComparer.OrdinalIgnoreCase);

		Assert.True(result.KnownStates["temporarily-hidden"]);
		Assert.False(result.KnownStates["current"]);
	}

	[Fact]
	public void Reconcile_UsesTheSuppliedComparerForEveryOutput()
	{
		var result = SelectionEvolutionPolicy.Reconcile(
			[".cs", ".CS"],
			new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
			{
				[".CS"] = false
			},
			static _ => true,
			StringComparer.OrdinalIgnoreCase);

		Assert.Empty(result.SelectedItems);
		Assert.Single(result.KnownStates);
	}
}
