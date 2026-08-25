using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using DevProjex.Infrastructure.Persistence;
using DevProjex.Infrastructure.Processes;
using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.DesktopControl;
using DevProjex.Terminal.Rendering;

namespace DevProjex.Terminal.Execution;

public sealed record DoctorStorageRoots(
	string Configuration,
	string Data,
	string State,
	string Cache);

public sealed class DoctorCommandHandler(
	TerminalServices services,
	ITerminalEnvironment environment,
	DesktopInstanceRegistry? desktopRegistry = null,
	Func<string>? currentDirectoryProvider = null,
	Func<DoctorStorageRoots>? storageRootsProvider = null)
{
	private const int MaximumGitVersionOutputCharacters = 64 * 1024;
	private readonly DesktopInstanceRegistry _desktopRegistry =
		desktopRegistry ?? new DesktopInstanceRegistry();
	private readonly Func<string> _currentDirectoryProvider =
		currentDirectoryProvider ?? Directory.GetCurrentDirectory;
	private readonly Func<DoctorStorageRoots> _storageRootsProvider =
		storageRootsProvider ?? ResolveStorageRoots;

	public async Task<int> ExecuteAsync(
		bool json,
		CancellationToken cancellationToken)
	{
		var checks = await BuildChecksAsync(cancellationToken).ConfigureAwait(false);
		var hasFailure = checks.Any(static check => check.Status == DoctorCheckStatus.Failure);
		if (json)
		{
			environment.Output.WriteLine(JsonSerializer.Serialize(
				new
				{
					schemaVersion = 1,
					kind = "devprojex-doctor",
					version = ResolveVersion(),
					os = RuntimeInformation.OSDescription,
					architecture = RuntimeInformation.ProcessArchitecture
						.ToString()
						.ToLowerInvariant(),
					packageType = ResolvePackageType(),
					singleFile = IsSingleFile(),
					checks = checks.Select(static check => new
					{
						name = check.Name,
						code = check.Code,
						status = ToToken(check.Status),
						severity = ToSeverityToken(check.Status),
						detail = check.Detail,
						hint = check.Hint,
						path = check.Path is null
							? null
							: NormalizeMachinePath(check.Path)
					})
				},
				new JsonSerializerOptions
				{
					WriteIndented = true,
					PropertyNamingPolicy = JsonNamingPolicy.CamelCase
				}));
		}
		else
		{
			environment.Output.WriteLine($"DevProjex {ResolveVersion()}");
			environment.Output.WriteLine(
				$"{TerminalTextEscaping.EscapeSingleLine(RuntimeInformation.OSDescription)} " +
				$"({RuntimeInformation.ProcessArchitecture})");
			foreach (var check in checks)
			{
				environment.Output.WriteLine(
					$"{StatusMarker(check.Status)} {ResolveCheckName(check.Name)}: " +
					TerminalTextEscaping.EscapeSingleLine(check.Detail));
				if (!string.IsNullOrWhiteSpace(check.Hint))
					environment.Output.WriteLine(
						$"  {services.Localization["Terminal.Label.Hint"]}: " +
						TerminalTextEscaping.EscapeSingleLine(check.Hint));
			}
		}

		return hasFailure
			? CommandLineExitCodes.PolicyFailure
			: CommandLineExitCodes.Success;
	}

	private async Task<IReadOnlyList<DoctorCheck>> BuildChecksAsync(
		CancellationToken cancellationToken)
	{
		var terminal = services.TerminalCommandSetupService.Probe();
		var currentDirectory = _currentDirectoryProvider();
		var storageRoots = _storageRootsProvider();
		var terminalSettingsPath = services.TerminalSettingsStore.GetPath();
		var profileStorePath = services.LocalProfileStore is ProjectProfileStore profileStore
			? profileStore.GetPath()
			: Path.Combine(
				storageRoots.Configuration,
				"DevProjex",
				"project-profiles.json");
		var recentWorkspacesPath = services.RecentProjectsStore.GetPath();
		var temp = Path.GetTempPath();
		var repositoryCache = services.RepoCacheService.CacheRootPath;
		var gitAvailable = await TryReadGitVersionAsync(cancellationToken).ConfigureAwait(false);
		var trackedReadiness = services.GitTrackedModeReadinessProbe.Probe(
			currentDirectory,
			cancellationToken);
		var desktopIpc = await ProbeDesktopIpcAsync(cancellationToken).ConfigureAwait(false);
		return
		[
			new DoctorCheck(
				"terminal-launcher",
				Status(terminal.IsReady, DoctorCheckStatus.Warning),
				terminal.State.ToString(),
				terminal.IsReady ? null : terminal.ShellProfileHint ?? terminal.PathSetupCommand,
				terminal.ResolvedCommandPath),
			new DoctorCheck(
				"path-resolution",
				Status(
					terminal.State != TerminalCommandSetupState.CommandShadowed,
					DoctorCheckStatus.Warning),
				terminal.ResolvedCommandPath ?? L("Terminal.Doctor.Value.NotResolved"),
				terminal.State == TerminalCommandSetupState.CommandShadowed
					? L("Terminal.Doctor.Hint.PathShadowed")
					: null,
				terminal.ResolvedCommandPath),
			new DoctorCheck(
				"interactive-tty",
				Status(
					environment.IsInputInteractive && environment.IsOutputInteractive,
					DoctorCheckStatus.Warning),
				$"stdin={environment.IsInputInteractive}, stdout={environment.IsOutputInteractive}, stderr={environment.IsErrorInteractive}",
				null),
			new DoctorCheck(
				"terminal-capabilities",
				Status(!environment.IsTermDumb, DoctorCheckStatus.Warning),
				$"width={environment.Width}, height={environment.Height}, color={!environment.IsNoColor && !environment.IsTermDumb}, unicode={environment.SupportsUnicode}, mouse={environment.IsInputInteractive && environment.IsOutputInteractive && !environment.IsTermDumb}",
				environment.IsTermDumb
					? L("Terminal.Doctor.Hint.DumbTerminal")
					: null),
			new DoctorCheck(
				"unicode",
				Status(environment.SupportsUnicode, DoctorCheckStatus.Warning),
				environment.SupportsUnicode
					? L("Terminal.Doctor.Value.Available")
					: L("Terminal.Doctor.Value.AsciiFallback"),
				null),
			new DoctorCheck(
				"git",
				Status(gitAvailable.Available, DoctorCheckStatus.Warning),
				gitAvailable.Available
					? gitAvailable.Version
					: L("Terminal.Doctor.Value.Unavailable"),
				L("Terminal.Doctor.Hint.GitOptional")),
			new DoctorCheck(
				"tracked-git-mode",
				!gitAvailable.Available
					? DoctorCheckStatus.Skip
					: Status(
						trackedReadiness.HasReadableIndex,
						DoctorCheckStatus.Warning),
				trackedReadiness.HasReadableIndex
					? services.Localization.Format(
						"Terminal.Doctor.Detail.TrackedPaths",
						trackedReadiness.TrackedPathCount,
						trackedReadiness.RepositoryRoot ?? string.Empty)
					: L("Terminal.Doctor.Detail.NoReadableIndex"),
				trackedReadiness.HasReadableIndex
					? null
					: L("Terminal.Doctor.Hint.TrackedMode")),
			new DoctorCheck(
				"current-directory",
				Status(CanReadDirectory(currentDirectory), DoctorCheckStatus.Failure),
				currentDirectory,
				null,
				currentDirectory),
			new DoctorCheck(
				"profile-store",
				Status(
					CanReadExistingProfileStore(profileStorePath),
					DoctorCheckStatus.Failure),
				profileStorePath,
				L("Terminal.Doctor.Hint.ProfileStore"),
				profileStorePath),
			BuildWritableDestinationCheck(
				"configuration-root",
				storageRoots.Configuration,
				DoctorCheckStatus.Failure),
			BuildWritableDestinationCheck(
				"data-root",
				storageRoots.Data,
				DoctorCheckStatus.Warning),
			BuildWritableDestinationCheck(
				"state-root",
				storageRoots.State,
				DoctorCheckStatus.Warning),
			BuildWritableDestinationCheck(
				"cache-root",
				storageRoots.Cache,
				DoctorCheckStatus.Warning),
			BuildWritableFileDestinationCheck(
				"terminal-settings",
				terminalSettingsPath,
				DoctorCheckStatus.Failure),
			new DoctorCheck(
				"recent-workspaces",
				Status(
					CanReadExistingProfileStore(recentWorkspacesPath),
					DoctorCheckStatus.Warning),
				recentWorkspacesPath,
				null,
				recentWorkspacesPath),
			BuildWritableCheck(
				"temporary-directory",
				temp,
				DoctorCheckStatus.Failure),
			BuildWritableDestinationCheck(
				"repository-cache",
				repositoryCache,
				DoctorCheckStatus.Warning),
			new DoctorCheck(
				"desktop-ipc",
				desktopIpc.Status,
				services.Localization.Format(
					"Terminal.Doctor.Detail.Instances",
					desktopIpc.Snapshot.Instances.Count,
					desktopIpc.Snapshot.StaleEntryCount),
				desktopIpc.HintKey is null ? null : L(desktopIpc.HintKey),
				_desktopRegistry.RegistryDirectory),
			new DoctorCheck(
				"environment",
				Status(!environment.IsTermDumb, DoctorCheckStatus.Warning),
				$"TERM={ReadVariable("TERM") ?? "<unset>"}, " +
				$"NO_COLOR={IsVariableSet("NO_COLOR").ToString().ToLowerInvariant()}, " +
				$"CI={IsVariableSet("CI").ToString().ToLowerInvariant()}",
				environment.IsTermDumb ? L("Terminal.Doctor.Hint.DumbTerminal") : null)
		];
	}

	private DoctorCheck BuildWritableCheck(
		string name,
		string path,
		DoctorCheckStatus failureStatus)
	{
		var writable = CanWriteDirectory(path);
		return new DoctorCheck(
			name,
			Status(writable, failureStatus),
			string.IsNullOrWhiteSpace(path) ? L("Terminal.Doctor.Value.Unavailable") : path,
			writable ? null : L("Terminal.Doctor.Hint.WritableData"),
			path);
	}

	private DoctorCheck BuildWritableDestinationCheck(
		string name,
		string path,
		DoctorCheckStatus failureStatus)
	{
		var writable = CanCreateAtDestination(path);
		return new DoctorCheck(
			name,
			Status(writable, failureStatus),
			path,
			writable ? null : L("Terminal.Doctor.Hint.WritableCache"),
			path);
	}

	private DoctorCheck BuildWritableFileDestinationCheck(
		string name,
		string path,
		DoctorCheckStatus failureStatus)
	{
		var writable = CanWriteFileDestination(path);
		return new DoctorCheck(
			name,
			Status(writable, failureStatus),
			path,
			writable ? null : L("Terminal.Doctor.Hint.WritableCache"),
			path);
	}

	private static bool CanCreateAtDestination(string path)
	{
		var existingAncestor = path;
		while (!string.IsNullOrWhiteSpace(existingAncestor) &&
		       !Directory.Exists(existingAncestor))
		{
			if (File.Exists(existingAncestor))
				return false;

			existingAncestor = Path.GetDirectoryName(existingAncestor);
		}

		return !string.IsNullOrWhiteSpace(existingAncestor) &&
		       CanWriteDirectory(existingAncestor);
	}

	private static bool CanWriteFileDestination(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || Directory.Exists(path))
			return false;

		var parent = Path.GetDirectoryName(path);
		if (string.IsNullOrWhiteSpace(parent))
			return false;

		if (!File.Exists(path))
			return CanCreateAtDestination(parent);

		try
		{
			using var stream = new FileStream(
				path,
				FileMode.Open,
				FileAccess.ReadWrite,
				FileShare.ReadWrite | FileShare.Delete);
			return stream.CanRead &&
			       stream.CanWrite &&
			       CanWriteDirectory(parent);
		}
		catch (Exception exception) when (exception is
			       IOException or
			       UnauthorizedAccessException or
			       SecurityException)
		{
			return false;
		}
	}

	private async Task<DesktopIpcDoctorProbe> ProbeDesktopIpcAsync(
		CancellationToken cancellationToken)
	{
		var pathState = InspectDirectoryPath(_desktopRegistry.RegistryDirectory);
		if (pathState == DirectoryPathState.Missing)
		{
			return new DesktopIpcDoctorProbe(
				new DesktopRegistrySnapshot([], 0),
				DoctorCheckStatus.Skip,
				HintKey: null);
		}

		if (pathState == DirectoryPathState.Failure)
		{
			return new DesktopIpcDoctorProbe(
				new DesktopRegistrySnapshot([], 0),
				DoctorCheckStatus.Failure,
				"Terminal.Doctor.Hint.WritableData");
		}

		try
		{
			var snapshot = await _desktopRegistry
				.ProbeAsync(removeStale: false, cancellationToken)
				.ConfigureAwait(false);
			return new DesktopIpcDoctorProbe(
				snapshot,
				snapshot.StaleEntryCount == 0
					? DoctorCheckStatus.Pass
					: DoctorCheckStatus.Warning,
				snapshot.StaleEntryCount == 0
					? null
					: "Terminal.Doctor.Hint.StaleRegistry");
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception) when (exception is
			       IOException or
			       UnauthorizedAccessException or
			       SecurityException)
		{
			return new DesktopIpcDoctorProbe(
				new DesktopRegistrySnapshot([], 0),
				DoctorCheckStatus.Failure,
				"Terminal.Doctor.Hint.WritableData");
		}
	}

	private static DirectoryPathState InspectDirectoryPath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return DirectoryPathState.Failure;

		try
		{
			var fullPath = Path.GetFullPath(path);
			var root = Path.GetPathRoot(fullPath);
			if (string.IsNullOrWhiteSpace(root))
				return DirectoryPathState.Failure;

			var current = root;
			foreach (var segment in Path.GetRelativePath(root, fullPath)
				         .Split(
					         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
					         StringSplitOptions.RemoveEmptyEntries))
			{
				current = Path.Combine(current, segment);
				FileAttributes attributes;
				try
				{
					attributes = File.GetAttributes(current);
				}
				catch (Exception exception) when (exception is
					       FileNotFoundException or
					       DirectoryNotFoundException)
				{
					return DirectoryPathState.Missing;
				}

				if (!attributes.HasFlag(FileAttributes.Directory))
					return DirectoryPathState.Failure;
			}

			using var enumerator = Directory
				.EnumerateFileSystemEntries(fullPath)
				.GetEnumerator();
			_ = enumerator.MoveNext();
			return DirectoryPathState.Ready;
		}
		catch (Exception exception) when (exception is
			       ArgumentException or
			       IOException or
			       NotSupportedException or
			       UnauthorizedAccessException or
			       SecurityException)
		{
			return DirectoryPathState.Failure;
		}
	}

	private static async Task<(bool Available, string Version)> TryReadGitVersionAsync(
		CancellationToken cancellationToken)
	{
		try
		{
			using var process = new Process
			{
				StartInfo = CreateGitVersionStartInfo()
			};
			if (!process.Start())
				return (false, "unavailable");
			process.StandardInput.Close();
			using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeoutSource.CancelAfter(TimeSpan.FromSeconds(3));
			var outputTask = BoundedTextReader.ReadAsync(
				process.StandardOutput,
				MaximumGitVersionOutputCharacters,
				timeoutSource.Token);
			var errorTask = BoundedTextReader.ReadAsync(
				process.StandardError,
				MaximumGitVersionOutputCharacters,
				timeoutSource.Token);
			try
			{
				await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
				var output = await outputTask.ConfigureAwait(false);
				var error = await errorTask.ConfigureAwait(false);
				if (output.ExceededLimit || error.ExceededLimit)
					return (false, "unavailable");
				var version = output.Text.Trim();
				return (process.ExitCode == 0 && version.Length > 0, version);
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				TryTerminate(process);
				await BoundedTextReader
					.ObserveCompletionAsync(outputTask, errorTask)
					.ConfigureAwait(false);
				return (false, "unavailable");
			}
			catch (OperationCanceledException)
			{
				TryTerminate(process);
				await BoundedTextReader
					.ObserveCompletionAsync(outputTask, errorTask)
					.ConfigureAwait(false);
				throw;
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return (false, "unavailable");
		}
	}

	internal static ProcessStartInfo CreateGitVersionStartInfo()
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = OperatingSystem.IsWindows() ? "git.exe" : "git",
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		startInfo.ArgumentList.Add("--version");
		startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
		return startInfo;
	}

	private static void TryTerminate(Process process)
	{
		try
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
		}
		catch (Exception exception) when (exception is
			       InvalidOperationException or
			       NotSupportedException or
			       System.ComponentModel.Win32Exception)
		{
			// The process may already have exited or the platform may not expose tree termination.
		}
	}

	private static bool CanReadDirectory(string path)
	{
		try
		{
			using var enumerator = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
			_ = enumerator.MoveNext();
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool CanWriteDirectory(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
			return false;

		try
		{
			var probe = Path.Combine(path, $".devprojex-doctor-{Guid.NewGuid():N}.tmp");
			using (File.Create(probe, 1, FileOptions.DeleteOnClose))
			{
			}
			return !File.Exists(probe);
		}
		catch
		{
			return false;
		}
	}

	private static bool CanReadExistingProfileStore(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return false;

		var directory = Path.GetDirectoryName(path);
		if (string.IsNullOrWhiteSpace(directory))
			return false;
		if (!Directory.Exists(directory))
			return true;
		if (!File.Exists(path))
			return CanReadDirectory(directory);

		try
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			return stream.CanRead;
		}
		catch
		{
			return false;
		}
	}

	private string? ReadVariable(string name) =>
		environment.Variables.TryGetValue(name, out var value) ? value : null;

	private bool IsVariableSet(string name) =>
		!string.IsNullOrEmpty(ReadVariable(name));

	private static string ResolveVersion() =>
		Assembly.GetEntryAssembly()?
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
			.InformationalVersion ??
		Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ??
		"unknown";

	private string ResolvePackageType() =>
		services.TerminalCommandSetupService.Probe().State == TerminalCommandSetupState.ManagedByOperatingSystem
			? "store"
			: "portable";

	private string ResolveCheckName(string name) =>
		services.Localization[$"Terminal.Doctor.{name}"];

	private string L(string key) => services.Localization[key];

	private static bool IsSingleFile() => ProcessEntryPointResolver.IsSingleFile();

	private static DoctorStorageRoots ResolveStorageRoots() =>
		new(
			UserDataPathResolver.GetConfigurationRoot(),
			UserDataPathResolver.GetLocalDataRoot(),
			UserDataPathResolver.GetStateRoot(),
			UserDataPathResolver.GetCacheRoot());

	private static DoctorCheckStatus Status(
		bool passed,
		DoctorCheckStatus failureStatus) =>
		passed ? DoctorCheckStatus.Pass : failureStatus;

	private static string StatusMarker(DoctorCheckStatus status) =>
		status switch
		{
			DoctorCheckStatus.Pass => "[+]",
			DoctorCheckStatus.Warning => "[!]",
			DoctorCheckStatus.Failure => "[x]",
			DoctorCheckStatus.Skip => "[-]",
			_ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
		};

	private static string ToToken(DoctorCheckStatus status) =>
		status switch
		{
			DoctorCheckStatus.Pass => "pass",
			DoctorCheckStatus.Warning => "warning",
			DoctorCheckStatus.Failure => "failure",
			DoctorCheckStatus.Skip => "skip",
			_ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
		};

	private static string ToSeverityToken(DoctorCheckStatus status) =>
		status switch
		{
			DoctorCheckStatus.Pass => "info",
			DoctorCheckStatus.Warning => "warning",
			DoctorCheckStatus.Failure => "error",
			DoctorCheckStatus.Skip => "info",
			_ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
		};

	private static string NormalizeMachinePath(string path) =>
		PathUtility.NormalizeSeparators(path);

	private enum DoctorCheckStatus
	{
		Pass,
		Warning,
		Failure,
		Skip
	}

	private enum DirectoryPathState
	{
		Missing,
		Ready,
		Failure
	}

	private sealed record DesktopIpcDoctorProbe(
		DesktopRegistrySnapshot Snapshot,
		DoctorCheckStatus Status,
		string? HintKey);

	private sealed record DoctorCheck(
		string Name,
		DoctorCheckStatus Status,
		string Detail,
		string? Hint,
		string? Path = null)
	{
		public string Code { get; } =
			$"DPX-DOCTOR-{Name.ToUpperInvariant()}";
	}
}
