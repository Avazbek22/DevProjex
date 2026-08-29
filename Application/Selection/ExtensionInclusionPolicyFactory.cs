namespace DevProjex.Application.Selection;

public static class ExtensionInclusionPolicyFactory
{
	public static IExtensionInclusionPolicy? Create(SelectionRefreshContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		return Create(
			context.ExtensionSelectionIsExplicit,
			forceAllExtensionsChecked:
				context.PreparedSelectionMode != PreparedSelectionMode.Profile &&
				context.AllExtensionsChecked,
			context.ExtensionsSelectionInitialized,
			context.ExtensionsSelectionCache,
			context.ExtensionOptionStateCache);
	}

	public static IExtensionInclusionPolicy? Create(
		bool selectionIsExplicit,
		bool forceAllExtensionsChecked,
		bool selectionInitialized,
		IReadOnlySet<string> selectedExtensions,
		IReadOnlyDictionary<string, bool>? knownStates)
	{
		ArgumentNullException.ThrowIfNull(selectedExtensions);
		if (!selectionInitialized)
			return null;
		if (selectionIsExplicit)
			return new ExtensionSetInclusionPolicy(selectedExtensions);
		if (forceAllExtensionsChecked)
			return null;

		return new ExtensionSelectionInclusionPolicy(
			new SelectionStateResolver(selectedExtensions, knownStates),
			defaultForNewExtension: knownStates is not null);
	}
}
