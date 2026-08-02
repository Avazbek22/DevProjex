namespace DevProjex.Kernel.Models;

public static class CommandLineExitCodes
{
	public const int Success = 0;
	public const int RuntimeError = 1;
	public const int UsageError = 2;
	public const int PolicyFailure = 3;
	public const int DestinationConflict = 4;
	public const int DesktopUnavailable = 5;
	public const int Canceled = 130;
}
