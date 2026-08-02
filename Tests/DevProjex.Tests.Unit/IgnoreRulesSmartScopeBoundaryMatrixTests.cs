namespace DevProjex.Tests.Unit;

public sealed class IgnoreRulesSmartScopeBoundaryMatrixTests
{
	public static IEnumerable<object[]> ScopeBoundaryCases()
	{
		var root = BuildScopeRoot();
		var inside = Path.Combine(root, "src", "Program.cs");
		var same = root;
		var siblingPrefixTrap = Path.Combine(Path.GetDirectoryName(root)!, "application", "file.cs");
		var outside = Path.Combine(Path.GetDirectoryName(root)!, "lib", "file.cs");

		yield return [inside, true];
		yield return [same, true];
		yield return [siblingPrefixTrap, false];
		yield return [outside, false];

		var upper = inside.ToUpperInvariant();
		var lower = inside.ToLowerInvariant();
		var expectedCaseMatch = OperatingSystem.IsWindows();
		yield return [upper, expectedCaseMatch];
		yield return [lower, expectedCaseMatch];

		if (Path.DirectorySeparatorChar != Path.AltDirectorySeparatorChar)
		{
			// Both Windows directory separators identify the same absolute scope.
			var alt = inside.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			yield return [alt, true];
		}
	}

	[Theory]
	[MemberData(nameof(ScopeBoundaryCases))]
	public void ShouldApplySmartIgnore_BoundaryAndCaseMatrix_IsCorrect(string candidatePath, bool expected)
	{
		var scopeRoot = BuildScopeRoot();
		var rules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			UseSmartIgnore = true,
			SmartIgnoreScopeRoots = [scopeRoot]
		};

		Assert.Equal(expected, rules.ShouldApplySmartIgnore(candidatePath));
	}

	[Fact]
	public void ShouldApplySmartIgnore_WhenDisabled_IsAlwaysFalse()
	{
		var scopeRoot = BuildScopeRoot();
		var rules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>())
		{
			UseSmartIgnore = false,
			SmartIgnoreScopeRoots = [scopeRoot]
		};

		Assert.False(rules.ShouldApplySmartIgnore(Path.Combine(scopeRoot, "src", "a.cs")));
		Assert.False(rules.ShouldApplySmartIgnore(Path.Combine(Path.GetDirectoryName(scopeRoot)!, "other", "a.cs")));
	}

	[Fact]
	public void ShouldApplySmartIgnore_WithoutScopes_AllowsAnyPath()
	{
		var rules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>())
		{
			UseSmartIgnore = true,
			SmartIgnoreScopeRoots = []
		};

		Assert.True(rules.ShouldApplySmartIgnore("/any/path/file.txt"));
	}

	private static string BuildScopeRoot()
		=> Path.Combine(Path.GetTempPath(), "DevProjexTests", "IgnoreRulesSmartScope", "app");
}
