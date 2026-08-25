using System.Runtime.InteropServices;

namespace DevProjex.Infrastructure.TerminalCommands;

public enum TerminalCommandHostPlatform
{
	Windows,
	Linux,
	MacOS,
	Other
}

public sealed record TerminalCommandSetupServiceOptions
{
	public TerminalCommandHostPlatform Platform { get; init; } = TerminalCommandSetupService.DetectPlatform();
	public Func<bool> IsWindowsPackagedApp { get; init; } = WindowsPackageIdentityProbe.IsPackagedApp;
	public Func<string?> HomeDirectoryProvider { get; init; } =
		() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
	public Func<string?> LocalAppDataPathProvider { get; init; } =
		() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
	public Func<string?> PathVariableProvider { get; init; } =
		() => Environment.GetEnvironmentVariable("PATH");
	public Action<string> ProcessPathVariableWriter { get; init; } =
		value => Environment.SetEnvironmentVariable("PATH", value);
	public Func<string?> ShellPathProvider { get; init; } =
		() => Environment.GetEnvironmentVariable("SHELL");
	public Func<string?> XdgConfigHomeProvider { get; init; } =
		() => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
	public Func<string?> ZdotDirectoryProvider { get; init; } =
		() => Environment.GetEnvironmentVariable("ZDOTDIR");
	public Func<string?> PathExtensionsProvider { get; init; } =
		() => Environment.GetEnvironmentVariable("PATHEXT");
	public Func<string?> UserPathVariableProvider { get; init; } =
		() => Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
	public Func<string?> MachinePathVariableProvider { get; init; } =
		() => Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);
	public Action<string> UserPathVariableWriter { get; init; } =
		value => Environment.SetEnvironmentVariable("PATH", value, EnvironmentVariableTarget.User);
	public Func<string?> ExecutablePathProvider { get; init; } = TerminalCommandSetupService.GetCurrentExecutablePath;
	public Func<string, TimeSpan, TerminalCommandValidationResult> LauncherValidator { get; init; } =
		TerminalCommandSetupService.ValidateLauncher;
	public char? PathListSeparator { get; init; }
}

