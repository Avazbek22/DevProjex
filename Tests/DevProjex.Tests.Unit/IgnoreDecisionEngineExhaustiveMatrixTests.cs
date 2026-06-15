namespace DevProjex.Tests.Unit;

public sealed class IgnoreDecisionEngineExhaustiveMatrixTests
{
	// This suite pins the single-entry ownership contract. It intentionally keeps
	// the expected model independent from production IgnoreRuleSemantics helpers so
	// a semantic regression cannot update both implementation and expectations at once.
	[Theory]
	[MemberData(nameof(DirectoryOwnershipCases))]
	public void EvaluateDirectory_OwnerPriorityMatrix_MatchesDocumentedContract(DirectoryOwnershipCase testCase)
	{
		var rules = CreateRules(
			ignoreHiddenFolders: testCase.IgnoreHiddenFolders,
			ignoreDotFolders: testCase.IgnoreDotFolders,
			smartFolders: testCase.SmartMatches
				? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { testCase.Name }
				: null);

		var decision = IgnoreDecisionEngine.EvaluateDirectory(
			$@"C:\repo\{testCase.Name}",
			testCase.Name,
			testCase.IsHidden,
			rules,
			testCase.GitIgnore);

		Assert.Equal(ExpectedDirectoryOwner(testCase), decision.Owner);
	}

	[Theory]
	[MemberData(nameof(FileOwnershipCases))]
	public void EvaluateFile_OwnerPriorityMatrix_MatchesDocumentedContract(FileOwnershipCase testCase)
	{
		var rules = CreateRules(
			ignoreHiddenFiles: testCase.IgnoreHiddenFiles,
			ignoreDotFiles: testCase.IgnoreDotFiles,
			ignoreEmptyFiles: testCase.IgnoreEmptyFiles,
			ignoreExtensionlessFiles: testCase.IgnoreExtensionlessFiles,
			smartFiles: testCase.SmartMatches
				? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { testCase.Name }
				: null);

		var decision = IgnoreDecisionEngine.EvaluateFile(
			$@"C:\repo\{testCase.Name}",
			testCase.Name,
			testCase.IsHidden,
			testCase.Length,
			rules,
			testCase.SmartApplies,
			testCase.GitIgnore);

		Assert.Equal(ExpectedFileOwner(testCase), decision.Owner);
	}

