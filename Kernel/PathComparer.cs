namespace DevProjex.Kernel;

public static class PathComparer
{
	public static StringComparer Default => OperatingSystem.IsWindows()
		? StringComparer.OrdinalIgnoreCase
		: StringComparer.Ordinal;
}
