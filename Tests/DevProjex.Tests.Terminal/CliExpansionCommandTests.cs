using System.Globalization;
using DevProjex.Infrastructure.Persistence;
using DevProjex.Infrastructure.RecentProjects;
using DevProjex.Infrastructure.Git;
using DevProjex.Kernel.Abstractions;
using DevProjex.Kernel.Models;

namespace DevProjex.Tests.Terminal;

public sealed class CliExpansionCommandTests
{
	[Fact]
	public async Task HelpCommandUsesTheCanonicalRendererAndAcceptsDocumentedAlias()
	{
		var direct = new TestTerminalEnvironment();
		var indirect = new TestTerminalEnvironment();

		var directExitCode = await new TerminalApplication(direct).RunAsync(
			["export", "context", "--language", "en", "--help"],
			TestContext.Current.CancellationToken);
		var indirectExitCode = await new TerminalApplication(indirect).RunAsync(
			["help", "export", "ctx", "--language", "en"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, directExitCode);
		Assert.Equal(CommandLineExitCodes.Success, indirectExitCode);
		Assert.Equal(direct.StandardOutput, indirect.StandardOutput);
		Assert.Contains("-f", indirect.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("-n", indirect.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(indirect.StandardError);

		var parent = new TestTerminalEnvironment();
		var parentExitCode = await new TerminalApplication(parent).RunAsync(
			["help", "export", "--language", "en"],
			TestContext.Current.CancellationToken);
		Assert.Equal(CommandLineExitCodes.Success, parentExitCode);
		Assert.Contains("context, ctx", parent.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("project, proj", parent.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public async Task HelpCommandRejectsAnUnknownNestedPath()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["help", "export", "missing", "--language", "en"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("DPX-CLI-UNKNOWN-COMMAND", environment.StandardError, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("context", "ctx", "markdown", "context.md")]
	[InlineData("project", "proj", "zip", "project.zip")]
	public async Task ExportAliasesAndShortFlagsAreExecutionEquivalent(
		string canonicalCommand,
		string alias,
		string formatOrKind,
		string outputName)
	{
		using var project = new TemporaryDirectory();
		using var output = new TemporaryDirectory();
		using var data = new TemporaryDirectory();
		project.WriteFile("src/App.cs", "internal sealed class App { }\n");
		var destination = Path.Combine(output.Path, outputName);
		var canonical = new TestTerminalEnvironment();
		var abbreviated = new TestTerminalEnvironment();
		var factory = new TerminalServiceFactory(() => data.Path);
		var canonicalArguments = canonicalCommand == "context"
			? new[]
			{
				"export", canonicalCommand, project.Path,
				"--format", formatOrKind, "--dry-run", "--verbosity", "quiet", "-o", destination
			}
			: [
				"export", canonicalCommand, project.Path,
				"--as", formatOrKind, "--dry-run", "--verbosity", "quiet", "-o", destination
			];
		var abbreviatedArguments = canonicalCommand == "context"
			? new[]
			{
				"export", alias, project.Path,
				"-f", formatOrKind, "-n", "-q", "-o", destination
			}
			: [
				"export", alias, project.Path,
				"--as", formatOrKind, "-n", "-q", "-o", destination
			];

		var canonicalExitCode = await new TerminalApplication(canonical, factory).RunAsync(
			canonicalArguments,
			TestContext.Current.CancellationToken);
		var abbreviatedExitCode = await new TerminalApplication(abbreviated, factory).RunAsync(
			abbreviatedArguments,
			TestContext.Current.CancellationToken);

		Assert.Equal(canonicalExitCode, abbreviatedExitCode);
		Assert.Equal(canonical.StandardOutput, abbreviated.StandardOutput);
		Assert.Equal(canonical.StandardError, abbreviated.StandardError);
		Assert.False(File.Exists(destination));
		Assert.False(Directory.Exists(destination));
	}

	[Fact]
	public void RemovedCompressAliasIsRejected()
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();

		var parseResult = root.Parse(["analyze", ".", "--compress"]);

		Assert.NotEmpty(parseResult.Errors);
	}

	[Theory]
	[InlineData("text")]
	[InlineData("markdown")]
	[InlineData("json")]
	[InlineData("xml")]
	public async Task TreeCommandMatchesTheSharedTreeExportService(string format)
	{
		using var project = new TemporaryDirectory();
		using var data = new TemporaryDirectory();
		project.WriteFile("src/App.cs", "internal sealed class App { }\n");
		project.WriteFile("docs/readme.md", "# Docs\n");
		var factory = new TerminalServiceFactory(() => data.Path);
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment, factory).RunAsync(
			[
				"tree", project.Path,
				"-f", format,
				"--git-mode", "none",
				"--exclude", "none",
				"-o", "-"
			],
			TestContext.Current.CancellationToken);

		var services = factory.Create(AppLanguage.En);
		var plan = await services.ContextFactory.BuildAsync(
			project.Path,
			new ProjectSelectionSpec(GitMode: GitFilteringMode.None, Exclusions: []),
			cancellationToken: TestContext.Current.CancellationToken);
		var expected = services.TreeExportService.BuildFullTree(
			project.Path,
			plan.ProjectedTree,
			ParseTreeFormat(format));

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(expected, environment.StandardOutput);
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task TreeHelpDoesNotExposeContentTransformations()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["tree", "--language", "en", "--help"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.DoesNotContain("--hide-secrets", environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("--hide-private-data", environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("--compress-code", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("--select-from", environment.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RecentJsonUsesStableSchemaAndNewestFirstOrdering()
	{
		using var data = new TemporaryDirectory();
		using var olderProject = new TemporaryDirectory();
		using var newerProject = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => data.Path);
		store.AddFolder(null, olderProject.Path);
		await Task.Delay(20, TestContext.Current.CancellationToken);
		store.AddFolder(null, newerProject.Path);
		store.AddRepository(null, "https://github.com/example/sample.git");
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => data.Path))
			.RunAsync(
				["recent", "--kind", "folder", "-f", "json", "--limit", "2"],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
		Assert.Equal("devprojex-recent", document.RootElement.GetProperty("kind").GetString());
		var items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
		Assert.Equal(2, items.Length);
		Assert.All(items, static item => Assert.Equal("folder", item.GetProperty("kind").GetString()));
		Assert.Equal(
			newerProject.Path.Replace('\\', '/'),
			items[0].GetProperty("path").GetString());
		Assert.Empty(environment.StandardError);
	}

	[Theory]
	[InlineData("remove", "https://github.com/example/sample.git")]
	[InlineData("clear", null)]
	public async Task DestructiveCacheCommandsRequireForce(string action, string? repositoryUrl)
	{
		var environment = new TestTerminalEnvironment();
		var arguments = new List<string> { "cache", action, "--language", "en" };
		if (repositoryUrl is not null)
			arguments.Insert(2, repositoryUrl);

		var exitCode = await new TerminalApplication(environment).RunAsync(
			arguments.ToArray(),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("--force", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task EmptyCacheListUsesStableJsonSchema()
	{
		using var data = new TemporaryDirectory();
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => data.Path))
			.RunAsync(
				["cache", "list", "-f", "json"],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
		Assert.Equal(
			"devprojex-repository-cache",
			document.RootElement.GetProperty("kind").GetString());
		Assert.False(document.RootElement.TryGetProperty("incomplete", out _));
		Assert.Empty(document.RootElement.GetProperty("items").EnumerateArray());
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task CacheRemoveNotFoundSanitizesCredentials()
	{
		using var data = new TemporaryDirectory();
		const string repositoryUrl = "https://user:top-secret@example.com/owner/repository.git";
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => data.Path))
			.RunAsync(
				["cache", "remove", repositoryUrl, "--force", "--language", "en"],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.RuntimeError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.DoesNotContain("top-secret", environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain(repositoryUrl, environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("https://example.com/owner/repository.git", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public void RecentTextEntryEscapesControlCharactersInsideFields()
	{
		var opened = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
		var line = Assert.Single(RecentCommandHandler.FormatTextEntries(
		[
			new RecentCommandHandler.RecentOutputEntry(
				"folder",
				"/tmp/source\r\tpath",
				null,
				"name\nrow",
				null,
				opened)
		]));

		Assert.Contains("name\\nrow", line, StringComparison.Ordinal);
		Assert.Contains("/tmp/source\\r\\tpath", line, StringComparison.Ordinal);
		Assert.EndsWith(
			opened.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
			line,
			StringComparison.Ordinal);
		Assert.DoesNotContain('\t', line);
		Assert.DoesNotContain('\n', line);
		Assert.DoesNotContain('\r', line);
	}

	[Fact]
	public void CacheTextEntryEscapesControlCharactersInsideFields()
	{
		var entry = new RepositoryCacheCatalogEntry(
			"https://example.com/owner/repo\nname.git",
			"repo",
			"feature\rbranch",
			new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
			42,
			RepositoryCacheContentKind.Git,
			"/tmp/cache\tpath",
			"commit\nvalue");

		var line = Assert.Single(CacheCommandHandler.FormatTextEntries([entry]));

		Assert.Contains("https://example.com/owner/repo\\nname.git", line, StringComparison.Ordinal);
		Assert.Contains("feature\\rbranch", line, StringComparison.Ordinal);
		Assert.Contains("commit\\nvalue", line, StringComparison.Ordinal);
		Assert.Contains("/tmp/cache\\tpath", line, StringComparison.Ordinal);
		Assert.Contains("42 B", line, StringComparison.Ordinal);
		Assert.DoesNotContain('\t', line);
		Assert.DoesNotContain('\n', line);
		Assert.DoesNotContain('\r', line);
	}

	[Fact]
	public void CacheTextSizeUsesBinaryHumanReadableUnits()
	{
		Assert.Equal("68.2 MiB", CacheCommandHandler.FormatByteSize(71_512_883));
		Assert.Equal("1 KiB", CacheCommandHandler.FormatByteSize(1_024));
		Assert.Equal("0 B", CacheCommandHandler.FormatByteSize(-1));
	}

	[Fact]
	public void RecentAndCacheTextSnapshotsUseLocalMinuteTimeAndSpaceSeparatedColumns()
	{
		var localDate = new DateTime(2026, 1, 2, 3, 4, 0, DateTimeKind.Unspecified);
		var localTimestamp = new DateTimeOffset(localDate, TimeZoneInfo.Local.GetUtcOffset(localDate));
		var recent = Assert.Single(RecentCommandHandler.FormatTextEntries(
		[
			new RecentCommandHandler.RecentOutputEntry(
				"folder",
				"/tmp/project",
				null,
				"sample",
				null,
				localTimestamp)
		]));
		var cache = Assert.Single(CacheCommandHandler.FormatTextEntries(
		[
			new RepositoryCacheCatalogEntry(
				"https://example.test/repo.git",
				"repo",
				"main",
				localTimestamp,
				71_512_883,
				RepositoryCacheContentKind.Git,
				"/tmp/cache",
				"abc")
		]));

		Assert.Equal("folder  sample  /tmp/project  2026-01-02 03:04", recent);
		Assert.Equal(
			"https://example.test/repo.git  ready  main  abc  68.2 MiB  2026-01-02 03:04  /tmp/cache",
			cache);
		Assert.DoesNotContain('\t', recent);
		Assert.DoesNotContain('\t', cache);
	}

	[Fact]
	public async Task CacheListWithBusyIndexLockReportsIncompletePolicyResult()
	{
		using var data = new TemporaryDirectory();
		var factory = new TerminalServiceFactory(() => data.Path);
		var services = factory.Create(AppLanguage.En);
		PublishSnapshot(services.RepoCacheService, "https://github.com/example/locked-list.git");
		var lockPath = Path.Combine(
			services.RepoCacheService.CacheRootPath,
			"cache-index.json.lock");
		using var heldLock = new FileStream(
			lockPath,
			FileMode.OpenOrCreate,
			FileAccess.ReadWrite,
			FileShare.None);
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment, factory).RunAsync(
			["cache", "list", "--format", "json", "--language", "en"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.PolicyFailure, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		Assert.True(document.RootElement.GetProperty("incomplete").GetBoolean());
		Assert.Empty(document.RootElement.GetProperty("items").EnumerateArray());
		Assert.Contains("cache list is incomplete", environment.StandardError, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task CacheListWithFutureSchemaReportsIncompletePolicyResult()
	{
		using var data = new TemporaryDirectory();
		var factory = new TerminalServiceFactory(() => data.Path);
		var services = factory.Create(AppLanguage.En);
		Directory.CreateDirectory(services.RepoCacheService.CacheRootPath);
		await File.WriteAllTextAsync(
			Path.Combine(services.RepoCacheService.CacheRootPath, "cache-index.json"),
			JsonSerializer.Serialize(
				new { SchemaVersion = 999, Entries = Array.Empty<object>() },
				new JsonSerializerOptions(JsonSerializerDefaults.Web)),
			TestContext.Current.CancellationToken);
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment, factory).RunAsync(
			["cache", "list", "--format", "json", "--language", "en"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.PolicyFailure, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		Assert.True(document.RootElement.GetProperty("incomplete").GetBoolean());
		Assert.Empty(document.RootElement.GetProperty("items").EnumerateArray());
		Assert.Contains("cache list is incomplete", environment.StandardError, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task CacheListIncludesDamagedEntriesWithThePromisedState()
	{
		using var data = new TemporaryDirectory();
		var factory = new TerminalServiceFactory(() => data.Path);
		var services = factory.Create(AppLanguage.En);
		const string repositoryUrl = "https://github.com/example/damaged.git";
		var staging = services.RepoCacheService.CreateRepositoryStagingDirectory(repositoryUrl);
		File.WriteAllText(Path.Combine(staging, "README.md"), "damaged\n");
		var published = services.RepoCacheService.PublishRepositoryDirectory(staging, repositoryUrl);
		services.RepoCacheService.RecordIndexedRepository(
			repositoryUrl,
			published,
			state: RepositoryCacheEntryState.Damaged);
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment, factory).RunAsync(
			["cache", "list", "--format", "json"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		var item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
		Assert.Equal(repositoryUrl, item.GetProperty("url").GetString());
		Assert.Equal("damaged", item.GetProperty("state").GetString());
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task CacheClearWithBusyIndexLockReturnsPolicyFailure()
	{
		using var data = new TemporaryDirectory();
		var factory = new TerminalServiceFactory(() => data.Path);
		var services = factory.Create(AppLanguage.En);
		PublishSnapshot(services.RepoCacheService, "https://github.com/example/locked.git");
		var lockPath = Path.Combine(
			services.RepoCacheService.CacheRootPath,
			"cache-index.json.lock");
		using var heldLock = new FileStream(
			lockPath,
			FileMode.OpenOrCreate,
			FileAccess.ReadWrite,
			FileShare.None);
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment, factory).RunAsync(
			["cache", "clear", "--force", "--language", "en"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.PolicyFailure, exitCode);
		Assert.Contains("Removed: 0. Retained: 0. Failed:", environment.StandardOutput);
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task CacheClearReportsLeasedRepositoryAsRetainedThenRemovesIt()
	{
		using var data = new TemporaryDirectory();
		const string repositoryUrl = "https://github.com/example/leased.git";
		var factory = new TerminalServiceFactory(() => data.Path);
		var services = factory.Create(AppLanguage.En);
		var staging = services.RepoCacheService.CreateRepositoryStagingDirectory(repositoryUrl);
		File.WriteAllText(Path.Combine(staging, "README.md"), "cached\n");
		var published = services.RepoCacheService.PublishRepositoryDirectory(staging, repositoryUrl);
		services.RepoCacheService.RecordIndexedRepository(repositoryUrl, published);
		using var session = await services.RepoCacheService.TryAcquireRepositorySessionAsync(
			repositoryUrl,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.NotNull(session);
		var listEnvironment = new TestTerminalEnvironment();
		var listExitCode = await new TerminalApplication(listEnvironment, factory).RunAsync(
			["cache", "list", "--format", "json"],
			TestContext.Current.CancellationToken);
		Assert.Equal(CommandLineExitCodes.Success, listExitCode);
		using (var list = JsonDocument.Parse(listEnvironment.StandardOutput))
		{
			var item = Assert.Single(list.RootElement.GetProperty("items").EnumerateArray());
			Assert.Equal(repositoryUrl, item.GetProperty("url").GetString());
			Assert.Equal("ready", item.GetProperty("state").GetString());
			Assert.True(item.GetProperty("approximateSizeBytes").GetInt64() >= 0);
			Assert.True(item.TryGetProperty("lastUsed", out _));
		}
		var retainedEnvironment = new TestTerminalEnvironment();

		var retainedExitCode = await new TerminalApplication(retainedEnvironment, factory).RunAsync(
			["cache", "clear", "--force", "--language", "en"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.PolicyFailure, retainedExitCode);
		Assert.Contains("Removed: 0. Retained: 1. Failed: 0.", retainedEnvironment.StandardOutput);
		Assert.True(Directory.Exists(published));

		session.Dispose();
		var removedEnvironment = new TestTerminalEnvironment();
		var removedExitCode = await new TerminalApplication(removedEnvironment, factory).RunAsync(
			["cache", "clear", "--force", "--language", "en"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, removedExitCode);
		Assert.Contains("Removed: 1. Retained: 0. Failed: 0.", removedEnvironment.StandardOutput);
		Assert.Empty(new RepoCacheService(Path.Combine(data.Path, "RepoCache")).ListIndexedRepositories());
	}

	[Fact]
	public async Task CacheRemoveDeletesOnlyTheRequestedRepository()
	{
		using var data = new TemporaryDirectory();
		var factory = new TerminalServiceFactory(() => data.Path);
		var services = factory.Create(AppLanguage.En);
		const string removedUrl = "https://github.com/example/remove.git";
		const string retainedUrl = "https://github.com/example/retain.git";
		var removedPath = PublishSnapshot(services.RepoCacheService, removedUrl);
		var retainedPath = PublishSnapshot(services.RepoCacheService, retainedUrl);
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment, factory).RunAsync(
			["cache", "remove", removedUrl, "--force", "--language", "en"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Contains("Removed: 1. Retained: 0. Failed: 0.", environment.StandardOutput);
		Assert.False(Directory.Exists(removedPath));
		Assert.True(Directory.Exists(retainedPath));
		Assert.Equal(
			retainedUrl,
			Assert.Single(services.RepoCacheService.ListIndexedRepositories()).RepositoryUrl);
	}

	private static TreeTextFormat ParseTreeFormat(string format) =>
		format switch
		{
			"text" => TreeTextFormat.Ascii,
			"markdown" => TreeTextFormat.Markdown,
			"json" => TreeTextFormat.Json,
			"xml" => TreeTextFormat.Xml,
			_ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
		};

	private static string PublishSnapshot(IRepoCacheService cache, string repositoryUrl)
	{
		var staging = cache.CreateRepositoryStagingDirectory(repositoryUrl);
		File.WriteAllText(Path.Combine(staging, "README.md"), "cached\n");
		var published = cache.PublishRepositoryDirectory(staging, repositoryUrl);
		cache.RecordIndexedRepository(repositoryUrl, published);
		return published;
	}
}
