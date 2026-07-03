namespace DevProjex.Tests.Unit;

public sealed class IgnoreDecisionEngineContractTests
{
	[Fact]
	public void EvaluateDirectory_GitIgnoreWithoutTraversal_OwnsEveryOverlap()
	{
		var rules = CreateRules(
			ignoreHiddenFolders: true,
			ignoreDotFolders: true,
			smartFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cache" });

		var decision = IgnoreDecisionEngine.EvaluateDirectory(
			fullPath: @"C:\repo\.cache",
			name: ".cache",
			isHidden: true,
			rules,
			new IgnoreRules.GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: false));

		Assert.Equal(IgnoreDecisionOwner.GitIgnore, decision.Owner);
	}

	[Fact]
	public void EvaluateDirectory_GitIgnoreWithTraversal_DoesNotOwnDirectoryItself()
	{
		var rules = CreateRules(ignoreHiddenFolders: true, ignoreDotFolders: true);

		var decision = IgnoreDecisionEngine.EvaluateDirectory(
			fullPath: @"C:\repo\.cache",
			name: ".cache",
			isHidden: true,
			rules,
			new IgnoreRules.GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: true));

		Assert.Equal(IgnoreDecisionOwner.DotFolders, decision.Owner);
	}

	[Fact]
	public void EvaluateDirectory_SmartIgnoreOwnsBeforeBasicDirectoryRules()
	{
		var rules = CreateRules(
			ignoreHiddenFolders: true,
			ignoreDotFolders: true,
			smartFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cache" });

		var decision = IgnoreDecisionEngine.EvaluateDirectory(
			fullPath: @"C:\repo\.cache",
			name: ".cache",
			isHidden: true,
			rules,
			IgnoreRules.GitIgnoreEvaluation.NotIgnored);

		Assert.Equal(IgnoreDecisionOwner.SmartIgnore, decision.Owner);
	}

	[Fact]
	public void EvaluateDirectory_DotFoldersOwnsDotHiddenOverlapBeforeHiddenFolders()
	{
		var rules = CreateRules(ignoreHiddenFolders: true, ignoreDotFolders: true);

		var decision = IgnoreDecisionEngine.EvaluateDirectory(
			fullPath: @"C:\repo\.idea",
			name: ".idea",
			isHidden: true,
			rules,
			IgnoreRules.GitIgnoreEvaluation.NotIgnored);

		Assert.Equal(IgnoreDecisionOwner.DotFolders, decision.Owner);
	}

	[Fact]
	public void EvaluateFile_ControllerAndBasicPriority_IsStable()
	{
		var rules = CreateRules(
			ignoreHiddenFiles: true,
			ignoreDotFiles: true,
			ignoreEmptyFiles: true,
			ignoreExtensionlessFiles: true,
			smartFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".env" });

		Assert.Equal(
			IgnoreDecisionOwner.GitIgnore,
			IgnoreDecisionEngine.EvaluateFile(
				@"C:\repo\.env",
				".env",
				isHidden: true,
				length: 0,
				rules,
				shouldApplySmartIgnore: true,
				new IgnoreRules.GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: false)).Owner);

		Assert.Equal(
			IgnoreDecisionOwner.SmartIgnore,
			IgnoreDecisionEngine.EvaluateFile(
				@"C:\repo\.env",
				".env",
				isHidden: true,
				length: 0,
				rules,
				shouldApplySmartIgnore: true,
				IgnoreRules.GitIgnoreEvaluation.NotIgnored).Owner);

		Assert.Equal(
			IgnoreDecisionOwner.DotFiles,
			IgnoreDecisionEngine.EvaluateFile(
				@"C:\repo\.env",
				".env",
				isHidden: true,
				length: 0,
				rules,
				shouldApplySmartIgnore: false,
				IgnoreRules.GitIgnoreEvaluation.NotIgnored).Owner);

		Assert.Equal(
			IgnoreDecisionOwner.ExtensionlessFiles,
			IgnoreDecisionEngine.EvaluateFile(
				@"C:\repo\Dockerfile",
				"Dockerfile",
				isHidden: true,
				length: 0,
				rules,
				shouldApplySmartIgnore: false,
				IgnoreRules.GitIgnoreEvaluation.NotIgnored).Owner);

		Assert.Equal(
			IgnoreDecisionOwner.EmptyFiles,
			IgnoreDecisionEngine.EvaluateFile(
				@"C:\repo\readme.txt",
				"readme.txt",
				isHidden: true,
				length: 0,
				rules,
				shouldApplySmartIgnore: false,
				IgnoreRules.GitIgnoreEvaluation.NotIgnored).Owner);

		Assert.Equal(
			IgnoreDecisionOwner.HiddenFiles,
			IgnoreDecisionEngine.EvaluateFile(
				@"C:\repo\readme.txt",
				"readme.txt",
				isHidden: true,
				length: 10,
				rules,
				shouldApplySmartIgnore: false,
				IgnoreRules.GitIgnoreEvaluation.NotIgnored).Owner);
	}

	[Fact]
	public void EvaluateFileWithoutControllers_MatchesRuntimeFileDecisionWhenControllersAreDisabled()
	{
		var names = new[] { ".env", "Dockerfile", "readme.txt", "file.", "archive.tar.gz" };
		var bools = new[] { false, true };

		foreach (var name in names)
		foreach (var isHidden in bools)
		foreach (var isEmpty in bools)
		foreach (var ignoreHidden in bools)
		foreach (var ignoreDot in bools)
		foreach (var ignoreEmpty in bools)
		foreach (var ignoreExtensionless in bools)
		{
			var rules = CreateRules(
				ignoreHiddenFiles: ignoreHidden,
				ignoreDotFiles: ignoreDot,
				ignoreEmptyFiles: ignoreEmpty,
				ignoreExtensionlessFiles: ignoreExtensionless);
			var length = isEmpty ? 0 : 10;
			var expected = IgnoreDecisionEngine.EvaluateFile(
				$@"C:\repo\{name}",
				name,
				isHidden,
				length,
				rules,
				shouldApplySmartIgnore: false,
				IgnoreRules.GitIgnoreEvaluation.NotIgnored);
			var actual = IgnoreDecisionEngine.EvaluateFileWithoutControllers(
				name,
				isHidden,
				isEmpty,
				IgnoreRuleSemantics.IsExtensionlessFileName(name),
				ignoreHidden,
				ignoreDot,
				ignoreEmpty,
				ignoreExtensionless);

			Assert.Equal(expected.Owner, actual.Owner);
		}
	}

	[Theory]
	[InlineData("", false)]
	[InlineData(".", false)]
	[InlineData(".env", false)]
	[InlineData("Dockerfile", true)]
	[InlineData("file.", true)]
	[InlineData("readme.txt", false)]
	[InlineData("archive.tar.gz", false)]
	public void IsExtensionlessFileName_EdgeCases_AreExplicitlyLocked(string name, bool expected)
	{
		Assert.Equal(expected, IgnoreRuleSemantics.IsExtensionlessFileName(name));
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
}
