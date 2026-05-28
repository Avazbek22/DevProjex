using DevProjex.Application.Models;

namespace DevProjex.Application.Selection;

public static class ExtensionSnapshotReusePolicy
{
	public static bool CanReuseSnapshot(
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		IReadOnlyList<SelectionOption> resolvedOptions)
	{
		if (resolvedOptions.Count == 0)
			return true;

		if (effectiveExtensionPolicy is null)
			return AllResolvedExtensionsAreChecked(resolvedOptions);

		foreach (var option in resolvedOptions)
		{
			if (effectiveExtensionPolicy.AllowsExtension(option.Name) != option.IsChecked)
				return false;
		}

		return true;
	}

	private static bool AllResolvedExtensionsAreChecked(IReadOnlyList<SelectionOption> options)
	{
		foreach (var option in options)
		{
			if (!option.IsChecked)
				return false;
		}

		return true;
	}
}
