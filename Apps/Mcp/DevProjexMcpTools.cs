using System.ComponentModel;

namespace DevProjex.Mcp;

internal sealed class DevProjexMcpTools(
	McpRootRegistry roots,
	McpProjectService projects,
	McpPackRegistry packs)
{
	private const int MaximumTreeLines = 2_000;
	private const int MaximumInlinePackCharacters = 50_000;
	private const int MaximumPageLines = 1_000;
	private const int MaximumPageCharacters = 50_000;
	private readonly McpProjectOperationGate _projectOperation = new();

	[Description("List the project roots this server is allowed to read and their saved local profiles.")]
	public Task<CallToolResult> ListProjects(
		RequestContext<CallToolRequestParams> request,
		CancellationToken cancellationToken) =>
		ExecuteAsync(() =>
		{
			_ = McpJsonArguments.Create(request.Params);
			var projectItems = roots.Roots
				.Select(root => new
				{
					path = root,
					name = ResolveProjectName(root),
					type = McpProjectService.IsGitRepository(root) ? "git-repository" : "local-folder"
				})
				.ToArray();
			var profiles = roots.Roots
				.Where(projects.HasLocalProfile)
				.Select(root => new { project = root, name = "local" })
				.ToArray();
			return Task.FromResult(McpToolResults.StructuredSuccess(new { projects = projectItems, profiles }));
		});

	[Description("Return the effective project tree after built-in, gitignore, profile, and optional agent glob filters.")]
	public Task<CallToolResult> GetTree(
		RequestContext<CallToolRequestParams> request,
		CancellationToken cancellationToken) =>
		RunProjectAsync(async () =>
		{
			var arguments = McpJsonArguments.Create(
				request.Params,
				"project",
				"include_patterns",
				"exclude_patterns",
				"max_depth",
				"tracked_only");
			var plan = await projects.BuildPlanAsync(
				arguments.OptionalString("project"),
				paths: null,
				arguments.OptionalStringArray("include_patterns"),
				arguments.OptionalStringArray("exclude_patterns"),
				profile: null,
				arguments.OptionalBoolean("tracked_only", false),
				cancellationToken).ConfigureAwait(false);
			var depth = arguments.OptionalInteger("max_depth", 0, 1_000);
			var renderedTree = depth is null
				? plan.ProjectedTree
				: PruneToDepth(plan.ProjectedTree, depth.Value);
			var tree = projects.TreeExportService.BuildFullTree(
				plan.SourceRoot,
				renderedTree,
				TreeTextFormat.Ascii);
			var lines = McpTextRanges.SplitLines(tree);
			var truncated = lines.Count > MaximumTreeLines;
			var shown = truncated ? lines.Take(MaximumTreeLines).ToArray() : lines;
			var body = string.Join('\n', shown);
			if (truncated)
				body += "\n[Tree truncated at 2000 lines. Narrow include_patterns, exclude_patterns, or max_depth.]";
			return McpToolResults.TextSuccess(McpSpotlight.Wrap(body));
		}, cancellationToken);

	[Description("Measure a redacted project selection and list its ten largest text files by estimated tokens.")]
	public Task<CallToolResult> Analyze(
		RequestContext<CallToolRequestParams> request,
		IProgress<ProgressNotificationValue> progress,
		CancellationToken cancellationToken) =>
		RunProjectAsync(async () =>
		{
			var operationProgress = new McpProgressReporter(progress);
			operationProgress.Milestone(1, "selecting files");
			var arguments = SelectionArguments(request.Params);
			var detail = McpDetailPolicy.Parse(arguments.OptionalString("detail"));
			var plan = await BuildSelectionAsync(arguments, cancellationToken).ConfigureAwait(false);
			operationProgress.Milestone(
				10,
				$"scanning files {plan.IncludedFiles.Count}/{plan.IncludedFiles.Count}");
			var effectiveDetail = projects.ResolveDetail(plan, detail);
			operationProgress.Milestone(11, $"transforming content 0/{plan.IncludedFiles.Count}");
			await using var prepared = await projects.PrepareAsync(
					plan,
					detail,
					operationProgress.Measure("transforming content", 12, 59),
					cancellationToken)
				.ConfigureAwait(false);
			operationProgress.Milestone(
				60,
				$"transforming content {plan.IncludedFiles.Count}/{plan.IncludedFiles.Count}");
			var analyzer = projects.CreatePreparedAnalyzer(prepared);
			operationProgress.Milestone(61, $"analyzing content 0/{plan.IncludedFiles.Count}");
			var metrics = await ProjectContentMetricsCalculator
				.CalculateAsync(
					analyzer,
					plan.IncludedFiles,
					operationProgress.Measure("analyzing content", 62, 89),
					cancellationToken)
				.ConfigureAwait(false);
			operationProgress.Milestone(
				90,
				$"analyzing content {plan.IncludedFiles.Count}/{plan.IncludedFiles.Count}");
			var largest = new List<FileWeight>();
			var rankedFiles = 0;
			var rankingProgress = operationProgress.Measure("ranking files", 91, 98);
			foreach (var file in plan.IncludedFiles)
			{
				var result = await analyzer.GetClassifiedMetricsAsync(file, cancellationToken).ConfigureAwait(false);
				rankingProgress.Report(new ProjectCopyExportProgress(
					++rankedFiles,
					plan.IncludedFiles.Count,
					BytesWritten: 0,
					Percentage: plan.IncludedFiles.Count == 0
						? 100d
						: rankedFiles * 100d / plan.IncludedFiles.Count));
				if (result.Metrics is not { } fileMetrics || !result.IsText)
					continue;
				largest.Add(new FileWeight(
					McpProjectService.ToRelative(plan.SourceRoot, file),
					CodeCompressionSnapshot.EstimateTokens(fileMetrics.CharCount)));
			}
			operationProgress.Milestone(
				99,
				$"ranking files {plan.IncludedFiles.Count}/{plan.IncludedFiles.Count}");
			var top = largest
				.OrderByDescending(static item => item.Tokens)
				.ThenBy(static item => item.Path, StringComparer.Ordinal)
				.Take(10)
				.Select(static item => new { path = item.Path, tokens = item.Tokens })
				.ToArray();
			var envelope = new
			{
				files = plan.IncludedFiles.Count,
				characters = metrics.Chars,
				tokens = metrics.Tokens,
				detail = effectiveDetail.Token,
				topFiles = top
			};
			operationProgress.Milestone(100, "building analysis");
			return McpToolResults.StructuredSuccess(envelope);
		}, cancellationToken);

	[Description("Build an exact redacted DevProjex context export. Large packs expire when this server process exits.")]
	public Task<CallToolResult> PackContext(
		RequestContext<CallToolRequestParams> request,
		IProgress<ProgressNotificationValue> progress,
		CancellationToken cancellationToken) =>
		RunProjectAsync(async () =>
		{
			var operationProgress = new McpProgressReporter(progress);
			operationProgress.Milestone(1, "selecting files");
			var arguments = McpJsonArguments.Create(
				request.Params,
				"project",
				"paths",
				"include_patterns",
				"exclude_patterns",
				"profile",
				"view",
				"format",
				"detail",
				"tracked_only");
			var detail = McpDetailPolicy.Parse(arguments.OptionalString("detail"));
			var plan = await BuildSelectionAsync(arguments, cancellationToken).ConfigureAwait(false);
			operationProgress.Milestone(
				10,
				$"scanning files {plan.IncludedFiles.Count}/{plan.IncludedFiles.Count}");
			var effectiveDetail = projects.ResolveDetail(plan, detail);
			plan = projects.ApplyDetail(plan, effectiveDetail);
			var view = ParseView(arguments.OptionalString("view") ?? "tree-content");
			var format = ParseFormat(arguments.OptionalString("format") ?? "markdown");
			var transformedFileCount = view == ProjectContextView.Tree ? 0 : plan.IncludedFiles.Count;
			operationProgress.Milestone(11, $"transforming content 0/{transformedFileCount}");
			await using var prepared = view == ProjectContextView.Tree
				? null
				: await projects.PrepareAsync(
						plan,
						McpDetailLevel.Full,
						operationProgress.Measure("transforming content", 12, 59),
						cancellationToken)
					.ConfigureAwait(false);
			operationProgress.Milestone(
				60,
				$"transforming content {transformedFileCount}/{transformedFileCount}");
			var writtenFileCount = view == ProjectContextView.Tree ? 0 : plan.IncludedFiles.Count;
			operationProgress.Milestone(61, $"writing pack 0/{writtenFileCount}");
			var pack = await packs.CreateAsync(
				async (stream, token) =>
				{
					var writeProgress = operationProgress.Measure("writing pack", 62, 99);
					if (prepared is null)
					{
						await projects.DocumentService.WriteCompleteAsync(
								plan,
								view,
								format,
								stream,
								token,
								plain: false,
								useUnifiedContentHeaders: true,
								writeProgress: writeProgress)
							.ConfigureAwait(false);
						return;
					}

					await projects.DocumentService.WritePreparedCompleteAsync(
							plan,
							view,
							format,
							stream,
							prepared,
							token,
							plain: false,
							useUnifiedContentHeaders: true,
							writeProgress)
						.ConfigureAwait(false);
				},
				cancellationToken).ConfigureAwait(false);
			if (pack.Characters <= MaximumInlinePackCharacters)
			{
				var content = await File.ReadAllTextAsync(pack.Path, cancellationToken).ConfigureAwait(false);
				packs.Remove(pack.Id);
				operationProgress.Milestone(
					100,
					$"writing pack {writtenFileCount}/{writtenFileCount}");
				return McpToolResults.TextSuccess(McpSpotlight.Wrap(content), advertiseLargeResult: true);
			}

			var tree = projects.TreeExportService.BuildFullTree(
				plan.SourceRoot,
				plan.ProjectedTree,
				TreeTextFormat.Ascii,
				projects.ResolveProtectedDocumentRoot(plan));
			var treeLines = McpTextRanges.SplitLines(tree);
			if (treeLines.Count > MaximumTreeLines)
				tree = string.Join('\n', treeLines.Take(MaximumTreeLines)) + "\n[Tree truncated at 2000 lines.]";
			var message = $"Pack stored as '{pack.Id}' ({pack.Characters} characters). " +
			              "Call read_pack with this pack_id to read ranges, or search_project to locate source content.\n" +
			              McpSpotlight.Wrap(tree);
			operationProgress.Milestone(
				100,
				$"writing pack {writtenFileCount}/{writtenFileCount}");
			return McpToolResults.TextSuccess(message, advertiseLargeResult: true);
		}, cancellationToken);

	[Description("Read a 1-based line range from a pack created by this server session.")]
	public Task<CallToolResult> ReadPack(
		RequestContext<CallToolRequestParams> request,
		CancellationToken cancellationToken) =>
		ExecuteAsync(async () =>
		{
			var arguments = McpJsonArguments.Create(request.Params, "pack_id", "start_line", "end_line");
			var packId = arguments.RequiredString("pack_id");
			var start = arguments.OptionalInteger("start_line", 1, int.MaxValue);
			var end = arguments.OptionalInteger("end_line", 1, int.MaxValue);
			var pack = packs.ResolveDocument(packId);
			var page = await ReadFilePageAsync(
					pack.Path,
					pack.Lines,
					start,
					end,
					cancellationToken)
				.ConfigureAwait(false);
			var text = page.Text;
			if (page.IsTruncated)
			{
				text += $"\n[Showing lines {page.StartLine}-{page.EndLine} of {page.TotalLines}; " +
				        $"continue with start_line={page.EndLine + 1}.]";
			}
			if (page.CharacterLimitReached)
				text += "\n[The current line exceeded the 50000-character response cap; use search_project to narrow the source.]";
			return McpToolResults.TextSuccess(
				McpSpotlight.Wrap(text),
				advertiseLargeResult: true);
		});

	[Description("Search already-redacted project text with a timeout-bounded .NET regular expression.")]
	public Task<CallToolResult> SearchProject(
		RequestContext<CallToolRequestParams> request,
		CancellationToken cancellationToken) =>
		RunProjectAsync(async () =>
		{
			var arguments = McpJsonArguments.Create(
				request.Params,
				"project",
				"pattern",
				"include_patterns",
				"exclude_patterns",
				"context_lines",
				"ignore_case",
				"max_results",
				"tracked_only");
			var pattern = arguments.RequiredString("pattern", allowWhitespace: true);
			var contextLines = arguments.OptionalInteger("context_lines", 0, 20) ?? 2;
			var ignoreCase = arguments.OptionalBoolean("ignore_case", true);
			var maximumResults = arguments.OptionalInteger("max_results", 1, 200) ?? 50;
			var regex = new McpSearchRegex(pattern, ignoreCase);

			var plan = await projects.BuildPlanAsync(
				arguments.OptionalString("project"),
				paths: null,
				arguments.OptionalStringArray("include_patterns"),
				arguments.OptionalStringArray("exclude_patterns"),
				profile: null,
				arguments.OptionalBoolean("tracked_only", false),
				cancellationToken).ConfigureAwait(false);
			await using var prepared = await projects.PrepareAsync(plan, cancellationToken).ConfigureAwait(false);
			var analyzer = projects.CreatePreparedAnalyzer(prepared);
			var output = new StringBuilder();
			var totalMatches = 0;
			foreach (var file in plan.IncludedFiles)
			{
				var content = await analyzer.TryReadAsTextAsync(file, cancellationToken).ConfigureAwait(false);
				if (content is null)
					continue;
				var lines = McpTextRanges.SplitLines(content.Content);
				for (var index = 0; index < lines.Count; index++)
				{
					var matches = regex.IsMatch(lines[index]);
					if (!matches)
						continue;
					totalMatches++;
					if (totalMatches > maximumResults)
						continue;
					AppendSearchResult(
						output,
						McpProjectService.ToRelative(plan.SourceRoot, file),
						lines,
						index,
						contextLines);
				}
			}
			var shown = Math.Min(totalMatches, maximumResults);
			if (totalMatches > shown)
				output.AppendLine($"[{totalMatches - shown} additional matches not shown; narrow the pattern or filters.]");
			return McpToolResults.TextSuccess(McpSpotlight.Wrap(output.ToString().TrimEnd()));
		}, cancellationToken);

	[Description("Read redacted text from one effective project file using an optional 1-based line range.")]
	public Task<CallToolResult> GetFile(
		RequestContext<CallToolRequestParams> request,
		CancellationToken cancellationToken) =>
		RunProjectAsync(async () =>
		{
			var arguments = McpJsonArguments.Create(
				request.Params,
				"project",
				"path",
				"start_line",
				"end_line");
			var plan = await projects.BuildPlanAsync(
				arguments.OptionalString("project"),
				paths: null,
				includePatterns: null,
				excludePatterns: null,
				profile: null,
				trackedOnly: false,
				cancellationToken).ConfigureAwait(false);
			var file = projects.ResolveFile(plan, arguments.RequiredString("path", allowWhitespace: true));
			await using var prepared = await projects.PrepareAsync(plan with { IncludedFiles = [file] }, cancellationToken)
				.ConfigureAwait(false);
			var content = await projects.CreatePreparedAnalyzer(prepared)
				.TryReadAsTextAsync(file, cancellationToken)
				.ConfigureAwait(false);
			if (content is null)
			{
				throw new McpToolException(
					McpErrorCodes.PayloadTruncated,
					$"{McpErrorCodes.PayloadTruncated}: file content is binary, unsupported, or exceeds the redaction scan limit and cannot be returned safely.");
			}
			var lines = McpTextRanges.SplitLines(content.Content);
			var page = McpTextRanges.Slice(
				lines,
				arguments.OptionalInteger("start_line", 1, int.MaxValue),
				arguments.OptionalInteger("end_line", 1, int.MaxValue),
				MaximumPageLines,
				int.MaxValue);
			var text = page.Text;
			if (page.IsTruncated)
				text += $"\n[Showing lines {page.StartLine}-{page.EndLine} of {page.TotalLines}; continue with start_line={page.EndLine + 1}.]";
			return McpToolResults.TextSuccess(McpSpotlight.Wrap(text));
		}, cancellationToken);

	private McpJsonArguments SelectionArguments(CallToolRequestParams request) =>
		McpJsonArguments.Create(
			request,
			"project",
			"paths",
			"include_patterns",
			"exclude_patterns",
			"profile",
			"detail",
			"tracked_only");

	private Task<ProjectContextPlan> BuildSelectionAsync(
		McpJsonArguments arguments,
		CancellationToken cancellationToken) =>
		projects.BuildPlanAsync(
			arguments.OptionalString("project"),
			arguments.OptionalStringArray("paths", allowWhitespace: true),
			arguments.OptionalStringArray("include_patterns"),
			arguments.OptionalStringArray("exclude_patterns"),
			arguments.OptionalString("profile"),
			arguments.OptionalBoolean("tracked_only", false),
			cancellationToken);

	private Task<CallToolResult> RunProjectAsync(
		Func<Task<CallToolResult>> operation,
		CancellationToken cancellationToken) =>
		_projectOperation.RunAsync(() => ExecuteAsync(operation), cancellationToken);

	private static async Task<CallToolResult> ExecuteAsync(Func<Task<CallToolResult>> operation)
	{
		try
		{
			return await operation().ConfigureAwait(false);
		}
		catch (McpToolException exception)
		{
			return McpToolResults.Error(exception);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (RegexMatchTimeoutException)
		{
			return McpToolResults.Error(new McpToolException(
				McpErrorCodes.InvalidPattern,
				$"{McpErrorCodes.InvalidPattern}: regex evaluation exceeded 2 seconds; simplify the pattern and retry."));
		}
		catch (Exception exception)
		{
			return McpToolResults.Error(exception);
		}
	}

	private static ProjectContextView ParseView(string token) => token switch
	{
		"tree" => ProjectContextView.Tree,
		"content" => ProjectContextView.Content,
		"tree-content" => ProjectContextView.TreeContent,
		_ => throw new McpToolException(
			McpErrorCodes.InvalidArguments,
			$"{McpErrorCodes.InvalidArguments}: invalid view '{token}'. Valid values: tree, content, tree-content.")
	};

	private static ProjectContextDocumentFormat ParseFormat(string token) => token switch
	{
		"text" => ProjectContextDocumentFormat.Text,
		"markdown" => ProjectContextDocumentFormat.Markdown,
		"json" => ProjectContextDocumentFormat.Json,
		"xml" => ProjectContextDocumentFormat.Xml,
		_ => throw new McpToolException(
			McpErrorCodes.InvalidArguments,
			$"{McpErrorCodes.InvalidArguments}: invalid format '{token}'. Valid values: text, markdown, json, xml.")
	};

	private static TreeNodeDescriptor PruneToDepth(TreeNodeDescriptor node, int remainingDepth) =>
		remainingDepth == 0
			? node with { Children = [] }
			: node with
			{
				Children = node.Children
					.Select(child => PruneToDepth(child, remainingDepth - 1))
					.ToArray()
			};

	private static async Task<McpTextPage> ReadFilePageAsync(
		string path,
		int totalLines,
		int? startLine,
		int? endLine,
		CancellationToken cancellationToken)
	{
		await using var stream = new FileStream(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			16 * 1024,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		return await McpTextRanges.ReadPageAsync(
			stream,
			startLine,
			endLine,
			MaximumPageLines,
			MaximumPageCharacters,
			cancellationToken,
			totalLines).ConfigureAwait(false);
	}

	private static void AppendSearchResult(
		StringBuilder output,
		string relativePath,
		IReadOnlyList<string> lines,
		int matchIndex,
		int contextLines)
	{
		var safePath = EscapeSingleLine(relativePath);
		var first = Math.Max(0, matchIndex - contextLines);
		var last = Math.Min(lines.Count - 1, matchIndex + contextLines);
		for (var index = first; index <= last; index++)
		{
			var marker = index == matchIndex ? ':' : '-';
			output.Append(safePath).Append(marker).Append(index + 1).Append(marker)
				.AppendLine(EscapeSingleLine(lines[index]));
		}
	}

	private static string EscapeSingleLine(string value) => value
		.Replace("\\", "\\\\", StringComparison.Ordinal)
		.Replace("\r", "\\r", StringComparison.Ordinal)
		.Replace("\n", "\\n", StringComparison.Ordinal)
		.Replace("\t", "\\t", StringComparison.Ordinal);

	private static string ResolveProjectName(string root)
	{
		var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
		return string.IsNullOrEmpty(name) ? root : name;
	}

	private sealed record FileWeight(string Path, long Tokens);
}
