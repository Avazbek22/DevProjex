using System.Runtime.InteropServices;

namespace DevProjex.Infrastructure.AppInstances;

public sealed class AppInstanceLauncher : IAppInstanceLauncher
{
    private const int AppModelErrorNoPackage = 15700;

    public AppInstanceLaunchResult LaunchNewInstance()
    {
        var launchContext = BuildCurrentContext();
        var candidates = AppInstanceStartInfoFactory.CreateCandidates(launchContext);
        Exception? lastError = null;

        foreach (var candidate in candidates)
        {
            try
            {
                // Each candidate is a fully independent process launch.
                // No project handoff or single-instance redirection happens here by design.
                using var process = Process.Start(candidate);
                if (process is not null)
                    return AppInstanceLaunchResult.Success;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        return AppInstanceLaunchResult.Failure(
            lastError?.Message ?? "No valid launch candidate was available for the current process.");
    }

    internal static AppInstanceLaunchContext BuildCurrentContext()
    {
        var processPath = Environment.ProcessPath;
        var entryAssemblyPath = ProcessEntryPointResolver.ResolveManagedAssemblyPath();
        var workingDirectory = ResolveWorkingDirectory(processPath);

        return new AppInstanceLaunchContext(
            IsWindows: OperatingSystem.IsWindows(),
            ProcessPath: processPath,
            EntryAssemblyPath: entryAssemblyPath,
            AppHostPath: ProcessEntryPointResolver.ResolveCurrentAppHostPath(),
            WorkingDirectory: workingDirectory,
            WindowsPackageFamilyName: TryGetCurrentPackageFamilyName());
    }

    private static string ResolveWorkingDirectory(string? processPath)
    {
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var directory = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrWhiteSpace(directory))
                return directory;
        }

        return AppContext.BaseDirectory;
    }

    internal static string? TryGetCurrentPackageFamilyName()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        uint length = 0;
        var firstResult = GetCurrentPackageFamilyName(ref length, null);
        if (firstResult == AppModelErrorNoPackage)
            return null;

        if (length == 0)
            return null;

        var builder = new StringBuilder((int)length);
        var secondResult = GetCurrentPackageFamilyName(ref length, builder);
        return secondResult == 0
            ? builder.ToString()
            : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref uint packageFamilyNameLength, StringBuilder? packageFamilyName);
}
