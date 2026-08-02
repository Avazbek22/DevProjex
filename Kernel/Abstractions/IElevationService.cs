namespace DevProjex.Kernel.Abstractions;

public interface IElevationService
{
	bool IsAdministrator { get; }
	bool TryRelaunchAsAdministrator(IReadOnlyList<string> arguments);
}
