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
	public Func<string?> PathVariableProvider { get; init; } =
		() => Environment.GetEnvironmentVariable("PATH");
	public Func<string?> ExecutablePathProvider { get; init; } = TerminalCommandSetupService.GetCurrentExecutablePath;
	public char? PathListSeparator { get; init; }
}

public sealed class TerminalCommandSetupService(TerminalCommandSetupServiceOptions? options = null)
	: ITerminalCommandSetupService
{
	private const string WrapperMarker = "# DevProjex terminal command wrapper";
	private const string TargetPrefix = "# target: ";
	private const string ShellProfileHint =
		"Add export PATH=\"$HOME/.local/bin:$PATH\" to your shell profile if ~/.local/bin is not already in PATH.";

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
		var initial = Probe();
		if (initial.State is TerminalCommandSetupState.Installed or TerminalCommandSetupState.ManagedByOperatingSystem)
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

		if (!initial.IsActionable || string.IsNullOrWhiteSpace(initial.CommandPath) ||
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
			Directory.CreateDirectory(Path.GetDirectoryName(commandPath)!);

			if (File.Exists(commandPath) && ReadManagedWrapperTarget(commandPath) is null)
			{
				var conflict = initial with { State = TerminalCommandSetupState.ConflictingCommand, CanInstall = false, CanRepair = false };
				return new TerminalCommandInstallResult(
					Success: false,
					Outcome: TerminalCommandInstallOutcome.ConflictingCommand,
					Snapshot: conflict,
					ErrorMessage: "The command path is occupied by a file not managed by DevProjex.");
			}

			File.WriteAllText(tempPath, BuildWrapperContent(targetPath), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			TrySetUnixExecutableMode(tempPath);
			File.Move(tempPath, commandPath, overwrite: true);
			TrySetUnixExecutableMode(commandPath);

			var refreshed = Probe();
			var outcome = initial.State == TerminalCommandSetupState.Stale
				? TerminalCommandInstallOutcome.Repaired
				: TerminalCommandInstallOutcome.Created;

			return new TerminalCommandInstallResult(
				Success: refreshed.State == TerminalCommandSetupState.Installed,
				Outcome: outcome,
				Snapshot: refreshed,
				ErrorMessage: refreshed.State == TerminalCommandSetupState.Installed
					? null
					: "The wrapper was written but the command is still not reported as installed.");
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
		return string.Join(
			"\n",
			"#!/bin/sh",
			WrapperMarker,
			TargetPrefix + targetPath,
			"exec " + ShellQuote(targetPath) + " \"$@\"",
			string.Empty);
	}

	internal static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

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

		return Unsupported(TerminalCommandSetupState.UnsupportedOnCurrentPackage);
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
		var isUserBinInPath = IsDirectoryInPath(userBinDirectory);
		var shellProfileHint = isUserBinInPath ? null : ShellProfileHint;

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
				shellProfileHint);
		}

		var installedTargetPath = ReadManagedWrapperTarget(commandPath);
		if (installedTargetPath is null)
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

		if (AreSamePath(installedTargetPath, targetPath) && File.Exists(installedTargetPath))
		{
			return UnixSnapshot(
				TerminalCommandSetupState.Installed,
				commandPath,
				targetPath,
				installedTargetPath,
				userBinDirectory,
				isUserBinInPath,
				canInstall: false,
				canRepair: false,
				shellProfileHint);
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
		string? shellProfileHint) =>
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
			ShellProfileHint: shellProfileHint);

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

	private string? ReadManagedWrapperTarget(string commandPath)
	{
		try
		{
			using var reader = new StreamReader(commandPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
			var firstLine = reader.ReadLine();
			var marker = firstLine;
			var target = reader.ReadLine();
			if (string.Equals(firstLine, "#!/bin/sh", StringComparison.Ordinal))
			{
				marker = target;
				target = reader.ReadLine();
			}

			if (!string.Equals(marker, WrapperMarker, StringComparison.Ordinal) ||
			    target is null ||
			    !target.StartsWith(TargetPrefix, StringComparison.Ordinal))
				return null;

			return target[TargetPrefix.Length..];
		}
		catch (IOException)
		{
			return null;
		}
		catch (UnauthorizedAccessException)
		{
			return null;
		}
	}

	private bool IsDirectoryInPath(string directory)
	{
		var path = SafeGetPathVariable();
		if (string.IsNullOrWhiteSpace(path))
			return false;

		var separator = _options.PathListSeparator ?? (_options.Platform == TerminalCommandHostPlatform.Windows ? ';' : ':');
		foreach (var entry in path.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (AreSamePath(entry, directory))
				return true;
		}

		return false;
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

	private static string NormalizePath(string value)
	{
		var trimmed = value.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		if (trimmed.Length == 0)
			return trimmed;

		try
		{
			return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		}
		catch
		{
			return trimmed;
		}
	}

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
