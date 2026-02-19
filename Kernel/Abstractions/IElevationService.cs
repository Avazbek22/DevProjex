namespace DevProjex.Kernel.Abstractions;

public interface IElevationService
{
	bool IsAdministrator { get; }
	bool TryRelaunchAsAdministrator(CommandLineOptions options);
}
