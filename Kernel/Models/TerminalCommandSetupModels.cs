namespace DevProjex.Kernel.Models;

public enum TerminalCommandSetupState
{
	ManagedByOperatingSystem,
	UnsupportedOnCurrentPackage,
	UnsupportedOnCurrentPlatform,
	HomeDirectoryUnavailable,
	NotInstalled,
	Installed,
	Stale,
	ConflictingCommand,
	PermissionDenied,
	Failed
}

public enum TerminalCommandInstallOutcome
{
	AlreadyInstalled,
	Created,
	Repaired,
	NotSupported,
	ConflictingCommand,
	Failed
}

public sealed record TerminalCommandSetupSnapshot(
	string CommandName,
	TerminalCommandSetupState State,
	string? CommandPath,
	string? TargetExecutablePath,
	string? InstalledTargetExecutablePath,
	string? UserBinDirectory,
	bool UserBinDirectoryIsInPath,
	bool CanInstall,
	bool CanRepair,
	string? ShellProfileHint)
{
	public bool IsReady => State is TerminalCommandSetupState.ManagedByOperatingSystem or TerminalCommandSetupState.Installed;

	public bool IsActionable => CanInstall || CanRepair;
}

public sealed record TerminalCommandInstallResult(
	bool Success,
	TerminalCommandInstallOutcome Outcome,
	TerminalCommandSetupSnapshot Snapshot,
	string? ErrorMessage = null);
