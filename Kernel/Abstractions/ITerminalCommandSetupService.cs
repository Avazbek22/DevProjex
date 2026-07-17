namespace DevProjex.Kernel.Abstractions;

public interface ITerminalCommandSetupService
{
	TerminalCommandSetupSnapshot Probe();

	TerminalCommandInstallResult InstallOrRepair();

	TerminalCommandInstallResult Reinstall();
}
