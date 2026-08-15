namespace DevProjex.Tests.Integration.Helpers;

internal sealed class GitTestRepository : IDisposable, IAsyncDisposable
{
    private static readonly string GitExecutable =
        OperatingSystem.IsWindows() ? "git.exe" : "git";

    private readonly TemporaryDirectory _tempDirectory;
    private readonly string _seedRepositoryPath;

    private GitTestRepository(
        TemporaryDirectory tempDirectory,
        string seedRepositoryPath,
        string bareRepositoryPath,
        string repositoryName)
    {
        _tempDirectory = tempDirectory;
        _seedRepositoryPath = seedRepositoryPath;
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
        var bareRepositoryPath = Path.Combine(tempDirectory.Path, $"{repositoryName}.git");
        var repository = new GitTestRepository(
            tempDirectory,
            seedRepositoryPath,
            bareRepositoryPath,
            repositoryName);

        await repository.InitializeAsync(includeLargePayload, cancellationToken);
        return repository;
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
        await RunGitAsync(_seedRepositoryPath, $"push origin \"{branchName}\"", cancellationToken);
    }

    public async Task<string> GetBranchHeadAsync(
        string branchName,
        CancellationToken cancellationToken = default) =>
        (await RunGitAsync(
            null,
            $"--git-dir=\"{BareRepositoryPath}\" rev-parse \"refs/heads/{branchName}\"",
            cancellationToken)).Trim();

    public void Dispose() => _tempDirectory.Dispose();

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
        await RunGitAsync(null, $"init --bare \"{BareRepositoryPath}\"", cancellationToken);
        await RunGitAsync(_seedRepositoryPath, $"remote add origin \"{BareRepositoryPath}\"", cancellationToken);
        await RunGitAsync(
            _seedRepositoryPath,
            $"push --force --set-upstream origin \"{DefaultBranchName}\"",
            cancellationToken);
        await RunGitAsync(_seedRepositoryPath, $"push --force origin \"{FeatureBranchName}\"", cancellationToken);
        await RunGitAsync(_seedRepositoryPath, $"push --force origin \"{ReleaseBranchName}\"", cancellationToken);
        await RunGitAsync(
            null,
            $"--git-dir=\"{BareRepositoryPath}\" symbolic-ref HEAD refs/heads/{DefaultBranchName}",
            cancellationToken);
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
