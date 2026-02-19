namespace DevProjex.Tests.Integration;

public sealed class ElevationServiceTests
{
	// Verifies non-Windows environments report no admin privileges.
	[Fact]
	public void IsAdministrator_ReturnsFalseOnNonWindows()
	{
		var service = new ElevationService();

		if (!OperatingSystem.IsWindows())
			Assert.False(service.IsAdministrator);
	}

	// Verifies non-Windows environments cannot relaunch with elevation.
	[Fact]
	public void TryRelaunchAsAdministrator_ReturnsFalseOnNonWindows()
	{
		var service = new ElevationService();

		if (!OperatingSystem.IsWindows())
			Assert.False(service.TryRelaunchAsAdministrator(CommandLineOptions.Empty));
	}
}
