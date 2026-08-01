using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace DevProjex.Tests.Unit;

public sealed class GitRepositoryServiceCancellationTests
{
    [Fact]
    public async Task IsGitAvailableAsync_PreCanceledToken_ThrowsWhenGitCliIsAvailable()
    {
        var service = new GitRepositoryService();

        if (!await service.IsGitAvailableAsync(cancellationToken: TestContext.Current.CancellationToken))
            return;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await service.IsGitAvailableAsync(cts.Token));
    }

    [Fact]
    public async Task WaitForExitOrTerminateAsync_CancellationWaitsForProcessTreeAndReleasesFileHandle()
    {
        using var temp = new TemporaryDirectory();
        var lockPath = Path.Combine(temp.Path, "git-process.lock");
        var readyPath = Path.Combine(temp.Path, "git-process.ready");
        var childProcessIdPath = GitCancellationProcessHost.GetChildProcessIdPath(readyPath);
        int? childProcessId = null;
        var childReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var readyWatcher = new FileSystemWatcher(
            temp.Path,
            Path.GetFileName(readyPath))
        {
            EnableRaisingEvents = true
        };
        readyWatcher.Created += (_, _) => childReady.TrySetResult();
        readyWatcher.Renamed += (_, _) => childReady.TrySetResult();
        using var process = new Process
        {
            StartInfo = GitCancellationProcessHost.CreateStartInfo(
                GitCancellationProcessHost.ParentRole,
                lockPath,
                readyPath,
                redirectOutput: true)
        };
        Assert.True(process.Start());
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (File.Exists(readyPath))
            childReady.TrySetResult();

        try
        {
            await childReady.Task
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            childProcessId = int.Parse(
                await File.ReadAllTextAsync(readyPath, TestContext.Current.CancellationToken),
                NumberStyles.None,
                CultureInfo.InvariantCulture);
            Assert.Throws<IOException>(() =>
            {
                using var _ = new FileStream(
                    lockPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None);
            });

            using var cancellation = new CancellationTokenSource();
            var wait = GitRepositoryService.WaitForExitOrTerminateAsync(
                process,
                cancellation.Token);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await wait);

            Assert.True(process.HasExited);
            // The child holds this handle for its entire executable lifetime. Acquiring it
            // exclusively proves that no descendant can still run or retain the resource;
            // unlike PID disappearance, this remains valid while Linux reaps a zombie.
            using var releasedHandle = new FileStream(
                lockPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        finally
        {
            await TerminateTestProcessAsync(process, entireProcessTree: true);

            childProcessId ??= TryReadProcessId(childProcessIdPath);
            if (childProcessId is { } processId)
                await TerminateTestProcessAsync(processId, entireProcessTree: false);
        }
    }

    private static int? TryReadProcessId(string path)
    {
        if (!File.Exists(path))
            return null;

        return int.TryParse(
            File.ReadAllText(path),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var processId)
            ? processId
            : null;
    }

    private static async Task TerminateTestProcessAsync(int processId, bool entireProcessTree)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            await TerminateTestProcessAsync(process, entireProcessTree);
        }
        catch (ArgumentException)
        {
            // The helper already exited.
        }
    }

    private static async Task TerminateTestProcessAsync(
        Process process,
        bool entireProcessTree)
    {
        if (process.HasExited)
            return;

        process.Kill(entireProcessTree);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(timeout.Token);
    }

}

internal static class GitCancellationProcessHost
{
    internal const string LockPathEnvironmentVariable = "DEVPROJEX_TEST_GIT_LOCK_PATH";
    internal const string ReadyPathEnvironmentVariable = "DEVPROJEX_TEST_GIT_READY_PATH";
    internal const string RoleEnvironmentVariable = "DEVPROJEX_TEST_GIT_PROCESS_ROLE";
    internal const string ParentRole = "parent";
    private const string ChildRole = "child";

    [ModuleInitializer]
    internal static void Run()
    {
        var lockPath = Environment.GetEnvironmentVariable(LockPathEnvironmentVariable);
        var readyPath = Environment.GetEnvironmentVariable(ReadyPathEnvironmentVariable);
        var role = Environment.GetEnvironmentVariable(RoleEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(lockPath) ||
            string.IsNullOrWhiteSpace(readyPath) ||
            string.IsNullOrWhiteSpace(role))
        {
            return;
        }

        if (role == ParentRole)
        {
            using var child = new Process
            {
                StartInfo = CreateStartInfo(ChildRole, lockPath, readyPath, redirectOutput: false)
            };
            if (!child.Start())
                throw new InvalidOperationException("The Git cancellation child process did not start.");

            WriteProcessIdAtomically(GetChildProcessIdPath(readyPath), child.Id);
            child.WaitForExit();
            return;
        }

        if (role != ChildRole)
            return;

        using var heldHandle = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        WriteProcessIdAtomically(readyPath, Environment.ProcessId);

        using var exitSignal = new ManualResetEventSlim(initialState: false);
        exitSignal.Wait();
    }

    internal static ProcessStartInfo CreateStartInfo(
        string role,
        string lockPath,
        string readyPath,
        bool redirectOutput)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet",
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(typeof(GitCancellationProcessHost).Assembly.Location);
        startInfo.Environment[LockPathEnvironmentVariable] = lockPath;
        startInfo.Environment[ReadyPathEnvironmentVariable] = readyPath;
        startInfo.Environment[RoleEnvironmentVariable] = role;
        return startInfo;
    }

    internal static string GetChildProcessIdPath(string readyPath) =>
        readyPath + ".child.pid";

    private static void WriteProcessIdAtomically(string path, int processId)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            processId.ToString(CultureInfo.InvariantCulture));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
