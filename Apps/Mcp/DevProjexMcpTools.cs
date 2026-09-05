using System.ComponentModel;
using System.Globalization;

namespace DevProjex.Mcp;

internal sealed class DevProjexMcpTools(
	McpRootRegistry roots,
	Lazy<McpProjectService> projectService,
	McpPackRegistry packs,
	bool agentExclusions = false)
{
	private const int MaximumTreeLines = 2_000;
	private const int MaximumInlinePackCharacters = 50_000;
	internal const int MaximumStoredPackResponseCharacters = 50_000;
	private const int MaximumStoredTreePreviewCharacters = 38_000;
	private const int MaximumStoredBudgetReportCharacters = 8_000;
	private const int MaximumStoredTrustedNoticeCharacters = 2_000;
	private const int MaximumPageLines = 1_000;
	private const int MaximumPageCharacters = 50_000;
	private const int MaximumExclusionTokenLength = 32;
	private const int MaximumSearchContentCharacters = 49_000;
	private const string StoredTreePreviewTruncationNotice =
		"[Tree preview truncated to fit the stored-pack response limit. Use read_pack for the complete pack.]";
	private const string StoredBudgetReportTruncationNotice =
		"[Token budget file list truncated to fit the stored-pack response limit.]";
	private const string StoredTrustedNoticeTruncationNotice =
		"[Additional trusted diagnostics truncated to fit the stored-pack response limit.]";
	private const string RedactionDescription =
		" Secrets are replaced with DEVPROJEX_REDACTED[<category>#<n>] placeholders before text is returned or searched; " +
		"known examples such as documentation domains, 555 numbers, EXAMPLE keys, and reserved IP ranges are intentionally preserved.";
	private readonly McpProjectOperationGate _projectOperation = new();
	private McpProjectService Projects => projectService.Value;

	[Description("List the project roots this server is allowed to read, their saved local profiles, and the selection baseline every call starts from.")]
	public Task<CallToolResult> ListProjects(
		RequestContext<CallToolRequestParams> request,
		CancellationToken cancellationToken) =>
		ExecuteAsync(() =>
		{
			_ = McpJsonArguments.Create(request.Params);
			var validatedRoots = roots.Roots
				.Select(root => roots.ResolveProject(root))
				.ToArray();
			var projectItems = validatedRoots
				.Select(root => new
				{
					path = root,
					name = ResolveProjectName(root),
					type = McpProjectService.IsGitRepository(root) ? "git-repository" : "local-folder"
				})
				.ToArray();
			var profiles = validatedRoots
				.Where(Projects.HasLocalProfile)
				.Select(root => new { project = root, name = "local" })
				.ToArray();
			// The baseline is server-wide, so the first call in the recommended sequence is
			// where an agent learns which filters shape every later answer and whether it
			// may change the exclusion toggles itself.
			var baseline = new
			{
				git = ProjectSelectionTokens.ToToken(Projects.ServerGitMode),
				exclusions = ProjectSelectionTokens
					.OrderExclusions(Projects.ServerExclusions)
					.Select(ProjectSelectionTokens.ToToken)
					.ToArray(),
				agentExclusions
			};
			return Task.FromResult(McpToolResults.StructuredSuccess(new { projects = projectItems, profiles, baseline }));
		});

	[Description("Return the effective project tree after built-in, gitignore, server-baseline, and optional agent filters.")]
	public Task<CallToolResult> GetTree(
		RequestContext<CallToolRequestParams> request,
		CancellationToken cancellationToken) =>
		RunProjectAsync(async () =>
		{
			var arguments = McpJsonArguments.Create(
				request.Params,
				WithAgentArguments(
					"project",
					"branch",
					"include_patterns",
					"exclude_patterns",
					"max_depth",
					"tracked_only",
					"git_scope",
					"max_file_bytes",
					"format"));
			var format = ParseTreeFormat(arguments.OptionalString("format") ?? "markdown");
			var includePatterns = arguments.OptionalStringArray("include_patterns");
			var excludePatterns = arguments.OptionalStringArray("exclude_patterns");
			var plan = await Projects.BuildPlanAsync(
				arguments.OptionalString("project"),
				arguments.OptionalString("branch"),
				paths: null,
				includePatterns,
				excludePatterns,
				profile: null,
				arguments.OptionalBoolean("tracked_only", false),
				arguments.OptionalString("git_scope"),
				arguments.OptionalInt64("max_file_bytes", 1, long.MaxValue),
				cancellationToken,
				includeOutputMetrics: false,
				exclusions: ParseExclusionsArgument(arguments)).ConfigureAwait(false);
			var depth = arguments.OptionalInteger("max_depth", 0, 1_000);
			var renderedTree = depth is null
				? plan.ProjectedTree
				: PruneToDepthWithCancellation(
					plan.ProjectedTree,
					depth.Value,
					cancellationToken);
			using var treeWriter = new McpBoundedLineTextWriter(MaximumTreeLines);
			try
			{
				await Projects.TreeExportService.WriteFullTreeAsync(
						treeWriter,
						plan.SourceRoot,
						renderedTree,
						format,
						displayRootPath: McpProjectService.ResolveAddressDocumentRoot(plan),
						cancellationToken: cancellationToken)
					.ConfigureAwait(false);
			}
			catch (McpLineLimitReachedException)
			{
				if (format is TreeTextFormat.Json or TreeTextFormat.Xml)
				{
					throw new McpToolException(
						McpErrorCodes.PayloadTruncated,
						$"{McpErrorCodes.PayloadTruncated}: the {format.ToString().ToLowerInvariant()} tree exceeds " +
						$"the {MaximumTreeLines}-line result limit. Reduce max_depth or narrow include_patterns " +
						"and exclude_patterns, then retry.");
				}
			}

			var treeTruncationNotice = treeWriter.IsTruncated
				? "[Tree truncated at 2000 lines. Narrow include_patterns, exclude_patterns, or max_depth.]"
				: null;
			return McpToolResults.TextSuccess(AppendTrustedNotices(
				McpSpotlight.Wrap(treeWriter.Text),
				treeTruncationNotice,
				McpTrustedDiagnosticFormatter.FormatWarnings(plan),
				SelectionNotices(
					plan,
					includeFilters: true,
					new McpSelectionNoticeContext(
						HasPaths: false,
						HasPatterns: HasItems(includePatterns) || HasItems(excludePatterns)))));
		}, cancellationToken);

	[Description("Measure a redacted project selection and list its largest text files by estimated tokens." + RedactionDescription)]
	public Task<CallToolResult> Analyze(
		RequestContext<CallToolRequestParams> request,
		CancellationToken cancellationToken) =>
		RunProjectAsync(async () =>
		{
			var operationProgress = new McpProgressReporter(request, cancellationToken);
			operationProgress.Milestone(1, "selecting files");
			var arguments = SelectionArguments(request.Params);
			var topFileCount = arguments.OptionalInteger("top_files", 1, 1_000) ?? 10;
			var detail = McpDetailPolicy.Parse(arguments.OptionalString("detail"));
			var selection = await BuildSelectionAsync(
				arguments,
				cancellationToken,
				includeOutputMetrics: false).ConfigureAwait(false);
			var plan = selection.Plan;
			operationProgress.Milestone(
				10,
				$"scanning files {plan.IncludedFiles.Count}/{plan.IncludedFiles.Count}");
			var effectiveDetail = Projects.ResolveDetail(plan, detail);
			operationProgress.Milestone(11, $"transforming content 0/{plan.IncludedFiles.Count}");
			await using var prepared = await Projects.PrepareAsync(
					plan,
					detail,
					operationProgress.Measure("transforming content", 12, 59),
					cancellationToken)
				.ConfigureAwait(false);
			operationProgress.Milestone(
				60,
				$"transforming content {plan.IncludedFiles.Count}/{plan.IncludedFiles.Count}");
			var analyzer = Projects.CreatePreparedAnalyzer(prepared);
			operationProgress.Milestone(61, $"analyzing content 0/{plan.IncludedFiles.Count}");
			var largest = new TopFileRanking(topFileCount);
			var metrics = await ProjectContentMetricsCalculator
				.CalculateAsync(
					analyzer,
					plan.IncludedFiles,
					fileMetrics => largest.Add(
						fileMetrics.Path,
						CodeCompressionSnapshot.EstimateTokens(fileMetrics.CharCount)),
					operationProgress.Measure("analyzing content", 62, 98),
					cancellationToken)
				.ConfigureAwait(false);
			operationProgress.Milestone(
				99,
				$"analyzing content {plan.IncludedFiles.Count}/{plan.IncludedFiles.Count}");
			var top = largest.Project(item => new
			{
				path = McpProjectService.ToRelative(plan.SourceRoot, item.Path),
				tokens = item.Tokens
			});
			// Echo the effective exclusion state so both the agent and a human reading the
			// transcript always see which toggles shaped this measurement.
			var activeExclusions = ProjectSelectionTokens
				.OrderExclusions(plan.Selection.Exclusions ?? [])
				.Select(ProjectSelectionTokens.ToToken)
				.ToArray();
			var envelope = new
			{
				files = plan.IncludedFiles.Count,
				characters = metrics.Chars,
				tokens = metrics.Tokens,
				detail = effectiveDetail.Token,
				exclusions = activeExclusions,
				topFiles = top
			};
			await operationProgress.CompleteAsync(100, "building analysis").ConfigureAwait(false);
			return McpToolResults.StructuredSuccess(
				envelope,
				CombineTrustedNotices(
					FormatUnscannableNotice(prepared.UnscannableFiles, UnscannableResultKind.Analysis),
					McpTrustedDiagnosticFormatter.FormatWarnings(plan),
					SelectionNotices(plan, includeFilters: false, selection.NoticeContext)));
		}, cancellationToken);

	[Description(
		"Build an exact redacted DevProjex context export. Results through 50,000 characters are returned inline; larger results are stored and return a pack_id for read_pack. " +
		"A stored pack id remains valid until this server process exits; after restart, call pack_context again." +
		RedactionDescription)]
	public Task<CallToolResult> PackContext(
		RequestContext<CallToolRequestParams> request,
		CancellationToken cancellationToken) =>
		RunProjectAsync(async () =>
		{
			var operationProgress = new McpProgressReporter(request, cancellationToken);
			operationProgress.Milestone(1, "selecting files");
			var arguments = McpJsonArguments.Create(
				request.Params,
				WithAgentArguments(
					"project",
					"branch",
					"paths",
					"include_patterns",
					"exclude_patterns",
					"profile",
					"view",
					"format",
					"detail",
					"tracked_only",
					"git_scope",
					"max_tokens",
					"max_file_bytes"));
			var detail = McpDetailPolicy.Parse(arguments.OptionalString("detail"));
			var maximumEstimatedTokens = arguments.OptionalInt64("max_tokens", 1, long.MaxValue);
			var format = ParseFormat(arguments.OptionalString("format") ?? "markdown");
			var view = ParseView(arguments.OptionalString("view") ?? "tree-content");
			var selection = await BuildSelectionAsync(
					arguments,
					cancellationToken,
					includeOutputMetrics:
						view == ProjectContextView.Tree &&
						format is ProjectContextDocumentFormat.Json or ProjectContextDocumentFormat.Xml)
				.ConfigureAwait(false);
			var plan = selection.Plan;
			// A pack is the answer many agents read instead of get_tree, so it carries the same
			// effective-filters footer next to its tree.
			var trustedPlanWarnings = CombineTrustedNotices(
				McpTrustedDiagnosticFormatter.FormatWarnings(plan),
				SelectionNotices(plan, includeFilters: true, selection.NoticeContext));
			operationProgress.Milestone(
				10,
				$"scanning files {plan.IncludedFiles.Count}/{plan.IncludedFiles.Count}");
			var effectiveDetail = Projects.ResolveDetail(plan, detail);
			plan = Projects.ApplyDetail(plan, effectiveDetail, cancellationToken);
			var outputPlan = WithoutWarningDiagnostics(plan);
			var transformedFileCount = view == ProjectContextView.Tree ? 0 : plan.IncludedFiles.Count;
			operationProgress.Milestone(11, $"transforming content 0/{transformedFileCount}");
			await using var prepared = view == ProjectContextView.Tree
				? null
				: await Projects.PrepareAsync(
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
			ProjectContextWriteResult? writeResult = null;
			var pack = await packs.CreateAsync(
				async (stream, token) =>
				{
					var writeProgress = operationProgress.Measure("writing pack", 62, 99);
					if (prepared is null)
					{
						writeResult = await Projects.DocumentService.WriteCompleteWithReportAsync(
								outputPlan,
								view,
								format,
								stream,
								token,
								plain: false,
								useSourceMappedStructuredPaths: true,
								writeProgress: writeProgress,
								maximumEstimatedTokens: maximumEstimatedTokens)
							.ConfigureAwait(false);
						return;
					}

					writeResult = await Projects.DocumentService.WritePreparedCompleteAsync(
							outputPlan,
							view,
							format,
							stream,
							prepared,
							token,
							plain: false,
							useSourceMappedStructuredPaths: true,
							writeProgress,
							maximumEstimatedTokens)
						.ConfigureAwait(false);
				},
				cancellationToken).ConfigureAwait(false);
			var retainPack = false;
			try
			{
				if (pack.Characters <= MaximumInlinePackCharacters)
				{
					var content = await File.ReadAllTextAsync(pack.Path, cancellationToken).ConfigureAwait(false);
					var inlineMessage = AppendTrustedNotices(BuildSpotlightedPackContent(
						content,
						writeResult?.TokenBudget),
						FormatUnscannableNotice(
							writeResult?.UnscannableFiles,
							UnscannableResultKind.Pack),
						trustedPlanWarnings);
					if (inlineMessage.Length <= MaximumInlinePackCharacters)
					{
						await operationProgress.CompleteAsync(
								100,
								$"writing pack {writtenFileCount}/{writtenFileCount}")
							.ConfigureAwait(false);
						return McpToolResults.TextSuccess(inlineMessage, advertiseLargeResult: true);
					}
				}

				using var treeWriter = new McpBoundedLineTextWriter(
					MaximumTreeLines,
					MaximumStoredTreePreviewCharacters);
				try
				{
					await Projects.TreeExportService.WriteFullTreeAsync(
							treeWriter,
							outputPlan.SourceRoot,
							outputPlan.ProjectedTree,
							Projects.ResolveProtectedDocumentRoot(outputPlan),
							includeFinalLineEnding: false,
							cancellationToken: cancellationToken)
						.ConfigureAwait(false);
				}
				catch (McpLineLimitReachedException)
				{
				}
				var message = BuildStoredPackResponse(
					pack,
					treeWriter.Text,
					treeWriter.IsTruncated,
					writeResult?.TokenBudget,
					FormatUnscannableNotice(
						writeResult?.UnscannableFiles,
						UnscannableResultKind.Pack),
					trustedPlanWarnings);
				await operationProgress.CompleteAsync(
						100,
						$"writing pack {writtenFileCount}/{writtenFileCount}")
					.ConfigureAwait(false);
				var response = McpToolResults.TextSuccess(message, advertiseLargeResult: true);
				retainPack = true;
				return response;
			}
			finally
			{
				if (!retainPack)
					packs.Remove(pack.Id);
			}
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
					pack,
					start,
					end,
					cancellationToken)
				.ConfigureAwait(false);
			var continuationNotice = page.IsTruncated
				? $"[Showing lines {page.StartLine}-{page.EndLine} of {page.TotalLines}; " +
				  $"continue with start_line={page.EndLine + 1}.]"
				: null;
			var characterLimitNotice = page.CharacterLimitReached
				? "[The current line exceeded the 50000-character response cap; use search_project to narrow the source.]"
				: null;
			return McpToolResults.TextSuccess(
				AppendTrustedNotices(McpSpotlight.Wrap(page.Text), continuationNotice, characterLimitNotice),
				advertiseLargeResult: true);
		});

	[Description("Search already-redacted project text with a timeout-bounded .NET regular expression." + RedactionDescription)]
	public Task<CallToolResult> SearchProject(
		RequestContext<CallToolRequestParams> request,
		CancellationToken cancellationToken) =>
		RunProjectAsync(async () =>
		{
			var arguments = McpJsonArguments.Create(
				request.Params,
				WithAgentArguments(
					"project",
					"branch",
					"pattern",
					"include_patterns",
					"exclude_patterns",
					"context_lines",
					"ignore_case",
					"max_results",
					"tracked_only",
					"git_scope",
					"max_file_bytes"));
			var pattern = arguments.RequiredString("pattern", allowWhitespace: true);
			var contextLines = arguments.OptionalInteger("context_lines", 0, 20) ?? 2;
			var ignoreCase = arguments.OptionalBoolean("ignore_case", true);
			var maximumResults = arguments.OptionalInteger("max_results", 1, 200) ?? 50;
			var regex = new McpSearchRegex(pattern, ignoreCase);
			var includePatterns = arguments.OptionalStringArray("include_patterns");
			var excludePatterns = arguments.OptionalStringArray("exclude_patterns");

			var plan = await Projects.BuildPlanAsync(
				arguments.OptionalString("project"),
				arguments.OptionalString("branch"),
				paths: null,
				includePatterns,
				excludePatterns,
				profile: null,
				arguments.OptionalBoolean("tracked_only", false),
				arguments.OptionalString("git_scope"),
				arguments.OptionalInt64("max_file_bytes", 1, long.MaxValue),
				cancellationToken,
				includeOutputMetrics: false,
				exclusions: ParseExclusionsArgument(arguments)).ConfigureAwait(false);
			await using var prepared = await Projects.PrepareAsync(plan, cancellationToken).ConfigureAwait(false);
			var analyzer = Projects.CreatePreparedAnalyzer(prepared);
			var output = new StringBuilder();
			var totalMatches = 0;
			var shownMatches = 0;
			var responseLimitReached = false;
			foreach (var file in plan.IncludedFiles)
			{
				var content = await analyzer
					.TryReadAsTextAsync(file, long.MaxValue, cancellationToken)
					.ConfigureAwait(false);
				if (content is null)
					continue;
				var scan = McpSearchTextScanner.Scan(
					content.Content,
					regex,
					contextLines,
					Math.Max(0, maximumResults - totalMatches),
					cancellationToken);
				totalMatches += scan.TotalMatches;
				if (responseLimitReached)
					continue;

				foreach (var match in scan.Matches)
				{
					if (AppendSearchResult(
						output,
						McpProjectService.ToRelative(plan.SourceRoot, file),
						content.Content,
						match,
						MaximumSearchContentCharacters))
					{
						shownMatches++;
					}
					else
					{
						responseLimitReached = true;
					}
				}
			}
			var additionalMatchesNotice = totalMatches > shownMatches
				? $"[{totalMatches - shownMatches} additional matches not shown; narrow the pattern or filters.]"
				: null;
			// An empty search result must say whether nothing matched or nothing was searched;
			// the count is trusted data, the file names never are.
			var noMatches = totalMatches == 0 && plan.IncludedFiles.Count > 0
				? $"[No matches] The pattern matched nothing in {plan.IncludedFiles.Count} selected file(s) ({McpEffectiveFilters.Describe(plan)})."
				: null;
			return McpToolResults.TextSuccess(AppendTrustedNotices(
				McpSpotlight.Wrap(output.ToString().TrimEnd()),
				FormatUnscannableNotice(prepared.UnscannableFiles, UnscannableResultKind.Search),
				McpTrustedDiagnosticFormatter.FormatWarnings(plan),
				noMatches,
				additionalMatchesNotice,
				SelectionNotices(
					plan,
					includeFilters: false,
					new McpSelectionNoticeContext(
						HasPaths: false,
						HasPatterns: HasItems(includePatterns) || HasItems(excludePatterns)))));
		}, cancellationToken);

	[Description("Read redacted text from one effective project file using an optional 1-based line range." + RedactionDescription)]
	public Task<CallToolResult> GetFile(
		RequestContext<CallToolRequestParams> request,
		CancellationToken cancellationToken) =>
		RunProjectAsync(async () =>
		{
			var arguments = McpJsonArguments.Create(
				request.Params,
				WithAgentArguments(
					"project",
					"branch",
					"path",
					"start_line",
					"end_line"));
			// get_file honors the delegated set too: a file revealed by get_tree or
			// search_project under a per-call exclusions value must stay readable.
			var plan = await Projects.BuildPlanAsync(
				arguments.OptionalString("project"),
				arguments.OptionalString("branch"),
				paths: null,
				includePatterns: null,
				excludePatterns: null,
				profile: null,
				trackedOnly: false,
				gitScope: null,
				maximumFileBytes: null,
				cancellationToken,
				includeOutputMetrics: false,
				exclusions: ParseExclusionsArgument(arguments)).ConfigureAwait(false);
			var file = Projects.ResolveFile(plan, arguments.RequiredString("path", allowWhitespace: true));
			await using var prepared = await Projects.PrepareAsync(plan with { IncludedFiles = [file] }, cancellationToken)
				.ConfigureAwait(false);
			var content = await Projects.CreatePreparedAnalyzer(prepared)
				.TryReadAsTextAsync(file, cancellationToken)
				.ConfigureAwait(false);
			if (content is null)
			{
				throw new McpToolException(
					McpErrorCodes.PayloadTruncated,
					$"{McpErrorCodes.PayloadTruncated}: file content is binary, unsupported, or exceeds the redaction scan limit and cannot be returned safely.");
			}
			var page = McpTextRanges.Slice(
				content.Content,
				arguments.OptionalInteger("start_line", 1, int.MaxValue),
				arguments.OptionalInteger("end_line", 1, int.MaxValue),
				MaximumPageLines,
				MaximumPageCharacters,
				cancellationToken);
			var continuationNotice = page.IsTruncated
				? $"[Showing lines {page.StartLine}-{page.EndLine} of {page.TotalLines}; continue with start_line={page.EndLine + 1}.]"
				: null;
			var characterLimitNotice = page.CharacterLimitReached
				? "[The current line exceeded the 50000-character response cap; use search_project to narrow the source.]"
				: null;
			return McpToolResults.TextSuccess(AppendTrustedNotices(
				McpSpotlight.Wrap(page.Text),
				continuationNotice,
				characterLimitNotice));
		}, cancellationToken);

	private McpJsonArguments SelectionArguments(CallToolRequestParams request) =>
		McpJsonArguments.Create(
			request,
			WithAgentArguments(
				"project",
				"branch",
				"paths",
				"include_patterns",
				"exclude_patterns",
				"profile",
				"detail",
				"tracked_only",
				"git_scope",
				"top_files",
				"max_file_bytes"));

	private async Task<McpSelectionResult> BuildSelectionAsync(
		McpJsonArguments arguments,
		CancellationToken cancellationToken,
		bool includeOutputMetrics = true)
	{
		var paths = arguments.OptionalStringArray(
			"paths",
			allowWhitespace: true,
			maximumItems: McpProjectService.MaximumRequestedPaths,
			maximumItemScalarValues: McpProjectService.MaximumRequestedPathLength);
		var includePatterns = arguments.OptionalStringArray("include_patterns");
		var excludePatterns = arguments.OptionalStringArray("exclude_patterns");
		var plan = await Projects.BuildPlanAsync(
			arguments.OptionalString("project"),
			arguments.OptionalString("branch"),
			paths,
			includePatterns,
			excludePatterns,
			arguments.OptionalString("profile"),
			arguments.OptionalBoolean("tracked_only", false),
			arguments.OptionalString("git_scope"),
			arguments.OptionalInt64("max_file_bytes", 1, long.MaxValue),
			cancellationToken,
			includeOutputMetrics,
			exclusions: ParseExclusionsArgument(arguments)).ConfigureAwait(false);
		return new McpSelectionResult(
			plan,
			new McpSelectionNoticeContext(
				HasPaths: HasItems(paths),
				HasPatterns: HasItems(includePatterns) || HasItems(excludePatterns)));
	}

	private string? SelectionNotices(
		ProjectContextPlan plan,
		bool includeFilters,
		McpSelectionNoticeContext request) =>
		McpEffectiveFilters.SelectionNotices(plan, agentExclusions, includeFilters, request);

	private static bool HasItems<T>(IReadOnlyCollection<T>? items) => items is { Count: > 0 };

	// The exclusions argument exists only on servers started with --allow-agent-exclusions;
	// everywhere else the allowlist rejects it, so a default server keeps the
	// narrowing-only contract byte for byte.
	private string[] WithAgentArguments(params string[] names) =>
		agentExclusions ? [.. names, "exclusions"] : names;

	private IReadOnlyList<ProjectExclusion>? ParseExclusionsArgument(McpJsonArguments arguments)
	{
		if (!agentExclusions)
			return null;

		var tokens = arguments.OptionalStringArray(
			"exclusions",
			maximumItems: ProjectSelectionTokens.Exclusions.Count,
			maximumItemScalarValues: MaximumExclusionTokenLength,
			tooManyItemsHint: "remove duplicate or extra tokens and retry",
			overLengthHint: "use the published exclusion tokens");
		if (tokens is null)
			return null;

		// The published schema declares uniqueItems, so the runtime enforces it too;
		// case-variant repeats count as duplicates because tokens parse case-insensitively.
		var parsed = new HashSet<ProjectExclusion>();
		foreach (var token in tokens)
		{
			if (!parsed.Add(ParseExclusionToken(token)))
			{
				throw new McpToolException(
					McpErrorCodes.InvalidArguments,
					$"{McpErrorCodes.InvalidArguments}: exclusions must not contain duplicate tokens.");
			}
		}

		return ProjectSelectionTokens.OrderExclusions(parsed);
	}

	private static ProjectExclusion ParseExclusionToken(string token)
	{
		foreach (var descriptor in ProjectPresentationCatalog.Exclusions)
		{
			if (string.Equals(descriptor.Token, token, StringComparison.OrdinalIgnoreCase) &&
			    descriptor.Id is { } exclusion)
			{
				return exclusion;
			}
		}

		throw new McpToolException(
			McpErrorCodes.InvalidArguments,
			$"{McpErrorCodes.InvalidArguments}: exclusions accepts only: " +
			$"{string.Join(", ", ProjectSelectionTokens.Exclusions)}. " +
			"Path globs belong in exclude_patterns; an empty array turns every toggle off.");
	}

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
		catch (PortableProjectProfileException exception)
		{
			// Profile validation carries curated user-facing text; surfacing it beats the
			// opaque operation-failed fallback, but CLI error codes do not cross the MCP boundary.
			return McpToolResults.Error(new McpToolException(
				McpErrorCodes.InvalidArguments,
				$"{McpErrorCodes.InvalidArguments}: {exception.Message}"));
		}
		catch (ProjectContextValidationException exception)
		{
			return McpToolResults.Error(new McpToolException(
				McpErrorCodes.InvalidArguments,
				$"{McpErrorCodes.InvalidArguments}: {exception.Message}"));
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

	private static TreeTextFormat ParseTreeFormat(string token) => token switch
	{
		"markdown" => TreeTextFormat.Markdown,
		"text" => TreeTextFormat.Ascii,
		"json" => TreeTextFormat.Json,
		"xml" => TreeTextFormat.Xml,
		_ => throw new McpToolException(
			McpErrorCodes.InvalidArguments,
			$"{McpErrorCodes.InvalidArguments}: invalid format '{token}'. Valid values: markdown, text, json, xml.")
	};

	private static string BuildSpotlightedPackContent(
		string content,
		ProjectContextTokenBudgetReport? report)
	{
		if (report is null)
			return McpSpotlight.Wrap(content);

		return McpSpotlight.Wrap(content) + "\n\n" +
		       McpSpotlight.Wrap(FormatTokenBudgetReport(report));
	}

	private static string BuildStoredPackResponse(
		McpPackDocument pack,
		string tree,
		bool treeWasTruncated,
		ProjectContextTokenBudgetReport? report,
		string? unscannableNotice,
		string? planWarnings)
	{
		var header = $"Pack stored as '{pack.Id}' ({pack.Characters} characters, {pack.Lines} lines). " +
		             "Call read_pack with this pack_id to read ranges, or search_project to locate source content.\n";
		var treePreview = TakeCompleteScalarPrefix(tree, MaximumStoredTreePreviewCharacters);
		var treePreviewWasTruncated = treeWasTruncated || treePreview.Length < tree.Length;
		var budgetReport = report is null
			? null
			: LimitResponseSegment(
				FormatTokenBudgetReport(report),
				MaximumStoredBudgetReportCharacters,
				StoredBudgetReportTruncationNotice,
				forceMarker: false);
		var trustedNotices = CombineTrustedNotices(unscannableNotice, planWarnings);
		if (trustedNotices is not null)
		{
			trustedNotices = LimitResponseSegment(
				trustedNotices,
				MaximumStoredTrustedNoticeCharacters,
				StoredTrustedNoticeTruncationNotice,
				forceMarker: false);
		}

		string Compose() => AppendTrustedNotices(
			header + McpSpotlight.Wrap(treePreview) +
			(budgetReport is null ? string.Empty : "\n\n" + McpSpotlight.Wrap(budgetReport)),
			treePreviewWasTruncated ? StoredTreePreviewTruncationNotice : null,
			trustedNotices);

		var message = Compose();
		if (message.Length <= MaximumStoredPackResponseCharacters)
			return message;

		var overflow = message.Length - MaximumStoredPackResponseCharacters;
		var reducedTreeLimit = Math.Max(0, treePreview.Length - overflow);
		treePreview = TakeCompleteScalarPrefix(treePreview, reducedTreeLimit);
		treePreviewWasTruncated = true;
		message = Compose();
		if (message.Length <= MaximumStoredPackResponseCharacters)
			return message;

		throw new InvalidOperationException("Stored pack response exceeded its character budget.");
	}

	internal static string LimitResponseSegment(
		string content,
		int maximumCharacters,
		string marker,
		bool forceMarker)
	{
		ArgumentNullException.ThrowIfNull(content);
		ArgumentException.ThrowIfNullOrEmpty(marker);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
		if (!forceMarker && content.Length <= maximumCharacters)
			return content;
		if (marker.Length >= maximumCharacters)
			return TakeCompleteScalarPrefix(marker, maximumCharacters);

		var prefixLimit = maximumCharacters - marker.Length - 1;
		var prefixLength = Math.Min(prefixLimit, content.Length);
		if (prefixLength > 0 &&
		    prefixLength < content.Length &&
		    char.IsHighSurrogate(content[prefixLength - 1]) &&
		    char.IsLowSurrogate(content[prefixLength]))
		{
			prefixLength--;
		}
		if (prefixLength > 0 && content[prefixLength - 1] == '\r')
			prefixLength--;
		return prefixLength == 0
			? marker
			: string.Concat(content.AsSpan(0, prefixLength), "\n", marker);
	}

	private static string TakeCompleteScalarPrefix(string content, int maximumCharacters)
	{
		var length = Math.Min(content.Length, maximumCharacters);
		if (length > 0 &&
		    length < content.Length &&
		    char.IsHighSurrogate(content[length - 1]) &&
		    char.IsLowSurrogate(content[length]))
		{
			length--;
		}
		return content[..length];
	}

	private static string AppendTrustedNotices(string content, params string?[] notices)
	{
		var combined = CombineTrustedNotices(notices);
		return combined is null ? content : content + "\n\n" + combined;
	}

	private static string? CombineTrustedNotices(params string?[] notices)
	{
		string? combined = null;
		foreach (var notice in notices)
		{
			if (string.IsNullOrWhiteSpace(notice))
				continue;
			combined = combined is null ? notice : combined + "\n" + notice;
		}
		return combined;
	}

	private static string? FormatUnscannableNotice(
		IReadOnlyList<UnscannableFile>? files,
		UnscannableResultKind resultKind)
	{
		if (files is not { Count: > 0 })
			return null;

		var count = files.Count.ToString(CultureInfo.InvariantCulture);
		var subject = files.Count == 1 ? $"{count} selected file" : $"{count} selected files";
		var consequence = resultKind switch
		{
			UnscannableResultKind.Analysis =>
				"Metrics for uninspected content may be estimated and do not reflect requested detail transformations.",
			UnscannableResultKind.Pack => "Uninspected content was withheld from the pack.",
			UnscannableResultKind.Search => "Uninspected content was not searched.",
			_ => throw new ArgumentOutOfRangeException(nameof(resultKind), resultKind, null)
		};
		return $"[Warning {McpErrorCodes.PayloadTruncated}] Mandatory redaction could not fully inspect {subject}. " +
		       $"{consequence} Results are partial. Set max_file_bytes={SecretRedactionOutputPreparer.MaximumScannableFileBytes} " +
		       "or lower, exclude oversized or unsupported files, and retry.";
	}

	private static ProjectContextPlan WithoutWarningDiagnostics(ProjectContextPlan plan)
	{
		if (!plan.Diagnostics.Any(static diagnostic =>
			    diagnostic.Severity == ContextDiagnosticSeverity.Warning))
			return plan;
		return plan with
		{
			Diagnostics = plan.Diagnostics
				.Where(static diagnostic => diagnostic.Severity != ContextDiagnosticSeverity.Warning)
				.ToArray()
		};
	}

	private static string FormatTokenBudgetReport(ProjectContextTokenBudgetReport report)
	{
		var output = new StringBuilder(512);
		output.Append("Token budget: ")
			.Append(report.MaximumEstimatedTokens.ToString(CultureInfo.InvariantCulture))
			.Append(" estimated tokens.\nIncluded: ")
			.Append(report.IncludedFileCount.ToString(CultureInfo.InvariantCulture))
			.Append(report.IncludedFileCount == 1 ? " file (" : " files (")
			.Append(report.IncludedEstimatedTokens.ToString(CultureInfo.InvariantCulture))
			.Append(" estimated tokens).\nSkipped: ")
			.Append(report.SkippedFileCount.ToString(CultureInfo.InvariantCulture))
			.Append(report.SkippedFileCount == 1 ? " file (" : " files (")
			.Append(report.SkippedEstimatedTokens.ToString(CultureInfo.InvariantCulture))
			.Append(" estimated tokens).\n");

		if (report.LargestSkippedFiles.Count > 0)
		{
			output.Append("Skipped files:\n");
			foreach (var file in report.LargestSkippedFiles)
			{
				output.Append("- ")
					.Append(McpTextEscaping.EscapeSingleLine(file.Path))
					.Append(" (")
					.Append(file.EstimatedTokens.ToString(CultureInfo.InvariantCulture))
					.Append(" estimated tokens)\n");
			}
			if (report.AdditionalSkippedFileCount > 0)
			{
				output.Append("- and ")
					.Append(report.AdditionalSkippedFileCount.ToString(CultureInfo.InvariantCulture))
					.Append(" more\n");
			}
		}

		if (report.SkippedFileCount > 0)
		{
			output.Append(
				"Tip: use detail=compact or detail=signatures, narrow the selection, or increase max_tokens.");
		}
		return output.ToString().TrimEnd('\r', '\n');
	}

	private sealed record McpSelectionResult(
		ProjectContextPlan Plan,
		McpSelectionNoticeContext NoticeContext);

	private enum UnscannableResultKind
	{
		Analysis,
		Pack,
		Search
	}

	internal static TreeNodeDescriptor PruneToDepth(TreeNodeDescriptor node, int remainingDepth) =>
		PruneToDepthWithCancellation(node, remainingDepth, CancellationToken.None);

	internal static TreeNodeDescriptor PruneToDepthWithCancellation(
		TreeNodeDescriptor node,
		int remainingDepth,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(node);
		ArgumentOutOfRangeException.ThrowIfNegative(remainingDepth);
		cancellationToken.ThrowIfCancellationRequested();

		var stack = new Stack<TreeDepthFrame>();
		stack.Push(new TreeDepthFrame(node, remainingDepth));
		TreeNodeDescriptor? result = null;
		while (stack.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var frame = stack.Peek();
			if (frame.RemainingDepth > 0 && frame.NextChildIndex < frame.Node.Children.Count)
			{
				var child = frame.Node.Children[frame.NextChildIndex++];
				stack.Push(new TreeDepthFrame(child, frame.RemainingDepth - 1));
				continue;
			}

			var projected = frame.Node with
			{
				Children = frame.RemainingDepth == 0
					? []
					: frame.ProjectedChildren
			};
			stack.Pop();
			if (stack.TryPeek(out var parent))
				parent.ProjectedChildren[parent.NextProjectedChildIndex++] = projected;
			else
				result = projected;
		}

		return result!;
	}

	private sealed class TreeDepthFrame(TreeNodeDescriptor node, int remainingDepth)
	{
		public TreeNodeDescriptor Node { get; } = node;
		public int RemainingDepth { get; } = remainingDepth;
		public TreeNodeDescriptor[] ProjectedChildren { get; } =
			remainingDepth == 0 ? [] : new TreeNodeDescriptor[node.Children.Count];
		public int NextChildIndex { get; set; }
		public int NextProjectedChildIndex { get; set; }
	}

	private static async Task<McpTextPage> ReadFilePageAsync(
		McpPackDocument pack,
		int? startLine,
		int? endLine,
		CancellationToken cancellationToken)
	{
		var checkpoint = pack.ResolveLineCheckpoint(startLine ?? 1);
		await using var stream = new FileStream(
			pack.Path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			16 * 1024,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		stream.Seek(checkpoint.ByteOffset, SeekOrigin.Begin);
		return await McpTextRanges.ReadPageAsync(
			stream,
			startLine,
			endLine,
			MaximumPageLines,
			MaximumPageCharacters,
			cancellationToken,
			pack.Lines,
			checkpoint.LineNumber).ConfigureAwait(false);
	}

	private static bool AppendSearchResult(
		StringBuilder output,
		string relativePath,
		string content,
		McpSearchMatchContext match,
		int maximumCharacters)
	{
		var safePath = EscapeSingleLine(relativePath);
		foreach (var line in match.Lines)
		{
			var marker = line.LineNumber == match.MatchLineNumber ? ':' : '-';
			var prefix = $"{safePath}{marker}{line.LineNumber}{marker}";
			var remaining = maximumCharacters - output.Length;
			if (prefix.Length > remaining)
			{
				AppendBoundedPrefix(output, prefix, Math.Max(0, remaining));
				return false;
			}
			output.Append(prefix);

			remaining = maximumCharacters - output.Length;
			var fullyEscaped = SingleLineTextEscaping.AppendBounded(
				output,
				content.AsSpan(line.Offset, line.Length),
				Math.Max(0, remaining));
			if (!fullyEscaped)
			{
				return false;
			}

			remaining = maximumCharacters - output.Length;
			if (Environment.NewLine.Length > remaining)
			{
				AppendBoundedPrefix(output, Environment.NewLine, Math.Max(0, remaining));
				return false;
			}

			output.Append(Environment.NewLine);
		}
		return true;
	}

	private static void AppendBoundedPrefix(StringBuilder output, string value, int maximumCharacters)
	{
		var length = Math.Min(value.Length, maximumCharacters);
		if (length > 0 &&
		    length < value.Length &&
		    char.IsHighSurrogate(value[length - 1]) &&
		    char.IsLowSurrogate(value[length]))
		{
			length--;
		}
		output.Append(value.AsSpan(0, length));
	}

	private static string EscapeSingleLine(string value) =>
		McpTextEscaping.EscapeSingleLine(
			value.Replace("\\", "\\\\", StringComparison.Ordinal));

	private static string ResolveProjectName(string root)
	{
		var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
		return string.IsNullOrEmpty(name) ? root : name;
	}

}