public sealed class TerminalCommandSetupService(TerminalCommandSetupServiceOptions? options = null)
	: ITerminalCommandSetupService
{
	private const string UnixWrapperMarker = "# DevProjex terminal command wrapper";
	private const string UnixPathMarker = "# DevProjex terminal PATH";
	private const string PosixPathSetupLine =
		"case \":$PATH:\" in *\":$HOME/.local/bin:\"*) ;; *) export PATH=\"$HOME/.local/bin:$PATH\" ;; esac";
	private const string FishPathSetupLine = "fish_add_path --move \"$HOME/.local/bin\"";
	private const string WindowsLauncherMarker = "rem DevProjex terminal command wrapper";
	private const string UnixTargetPrefix = "# target: ";
	private const string WindowsTargetPrefix = "rem target: ";
	private const string WindowsEncodedTargetPrefix = "rem target-base64: ";
	private const string WindowsPathHint =
		"DevProjex will add its terminal launcher folder to your user PATH. Restart already-open terminal windows after enabling it.";
	private static readonly TimeSpan LauncherValidationTimeout = TimeSpan.FromSeconds(5);
	// Cover lock-free probes and writes within one process; the named mutex still protects separate processes.
	private static readonly object ProcessSetupSync = new();

	private readonly TerminalCommandSetupServiceOptions _options = options ?? new TerminalCommandSetupServiceOptions();

	public TerminalCommandSetupSnapshot Probe()
	{
		try
		{
			return _options.Platform switch
			{
				TerminalCommandHostPlatform.Windows => ProbeWindows(),
				TerminalCommandHostPlatform.Linux or TerminalCommandHostPlatform.MacOS => ProbeUnixLike(),
				_ => Unsupported(TerminalCommandSetupState.UnsupportedOnCurrentPlatform)
			};
		}
		catch
		{
			return Unsupported(TerminalCommandSetupState.Failed);
		}
	}

	public TerminalCommandInstallResult InstallOrRepair()
	{
		lock (ProcessSetupSync)
			return InstallOrRepairCore(forceReinstall: false);
	}

	public TerminalCommandPathSetupResult ConfigurePath()
	{
		lock (ProcessSetupSync)
			return ConfigurePathCore();
	}

	private TerminalCommandPathSetupResult ConfigurePathCore()
	{
		var initial = Probe();
		if (initial.State == TerminalCommandSetupState.Installed)
			return new TerminalCommandPathSetupResult(true, initial);

		if (initial.State is not (TerminalCommandSetupState.InstalledPathMissing or
		        TerminalCommandSetupState.CommandShadowed) ||
		    string.IsNullOrWhiteSpace(initial.UserBinDirectory))
		{
			return PathSetupFailed(initial, "The terminal launcher must be installed before PATH can be configured.");
		}

		var home = _options.Platform == TerminalCommandHostPlatform.Windows
			? null
			: SafeGetHomeDirectory();
		if (_options.Platform is TerminalCommandHostPlatform.Linux or TerminalCommandHostPlatform.MacOS &&
		    string.IsNullOrWhiteSpace(home))
			return PathSetupFailed(initial, "The user home directory is unavailable.");

		try
		{
			using var setupLock = AcquireSetupLock(initial.CommandPath!);
			if (_options.Platform == TerminalCommandHostPlatform.Windows)
			{
				EnsureWindowsUserBinDirectoryIsInPath(initial.UserBinDirectory, forceFirst: true);
			}
			else if (_options.Platform is TerminalCommandHostPlatform.Linux or TerminalCommandHostPlatform.MacOS)
			{
				foreach (var profile in ResolveUnixShellProfiles(home!))
					EnsureShellProfileContainsPathSetup(profile);
				EnsureCurrentProcessPathContains(initial.UserBinDirectory, forceFirst: true);
			}
			else
			{
				return PathSetupFailed(initial, "PATH setup is not supported on the current platform.");
			}

			var final = Probe();
			return final.State == TerminalCommandSetupState.Installed
				? new TerminalCommandPathSetupResult(true, final)
				: PathSetupFailed(final, "The PATH was updated, but devprojex is still not the resolved terminal command.");
		}
		catch (UnauthorizedAccessException ex)
		{
			return PathSetupFailed(initial, ex.Message);
		}
		catch (IOException ex)
		{
			return PathSetupFailed(initial, ex.Message);
		}
		catch (Exception ex)
		{
			return PathSetupFailed(initial, ex.Message);
		}
	}

	public TerminalCommandInstallResult Reinstall()
	{
		lock (ProcessSetupSync)
			return InstallOrRepairCore(forceReinstall: true);
	}

	private TerminalCommandInstallResult InstallOrRepairCore(bool forceReinstall)
	{
		var initial = Probe();
		if (initial.State is TerminalCommandSetupState.Failed or TerminalCommandSetupState.PermissionDenied &&
		    !string.IsNullOrWhiteSpace(initial.CommandPath))
		{
			try
			{
				// A concurrent atomic replacement can make the lock-free probe observe a transient missing or unreadable launcher.
				using var setupLock = AcquireSetupLock(initial.CommandPath);
				initial = Probe();
			}
			catch (UnauthorizedAccessException ex)
			{
				return FailedAfterInstallAttempt(initial, TerminalCommandSetupState.PermissionDenied, ex.Message);
			}
			catch (Exception ex)
			{
				return FailedAfterInstallAttempt(initial, TerminalCommandSetupState.Failed, ex.Message);
			}
		}

		if (initial.State == TerminalCommandSetupState.ManagedByOperatingSystem)
		{
			return new TerminalCommandInstallResult(
				Success: !forceReinstall,
				Outcome: forceReinstall
					? TerminalCommandInstallOutcome.NotSupported
					: TerminalCommandInstallOutcome.AlreadyInstalled,
				Snapshot: initial,
				ErrorMessage: forceReinstall
					? "The terminal command is managed by the operating system and cannot be reinstalled by DevProjex."
					: null);
		}

		if (!forceReinstall &&
		    (initial.State is TerminalCommandSetupState.Installed or TerminalCommandSetupState.InstalledPathMissing))
		{
			return new TerminalCommandInstallResult(
				Success: true,
				Outcome: TerminalCommandInstallOutcome.AlreadyInstalled,
				Snapshot: initial);
		}

		if (initial.State == TerminalCommandSetupState.ConflictingCommand)
		{
			return new TerminalCommandInstallResult(
				Success: false,
				Outcome: TerminalCommandInstallOutcome.ConflictingCommand,
				Snapshot: initial,
				ErrorMessage: "A non-DevProjex command already exists at the target command path.");
		}

		if ((!initial.IsActionable && !(forceReinstall && initial.CanReinstall)) ||
		    string.IsNullOrWhiteSpace(initial.CommandPath) ||
		    string.IsNullOrWhiteSpace(initial.TargetExecutablePath))
		{
			return new TerminalCommandInstallResult(
				Success: false,
				Outcome: TerminalCommandInstallOutcome.NotSupported,
				Snapshot: initial,
				ErrorMessage: "Terminal command setup is not supported for the current package or platform.");
		}

		var commandPath = initial.CommandPath!;
		var targetPath = initial.TargetExecutablePath!;
		var tempPath = Path.Combine(Path.GetDirectoryName(commandPath)!, $".{initial.CommandName}.{Guid.NewGuid():N}.tmp");

		try
		{
			using var setupLock = AcquireSetupLock(commandPath);
			Directory.CreateDirectory(Path.GetDirectoryName(commandPath)!);

			var commandExistedBeforeReplacement = File.Exists(commandPath);
			var ownership = commandExistedBeforeReplacement
				? ReadManagedWrapper(commandPath)
				: null;
			if (ownership is not null && ownership.Status != ManagedWrapperReadStatus.Managed)
			{
				return OwnershipFailure(
					initial,
					ownership.Status,
					"The command path is occupied by a file not managed by DevProjex.");
			}

			File.WriteAllText(tempPath, BuildCommandFileContent(targetPath), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			if (_options.Platform is not TerminalCommandHostPlatform.Windows)
				TrySetUnixExecutableMode(tempPath);

			// Recheck under the cross-process lock and refuse every ownership change visible before replacement.
			if (File.Exists(commandPath))
			{
				ownership = ReadManagedWrapper(commandPath);
				if (ownership.Status != ManagedWrapperReadStatus.Managed)
				{
					return OwnershipFailure(
						initial,
						ownership.Status,
						"The command path changed and is no longer managed by DevProjex.");
				}
			}

			// A new foreign file must win the final race instead of being overwritten.
			File.Move(tempPath, commandPath, overwrite: commandExistedBeforeReplacement);
			if (_options.Platform is TerminalCommandHostPlatform.Windows)
				EnsureWindowsUserBinDirectoryIsInPath(initial.UserBinDirectory);
			else
				TrySetUnixExecutableMode(commandPath);

			var refreshed = Probe();
			var outcome = forceReinstall
				? TerminalCommandInstallOutcome.Reinstalled
				: initial.State == TerminalCommandSetupState.Stale
					? TerminalCommandInstallOutcome.Repaired
					: TerminalCommandInstallOutcome.Created;

			if (forceReinstall && refreshed.State == TerminalCommandSetupState.Installed)
			{
				var validation = _options.LauncherValidator(commandPath, LauncherValidationTimeout);
				if (!validation.Success)
				{
					return new TerminalCommandInstallResult(
						Success: false,
						Outcome: TerminalCommandInstallOutcome.Failed,
						Snapshot: refreshed,
						ErrorMessage: validation.ErrorMessage ?? "The terminal launcher did not pass its functional check.");
				}
			}

			var launcherWasInstalled = refreshed.State is
				TerminalCommandSetupState.Installed or
				TerminalCommandSetupState.InstalledPathMissing or
				TerminalCommandSetupState.CommandShadowed;
			return new TerminalCommandInstallResult(
				Success: launcherWasInstalled,
				Outcome: outcome,
				Snapshot: refreshed,
				ErrorMessage: launcherWasInstalled
					? null
					: "The command file was written but the command is still not reported as installed.");
		}
		catch (UnauthorizedAccessException ex)
		{
			return FailedAfterInstallAttempt(initial, TerminalCommandSetupState.PermissionDenied, ex.Message);
		}
		catch (IOException ex)
		{
			return FailedAfterInstallAttempt(initial, TerminalCommandSetupState.Failed, ex.Message);
		}
		catch (Exception ex)
		{
			return FailedAfterInstallAttempt(initial, TerminalCommandSetupState.Failed, ex.Message);
		}
		finally
		{
			TryDeleteTempFile(tempPath);
		}
	}

	internal static TerminalCommandHostPlatform DetectPlatform()
	{
		if (OperatingSystem.IsWindows())
			return TerminalCommandHostPlatform.Windows;
		if (OperatingSystem.IsLinux())
			return TerminalCommandHostPlatform.Linux;
		if (OperatingSystem.IsMacOS())
			return TerminalCommandHostPlatform.MacOS;
		return TerminalCommandHostPlatform.Other;
	}

	internal static string? GetCurrentExecutablePath()
	{
		if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
			return Environment.ProcessPath;

		return Process.GetCurrentProcess().MainModule?.FileName;
	}

	internal static string BuildWrapperContent(string targetPath)
	{
		// The target comment is intentionally plain text so stale wrappers can be
		// repaired without parsing shell syntax or executing anything.
		var script = string.Join(
			"\n",
			"#!/bin/sh",
			UnixWrapperMarker,
			UnixTargetPrefix + targetPath,
			"export DEVPROJEX_TERMINAL_HOST=1",
			"exec " + ShellQuote(targetPath) + " \"$@\"",
			string.Empty);

		// Unix kernels parse the shebang before a shell sees the file; CRLF in the
		// first line turns /bin/sh into /bin/sh\r and makes exec fail with ENOENT.
		return script.Replace("\r\n", "\n", StringComparison.Ordinal);
	}

	internal static string BuildWindowsLauncherContent(string targetPath)
	{
		var escapedTargetPath = EscapeWindowsBatchValue(targetPath);
		var escapedManagedAssemblyPath = EscapeWindowsBatchValue(BuildWindowsManagedAssemblyPath(targetPath));
		var encodedTargetPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(targetPath));
		return string.Join(
			"\r\n",
			"@echo off",
			"setlocal DisableDelayedExpansion",
			WindowsLauncherMarker,
			WindowsEncodedTargetPrefix + encodedTargetPath,
			"for /f \"tokens=2 delims=:\" %%C in ('chcp') do set \"DEVPROJEX_ORIGINAL_CODE_PAGE=%%C\"",
			"chcp 65001 >nul",
			"set \"DEVPROJEX_EXE=" + escapedTargetPath + "\"",
			"set \"DEVPROJEX_DLL=" + escapedManagedAssemblyPath + "\"",
			"set \"DEVPROJEX_TERMINAL_HOST=1\"",
			"chcp %DEVPROJEX_ORIGINAL_CODE_PAGE% >nul",
			"if not exist \"%DEVPROJEX_DLL%\" goto :run-native",
			"dotnet \"%DEVPROJEX_DLL%\" %*",
			"set \"DEVPROJEX_EXIT_CODE=%ERRORLEVEL%\"",
			"goto :restore-code-page",
			":run-native",
			"\"%DEVPROJEX_EXE%\" %*",
			"set \"DEVPROJEX_EXIT_CODE=%ERRORLEVEL%\"",
			":restore-code-page",
			"chcp %DEVPROJEX_ORIGINAL_CODE_PAGE% >nul",
			"exit /b %DEVPROJEX_EXIT_CODE%",
			string.Empty);
	}

	internal static string BuildWindowsManagedAssemblyPath(string executablePath)
	{
		var separatorIndex = Math.Max(
			executablePath.LastIndexOf('\\'),
			executablePath.LastIndexOf('/'));
		var directory = separatorIndex >= 0 ? executablePath[..separatorIndex] : string.Empty;
		var separator = separatorIndex >= 0 ? executablePath[separatorIndex] : Path.DirectorySeparatorChar;
		var fileName = separatorIndex >= 0 ? executablePath[(separatorIndex + 1)..] : executablePath;
		var extensionIndex = fileName.LastIndexOf('.');
		var fileStem = extensionIndex > 0 ? fileName[..extensionIndex] : fileName;
		var managedAssemblyName = fileStem + ".dll";

		return string.IsNullOrEmpty(directory)
			? managedAssemblyName
			: directory + separator + managedAssemblyName;
	}

	internal static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

	internal static string EscapeWindowsBatchValue(string value) =>
		value.Replace("%", "%%", StringComparison.Ordinal);

	private TerminalCommandSetupSnapshot ProbeWindows()
	{
		if (_options.IsWindowsPackagedApp())
		{
			return new TerminalCommandSetupSnapshot(
				CommandName: CommandLineExecutableAliases.WindowsStoreAlias,
				State: TerminalCommandSetupState.ManagedByOperatingSystem,
				CommandPath: null,
				TargetExecutablePath: null,
				InstalledTargetExecutablePath: null,
				UserBinDirectory: null,
				UserBinDirectoryIsInPath: true,
				CanInstall: false,
				CanRepair: false,
				ShellProfileHint: null);
		}

		var localAppData = SafeGetLocalAppDataDirectory();
		var targetPath = SafeGetExecutablePath();
		if (string.IsNullOrWhiteSpace(localAppData))
		{
			return WindowsPortableSnapshot(
				TerminalCommandSetupState.HomeDirectoryUnavailable,
				commandPath: null,
				targetPath,
				installedTargetPath: null,
				userBinDirectory: null,
				isUserBinInPath: false,
				canInstall: false,
				canRepair: false);
		}

		var userBinDirectory = Path.Combine(localAppData, "DevProjex", "bin");
		var commandPath = Path.Combine(userBinDirectory, CommandLineExecutableAliases.WindowsPortableCommandFileName);
		var pathResolution = ResolveWindowsCommand(userBinDirectory, resolveCommand: false);
		var isUserBinInPath = pathResolution.IsManagedDirectoryInPath;

		if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
		{
			return WindowsPortableSnapshot(
				TerminalCommandSetupState.Failed,
				commandPath,
				targetPath,
				installedTargetPath: null,
				userBinDirectory,
				isUserBinInPath,
				canInstall: false,
				canRepair: false);
		}

		if (Directory.Exists(commandPath))
		{
			return WindowsPortableSnapshot(
				TerminalCommandSetupState.ConflictingCommand,
				commandPath,
				targetPath,
				installedTargetPath: null,
				userBinDirectory,
				isUserBinInPath,
				canInstall: false,
				canRepair: false);
		}

		if (!File.Exists(commandPath))
		{
			return WindowsPortableSnapshot(
				TerminalCommandSetupState.NotInstalled,
				commandPath,
				targetPath,
				installedTargetPath: null,
				userBinDirectory,
				isUserBinInPath,
				canInstall: true,
				canRepair: false);
		}

		var wrapper = ReadManagedWrapper(commandPath);
		if (wrapper.Status != ManagedWrapperReadStatus.Managed)
		{
			return WindowsPortableSnapshot(
				MapWrapperReadFailureState(wrapper.Status),
				commandPath,
				targetPath,
				installedTargetPath: null,
				userBinDirectory,
				isUserBinInPath,
				canInstall: false,
				canRepair: false);
		}
		var installedTargetPath = wrapper.TargetPath!;
		pathResolution = ResolveWindowsCommand(userBinDirectory, resolveCommand: true);
		isUserBinInPath = pathResolution.IsManagedDirectoryInPath;

		var isCurrentLauncher = IsCurrentWindowsLauncher(commandPath, targetPath);
		if (AreSamePath(installedTargetPath, targetPath) && File.Exists(installedTargetPath) && isCurrentLauncher)
		{
			var installedState = !isUserBinInPath
				? TerminalCommandSetupState.InstalledPathMissing
				: pathResolution.ResolvedCommandPath is null ||
				  !AreSameCommandPath(pathResolution.ResolvedCommandPath, commandPath)
					? TerminalCommandSetupState.CommandShadowed
					: TerminalCommandSetupState.Installed;
			return WindowsPortableSnapshot(
				installedState,
				commandPath,
				targetPath,
				installedTargetPath,
				userBinDirectory,
				isUserBinInPath,
				canInstall: false,
				canRepair: false,
				pathResolution.ResolvedCommandPath);
		}

		return WindowsPortableSnapshot(
			TerminalCommandSetupState.Stale,
			commandPath,
			targetPath,
			installedTargetPath,
			userBinDirectory,
			isUserBinInPath,
			canInstall: false,
			canRepair: true);
	}

	private static bool IsCurrentWindowsLauncher(string commandPath, string targetPath)
	{
		try
		{
			return string.Equals(
				ReadAllTextDuringAtomicReplacement(commandPath),
				BuildWindowsLauncherContent(targetPath),
				StringComparison.Ordinal);
		}
		catch (IOException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}

	private TerminalCommandSetupSnapshot ProbeUnixLike()
	{
		var home = SafeGetHomeDirectory();
		if (string.IsNullOrWhiteSpace(home))
		{
			return new TerminalCommandSetupSnapshot(
				CommandName: CommandLineExecutableAliases.UnixCommand,
				State: TerminalCommandSetupState.HomeDirectoryUnavailable,
				CommandPath: null,
				TargetExecutablePath: SafeGetExecutablePath(),
				InstalledTargetExecutablePath: null,
				UserBinDirectory: null,
				UserBinDirectoryIsInPath: false,
				CanInstall: false,
				CanRepair: false,
				ShellProfileHint: null);
		}

		var userBinDirectory = Path.Combine(home, ".local", "bin");
		var commandPath = Path.Combine(userBinDirectory, CommandLineExecutableAliases.UnixCommand);
		var targetPath = SafeGetExecutablePath();
		var pathResolution = ResolveUnixCommand(userBinDirectory, resolveCommand: false);
		var isUserBinInPath = pathResolution.IsManagedDirectoryInPath;
		var pathNeedsSetup = !isUserBinInPath ||
		                     pathResolution.ResolvedCommandPath is not null &&
		                     !AreSameCommandPath(pathResolution.ResolvedCommandPath, commandPath);
		var shellProfileHint = pathNeedsSetup ? BuildUnixShellProfileHint() : null;
		var pathSetupCommand = pathNeedsSetup ? BuildUnixPathSetupCommand() : null;

		if (string.IsNullOrWhiteSpace(targetPath))
		{
			return UnixSnapshot(
				TerminalCommandSetupState.Failed,
				commandPath,
				targetPath,
				installedTargetPath: null,
				userBinDirectory,
				isUserBinInPath,
				canInstall: false,
				canRepair: false,
				shellProfileHint);
		}

		if (!File.Exists(targetPath))
		{
			return UnixSnapshot(
				TerminalCommandSetupState.Failed,
				commandPath,
				targetPath,
				installedTargetPath: null,
				userBinDirectory,
				isUserBinInPath,
				canInstall: false,
				canRepair: false,
				shellProfileHint);
		}

		if (Directory.Exists(commandPath))
		{
			return UnixSnapshot(
				TerminalCommandSetupState.ConflictingCommand,
				commandPath,
				targetPath,
				installedTargetPath: null,
				userBinDirectory,
				isUserBinInPath,
				canInstall: false,
				canRepair: false,
				shellProfileHint);
		}

		if (!File.Exists(commandPath))
		{
			return UnixSnapshot(
				TerminalCommandSetupState.NotInstalled,
				commandPath,
				targetPath,
				installedTargetPath: null,
				userBinDirectory,
				isUserBinInPath,
				canInstall: true,
				canRepair: false,
				shellProfileHint,
				pathSetupCommand);
		}

		var wrapper = ReadManagedWrapper(commandPath);
		if (wrapper.Status != ManagedWrapperReadStatus.Managed)
		{
			return UnixSnapshot(
				MapWrapperReadFailureState(wrapper.Status),
				commandPath,
				targetPath,
				installedTargetPath: null,
				userBinDirectory,
				isUserBinInPath,
				canInstall: false,
				canRepair: false,
				shellProfileHint);
		}
		var installedTargetPath = wrapper.TargetPath!;
		pathResolution = ResolveUnixCommand(userBinDirectory, resolveCommand: true);
		isUserBinInPath = pathResolution.IsManagedDirectoryInPath;
		pathNeedsSetup = !isUserBinInPath ||
		                 pathResolution.ResolvedCommandPath is null ||
		                 !AreSameCommandPath(pathResolution.ResolvedCommandPath, commandPath);
		shellProfileHint = pathNeedsSetup ? BuildUnixShellProfileHint() : null;
		pathSetupCommand = pathNeedsSetup ? BuildUnixPathSetupCommand() : null;

		var isCurrentWrapper = IsCurrentUnixWrapper(commandPath, targetPath);
		var hasExecutableMode = HasUnixExecutableMode(commandPath);
		if (AreSamePath(installedTargetPath, targetPath) &&
		    File.Exists(installedTargetPath) &&
		    isCurrentWrapper &&
		    hasExecutableMode)
		{
			var installedState = isUserBinInPath
				? pathResolution.ResolvedCommandPath is null ||
				  !AreSameCommandPath(pathResolution.ResolvedCommandPath, commandPath)
					? TerminalCommandSetupState.CommandShadowed
					: TerminalCommandSetupState.Installed
				: TerminalCommandSetupState.InstalledPathMissing;
			return UnixSnapshot(
				installedState,
				commandPath,
				targetPath,
				installedTargetPath,
				userBinDirectory,
				isUserBinInPath,
				canInstall: false,
				canRepair: false,
				shellProfileHint,
				pathSetupCommand,
				pathResolution.ResolvedCommandPath);
		}

		return UnixSnapshot(
			TerminalCommandSetupState.Stale,
			commandPath,
			targetPath,
			installedTargetPath,
			userBinDirectory,
			isUserBinInPath,
			canInstall: false,
			canRepair: true,
			shellProfileHint);
	}

	private TerminalCommandSetupSnapshot Unsupported(TerminalCommandSetupState state) =>
		new(
			CommandName: _options.Platform == TerminalCommandHostPlatform.Windows
				? CommandLineExecutableAliases.WindowsStoreAlias
				: CommandLineExecutableAliases.UnixCommand,
			State: state,
			CommandPath: null,
			TargetExecutablePath: SafeGetExecutablePath(),
			InstalledTargetExecutablePath: null,
			UserBinDirectory: null,
			UserBinDirectoryIsInPath: false,
			CanInstall: false,
			CanRepair: false,
			ShellProfileHint: null);

	private static TerminalCommandSetupSnapshot UnixSnapshot(
		TerminalCommandSetupState state,
		string commandPath,
		string? targetPath,
		string? installedTargetPath,
		string userBinDirectory,
		bool isUserBinInPath,
		bool canInstall,
		bool canRepair,
		string? shellProfileHint,
		string? pathSetupCommand = null,
		string? resolvedCommandPath = null) =>
		new(
			CommandName: CommandLineExecutableAliases.UnixCommand,
			State: state,
			CommandPath: commandPath,
			TargetExecutablePath: targetPath,
			InstalledTargetExecutablePath: installedTargetPath,
			UserBinDirectory: userBinDirectory,
			UserBinDirectoryIsInPath: isUserBinInPath,
			CanInstall: canInstall,
			CanRepair: canRepair,
			ShellProfileHint: shellProfileHint,
			PathSetupCommand: pathSetupCommand,
			ResolvedCommandPath: resolvedCommandPath);

	private static TerminalCommandSetupSnapshot WindowsPortableSnapshot(
		TerminalCommandSetupState state,
		string? commandPath,
		string? targetPath,
		string? installedTargetPath,
		string? userBinDirectory,
		bool isUserBinInPath,
		bool canInstall,
		bool canRepair,
		string? resolvedCommandPath = null) =>
		new(
			CommandName: CommandLineExecutableAliases.UnixCommand,
			State: state,
			CommandPath: commandPath,
			TargetExecutablePath: targetPath,
			InstalledTargetExecutablePath: installedTargetPath,
			UserBinDirectory: userBinDirectory,
			UserBinDirectoryIsInPath: isUserBinInPath,
			CanInstall: canInstall,
			CanRepair: canRepair,
			ShellProfileHint: isUserBinInPath ? null : WindowsPathHint,
			ResolvedCommandPath: resolvedCommandPath);

	private static TerminalCommandInstallResult FailedAfterInstallAttempt(
		TerminalCommandSetupSnapshot initial,
		TerminalCommandSetupState state,
		string message)
	{
		var failed = initial with
		{
			State = state,
			CanInstall = false,
			CanRepair = false
		};

		return new TerminalCommandInstallResult(
			Success: false,
			Outcome: TerminalCommandInstallOutcome.Failed,
			Snapshot: failed,
			ErrorMessage: message);
	}

	private static TerminalCommandPathSetupResult PathSetupFailed(
		TerminalCommandSetupSnapshot snapshot,
		string message) =>
		new(false, snapshot, message);

	private static TerminalCommandInstallResult OwnershipFailure(
		TerminalCommandSetupSnapshot initial,
		ManagedWrapperReadStatus status,
		string conflictMessage)
	{
		var state = MapWrapperReadFailureState(status);
		var snapshot = initial with { State = state, CanInstall = false, CanRepair = false };
		return new TerminalCommandInstallResult(
			Success: false,
			Outcome: state == TerminalCommandSetupState.ConflictingCommand
				? TerminalCommandInstallOutcome.ConflictingCommand
				: TerminalCommandInstallOutcome.Failed,
			Snapshot: snapshot,
			ErrorMessage: state switch
			{
				TerminalCommandSetupState.PermissionDenied => "DevProjex cannot read the existing terminal command.",
				TerminalCommandSetupState.Failed => "The existing terminal command is temporarily unavailable.",
				_ => conflictMessage
			});
	}

	private ManagedWrapperReadResult ReadManagedWrapper(string commandPath)
	{
		try
		{
			// Lock-free probes may read the previous complete launcher while a serialized writer atomically replaces it.
			using var stream = new FileStream(
				commandPath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read | FileShare.Delete);
			using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
			var firstLine = reader.ReadLine();
			var marker = firstLine;
			var target = reader.ReadLine();
			var targetPrefix = UnixTargetPrefix;
			if (string.Equals(firstLine, "#!/bin/sh", StringComparison.Ordinal))
			{
				marker = target;
				target = reader.ReadLine();
			}
			else if (string.Equals(firstLine, "@echo off", StringComparison.OrdinalIgnoreCase))
			{
				marker = target;
				target = reader.ReadLine();
				if (string.Equals(marker, "setlocal DisableDelayedExpansion", StringComparison.OrdinalIgnoreCase))
				{
					marker = target;
					target = reader.ReadLine();
				}
				targetPrefix = WindowsTargetPrefix;
			}

			if (string.Equals(marker, WindowsLauncherMarker, StringComparison.OrdinalIgnoreCase))
				targetPrefix = WindowsTargetPrefix;

			if (!IsManagedWrapperMarker(marker) || target is null)
				return new ManagedWrapperReadResult(ManagedWrapperReadStatus.Foreign, null);

			if (string.Equals(marker, WindowsLauncherMarker, StringComparison.OrdinalIgnoreCase) &&
			    target.StartsWith(WindowsEncodedTargetPrefix, StringComparison.OrdinalIgnoreCase))
			{
				try
				{
					var encoded = target[WindowsEncodedTargetPrefix.Length..];
					var decoded = new UTF8Encoding(
						encoderShouldEmitUTF8Identifier: false,
						throwOnInvalidBytes: true).GetString(Convert.FromBase64String(encoded));
					return string.IsNullOrWhiteSpace(decoded)
						? new ManagedWrapperReadResult(ManagedWrapperReadStatus.Foreign, null)
						: new ManagedWrapperReadResult(ManagedWrapperReadStatus.Managed, decoded);
				}
				catch (FormatException)
				{
					return new ManagedWrapperReadResult(ManagedWrapperReadStatus.Foreign, null);
				}
				catch (DecoderFallbackException)
				{
					return new ManagedWrapperReadResult(ManagedWrapperReadStatus.Foreign, null);
				}
			}

			if (!target.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
				return new ManagedWrapperReadResult(ManagedWrapperReadStatus.Foreign, null);
			return new ManagedWrapperReadResult(
				ManagedWrapperReadStatus.Managed,
				target[targetPrefix.Length..]);
		}
		catch (UnauthorizedAccessException)
		{
			return new ManagedWrapperReadResult(ManagedWrapperReadStatus.PermissionDenied, null);
		}
		catch (IOException)
		{
			return new ManagedWrapperReadResult(ManagedWrapperReadStatus.Unreadable, null);
		}
	}

	private static TerminalCommandSetupState MapWrapperReadFailureState(ManagedWrapperReadStatus status) => status switch
	{
		ManagedWrapperReadStatus.PermissionDenied => TerminalCommandSetupState.PermissionDenied,
		ManagedWrapperReadStatus.Unreadable => TerminalCommandSetupState.Failed,
		_ => TerminalCommandSetupState.ConflictingCommand
	};

	private static bool IsManagedWrapperMarker(string? value) =>
		string.Equals(value, UnixWrapperMarker, StringComparison.Ordinal) ||
		string.Equals(value, WindowsLauncherMarker, StringComparison.OrdinalIgnoreCase);

	private static bool IsCurrentUnixWrapper(string commandPath, string targetPath)
	{
		try
		{
			return string.Equals(
				ReadAllTextDuringAtomicReplacement(commandPath),
				BuildWrapperContent(targetPath),
				StringComparison.Ordinal);
		}
		catch (IOException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static string ReadAllTextDuringAtomicReplacement(string path)
	{
		// Probes must not block the serialized writer from atomically replacing a complete launcher.
		using var stream = new FileStream(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read | FileShare.Delete);
		using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		return reader.ReadToEnd();
	}

	private static bool HasUnixExecutableMode(string commandPath)
	{
		if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
			return true;

		try
		{
			return File.GetUnixFileMode(commandPath).HasFlag(UnixFileMode.UserExecute);
		}
		catch
		{
			return false;
		}
	}

	private bool IsDirectoryInPathValue(string directory, string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return false;
		var separator = _options.PathListSeparator ?? (_options.Platform == TerminalCommandHostPlatform.Windows ? ';' : ':');
		foreach (var entry in path.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (AreSameDirectory(entry, directory))
				return true;
		}

		return false;
	}

	private CommandPathResolution ResolveUnixCommand(string managedDirectory, bool resolveCommand)
	{
		var path = SafeGetPathVariable();
		return new CommandPathResolution(
			IsDirectoryInPathValue(managedDirectory, path),
			resolveCommand ? FindFirstUnixCommand(path, managedDirectory) : null);
	}

	private string? FindFirstUnixCommand(string? path, string managedDirectory)
	{
		if (string.IsNullOrWhiteSpace(path))
			return null;

		var separator = _options.PathListSeparator ?? ':';
		foreach (var entry in path.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var candidate = Path.Combine(NormalizePath(entry), CommandLineExecutableAliases.UnixCommand);
			if (File.Exists(candidate) && HasUnixExecutableMode(candidate))
				return candidate;
			// Entries after the managed directory cannot shadow it and may include slow network mounts.
			if (AreSameDirectory(entry, managedDirectory))
				break;
		}

		return null;
	}

	private CommandPathResolution ResolveWindowsCommand(string managedDirectory, bool resolveCommand)
	{
		var machinePath = SafeGetMachinePathVariable();
		var userPath = SafeGetUserPathVariable();
		var isManagedDirectoryInPath =
			IsDirectoryInPathValue(managedDirectory, machinePath) ||
			IsDirectoryInPathValue(managedDirectory, userPath);
		var separator = _options.PathListSeparator ?? ';';
		var effectivePath = string.Join(
			separator,
			new[] { machinePath, userPath }.Where(static value => !string.IsNullOrWhiteSpace(value)));

		return new CommandPathResolution(
			isManagedDirectoryInPath,
			resolveCommand && isManagedDirectoryInPath
				? FindFirstWindowsCommand(effectivePath, managedDirectory)
				: null);
	}

	private string? FindFirstWindowsCommand(string? path, string managedDirectory)
	{
		if (string.IsNullOrWhiteSpace(path))
			return null;

		var separator = _options.PathListSeparator ?? ';';
		var extensions = GetWindowsPathExtensions();
		foreach (var entry in path.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var isManagedDirectory = AreSameDirectory(entry, managedDirectory);
			// Use the known physical path after a Windows-semantic match so case-sensitive CI hosts do not probe a synthetic casing.
			var directory = isManagedDirectory ? managedDirectory : NormalizePath(entry);
			var candidate = FindWindowsCommandCandidate(directory, extensions);
			if (candidate is not null)
				return candidate;
			// Command resolution is decided once the managed directory has been inspected.
			if (isManagedDirectory)
				break;
		}

		return null;
	}

	private IReadOnlyList<string> GetWindowsPathExtensions()
	{
		string? value;
		try
		{
			value = _options.PathExtensionsProvider();
		}
		catch
		{
			value = null;
		}

		value = string.IsNullOrWhiteSpace(value) ? ".COM;.EXE;.BAT;.CMD" : value;
		return value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(static extension => extension.StartsWith('.') ? extension : "." + extension)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private void EnsureWindowsUserBinDirectoryIsInPath(string? directory, bool forceFirst = false)
	{
		if (string.IsNullOrWhiteSpace(directory))
			return;
		if (!forceFirst && IsDirectoryInPathValue(directory, SafeGetMachinePathVariable()))
			return;

		var userPath = SafeGetUserPathVariable();
		if (!forceFirst && IsDirectoryInPathValue(directory, userPath) &&
		    !IsWindowsPortableCommandShadowed(directory))
			return;

		var separator = _options.PathListSeparator ?? ';';
		var entries = string.IsNullOrWhiteSpace(userPath)
			? new List<string>()
			: userPath.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

		for (var index = entries.Count - 1; index >= 0; index--)
		{
			if (AreSameDirectory(entries[index], directory))
				entries.RemoveAt(index);
		}

		if (forceFirst)
			entries.Insert(0, directory);
		else
		{
			var shadowingCommandIndex = entries.FindIndex(ContainsWindowsCommandCandidate);
			if (shadowingCommandIndex >= 0)
				entries.Insert(shadowingCommandIndex, directory);
			else
				entries.Add(directory);
		}

		_options.UserPathVariableWriter(string.Join(separator, entries));
		WindowsEnvironmentChangeBroadcaster.NotifyEnvironmentChanged();
	}

	private bool IsWindowsPortableCommandShadowed(string directory)
	{
		var resolution = ResolveWindowsCommand(directory, resolveCommand: true);
		return resolution.ResolvedCommandPath is not null &&
		       !AreSameCommandPath(resolution.ResolvedCommandPath, Path.Combine(
			       directory,
			       CommandLineExecutableAliases.WindowsPortableCommandFileName));
	}

	private bool ContainsWindowsCommandCandidate(string directory)
	{
		directory = NormalizePath(directory);
		return FindWindowsCommandCandidate(directory, GetWindowsPathExtensions()) is not null;
	}

	private static string? FindWindowsCommandCandidate(
		string directory,
		IReadOnlyList<string> extensions)
	{
		foreach (var extension in extensions)
		{
			var candidate = Path.Combine(directory, CommandLineExecutableAliases.UnixCommand + extension);
			if (File.Exists(candidate))
				return candidate;
		}

		if (OperatingSystem.IsWindows())
			return null;

		try
		{
			// Windows command resolution is case-insensitive even when its abstraction is exercised on a case-sensitive CI host.
			foreach (var candidate in Directory.EnumerateFiles(directory))
			{
				var fileName = Path.GetFileName(candidate);
				foreach (var extension in extensions)
				{
					if (string.Equals(
						    fileName,
						    CommandLineExecutableAliases.UnixCommand + extension,
						    StringComparison.OrdinalIgnoreCase))
					{
						return candidate;
					}
				}
			}
		}
		catch (IOException)
		{
			return null;
		}
		catch (UnauthorizedAccessException)
		{
			return null;
		}

		return null;
	}

	private string? SafeGetHomeDirectory()
	{
		try
		{
			return _options.HomeDirectoryProvider();
		}
		catch
		{
			return null;
		}
	}

	private string? SafeGetPathVariable()
	{
		try
		{
			return _options.PathVariableProvider();
		}
		catch
		{
			return null;
		}
	}

	private string? SafeGetUserPathVariable()
	{
		try
		{
			return _options.UserPathVariableProvider();
		}
		catch
		{
			return null;
		}
	}

	private string? SafeGetMachinePathVariable()
	{
		try
		{
			return _options.MachinePathVariableProvider();
		}
		catch
		{
			return null;
		}
	}

	private string? SafeGetLocalAppDataDirectory()
	{
		try
		{
			return _options.LocalAppDataPathProvider();
		}
		catch
		{
			return null;
		}
	}

	private string? SafeGetExecutablePath()
	{
		try
		{
			return _options.ExecutablePathProvider();
		}
		catch
		{
			return null;
		}
	}

	private bool AreSamePath(string left, string right)
	{
		var comparison = _options.Platform == TerminalCommandHostPlatform.Windows
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;

		return string.Equals(NormalizePath(left), NormalizePath(right), comparison);
	}

	private bool AreSameCommandPath(string left, string right)
	{
		if (AreSamePath(left, right))
			return true;

		var leftDirectory = Path.GetDirectoryName(left);
		var rightDirectory = Path.GetDirectoryName(right);
		if (string.IsNullOrWhiteSpace(leftDirectory) || string.IsNullOrWhiteSpace(rightDirectory))
			return false;

		var comparison = _options.Platform == TerminalCommandHostPlatform.Windows
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		return string.Equals(Path.GetFileName(left), Path.GetFileName(right), comparison) &&
		       AreSameDirectory(leftDirectory, rightDirectory);
	}

	private bool AreSameDirectory(string left, string right)
	{
		if (AreSamePath(left, right))
			return true;
		if (_options.Platform == TerminalCommandHostPlatform.MacOS &&
		    Directory.Exists(left) &&
		    Directory.Exists(right) &&
		    string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase))
		{
			return AreSameCaseInsensitiveMacDirectory(left, right);
		}

		var leftTarget = ResolveDirectoryLink(left);
		var rightTarget = ResolveDirectoryLink(right);
		return leftTarget is not null && rightTarget is not null && AreSamePath(leftTarget, rightTarget);
	}

	private bool AreSameCaseInsensitiveMacDirectory(string left, string right)
	{
		if (AreSamePath(left, right))
			return true;

		var leftParent = Path.GetDirectoryName(left);
		var rightParent = Path.GetDirectoryName(right);
		if (string.IsNullOrWhiteSpace(leftParent) || string.IsNullOrWhiteSpace(rightParent) ||
		    !AreSameDirectory(leftParent, rightParent))
		{
			return false;
		}

		try
		{
			var name = Path.GetFileName(left);
			// Two separate entries with case-only names are valid on a case-sensitive APFS volume.
			return Directory.EnumerateDirectories(leftParent)
				.Count(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)) == 1;
		}
		catch
		{
			return false;
		}
	}

	private static string? ResolveDirectoryLink(string path)
	{
		try
		{
			return Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ??
			       (Directory.Exists(path) ? Path.GetFullPath(path) : null);
		}
		catch
		{
			return null;
		}
	}

	private string NormalizePath(string value)
	{
		var trimmed = value.Trim();
		if (_options.Platform == TerminalCommandHostPlatform.Windows)
			trimmed = Environment.ExpandEnvironmentVariables(trimmed.Trim('"'));
		trimmed = TrimTrailingDirectorySeparators(trimmed);
		if (trimmed.Length == 0)
			return trimmed;

		try
		{
			return TrimTrailingDirectorySeparators(Path.GetFullPath(trimmed));
		}
		catch
		{
			return trimmed;
		}
	}

	private string TrimTrailingDirectorySeparators(string value)
	{
		var end = value.Length;
		while (end > 0 && IsTrimmableTrailingDirectorySeparator(value, end))
			end--;

		return end == value.Length ? value : value[..end];
	}

	private bool IsTrimmableTrailingDirectorySeparator(string value, int end)
	{
		var index = end - 1;
		if (!IsDirectorySeparator(value[index]))
			return false;

		if (end == 1)
			return false;

		if (_options.Platform == TerminalCommandHostPlatform.Windows &&
		    end == 3 &&
		    char.IsLetter(value[0]) &&
		    value[1] == ':' &&
		    IsDirectorySeparator(value[2]))
		{
			return false;
		}

		return true;
	}

	private bool IsDirectorySeparator(char value) =>
		_options.Platform == TerminalCommandHostPlatform.Windows
			? value is '\\' or '/'
			: value == '/';

	private static void TrySetUnixExecutableMode(string path)
	{
		if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
			return;

		try
		{
			File.SetUnixFileMode(
				path,
				UnixFileMode.UserRead |
				UnixFileMode.UserWrite |
				UnixFileMode.UserExecute |
				UnixFileMode.GroupRead |
				UnixFileMode.GroupExecute |
				UnixFileMode.OtherRead |
				UnixFileMode.OtherExecute);
		}
		catch
		{
			// Missing chmod support should not prevent wrapper creation; the caller
			// still receives a deterministic probe result after the write.
		}
	}

	private static void TryDeleteTempFile(string tempPath)
	{
		try
		{
			if (File.Exists(tempPath))
				File.Delete(tempPath);
		}
		catch
		{
			// Best effort cleanup only.
		}
	}

	private string BuildCommandFileContent(string targetPath) =>
		_options.Platform == TerminalCommandHostPlatform.Windows
			? BuildWindowsLauncherContent(targetPath)
			: BuildWrapperContent(targetPath);

	internal static TerminalCommandValidationResult ValidateLauncher(string commandPath, TimeSpan timeout)
	{
		Process? process = null;
		try
		{
			var startInfo = CreateLauncherValidationStartInfo(commandPath);
			process = new Process { StartInfo = startInfo };
			if (!process.Start())
				return new TerminalCommandValidationResult(false, "The terminal launcher process could not be started.");
			process.StandardInput.Close();

			var standardOutput = process.StandardOutput.ReadToEndAsync();
			var standardError = process.StandardError.ReadToEndAsync();
			var completion = Task.WhenAll(process.WaitForExitAsync(), standardOutput, standardError);
			if (!completion.Wait(timeout))
			{
				TryKillProcess(process);
				return new TerminalCommandValidationResult(false, "The terminal launcher validation timed out.");
			}

			if (process.ExitCode == 0)
				return new TerminalCommandValidationResult(true);

			var error = standardError.Result.Trim();
			return new TerminalCommandValidationResult(
				false,
				string.IsNullOrWhiteSpace(error)
					? $"The terminal launcher exited with code {process.ExitCode}."
					: error);
		}
		catch (Exception ex)
		{
			TryKillProcess(process);
			return new TerminalCommandValidationResult(false, ex.Message);
		}
		finally
		{
			process?.Dispose();
		}
	}

	private static SetupLockLease AcquireSetupLock(string commandPath)
	{
		// A named mutex serializes independent app instances without leaving lock artifacts in user folders.
		var normalizedPath = Path.GetFullPath(commandPath);
		if (OperatingSystem.IsWindows())
			normalizedPath = normalizedPath.ToUpperInvariant();
		var hash = Convert.ToHexString(
			System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
		var name = OperatingSystem.IsWindows()
			? $"Local\\DevProjex.TerminalCommand.{hash}"
			: $"DevProjex.TerminalCommand.{hash}";
		var mutex = new Mutex(initiallyOwned: false, name);
		try
		{
			var acquired = false;
			try
			{
				acquired = mutex.WaitOne(TimeSpan.FromSeconds(2));
			}
			catch (AbandonedMutexException)
			{
				acquired = true;
			}

			if (!acquired)
				throw new IOException("Another DevProjex process is already updating the terminal command.");

			return new SetupLockLease(mutex);
		}
		catch
		{
			mutex.Dispose();
			throw;
		}
	}

	private string BuildUnixShellProfileHint()
	{
		var home = SafeGetHomeDirectory();
		var profile = GetUnixShellName() switch
		{
			"bash" => "the Bash interactive and login profiles",
			"zsh" => ToHomeRelativeDisplayPath(ResolveZshConfigDirectory(home), home, ".zshrc"),
			"fish" => ToHomeRelativeDisplayPath(ResolveFishConfigDirectory(home), home, "config.fish"),
			_ => "your shell profile"
		};

		return $"Add ~/.local/bin to PATH in {profile}, then open a new terminal.";
	}

	private string BuildUnixPathSetupCommand() => GetUnixShellName() switch
	{
		"fish" => FishPathSetupLine,
		"bash" => BuildBashPathSetupCommand(),
		"zsh" => BuildPosixProfileCommand(GetZshProfileCommandPath()),
		_ => BuildPosixProfileCommand("$HOME/.profile")
	};

	private string BuildBashPathSetupCommand()
	{
		var home = SafeGetHomeDirectory();
		if (string.IsNullOrWhiteSpace(home))
			return BuildPosixProfileCommand("$HOME/.bashrc");

		return string.Join("; ", ResolveBashProfiles(home).Select(profile => BuildPosixProfileCommand(profile.Path)));
	}

	private static string BuildPosixProfileCommand(string profilePath)
	{
		var quotedProfilePath = profilePath.StartsWith("$HOME/", StringComparison.Ordinal)
			? $"\"{profilePath}\""
			: ShellQuote(profilePath);
		return $"grep -qxF '{PosixPathSetupLine}' {quotedProfilePath} 2>/dev/null || " +
		       $"printf '\\n{PosixPathSetupLine}\\n' >> {quotedProfilePath}";
	}

	private IReadOnlyList<UnixShellProfile> ResolveUnixShellProfiles(string home) => GetUnixShellName() switch
	{
		"bash" => ResolveBashProfiles(home),
		"zsh" => [new UnixShellProfile(Path.Combine(ResolveZshConfigDirectory(home), ".zshrc"), PosixPathSetupLine)],
		"fish" => [new UnixShellProfile(Path.Combine(ResolveFishConfigDirectory(home), "config.fish"), FishPathSetupLine)],
		_ => [new UnixShellProfile(Path.Combine(home, ".profile"), PosixPathSetupLine)]
	};

	private static IReadOnlyList<UnixShellProfile> ResolveBashProfiles(string home)
	{
		// Bash uses different files for interactive and login shells; configure both with an idempotent line.
		var loginProfile = new[] { ".bash_profile", ".bash_login", ".profile" }
			.Select(fileName => Path.Combine(home, fileName))
			.FirstOrDefault(File.Exists) ?? Path.Combine(home, ".profile");

		return
		[
			new UnixShellProfile(Path.Combine(home, ".bashrc"), PosixPathSetupLine),
			new UnixShellProfile(loginProfile, PosixPathSetupLine)
		];
	}

	private string ResolveFishConfigDirectory(string? home)
	{
		var xdgConfigHome = SafeGetOptionalPath(_options.XdgConfigHomeProvider);
		return !string.IsNullOrWhiteSpace(xdgConfigHome) && Path.IsPathRooted(xdgConfigHome)
			? Path.Combine(xdgConfigHome, "fish")
			: Path.Combine(home ?? string.Empty, ".config", "fish");
	}

	private string ResolveZshConfigDirectory(string? home)
	{
		var zdotDirectory = SafeGetOptionalPath(_options.ZdotDirectoryProvider);
		if (string.IsNullOrWhiteSpace(zdotDirectory))
			return home ?? string.Empty;

		if (zdotDirectory.StartsWith("~/", StringComparison.Ordinal))
			return Path.Combine(home ?? string.Empty, zdotDirectory[2..]);

		return Path.IsPathRooted(zdotDirectory) ? zdotDirectory : home ?? string.Empty;
	}

	private string GetZshProfileCommandPath()
	{
		var home = SafeGetHomeDirectory();
		var directory = ResolveZshConfigDirectory(home);
		return directory.Equals(home, StringComparison.Ordinal)
			? "$HOME/.zshrc"
			: Path.Combine(directory, ".zshrc");
	}

	private static string ToHomeRelativeDisplayPath(string directory, string? home, string fileName)
	{
		var path = Path.Combine(directory, fileName);
		if (!string.IsNullOrWhiteSpace(home))
		{
			var relative = Path.GetRelativePath(home, path);
			if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
				return "~/" + PathUtility.NormalizeSeparators(relative);
		}

		return path;
	}

	private static string? SafeGetOptionalPath(Func<string?> provider)
	{
		try
		{
			return provider();
		}
		catch
		{
			return null;
		}
	}

	private static void EnsureShellProfileContainsPathSetup(UnixShellProfile profile)
	{
		// Edit a fixed profile line directly: setup must not execute shell text or rewrite user content.
		var directory = Path.GetDirectoryName(profile.Path);
		if (!string.IsNullOrWhiteSpace(directory))
			Directory.CreateDirectory(directory);

		using var stream = new FileStream(
			profile.Path,
			FileMode.OpenOrCreate,
			FileAccess.ReadWrite,
			FileShare.Read);
		using var reader = new StreamReader(
			stream,
			Encoding.UTF8,
			detectEncodingFromByteOrderMarks: true,
			leaveOpen: true);
		var content = reader.ReadToEnd();
		if (ContainsProfileLine(content, profile.SetupLine))
			return;

		var prefix = content.Length > 0 && !content.EndsWith('\n') ? "\n" : string.Empty;
		var marker = ContainsProfileLine(content, UnixPathMarker)
			? string.Empty
			: UnixPathMarker + "\n";
		var addition = prefix + marker + profile.SetupLine + "\n";
		var bytes = reader.CurrentEncoding.GetBytes(addition);

		stream.Position = stream.Length;
		stream.Write(bytes);
		stream.Flush(flushToDisk: true);
	}

	private static bool ContainsProfileLine(string content, string expectedLine)
	{
		foreach (var line in content.Split('\n'))
		{
			if (string.Equals(line.Trim(), expectedLine, StringComparison.Ordinal))
				return true;
		}

		return false;
	}

	private void EnsureCurrentProcessPathContains(string userBinDirectory, bool forceFirst = false)
	{
		// New terminals read the profile; updating this process lets the immediate probe converge too.
		var currentPath = SafeGetPathVariable();
		if (!forceFirst && IsDirectoryInPathValue(userBinDirectory, currentPath))
			return;

		var separator = _options.PathListSeparator ?? ':';
		var entries = string.IsNullOrWhiteSpace(currentPath)
			? new List<string>()
			: currentPath.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
		entries.RemoveAll(entry => AreSameDirectory(entry, userBinDirectory));
		entries.Insert(0, userBinDirectory);
		var updatedPath = string.Join(separator, entries);
		_options.ProcessPathVariableWriter(updatedPath);
	}

	private string GetUnixShellName()
	{
		try
		{
			return Path.GetFileName(_options.ShellPathProvider())?.ToLowerInvariant() ?? string.Empty;
		}
		catch
		{
			return string.Empty;
		}
	}

	private sealed record UnixShellProfile(string Path, string SetupLine);
	private sealed record CommandPathResolution(bool IsManagedDirectoryInPath, string? ResolvedCommandPath);
	private sealed record ManagedWrapperReadResult(ManagedWrapperReadStatus Status, string? TargetPath);
	private sealed class SetupLockLease(Mutex mutex) : IDisposable
	{
		public void Dispose()
		{
			try
			{
				mutex.ReleaseMutex();
			}
			finally
			{
				mutex.Dispose();
			}
		}
	}

	private enum ManagedWrapperReadStatus
	{
		Managed,
		Foreign,
		PermissionDenied,
		Unreadable
	}

	internal static ProcessStartInfo CreateLauncherValidationStartInfo(string commandPath)
	{
		var startInfo = new ProcessStartInfo
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};

		if (OperatingSystem.IsWindows() && commandPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
		{
			startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
			startInfo.Arguments = $"/d /c \"\"{commandPath}\" --version\"";
		}
		else
		{
			startInfo.FileName = commandPath;
			startInfo.ArgumentList.Add("--version");
		}

		return startInfo;
	}

	private static void TryKillProcess(Process? process)
	{
		if (process is null)
			return;

		try
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
		}
		catch
		{
			// Process cleanup is best effort after validation failure or timeout.
		}
	}

}

internal static class WindowsEnvironmentChangeBroadcaster
{
	private const int HwndBroadcast = 0xffff;
	private const int WmSettingChange = 0x001a;
	private const int SmtoAbortIfHung = 0x0002;

	public static void NotifyEnvironmentChanged()
	{
		if (!OperatingSystem.IsWindows())
			return;

		try
		{
			_ = SendMessageTimeout(
				new IntPtr(HwndBroadcast),
				WmSettingChange,
				UIntPtr.Zero,
				"Environment",
				SmtoAbortIfHung,
				100,
				out _);
		}
		catch
		{
			// Updating the registry-backed user PATH is the durable operation.
			// Broadcasting only helps already-running shells notice the change sooner.
		}
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
	private static extern IntPtr SendMessageTimeout(
		IntPtr hWnd,
		int msg,
		UIntPtr wParam,
		string lParam,
		int flags,
		int timeout,
		out UIntPtr result);
}

internal static class WindowsPackageIdentityProbe
{
	private const int Success = 0;
	private const int ErrorInsufficientBuffer = 122;
	private const int AppModelErrorNoPackage = 15700;

	public static bool IsPackagedApp()
	{
		if (!OperatingSystem.IsWindows())
			return false;

		var length = 0;
		var result = GetCurrentPackageFullName(ref length, null);
		return result is Success or ErrorInsufficientBuffer;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);
}
