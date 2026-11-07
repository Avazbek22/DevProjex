using DevProjex.Application.Diff;

namespace DevProjex.Tests.Integration;

public sealed class GitFileVersionReaderIntegrationTests
{
	[Fact]
	public async Task ChangesReadsTheCommittedSideOnly()
	{
		using var repository = new GitModeRepository();
		var modified = repository.Write("src/app.txt", "one\ntwo\n");
		repository.Commit();
		repository.Write("src/app.txt", "one\nTWO\n");
		var added = repository.Write("src/new.txt", "fresh\n");

		var versions = await ReadAsync(repository, GitFilteringMode.Changes, null, modified, added);

		Assert.Equal("one\ntwo\n", Text(versions.Files[modified].Old));
		Assert.Null(versions.Files[modified].New);
		Assert.Null(versions.Files[added].Old);
	}

	[Fact]
	public async Task StagedReadsHeadAndIndexStageZero()
	{
		using var repository = new GitModeRepository();
		var file = repository.Write("app.txt", "committed\n");
		repository.Commit();
		repository.Write("app.txt", "staged\n");
		repository.Git("add", "app.txt");
		repository.Write("app.txt", "working\n");

		var versions = await ReadAsync(repository, GitFilteringMode.Staged, null, file);

		Assert.Equal("committed\n", Text(versions.Files[file].Old));
		Assert.Equal("staged\n", Text(versions.Files[file].New));
	}

	[Fact]
	public async Task RefToRefReadsBothCommits()
	{
		using var repository = new GitModeRepository();
		var file = repository.Write("app.txt", "first\n");
		repository.Commit();
		repository.Write("app.txt", "second\n");
		repository.Commit();
		repository.Write("app.txt", "working\n");

		var versions = await ReadAsync(repository, GitFilteringMode.Diff, "HEAD~1..HEAD", file);

		Assert.Equal("first\n", Text(versions.Files[file].Old));
		Assert.Equal("second\n", Text(versions.Files[file].New));
	}

	[Fact]
	public async Task AnUnbornBranchHasNoOldSide()
	{
		using var repository = new GitModeRepository();
		var file = repository.Write("app.txt", "first\n");
		repository.Git("add", "app.txt");

		var staged = await ReadAsync(repository, GitFilteringMode.Staged, null, file);
		var changes = await ReadAsync(repository, GitFilteringMode.Changes, null, file);

		Assert.True(staged.IsAvailable, staged.FailureReason);
		Assert.Null(staged.Files[file].Old);
		Assert.Equal("first\n", Text(staged.Files[file].New));
		Assert.True(changes.IsAvailable, changes.FailureReason);
		Assert.Null(changes.Files[file].Old);
	}

	[Fact]
	public async Task AnUnknownRevisionIsReportedAsSuch()
	{
		using var repository = new GitModeRepository();
		var file = repository.Write("app.txt", "first\n");
		repository.Commit();

		var versions = await ReadAsync(repository, GitFilteringMode.Diff, "HEAD..no-such-branch", file);

		Assert.Equal(GitFileVersionFailure.UnknownRevision, versions.Failure);
		Assert.Empty(versions.Files);
	}

	[Fact]
	public async Task ARenameUsesTheSourceOnlyWhenTheSourceWasRequested()
	{
		using var repository = new GitModeRepository();
		var source = repository.Write("old.txt", string.Concat(Enumerable.Range(0, 20).Select(index => $"line {index}\n")));
		repository.Commit();
		repository.Git("mv", "old.txt", "new.txt");
		var destination = Path.Combine(repository.Root, "new.txt");

		var withSource = await ReadAsync(repository, GitFilteringMode.Staged, null, destination, source);
		var withoutSource = await ReadAsync(repository, GitFilteringMode.Staged, null, destination);

		Assert.Equal(source, withSource.Files[destination].RenamedFrom);
		Assert.StartsWith("line 0\n", Text(withSource.Files[destination].Old), StringComparison.Ordinal);
		Assert.Null(withSource.Files[source].New);
		Assert.Null(withoutSource.Files[destination].RenamedFrom);
		Assert.Null(withoutSource.Files[destination].Old);
		Assert.False(withoutSource.Files.ContainsKey(source));
	}

