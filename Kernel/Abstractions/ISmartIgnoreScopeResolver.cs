namespace DevProjex.Kernel.Abstractions;

public interface ISmartIgnoreScopeResolver
{
	bool IsIgnoredDirectory(string fullPath, string name);

	bool IsIgnoredFile(string fullPath, string name);
}
