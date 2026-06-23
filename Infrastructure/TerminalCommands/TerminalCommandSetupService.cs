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
	public Func<string?> UserPathVariableProvider { get; init; } =
		() => Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
	public Func<string?> MachinePathVariableProvider { get; init; } =
		() => Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);
	public Action<string> UserPathVariableWriter { get; init; } =
		value => Environment.SetEnvironmentVariable("PATH", value, EnvironmentVariableTarget.User);
	public Func<string?> ExecutablePathProvider { get; init; } = TerminalCommandSetupService.GetCurrentExecutablePath;
	public char? PathListSeparator { get; init; }
}

public sealed class TerminalCommandSetupService(TerminalCommandSetupServiceOptions? options = null)
	: ITerminalCommandSetupService
{
	private const string UnixWrapperMarker = "# DevProjex terminal command wrapper";
	private const string WindowsLauncherMarker = "rem DevProjex terminal command wrapper";
	private const string UnixTargetPrefix = "# target: ";
	private const string WindowsTargetPrefix = "rem target: ";
	private const string ShellProfileHint =
		"Add export PATH=\"$HOME/.local/bin:$PATH\" to your shell profile if ~/.local/bin is not already in PATH.";
	private const string WindowsPathHint =
		"DevProjex will add its terminal launcher folder to your user PATH. Restart already-open terminal windows after enabling it.";

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

			File.WriteAllText(tempPath, BuildCommandFileContent(targetPath), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			if (_options.Platform is not TerminalCommandHostPlatform.Windows)
				TrySetUnixExecutableMode(tempPath);
			File.Move(tempPath, commandPath, overwrite: true);
			if (_options.Platform is TerminalCommandHostPlatform.Windows)
				EnsureWindowsUserBinDirectoryIsInPath(initial.UserBinDirectory);
			else
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
		return string.Join(
			"\n",
			"#!/bin/sh",
			UnixWrapperMarker,
			UnixTargetPrefix + targetPath,
			"exec " + ShellQuote(targetPath) + " \"$@\"",
			string.Empty);
	}

	internal static string BuildWindowsLauncherContent(string targetPath)
	{
		var escapedTargetPath = EscapeWindowsBatchValue(targetPath);
		return string.Join(
			"\r\n",
			"@echo off",
			WindowsLauncherMarker,
			WindowsTargetPrefix + targetPath,
			"\"" + escapedTargetPath + "\" %*",
			string.Empty);
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
		var isUserBinInPath = IsWindowsUserBinDirectoryReachable(userBinDirectory);

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

		var installedTargetPath = ReadManagedWrapperTarget(commandPath);
		if (installedTargetPath is null)
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

		if (AreSamePath(installedTargetPath, targetPath) && File.Exists(installedTargetPath) && isUserBinInPath)
		{
			return WindowsPortableSnapshot(
				TerminalCommandSetupState.Installed,
				commandPath,
				targetPath,
				installedTargetPath,
				userBinDirectory,
				isUserBinInPath,
				canInstall: false,
				canRepair: false);
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

	private static TerminalCommandSetupSnapshot WindowsPortableSnapshot(
		TerminalCommandSetupState state,
		string? commandPath,
		string? targetPath,
		string? installedTargetPath,
		string? userBinDirectory,
		bool isUserBinInPath,
		bool canInstall,
		bool canRepair) =>
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
			ShellProfileHint: isUserBinInPath ? null : WindowsPathHint);

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
				targetPrefix = WindowsTargetPrefix;
			}

			if (string.Equals(marker, WindowsLauncherMarker, StringComparison.OrdinalIgnoreCase))
				targetPrefix = WindowsTargetPrefix;

			if (!IsManagedWrapperMarker(marker) ||
			    target is null ||
			    !target.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
				return null;

			return target[targetPrefix.Length..];
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

	private static bool IsManagedWrapperMarker(string? value) =>
		string.Equals(value, UnixWrapperMarker, StringComparison.Ordinal) ||
		string.Equals(value, WindowsLauncherMarker, StringComparison.OrdinalIgnoreCase);

	private bool IsDirectoryInPath(string directory)
	{
		var path = SafeGetPathVariable();
		return IsDirectoryInPathValue(directory, path);
	}

	private bool IsDirectoryInPathValue(string directory, string? path)
	{
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

	private bool IsWindowsUserBinDirectoryReachable(string directory) =>
		IsDirectoryInPathValue(directory, SafeGetUserPathVariable()) ||
		IsDirectoryInPathValue(directory, SafeGetMachinePathVariable());

	private void EnsureWindowsUserBinDirectoryIsInPath(string? directory)
	{
		if (string.IsNullOrWhiteSpace(directory) || IsWindowsUserBinDirectoryReachable(directory))
			return;

		var userPath = SafeGetUserPathVariable();
		var separator = _options.PathListSeparator ?? ';';
		var entries = string.IsNullOrWhiteSpace(userPath)
			? new List<string>()
			: userPath.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

		if (!entries.Any(entry => AreSamePath(entry, directory)))
			entries.Add(directory);

		_options.UserPathVariableWriter(string.Join(separator, entries));
		WindowsEnvironmentChangeBroadcaster.NotifyEnvironmentChanged();
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

	private string NormalizePath(string value)
	{
		var trimmed = TrimTrailingDirectorySeparators(value.Trim());
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
