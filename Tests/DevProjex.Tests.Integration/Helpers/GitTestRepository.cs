namespace DevProjex.Tests.Integration.Helpers;

internal sealed class GitTestRepository : IDisposable, IAsyncDisposable
{
    private static readonly string GitExecutable =
        OperatingSystem.IsWindows() ? "git.exe" : "git";

    private readonly TemporaryDirectory _tempDirectory;
    private readonly string _seedRepositoryPath;
    private readonly string _bareRepositoryRootPath;

    private GitTestRepository(
        TemporaryDirectory tempDirectory,
        string seedRepositoryPath,
        string bareRepositoryRootPath,
        string bareRepositoryPath,
        string repositoryName)
    {
        _tempDirectory = tempDirectory;
        _seedRepositoryPath = seedRepositoryPath;
        _bareRepositoryRootPath = bareRepositoryRootPath;
        BareRepositoryPath = bareRepositoryPath;
        RepositoryName = repositoryName;
        RepositoryUrl = new Uri(BareRepositoryPath).AbsoluteUri;
    }

    public string BareRepositoryPath { get; }

    public string RepositoryName { get; }

    public string RepositoryUrl { get; }

    public string DefaultBranchName => "master";

    public string FeatureBranchName => "feature/demo";

    public string ReleaseBranchName => "release/v1";

    public static async Task<GitTestRepository> CreateAsync(
        string repositoryName = "Hello-World",
        bool includeLargePayload = false,
        CancellationToken cancellationToken = default)
    {
        var tempDirectory = new TemporaryDirectory();
        var seedRepositoryPath = tempDirectory.CreateDirectory("seed");
        var bareRepositoryRootPath = Path.Combine(
            Path.GetTempPath(),
            "DevProjex",
            "GitTestRepositories",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bareRepositoryRootPath);
        var bareRepositoryPath = Path.Combine(bareRepositoryRootPath, $"{repositoryName}.git");
        var repository = new GitTestRepository(
            tempDirectory,
            seedRepositoryPath,
            bareRepositoryRootPath,
            bareRepositoryPath,
            repositoryName);

        try
        {
            await repository.InitializeAsync(includeLargePayload, cancellationToken);
            return repository;
        }
        catch
        {
            repository.Dispose();
            throw;
        }
    }

