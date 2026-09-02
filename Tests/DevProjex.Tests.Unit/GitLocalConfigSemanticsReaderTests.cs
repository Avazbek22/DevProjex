namespace DevProjex.Tests.Unit;

public sealed class GitLocalConfigSemanticsReaderTests
{
	[Fact]
	public void ExplicitLocalValuesResolveWithoutNativeGitFallback()
	{
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateFolder("repository");
		var gitMetadataPath = CreateStandardMetadata(temp, "repository/.git");
		temp.CreateFile(
			"repository/.git/config",
			"""
			[core]
				repositoryformatversion = 0
				ignorecase = yes
				precomposeunicode = on
			""");

		var resolved = GitLocalConfigSemanticsReader.TryRead(
			repositoryRoot,
			gitMetadataPath,
			out var semantics);

		Assert.True(resolved);
		Assert.True(semantics.IsAuthoritative);
		Assert.True(semantics.IgnoreCase);
		Assert.Equal(OperatingSystem.IsMacOS(), semantics.NormalizeUnicode);
	}

	[Fact]
	public void IncludeDirectiveRequiresNativeGitFallback()
	{
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateFolder("repository");
		var gitMetadataPath = CreateStandardMetadata(temp, "repository/.git");
		temp.CreateFile(
			"repository/.git/config",
			"""
			[core]
				repositoryformatversion = 0
				ignorecase = true
			[include]
				path = ../shared.config
			""");

		Assert.False(GitLocalConfigSemanticsReader.TryRead(
			repositoryRoot,
			gitMetadataPath,
			out _));
	}

	[Fact]
	public void WorktreeConfigOverridesCommonRepositoryValues()
	{
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateFolder("repository");
		var gitMetadataPath = CreateStandardMetadata(temp, "repository/.git");
		temp.CreateFile(
			"repository/.git/config",
			"""
			[core]
				repositoryformatversion = 0
				ignorecase = false
				precomposeunicode = true
			[extensions]
				worktreeconfig = true
			""");
		temp.CreateFile(
			"repository/.git/config.worktree",
			"""
			[core]
				ignorecase = true
				precomposeunicode = false
			""");

		var resolved = GitLocalConfigSemanticsReader.TryRead(
			repositoryRoot,
			gitMetadataPath,
			out var semantics);

		Assert.True(resolved);
		Assert.True(semantics.IgnoreCase);
		Assert.False(semantics.NormalizeUnicode);
	}

	[Fact]
	public void LinkedWorktreeUsesCommonConfigAndWorktreeOverride()
	{
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateFolder("linked workspace");
		var commonGitDirectory = temp.CreateFolder("main repository/.git");
		var worktreeGitDirectory = temp.CreateFolder("main repository/.git/worktrees/feature");
		temp.CreateFile("main repository/.git/worktrees/feature/HEAD", "ref: refs/heads/feature\n");
		temp.CreateFolder("main repository/.git/objects");
		temp.CreateFolder("main repository/.git/refs");
		var gitMetadataPath = temp.CreateFile(
			"linked workspace/.git",
			$"gitdir: {worktreeGitDirectory}\n");
		temp.CreateFile("main repository/.git/worktrees/feature/commondir", "../..\n");
		temp.CreateFile(
			"main repository/.git/config",
			"""
			[core]
				repositoryformatversion = 0
				ignorecase = false
				precomposeunicode = true
			[extensions]
				worktreeconfig = true
			""");
		temp.CreateFile(
			"main repository/.git/worktrees/feature/config.worktree",
			"""
			[core]
				ignorecase = true
				precomposeunicode = false
			""");

		var resolved = GitLocalConfigSemanticsReader.TryRead(
			repositoryRoot,
			gitMetadataPath,
			out var semantics);

		Assert.True(resolved);
		Assert.True(semantics.IgnoreCase);
		Assert.False(semantics.NormalizeUnicode);
		Assert.True(Directory.Exists(commonGitDirectory));
	}

	[Fact]
	public void MissingExplicitCasePolicyRequiresNativeGitFallback()
	{
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateFolder("repository");
		var gitMetadataPath = CreateStandardMetadata(temp, "repository/.git");
		temp.CreateFile(
			"repository/.git/config",
			"""
			[core]
				repositoryformatversion = 0
			""");

		Assert.False(GitLocalConfigSemanticsReader.TryRead(
			repositoryRoot,
			gitMetadataPath,
			out _));
	}

	[Fact]
	public void MalformedUnrelatedConfigRequiresNativeGitFallback()
	{
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateFolder("repository");
		var gitMetadataPath = CreateStandardMetadata(temp, "repository/.git");
		temp.CreateFile(
			"repository/.git/config",
			"""
			[core]
				repositoryformatversion = 0
				ignorecase = true
			[remote "unterminated]
				url = https://example.test/repository.git
			""");

		Assert.False(GitLocalConfigSemanticsReader.TryRead(
			repositoryRoot,
			gitMetadataPath,
			out _));
	}

	[Fact]
	public void ConfigWithoutRepositoryStructureRequiresNativeGitFallback()
	{
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateFolder("repository");
		var gitMetadataPath = temp.CreateFolder("repository/.git");
		temp.CreateFile(
			"repository/.git/config",
			"""
			[core]
				repositoryformatversion = 0
				ignorecase = true
			""");

		Assert.False(GitLocalConfigSemanticsReader.TryRead(
			repositoryRoot,
			gitMetadataPath,
			out _));
	}

	[Fact]
	public void BoundedTextRead_ExactLimitSucceedsAndOverflowStopsAfterProbeCharacter()
	{
		using var exactReader = new StaleLengthMemoryStream(
			Encoding.UTF8.GetBytes("0123456789"),
			reportedLength: 10);
		using var oversizedReader = new StaleLengthMemoryStream(
			Encoding.UTF8.GetBytes(new string('\u00E9', 10_000)),
			reportedLength: 10);

		var exact = GitLocalConfigSemanticsReader.TryReadBoundedText(
			exactReader,
			maximumBytes: 10,
			out var text);
		var oversized = GitLocalConfigSemanticsReader.TryReadBoundedText(
			oversizedReader,
			maximumBytes: 10,
			out _);

		Assert.True(exact);
		Assert.Equal("0123456789", text);
		Assert.False(oversized);
		Assert.Equal(11, oversizedReader.Position);
	}

	private static string CreateStandardMetadata(TemporaryDirectory temp, string relativeGitPath)
	{
		var gitMetadataPath = temp.CreateFolder(relativeGitPath);
		temp.CreateFile(Path.Combine(relativeGitPath, "HEAD"), "ref: refs/heads/main\n");
		temp.CreateFolder(Path.Combine(relativeGitPath, "objects"));
		temp.CreateFolder(Path.Combine(relativeGitPath, "refs"));
		return gitMetadataPath;
	}

	private sealed class StaleLengthMemoryStream(byte[] buffer, long reportedLength) :
		MemoryStream(buffer, writable: false)
	{
		public override long Length => reportedLength;
	}
}
