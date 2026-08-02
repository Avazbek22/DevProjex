namespace DevProjex.Kernel.Abstractions;

public interface ITerminalCommandSetupService
{
	TerminalCommandSetupSnapshot Probe();

	TerminalCommandInstallResult InstallOrRepair();

	TerminalCommandPathSetupResult ConfigurePath();

	TerminalCommandInstallResult Reinstall();
}
