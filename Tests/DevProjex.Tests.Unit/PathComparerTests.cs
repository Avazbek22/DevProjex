namespace DevProjex.Tests.Unit;

public sealed class PathComparerTests
{
	[Fact]
	public void Default_UsesOrdinalIgnoreCaseOnWindows()
	{
		if (!OperatingSystem.IsWindows())
			return;

		Assert.Same(StringComparer.OrdinalIgnoreCase, PathComparer.Default);
	}

	[Fact]
	public void Default_UsesOrdinalOnNonWindows()
	{
		if (OperatingSystem.IsWindows())
			return;

		Assert.Same(StringComparer.Ordinal, PathComparer.Default);
	}
}
