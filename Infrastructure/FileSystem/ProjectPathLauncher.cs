namespace DevProjex.Infrastructure.FileSystem;

public enum DesktopPlatform
{
	Windows,
	MacOS,
	Linux
}

public enum ProjectPathLaunchFailure
{
	None,
	PathNotFound,
	LaunchFailed
}

public readonly record struct ProjectPathLaunchResult(
	bool Succeeded,
	ProjectPathLaunchFailure Failure,
	string? ErrorMessage = null)
{
	public static ProjectPathLaunchResult Success { get; } =
		new(true, ProjectPathLaunchFailure.None);
}

public interface IProjectPathLauncher
{
	Task<ProjectPathLaunchResult> LaunchAsync(
		string fullPath,
		bool isDirectory,
		CancellationToken cancellationToken = default);
}

internal readonly record struct ProjectPathLaunchCandidate(
	ProcessStartInfo StartInfo,
	bool RequiresSuccessfulExit = false);

internal static class ProjectPathStartInfoFactory
{
	public static IReadOnlyList<ProjectPathLaunchCandidate> CreateCandidates(
		DesktopPlatform platform,
		string fullPath,
		bool isDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
		if (!IsAbsolutePath(platform, fullPath))
			throw new ArgumentException("The file-manager path must be absolute.", nameof(fullPath));

		return platform switch
		{
			DesktopPlatform.Windows => [CreateWindows(fullPath, isDirectory)],
			DesktopPlatform.MacOS => [CreateMacOS(fullPath, isDirectory)],
			DesktopPlatform.Linux => CreateLinux(fullPath, isDirectory),
			_ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null)
		};
	}

	private static bool IsAbsolutePath(DesktopPlatform platform, string path) =>
		platform switch
		{
			DesktopPlatform.Windows =>
				path.StartsWith("\\\\", StringComparison.Ordinal) ||
				(path.Length >= 3 &&
				 char.IsAsciiLetter(path[0]) &&
				 path[1] == ':' &&
				 path[2] is '\\' or '/'),
			DesktopPlatform.MacOS or DesktopPlatform.Linux => path.StartsWith("/", StringComparison.Ordinal),
			_ => false
		};

	private static ProjectPathLaunchCandidate CreateWindows(string path, bool isDirectory)
	{
		var startInfo = CreateStartInfo("explorer.exe", useShellExecute: true);
		startInfo.ArgumentList.Add(isDirectory ? path : $"/select,{path}");
		return new ProjectPathLaunchCandidate(startInfo);
	}

	private static ProjectPathLaunchCandidate CreateMacOS(string path, bool isDirectory)
	{
		var startInfo = CreateStartInfo("open");
		if (!isDirectory)
			startInfo.ArgumentList.Add("-R");
		startInfo.ArgumentList.Add(path);
		return new ProjectPathLaunchCandidate(startInfo);
	}

	private static IReadOnlyList<ProjectPathLaunchCandidate> CreateLinux(
		string path,
		bool isDirectory)
	{
		if (isDirectory)
		{
			var openDirectory = CreateStartInfo("xdg-open");
			openDirectory.ArgumentList.Add(path);
			return [new ProjectPathLaunchCandidate(openDirectory)];
		}

		var showItem = CreateStartInfo("dbus-send");
		showItem.RedirectStandardError = true;
		showItem.RedirectStandardOutput = true;
		showItem.ArgumentList.Add("--session");
		showItem.ArgumentList.Add("--print-reply");
		showItem.ArgumentList.Add("--dest=org.freedesktop.FileManager1");
		showItem.ArgumentList.Add("/org/freedesktop/FileManager1");
		showItem.ArgumentList.Add("org.freedesktop.FileManager1.ShowItems");
		showItem.ArgumentList.Add($"array:string:{CreateFileUri(path)}");
		showItem.ArgumentList.Add("string:");

		var openParent = CreateStartInfo("xdg-open");
		openParent.ArgumentList.Add(GetPosixParentPath(path));
		return
		[
			new ProjectPathLaunchCandidate(showItem, RequiresSuccessfulExit: true),
			new ProjectPathLaunchCandidate(openParent)
		];
	}

	private static ProcessStartInfo CreateStartInfo(
		string fileName,
		bool useShellExecute = false) =>
		new()
		{
			FileName = fileName,
			UseShellExecute = useShellExecute,
			CreateNoWindow = !useShellExecute
		};

	private static string CreateFileUri(string path)
	{
		if (!path.StartsWith("/", StringComparison.Ordinal))
			throw new ArgumentException("A Linux file-manager path must be absolute.", nameof(path));

		var encodedPath = string.Join(
			'/',
			path.Split('/').Select(Uri.EscapeDataString));
		return $"file://{encodedPath}";
	}

	private static string GetPosixParentPath(string path)
	{
		var normalized = path.TrimEnd('/');
		var separator = normalized.LastIndexOf('/');
		return separator switch
		{
			< 0 => ".",
			0 => "/",
			_ => normalized[..separator]
		};
	}
}

