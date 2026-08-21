namespace DevProjex.Avalonia.Coordinators;

internal static class ProjectTreeSelectionOperations
{
	public static bool HasSelectionOtherThan(
		IList<TreeNodeViewModel> roots,
		TreeNodeViewModel target)
	{
		ArgumentNullException.ThrowIfNull(roots);
		ArgumentNullException.ThrowIfNull(target);

		var selected = new HashSet<string>(PathComparer.Default);
		foreach (var root in roots)
			root.CollectCheckedPaths(selected);

		return selected.Any(path => !PathComparer.Default.Equals(path, target.FullPath));
	}

	public static bool SelectOnly(
		IList<TreeNodeViewModel> roots,
		TreeNodeViewModel target)
	{
		ArgumentNullException.ThrowIfNull(roots);
		ArgumentNullException.ThrowIfNull(target);

		if (IsOnlySelectedPath(roots, target.FullPath))
			return false;

		foreach (var root in roots)
			root.SetCheckedForTreeStateRestore(false);

		target.IsChecked = true;
		return true;
	}

	private static bool IsOnlySelectedPath(
		IList<TreeNodeViewModel> roots,
		string targetPath)
	{
		var selected = new HashSet<string>(PathComparer.Default);
		foreach (var root in roots)
			root.CollectCheckedPaths(selected);

		return selected.Count == 1 && selected.Contains(targetPath);
	}
}