	public static IEnumerable<object[]> DirectoryOwnershipCases()
	{
		// Dot+hidden overlap is the high-risk case: DotFolders must own it while the
		// dot toggle is active, and HiddenFolders may only own it after DotFolders is off.
		var directoryFacts = new[]
		{
			new DirectoryFacts(".dot-hidden", IsHidden: true),
			new DirectoryFacts(".dot-visible", IsHidden: false),
			new DirectoryFacts("hidden-folder", IsHidden: true),
			new DirectoryFacts("plain-folder", IsHidden: false)
		};
		var gitStates = new[]
		{
			IgnoreRules.GitIgnoreEvaluation.NotIgnored,
			new IgnoreRules.GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: false),
			new IgnoreRules.GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: true)
		};
		var bools = new[] { false, true };

		foreach (var facts in directoryFacts)
		foreach (var gitState in gitStates)
		foreach (var smartMatches in bools)
		foreach (var ignoreHiddenFolders in bools)
		foreach (var ignoreDotFolders in bools)
		{
			yield return
			[
				new DirectoryOwnershipCase(
					facts.Name,
					facts.IsHidden,
					gitState,
					smartMatches,
					ignoreHiddenFolders,
					ignoreDotFolders)
			];
		}
	}

	public static IEnumerable<object[]> FileOwnershipCases()
	{
		// These facts cover every file-level conflict pair: dot+hidden, empty+
		// extensionless, trailing-dot extensionless names, and ordinary hidden files.
		var fileFacts = new[]
		{
			new FileFacts(".env", IsHidden: true, Length: 0),
			new FileFacts(".env", IsHidden: false, Length: 10),
			new FileFacts("Dockerfile", IsHidden: true, Length: 0),
			new FileFacts("file.", IsHidden: false, Length: 0),
			new FileFacts("empty.txt", IsHidden: false, Length: 0),
			new FileFacts("hidden.txt", IsHidden: true, Length: 10),
			new FileFacts("readme.txt", IsHidden: false, Length: 10)
		};
		var flagSets = new[]
		{
			new FileRuleFlags(),
			new FileRuleFlags(IgnoreDotFiles: true),
			new FileRuleFlags(IgnoreHiddenFiles: true),
			new FileRuleFlags(IgnoreEmptyFiles: true),
			new FileRuleFlags(IgnoreExtensionlessFiles: true),
			new FileRuleFlags(IgnoreDotFiles: true, IgnoreHiddenFiles: true),
			new FileRuleFlags(IgnoreEmptyFiles: true, IgnoreExtensionlessFiles: true),
			new FileRuleFlags(IgnoreHiddenFiles: true, IgnoreDotFiles: true, IgnoreEmptyFiles: true, IgnoreExtensionlessFiles: true),
			new FileRuleFlags(IgnoreHiddenFiles: true, IgnoreEmptyFiles: true, IgnoreExtensionlessFiles: true)
		};
		var gitStates = new[]
		{
			IgnoreRules.GitIgnoreEvaluation.NotIgnored,
			new IgnoreRules.GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: false)
		};
		var bools = new[] { false, true };

		foreach (var facts in fileFacts)
		foreach (var flags in flagSets)
		foreach (var gitState in gitStates)
		foreach (var smartMatches in bools)
		foreach (var smartApplies in bools)
		{
			yield return
			[
				new FileOwnershipCase(
					facts.Name,
					facts.IsHidden,
					facts.Length,
					gitState,
					smartMatches,
					smartApplies,
					flags.IgnoreHiddenFiles,
					flags.IgnoreDotFiles,
					flags.IgnoreEmptyFiles,
					flags.IgnoreExtensionlessFiles)
			];
		}
	}

	private static IgnoreDecisionOwner ExpectedDirectoryOwner(DirectoryOwnershipCase testCase)
	{
		// This is the public ownership contract: controller rules own first, Git traversal
		// keeps directories visible for negated descendants, and DotFolders owns dot+hidden
		// overlap while the dot rule is enabled.
		if (testCase.GitIgnore.IsIgnored && !testCase.GitIgnore.ShouldTraverseIgnoredDirectory)
			return IgnoreDecisionOwner.GitIgnore;
		if (testCase.SmartMatches)
			return IgnoreDecisionOwner.SmartIgnore;

		var isDot = IsDotNameByContract(testCase.Name);
		if (testCase.IgnoreDotFolders && isDot)
			return IgnoreDecisionOwner.DotFolders;
		if (ShouldHiddenOwnEntryByContract(testCase.IgnoreHiddenFolders, testCase.IsHidden, isDot, testCase.IgnoreDotFolders))
			return IgnoreDecisionOwner.HiddenFolders;

		return IgnoreDecisionOwner.None;
	}

	private static IgnoreDecisionOwner ExpectedFileOwner(FileOwnershipCase testCase)
	{
		// File rules deliberately put Extensionless before Empty and Hidden last. This
		// matches Help 11.9 ownership semantics and prevents one physical file from being
		// counted by several visible basic rules at the same time.
		if (testCase.GitIgnore.IsIgnored)
			return IgnoreDecisionOwner.GitIgnore;
		if (testCase.SmartMatches && testCase.SmartApplies)
			return IgnoreDecisionOwner.SmartIgnore;

		var isDot = IsDotNameByContract(testCase.Name);
		if (testCase.IgnoreDotFiles && isDot)
			return IgnoreDecisionOwner.DotFiles;
		if (testCase.IgnoreExtensionlessFiles && IsExtensionlessFileNameByContract(testCase.Name))
			return IgnoreDecisionOwner.ExtensionlessFiles;
		if (testCase.IgnoreEmptyFiles && testCase.Length == 0)
			return IgnoreDecisionOwner.EmptyFiles;
		if (ShouldHiddenOwnEntryByContract(testCase.IgnoreHiddenFiles, testCase.IsHidden, isDot, testCase.IgnoreDotFiles))
			return IgnoreDecisionOwner.HiddenFiles;

		return IgnoreDecisionOwner.None;
	}

	private static bool ShouldHiddenOwnEntryByContract(
		bool ignoreHidden,
		bool isHidden,
		bool isDot,
		bool ignoreDotEntry)
	{
		if (!ignoreHidden || !isHidden)
			return false;

		if (!isDot)
			return true;

		if (ignoreDotEntry)
			return false;

		// Dot names are hidden by convention on Unix-like platforms. Hidden ownership
		// for dot entries is only a real filesystem Hidden attribute contract on Windows.
		return OperatingSystem.IsWindows();
	}

	private static bool IsDotNameByContract(string name) =>
		name.Length > 1 && name[0] == '.';

	private static bool IsExtensionlessFileNameByContract(string name)
	{
		var extension = Path.GetExtension(name);
		return string.IsNullOrEmpty(extension) || extension == ".";
	}

	private static IgnoreRules CreateRules(
		bool ignoreHiddenFolders = false,
		bool ignoreHiddenFiles = false,
		bool ignoreDotFolders = false,
		bool ignoreDotFiles = false,
		bool ignoreEmptyFiles = false,
		bool ignoreExtensionlessFiles = false,
		IReadOnlySet<string>? smartFolders = null,
		IReadOnlySet<string>? smartFiles = null)
	{
		return new IgnoreRules(
			ignoreHiddenFolders,
			ignoreHiddenFiles,
			ignoreDotFolders,
			ignoreDotFiles,
			smartFolders ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			smartFiles ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			UseSmartIgnore = smartFolders is not null || smartFiles is not null,
			IgnoreEmptyFiles = ignoreEmptyFiles,
			IgnoreExtensionlessFiles = ignoreExtensionlessFiles
		};
	}

	public sealed record DirectoryOwnershipCase(
		string Name,
		bool IsHidden,
		IgnoreRules.GitIgnoreEvaluation GitIgnore,
		bool SmartMatches,
		bool IgnoreHiddenFolders,
		bool IgnoreDotFolders);

	public sealed record FileOwnershipCase(
		string Name,
		bool IsHidden,
		long Length,
		IgnoreRules.GitIgnoreEvaluation GitIgnore,
		bool SmartMatches,
		bool SmartApplies,
		bool IgnoreHiddenFiles,
		bool IgnoreDotFiles,
		bool IgnoreEmptyFiles,
		bool IgnoreExtensionlessFiles);

	private sealed record DirectoryFacts(string Name, bool IsHidden);

	private sealed record FileFacts(string Name, bool IsHidden, long Length);

	private sealed record FileRuleFlags(
		bool IgnoreHiddenFiles = false,
		bool IgnoreDotFiles = false,
		bool IgnoreEmptyFiles = false,
		bool IgnoreExtensionlessFiles = false);
}