public sealed class ProjectPathLauncher : IProjectPathLauncher
{
	private static readonly TimeSpan CandidateTimeout = TimeSpan.FromSeconds(5);
	private readonly DesktopPlatform _platform;
	private readonly Func<string, bool> _fileExists;
	private readonly Func<string, bool> _directoryExists;
	private readonly Func<ProjectPathLaunchCandidate, CancellationToken, Task<bool>> _launch;

	public ProjectPathLauncher()
		: this(
			ResolveCurrentPlatform(),
			File.Exists,
			Directory.Exists,
			LaunchCandidateAsync)
	{
	}

	internal ProjectPathLauncher(
		DesktopPlatform platform,
		Func<string, bool> fileExists,
		Func<string, bool> directoryExists,
		Func<ProjectPathLaunchCandidate, CancellationToken, Task<bool>> launch)
	{
		_platform = platform;
		_fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
		_directoryExists = directoryExists ?? throw new ArgumentNullException(nameof(directoryExists));
		_launch = launch ?? throw new ArgumentNullException(nameof(launch));
	}

	public async Task<ProjectPathLaunchResult> LaunchAsync(
		string fullPath,
		bool isDirectory,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
		cancellationToken.ThrowIfCancellationRequested();
		bool pathExists;
		try
		{
			pathExists = isDirectory ? _directoryExists(fullPath) : _fileExists(fullPath);
		}
		catch (Exception exception) when (IsExpectedLaunchFailure(exception))
		{
			return new ProjectPathLaunchResult(
				false,
				ProjectPathLaunchFailure.LaunchFailed,
				exception.Message);
		}

		if (!pathExists)
		{
			return new ProjectPathLaunchResult(
				false,
				ProjectPathLaunchFailure.PathNotFound);
		}

		Exception? lastError = null;
		foreach (var candidate in ProjectPathStartInfoFactory.CreateCandidates(
			         _platform,
			         fullPath,
			         isDirectory))
		{
			try
			{
				if (await _launch(candidate, cancellationToken).ConfigureAwait(false))
					return ProjectPathLaunchResult.Success;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception) when (IsExpectedLaunchFailure(exception))
			{
				lastError = exception;
			}
		}

		return new ProjectPathLaunchResult(
			false,
			ProjectPathLaunchFailure.LaunchFailed,
			lastError?.Message);
	}

	private static async Task<bool> LaunchCandidateAsync(
		ProjectPathLaunchCandidate candidate,
		CancellationToken cancellationToken)
	{
		using var process = Process.Start(candidate.StartInfo);
		if (process is null)
			return false;
		if (!candidate.RequiresSuccessfulExit)
			return true;

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(CandidateTimeout);
		try
		{
			await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
			return process.ExitCode == 0;
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			TryKill(process);
			return false;
		}
		catch (OperationCanceledException)
		{
			TryKill(process);
			throw;
		}
	}

	private static void TryKill(Process process)
	{
		try
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
		}
		catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
		}
	}

	private static bool IsExpectedLaunchFailure(Exception exception) =>
		exception is InvalidOperationException or
			System.ComponentModel.Win32Exception or
			IOException or
			UnauthorizedAccessException or
			System.Security.SecurityException or
			NotSupportedException;

	private static DesktopPlatform ResolveCurrentPlatform() =>
		OperatingSystem.IsWindows()
			? DesktopPlatform.Windows
			: OperatingSystem.IsMacOS()
				? DesktopPlatform.MacOS
				: DesktopPlatform.Linux;
}
