namespace DevProjex.Kernel.Abstractions;

public readonly record struct SmartIgnoreScopeDecision(bool IsResolved, bool IsIgnored)
{
	public static SmartIgnoreScopeDecision Unresolved { get; } = new(false, false);

	public static SmartIgnoreScopeDecision Include { get; } = new(true, false);

	public static SmartIgnoreScopeDecision Exclude { get; } = new(true, true);
}

public interface ISmartIgnoreScopeResolver
{
	SmartIgnoreScopeDecision EvaluateDirectory(string fullPath, string name);

	SmartIgnoreScopeDecision EvaluateFile(string fullPath, string name);
}
