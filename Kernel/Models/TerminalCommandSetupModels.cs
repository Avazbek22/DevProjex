namespace DevProjex.Kernel.Models;

public enum TerminalCommandSetupState
{
	ManagedByOperatingSystem,
	UnsupportedOnCurrentPackage,
	UnsupportedOnCurrentPlatform,
	HomeDirectoryUnavailable,
	NotInstalled,
	Installed,
	InstalledPathMissing,
	CommandShadowed,
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
	Reinstalled,
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
	string? ShellProfileHint,
	string? PathSetupCommand = null,
	string? ResolvedCommandPath = null)
{
	public bool IsReady =>
		State == TerminalCommandSetupState.ManagedByOperatingSystem ||
		(State == TerminalCommandSetupState.Installed && UserBinDirectoryIsInPath);

	public bool IsActionable => CanInstall || CanRepair;

	public bool CanReinstall =>
		State == TerminalCommandSetupState.Installed &&
		UserBinDirectoryIsInPath &&
		!string.IsNullOrWhiteSpace(CommandPath) &&
		!string.IsNullOrWhiteSpace(TargetExecutablePath);
}

public sealed record TerminalCommandInstallResult(
	bool Success,
	TerminalCommandInstallOutcome Outcome,
	TerminalCommandSetupSnapshot Snapshot,
	string? ErrorMessage = null);

public sealed record TerminalCommandPathSetupResult(
	bool Success,
	TerminalCommandSetupSnapshot Snapshot,
	string? ErrorMessage = null);

public sealed record TerminalCommandValidationResult(
	bool Success,
	string? ErrorMessage = null);
