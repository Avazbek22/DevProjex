using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using DevProjex.Infrastructure.Git;

namespace DevProjex.Tests.Terminal;

public sealed class PublishedSingleFileExtractionProcessTests
{
	private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);
	private const string ProcessSecret = "ghp_" + "Q7wE9rT2yU4iO6pA8sD0fG1hJ3kL5zX7cV9b";

	[Fact]
	public async Task ExpandedCliCommandsHonorProcessContractsAndRedirectedStdin()
	{
		var application = GetPublishedSingleFileOrSkip();
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("cli/project");
		workspace.WriteFile(
			"cli/project/src/app.cs",
			$"internal static class App {{ private const string Token = \"{ProcessSecret}\"; }}\n");
		var home = workspace.CreateDirectory("cli/home");
		var temporary = workspace.CreateDirectory("cli/temp");
		var dataRoot = workspace.CreateDirectory("cli/data");
		var environment = CreateEnvironment(home, temporary, dataRoot, bundleExtractionRoot: null);

		var help = await RunAsync(
			application,
			["help", "tree", "--language", "en"],
			environment,
			project,
			TestContext.Current.CancellationToken);
		Assert.Equal(CommandLineExitCodes.Success, help.ExitCode);
		Assert.Contains("devprojex tree", help.StandardOutput, StringComparison.Ordinal);

		var tree = await RunAsync(
			application,
			["tree", project, "--git-mode", "none", "--format", "json", "-o", "-", "--plain"],
			environment,
			project,
			TestContext.Current.CancellationToken);
		Assert.Equal(CommandLineExitCodes.Success, tree.ExitCode);
		Assert.Contains("app.cs", tree.StandardOutput, StringComparison.Ordinal);

		var recent = await RunAsync(
			application,
			["recent", "--format", "json", "--language", "en"],
			environment,
			project,
			TestContext.Current.CancellationToken);
		Assert.Equal(CommandLineExitCodes.Success, recent.ExitCode);
		using (var document = JsonDocument.Parse(recent.StandardOutput))
			Assert.Equal("devprojex-recent", document.RootElement.GetProperty("kind").GetString());

		var cacheList = await RunAsync(
			application,
			["cache", "list", "--format", "json", "--language", "en"],
			environment,
			project,
			TestContext.Current.CancellationToken);
		Assert.Equal(CommandLineExitCodes.Success, cacheList.ExitCode);
		using (var document = JsonDocument.Parse(cacheList.StandardOutput))
			Assert.Equal("devprojex-repository-cache", document.RootElement.GetProperty("kind").GetString());

		var cacheClear = await RunAsync(
			application,
			["cache", "clear", "--force", "--language", "en"],
			environment,
			project,
			TestContext.Current.CancellationToken);
		Assert.Equal(CommandLineExitCodes.Success, cacheClear.ExitCode);
		Assert.Contains("Removed: 0. Retained: 0. Failed: 0.", cacheClear.StandardOutput, StringComparison.Ordinal);

		var findings = await RunAsync(
			application,
			[
				"analyze", project,
				"--git-mode", "none", "--hide-secrets", "--findings", "--fail-on-findings",
				"--format", "json", "-o", "-", "--plain"
			],
			environment,
			project,
			TestContext.Current.CancellationToken);
		Assert.Equal(CommandLineExitCodes.PolicyFailure, findings.ExitCode);
		Assert.DoesNotContain(ProcessSecret, findings.StandardOutput, StringComparison.Ordinal);
		using (var document = JsonDocument.Parse(findings.StandardOutput))
			Assert.Single(document.RootElement.GetProperty("findings").EnumerateArray());

		var selectedTree = await RunAsync(
			application,
			[
				"tree", project, "--git-mode", "none", "--select-from", "-",
				"--format", "json", "-o", "-", "--plain"
			],
			environment,
			project,
			"src/app.cs\n",
			TestContext.Current.CancellationToken);
		Assert.Equal(CommandLineExitCodes.Success, selectedTree.ExitCode);
		Assert.Contains("app.cs", selectedTree.StandardOutput, StringComparison.Ordinal);

		Assert.All(
			new[] { help, tree, recent, cacheList, cacheClear, findings, selectedTree },
			static result => Assert.DoesNotContain('\u001b', result.StandardOutput + result.StandardError));
	}

	[Fact]
	public async Task UrlSourcesExportThroughThePublishedProcess()
	{
		var application = GetPublishedSingleFileOrSkip();
		if (!IsGitAvailable())
			Assert.Skip("Git is unavailable on this test host.");

		using var workspace = new TemporaryDirectory();
		var source = workspace.CreateDirectory("url/source");
		RunGit(source, "init", "--initial-branch=main");
		RunGit(source, "config", "user.email", "terminal-tests@devprojex.local");
		RunGit(source, "config", "user.name", "DevProjex Terminal Tests");
		workspace.WriteFile("url/source/src/remote.cs", "internal sealed class PublishedRemoteMarker {}\n");
		RunGit(source, "add", ".");
		RunGit(source, "commit", "-m", "initial");
		var bare = Path.Combine(workspace.Path, "url", "origin.git");
		RunGit(workspace.Path, "clone", "--bare", source, bare);
		var repositoryUrl = new Uri(bare + Path.DirectorySeparatorChar).AbsoluteUri;
		var home = workspace.CreateDirectory("url/home");
		var temporary = workspace.CreateDirectory("url/temp");
		var dataRoot = workspace.CreateDirectory("url/data");
		var environment = CreateEnvironment(home, temporary, dataRoot, bundleExtractionRoot: null);
		environment[GitRepositoryService.TestFileTransportPolicyVariable] = "1";

		var context = await RunAsync(
			application,
			[
				"export", "context", repositoryUrl,
				"--git-mode", "none", "--view", "content", "--format", "text", "-o", "-", "--plain",
				"--language", "en"
			],
			environment,
			workspace.Path,
			TestContext.Current.CancellationToken);
		Assert.Equal(CommandLineExitCodes.Success, context.ExitCode);
		Assert.Contains("PublishedRemoteMarker", context.StandardOutput, StringComparison.Ordinal);
		var progressLines = context.StandardError
			.ReplaceLineEndings("\n")
			.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		Assert.InRange(progressLines.Length, 2, 6);
		Assert.StartsWith("Cloning ", progressLines[0], StringComparison.Ordinal);
		Assert.Equal("Clone completed.", progressLines[^1]);

		var quietContext = await RunAsync(
			application,
			[
				"export", "context", repositoryUrl,
				"--git-mode", "none", "--view", "content", "--format", "text", "-o", "-", "--plain",
				"--language", "en", "--progress", "never"
			],
			environment,
			workspace.Path,
			TestContext.Current.CancellationToken);
		Assert.Equal(CommandLineExitCodes.Success, quietContext.ExitCode);
		Assert.Equal(context.StandardOutput, quietContext.StandardOutput);
		Assert.Empty(quietContext.StandardError);

		var destination = Path.Combine(workspace.Path, "url", "exported");
		var project = await RunAsync(
			application,
			[
				"export", "project", repositoryUrl,
				"--git-mode", "none", "--as", "folder", "-o", destination, "--plain"
			],
			environment,
			workspace.Path,
			TestContext.Current.CancellationToken);
		Assert.Equal(CommandLineExitCodes.Success, project.ExitCode);
		Assert.Equal(
			"internal sealed class PublishedRemoteMarker {}\n",
			File.ReadAllText(Path.Combine(destination, "src", "remote.cs")).ReplaceLineEndings("\n"));
		Assert.Empty(project.StandardError);
	}

	[Fact]
	public async Task WritableDefaultExtractionSupportsColdAndWarmDirectCli()
	{
		var application = GetPublishedSingleFileOrSkip();
		using var workspace = new TemporaryDirectory();
		var home = workspace.CreateDirectory("default/home");
		var temporary = workspace.CreateDirectory("default/temp");
		var dataRoot = workspace.CreateDirectory("default/data");
		var environment = CreateEnvironment(
			home,
			temporary,
			dataRoot,
			bundleExtractionRoot: null);

		var cold = await RunVersionAsync(
			application,
			environment,
			TestContext.Current.CancellationToken);
		var expectedOutput = AssertCleanVersionResult(cold);
		var defaultExtractionRoot = OperatingSystem.IsWindows()
			? Path.Combine(temporary, ".net")
			: Path.Combine(home, ".net");
		var coldSnapshot = CaptureExtractionSnapshot(defaultExtractionRoot);

		var warm = await RunVersionAsync(
			application,
			environment,
			TestContext.Current.CancellationToken);
		AssertCleanVersionResult(warm, expectedOutput);
		var warmSnapshot = CaptureExtractionSnapshot(defaultExtractionRoot);

		Assert.Equal(coldSnapshot, warmSnapshot);
	}

	[Fact]
	public async Task ExplicitExtractionRootSupportsUnsetHome()
	{
		var application = GetPublishedSingleFileOrSkip();
		using var workspace = new TemporaryDirectory();
		var home = workspace.CreateDirectory("unset-home/home");
		var temporary = workspace.CreateDirectory("unset-home/temp");
		var dataRoot = workspace.CreateDirectory("unset-home/data");
		var extractionRoot = workspace.CreateDirectory("unset-home/extraction");
		var environment = CreateEnvironment(
			home,
			temporary,
			dataRoot,
			extractionRoot);
		environment["HOME"] = null;

		var result = await RunVersionAsync(
			application,
			environment,
			TestContext.Current.CancellationToken);

		AssertCleanVersionResult(result);
		CaptureExtractionSnapshot(extractionRoot);
		Assert.False(Directory.Exists(Path.Combine(temporary, ".net")));
	}

	[Fact]
	public async Task ExplicitExtractionRootBypassesWindowsTempAndTmpThatAreFiles()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Skip(
				"This regression covers the Windows TEMP/TMP bundle-host fallback.");
			return;
		}

		var application = GetPublishedSingleFileOrSkip();
		using var workspace = new TemporaryDirectory();
		var home = workspace.CreateDirectory("invalid-windows-temp/home");
		var fallbackTemporaryDirectory = workspace.CreateDirectory(
			"invalid-windows-temp/fallback");
		var unusableTempPath = workspace.WriteFile(
			"invalid-windows-temp/temp-is-a-file",
			"TEMP sentinel\n");
		var unusableTmpPath = workspace.WriteFile(
			"invalid-windows-temp/tmp-is-a-file",
			"TMP sentinel\n");
		var dataRoot = workspace.CreateDirectory("invalid-windows-temp/data");
		var extractionRoot = workspace.CreateDirectory(
			"invalid-windows-temp/extraction");
		var environment = CreateEnvironment(
			home,
			fallbackTemporaryDirectory,
			dataRoot,
			extractionRoot);
		environment["TEMP"] = unusableTempPath;
		environment["TMP"] = unusableTmpPath;

		var result = await RunVersionAsync(
			application,
			environment,
			TestContext.Current.CancellationToken);

		AssertCleanVersionResult(result);
		CaptureExtractionSnapshot(extractionRoot);
		Assert.Equal(
			"TEMP sentinel\n",
			File.ReadAllText(unusableTempPath));
		Assert.Equal(
			"TMP sentinel\n",
			File.ReadAllText(unusableTmpPath));
	}

	[Fact]
	public async Task ParallelColdStartAndWarmReuseShareOneCompleteExtraction()
	{
		var application = GetPublishedSingleFileOrSkip();
		using var workspace = new TemporaryDirectory();
		var home = workspace.CreateDirectory("parallel/home");
		var temporary = workspace.CreateDirectory("parallel/temp");
		var dataRoot = workspace.CreateDirectory("parallel/data");
		var extractionRoot = workspace.CreateDirectory("parallel/extraction");
		var environment = CreateEnvironment(
			home,
			temporary,
			dataRoot,
			extractionRoot);

		var coldStarts = Enumerable.Range(0, 6)
			.Select(_ => RunVersionAsync(
				application,
				environment,
				TestContext.Current.CancellationToken))
			.ToArray();
		var coldResults = await Task.WhenAll(coldStarts);
		var expectedOutput = AssertCleanVersionResult(coldResults[0]);
		foreach (var result in coldResults.Skip(1))
			AssertCleanVersionResult(result, expectedOutput);
		var coldSnapshot = CaptureExtractionSnapshot(extractionRoot);

		var warm = await RunVersionAsync(
			application,
			environment,
			TestContext.Current.CancellationToken);
		AssertCleanVersionResult(warm, expectedOutput);
		var warmSnapshot = CaptureExtractionSnapshot(extractionRoot);

		Assert.Equal(coldSnapshot, warmSnapshot);
	}

	[Fact]
	public async Task ExplicitExtractionRootBypassesReadOnlyUnixHomeAndTemp()
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip(
				"Windows directory readonly attributes do not deny writes. " +
				"The Windows default %TEMP% path and explicit extraction root are " +
				"covered without unreliable hosted-runner ACL mutation.");
			return;
		}

		var application = GetPublishedSingleFileOrSkip();
		using var workspace = new TemporaryDirectory();
		var home = workspace.CreateDirectory("readonly/home");
		var temporary = workspace.CreateDirectory("readonly/temp");
		var dataRoot = workspace.CreateDirectory("readonly/data");
		var extractionRoot = workspace.CreateDirectory("readonly/extraction");
		var environment = CreateEnvironment(
			home,
			temporary,
			dataRoot,
			extractionRoot);
		var readOnlyMode = UnixFileMode.UserRead | UnixFileMode.UserExecute;
		var writableMode =
			UnixFileMode.UserRead |
			UnixFileMode.UserWrite |
			UnixFileMode.UserExecute;

		File.SetUnixFileMode(home, readOnlyMode);
		File.SetUnixFileMode(temporary, readOnlyMode);
		try
		{
			var result = await RunVersionAsync(
				application,
				environment,
				TestContext.Current.CancellationToken);

			AssertCleanVersionResult(result);
			CaptureExtractionSnapshot(extractionRoot);
			Assert.Empty(Directory.EnumerateFileSystemEntries(home));
			Assert.Empty(Directory.EnumerateFileSystemEntries(temporary));
		}
		finally
		{
			File.SetUnixFileMode(home, writableMode);
			File.SetUnixFileMode(temporary, writableMode);
		}
	}

	[Fact]
	public async Task EmptyInlineAssignmentCannotConsumeHelpOrWriteAnArtifact()
	{
		var application = GetPublishedSingleFileOrSkip();
		using var workspace = new TemporaryDirectory();
		var home = workspace.CreateDirectory("integrity/home");
		var temporary = workspace.CreateDirectory("integrity/temp");
		var dataRoot = workspace.CreateDirectory("integrity/data");
		var extractionRoot = workspace.CreateDirectory("integrity/extraction");
		var project = workspace.CreateDirectory("integrity/source");
		File.WriteAllText(
			Path.Combine(project, "App.cs"),
			"class App {}\n",
			new UTF8Encoding(false));
		var environment = CreateEnvironment(
			home,
			temporary,
			dataRoot,
			extractionRoot);
		var accidentalArtifact = Path.Combine(workspace.Path, "--help");

		var result = await RunAsync(
			application,
			[
				"profile", "export", project,
				"--profile", "standard",
				"--output=",
				"--help",
				"--language", "en"
			],
			environment,
			workspace.Path,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Empty(result.StandardOutput);
		Assert.Contains(
			"error[DPX-CLI-MISSING-VALUE]",
			result.StandardError,
			StringComparison.Ordinal);
		Assert.False(File.Exists(accidentalArtifact));
		Assert.Equal(
			["App.cs"],
			Directory.EnumerateFiles(project, "*", SearchOption.AllDirectories)
				.Select(path => Path.GetRelativePath(project, path))
				.ToArray());
	}

	[Fact]
	public async Task TrackedModeFailsClosedWhenGitIsUnavailableInPublishedProcess()
	{
		var application = GetPublishedSingleFileOrSkip();
		using var workspace = new TemporaryDirectory();
		var home = workspace.CreateDirectory("git-unavailable/home");
		var temporary = workspace.CreateDirectory("git-unavailable/temp");
		var dataRoot = workspace.CreateDirectory("git-unavailable/data");
		var extractionRoot = workspace.CreateDirectory("git-unavailable/extraction");
		var emptyPath = workspace.CreateDirectory("git-unavailable/empty-path");
		var project = workspace.CreateDirectory("git-unavailable/source");
		var sourcePath = workspace.WriteFile(
			"git-unavailable/source/App.cs",
			"class App {}\n");
		var indexPath = workspace.WriteFile(
			"git-unavailable/source/.git/index",
			"unreadable index fixture\n");
		var outputPath = Path.Combine(workspace.Path, "git-unavailable", "context.json");
		var environment = CreateEnvironment(
			home,
			temporary,
			dataRoot,
			extractionRoot);
		environment["PATH"] = emptyPath;
		var sourceSnapshot = CaptureSourceTree(project);

		var analysis = await RunAsync(
			application,
			[
				"analyze", project,
				"--format", "json",
				"--git-mode", "tracked",
				"--exclude", "none",
				"--plain",
				"--language", "en"
			],
			environment,
			workspace.Path,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.PolicyFailure, analysis.ExitCode);
		using (var document = JsonDocument.Parse(analysis.StandardOutput))
		{
			Assert.Equal(
				0,
				document.RootElement.GetProperty("inventory").GetProperty("files").GetInt32());
			Assert.Contains(
				document.RootElement.GetProperty("diagnostics").EnumerateArray(),
				static diagnostic =>
					diagnostic.GetProperty("code").GetString() ==
					"DPX-GIT-TRACKED-INDEX-UNAVAILABLE");
		}
		Assert.Contains(
			"DPX-GIT-TRACKED-INDEX-UNAVAILABLE",
			analysis.StandardError,
			StringComparison.Ordinal);
		Assert.DoesNotContain('\u001b', analysis.StandardOutput);
		Assert.DoesNotContain('\u001b', analysis.StandardError);
		Assert.DoesNotContain("DPX-CLI-UNEXPECTED", analysis.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain(" at DevProjex.", analysis.StandardError, StringComparison.Ordinal);

		var contextExport = await RunAsync(
			application,
			[
				"export", "context", project,
				"--format", "json",
				"--git-mode", "tracked",
				"--exclude", "none",
				"--output", outputPath,
				"--plain",
				"--language", "en"
			],
			environment,
			workspace.Path,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.PolicyFailure, contextExport.ExitCode);
		Assert.Empty(contextExport.StandardOutput);
		Assert.Contains(
			"DPX-GIT-TRACKED-INDEX-UNAVAILABLE",
			contextExport.StandardError,
			StringComparison.Ordinal);
		Assert.DoesNotContain('\u001b', contextExport.StandardError);
		Assert.DoesNotContain("DPX-CLI-UNEXPECTED", contextExport.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain(" at DevProjex.", contextExport.StandardError, StringComparison.Ordinal);
		Assert.False(File.Exists(outputPath));
		Assert.Equal("class App {}\n", File.ReadAllText(sourcePath));
		Assert.Equal("unreadable index fixture\n", File.ReadAllText(indexPath));
		Assert.Equal(sourceSnapshot, CaptureSourceTree(project));
	}

	[Fact]
	public async Task CompressionFlowsThroughEveryPublishedDirectOutputWithoutChangingSource()
	{
		var application = GetPublishedSingleFileOrSkip();
		using var workspace = new TemporaryDirectory();
		var home = workspace.CreateDirectory("compression/home");
		var temporary = workspace.CreateDirectory("compression/temp");
		var dataRoot = workspace.CreateDirectory("compression/data");
		var extractionRoot = workspace.CreateDirectory("compression/extraction");
		var project = workspace.CreateDirectory("compression/source");
		var sources = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["pipeline.c"] =
				"typedef int (*transform_fn)(int);\nint apply(transform_fn fn, int value) { int published_c_marker = fn(value); published_c_marker += 10; return published_c_marker; }\n",
			["box.cpp"] =
				"template <typename T> class Box { public: T map(T value) { T published_cpp_marker = value; published_cpp_marker += value; return published_cpp_marker; } };\n",
			["Widget.cs"] =
				"""
				public sealed class Widget
				{
				    private readonly System.Func<int> _factory = Create;

				    public int Compute(int left, int right)
				    {
				        var published_csharp_marker = left + right;
				        published_csharp_marker += 10;
				        return published_csharp_marker;
				    }

				    private static int Create()
				    {
				        var published_csharp_factory_marker = 40;
				        return published_csharp_factory_marker + 2;
				    }
				}
				""",
			["store.go"] =
				"package sample\ntype Store[T any] struct { value T }\nfunc (store *Store[T]) Map(transform func(T) T) T { published_go_marker := transform(store.value); published_go_marker = transform(published_go_marker); return published_go_marker }\n",
			["Range.java"] =
				"record Range(int start, int end) { Range { int published_java_compact_marker = end - start; if (published_java_compact_marker < 0) throw new IllegalArgumentException(); } }\n",
			["stream.mjs"] =
				"export async function* stream(values) { const published_js_generator_marker = values.length; yield published_js_generator_marker; yield values.length; }\n",
			["service.py"] =
				"async def create(values):\n    published_python_marker = sum(values)\n    published_python_marker += 10\n    return published_python_marker\n",
			["repository.rs"] =
				"pub fn map<T: Clone>(value: T) -> T { let published_rust_marker = value.clone(); let _copy = published_rust_marker.clone(); published_rust_marker }\n",
			["repository.ts"] =
				"export async function* streamTs(values: readonly number[]) { const published_typescript_marker = values.length; yield published_typescript_marker; yield values.length; }\n",
			["Widget.tsx"] =
				"export function Widget({ value }: { value: number }) { const published_tsx_marker = value + 1; const label = published_tsx_marker.toString(); return <section>{label}</section>; }\n"
		};
		foreach (var source in sources)
		{
			workspace.WriteFile(
				Path.Combine("compression", "source", source.Key),
				source.Value);
		}
		var sourceSnapshot = CaptureSourceTree(project);
		var environment = CreateEnvironment(home, temporary, dataRoot, extractionRoot);
		var common = new[]
		{
			"--compress-code",
			"--git-mode", "none",
			"--exclude", "none",
			"--progress", "never",
			"--plain",
			"--language", "en"
		};

		var analysis = await RunAsync(
			application,
			["analyze", project, "--format", "json", .. common],
			environment,
			workspace.Path,
			TestContext.Current.CancellationToken);
		Assert.Equal(CommandLineExitCodes.Success, analysis.ExitCode);
		Assert.Empty(analysis.StandardError);
		using (var document = JsonDocument.Parse(analysis.StandardOutput))
		{
			Assert.True(document.RootElement.GetProperty("selection").GetProperty("compressCode").GetBoolean());
			Assert.Equal(10, document.RootElement.GetProperty("compression").GetProperty("compressedFiles").GetInt32());
			Assert.Equal(0, document.RootElement.GetProperty("compression").GetProperty("unchangedFiles").GetInt32());
		}

		var context = await RunAsync(
			application,
			[
				"export", "context", project,
				"--view", "content",
				"--format", "markdown",
				"-o", "-",
				.. common
			],
			environment,
			workspace.Path,
			TestContext.Current.CancellationToken);
		Assert.Equal(CommandLineExitCodes.Success, context.ExitCode);
		Assert.Empty(context.StandardError);
		Assert.Contains("Compute(int left, int right)", context.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("_factory = Create;", context.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("async function* stream(values)", context.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("async function* streamTs(values", context.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("published_", context.StandardOutput, StringComparison.OrdinalIgnoreCase);

		var folderDestination = Path.Combine(workspace.Path, "compression", "folder-output");
		var folder = await RunAsync(
			application,
			[
				"export", "project", project,
				"--as", "folder",
				"-o", folderDestination,
				.. common
			],
			environment,
			workspace.Path,
			TestContext.Current.CancellationToken);
		Assert.Equal(CommandLineExitCodes.Success, folder.ExitCode);
		Assert.Empty(folder.StandardError);
		Assert.Equal(Path.GetFullPath(folderDestination), folder.StandardOutput.Trim());
		var folderSource = File.ReadAllText(Path.Combine(folderDestination, "Widget.cs"));
		Assert.Contains("_factory = Create;", folderSource, StringComparison.Ordinal);
		foreach (var relativePath in sources.Keys)
		{
			Assert.DoesNotContain(
				"published_",
				File.ReadAllText(Path.Combine(folderDestination, relativePath)),
				StringComparison.OrdinalIgnoreCase);
		}
		Assert.True(File.Exists(Path.Combine(
			folderDestination,
			ProjectCopyExportService.TransformationNoticeFileName)));

		var zipDestination = Path.Combine(workspace.Path, "compression", "project-output.zip");
		var zip = await RunAsync(
			application,
			[
				"export", "project", project,
				"--as", "zip",
				"-o", zipDestination,
				.. common
			],
			environment,
			workspace.Path,
			TestContext.Current.CancellationToken);
		Assert.Equal(CommandLineExitCodes.Success, zip.ExitCode);
		Assert.Empty(zip.StandardError);
		Assert.Equal(Path.GetFullPath(zipDestination), zip.StandardOutput.Trim());
		using (var archive = ZipFile.OpenRead(zipDestination))
		{
			foreach (var relativePath in sources.Keys)
			{
				var normalizedPath = relativePath.Replace('\\', '/');
				var sourceEntry = Assert.Single(archive.Entries, entry =>
					entry.FullName.EndsWith(normalizedPath, StringComparison.Ordinal));
				using var reader = new StreamReader(sourceEntry.Open(), Encoding.UTF8);
				Assert.Equal(
					File.ReadAllText(Path.Combine(folderDestination, relativePath)),
					await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
			}
			Assert.Contains(archive.Entries, static entry =>
				entry.FullName.EndsWith(
					ProjectCopyExportService.TransformationNoticeFileName,
					StringComparison.Ordinal));
		}

		Assert.Equal(sourceSnapshot, CaptureSourceTree(project));
	}

	private static IReadOnlyList<string> CaptureSourceTree(string rootPath)
	{
		var entries = new List<string>();
		foreach (var directory in Directory.EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories))
		{
			entries.Add($"D:{Path.GetRelativePath(rootPath, directory).Replace('\\', '/')}");
		}
		foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
		{
			var relativePath = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
			entries.Add($"F:{relativePath}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}");
		}

		entries.Sort(StringComparer.Ordinal);
		return entries;
	}

	private static string GetPublishedSingleFileOrSkip()
	{
		var explicitPath = Environment.GetEnvironmentVariable(
			"DEVPROJEX_TUI_TEST_BINARY");
		if (string.IsNullOrWhiteSpace(explicitPath))
		{
			Assert.Skip(
				"Single-file extraction requires a native published artifact supplied " +
				"through DEVPROJEX_TUI_TEST_BINARY.");
		}

		var application = Path.GetFullPath(explicitPath);
		Assert.True(
			File.Exists(application),
			$"Published application does not exist: {application}");
		Assert.False(
			File.Exists(Path.ChangeExtension(application, ".runtimeconfig.json")),
			$"The extraction gate requires a single-file artifact: {application}");
		Assert.False(
			File.Exists(Path.ChangeExtension(application, ".deps.json")),
			$"The extraction gate requires a single-file artifact: {application}");
		return application;
	}

	private static Dictionary<string, string?> CreateEnvironment(
		string home,
		string temporary,
		string dataRoot,
		string? bundleExtractionRoot) =>
		new(StringComparer.OrdinalIgnoreCase)
		{
			["HOME"] = home,
			["USERPROFILE"] = home,
			["TEMP"] = temporary,
			["TMP"] = temporary,
			["TMPDIR"] = temporary,
			["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = bundleExtractionRoot,
			["DOTNET_NOLOGO"] = "1",
			["CI"] = "1",
			[InvocationEnvironment.TerminalHostVariable] = "1",
			[InvocationEnvironment.InternalDataRootVariable] = dataRoot
		};

	private static async Task<VersionProcessResult> RunVersionAsync(
		string application,
		IReadOnlyDictionary<string, string?> environment,
		CancellationToken cancellationToken) =>
		await RunAsync(
			application,
			["--version"],
			environment,
			workingDirectory: null,
			cancellationToken);

	private static async Task<VersionProcessResult> RunAsync(
		string application,
		IReadOnlyList<string> arguments,
		IReadOnlyDictionary<string, string?> environment,
		string? workingDirectory,
		CancellationToken cancellationToken) =>
		await RunAsync(
			application,
			arguments,
			environment,
			workingDirectory,
			standardInput: null,
			cancellationToken);

	private static bool IsGitAvailable()
	{
		try
		{
			using var process = Process.Start(CreateGitStartInfo(null, ["--version"]));
			process?.WaitForExit(5_000);
			return process is { HasExited: true, ExitCode: 0 };
		}
		catch
		{
			return false;
		}
	}

	private static void RunGit(string workingDirectory, params string[] arguments)
	{
		var result = TerminalTestProcess.Run(CreateGitStartInfo(workingDirectory, arguments));
		Assert.True(
			result.ExitCode == 0,
			$"git {string.Join(' ', arguments)} failed: {result.StandardOutput}{result.StandardError}");
	}

	private static ProcessStartInfo CreateGitStartInfo(
		string? workingDirectory,
		IReadOnlyList<string> arguments)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = "git",
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		if (workingDirectory is not null)
			startInfo.WorkingDirectory = workingDirectory;
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		string[] repositoryOverrides =
		[
			"GIT_DIR",
			"GIT_WORK_TREE",
			"GIT_INDEX_FILE",
			"GIT_OBJECT_DIRECTORY",
			"GIT_ALTERNATE_OBJECT_DIRECTORIES",
			"GIT_COMMON_DIR",
			"GIT_NAMESPACE"
		];
		foreach (var variable in repositoryOverrides)
		{
			startInfo.Environment.Remove(variable);
		}
		startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
		return startInfo;
	}

	private static async Task<VersionProcessResult> RunAsync(
		string application,
		IReadOnlyList<string> arguments,
		IReadOnlyDictionary<string, string?> environment,
		string? workingDirectory,
		string? standardInput,
		CancellationToken cancellationToken)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = application,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = standardInput is not null,
			WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
		};
		if (standardInput is not null)
			startInfo.StandardInputEncoding = new UTF8Encoding(false);
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		foreach (var entry in environment)
		{
			if (entry.Value is null)
				startInfo.Environment.Remove(entry.Key);
			else
				startInfo.Environment[entry.Key] = entry.Value;
		}

		using var process = new Process { StartInfo = startInfo };
		Assert.True(process.Start(), $"Could not start {application}.");
		if (standardInput is not null)
		{
			await process.StandardInput.WriteAsync(standardInput);
			process.StandardInput.Close();
		}
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
			cancellationToken);
		timeout.CancelAfter(ProcessTimeout);
		try
		{
			var standardOutputTask =
				process.StandardOutput.ReadToEndAsync(timeout.Token);
			var standardErrorTask =
				process.StandardError.ReadToEndAsync(timeout.Token);
			await process.WaitForExitAsync(timeout.Token);
			return new VersionProcessResult(
				process.ExitCode,
				await standardOutputTask,
				await standardErrorTask);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
				await process.WaitForExitAsync(CancellationToken.None);
			}

			throw new TimeoutException(
				$"Published direct command did not exit within {ProcessTimeout}.");
		}
	}

	private static string AssertCleanVersionResult(
		VersionProcessResult result,
		string? expectedOutput = null)
	{
		Assert.True(
			result.ExitCode == 0,
			$"Published --version exited with {result.ExitCode}. stderr=[{result.StandardError}]");
		Assert.Empty(result.StandardError);
		Assert.DoesNotContain('\u001b', result.StandardOutput);
		var normalized = result.StandardOutput.ReplaceLineEndings("\n");
		var lines = normalized.Split(
			'\n',
			StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		Assert.Single(lines);
		Assert.True(
			Version.TryParse(lines[0], out _),
			$"Published --version returned an invalid version: [{result.StandardOutput}]");
		Assert.EndsWith("\n", normalized, StringComparison.Ordinal);
		if (expectedOutput is not null)
			Assert.Equal(expectedOutput, normalized);
		return normalized;
	}

	private static ExtractedFile[] CaptureExtractionSnapshot(string extractionRoot)
	{
		Assert.True(
			Directory.Exists(extractionRoot),
			$"Bundle extraction root was not created: {extractionRoot}");
		var files = Directory
			.EnumerateFiles(extractionRoot, "*", SearchOption.AllDirectories)
			.Select(path => new ExtractedFile(
				Path.GetRelativePath(extractionRoot, path)
					.Replace('\\', '/'),
				new FileInfo(path).Length))
			.OrderBy(static file => file.RelativePath, StringComparer.Ordinal)
			.ToArray();
		Assert.NotEmpty(files);
		return files;
	}

	private sealed record VersionProcessResult(
		int ExitCode,
		string StandardOutput,
		string StandardError);

	private sealed record ExtractedFile(string RelativePath, long Length);
}
