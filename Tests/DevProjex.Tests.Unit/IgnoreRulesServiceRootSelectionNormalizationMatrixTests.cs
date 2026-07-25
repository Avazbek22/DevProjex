namespace DevProjex.Tests.Unit;

public sealed class IgnoreRulesServiceRootSelectionNormalizationMatrixTests
{
	public static IEnumerable<object[]> SelectionNormalizationCases()
	{
		foreach (var entry in BuildCaseEntries())
		{
			foreach (var variant in BuildSelectionVariants(entry.CanonicalSelectedRoots))
				yield return [entry.Name, variant, entry.CanonicalSelectedRoots];
		}
	}

	[Theory]
	[MemberData(nameof(SelectionNormalizationCases))]
	public void IgnoreRulesService_SelectedRootsNormalization_DuplicateAndOrderVariants_AreStable(
		string _,
		IReadOnlyCollection<string> selectedRootsVariant,
		IReadOnlyCollection<string> canonicalSelectedRoots)
	{
		using var temp = new TemporaryDirectory();
		SeedWorkspace(temp);

		var service = CreateService();
		var baselineAvailability = service.GetIgnoreOptionsAvailability(temp.Path, canonicalSelectedRoots);
		var variantAvailability = service.GetIgnoreOptionsAvailability(temp.Path, selectedRootsVariant);

		Assert.Equal(baselineAvailability.IncludeGitIgnore, variantAvailability.IncludeGitIgnore);
		Assert.Equal(baselineAvailability.IncludeSmartIgnore, variantAvailability.IncludeSmartIgnore);

		var selectedOptions = new[] { IgnoreOptionId.UseGitIgnore, IgnoreOptionId.SmartIgnore };
		var baselineRules = service.Build(temp.Path, selectedOptions, canonicalSelectedRoots);
		var variantRules = service.Build(temp.Path, selectedOptions, selectedRootsVariant);

		Assert.Equal(baselineRules.UseGitIgnore, variantRules.UseGitIgnore);
		Assert.Equal(baselineRules.UseSmartIgnore, variantRules.UseSmartIgnore);

		Assert.Equal(
			baselineRules.ScopedGitIgnoreMatchers.Select(m => m.ScopeRootPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
			variantRules.ScopedGitIgnoreMatchers.Select(m => m.ScopeRootPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

		Assert.Equal(
			baselineRules.SmartIgnoreScopeRoots.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
			variantRules.SmartIgnoreScopeRoots.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
	}

	public static IEnumerable<object[]> CanonicalBehaviorCases()
	{
		yield return ["git-only", new[] { "alpha" }, true, true, true, true];
		yield return ["smart-only", new[] { "beta" }, false, true, false, true];
		yield return ["mixed-git-smart", new[] { "alpha", "beta" }, true, true, true, true];
		yield return ["nested-smart", new[] { "gamma" }, false, true, false, true];
	}

	[Theory]
	[MemberData(nameof(CanonicalBehaviorCases))]
	public void IgnoreRulesService_CanonicalSelections_ExposeExpectedAvailabilityAndRuntimeBehavior(
		string _,
		IReadOnlyCollection<string> selectedRoots,
		bool expectedIncludeGitIgnore,
		bool expectedIncludeSmartIgnore,
		bool expectedRuntimeUseGitIgnore,
		bool expectedRuntimeUseSmartIgnore)
	{
		using var temp = new TemporaryDirectory();
		SeedWorkspace(temp);

		var service = CreateService();
		var availability = service.GetIgnoreOptionsAvailability(temp.Path, selectedRoots);
		Assert.Equal(expectedIncludeGitIgnore, availability.IncludeGitIgnore);
		Assert.Equal(expectedIncludeSmartIgnore, availability.IncludeSmartIgnore);

		var rules = service.Build(
			temp.Path,
			[IgnoreOptionId.UseGitIgnore, IgnoreOptionId.SmartIgnore],
			selectedRoots);

		Assert.Equal(expectedRuntimeUseGitIgnore, rules.UseGitIgnore);
		Assert.Equal(expectedRuntimeUseSmartIgnore, rules.UseSmartIgnore);
	}

	private static IReadOnlyCollection<string>[] BuildSelectionVariants(IReadOnlyCollection<string> canonical)
	{
		var variants = new List<IReadOnlyCollection<string>>
		{
			canonical.ToArray(),
			canonical.Concat(canonical).ToArray(),
			canonical.SelectMany(x => new[] { x, x }).ToArray()
		};

		if (canonical.Count > 1)
		{
			variants.Add(canonical.Reverse().ToArray());
			variants.Add(canonical.Reverse().Concat(canonical).ToArray());
		}

		return variants.ToArray();
	}

	private static IReadOnlyList<SelectionCaseEntry> BuildCaseEntries()
	{
		return
		[
			new SelectionCaseEntry("git-only", ["alpha"]),
			new SelectionCaseEntry("smart-only", ["beta"]),
			new SelectionCaseEntry("mixed-git-smart", ["alpha", "beta"]),
			new SelectionCaseEntry("nested-smart", ["gamma"]),
			new SelectionCaseEntry("mixed-with-unknown", ["alpha", "missing", "beta"])
		];
	}

	private static IgnoreRulesService CreateService()
	{
		return new IgnoreRulesService(new SmartIgnoreService([
			new FixedSmartIgnoreRule(["node_modules", "dist", "target"], [])
		]));
	}

	private static void SeedWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile("alpha/.gitignore", "target/\n");
		temp.CreateFile("alpha/App.csproj", "<Project />\n");
		temp.CreateFile("alpha/target/debug/a.dll", "bin");

		temp.CreateFile("beta/package.json", "{}\n");
		temp.CreateFile("beta/node_modules/pkg/index.js", "module.exports = 1;\n");

		temp.CreateFile("gamma/inner/service/Service.csproj", "<Project />\n");
		temp.CreateFile("gamma/inner/service/bin/debug/service.dll", "bin");
	}

	private sealed record SelectionCaseEntry(string Name, IReadOnlyCollection<string> CanonicalSelectedRoots);

	private sealed class FixedSmartIgnoreRule(IReadOnlyCollection<string> folders, IReadOnlyCollection<string> files)
		: ISmartIgnoreRule
	{
		public SmartIgnoreResult Evaluate(string rootPath)
		{
			return new SmartIgnoreResult(
				new HashSet<string>(folders, StringComparer.OrdinalIgnoreCase),
				new HashSet<string>(files, StringComparer.OrdinalIgnoreCase));
		}
	}
}
