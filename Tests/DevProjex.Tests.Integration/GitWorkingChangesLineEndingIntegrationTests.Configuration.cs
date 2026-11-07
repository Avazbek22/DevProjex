namespace DevProjex.Tests.Integration;

public sealed partial class GitWorkingChangesLineEndingIntegrationTests
{
	[Theory]
	[InlineData("true", "lf")]
	[InlineData("true", "crlf")]
	[InlineData("true", "native")]
	[InlineData("input", "lf")]
	[InlineData("input", "crlf")]
	[InlineData("input", "native")]
	[InlineData("false", "lf")]
	[InlineData("false", "crlf")]
	[InlineData("false", "native")]
	public async Task ChangesScopeMatchesGitAfterCheckoutStatDataBecomesStale(string autocrlf, string eol)
	{
		using var repository = new GitModeRepository();
		repository.Git("config", "core.autocrlf", autocrlf);
		repository.Git("config", "core.eol", eol);
		repository.Write("plain.txt", "plain\nunchanged\n");
		repository.Write("edited.txt", "before\n");
		repository.Write("mixed.txt", "one\r\ntwo\nthree\r\n");
		repository.Write(".gitattributes", "auto.txt text=auto\ncrlf.txt text eol=crlf\nraw.txt -text\nbinary.txt binary\n");
		repository.Write("auto.txt", "auto\n");
		repository.Write("crlf.txt", "crlf\n");
		repository.Write("raw.txt", "raw\r\n");
		repository.Write("binary.txt", "binary\r\n");
		repository.Commit();
		foreach (var path in Directory.EnumerateFiles(repository.Root).Where(static path => !path.EndsWith(".gitattributes", StringComparison.Ordinal)))
			File.Delete(path);
		repository.Git("checkout", "--", ".");
		Assert.Contains("\r\n", File.ReadAllText(Path.Combine(repository.Root, "crlf.txt")), StringComparison.Ordinal);
		Assert.Equal("raw\r\n", File.ReadAllText(Path.Combine(repository.Root, "raw.txt")));
		foreach (var path in Directory.EnumerateFiles(repository.Root))
			File.SetLastWriteTimeUtc(path, new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
		repository.Write("edited.txt", "after\n");
		var expected = repository.Git("diff", "--name-only", "-z", "--").Split('\0', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToArray();
		Assert.Equal(["edited.txt"], expected);
		repository.Git("config", "--unset", "core.autocrlf");
		repository.Git("config", "--unset", "core.eol");
		File.WriteAllText(repository.UserConfiguration, $"[core]\n autocrlf = {autocrlf}\n eol = {eol}\n");
		var result = await CreateIsolatedChangesProvider(repository).ResolveAsync(repository.Root, GitFilteringMode.Changes, null, TestContext.Current.CancellationToken);
		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.Equal(expected, RelativePaths(repository.Root, result.IncludedPaths));
	}

	[Theory]
	[InlineData("true\\n")]
	[InlineData("input -c core.fsmonitor=hostile")]
	[InlineData("true\\ncore.eol crlf")]
	public async Task LineEndingSettingsRejectMultilineAndOptionLikeValues(string value)
	{
		using var repository = new GitModeRepository();
		repository.Git("config", "--unset", "core.autocrlf");
		repository.Git("config", "--unset", "core.eol");
		File.WriteAllText(repository.UserConfiguration, $"[core]\n autocrlf = \"{value}\"\n");
		var nativeValue = repository.Git("config", "--file", repository.UserConfiguration, "--get", "core.autocrlf");
		Assert.Equal(value.Replace("\\n", "\n", StringComparison.Ordinal) + "\n", nativeValue.Replace("\r\n", "\n", StringComparison.Ordinal));
		var actual = await GitLineEndingSettings.ReadAsync(repository.Root, [repository.UserConfiguration], false, TestContext.Current.CancellationToken);
		Assert.Empty(actual);
	}

	[Fact]
	public async Task LineEndingSettingsIgnoreUserIncludesAndHonorWorktreeOverrides()
	{
		using var repository = new GitModeRepository();
		repository.Write("file.txt", "value\n");
		repository.Commit();
		var included = repository.Write("included.config", "[core]\n autocrlf = input\n eol = crlf\n");
		File.WriteAllText(repository.UserConfiguration, $"[include]\n path = {included.Replace('\\', '/')}\n[includeIf \"gitdir:**\"]\n path = {included.Replace('\\', '/')}\n");
		repository.Git("config", "--unset", "core.autocrlf");
		repository.Git("config", "--unset", "core.eol");
		Assert.Empty(await GitLineEndingSettings.ReadAsync(repository.Root, [repository.UserConfiguration], false, TestContext.Current.CancellationToken));
		repository.Git("config", "extensions.worktreeConfig", "true");
		repository.Git("config", "--worktree", "core.autocrlf", "input");
		repository.Git("config", "--worktree", "core.eol", "lf");
		Assert.Equal(["core.autocrlf=input", "core.eol=lf"], await GitLineEndingSettings.ReadAsync(repository.Root, [repository.UserConfiguration], false, TestContext.Current.CancellationToken));
	}

	private static GitScopePathProvider CreateIsolatedChangesProvider(GitModeRepository repository) => new(
		null,
		(root, token) => GitLineEndingSettings.ReadAsync(root, [repository.UserConfiguration], false, token));

	[Fact]
	public async Task ChangesScopeRechecksSameSizeEditsWhenTheIndexTimestampIsRacy()
	{
		using var repository = new GitModeRepository();
		var path = repository.Write("racy.txt", "before\n");
		var timestamp = new DateTime(2035, 1, 2, 3, 4, 5, DateTimeKind.Utc);
		File.SetLastWriteTimeUtc(path, timestamp);
		repository.Commit();
		File.SetLastWriteTimeUtc(Path.Combine(repository.Root, ".git", "index"), timestamp);
		repository.Write("racy.txt", "after!\n");
		File.SetLastWriteTimeUtc(path, timestamp);
		Assert.Equal("racy.txt", repository.Git("diff", "--name-only").Trim());
		var result = await CreateIsolatedChangesProvider(repository).ResolveAsync(repository.Root, GitFilteringMode.Changes, null, TestContext.Current.CancellationToken);
		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.Equal("racy.txt", Path.GetFileName(Assert.Single(result.IncludedPaths)));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ChangesScopeRetainsLineEndingsAfterRepositoryCopyOrArchiveRestore(bool archive)
	{
		using var repository = new GitModeRepository();
		repository.Git("config", "core.autocrlf", "true");
		repository.Write("unchanged.txt", "unchanged\r\n");
		repository.Write("changed.txt", "before\r\n");
		repository.Commit();
		repository.Git("config", "--unset", "core.autocrlf");
		File.WriteAllText(repository.UserConfiguration, "[core]\n autocrlf = true\n");
		var destination = Path.Combine(Path.GetDirectoryName(repository.Root)!, "restored");
		if (archive)
		{
			var zip = Path.Combine(Path.GetDirectoryName(repository.Root)!, "repository.zip");
			System.IO.Compression.ZipFile.CreateFromDirectory(repository.Root, zip);
			System.IO.Compression.ZipFile.ExtractToDirectory(zip, destination);
		}
		else
		{
			foreach (var file in Directory.EnumerateFiles(repository.Root, "*", SearchOption.AllDirectories))
			{
				var target = Path.Combine(destination, Path.GetRelativePath(repository.Root, file));
				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				File.Copy(file, target);
			}
		}
		foreach (var file in Directory.EnumerateFiles(destination))
			File.SetLastWriteTimeUtc(file, new DateTime(2002, 1, 2, 3, 4, 5, DateTimeKind.Utc));
		File.AppendAllText(Path.Combine(destination, "changed.txt"), "edited\r\n");
		var result = await CreateIsolatedChangesProvider(repository).ResolveAsync(destination, GitFilteringMode.Changes, null, TestContext.Current.CancellationToken);
		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.Equal("changed.txt", Path.GetFileName(Assert.Single(result.IncludedPaths)));
	}

	private static string[] RelativePaths(string root, IEnumerable<string> paths) => paths
		.Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();

	[Fact]
	public async Task ChangesScopeDoesNotImportTheUsersFileModeSettingAlongWithLineEndings()
	{
		using var repository = new GitModeRepository();
		repository.Write("script.sh", "echo fixture\n");
		repository.Git("add", "script.sh");
		repository.Git("update-index", "--chmod=+x", "script.sh");
		repository.Git("commit", "--quiet", "-m", "executable index");
		repository.Git("config", "--unset", "core.fileMode");
		File.WriteAllText(repository.UserConfiguration, "[core]\n fileMode = false\n autocrlf = false\n");
		Assert.Empty(repository.Git("diff", "--name-only"));
		Assert.Equal("script.sh", repository.Git("-c", "core.fileMode=true", "diff", "--name-only").Trim());
		var settings = await GitLineEndingSettings.ReadAsync(repository.Root, [repository.UserConfiguration], false, TestContext.Current.CancellationToken);
		Assert.DoesNotContain(settings, static value => value.StartsWith("core.fileMode", StringComparison.OrdinalIgnoreCase));
		var result = await CreateIsolatedChangesProvider(repository).ResolveAsync(repository.Root, GitFilteringMode.Changes, null, TestContext.Current.CancellationToken);
		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.Equal("script.sh", Path.GetFileName(Assert.Single(result.IncludedPaths)));
	}

	[Theory]
	[InlineData("2")]
	[InlineData("-1")]
	[InlineData("+2")]
	[InlineData("01")]
	[InlineData("0x1")]
	[InlineData("-0x1")]
	[InlineData("1K")]
	[InlineData("-2m")]
	[InlineData("1g")]
	[InlineData("0k")]
	[InlineData("0x0")]
	[InlineData("2147483647")]
	public async Task LineEndingSettingsHonorGitIntegerBooleanSpellings(string value)
	{
		using var repository = new GitModeRepository();
		repository.Git("config", "--unset", "core.autocrlf");
		repository.Git("config", "--unset", "core.eol");
		File.WriteAllText(repository.UserConfiguration, $"[core]\n autocrlf = {value}\n");
		var native = repository.Git("config", "--file", repository.UserConfiguration, "--type=bool", "--get", "core.autocrlf").Trim();
		Assert.Contains(native, new[] { "true", "false" });
		var result = await GitLineEndingSettings.ReadAsync(repository.Root, [repository.UserConfiguration], false, TestContext.Current.CancellationToken);
		Assert.Equal(["core.autocrlf=" + native], result);
	}

	[Theory]
	[InlineData("0")]
	[InlineData("1")]
	[InlineData("2")]
	[InlineData("-1")]
	[InlineData("0x1")]
	public async Task SystemConfigurationOptOutAgreesWithGitWithoutReadingTheMachinesSystemFile(string value)
	{
		using var repository = new GitModeRepository();
		using var temp = new TemporaryDirectory();
		var system = temp.CreateFile("system.config", "[core]\n autocrlf = true\n");
		var start = new ProcessStartInfo("git")
		{
			WorkingDirectory = temp.Path,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		repository.SetProcessEnvironment(start);
		start.Environment["GIT_CONFIG_SYSTEM"] = system;
		start.Environment["GIT_CONFIG_NOSYSTEM"] = value;
		foreach (var argument in new[] { "config", "--get", "core.autocrlf" })
			start.ArgumentList.Add(argument);
		using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Git.");
		process.StandardInput.Close();
		var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		try
		{
			await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
			Assert.Contains(process.ExitCode, new[] { 0, 1 });
			Assert.Empty(await error);
			Assert.Equal(process.ExitCode == 0, GitLineEndingSettings.ReadsSystemConfiguration(value));
			Assert.Equal(process.ExitCode == 0 ? "true" : "", (await output).Trim());
		}
		finally
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
		}
	}

	[Theory]
	[InlineData("trustctime", 12)]
	[InlineData("checkStat", 24)]
	public async Task IsolatedChangesMatchesNativeGitWithoutUserStatShortcuts(string setting, int statOffset)
	{
		using var repository = new GitModeRepository();
		var path = repository.Write("file.txt", "before\n");
		var timestamp = new DateTime(2001, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1_234_500);
		File.SetLastWriteTimeUtc(path, timestamp);
		repository.Commit();
		repository.Git("update-index", "--index-version=2");
		Assert.Equal(["file.txt"], repository.Git("ls-files", "-z").Split('\0', StringSplitOptions.RemoveEmptyEntries));
		var indexPath = Path.Combine(repository.Root, ".git", "index");
		var index = File.ReadAllBytes(indexPath);
		Assert.Equal("DIRC", Encoding.ASCII.GetString(index, 0, 4));
		Assert.Equal(2u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(4, 4)));
		Assert.Equal(1u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(8, 4)));
		Assert.NotEqual(0u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(statOffset, 4)));
		index.AsSpan(statOffset, 4).Clear();
		System.Security.Cryptography.SHA1.HashData(index.AsSpan(0, index.Length - 20), index.AsSpan(index.Length - 20));
		File.WriteAllBytes(indexPath, index);
		File.SetLastWriteTimeUtc(indexPath, new DateTime(2035, 1, 2, 3, 4, 5, DateTimeKind.Utc));
		repository.Write("file.txt", "after!\n");
		File.SetLastWriteTimeUtc(path, timestamp);
		File.WriteAllText(repository.UserConfiguration, setting == "trustctime"
			? "[core]\n trustctime = false\n"
			: "[core]\n trustctime = false\n checkStat = minimal\n");
		Assert.Empty(repository.Git("diff", "--name-only"));
		var isolated = repository.Git("-c", "core.trustctime=true", "-c", "core.checkStat=default", "diff", "--name-only", "-z")
			.Split('\0', StringSplitOptions.RemoveEmptyEntries);
		if (setting == "trustctime")
			Assert.Equal(["file.txt"], isolated);
		var result = await CreateIsolatedChangesProvider(repository).ResolveAsync(repository.Root, GitFilteringMode.Changes, null, TestContext.Current.CancellationToken);
		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.Equal(isolated, RelativePaths(repository.Root, result.IncludedPaths));
	}