	[Fact]
	public async Task StoredBytesAreReturnedUnconverted()
	{
		using var repository = new GitModeRepository();
		var file = repository.Write("crlf.txt", "a\r\nb\r\n");
		var dashed = repository.Write("-dash name.txt", "dash\n");
		repository.Commit();

		var versions = await ReadAsync(repository, GitFilteringMode.Changes, null, file, dashed);

		Assert.Equal("a\r\nb\r\n", Text(versions.Files[file].Old));
		Assert.Equal("dash\n", Text(versions.Files[dashed].Old));
	}

	[Fact]
	public async Task ANestedRepositoryUsesItsOwnHead()
	{
		using var repository = new GitModeRepository();
		var outer = repository.Write("outer.txt", "outer\n");
		repository.Write(".gitignore", "nested/\n");
		repository.Commit();
		var nestedRoot = Path.Combine(repository.Root, "nested");
		using var nested = new GitModeRepository(nestedRoot);
		var inner = nested.Write("inner.txt", "inner\n");
		nested.Commit();

		var versions = await ReadAsync(repository, GitFilteringMode.Changes, null, outer, inner);

		Assert.Equal("outer\n", Text(versions.Files[outer].Old));
		Assert.Equal("inner\n", Text(versions.Files[inner].Old));
	}

	[Fact]
	public async Task OnlyRequestedPathsAreReported()
	{
		using var repository = new GitModeRepository();
		var requested = repository.Write("a.txt", "a\n");
		repository.Write("b.txt", "b\n");
		repository.Commit();

		var versions = await ReadAsync(repository, GitFilteringMode.Changes, null, requested);

		Assert.Equal([requested], versions.Files.Keys);
	}

	[Fact]
	public async Task ABackslashOutsideWindowsIsPartOfTheNameAndIsNeverReadAsAnotherPath()
	{
		if (OperatingSystem.IsWindows())
			Assert.Skip("Windows separates directories with a backslash.");
		using var repository = new GitModeRepository();
		var nested = repository.Write("config/.env", "nested\n");
		var literal = repository.Write("config\\.env", "literal\n");
		repository.Commit();
		Assert.Contains("config\\.env", repository.Git("ls-files", "-z").Split('\0'));

		var both = await ReadAsync(repository, GitFilteringMode.Changes, null, nested, literal);
		var alone = await ReadAsync(repository, GitFilteringMode.Staged, null, literal);

		Assert.Equal("nested\n", Text(both.Files[nested].Old));
		Assert.True(both.Files[literal].Unsupported);
		Assert.Null(both.Files[literal].Old);
		Assert.True(alone.Files[literal].Unsupported);
		Assert.Null(alone.Files[literal].Old);
		Assert.Null(alone.Files[literal].New);
	}

	[Fact]
	public async Task ThePromisorQueryFindsAPartialCloneDeclaredOnlyByTheExtension()
	{
		using var repository = new GitModeRepository();
		repository.Write("app.txt", "one\n");
		repository.Commit();
		var plain = await GitRepositorySafetyInspector.RunAsync(
			repository.Root,
			GitProcessOperation.ReadConfigValue(GitConfigReadKind.PromisorRemotes),
			TestContext.Current.CancellationToken);
		repository.Git("config", "core.repositoryformatversion", "1");
		repository.Git("config", "extensions.partialClone", "origin");

		var partial = await GitRepositorySafetyInspector.RunAsync(
			repository.Root,
			GitProcessOperation.ReadConfigValue(GitConfigReadKind.PromisorRemotes),
			TestContext.Current.CancellationToken);

		Assert.False(GitRepositorySafetyInspector.InterpretPromisorResult(plain));
		Assert.NotNull(partial);
		Assert.Equal(0, partial.ExitCode);
		Assert.Equal("extensions.partialclone", partial.Output.Trim());
		Assert.True(GitRepositorySafetyInspector.InterpretPromisorResult(partial));
	}

	private static async Task<GitFileVersionSet> ReadAsync(
		GitModeRepository repository,
		GitFilteringMode mode,
		string? range,
		params string[] files)
	{
		var versions = await new GitFileVersionReader().ReadAsync(
			new GitFileVersionRequest(repository.Root, mode, range, files),
			TestContext.Current.CancellationToken);
		if (versions.Failure != GitFileVersionFailure.UnknownRevision)
			Assert.True(versions.IsAvailable, versions.FailureReason);
		return versions;
	}

	private static string Text(GitStoredFile? file)
	{
		Assert.NotNull(file);
		Assert.Equal(GitStoredFileKind.Regular, file.Kind);
		return Encoding.UTF8.GetString(Assert.IsType<byte[]>(file.Content));
	}
}
