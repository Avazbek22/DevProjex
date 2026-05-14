namespace DevProjex.Kernel.Models;

public sealed class ExtensionSetInclusionPolicy(IReadOnlySet<string> allowedExtensions)
	: IExtensionInclusionPolicy
{
	public bool AllowsExtension(string extension) =>
		!string.IsNullOrWhiteSpace(extension) &&
		allowedExtensions.Contains(extension);
}