	[Fact]
	public async Task ChangesScopeDoesNotImportUserSymlinkEmulationSetting()
	{
		using var repository = new GitModeRepository();
		repository.Write("link.txt", "target.txt");
		var blob = repository.Git("hash-object", "-w", "link.txt").Trim();
		repository.Git("update-index", "--add", "--cacheinfo", "120000", blob, "link.txt");
		repository.Git("config", "core.symlinks", "false");
		repository.Git("commit", "--quiet", "-m", "emulated link");
		repository.Git("config", "--unset", "core.symlinks");
		File.WriteAllText(repository.UserConfiguration, "[core]\n symlinks = false\n");
		Assert.Empty(repository.Git("diff", "--name-only"));
		Assert.Equal("link.txt", repository.Git("-c", "core.symlinks=true", "diff", "--name-only").Trim());
		File.WriteAllText(repository.UserConfiguration, string.Empty);
		var isolated = repository.Git("diff", "--name-only", "-z").Split('\0', StringSplitOptions.RemoveEmptyEntries);
		File.WriteAllText(repository.UserConfiguration, "[core]\n symlinks = false\n");
		var result = await CreateIsolatedChangesProvider(repository).ResolveAsync(repository.Root, GitFilteringMode.Changes, null, TestContext.Current.CancellationToken);
		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.Equal(isolated, RelativePaths(repository.Root, result.IncludedPaths));
	}

	[Fact]
	public async Task UserStatusAndRenamePresentationSettingsCannotSuppressUntrackedOrRenamedPaths()
	{
		using var repository = new GitModeRepository();
		repository.Write("before.txt", "same content\n");
		repository.Commit();
		repository.Git("mv", "before.txt", "after.txt");
		repository.Write("untracked.txt", "untracked\n");
		File.WriteAllText(repository.UserConfiguration, "[status]\n showUntrackedFiles = no\n[diff]\n renames = false\n");
		Assert.DoesNotContain("untracked.txt", repository.Git("status", "--short"), StringComparison.Ordinal);
		Assert.Contains("untracked.txt", repository.Git("ls-files", "--others", "--exclude-standard"), StringComparison.Ordinal);
		Assert.DoesNotContain("R", repository.Git("diff", "--cached", "--name-status"), StringComparison.Ordinal);
		var result = await CreateIsolatedChangesProvider(repository).ResolveAsync(repository.Root, GitFilteringMode.Changes, null, TestContext.Current.CancellationToken);
		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.Equal(["after.txt", "untracked.txt"], RelativePaths(repository.Root, result.IncludedPaths));
		Assert.Equal(1, result.DeletedPathCount);
	}
}
