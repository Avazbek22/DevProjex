namespace DevProjex.Application.Selection;

public sealed class ExtensionSelectionInclusionPolicy(
	SelectionStateResolver selectionStateResolver,
	bool defaultForNewExtension)
	: IExtensionInclusionPolicy
{
	public bool AllowsExtension(string extension) =>
		!string.IsNullOrWhiteSpace(extension) &&
		selectionStateResolver.Resolve(extension, defaultForNewExtension);

	public bool AllowsExtension(ReadOnlySpan<char> extension) =>
		!extension.IsWhiteSpace() &&
		selectionStateResolver.Resolve(extension.ToString(), defaultForNewExtension);
}
