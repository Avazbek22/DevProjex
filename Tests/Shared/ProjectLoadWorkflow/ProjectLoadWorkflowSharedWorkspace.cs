namespace DevProjex.Tests.Shared.ProjectLoadWorkflow;

public static class ProjectLoadWorkflowSharedWorkspace
{
    private static readonly object SyncRoot = new();
    private static string? _rootPath;

    static ProjectLoadWorkflowSharedWorkspace()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
    }

    public static string RootPath
    {
        get
        {
            lock (SyncRoot)
            {
                _rootPath ??= CreateWorkspace();
                return _rootPath;
            }
        }
    }

    private static string CreateWorkspace()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "DevProjex",
            "ProjectLoadWorkflow.Shared",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(rootPath);
        ProjectLoadWorkflowWorkspaceSeeder.Seed(rootPath);
        return rootPath;
    }

    private static void Cleanup()
    {
        lock (SyncRoot)
        {
            if (string.IsNullOrWhiteSpace(_rootPath) || !Directory.Exists(_rootPath))
                return;

            try
            {
                Directory.Delete(_rootPath, recursive: true);
            }
            catch
            {
                // Best effort cleanup only. Test runners can keep file handles alive
                // for a short time while shutting down the process.
            }
        }
    }
}
