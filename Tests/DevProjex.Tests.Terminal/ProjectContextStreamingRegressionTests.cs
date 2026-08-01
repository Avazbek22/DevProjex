using System.Collections;
using System.Diagnostics;
using System.Xml.Linq;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Tests.Terminal;

public sealed class ProjectContextStreamingRegressionTests
{
	[Fact]
	public async Task DocumentServiceRejectsUnknownFormatBeforeWritingAnyBytes()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/app.cs", "class App {}\n");
		var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var plan = await services.ContextFactory.BuildAsync(
			project,
			new ProjectSelectionSpec(
				GitMode: GitFilteringMode.None,
				Exclusions: []),
			cancellationToken: TestContext.Current.CancellationToken);
		var invalidFormat = (ProjectContextDocumentFormat)int.MaxValue;
		await using var destination = new WriteOnlyNonSeekableStream();

		var streamingException = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
			services.ContextDocumentService.WriteCompleteAsync(
				plan,
				ProjectContextView.TreeContent,
				invalidFormat,
				destination,
				TestContext.Current.CancellationToken));
		var boundedException = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
			services.ContextDocumentService.BuildAsync(
				plan,
				ProjectContextView.TreeContent,
				invalidFormat,
				new ProjectContextDocumentLimits(),
				TestContext.Current.CancellationToken));

		Assert.Equal("format", streamingException.ParamName);
		Assert.Equal("format", boundedException.ParamName);
		Assert.Empty(destination.ToArray());
	}

	[Fact]
	public async Task BoundedDocumentBuildRequiresExplicitNonNullLimits()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/app.cs", "class App {}\n");
		var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var plan = await services.ContextFactory.BuildAsync(
			project,
			new ProjectSelectionSpec(
				GitMode: GitFilteringMode.None,
				Exclusions: []),
			cancellationToken: TestContext.Current.CancellationToken);
		var limitsParameter = Assert.Single(
			typeof(ProjectContextDocumentService)
				.GetMethod(nameof(ProjectContextDocumentService.BuildAsync))!
				.GetParameters(),
			static parameter => parameter.Name == "limits");

		Assert.False(limitsParameter.IsOptional);
		Assert.False(limitsParameter.HasDefaultValue);
		var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
			services.ContextDocumentService.BuildAsync(
				plan,
				ProjectContextView.TreeContent,
				ProjectContextDocumentFormat.Text,
				null!,
				TestContext.Current.CancellationToken));
		Assert.Equal("limits", exception.ParamName);
	}

	[Fact]
	public async Task DocumentServiceRejectsUnknownViewBeforeWritingAnyBytes()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/app.cs", "class App {}\n");
		var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var plan = await services.ContextFactory.BuildAsync(
			project,
			new ProjectSelectionSpec(
				GitMode: GitFilteringMode.None,
				Exclusions: []),
			cancellationToken: TestContext.Current.CancellationToken);
		await using var destination = new WriteOnlyNonSeekableStream();

		var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
			services.ContextDocumentService.WriteCompleteAsync(
				plan,
				(ProjectContextView)int.MaxValue,
				ProjectContextDocumentFormat.Text,
				destination,
				TestContext.Current.CancellationToken));

		Assert.Equal("view", exception.ParamName);
		Assert.Empty(destination.ToArray());
	}

	[Theory]
	[InlineData(ProjectContextDocumentFormat.Text)]
	[InlineData(ProjectContextDocumentFormat.Markdown)]
	[InlineData(ProjectContextDocumentFormat.Json)]
	[InlineData(ProjectContextDocumentFormat.Xml)]
	public async Task CompleteDocumentDoesNotRequestMaterializedFileContent(
		ProjectContextDocumentFormat format)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/app.cs", "class App {}\n");
		var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var plan = await services.ContextFactory.BuildAsync(
			project,
			new ProjectSelectionSpec(
				GitMode: GitFilteringMode.None,
				Exclusions: []),
			cancellationToken: TestContext.Current.CancellationToken);
		var documentService = new ProjectContextDocumentService(
			services.TreeExportService,
			new MaterializedContentRejectingAnalyzer());
		await using var destination = new WriteOnlyNonSeekableStream();

		await documentService.WriteCompleteAsync(
			plan,
			ProjectContextView.Content,
			format,
			destination,
			TestContext.Current.CancellationToken);

		Assert.NotEmpty(destination.ToArray());
	}

	[Theory]
	[InlineData(ProjectContextDocumentFormat.Text)]
	[InlineData(ProjectContextDocumentFormat.Markdown)]
	[InlineData(ProjectContextDocumentFormat.Json)]
	[InlineData(ProjectContextDocumentFormat.Xml)]
	public async Task CompleteDocumentSupportsWriteOnlyNonSeekableDestination(
		ProjectContextDocumentFormat format)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/app.cs", "class App {}\n");
		var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var plan = await services.ContextFactory.BuildAsync(
			project,
			new ProjectSelectionSpec(
				GitMode: GitFilteringMode.None,
				Exclusions: []),
			cancellationToken: TestContext.Current.CancellationToken);
		await using var destination = new WriteOnlyNonSeekableStream();

		await services.ContextDocumentService.WriteCompleteAsync(
			plan,
			ProjectContextView.TreeContent,
			format,
			destination,
			TestContext.Current.CancellationToken);

		var bytes = destination.ToArray();
		Assert.NotEmpty(bytes);
		Assert.False(destination.CanRead);
		Assert.False(destination.CanSeek);
		if (format == ProjectContextDocumentFormat.Json)
		{
			using var document = JsonDocument.Parse(bytes);
			Assert.Equal(
				"devprojex-context",
				document.RootElement.GetProperty("kind").GetString());
		}
		else if (format == ProjectContextDocumentFormat.Xml)
		{
			var document = XDocument.Parse(Encoding.UTF8.GetString(bytes));
			Assert.Equal("devprojexContext", document.Root!.Name.LocalName);
		}
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public async Task StreamingTreeEmitterPreservesTheExistingStringContract(
		bool plain,
		bool includeFinalLineEnding)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/app.cs", "class App {}\n");
		workspace.WriteFile("project/README.md", "# App\n");
		var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var plan = await services.ContextFactory.BuildAsync(
			project,
			new ProjectSelectionSpec(
				GitMode: GitFilteringMode.None,
				Exclusions: []),
			cancellationToken: TestContext.Current.CancellationToken);
		var expected = plain
			? services.TreeExportService.BuildFullTreePlain(
				plan.SourceRoot,
				plan.ProjectedTree)
			: services.TreeExportService.BuildFullTree(
				plan.SourceRoot,
				plan.ProjectedTree,
				TreeTextFormat.Ascii);
		if (!includeFinalLineEnding)
			expected = expected.TrimEnd('\r', '\n');
		await using var writer = new StringWriter();

		if (plain)
		{
			await services.TreeExportService.WriteFullTreePlainAsync(
				writer,
				plan.SourceRoot,
				plan.ProjectedTree,
				includeFinalLineEnding: includeFinalLineEnding,
				cancellationToken: TestContext.Current.CancellationToken);
		}
		else
		{
			await services.TreeExportService.WriteFullTreeAsync(
				writer,
				plan.SourceRoot,
				plan.ProjectedTree,
				includeFinalLineEnding: includeFinalLineEnding,
				cancellationToken: TestContext.Current.CancellationToken);
		}

		Assert.Equal(expected, writer.ToString());
	}

	[Theory]
	[InlineData(ProjectContextDocumentFormat.Text)]
	[InlineData(ProjectContextDocumentFormat.Markdown)]
	public async Task CompleteTextualTreeStartsWritingBeforeEnumeratingEveryNodeAndUsesBoundedChunks(
		ProjectContextDocumentFormat format)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/seed.txt", string.Empty);
		var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var plan = await services.ContextFactory.BuildAsync(
			project,
			new ProjectSelectionSpec(
				GitMode: GitFilteringMode.None,
				Exclusions: []),
			cancellationToken: TestContext.Current.CancellationToken);
		const int nodeCount = 50_000;
		var children = new LazyWideTreeChildren(plan.SourceRoot, nodeCount);
		var wideRoot = plan.ProjectedTree with { Children = children };
		var widePlan = plan with
		{
			EffectiveTree = wideRoot,
			ProjectedTree = wideRoot,
			IncludedFiles = [],
			IncludedFolders = []
		};
		await using var destination = new CountingDiscardStream(
			() => children.AccessCount);

		await services.ContextDocumentService.WriteCompleteAsync(
			widePlan,
			ProjectContextView.Tree,
			format,
			destination,
			TestContext.Current.CancellationToken);

		Assert.InRange(destination.AccessCountAtFirstWrite, 0, nodeCount - 1);
		Assert.True(
			destination.WriteCount > 1,
			"Large tree output must reach the destination incrementally.");
		Assert.InRange(destination.MaximumWriteSize, 1, 32 * 1024);
		Assert.True(destination.TotalBytesWritten > 1_000_000);
		Assert.False(destination.CanRead);
		Assert.False(destination.CanSeek);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CompleteMarkdownTreePreservesExactFenceAndFinalNewlineContract(
		bool plain)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/name-````.txt", string.Empty);
		var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var plan = await services.ContextFactory.BuildAsync(
			project,
			new ProjectSelectionSpec(
				GitMode: GitFilteringMode.None,
				Exclusions: []),
			cancellationToken: TestContext.Current.CancellationToken);
		var projectName = Path.GetFileName(
			Path.TrimEndingDirectorySeparator(plan.SourceRoot));
		var tree = (plain
				? services.TreeExportService.BuildFullTreePlain(
					plan.SourceRoot,
					plan.ProjectedTree,
					plan.SourceRoot,
					projectName)
				: services.TreeExportService.BuildFullTree(
					plan.SourceRoot,
					plan.ProjectedTree,
					TreeTextFormat.Ascii,
					plan.SourceRoot,
					projectName))
			.TrimEnd('\r', '\n');
		var fence = new string(
			'`',
			Math.Max(3, FindLongestBacktickRun(tree) + 1));
		var expected =
			$"# {projectName}{Environment.NewLine}{Environment.NewLine}" +
			$"## Project tree{Environment.NewLine}{Environment.NewLine}" +
			$"{fence}text{Environment.NewLine}" +
			$"{tree}{Environment.NewLine}{fence}";
		await using var destination = new WriteOnlyNonSeekableStream();

		await services.ContextDocumentService.WriteCompleteAsync(
			plan,
			ProjectContextView.Tree,
			ProjectContextDocumentFormat.Markdown,
			destination,
			TestContext.Current.CancellationToken,
			plain);

		Assert.Equal(
			Encoding.UTF8.GetBytes(expected),
			destination.ToArray());
	}

	[Theory]
	[InlineData(ProjectContextDocumentFormat.Text)]
	[InlineData(ProjectContextDocumentFormat.Markdown)]
	[InlineData(ProjectContextDocumentFormat.Json)]
	[InlineData(ProjectContextDocumentFormat.Xml)]
	public async Task CompleteDocumentPreservesContentAcrossStreamingSegments(
		ProjectContextDocumentFormat format)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var content =
			new string('a', 8_191) +
			"😀" +
			"`````" +
			new string('z', 8_191) +
			"\r\n\r\n";
		workspace.WriteFile("project/src/chunk.txt", content);
		var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var plan = await services.ContextFactory.BuildAsync(
			project,
			new ProjectSelectionSpec(
				GitMode: GitFilteringMode.None,
				Exclusions: []),
			cancellationToken: TestContext.Current.CancellationToken);
		await using var destination = new WriteOnlyNonSeekableStream();

		await services.ContextDocumentService.WriteCompleteAsync(
			plan,
			ProjectContextView.Content,
			format,
			destination,
			TestContext.Current.CancellationToken);

		var payload = Encoding.UTF8.GetString(destination.ToArray());
		switch (format)
		{
			case ProjectContextDocumentFormat.Text:
				Assert.Equal(
					$"src/chunk.txt:{Environment.NewLine}{Environment.NewLine}" +
					content.TrimEnd('\r', '\n'),
					payload);
				break;
			case ProjectContextDocumentFormat.Markdown:
				Assert.Contains(content, payload, StringComparison.Ordinal);
				Assert.Contains("``````txt", payload, StringComparison.Ordinal);
				break;
			case ProjectContextDocumentFormat.Json:
				using (var document = JsonDocument.Parse(payload))
				{
					var file = Assert.Single(
						document.RootElement.GetProperty("files").EnumerateArray());
					Assert.Equal(content, file.GetProperty("content").GetString());
				}
				break;
			case ProjectContextDocumentFormat.Xml:
				var xml = XDocument.Parse(payload);
				var element = Assert.Single(xml.Root!.Element("files")!.Elements("file"));
				Assert.Equal(
					content.Replace("\r\n", "\n", StringComparison.Ordinal),
					element.Element("content")?.Value);
				break;
		}
	}

	[Fact]
	public async Task MarkdownUsesOneFileSnapshotWhenSameLengthRewriteAddsBackticks()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		const string originalContent = "plain-content";
		var rewrittenContent =
			"````" +
			new string('x', originalContent.Length - 4);
		var path = workspace.WriteFile("project/source.txt", originalContent);
		var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var plan = await services.ContextFactory.BuildAsync(
			project,
			new ProjectSelectionSpec(
				GitMode: GitFilteringMode.None,
				Exclusions: []),
			cancellationToken: TestContext.Current.CancellationToken);
		var analyzer = new ReplacingBetweenPassesAnalyzer(
			path,
			rewrittenContent);
		var documentService = new ProjectContextDocumentService(
			services.TreeExportService,
			analyzer);
		await using var destination = new WriteOnlyNonSeekableStream();

		await documentService.WriteCompleteAsync(
			plan,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Markdown,
			destination,
			TestContext.Current.CancellationToken);

		var payload = Encoding.UTF8.GetString(destination.ToArray());
		Assert.Contains(originalContent, payload, StringComparison.Ordinal);
		Assert.DoesNotContain(rewrittenContent, payload, StringComparison.Ordinal);
		Assert.Contains(
			$"```txt{Environment.NewLine}{originalContent}{Environment.NewLine}```",
			payload,
			StringComparison.Ordinal);
		Assert.Equal(rewrittenContent, File.ReadAllText(path));
	}

	[Fact]
	public async Task GrowingFileCannotBeSilentlyTruncatedToPreflightCharacterCount()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		const string originalContent = "old";
		const string rewrittenContent = "replacement content grew";
		var path = workspace.WriteFile("project/source.txt", originalContent);
		var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var plan = await services.ContextFactory.BuildAsync(
			project,
			new ProjectSelectionSpec(
				GitMode: GitFilteringMode.None,
				Exclusions: []),
			cancellationToken: TestContext.Current.CancellationToken);
		var documentService = new ProjectContextDocumentService(
			services.TreeExportService,
			new ReplacingBetweenPassesAnalyzer(path, rewrittenContent));
		await using var destination = new WriteOnlyNonSeekableStream();

		await documentService.WriteCompleteAsync(
			plan,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Json,
			destination,
			TestContext.Current.CancellationToken);

		using var document = JsonDocument.Parse(destination.ToArray());
		var file = Assert.Single(
			document.RootElement.GetProperty("files").EnumerateArray());
		Assert.Equal(originalContent, file.GetProperty("content").GetString());
		Assert.Equal(rewrittenContent, File.ReadAllText(path));
	}

	[Fact]
	public async Task EmptyToNonEmptyRewriteKeepsClassificationAndContentOnOneSnapshot()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		const string rewrittenContent = "became nonempty";
		var path = workspace.WriteFile("project/source.txt", string.Empty);
		var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var plan = await services.ContextFactory.BuildAsync(
			project,
			new ProjectSelectionSpec(
				GitMode: GitFilteringMode.None,
				Exclusions: []),
			cancellationToken: TestContext.Current.CancellationToken);
		var analyzer = new ReplacingBetweenPassesAnalyzer(
			path,
			rewrittenContent);
		var documentService = new ProjectContextDocumentService(
			services.TreeExportService,
			analyzer);
		await using var destination = new WriteOnlyNonSeekableStream();

		await documentService.WriteCompleteAsync(
			plan,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Json,
			destination,
			TestContext.Current.CancellationToken);

		using var document = JsonDocument.Parse(destination.ToArray());
		var file = Assert.Single(
			document.RootElement.GetProperty("files").EnumerateArray());
		Assert.Equal("text", file.GetProperty("classification").GetString());
		Assert.Equal(string.Empty, file.GetProperty("content").GetString());
		Assert.Equal(1, analyzer.SnapshotOpenCalls);
		Assert.Equal(rewrittenContent, File.ReadAllText(path));
	}

	[Fact]
	public async Task FileSnapshotCopiesLargeTextInBoundedChunksAndObservesCancellation()
	{
		using var workspace = new TemporaryDirectory();
		var path = Path.Combine(workspace.Path, "large.txt");
		const int fileSize = 4 * 1024 * 1024;
		await WriteLargeTextFileAsync(
			path,
			fileSize,
			TestContext.Current.CancellationToken);
		var analyzer = new FileContentAnalyzer();
		await using var snapshot = await analyzer.OpenCompleteSnapshotAsync(
			path,
			TestContext.Current.CancellationToken);
		Assert.Equal(
			FileContentClassification.Text,
			snapshot.Result.Classification);
		Assert.Equal(fileSize, snapshot.Result.Metrics?.CharCount);
		var copiedCharacters = 0;
		var maximumChunk = 0;
		using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
			await snapshot.CopyTextToAsync(
				fileSize,
				(chunk, _) =>
				{
					copiedCharacters += chunk.Length;
					maximumChunk = Math.Max(maximumChunk, chunk.Length);
					cancellationSource.Cancel();
					return ValueTask.CompletedTask;
				},
				cancellationSource.Token));

		Assert.InRange(copiedCharacters, 1, 8_192);
		Assert.InRange(maximumChunk, 1, 8_192);
	}

	[Fact]
	public async Task FileSnapshotPreservesOriginalContentAfterPathReplacement()
	{
		using var workspace = new TemporaryDirectory();
		var originalContent = new string('x', 32 * 1024);
		var path = workspace.WriteFile("source.txt", originalContent);
		var analyzer = new FileContentAnalyzer();
		await using var snapshot = await analyzer.OpenCompleteSnapshotAsync(
			path,
			TestContext.Current.CancellationToken);
		Assert.Equal(
			FileContentClassification.Text,
			snapshot.Result.Classification);
		var replacementPath = workspace.WriteFile("replacement.txt", "short");
		File.Replace(replacementPath, path, destinationBackupFileName: null);
		var copied = new StringBuilder(originalContent.Length);

		await snapshot.CopyTextToAsync(
			snapshot.Result.Metrics!.CharCount,
			(chunk, _) =>
			{
				copied.Append(chunk.Span);
				return ValueTask.CompletedTask;
			},
			TestContext.Current.CancellationToken);

		Assert.Equal(originalContent, copied.ToString());
		Assert.Equal("short", File.ReadAllText(path));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task AtomicContextOutputRemovesStagingAfterInterruptedWrite(
		bool cancel)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var source = Path.Combine(project, "large.txt");
		await WriteLargeTextFileAsync(
			source,
			2 * 1024 * 1024,
			TestContext.Current.CancellationToken);
		var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var plan = await services.ContextFactory.BuildAsync(
			project,
			new ProjectSelectionSpec(
				GitMode: GitFilteringMode.None,
				Exclusions: []),
			cancellationToken: TestContext.Current.CancellationToken);
		var outputDirectory = workspace.CreateDirectory("output");
		var destination = Path.Combine(outputDirectory, "context.txt");
		using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);

		Task WriteAsync(Stream stream, CancellationToken token)
		{
			var interrupted = new InterruptingWriteStream(
				stream,
				bytesBeforeInterruption: 16 * 1024,
				cancel ? cancellationSource : null);
			return services.ContextDocumentService.WriteCompleteAsync(
				plan,
				ProjectContextView.Content,
				ProjectContextDocumentFormat.Text,
				interrupted,
				token);
		}

		if (cancel)
		{
			await Assert.ThrowsAnyAsync<OperationCanceledException>(
				() => AtomicOutputWriter.WriteAsync(
					destination,
					overwrite: false,
					WriteAsync,
					cancellationSource.Token));
		}
		else
		{
			await Assert.ThrowsAsync<IOException>(
				() => AtomicOutputWriter.WriteAsync(
					destination,
					overwrite: false,
					WriteAsync,
					cancellationSource.Token));
		}

		Assert.False(File.Exists(destination));
		Assert.Empty(Directory.EnumerateFiles(outputDirectory, ".*.tmp"));
	}

	[Fact]
	public async Task AtomicOutputRemainsBoundToResolvedParentWhenAliasIsRetargeted()
	{
		if (OperatingSystem.IsWindows())
			Assert.Skip("This deterministic symbolic-link race test runs on Unix hosts.");

		using var workspace = new TemporaryDirectory();
		var source = workspace.CreateDirectory("source");
		var safeTarget = workspace.CreateDirectory("safe-target");
		var alias = Path.Combine(workspace.Path, "output-alias");
		try
		{
			Directory.CreateSymbolicLink(alias, safeTarget);
		}
		catch (Exception exception) when (exception is
			       UnauthorizedAccessException or
			       IOException or
			       PlatformNotSupportedException)
		{
			Assert.Skip(
				$"Directory symbolic links are unavailable: {exception.GetType().Name}.");
		}

		var requestedDestination = Path.Combine(alias, "report.txt");
		_ = ExactOutputDestinationValidator.ValidateContext(
			source,
			requestedDestination,
			overwrite: false);
		var payload = Encoding.UTF8.GetBytes("safe payload");
		try
		{
			var writtenPath = await AtomicOutputWriter.WriteAsync(
				requestedDestination,
				overwrite: false,
				async (stream, cancellationToken) =>
				{
					Directory.Delete(alias);
					Directory.CreateSymbolicLink(alias, source);
					await stream.WriteAsync(payload, cancellationToken);
				},
				TestContext.Current.CancellationToken,
				path => ExactOutputDestinationValidator.ValidateContext(
					source,
					path,
					overwrite: false));

			var safeDestination = Path.Combine(safeTarget, "report.txt");
			var physicalSafeDestination =
				ProjectCopyExportService.ResolveDestinationOutsideProject(
					source,
					safeDestination);
			Assert.Equal(physicalSafeDestination, writtenPath);
			Assert.NotEqual(Path.GetFullPath(requestedDestination), writtenPath);
			Assert.Equal(
				"safe payload",
				await File.ReadAllTextAsync(
					safeDestination,
					TestContext.Current.CancellationToken));
			Assert.False(File.Exists(Path.Combine(source, "report.txt")));
			Assert.Empty(Directory.EnumerateFiles(safeTarget, ".*.tmp"));
			Assert.Empty(Directory.EnumerateFiles(source, ".*.tmp"));
		}
		finally
		{
			Directory.Delete(alias);
		}
	}

	[Fact]
	public async Task AtomicOutputReportsStableRequestedAliasAfterPhysicalCommit()
	{
		using var workspace = new TemporaryDirectory();
		var source = workspace.CreateDirectory("source");
		var safeTarget = workspace.CreateDirectory("safe-target");
		var alias = Path.Combine(workspace.Path, "output-alias");
		try
		{
			Directory.CreateSymbolicLink(alias, safeTarget);
		}
		catch (Exception exception) when (exception is
			       UnauthorizedAccessException or
			       IOException or
			       PlatformNotSupportedException)
		{
			Assert.Skip(
				$"Directory symbolic links are unavailable: {exception.GetType().Name}.");
		}

		try
		{
			var requestedDestination = Path.Combine(alias, "report.txt");
			var payload = Encoding.UTF8.GetBytes("safe payload");

			var writtenPath = await AtomicOutputWriter.WriteAsync(
				requestedDestination,
				overwrite: false,
				(stream, cancellationToken) =>
					stream.WriteAsync(payload, cancellationToken).AsTask(),
				TestContext.Current.CancellationToken,
				path => ExactOutputDestinationValidator.ValidateContext(
					source,
					path,
					overwrite: false));

			Assert.Equal(Path.GetFullPath(requestedDestination), writtenPath);
			Assert.Equal(
				"safe payload",
				await File.ReadAllTextAsync(
					Path.Combine(safeTarget, "report.txt"),
					TestContext.Current.CancellationToken));
			Assert.Empty(Directory.EnumerateFiles(safeTarget, ".*.tmp"));
		}
		finally
		{
			Directory.Delete(alias);
		}
	}

	[Fact]
	public async Task AtomicOutputCancellationDuringFinalRevalidationDoesNotCommit()
	{
		using var workspace = new TemporaryDirectory();
		var outputDirectory = workspace.CreateDirectory("output");
		var destination = Path.Combine(outputDirectory, "context.txt");
		using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);
		var validationCount = 0;

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			AtomicOutputWriter.WriteAsync(
				destination,
				overwrite: false,
				async (stream, cancellationToken) =>
				{
					await stream.WriteAsync(
						"payload"u8.ToArray(),
						cancellationToken);
				},
				cancellationSource.Token,
				path =>
				{
					validationCount++;
					if (validationCount == 3)
						cancellationSource.Cancel();
					return path;
				}));

		Assert.Equal(3, validationCount);
		Assert.False(File.Exists(destination));
		Assert.Empty(Directory.EnumerateFiles(outputDirectory, ".*.tmp"));
	}

	[Fact]
	public async Task LargeContextProcessKeepsMemoryGrowthBelowTheSourceFileSizeEnvelope()
	{
		using var workspace = new TemporaryDirectory();
		var smallProject = workspace.CreateDirectory("small-project");
		workspace.WriteFile("small-project/input.txt", new string('s', 1024));
		var largeProject = workspace.CreateDirectory("large-project");
		var largeSource = Path.Combine(largeProject, "input.txt");
		const int largeFileSize = 128 * 1024 * 1024;
		await WriteLargeTextFileAsync(
			largeSource,
			largeFileSize,
			TestContext.Current.CancellationToken);
		var applicationAssembly = FindApplicationAssembly();
		Assert.True(
			File.Exists(applicationAssembly),
			$"Application assembly was not found: {applicationAssembly}");

		var smallResult = await RunContextProcessAsync(
			applicationAssembly,
			smallProject,
			Path.Combine(workspace.Path, "small-output.txt"),
			workspace.CreateDirectory("small-app-data"),
			TestContext.Current.CancellationToken);
		var largeOutput = Path.Combine(workspace.Path, "large-output.txt");
		var largeResult = await RunContextProcessAsync(
			applicationAssembly,
			largeProject,
			largeOutput,
			workspace.CreateDirectory("large-app-data"),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, smallResult.ExitCode);
		Assert.Empty(smallResult.StandardError);
		Assert.Equal(CommandLineExitCodes.Success, largeResult.ExitCode);
		Assert.Empty(largeResult.StandardError);
		Assert.True(new FileInfo(largeOutput).Length >= largeFileSize);
		var memoryGrowth = Math.Max(
			0,
			largeResult.PeakWorkingSetBytes - smallResult.PeakWorkingSetBytes);
		var allowedGrowth = largeFileSize + (64L * 1024 * 1024);
		Assert.True(
			memoryGrowth < allowedGrowth,
			$"Peak working-set growth {memoryGrowth:N0} bytes exceeded " +
			$"the bounded envelope {allowedGrowth:N0} bytes. " +
			$"Small={smallResult.PeakWorkingSetBytes:N0}, " +
			$"large={largeResult.PeakWorkingSetBytes:N0}.");
	}

	private static async Task<ContextProcessResult> RunContextProcessAsync(
		string applicationAssembly,
		string project,
		string output,
		string dataRoot,
		CancellationToken cancellationToken)
	{
		using var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = "dotnet",
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			}
		};
		foreach (var argument in new[]
		         {
			         applicationAssembly,
			         "export", "context", project,
			         "--view", "content",
			         "--format", "text",
			         "--git-mode", "none",
			         "--exclude", "none",
			         "--plain",
			         "--progress", "never",
			         "-o", output
		         })
		{
			process.StartInfo.ArgumentList.Add(argument);
		}
		process.StartInfo.Environment[InvocationEnvironment.TerminalHostVariable] = "1";
		process.StartInfo.Environment[InvocationEnvironment.InternalDataRootVariable] = dataRoot;
		process.StartInfo.Environment["DOTNET_NOLOGO"] = "1";

		Assert.True(process.Start());
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(90));
		var standardOutputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
		var standardErrorTask = process.StandardError.ReadToEndAsync(timeout.Token);
		var peakWorkingSetTask = ObservePeakWorkingSetAsync(process, timeout.Token);
		try
		{
			await process.WaitForExitAsync(timeout.Token);
			return new ContextProcessResult(
				process.ExitCode,
				await standardOutputTask,
				await standardErrorTask,
				await peakWorkingSetTask);
		}
		finally
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
				await process.WaitForExitAsync(CancellationToken.None);
			}
		}
	}

	private static async Task<long> ObservePeakWorkingSetAsync(
		Process process,
		CancellationToken cancellationToken)
	{
		var peakWorkingSet = 0L;
		while (!process.HasExited)
		{
			try
			{
				process.Refresh();
				peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
			}
			catch (InvalidOperationException) when (process.HasExited)
			{
				break;
			}
			await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
		}
		return peakWorkingSet;
	}

	private static string FindApplicationAssembly()
	{
		var configuration = new DirectoryInfo(
				AppContext.BaseDirectory.TrimEnd(
					Path.DirectorySeparatorChar,
					Path.AltDirectorySeparatorChar))
			.Parent?
			.Name ?? "Debug";
		return Path.Combine(
			PublishedApplicationLocator.FindRepositoryRoot(),
			"Apps",
			"Avalonia",
			"bin",
			configuration,
			"net10.0",
			"DevProjex.dll");
	}

	private static async Task WriteLargeTextFileAsync(
		string path,
		int byteCount,
		CancellationToken cancellationToken)
	{
		var buffer = new byte[64 * 1024];
		Array.Fill(buffer, (byte)'x');
		await using var destination = new FileStream(
			path,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None,
			buffer.Length,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		for (var remaining = byteCount; remaining > 0;)
		{
			var count = Math.Min(buffer.Length, remaining);
			await destination.WriteAsync(
				buffer.AsMemory(0, count),
				cancellationToken);
			remaining -= count;
		}
	}

	private static int FindLongestBacktickRun(string value)
	{
		var longest = 0;
		var current = 0;
		foreach (var character in value)
		{
			if (character == '`')
			{
				current++;
				longest = Math.Max(longest, current);
			}
			else
			{
				current = 0;
			}
		}

		return longest;
	}

	private sealed class WriteOnlyNonSeekableStream : Stream
	{
		private readonly MemoryStream _destination = new();

		public override bool CanRead => false;
		public override bool CanSeek => false;
		public override bool CanWrite => true;
		public override long Length => throw new NotSupportedException();

		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public byte[] ToArray() => _destination.ToArray();

		public override void Flush() => _destination.Flush();

		public override Task FlushAsync(CancellationToken cancellationToken) =>
			_destination.FlushAsync(cancellationToken);

		public override int Read(byte[] buffer, int offset, int count) =>
			throw new NotSupportedException();

		public override long Seek(long offset, SeekOrigin origin) =>
			throw new NotSupportedException();

		public override void SetLength(long value) =>
			throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count) =>
			_destination.Write(buffer, offset, count);

		public override void Write(ReadOnlySpan<byte> buffer) =>
			_destination.Write(buffer);

		public override Task WriteAsync(
			byte[] buffer,
			int offset,
			int count,
			CancellationToken cancellationToken) =>
			_destination.WriteAsync(buffer, offset, count, cancellationToken);

		public override ValueTask WriteAsync(
			ReadOnlyMemory<byte> buffer,
			CancellationToken cancellationToken = default) =>
			_destination.WriteAsync(buffer, cancellationToken);

		protected override void Dispose(bool disposing)
		{
			if (disposing)
				_destination.Dispose();
			base.Dispose(disposing);
		}

		public override async ValueTask DisposeAsync()
		{
			await _destination.DisposeAsync();
			GC.SuppressFinalize(this);
		}
	}

	private sealed class LazyWideTreeChildren(
		string rootPath,
		int count) : IReadOnlyList<TreeNodeDescriptor>
	{
		private int _accessCount;

		public int Count { get; } = count;
		public int AccessCount => Volatile.Read(ref _accessCount);

		public TreeNodeDescriptor this[int index]
		{
			get
			{
				ArgumentOutOfRangeException.ThrowIfNegative(index);
				ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
				Interlocked.Increment(ref _accessCount);
				var name = $"file-{index:D6}-{new string('x', 64)}.txt";
				return new TreeNodeDescriptor(
					name,
					Path.Combine(rootPath, name),
					IsDirectory: false,
					IsAccessDenied: false,
					IconKey: "file",
					Children: []);
			}
		}

		public IEnumerator<TreeNodeDescriptor> GetEnumerator()
		{
			for (var index = 0; index < Count; index++)
				yield return this[index];
		}

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class CountingDiscardStream(
		Func<int> getAccessCount) : Stream
	{
		private int _accessCountAtFirstWrite = -1;

		public int AccessCountAtFirstWrite => Volatile.Read(ref _accessCountAtFirstWrite);
		public int WriteCount { get; private set; }
		public int MaximumWriteSize { get; private set; }
		public long TotalBytesWritten { get; private set; }
		public override bool CanRead => false;
		public override bool CanSeek => false;
		public override bool CanWrite => true;
		public override long Length => throw new NotSupportedException();

		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public override void Flush()
		{
		}

		public override Task FlushAsync(CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.CompletedTask;
		}

		public override int Read(byte[] buffer, int offset, int count) =>
			throw new NotSupportedException();

		public override long Seek(long offset, SeekOrigin origin) =>
			throw new NotSupportedException();

		public override void SetLength(long value) =>
			throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count) =>
			ObserveWrite(count);

		public override void Write(ReadOnlySpan<byte> buffer) =>
			ObserveWrite(buffer.Length);

		public override Task WriteAsync(
			byte[] buffer,
			int offset,
			int count,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ObserveWrite(count);
			return Task.CompletedTask;
		}

		public override ValueTask WriteAsync(
			ReadOnlyMemory<byte> buffer,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ObserveWrite(buffer.Length);
			return ValueTask.CompletedTask;
		}

		private void ObserveWrite(int count)
		{
			Interlocked.CompareExchange(
				ref _accessCountAtFirstWrite,
				getAccessCount(),
				comparand: -1);
			WriteCount++;
			MaximumWriteSize = Math.Max(MaximumWriteSize, count);
			TotalBytesWritten += count;
		}
	}

	private sealed class InterruptingWriteStream(
		Stream inner,
		long bytesBeforeInterruption,
		CancellationTokenSource? cancellationSource) : Stream
	{
		private long _bytesWritten;

		public override bool CanRead => false;
		public override bool CanSeek => false;
		public override bool CanWrite => true;
		public override long Length => throw new NotSupportedException();

		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public override void Flush() => inner.Flush();

		public override Task FlushAsync(CancellationToken cancellationToken) =>
			inner.FlushAsync(cancellationToken);

		public override int Read(byte[] buffer, int offset, int count) =>
			throw new NotSupportedException();

		public override long Seek(long offset, SeekOrigin origin) =>
			throw new NotSupportedException();

		public override void SetLength(long value) =>
			throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count)
		{
			InterruptIfNeeded(count);
			inner.Write(buffer, offset, count);
		}

		public override void Write(ReadOnlySpan<byte> buffer)
		{
			InterruptIfNeeded(buffer.Length);
			inner.Write(buffer);
		}

		public override Task WriteAsync(
			byte[] buffer,
			int offset,
			int count,
			CancellationToken cancellationToken)
		{
			InterruptIfNeeded(count);
			return inner.WriteAsync(buffer, offset, count, cancellationToken);
		}

		public override ValueTask WriteAsync(
			ReadOnlyMemory<byte> buffer,
			CancellationToken cancellationToken = default)
		{
			InterruptIfNeeded(buffer.Length);
			return inner.WriteAsync(buffer, cancellationToken);
		}

		private void InterruptIfNeeded(int count)
		{
			if (_bytesWritten + count < bytesBeforeInterruption)
			{
				_bytesWritten += count;
				return;
			}

			if (cancellationSource is not null)
			{
				cancellationSource.Cancel();
				throw new OperationCanceledException(cancellationSource.Token);
			}

			throw new IOException("Simulated destination write failure.");
		}
	}

	private sealed class MaterializedContentRejectingAnalyzer : IFileContentAnalyzer
	{
		private readonly FileContentAnalyzer _inner = new();

		public FileContentClassification? ClassifyWithoutReading(string path) =>
			_inner.ClassifyWithoutReading(path);

		public ValueTask<FileContentReadResult> ReadClassifiedAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			throw new InvalidOperationException(
				"Complete document export must not request materialized file content.");

		public ValueTask<bool> IsTextFileAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			_inner.IsTextFileAsync(path, cancellationToken);

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			_inner.GetTextFileMetricsAsync(path, cancellationToken);

		public ValueTask<FileContentMetricsResult> GetClassifiedMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			_inner.GetClassifiedMetricsAsync(path, cancellationToken);

		public ValueTask<IFileContentSnapshot> OpenCompleteSnapshotAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			_inner.OpenCompleteSnapshotAsync(path, cancellationToken);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			throw new InvalidOperationException(
				"Complete document export must not request materialized file content.");

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			throw new InvalidOperationException(
				"Complete document export must not request materialized file content.");
	}

	private sealed class ReplacingBetweenPassesAnalyzer(
		string pathToReplace,
		string replacementContent) : IFileContentAnalyzer
	{
		private readonly FileContentAnalyzer _inner = new();
		private int _replacementPerformed;

		public int SnapshotOpenCalls { get; private set; }

		public FileContentClassification? ClassifyWithoutReading(string path) =>
			_inner.ClassifyWithoutReading(path);

		public ValueTask<FileContentReadResult> ReadClassifiedAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			_inner.ReadClassifiedAsync(path, maxSizeForFullRead, cancellationToken);

		public ValueTask<bool> IsTextFileAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			_inner.IsTextFileAsync(path, cancellationToken);

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			_inner.GetTextFileMetricsAsync(path, cancellationToken);

		public ValueTask<FileContentMetricsResult> GetClassifiedMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			_inner.GetClassifiedMetricsAsync(path, cancellationToken);

		public async ValueTask<IFileContentSnapshot> OpenCompleteSnapshotAsync(
			string path,
			CancellationToken cancellationToken = default)
		{
			SnapshotOpenCalls++;
			var snapshot = await _inner
				.OpenCompleteSnapshotAsync(path, cancellationToken)
				.ConfigureAwait(false);
			try
			{
				ReplacePath();
				return snapshot;
			}
			catch
			{
				await snapshot.DisposeAsync().ConfigureAwait(false);
				throw;
			}
		}

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			_inner.TryReadAsTextAsync(path, cancellationToken);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			_inner.TryReadAsTextAsync(path, maxSizeForFullRead, cancellationToken);

		private void ReplacePath()
		{
			if (Interlocked.Exchange(ref _replacementPerformed, 1) != 0)
				return;

			var replacementPath = pathToReplace + ".replacement";
			File.WriteAllText(
				replacementPath,
				replacementContent,
				new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			File.Replace(replacementPath, pathToReplace, destinationBackupFileName: null);
		}
	}

	private sealed record ContextProcessResult(
		int ExitCode,
		string StandardOutput,
		string StandardError,
		long PeakWorkingSetBytes);
}
