namespace DevProjex.Terminal.Tui;

internal static class TerminalRepositorySessionOwnership
{
	public static bool TryPublishAndReplace(
		bool operationIsCurrent,
		Action publishWorkspace,
		ref IRepositoryCacheSession? current,
		IRepositoryCacheSession? candidate)
	{
		ArgumentNullException.ThrowIfNull(publishWorkspace);
		if (!operationIsCurrent)
			return false;

		publishWorkspace();
		var previous = Interlocked.Exchange(ref current, candidate);
		previous?.Dispose();
		return true;
	}
}
