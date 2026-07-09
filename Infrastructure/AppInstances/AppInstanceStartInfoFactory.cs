namespace DevProjex.Infrastructure.AppInstances;

internal readonly record struct AppInstanceLaunchContext(
    bool IsWindows,
    string? ProcessPath,
    string? EntryAssemblyPath,
    string WorkingDirectory,
    string? WindowsPackageFamilyName);

internal static class AppInstanceStartInfoFactory
{
    private const string WindowsStoreApplicationId = "App";

    public static IReadOnlyList<ProcessStartInfo> CreateCandidates(AppInstanceLaunchContext context)
    {
        var candidates = new List<ProcessStartInfo>(capacity: 2);

        if (context.IsWindows && !string.IsNullOrWhiteSpace(context.WindowsPackageFamilyName))
        {
            // Packaged Desktop Bridge apps are activated through AppsFolder.
            // Falling back to the raw executable path is still useful for unpackaged/debug runs.
            candidates.Add(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $@"shell:AppsFolder\{context.WindowsPackageFamilyName}!{WindowsStoreApplicationId}",
                UseShellExecute = true
            });
        }

        if (string.IsNullOrWhiteSpace(context.ProcessPath))
            return candidates;

        if (IsDotnetHost(context.ProcessPath))
        {
            if (string.IsNullOrWhiteSpace(context.EntryAssemblyPath))
                return candidates;

            // Development runs can execute under the dotnet host instead of an apphost executable.
            // Launching another independent process must replay the entry assembly explicitly there.
            var dotnetHostStartInfo = new ProcessStartInfo
            {
                FileName = context.ProcessPath,
                UseShellExecute = false,
                WorkingDirectory = context.WorkingDirectory
            };
            dotnetHostStartInfo.ArgumentList.Add(context.EntryAssemblyPath);
            candidates.Add(dotnetHostStartInfo);
            return candidates;
        }

        candidates.Add(new ProcessStartInfo
        {
            FileName = context.ProcessPath,
            UseShellExecute = false,
            WorkingDirectory = context.WorkingDirectory
        });

        return candidates;
    }

    private static bool IsDotnetHost(string processPath)
    {
        var separatorIndex = processPath.LastIndexOfAny(['\\', '/']);
        var fileName = separatorIndex >= 0
            ? processPath[(separatorIndex + 1)..]
            : processPath;

        return string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, "dotnet.exe", StringComparison.OrdinalIgnoreCase);
    }
}
