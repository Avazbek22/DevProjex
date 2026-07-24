namespace DevProjex.Tests.Unit;

public sealed class IgnoreRulesSmartScopePathMatrixTests
{
	[Theory]
	[MemberData(nameof(SmartScopeCases))]
	public void ShouldApplySmartIgnore_PathMatrix(
		int caseId,
		bool useSmartIgnore,
		bool useEmptyScopeRoots,
		string pathKind,
		bool isDirectory,
		bool expected)
	{
		using var temp = new TemporaryDirectory();
		var scopeRoot = temp.CreateFolder("scope-root");
		var outsideRoot = temp.CreateFolder("outside-root");
		var rules = CreateRules(useSmartIgnore, useEmptyScopeRoots ? [] : [scopeRoot]);

		var path = ResolvePath(pathKind, scopeRoot, outsideRoot);

		var actual = rules.ShouldApplySmartIgnore(path, isDirectory);

		Assert.Equal(expected, actual);
		Assert.True(caseId >= 0);
	}

	[Fact]
	public void ShouldApplySmartIgnore_MultipleScopeRoots_MatchesSecondScope()
	{
		using var temp = new TemporaryDirectory();
		var first = temp.CreateFolder("first-scope");
		var second = temp.CreateFolder("second-scope");
		var filePath = Path.Combine(second, "src", "App.cs");
		var rules = CreateRules(useSmartIgnore: true, [first, second]);

		var applies = rules.ShouldApplySmartIgnore(filePath, isDirectory: false);

		Assert.True(applies);
	}

	[Fact]
	public void ShouldApplySmartIgnore_ScopedRules_RelativeFilePathReturnsFalse()
	{
		using var temp = new TemporaryDirectory();
		var scopeRoot = temp.CreateFolder("scope");
		var rules = CreateRules(useSmartIgnore: true, [scopeRoot]);

		var applies = rules.ShouldApplySmartIgnore("Program.cs", isDirectory: false);

		Assert.False(applies);
	}

	[Fact]
	public void ShouldApplySmartIgnore_EmptyScopeRoots_WhitespacePathReturnsTrue()
	{
		var rules = CreateRules(useSmartIgnore: true, []);

		var applies = rules.ShouldApplySmartIgnore("   ", isDirectory: false);

		// Empty scope roots are treated as "apply globally".
		Assert.True(applies);
	}

	[Fact]
	public void ShouldApplySmartIgnore_CaseVariance_FollowsPlatformPathComparison()
	{
		using var temp = new TemporaryDirectory();
		var scopeRoot = temp.CreateFolder("ScopeCase");
		var rules = CreateRules(useSmartIgnore: true, [scopeRoot]);
		var candidate = scopeRoot.ToUpperInvariant();
		var expected = OperatingSystem.IsWindows() ||
		               string.Equals(scopeRoot, candidate, StringComparison.Ordinal);

		var applies = rules.ShouldApplySmartIgnore(candidate, isDirectory: true);

		Assert.Equal(expected, applies);
	}

	public static IEnumerable<object[]> SmartScopeCases()
	{
		var caseId = 0;
		var pathKinds = new[]
		{
			"inside_dir",
			"inside_file",
			"outside_dir",
			"outside_file",
			"relative_file",
			"whitespace"
		};

		foreach (var pathKind in pathKinds)
		{
			yield return [caseId++, false, false, pathKind, true, false];
			yield return [caseId++, false, true, pathKind, false, false];
		}

		// Empty scope roots => global Smart Ignore applicability.
		yield return [caseId++, true, true, "inside_dir", true, true];
		yield return [caseId++, true, true, "inside_file", false, true];
		yield return [caseId++, true, true, "outside_dir", true, true];
		yield return [caseId++, true, true, "outside_file", false, true];
		yield return [caseId++, true, true, "whitespace", false, true];

		// Scoped roots => only in-scope paths apply.
		yield return [caseId++, true, false, "inside_dir", true, true];
		yield return [caseId++, true, false, "inside_file", false, true];
		yield return [caseId++, true, false, "inside_file", true, true];
		yield return [caseId++, true, false, "outside_dir", true, false];
		yield return [caseId++, true, false, "outside_file", false, false];
		yield return [caseId++, true, false, "relative_file", false, false];
		yield return [caseId++, true, false, "whitespace", false, false];
	}

	private static string ResolvePath(string pathKind, string scopeRoot, string outsideRoot)
	{
		return pathKind switch
		{
			"inside_dir" => Path.Combine(scopeRoot, "src"),
			"inside_file" => Path.Combine(scopeRoot, "src", "Program.cs"),
			"outside_dir" => Path.Combine(outsideRoot, "src"),
			"outside_file" => Path.Combine(outsideRoot, "src", "Program.cs"),
			"relative_file" => "Program.cs",
			"whitespace" => "   ",
			_ => throw new ArgumentOutOfRangeException(nameof(pathKind), pathKind, "Unknown path kind.")
		};
	}

	private static IgnoreRules CreateRules(bool useSmartIgnore, IReadOnlyList<string> scopeRoots)
	{
		return new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			UseSmartIgnore = useSmartIgnore,
			SmartIgnoreScopeRoots = scopeRoots
		};
	}
}
