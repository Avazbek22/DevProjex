using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.DesktopControl;
using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Terminal.Execution;

public sealed class DoctorCommandHandler(
	TerminalServices services,
	ITerminalEnvironment environment,
	DesktopInstanceRegistry? desktopRegistry = null,
	Func<string>? currentDirectoryProvider = null)
{
	private readonly DesktopInstanceRegistry _desktopRegistry =
		desktopRegistry ?? new DesktopInstanceRegistry();
	private readonly Func<string> _currentDirectoryProvider =
		currentDirectoryProvider ?? Directory.GetCurrentDirectory;

	public async Task<int> ExecuteAsync(
		bool json,
		CancellationToken cancellationToken)
	{
		var checks = await BuildChecksAsync(cancellationToken).ConfigureAwait(false);
		if (json)
		{
			environment.Output.WriteLine(JsonSerializer.Serialize(
				new
				{
					schemaVersion = 1,
					kind = "devprojex-doctor",
					version = ResolveVersion(),
					os = RuntimeInformation.OSDescription,
					architecture = RuntimeInformation.ProcessArchitecture.ToString(),
					packageType = ResolvePackageType(),
					singleFile = IsSingleFile(),
					checks
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
			environment.Output.WriteLine($"{RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})");
			foreach (var check in checks)
			{
				environment.Output.WriteLine(
					$"{(check.Ok ? "[OK]" : "[WARN]")} {ResolveCheckName(check.Name)}: {check.Detail}");
				if (!string.IsNullOrWhiteSpace(check.Hint))
					environment.Output.WriteLine(
						$"  {services.Localization["Terminal.Label.Hint"]}: {check.Hint}");
			}
		}

		return CommandLineExitCodes.Success;
	}

	private async Task<IReadOnlyList<DoctorCheck>> BuildChecksAsync(
		CancellationToken cancellationToken)
	{
		var terminal = services.TerminalCommandSetupService.Probe();
		var currentDirectory = _currentDirectoryProvider();
		var appData = UserDataPathResolver.GetConfigurationRoot();
		var localData = UserDataPathResolver.GetLocalDataRoot();
		var temp = Path.GetTempPath();
		var cache = Path.Combine(temp, "DevProjex");
		var gitAvailable = await TryReadGitVersionAsync(cancellationToken).ConfigureAwait(false);
		var trackedReadiness = services.GitTrackedModeReadinessProbe.Probe(
			currentDirectory,
			cancellationToken);
		var desktopRegistry = await _desktopRegistry
			.ProbeAsync(removeStale: false, cancellationToken)
			.ConfigureAwait(false);
		return
		[
			new DoctorCheck(
				"terminal-launcher",
				terminal.IsReady,
				terminal.State.ToString(),
				terminal.IsReady ? null : terminal.ShellProfileHint ?? terminal.PathSetupCommand),
			new DoctorCheck(
				"path-resolution",
				terminal.State != TerminalCommandSetupState.CommandShadowed,
				terminal.ResolvedCommandPath ?? L("Terminal.Doctor.Value.NotResolved"),
				terminal.State == TerminalCommandSetupState.CommandShadowed
					? L("Terminal.Doctor.Hint.PathShadowed")
					: null),
			new DoctorCheck(
				"interactive-tty",
				environment.IsInputInteractive && environment.IsOutputInteractive,
				$"stdin={environment.IsInputInteractive}, stdout={environment.IsOutputInteractive}, stderr={environment.IsErrorInteractive}",
				null),
			new DoctorCheck(
				"terminal-capabilities",
				!environment.IsTermDumb,
				$"color={!environment.IsNoColor && !environment.IsTermDumb}, unicode={environment.SupportsUnicode}, mouse={environment.IsInputInteractive && environment.IsOutputInteractive && !environment.IsTermDumb}",
				environment.IsTermDumb
					? L("Terminal.Doctor.Hint.DumbTerminal")
					: null),
			new DoctorCheck(
				"unicode",
				environment.SupportsUnicode,
				environment.SupportsUnicode
					? L("Terminal.Doctor.Value.Available")
					: L("Terminal.Doctor.Value.AsciiFallback"),
				null),
			new DoctorCheck(
				"git",
				gitAvailable.Available,
				gitAvailable.Available
					? gitAvailable.Version
					: L("Terminal.Doctor.Value.Unavailable"),
				L("Terminal.Doctor.Hint.GitOptional")),
			new DoctorCheck(
				"tracked-git-mode",
				trackedReadiness.HasReadableIndex,
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
				CanReadDirectory(currentDirectory),
				currentDirectory,
				null),
			new DoctorCheck(
				"profile-store",
				CanReadExistingProfileStore(appData),
				ResolveProfileStorePath(appData),
				L("Terminal.Doctor.Hint.ProfileStore")),
			BuildWritableCheck("application-data", appData),
			BuildWritableCheck("local-data", localData),
			BuildWritableCheck("temporary-directory", temp),
			BuildWritableDestinationCheck("cache-directory", cache),
			new DoctorCheck(
				"desktop-ipc",
				desktopRegistry.StaleEntryCount == 0,
				services.Localization.Format(
					"Terminal.Doctor.Detail.Instances",
					desktopRegistry.Instances.Count,
					desktopRegistry.StaleEntryCount),
				desktopRegistry.StaleEntryCount == 0
					? null
					: L("Terminal.Doctor.Hint.StaleRegistry")),
			new DoctorCheck(
				"environment",
				!environment.IsTermDumb,
				$"TERM={ReadVariable("TERM") ?? "<unset>"}, NO_COLOR={ReadVariable("NO_COLOR") ?? "<unset>"}, CI={ReadVariable("CI") ?? "<unset>"}",
				environment.IsTermDumb ? L("Terminal.Doctor.Hint.DumbTerminal") : null)
		];
	}

	private DoctorCheck BuildWritableCheck(string name, string path)
	{
		var writable = CanWriteDirectory(path);
		return new DoctorCheck(
			name,
			writable,
			string.IsNullOrWhiteSpace(path) ? L("Terminal.Doctor.Value.Unavailable") : path,
			writable ? null : L("Terminal.Doctor.Hint.WritableData"));
	}

	private DoctorCheck BuildWritableDestinationCheck(string name, string path)
	{
		var existingAncestor = path;
		while (!string.IsNullOrWhiteSpace(existingAncestor) &&
		       !Directory.Exists(existingAncestor))
		{
			existingAncestor = Path.GetDirectoryName(existingAncestor);
		}

		var writable = !string.IsNullOrWhiteSpace(existingAncestor) &&
		               CanWriteDirectory(existingAncestor);
		return new DoctorCheck(
			name,
			writable,
			path,
			writable ? null : L("Terminal.Doctor.Hint.WritableCache"));
	}

	private static async Task<(bool Available, string Version)> TryReadGitVersionAsync(
		CancellationToken cancellationToken)
	{
		try
		{
			using var process = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = OperatingSystem.IsWindows() ? "git.exe" : "git",
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true
				}
			};
			process.StartInfo.ArgumentList.Add("--version");
			if (!process.Start())
				return (false, "unavailable");
			using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeoutSource.CancelAfter(TimeSpan.FromSeconds(3));
			var outputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
			await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
			var version = (await outputTask.ConfigureAwait(false)).Trim();
			return (process.ExitCode == 0 && version.Length > 0, version);
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

	private static string ResolveProfileStorePath(string appData) =>
		string.IsNullOrWhiteSpace(appData)
			? "unavailable"
			: Path.Combine(appData, "DevProjex", "project-profiles.json");

	private static bool CanReadExistingProfileStore(string appData)
	{
		var path = ResolveProfileStorePath(appData);
		if (path == "unavailable")
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

	private sealed record DoctorCheck(string Name, bool Ok, string Detail, string? Hint);
}
