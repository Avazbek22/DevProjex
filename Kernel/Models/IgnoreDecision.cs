namespace DevProjex.Kernel.Models;

public enum IgnoreDecisionOwner
{
	None = 0,
	GitIgnore,
	SmartIgnore,
	DotFolders,
	DotFiles,
	HiddenFolders,
	HiddenFiles,
	ExtensionlessFiles,
	EmptyFiles
}

public readonly record struct IgnoreDecision(IgnoreDecisionOwner Owner)
{
	public static readonly IgnoreDecision Visible = new(IgnoreDecisionOwner.None);

	public bool IsIgnored => Owner != IgnoreDecisionOwner.None;
}

public static class IgnoreDecisionEngine
{
	public static IgnoreDecision EvaluateDirectory(
		string fullPath,
		string name,
		bool isHidden,
		IgnoreRules rules,
		in IgnoreRules.GitIgnoreEvaluation gitIgnoreEvaluation)
	{
		// Git can require traversal for negated descendants. In that case the directory
		// remains visible so every pipeline can reach the unignored child entries.
		if (gitIgnoreEvaluation.IsIgnored && !gitIgnoreEvaluation.ShouldTraverseIgnoredDirectory)
			return new IgnoreDecision(IgnoreDecisionOwner.GitIgnore);

		if (rules.IsSmartIgnoredDirectory(fullPath, name))
			return new IgnoreDecision(IgnoreDecisionOwner.SmartIgnore);

		var isDot = IgnoreRuleSemantics.IsDotName(name);
		if (IgnoreRuleSemantics.ShouldIgnoreDotDirectory(rules.IgnoreDotFolders, isDot))
			return new IgnoreDecision(IgnoreDecisionOwner.DotFolders);

		if (IgnoreRuleSemantics.ShouldIgnoreHiddenDirectory(
			    rules.IgnoreHiddenFolders,
			    isHidden,
			    isDot,
			    rules.IgnoreDotFolders))
		{
			return new IgnoreDecision(IgnoreDecisionOwner.HiddenFolders);
		}

		return IgnoreDecision.Visible;
	}

	public static IgnoreDecision EvaluateFile(
		string fullPath,
		string name,
		bool isHidden,
		long length,
		IgnoreRules rules,
		bool shouldApplySmartIgnore,
		in IgnoreRules.GitIgnoreEvaluation gitIgnoreEvaluation)
	{
		if (gitIgnoreEvaluation.IsIgnored)
			return new IgnoreDecision(IgnoreDecisionOwner.GitIgnore);

		if (rules.IsSmartIgnoredFile(fullPath, name, shouldApplySmartIgnore))
			return new IgnoreDecision(IgnoreDecisionOwner.SmartIgnore);

		var isDot = IgnoreRuleSemantics.IsDotName(name);
		if (IgnoreRuleSemantics.ShouldIgnoreDotFile(rules.IgnoreDotFiles, isDot))
			return new IgnoreDecision(IgnoreDecisionOwner.DotFiles);

		if (rules.IgnoreExtensionlessFiles && IgnoreRuleSemantics.IsExtensionlessFileName(name))
			return new IgnoreDecision(IgnoreDecisionOwner.ExtensionlessFiles);

		if (rules.IgnoreEmptyFiles && length == 0)
			return new IgnoreDecision(IgnoreDecisionOwner.EmptyFiles);

		if (IgnoreRuleSemantics.ShouldIgnoreHiddenFile(
			    rules.IgnoreHiddenFiles,
			    isHidden,
			    isDot,
			    rules.IgnoreDotFiles))
		{
			return new IgnoreDecision(IgnoreDecisionOwner.HiddenFiles);
		}

		return IgnoreDecision.Visible;
	}

	public static IgnoreDecision EvaluateFileWithoutControllers(
		string name,
		bool isHidden,
		bool isEmpty,
		bool isExtensionless,
		bool ignoreHiddenFiles,
		bool ignoreDotFiles,
		bool ignoreEmptyFiles,
		bool ignoreExtensionlessFiles)
	{
		var isDot = IgnoreRuleSemantics.IsDotName(name);
		if (IgnoreRuleSemantics.ShouldIgnoreDotFile(ignoreDotFiles, isDot))
			return new IgnoreDecision(IgnoreDecisionOwner.DotFiles);

		if (ignoreExtensionlessFiles && isExtensionless)
			return new IgnoreDecision(IgnoreDecisionOwner.ExtensionlessFiles);

		if (ignoreEmptyFiles && isEmpty)
			return new IgnoreDecision(IgnoreDecisionOwner.EmptyFiles);

		if (IgnoreRuleSemantics.ShouldIgnoreHiddenFile(
			    ignoreHiddenFiles,
			    isHidden,
			    isDot,
			    ignoreDotFiles))
		{
			return new IgnoreDecision(IgnoreDecisionOwner.HiddenFiles);
		}

		return IgnoreDecision.Visible;
	}
}