    public async Task AddCommitToBranchAsync(
        string branchName,
        string relativePath,
        string content,
        string commitMessage,
        CancellationToken cancellationToken = default)
    {
        await RunGitAsync(_seedRepositoryPath, $"checkout \"{branchName}\"", cancellationToken);

        var fullPath = Path.Combine(_seedRepositoryPath, relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(fullPath, content, cancellationToken);
        await RunGitAsync(_seedRepositoryPath, $"add \"{relativePath}\"", cancellationToken);
        await RunGitAsync(_seedRepositoryPath, $"commit -m \"{commitMessage}\"", cancellationToken);
        await PushBranchAsync(branchName, force: false, setUpstream: false, cancellationToken);
    }

    public async Task<string> GetBranchHeadAsync(
        string branchName,
        CancellationToken cancellationToken = default) =>
        (await RunGitAsync(
            null,
            $"--git-dir=\"{BareRepositoryPath}\" rev-parse --verify \"refs/heads/{branchName}\"",
            cancellationToken)).Trim();

    public void Dispose()
    {
        DeleteDirectoryBestEffort(_bareRepositoryRootPath);
        _tempDirectory.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task InitializeAsync(bool includeLargePayload, CancellationToken cancellationToken)
    {
        await RunGitAsync(_seedRepositoryPath, "init", cancellationToken);
        await RunGitAsync(_seedRepositoryPath, "config user.email \"tests@devprojex.local\"", cancellationToken);
        await RunGitAsync(_seedRepositoryPath, "config user.name \"DevProjex Tests\"", cancellationToken);

        await CreateInitialContentAsync(includeLargePayload, cancellationToken);
        await CommitSeedAsync("Initial commit", cancellationToken);
        await RunGitAsync(_seedRepositoryPath, $"branch -M \"{DefaultBranchName}\"", cancellationToken);

        await RunGitAsync(_seedRepositoryPath, $"checkout -b \"{FeatureBranchName}\"", cancellationToken);
        await AddBranchSpecificContentAsync(
            "feature",
            "feature.txt",
            "Feature branch payload",
            cancellationToken);
        await CommitSeedAsync("Feature commit", cancellationToken);

        await RunGitAsync(
            _seedRepositoryPath,
            $"checkout -b \"{ReleaseBranchName}\" \"{DefaultBranchName}\"",
            cancellationToken);
        await AddBranchSpecificContentAsync(
            "release",
            "release-notes.txt",
            "Release branch payload",
            cancellationToken);
        await CommitSeedAsync("Release commit", cancellationToken);

        await RunGitAsync(_seedRepositoryPath, $"checkout \"{DefaultBranchName}\"", cancellationToken);
        try
        {
            await RunGitAsync(null, $"init --bare \"{BareRepositoryPath}\"", cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"Git test bare repository initialization failed. {DescribeBareRepositoryState()}",
                exception);
        }

        EnsureBareRepositoryStructure("bare init");
        await RunGitAsync(_seedRepositoryPath, $"remote add origin \"{BareRepositoryPath}\"", cancellationToken);
        await PushBranchAsync(DefaultBranchName, force: true, setUpstream: true, cancellationToken);
        await PushBranchAsync(FeatureBranchName, force: true, setUpstream: false, cancellationToken);
        await PushBranchAsync(ReleaseBranchName, force: true, setUpstream: false, cancellationToken);
        await RunGitAsync(
            null,
            $"--git-dir=\"{BareRepositoryPath}\" symbolic-ref HEAD refs/heads/{DefaultBranchName}",
            cancellationToken);
    }

    private async Task PushBranchAsync(
        string branchName,
        bool force,
        bool setUpstream,
        CancellationToken cancellationToken)
    {
        EnsureBareRepositoryStructure($"before push of '{branchName}'");
        var options = force ? " --force" : string.Empty;
        if (setUpstream)
            options += " --set-upstream";

        try
        {
            await RunGitAsync(
                _seedRepositoryPath,
                $"push{options} origin \"{branchName}\"",
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"Git test repository push failed for '{branchName}'. {DescribeBareRepositoryState()}",
                exception);
        }

        EnsureBareRepositoryStructure($"after push of '{branchName}'");
        var localHead = (await RunGitAsync(
            _seedRepositoryPath,
            $"rev-parse --verify \"refs/heads/{branchName}\"",
            cancellationToken)).Trim();
        string bareHead;
        try
        {
            bareHead = await GetBranchHeadAsync(branchName, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"Git test repository ref verification failed for '{branchName}'. {DescribeBareRepositoryState()}",
                exception);
        }

        if (!string.Equals(localHead, bareHead, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Git test repository ref mismatch for '{branchName}': local={localHead}, bare={bareHead}. " +
                DescribeBareRepositoryState());
        }
    }

    private void EnsureBareRepositoryStructure(string phase)
    {
        if (Directory.Exists(BareRepositoryPath) &&
            Directory.Exists(Path.Combine(BareRepositoryPath, "objects")) &&
            Directory.Exists(Path.Combine(BareRepositoryPath, "refs", "heads")))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Git test bare repository disappeared during {phase}. {DescribeBareRepositoryState()}");
    }

    private string DescribeBareRepositoryState() =>
        $"root='{_bareRepositoryRootPath}' exists={Directory.Exists(_bareRepositoryRootPath)}; " +
        $"bare='{BareRepositoryPath}' exists={Directory.Exists(BareRepositoryPath)}; " +
        $"objects exists={Directory.Exists(Path.Combine(BareRepositoryPath, "objects"))}; " +
        $"refs/heads exists={Directory.Exists(Path.Combine(BareRepositoryPath, "refs", "heads"))}.";

    private static void DeleteDirectoryBestEffort(string path)
    {
        // Git may release fixture files shortly after the test process observes completion.
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }

    private async Task CreateInitialContentAsync(bool includeLargePayload, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.Combine(_seedRepositoryPath, "src"));
        Directory.CreateDirectory(Path.Combine(_seedRepositoryPath, "docs"));

        await File.WriteAllTextAsync(
            Path.Combine(_seedRepositoryPath, "README.md"),
            "# Hello-World",
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(_seedRepositoryPath, "src", "app.txt"),
            "master branch payload",
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(_seedRepositoryPath, "docs", "guide.md"),
            "guide",
            cancellationToken);

        if (!includeLargePayload)
            return;

        Directory.CreateDirectory(Path.Combine(_seedRepositoryPath, "artifacts"));

        // Use a moderately large payload to make clone cancellation tests observe an active process
        // without turning the whole suite into a slow disk benchmark.
        var payloadPath = Path.Combine(_seedRepositoryPath, "artifacts", "payload.bin");
        await using var stream = File.Create(payloadPath);
        var buffer = new byte[1024 * 1024];
        new Random(42).NextBytes(buffer);

        for (var i = 0; i < 12; i++)
            await stream.WriteAsync(buffer, cancellationToken);
    }

    private async Task AddBranchSpecificContentAsync(
        string directoryName,
        string fileName,
        string content,
        CancellationToken cancellationToken)
    {
        var directoryPath = Path.Combine(_seedRepositoryPath, directoryName);
        Directory.CreateDirectory(directoryPath);
        await File.WriteAllTextAsync(Path.Combine(directoryPath, fileName), content, cancellationToken);
    }

    private async Task CommitSeedAsync(string message, CancellationToken cancellationToken)
    {
        await RunGitAsync(_seedRepositoryPath, "add .", cancellationToken);
        await RunGitAsync(_seedRepositoryPath, $"commit -m \"{message}\"", cancellationToken);
    }

    private static async Task<string> RunGitAsync(
        string? workingDirectory,
        string arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = GitExecutable,
                Arguments = arguments,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode == 0)
            return output;

        throw new InvalidOperationException(
            $"Git command failed: git {arguments}{Environment.NewLine}{output}{Environment.NewLine}{error}");
    }
}
