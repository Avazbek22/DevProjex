using System.ComponentModel;
using System.Globalization;
using DevProjex.Application.Ranking;

namespace DevProjex.Mcp;

internal sealed class DevProjexMcpTools(
	McpRootRegistry roots,
	Lazy<McpProjectService> projectService,
	McpPackRegistry packs,
	bool agentExclusions = false,
	bool allowRemote = false,
	IReadOnlySet<string>? remoteHosts = null)
{
	private const int MaximumTreeLines = 2_000;
	private const int MaximumTreeCharacters = 50_000;
	private const int MaximumInlinePackCharacters = 50_000;
	internal const int MaximumStoredPackResponseCharacters = 50_000;
	private const int MaximumStoredTreePreviewCharacters = 38_000;
	private const int MaximumStoredBudgetReportCharacters = 8_000;
	private const int MaximumStoredTrustedNoticeCharacters = 2_000;
	private const int MaximumPageLines = 1_000;
	private const int MaximumPageCharacters = 50_000;
	private const int MaximumExclusionTokenLength = 32;
	// A wide alternation with context lines used to spend a quarter of an agent's whole
	// context budget in one unpredictable call. The cap bounds that, and the totals line
	// tells the caller how much it did not get.
	private const int MaximumSearchContentCharacters = 16_000;
	// Asking for a file by name is the one request the selection vocabulary answers in a form a
	// caller rarely guesses: a bare name is root-only, and paths selects what already exists at
	// the depth it names. Both roads end in an empty or misleading answer, so the two tools that
	// tolerate them point at the form that works. The pointer is a constant; nothing the caller
	// sent reaches it.
	private const string NameSearchNotice =
		"[Name search] File names and paths are matched only by include_patterns: prefix a bare name " +
		"with '**/' to find it at any depth, or append '/**' to a directory to select its files. " +
		"search_project matches file content, and paths selects a path that already exists.";
	private const int MaximumNameSearchExtensionLength = 8;
	private const string SearchContentCapNotice =
		"[Search truncated] The returned text reached the 16000-character search cap. " +
		"Narrow the pattern, add paths or include_patterns, or lower context_lines.";
	private const int MaximumAnalyzeTopFilesCharacters = 32_000;
	private const int MaximumAdmissionIncludedFilesCharacters = 32_000;
	private const int MaximumReportedUnmatchedDetailPatterns = 8;
	private const int MaximumReportedDetailPatternCharacters = 80;
	private const long MaximumSearchInspectedBytes = 64L * 1024 * 1024;
	private const string StoredTreePreviewTruncationNotice =
		"[Tree preview truncated to fit the stored-pack response limit. Use read_pack for the complete pack.]";
	private const string StoredBudgetReportTruncationNotice =
		"[Token budget file list truncated to fit the stored-pack response limit.]";
	private const string StoredTrustedNoticeTruncationNotice =
		"[Additional trusted diagnostics truncated to fit the stored-pack response limit.]";
	private static readonly string[] SafeNoFactsReasons =
	[
		"file language is not supported by the dependency engine yet",
		"source file could not be read",
		"dependency grammar could not be loaded",
		"source is binary",
		"source uses an unsupported encoding",
		"fact limit exceeded"
	];
	private readonly McpProjectOperationGate _projectOperation = new();
	private readonly McpServiceNoticeMemo serviceNotices = new();
	private static readonly IReadOnlySet<string> EmptyArgumentNames = McpJsonArguments.FreezeAllowed();
	private static readonly IReadOnlySet<string> ReadPackArgumentNames =
		McpJsonArguments.FreezeAllowed("pack_id", "start_line", "end_line", "start_column");
	private readonly IReadOnlySet<string> getTreeArgumentNames = Allowed(agentExclusions,
		"project", "branch", "paths", "include_patterns", "exclude_patterns", "tracked_only",
		"git_scope", "max_file_bytes", "max_depth", "format");
	private readonly IReadOnlySet<string> selectionArgumentNames = Allowed(agentExclusions,
		"project", "branch", "paths", "include_patterns", "exclude_patterns", "profile", "detail",
		"detail_by_pattern", "tracked_only", "git_scope", "top_files", "max_file_bytes",
		"max_tokens", "rank", "focus");
	private readonly IReadOnlySet<string> packArgumentNames = Allowed(agentExclusions,
		"project", "branch", "paths", "include_patterns", "exclude_patterns", "profile", "view",
		"format", "detail", "detail_by_pattern", "tracked_only", "git_scope", "rank", "focus",
		"max_tokens", "max_file_bytes", McpRelatedExpansion.ParameterName);
	private readonly IReadOnlySet<string> searchArgumentNames = Allowed(agentExclusions,
		"project", "branch", "pattern", "paths", "include_patterns", "exclude_patterns", "context_lines",
		"ignore_case", "max_results", "tracked_only", "git_scope", "max_file_bytes");
	private readonly IReadOnlySet<string> relatedArgumentNames = Allowed(agentExclusions,
		"project", "branch", "path", "direction", "include_patterns", "exclude_patterns", "profile",
		"tracked_only", "git_scope", "max_file_bytes");
	private readonly IReadOnlySet<string> getFileArgumentNames = Allowed(agentExclusions,
		"project", "branch", "profile", "path", "requests", "start_line", "end_line", "start_column");
	private McpProjectService Projects => projectService.Value;

	[Description(
		"Lists configured local projects, saved profiles, and baseline filters. Call it first in a session to obtain the project value accepted by other tools; use get_tree instead when you need one project's structure. Returns structured names, absolute paths, root types, profiles, and the Git/exclusion baseline. project accepts a unique listed name or its listed path. This tool has no parameters; remote Git URLs are accepted only by project tools when the server enables remote sources.")]
	public Task<CallToolResult> ListProjects(
		RequestContext<CallToolRequestParams> request,
		CancellationToken cancellationToken) =>
		ExecuteAsync(async () =>
		{
			_ = McpJsonArguments.Create(request.Params, EmptyArgumentNames);
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
			var profileCatalog = await Projects
				.ReadLocalProfileCatalogAsync(validatedRoots, cancellationToken)
				.ConfigureAwait(false);
			var profiles = validatedRoots
				.Where(profileCatalog.ProjectRoots.Contains)
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
				agentExclusions,
				protection = new
				{
					secrets = "always",
					privateData = Projects.HidePrivateData ? "enabled" : "disabled"
				},
				remote = new
				{
					enabled = allowRemote,
					hosts = remoteHosts?.Order(StringComparer.Ordinal).ToArray() ?? []
				}
			};
			return McpToolResults.StructuredSuccess(new
			{
				projects = projectItems,
				profiles,
				profilesStatus = profileCatalog.Status,
				baseline
			});
		});

	[Description(
		"Returns the filtered project structure without file contents. Use it to orient, to list what a directory holds, or to find files by name; use analyze instead for size and token metrics, or pack_context for multi-file content. Returns Markdown, text, JSON, or XML within 2,000 lines and 50,000 characters. project accepts a unique name or path from list_projects, or an allowed remote Git URL. Key parameters: paths narrows to literal files or directories; include_patterns finds files by name, as include_patterns=[\"**/*router*.ts\"]; format=markdown|text|json|xml; max_depth=0..1000 counts levels below the project root, not below paths; git_scope=staged|changes|diff:<ref>..<ref>; exclude_patterns and max_file_bytes narrow further.")]
	public Task<CallToolResult> GetTree(
		RequestContext<CallToolRequestParams> request,
		CancellationToken cancellationToken) =>
		RunProjectAsync(async () =>
		{
			var arguments = McpJsonArguments.Create(request.Params, getTreeArgumentNames);
			var format = ParseTreeFormat(arguments.OptionalString("format") ?? "markdown");
			var paths = ParsePaths(arguments);
			var includePatterns = arguments.OptionalStringArray("include_patterns");
			var excludePatterns = arguments.OptionalStringArray("exclude_patterns");
			var depth = arguments.OptionalInteger("max_depth", 0, 1_000);
			var maximumFileBytes = arguments.OptionalInt64("max_file_bytes", 1, long.MaxValue);
			var plan = await Projects.BuildPlanAsync(
				arguments.OptionalString("project"),
				arguments.OptionalString("branch"),
				paths,
				includePatterns,
				excludePatterns,
				profile: null,
				arguments.OptionalBoolean("tracked_only", false),
				arguments.OptionalString("git_scope"),
				maximumFileBytes,
				cancellationToken,
				includeOutputMetrics: false,
				exclusions: ParseExclusionsArgument(arguments),
				tolerateMissingPaths: true).ConfigureAwait(false);
			var depthFit = CalculateTreeDepthFit(
				plan.ProjectedTree,
				format,
				MaximumTreeLines,
				cancellationToken);
			int? automaticDepth = depth is null &&
			                     format is TreeTextFormat.Ascii or TreeTextFormat.Markdown &&
			                     !depthFit.FullTreeFits &&
			                     depthFit.DeepestCompleteDepth >= 1
				? depthFit.DeepestCompleteDepth
				: null;
			var effectiveDepth = depth ?? automaticDepth;
			var renderedTree = effectiveDepth is null
				? plan.ProjectedTree
				: PruneToDepthWithCancellation(
					plan.ProjectedTree,
					effectiveDepth.Value,
					cancellationToken);
			using var treeWriter = new McpBoundedLineTextWriter(MaximumTreeLines, MaximumTreeCharacters);
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
					var guidance = !depthFit.FullTreeFits
						? $"pass max_depth: {depthFit.DeepestCompleteDepth} for a complete document, " +
						  "or narrow paths, include_patterns, and exclude_patterns, then retry."
						: "narrow paths, include_patterns, or exclude_patterns, then retry.";
					throw new McpToolException(
						McpErrorCodes.PayloadTruncated,
						$"{McpErrorCodes.PayloadTruncated}: the {format.ToString().ToLowerInvariant()} tree exceeds " +
						$"the {MaximumTreeLines}-line or {MaximumTreeCharacters}-character result limit; {guidance}");
				}
			}

			var treeTruncationNotice = treeWriter.IsTruncated
				? "[Tree truncated at 2000 lines or 50000 characters. Narrow paths, include_patterns, exclude_patterns, or max_depth.]"
				: automaticDepth is { } selectedDepth
					? $"[Tree limited to depth {selectedDepth} of {depthFit.FullDepth} to fit {MaximumTreeLines} lines; " +
					  "pass max_depth or include_patterns for a subtree.]"
					: null;
			return McpToolResults.TextSuccess(AppendTrustedNotices(
				McpSpotlight.Wrap(treeWriter.Text),
				treeTruncationNotice,
				McpTrustedDiagnosticFormatter.FormatWarnings(plan),
				FormatNameSearchNotice(plan, paths),
				SelectionNotices(
					plan,
					includeFilters: true,
					new McpSelectionNoticeContext(
						HasPaths: HasItems(paths),
						HasPatterns: HasItems(includePatterns) || HasItems(excludePatterns),
						HasRootOnlyPattern: HasRootOnlyPattern(includePatterns)))));
		}, cancellationToken);

	[Description(
		"Measures a selection before packaging: transformed content, estimates, canonical content/text document size, and largest files. Use it to choose pack_context filters or max_tokens; use get_tree for structure and pack_context for actual content. Returns structured measured-versus-estimated metrics after required protection. project comes from list_projects. Key parameters: detail=full|compact|signatures, top_files=1..1000, git_scope, paths, patterns, profile, max_file_bytes. detail_by_pattern overrides detail per file; the last matching entry wins. max_tokens adds admission: the files that budget admits, from the same greedy pass pack_context uses, producing no content. Use it to pick a budget, not before every pack. rank and focus require max_tokens.")]
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
			var detailOverrides = McpDetailOverrides.Parse(arguments);
			var maximumEstimatedTokens = arguments.OptionalInt64("max_tokens", 1, long.MaxValue);
			var rank = ParseRank(arguments.OptionalString("rank"));
			var focusSpecified = request.Params.Arguments?.ContainsKey("focus") == true;
			var focus = focusSpecified
				? arguments.RequiredStringOrArray(
					"focus",
					maximumItems: 16,
					maximumItemScalarValues: McpProjectService.MaximumRequestedPathLength)
				: null;
			// An ordering that is neither returned nor used must not be computed, so both ordering
			// inputs are refused unless there is a budget for them to order.
			if (maximumEstimatedTokens is null && (rank is not null || focusSpecified))
			{
				throw new McpToolException(
					McpErrorCodes.InvalidArguments,
					$"{McpErrorCodes.InvalidArguments}: rank and focus are valid on analyze only together " +
					"with max_tokens.");
			}
			if (focusSpecified && rank is null)
			{
				throw new McpToolException(
					McpErrorCodes.InvalidArguments,
					$"{McpErrorCodes.InvalidArguments}: focus is valid only together with rank: \"importance\".");
			}
			var selection = await BuildSelectionAsync(
				arguments,
				cancellationToken,
				includeOutputMetrics: false).ConfigureAwait(false);
			var plan = Projects.ApplyDetailOverrides(selection.Plan, detailOverrides, cancellationToken);
			operationProgress.Milestone(
				10,
				$"scanning files {plan.IncludedFiles.Count}/{plan.IncludedFiles.Count}");
			var effectiveDetail = Projects.ResolveDetail(plan, detail);
			var detailMix = ContentDetailSelection.Resolve(plan.Selection, effectiveDetail.Kinds) is { } analyzePolicy
				? ContentDetailMix.Create(
					analyzePolicy,
					plan.SourceRoot,
					plan.IncludedFiles,
					cancellationToken)
				: null;
			operationProgress.Milestone(11, $"transforming content 0/{plan.IncludedFiles.Count}");
			await using var prepared = await Projects.MeasureAsync(
					plan,
					detail,
					operationProgress.Measure("transforming content", 12, 59),
					cancellationToken)
				.ConfigureAwait(false);
			operationProgress.Milestone(
				60,
				$"transforming content {plan.IncludedFiles.Count}/{plan.IncludedFiles.Count}");
			operationProgress.Milestone(61, $"analyzing content 0/{plan.IncludedFiles.Count}");
			var largest = new TopFileRanking(topFileCount);
			var uninspectedPaths = prepared.UnscannablePaths.ToHashSet(
				ProjectTreePathIdentity.CanonicalComparer);
			var estimatedPaths = new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer);
			long estimatedContentCharacters = 0;
			var metrics = prepared.GetTransformedMetrics();
			var analyzedFiles = 0;
			var analysisProgress = operationProgress.Measure("analyzing content", 62, 98);
			foreach (var fileMetrics in prepared.TransformedFileMetrics)
			{
				cancellationToken.ThrowIfCancellationRequested();
				largest.Add(
					fileMetrics.Path,
					CodeCompressionSnapshot.EstimateTokens(fileMetrics.CharCount));
				if (fileMetrics.IsEstimated)
				{
					estimatedPaths.Add(fileMetrics.Path);
					estimatedContentCharacters =
						estimatedContentCharacters > long.MaxValue - fileMetrics.CharCount
							? long.MaxValue
							: estimatedContentCharacters + fileMetrics.CharCount;
				}
				analyzedFiles++;
				analysisProgress.Report(new ProjectCopyExportProgress(
					analyzedFiles,
					plan.IncludedFiles.Count,
					BytesWritten: 0,
					Percentage: analyzedFiles * 100d / Math.Max(1, plan.IncludedFiles.Count)));
			}
			operationProgress.Milestone(
				99,
				$"analyzing content {plan.IncludedFiles.Count}/{plan.IncludedFiles.Count}");
			ProjectContextTokenBudgetReport? admissionBudget = null;
			if (maximumEstimatedTokens is not null)
			{
				var ranking = rank is null
					? null
					: await RankForAdmissionAsync(plan, focus, cancellationToken).ConfigureAwait(false);
				// The same cost-and-admission service a pack uses. There is no second greedy pass,
				// which is what lets the two agree by construction rather than by coincidence.
				var admission = await new ProjectContextTokenAdmissionService(Projects.DocumentService)
					.AdmitMeasuredAsync(
						plan,
						ProjectContextView.Content,
						ProjectContextDocumentFormat.Text,
						maximumEstimatedTokens.Value,
						prepared,
						ranking,
						cancellationToken)
					.ConfigureAwait(false);
				admissionBudget = admission.WriteResult.TokenBudget;
			}
			var allTop = largest.Project(item =>
			{
				var topFile = new Dictionary<string, object>(3, StringComparer.Ordinal)
				{
					["path"] = McpProjectService.ToRelative(plan.SourceRoot, item.Path),
					["tokens"] = item.Tokens,
					["estimated"] = estimatedPaths.Contains(item.Path)
				};
				if (uninspectedPaths.Contains(item.Path))
					topFile["uninspected"] = true;
				return topFile;
			});
			var top = new List<Dictionary<string, object>>(allTop.Length);
			var topCharacters = 2;
			foreach (var item in allTop)
			{
				var itemCharacters = JsonSerializer.Serialize(item).Length + (top.Count == 0 ? 0 : 1);
				if (topCharacters + itemCharacters > MaximumAnalyzeTopFilesCharacters)
					break;
				top.Add(item);
				topCharacters += itemCharacters;
			}
			var topFilesRemaining = allTop.Length - top.Count;
			var totalCharacters = metrics.Chars > long.MaxValue - estimatedContentCharacters
				? long.MaxValue
				: metrics.Chars + estimatedContentCharacters;
			var analysisMetrics = ExportOutputMetricsCalculator.FromOrderedContentFilesForAnalysis(
				prepared.TransformedFileMetrics,
				plan.IncludedFiles,
				plan.SourceRoot,
				Projects.ResolveProtectedDocumentRoot(plan));
			// Echo the effective exclusion state so both the agent and a human reading the
			// transcript always see which toggles shaped this measurement.
			var activeExclusions = ProjectSelectionTokens
				.OrderExclusions(plan.Selection.Exclusions ?? [])
				.Select(ProjectSelectionTokens.ToToken)
				.ToArray();
			var envelope = new Dictionary<string, object>(10, StringComparer.Ordinal)
			{
				["files"] = plan.IncludedFiles.Count,
				["characters"] = totalCharacters,
				["tokens"] = CodeCompressionSnapshot.EstimateTokens(totalCharacters),
				["detail"] = effectiveDetail.Token,
				["contentMetrics"] = new
				{
					measured = new
					{
						files = analysisMetrics.ContentOnly.Measured.Files,
						lines = analysisMetrics.ContentOnly.Measured.Lines,
						characters = analysisMetrics.ContentOnly.Measured.Characters,
						tokens = analysisMetrics.ContentOnly.Measured.Tokens
					},
					estimated = new
					{
						files = analysisMetrics.ContentOnly.Estimated.Files,
						characters = analysisMetrics.ContentOnly.Estimated.Characters,
						tokens = analysisMetrics.ContentOnly.Estimated.Tokens
					}
				},
				["documentMetrics"] = new
				{
					view = analysisMetrics.Document.View,
					format = analysisMetrics.Document.Format,
					lines = analysisMetrics.Document.Lines,
					characters = analysisMetrics.Document.Characters,
					tokens = analysisMetrics.Document.Tokens,
					estimated = analysisMetrics.Document.IsEstimated
				},
				["exclusions"] = activeExclusions,
				["topFiles"] = top,
				["topFilesTruncated"] = topFilesRemaining > 0,
				["topFilesRemaining"] = topFilesRemaining
			};
			if (admissionBudget is not null)
				envelope["admission"] = BuildAdmission(admissionBudget, effectiveDetail.Token);
			envelope["protection"] = new
			{
				secrets = "always",
				privateData = Projects.HidePrivateData ? "enabled" : "disabled"
			};
			if (plan.SourceIdentity is { SourceType: ProjectSourceType.GitClone } sourceIdentity)
			{
				envelope["remote"] = new
				{
					commit = sourceIdentity.CommitHash ?? "unknown",
					branch = sourceIdentity.Branch ?? "default"
				};
			}
			if (prepared.CompressionSnapshot?.Availability is
			    { IsUnavailable: true, PrimaryReason: { Length: > 0 } reason } availability)
			{
				envelope["compressionUnavailable"] = new
				{
					reason,
					languages = availability.Failures
						.Where(static failure => failure.LanguageId is not null)
						.Select(static failure => failure.LanguageId!)
						.Distinct(StringComparer.Ordinal)
						.Order(StringComparer.Ordinal)
						.ToArray()
				};
			}
			await operationProgress.CompleteAsync(100, "building analysis").ConfigureAwait(false);
			// The budget report goes into the trusted notices rather than the structured block: the
			// first text block has to stay a byte-identical serialization of structuredContent.
			var formattedAdmissionReport = admissionBudget is null
				? null
				: FormatTokenBudgetReport(admissionBudget);
			var notices = CombineTrustedNotices(
				FormatUnscannableNotice(prepared.UnscannableFiles, UnscannableResultKind.Analysis),
				FormatDetailMix(detailMix),
				formattedAdmissionReport,
				FormatCompressionUnavailable(prepared.CompressionSnapshot),
				McpTrustedDiagnosticFormatter.FormatWarnings(plan),
				SelectionNotices(plan, includeFilters: false, selection.NoticeContext, includeProtection: false));
			return McpToolResults.StructuredSuccess(
				envelope,
				notices,
				(structuredCharacters, trailer) => admissionBudget is null
					? trailer
					// The reply is the spotlighted structured block plus these notices, so both are
					// counted: measuring only the notices would understate it by the whole plan.
					: AppendBudgetAccounting(
						trailer ?? string.Empty,
						admissionBudget,
						formattedAdmissionReport!.Length,
						additionalReplyCharacters: structuredCharacters));
		}, cancellationToken);

	[Description(
		"Builds multi-file project context. Use it after get_tree, search_project, or analyze; use get_file instead for one file. Returns inline untrusted project data, or pack_id plus a preview when output exceeds 50,000 characters; page that result with read_pack. Values: detail=full|compact|signatures; view=tree|content|tree-content; format=markdown|text|json|xml; rank=importance; git_scope=staged|changes|diff:<ref>..<ref>. expand_related also packs the resolved dependency neighbours of its seeds and only narrows. focus requires rank=importance; max_tokens applies greedy content admission and reports heuristic token estimates, not tokenizer counts. detail_by_pattern overrides detail per file; the last matching entry wins, so list general globs first.")]
	public Task<CallToolResult> PackContext(
		RequestContext<CallToolRequestParams> request,
		CancellationToken cancellationToken) =>
		RunProjectAsync(async () =>
		{
			var operationProgress = new McpProgressReporter(request, cancellationToken);
			operationProgress.Milestone(1, "selecting files");
			var arguments = McpJsonArguments.Create(request.Params, packArgumentNames);
			var detail = McpDetailPolicy.Parse(arguments.OptionalString("detail"));
			var detailOverrides = McpDetailOverrides.Parse(arguments);
			var maximumEstimatedTokens = arguments.OptionalInt64("max_tokens", 1, long.MaxValue);
			var format = ParseFormat(arguments.OptionalString("format") ?? "markdown");
			var view = ParseView(arguments.OptionalString("view") ?? "tree-content");
			var rank = ParseRank(arguments.OptionalString("rank"));
			var focusSpecified = request.Params.Arguments?.ContainsKey("focus") == true;
			var focus = focusSpecified
				? arguments.RequiredStringOrArray(
					"focus",
					maximumItems: 16,
					maximumItemScalarValues: McpProjectService.MaximumRequestedPathLength)
				: null;
			if (focusSpecified && rank is null)
			{
				throw new McpToolException(
					McpErrorCodes.InvalidArguments,
					$"{McpErrorCodes.InvalidArguments}: focus is valid only together with rank: \"importance\".");
			}
			if (rank is not null && view == ProjectContextView.Tree)
			{
				throw new McpToolException(
					McpErrorCodes.InvalidArguments,
					$"{McpErrorCodes.InvalidArguments}: rank is valid only when pack_context includes file content.");
			}
			if (detailOverrides is not null && view == ProjectContextView.Tree)
			{
				throw new McpToolException(
					McpErrorCodes.InvalidArguments,
					$"{McpErrorCodes.InvalidArguments}: {McpDetailOverrides.ParameterName} is valid only when " +
					"pack_context includes file content.");
			}
			var expansionRequest = McpRelatedExpansion.Parse(arguments);
			var selection = await BuildSelectionAsync(
					arguments,
					cancellationToken,
					includeOutputMetrics:
						view == ProjectContextView.Tree &&
						format is ProjectContextDocumentFormat.Json or ProjectContextDocumentFormat.Xml)
				.ConfigureAwait(false);
			var plan = selection.Plan;
			// Expansion runs against the plan the filters produced and can only remove from it, so
			// a neighbour outside the effective selection has no way in. It also runs before focus
			// is resolved, which is what makes a focus seed the expansion did not admit fail with
			// the same error any unselected path gets.
			var expansionResult = expansionRequest is null
				? null
				: await McpRelatedExpansion.ExpandAsync(
						Projects.DependencyFactsEngine,
						plan,
						Projects.ResolveRequestedFiles(plan, expansionRequest.Seeds, cancellationToken)
							.Select(seed => McpProjectService.ToRelative(plan.SourceRoot, seed))
							.ToArray(),
						expansionRequest,
						operationProgress.MeasureFacts("Indexing dependency facts", 2, 8),
						cancellationToken)
					.ConfigureAwait(false);
			if (expansionResult is not null)
			{
				plan = await Projects
					.NarrowSelectionAsync(plan, expansionResult.RelativePaths, cancellationToken)
					.ConfigureAwait(false);
			}

			var selectedFileCount = plan.IncludedFiles.Count;
			var focusSeeds = focus is null
				? null
				: Projects.ResolveRequestedFiles(plan, focus, cancellationToken)
					.Select((path, index) => new FocusRankingSeedRequest(focus[index], path))
					.ToArray();
			// A pack is the answer many agents read instead of get_tree, so it carries the same
			// effective-filters footer next to its tree.
			var trustedPlanWarnings = CombineTrustedNotices(
				FormatExpansionNotice(expansionResult),
				McpTrustedDiagnosticFormatter.FormatWarnings(plan),
				SelectionNotices(plan, includeFilters: true, selection.NoticeContext));
			operationProgress.Milestone(
				10,
				$"scanning files {plan.IncludedFiles.Count}/{plan.IncludedFiles.Count}");
			var effectiveDetail = Projects.ResolveDetail(plan, detail);
			plan = Projects.ApplyDetail(plan, effectiveDetail, detailOverrides, cancellationToken);
			var rankingService = rank is null
				? null
				: new ImportanceRankingService(
						Projects.DependencyFactsEngine,
						new ProjectGitHistoryReader());
			ImportanceRankingReport? ranking = null;
			if (rankingService is not null)
			{
				var rankingProgress = new Progress<ImportanceRankingProgress>(value =>
				{
					var total = Math.Max(1, value.Total);
					var fraction = Math.Clamp((double)value.Completed / total, 0, 1);
					var (start, end, label) = value.Stage switch
					{
						ImportanceRankingStage.IndexingFacts => (11d, 20d, "indexing ranking facts"),
						ImportanceRankingStage.ReadingHistory => (21d, 23d, "reading ranking history"),
						ImportanceRankingStage.ComputingPriorities => (24d, 29d, "computing priorities"),
						_ => throw new ArgumentOutOfRangeException()
					};
					operationProgress.Milestone(
						start + fraction * (end - start),
						$"{label} {value.Completed}/{value.Total}");
				});
				ranking = focusSeeds is null
					? await rankingService.RankAsync(
							plan.SourceRoot,
							plan.IncludedFiles,
							rankingProgress,
							cancellationToken)
						.ConfigureAwait(false)
					: await rankingService.RankAsync(
							plan.SourceRoot,
							plan.IncludedFiles,
							new FocusRankingRequest(focusSeeds),
							rankingProgress,
							cancellationToken)
						.ConfigureAwait(false);
			}
			ProjectContextWriteResult? admissionResult = null;
			await using var measured = maximumEstimatedTokens is not null && view != ProjectContextView.Tree
				? await Projects.MeasureAsync(
						plan,
						McpDetailLevel.Full,
						operationProgress.Measure("measuring content", 30, 45),
						cancellationToken)
					.ConfigureAwait(false)
				: null;
			if (measured is not null)
			{
				var admission = await new ProjectContextTokenAdmissionService(Projects.DocumentService)
					.AdmitMeasuredAsync(
						plan,
						view,
						format,
						maximumEstimatedTokens!.Value,
						measured,
						ranking,
						cancellationToken)
					.ConfigureAwait(false);
				plan = admission.Plan;
				admissionResult = admission.WriteResult;
			}
			// Computed after admission, so the reported mix describes what the pack actually carries
			// rather than a selection a budget has since narrowed.
			var detailMix = ContentDetailSelection.Resolve(plan.Selection) is { } packPolicy
				? ContentDetailMix.Create(
					packPolicy,
					plan.SourceRoot,
					plan.IncludedFiles,
					cancellationToken)
				: null;
			await McpProjectService.EnsureRankingSourcesCurrentAsync(
				ranking,
				plan.IncludedFiles,
				cancellationToken).ConfigureAwait(false);
			var outputPlan = WithoutWarningDiagnostics(plan);
			var transformedFileCount = view == ProjectContextView.Tree ? 0 : plan.IncludedFiles.Count;
			operationProgress.Milestone(30, $"transforming content 0/{transformedFileCount}");
			await using var prepared = view == ProjectContextView.Tree
				? null
				: await Projects.PrepareAsync(
						plan,
						McpDetailLevel.Full,
						operationProgress.Measure("transforming content", 31, 64),
						cancellationToken,
						captureTransformedMetrics: format is
							ProjectContextDocumentFormat.Json or ProjectContextDocumentFormat.Xml)
					.ConfigureAwait(false);
			operationProgress.Milestone(
				65,
				$"transforming content {transformedFileCount}/{transformedFileCount}");
			// Writing still accounts for every selected file in the preserved budget report,
			// including entries denied before content materialization.
			var writtenFileCount = view == ProjectContextView.Tree ? 0 : selectedFileCount;
			operationProgress.Milestone(66, $"writing pack 0/{writtenFileCount}");
			ProjectContextWriteResult? writeResult = null;
			var pack = await packs.CreateAsync(
				async (stream, token) =>
				{
					var writeProgress = operationProgress.Measure("writing pack", 67, 99);
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
								maximumEstimatedTokens: maximumEstimatedTokens,
								ranking: ranking)
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
							maximumEstimatedTokens,
							ranking,
							admissionResult?.TokenBudget,
							preserveContentMetrics: admissionResult is not null)
						.ConfigureAwait(false);
					if (admissionResult is not null)
						writeResult = writeResult with { UnscannableFiles = admissionResult.UnscannableFiles };
				},
				cancellationToken).ConfigureAwait(false);
			var retainPack = false;
			try
			{
				var formattedBudgetReport = writeResult?.TokenBudget is { } completedBudget
					? FormatTokenBudgetReport(completedBudget)
					: null;
				if (pack.Characters <= MaximumInlinePackCharacters)
				{
					var content = await File.ReadAllTextAsync(pack.Path, cancellationToken).ConfigureAwait(false);
					var inlineMessage = AppendTrustedNotices(BuildSpotlightedPackContent(
						content,
						formattedBudgetReport),
						FormatPackEvictions(pack),
						FormatRankingReport(writeResult?.Ranking, writeResult?.TokenBudget),
						FormatUnscannableNotice(
							writeResult?.UnscannableFiles,
							UnscannableResultKind.Pack),
						FormatDetailMix(detailMix),
						FormatCompressionUnavailable(prepared?.CompressionSnapshot),
						trustedPlanWarnings);
					if (writeResult?.TokenBudget is { } inlineBudget)
						inlineMessage = AppendBudgetAccounting(
							inlineMessage,
							inlineBudget,
							formattedBudgetReport!.Length);
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
					formattedBudgetReport,
					FormatUnscannableNotice(
						writeResult?.UnscannableFiles,
						UnscannableResultKind.Pack),
					CombineTrustedNotices(
						FormatRankingReport(writeResult?.Ranking, writeResult?.TokenBudget),
						FormatDetailMix(detailMix),
						FormatCompressionUnavailable(prepared?.CompressionSnapshot),
						trustedPlanWarnings));
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

	[Description(
		"Reads one page of a stored result created by pack_context or related_files in this server process. Use it for a returned pack_id; use pack_context instead, or related_files for dependency results, when an id is absent or expired. Returns untrusted result data up to 1,000 lines or 50,000 characters plus trusted continuation or range-clamp notes. Required: pack_id. Optional start_line and end_line are inclusive 1-based integers or numeric strings; start_column continues within start_line using 1-based Unicode characters.")]
	public Task<CallToolResult> ReadPack(
		RequestContext<CallToolRequestParams> request,
		CancellationToken cancellationToken) =>
		ExecuteAsync(async () =>
		{
			var arguments = McpJsonArguments.Create(request.Params, ReadPackArgumentNames);
			var packId = arguments.RequiredString("pack_id");
			var start = arguments.OptionalInteger("start_line", 1, int.MaxValue);
			var end = arguments.OptionalInteger("end_line", 1, int.MaxValue);
			var startColumn = arguments.OptionalInteger("start_column", 1, int.MaxValue);
			ValidateLineRange(start, end);
			await using var packLease = packs.OpenReadDocument(packId);
			var pack = packLease.Document;
			var page = await ReadFilePageAsync(
					pack,
					packLease.Stream,
					start,
					end,
					startColumn,
					cancellationToken)
				.ConfigureAwait(false);
			var rangeNotice = FormatLineRangeNotice(page, end);
			var characterLimitNotice = page.CharacterLimitReached
				? "[The current line exceeded the 50000-character response cap; use search_project to narrow the source.]"
				: null;
			return McpToolResults.TextSuccess(
				AppendTrustedNotices(McpSpotlight.Wrap(page.Text), rangeNotice, characterLimitNotice),
				advertiseLargeResult: true);
		});

	[Description(
		"Searches safe transformed project text with a timed .NET regular expression. It matches file content, never paths; find files by name with get_tree include_patterns. Use it to locate symbols or phrases; use related_files instead for dependency links. Returns path:line:text matches, merged context groups separated by --, exact match and file counts, and the count of additional matches beyond max_results; line numbers refer to that text, and generated redaction replacements never match. Key parameters: pattern; paths narrows to literal files or directories; context_lines=0..20; ignore_case=true|false; max_results=1..200; git_scope=staged|changes|diff:<ref>..<ref>; patterns and max_file_bytes narrow further. Read several hits with one batched get_file requests call.")]
	public Task<CallToolResult> SearchProject(
		RequestContext<CallToolRequestParams> request,
		CancellationToken cancellationToken) =>
		RunProjectAsync(async () =>
		{
			var arguments = McpJsonArguments.Create(request.Params, searchArgumentNames);
			var pattern = arguments.RequiredString("pattern", allowWhitespace: true);
			var contextLines = arguments.OptionalInteger("context_lines", 0, 20) ?? 2;
			var ignoreCase = arguments.OptionalBoolean("ignore_case", true);
			var maximumResults = arguments.OptionalInteger("max_results", 1, 200) ?? 50;
			var regex = new McpSearchRegex(pattern, ignoreCase);
			var paths = ParsePaths(arguments);
			var includePatterns = arguments.OptionalStringArray("include_patterns");
			var excludePatterns = arguments.OptionalStringArray("exclude_patterns");

			var plan = await Projects.BuildPlanAsync(
				arguments.OptionalString("project"),
				arguments.OptionalString("branch"),
				paths,
				includePatterns,
				excludePatterns,
				profile: null,
				arguments.OptionalBoolean("tracked_only", false),
				arguments.OptionalString("git_scope"),
				arguments.OptionalInt64("max_file_bytes", 1, long.MaxValue),
				cancellationToken,
				includeOutputMetrics: false,
				exclusions: ParseExclusionsArgument(arguments),
				tolerateMissingPaths: true).ConfigureAwait(false);
			var output = new StringBuilder();
			var totalMatches = 0;
			var shownMatches = 0;
			var matchingFiles = 0;
			var responseLimitReached = false;
			var resultGroupTruncated = false;
			var inspectedFiles = new List<string>(plan.IncludedFiles.Count);
			long inspectedBytes = 0;
			foreach (var path in plan.IncludedFiles)
			{
				if (plan.EffectiveFileSizes?.TryGetValue(path, out var fileBytes) != true ||
				    fileBytes < 0 || fileBytes > MaximumSearchInspectedBytes - inspectedBytes)
				{
					break;
				}
				inspectedFiles.Add(path);
				inspectedBytes += fileBytes;
			}
			var inspectionBudgetReached = inspectedFiles.Count < plan.IncludedFiles.Count;
			await using var searched = await Projects.ConsumeSearchTextAsync(
				plan with { IncludedFiles = inspectedFiles },
				(file, token) =>
				{
					var scan = McpSearchTextScanner.Scan(
						file.Content,
						regex,
						contextLines,
						Math.Max(0, maximumResults - totalMatches),
						file.ReplacementRanges,
						token);
					totalMatches += scan.TotalMatches;
					if (scan.TotalMatches > 0)
						matchingFiles++;
					if (responseLimitReached)
						return ValueTask.CompletedTask;

					foreach (var match in scan.Matches)
					{
						var appended = AppendSearchResult(
							output,
							McpProjectService.ToRelative(plan.SourceRoot, file.Path),
							file.Content,
							match,
							MaximumSearchContentCharacters);
						shownMatches += appended.WrittenMatches;
						if (appended.Truncated)
						{
							responseLimitReached = true;
							resultGroupTruncated = true;
							break;
						}
					}
					return ValueTask.CompletedTask;
				},
				cancellationToken).ConfigureAwait(false);
			var additionalMatchesNotice = totalMatches > shownMatches
				? $"[{totalMatches - shownMatches} additional matches not shown; narrow the pattern or filters.]"
				: null;
			// Sizing information is only worth its characters when the caller did not
			// receive every match the pattern found. A group cut in its trailing context
			// lines withheld no match and gets the cap notice alone.
			var searchTotalsNotice = totalMatches > shownMatches
				? $"[Search totals] matches={totalMatches.ToString(CultureInfo.InvariantCulture)} · " +
				  $"files={matchingFiles.ToString(CultureInfo.InvariantCulture)}"
				: null;
			// An empty search result must say whether nothing matched or nothing was searched;
			// the count is trusted data, the file names never are.
			var noMatches = totalMatches == 0 && plan.IncludedFiles.Count > 0
				? $"[No matches] The pattern matched nothing in {plan.IncludedFiles.Count} selected file(s) ({McpEffectiveFilters.Describe(plan)})."
				: null;
			return McpToolResults.TextSuccess(AppendTrustedNotices(
				McpSpotlight.Wrap(output.ToString().TrimEnd()),
				FormatUnscannableNotice(searched.UnscannableFiles, UnscannableResultKind.Search),
				McpTrustedDiagnosticFormatter.FormatWarnings(plan),
				noMatches,
				FormatNameSearchNotice(plan, paths, pattern, totalMatches),
				additionalMatchesNotice,
				searchTotalsNotice,
				inspectionBudgetReached
					? "[Search incomplete] The inspected-text byte budget was reached; additional selected files were not searched and match counts are partial."
					: null,
				resultGroupTruncated ? SearchContentCapNotice : null,
				SelectionNotices(
					plan,
					includeFilters: false,
					new McpSelectionNoticeContext(
						HasPaths: HasItems(paths),
						HasPatterns: HasItems(includePatterns) || HasItems(excludePatterns),
						HasRootOnlyPattern: HasRootOnlyPattern(includePatterns)))));
		}, cancellationToken);

	[Description(
		"Finds statically evidenced dependencies and dependents for one to 16 seed files without widening selection. Use it after search_project or get_tree; use search_project instead for textual references, or get_file for content. Returns paths, evidence, resolution status, token estimates, language-limited coverage, configuration diagnostics, and scope; results over 50,000 characters use read_pack. Key parameters: path=string|array, direction=dependencies|dependents|both, profile, git_scope=staged|changes|diff:<ref>..<ref>, patterns, and max_file_bytes.")]
	public Task<CallToolResult> RelatedFiles(
		RequestContext<CallToolRequestParams> request,
		CancellationToken cancellationToken) =>
		RunProjectAsync(async () =>
		{
			var arguments = McpJsonArguments.Create(request.Params, relatedArgumentNames);
			var seeds = arguments.RequiredStringOrArray(
				"path",
				maximumItems: 16,
				maximumItemScalarValues: McpProjectService.MaximumRequestedPathLength);
			var direction = ParseDependencyDirection(arguments.OptionalString("direction") ?? "both");
			var includePatterns = arguments.OptionalStringArray("include_patterns");
			var excludePatterns = arguments.OptionalStringArray("exclude_patterns");
			var plan = await Projects.BuildPlanAsync(
				arguments.OptionalString("project"),
				arguments.OptionalString("branch"),
				paths: null,
				includePatterns,
				excludePatterns,
				arguments.OptionalString("profile"),
				arguments.OptionalBoolean("tracked_only", false),
				arguments.OptionalString("git_scope"),
				arguments.OptionalInt64("max_file_bytes", 1, long.MaxValue),
				cancellationToken,
				includeOutputMetrics: false,
				exclusions: ParseExclusionsArgument(arguments)).ConfigureAwait(false);
			var resolvedSeeds = Projects.ResolveRequestedFiles(plan, seeds, cancellationToken);
			var relativeSeeds = resolvedSeeds
				.Select(seed => McpProjectService.ToRelative(plan.SourceRoot, seed))
				.ToArray();
			var progress = new McpProgressReporter(request, cancellationToken);
			progress.Milestone(5, "Preparing dependency manifest");
			var related = await Projects.DependencyFactsEngine.FindRelatedAsync(
				plan.SourceRoot,
				plan.IncludedFiles,
				relativeSeeds,
				direction,
				progress.MeasureFacts("Indexing dependency facts", 5, 90),
				cancellationToken).ConfigureAwait(false);
			await progress.CompleteAsync(100, related.Index.Metrics.ResolutionCacheHit
				? "Dependency index reused"
				: "Dependency index complete").ConfigureAwait(false);

			var coverage = related.Index.Coverage;
			var resolution = CountRelatedResolution(related.Index, relativeSeeds, direction);
			var configurationData = FormatDependencyConfigurationData(coverage.ConfigurationDiagnostics);
			var selectionContext = new McpSelectionNoticeContext(
				HasPaths: false,
				HasPatterns: HasItems(includePatterns) || HasItems(excludePatterns),
				HasRootOnlyPattern: HasRootOnlyPattern(includePatterns));
			var noRelatedNotice = resolution.Resolved == 0
				? resolution.Unresolved == 0
					? "[No related files] in the effective selection."
					: $"[No related files] in the effective selection; unresolved references={resolution.Unresolved.ToString(CultureInfo.InvariantCulture)}."
				: null;
			var trustedNotices = CombineTrustedNotices(
				$"[Resolution] resolved={resolution.Resolved.ToString(CultureInfo.InvariantCulture)} · ambiguous={resolution.Ambiguous.ToString(CultureInfo.InvariantCulture)} · unresolved={resolution.Unresolved.ToString(CultureInfo.InvariantCulture)} · external={resolution.External.ToString(CultureInfo.InvariantCulture)}",
				$"[Facts coverage] files={coverage.Files}, supported={coverage.Supported}, unsupported={coverage.Unsupported}, extraction-failed={coverage.ExtractionFailed}; supported means facts were extracted for a recognized language, unsupported means no supported extractor was available",
				FormatDependencyConfigurationDiagnostics(coverage.ConfigurationDiagnostics),
				$"[Search scope] files={plan.IncludedFiles.Count}",
				SelectionNotices(plan, includeFilters: true, selectionContext),
				FormatSafeNoFactsNotice(related.Seeds),
				noRelatedNotice);
			using var relatedBody = new StringWriter(CultureInfo.InvariantCulture);
			WriteRelatedFiles(relatedBody, related, direction, configurationData, cancellationToken);
			var protectedBody = Projects.RedactSyntheticText(
				plan,
				resolvedSeeds[0],
				relatedBody.ToString(),
				cancellationToken);
			using var inline = new McpBoundedStringTextWriter(MaximumInlinePackCharacters);
			try
			{
				WriteRelatedMessage(inline, protectedBody, trustedNotices);
			}
			catch (McpLineLimitReachedException)
			{
				// The same deterministic formatter is replayed directly into bounded pack storage.
			}
			if (!inline.IsTruncated)
				return McpToolResults.TextSuccess(inline.Text, advertiseLargeResult: true);

			var pack = await packs.CreateAsync(
				async (stream, token) =>
				{
					await using var writer = new StreamWriter(
						stream,
						new UTF8Encoding(false),
						bufferSize: 16 * 1024,
						leaveOpen: true);
					WriteRelatedMessage(writer, protectedBody, trustedNotices);
					await writer.FlushAsync(token).ConfigureAwait(false);
				},
				cancellationToken).ConfigureAwait(false);
			return McpToolResults.TextSuccess(
				$"Related-files result stored as '{pack.Id}' ({pack.Characters} characters). " +
				"Call read_pack with this pack_id to read it.",
				advertiseLargeResult: true);
		}, cancellationToken);

	[Description(
		"Reads selected file text after mandatory secret and configured private-data replacement. Use it after get_tree or search_project; use pack_context for broad multi-file context. Pass path for one page, or requests for up to eight files and sixteen inclusive ranges; the forms are mutually exclusive. Send one batched call whenever you want more than one file or more than one range, as requests=[{\"path\":\"src/a.ts\",\"ranges\":[{\"start_line\":10,\"end_line\":30}]}]. Batch responses report ok, partial, not-returned, or unavailable for every range, merge overlaps, and share the 1,000-line/50,000-character limit. Coordinates refer to returned text after replacements; start_column continues a single-file page.")]
	public Task<CallToolResult> GetFile(
		RequestContext<CallToolRequestParams> request,
		CancellationToken cancellationToken) =>
		RunProjectAsync(async () =>
		{
			var arguments = McpJsonArguments.Create(request.Params, getFileArgumentNames);
			var requestSet = McpGetFileRequestSet.Parse(arguments);
			if (requestSet.IsBatch)
				return await GetFileBatchAsync(arguments, requestSet, cancellationToken).ConfigureAwait(false);

			// get_file honors the delegated set too: a file revealed by get_tree or
			// search_project under a per-call exclusions value must stay readable.
			var requestedPath = requestSet.Requests[0].Path;
			var start = arguments.OptionalInteger("start_line", 1, int.MaxValue);
			var end = arguments.OptionalInteger("end_line", 1, int.MaxValue);
			var startColumn = arguments.OptionalInteger("start_column", 1, int.MaxValue);
			ValidateLineRange(start, end);
			var plan = await Projects.BuildPlanAsync(
				arguments.OptionalString("project"),
				arguments.OptionalString("branch"),
				paths: [requestedPath],
				includePatterns: null,
				excludePatterns: null,
				profile: arguments.OptionalString("profile"),
				trackedOnly: false,
				gitScope: null,
				maximumFileBytes: null,
				cancellationToken,
				includeOutputMetrics: false,
				exclusions: ParseExclusionsArgument(arguments)).ConfigureAwait(false);
			var file = Projects.ResolveFile(plan, requestedPath);
			TransformedTextFile? transformed = null;
			await using var inspected = await Projects.ConsumeSearchTextAsync(
					plan with { IncludedFiles = [file] },
					(value, _) =>
					{
						transformed = value;
						return ValueTask.CompletedTask;
					},
					cancellationToken)
				.ConfigureAwait(false);
			if (transformed is null)
			{
				var classification = inspected.UnscannableFiles
					.FirstOrDefault(item => PathComparer.Default.Equals(item.Path, file))?.Classification ??
					FileContentClassification.Binary;
				var detail = classification == FileContentClassification.TooLarge
					? $"file size {new FileInfo(file).Length} bytes exceeds the mandatory redaction scan limit of {SecretRedactionOutputPreparer.MaximumScannableFileBytes} bytes"
					: $"file classification is {classification.ToString().ToLowerInvariant()}";
				throw new McpToolException(
					McpErrorCodes.PayloadTruncated,
					$"{McpErrorCodes.PayloadTruncated}: {detail}; content was not returned because it could not be inspected safely. " +
					$"Select a file no larger than {SecretRedactionOutputPreparer.MaximumScannableFileBytes} bytes or narrow the project before retrying.");
			}
			var page = McpTextRanges.Slice(
				transformed.Content,
				start,
				end,
				MaximumPageLines,
				MaximumPageCharacters,
				cancellationToken,
				startColumn);
			var rangeNotice = FormatLineRangeNotice(page, end);
			var characterLimitNotice = page.CharacterLimitReached
				? "[The current line exceeded the 50000-character response cap; use search_project to narrow the source.]"
				: null;
			return McpToolResults.TextSuccess(AppendTrustedNotices(
				McpSpotlight.Wrap(page.Text),
				rangeNotice,
				characterLimitNotice,
				FormatCompressionUnavailable(inspected.CompressionSnapshot),
				SelectionNotices(
					plan,
					includeFilters: false,
					new McpSelectionNoticeContext(HasPaths: true, HasPatterns: false))));
		}, cancellationToken);

	private async Task<CallToolResult> GetFileBatchAsync(
		McpJsonArguments arguments,
		McpGetFileRequestSet requestSet,
		CancellationToken cancellationToken)
	{
		var plan = await Projects.BuildPlanAsync(
			arguments.OptionalString("project"),
			arguments.OptionalString("branch"),
			paths: null,
			includePatterns: null,
			excludePatterns: null,
			profile: arguments.OptionalString("profile"),
			trackedOnly: false,
			gitScope: null,
			maximumFileBytes: null,
			cancellationToken,
			includeOutputMetrics: false,
			exclusions: ParseExclusionsArgument(arguments)).ConfigureAwait(false);

		var resolvedRequests = new List<McpResolvedFileReadRequest>(requestSet.Requests.Count);
		foreach (var item in requestSet.Requests)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				resolvedRequests.Add(new McpResolvedFileReadRequest(
					item,
					Projects.ResolveFile(plan, item.Path)));
			}
			catch (McpToolException exception) when (exception.Code is
			       McpErrorCodes.PathNotFound or McpErrorCodes.RootViolation)
			{
				resolvedRequests.Add(new McpResolvedFileReadRequest(item, PhysicalPath: null));
			}
		}

		var uniqueFiles = resolvedRequests
			.Where(static item => item.PhysicalPath is not null)
			.Select(static item => item.PhysicalPath!)
			.Distinct(PathComparer.Default)
			.Order(ProjectTreePathIdentity.CanonicalComparer)
			.ToArray();
		var transformed = new Dictionary<string, TransformedTextFile>(PathComparer.Default);
		await using var inspected = await Projects.ConsumeSearchTextAsync(
			plan with { IncludedFiles = uniqueFiles },
			(file, _) =>
			{
				transformed[file.Path] = file;
				return ValueTask.CompletedTask;
			},
			cancellationToken).ConfigureAwait(false);

		var rendered = RenderBatchFileReads(resolvedRequests, transformed, cancellationToken);
		return McpToolResults.TextSuccess(AppendTrustedNotices(
			McpSpotlight.Wrap(rendered.Text),
			rendered.Summary,
			rendered.UnavailableNotice,
			rendered.Continuations,
			FormatCompressionUnavailable(inspected.CompressionSnapshot),
			SelectionNotices(
				plan,
				includeFilters: false,
				new McpSelectionNoticeContext(HasPaths: true, HasPatterns: false))));
	}

	private static McpBatchFileReadResult RenderBatchFileReads(
		IReadOnlyList<McpResolvedFileReadRequest> requests,
		IReadOnlyDictionary<string, TransformedTextFile> transformed,
		CancellationToken cancellationToken)
	{
		var status = requests
			.SelectMany(static request => request.Request.Ranges)
			.ToDictionary(static range => (range.RequestIndex, range.RangeIndex), static _ => "unavailable");
		var unavailableReasons = new Dictionary<(int RequestIndex, int RangeIndex), string>();
		foreach (var request in requests)
		{
			var reason = request.PhysicalPath is null
				? "outside effective selection"
				: transformed.ContainsKey(request.PhysicalPath)
					? null
					: McpErrorCodes.PayloadTruncated;
			if (reason is null)
				continue;
			foreach (var range in request.Request.Ranges)
				unavailableReasons[(range.RequestIndex, range.RangeIndex)] = reason;
		}
		var groups = BuildMergedReadGroups(requests);
		var sectionBudgetLines = MaximumPageLines - status.Count - 1;
		var statusReserve = "Requests:\n" + string.Join('\n', status.Keys.Select(key =>
			$"{key.RequestIndex}.{key.RangeIndex} — not-returned" +
			(unavailableReasons.TryGetValue(key, out var reason) ? $" — {reason}" : string.Empty)));
		var sectionBudgetCharacters = MaximumPageCharacters - statusReserve.Length;
		var sections = new StringBuilder();
		var continuations = new List<string>();
		var rangeNotices = new List<string>();
		var usedLines = 0;

		foreach (var group in groups)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (group.PhysicalPath is null)
				continue;
			if (!transformed.TryGetValue(group.PhysicalPath, out var file))
				continue;

			var separator = sections.Length == 0 ? string.Empty : "\n\n";
			var requestIds = string.Join(", ", group.Ranges.Select(static range =>
				$"{range.RequestIndex}.{range.RangeIndex}"));
			var headerPrefix = $"File: {McpTextEscaping.EscapeSingleLine(group.DisplayPath)}\nRequests: {requestIds}\n";
			const string headerSuffix = "\n";
			var headerLines = 4;
			var availableLines = sectionBudgetLines - usedLines - (sections.Length == 0 ? 0 : 1) - headerLines;
			var availableCharacters = sectionBudgetCharacters - sections.Length - separator.Length -
			                          headerPrefix.Length - "Status: partial\nLines: 1-1 of 1\n".Length;
			if (availableLines <= 0 || availableCharacters <= 0)
			{
				foreach (var range in group.Ranges)
					status[(range.RequestIndex, range.RangeIndex)] = "not-returned";
				continue;
			}

			McpTextPage page;
			try
			{
				page = McpTextRanges.Slice(
					file.Content,
					group.StartLine,
					group.EndLine,
					availableLines,
					availableCharacters,
					cancellationToken);
			}
			catch (McpToolException exception) when (exception.Code == McpErrorCodes.InvalidRange)
			{
				foreach (var range in group.Ranges)
					status[(range.RequestIndex, range.RangeIndex)] = "not-returned";
				continue;
			}

			var sectionStatus = page.IsTruncated ? "partial" : "ok";
			var header = headerPrefix + $"Status: {sectionStatus}\nLines: {page.StartLine}-{page.EndLine} of {page.TotalLines}" + headerSuffix;
			var section = separator + header + page.Text;
			if (sections.Length + section.Length > sectionBudgetCharacters)
			{
				foreach (var range in group.Ranges)
					status[(range.RequestIndex, range.RangeIndex)] = "not-returned";
				continue;
			}

			sections.Append(section);
			usedLines += CountResponseLines(section);
			foreach (var range in group.Ranges)
				status[(range.RequestIndex, range.RangeIndex)] = sectionStatus;
			if (page.IsTruncated)
			{
				continuations.Add(
					$"[Batch continuation] requests={requestIds}; start_line={page.NextLine ?? page.EndLine + 1}" +
					(page.NextColumn is > 1 ? $"; start_column={page.NextColumn}" : string.Empty) + ".");
			}
			else if (group.EndLine > page.TotalLines)
			{
				rangeNotices.Add(
					$"[Range clamped] requests={requestIds}; end_line exceeded the file; returned through line {page.TotalLines}.");
			}
		}

		var statusText = "Requests:\n" + string.Join('\n', status.Select(pair =>
			$"{pair.Key.RequestIndex}.{pair.Key.RangeIndex} — {pair.Value}" +
			(unavailableReasons.TryGetValue(pair.Key, out var reason) ? $" — {reason}" : string.Empty)));
		var body = sections.Length == 0 ? statusText : statusText + "\n\n" + sections;
		var counts = status.Values.GroupBy(static value => value, StringComparer.Ordinal)
			.ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
		var summary = $"[Batch read] ok={GetCount("ok")} · partial={GetCount("partial")} · " +
		              $"not-returned={GetCount("not-returned")} · unavailable={GetCount("unavailable")}.";
		return new McpBatchFileReadResult(
			body,
			summary,
			GetCount("unavailable") == 0
				? null
				: $"[Batch unavailable] files={CountUnavailableFiles()} · ranges={GetCount("unavailable")}; " +
				  $"{McpErrorCodes.PayloadTruncated} marks mandatory-inspection failures; other entries are outside the effective selection.",
			continuations.Count == 0 && rangeNotices.Count == 0
				? null
				: string.Join('\n', continuations.Concat(rangeNotices)));

		int GetCount(string value) => counts.GetValueOrDefault(value);

		int CountUnavailableFiles() => unavailableReasons.Keys
			.Select(static key => key.RequestIndex)
			.Distinct()
			.Count();
	}

	private static IReadOnlyList<McpMergedFileReadGroup> BuildMergedReadGroups(
		IReadOnlyList<McpResolvedFileReadRequest> requests)
	{
		var groups = new List<McpMergedFileReadGroup>();
		foreach (var request in requests)
		{
			foreach (var range in request.Request.Ranges)
			{
				var matching = groups
					.Where(group => SameRequestedFile(group, request) &&
					                group.StartLine <= range.EndLine && range.StartLine <= group.EndLine)
					.ToArray();
				if (matching.Length == 0)
				{
					groups.Add(new McpMergedFileReadGroup(
						request.PhysicalPath,
						request.Request.Path,
						range.StartLine,
						range.EndLine,
						[range]));
					continue;
				}

				var target = matching[0];
				target.StartLine = Math.Min(target.StartLine, range.StartLine);
				target.EndLine = Math.Max(target.EndLine, range.EndLine);
				target.Ranges.Add(range);
				foreach (var duplicate in matching.Skip(1))
				{
					target.StartLine = Math.Min(target.StartLine, duplicate.StartLine);
					target.EndLine = Math.Max(target.EndLine, duplicate.EndLine);
					target.Ranges.AddRange(duplicate.Ranges);
					groups.Remove(duplicate);
				}
			}
		}
		return groups;

		static bool SameRequestedFile(McpMergedFileReadGroup group, McpResolvedFileReadRequest request) =>
			group.PhysicalPath is not null && request.PhysicalPath is not null
				? PathComparer.Default.Equals(group.PhysicalPath, request.PhysicalPath)
				: group.PhysicalPath is null && request.PhysicalPath is null &&
				  StringComparer.Ordinal.Equals(group.DisplayPath, request.Request.Path);
	}

	private static int CountResponseLines(string value)
	{
		var lines = value.Length == 0 ? 0 : 1;
		foreach (var character in value)
			if (character == '\n') lines++;
		return lines;
	}

	private static string? FormatLineRangeNotice(McpTextPage page, int? requestedEnd)
	{
		if (page.IsTruncated)
		{
			return $"[Showing lines {page.StartLine}-{page.EndLine} of {page.TotalLines}; " +
			       $"continue with start_line={page.NextLine ?? page.EndLine + 1}" +
			       (page.NextColumn is > 1 ? $" start_column={page.NextColumn}" : string.Empty) + ".]";
		}

		return requestedEnd > page.TotalLines
			? $"[Showing lines {page.StartLine}-{page.EndLine} of {page.TotalLines}; " +
			  $"end_line {requestedEnd} exceeded the file.]"
			: null;
	}

	private McpJsonArguments SelectionArguments(CallToolRequestParams request) =>
		McpJsonArguments.Create(request, selectionArgumentNames);

	private async Task<McpSelectionResult> BuildSelectionAsync(
		McpJsonArguments arguments,
		CancellationToken cancellationToken,
		bool includeOutputMetrics = true)
	{
		var paths = ParsePaths(arguments);
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
				HasPatterns: HasItems(includePatterns) || HasItems(excludePatterns),
				HasRootOnlyPattern: HasRootOnlyPattern(includePatterns)));
	}

	private string? SelectionNotices(
		ProjectContextPlan plan,
		bool includeFilters,
		McpSelectionNoticeContext request,
		bool includeProtection = true)
	{
		var selection = McpEffectiveFilters.SelectionNoticeParts(plan, agentExclusions, includeFilters, request);
		var protection = includeProtection
			? $"[Protection] secrets=always · private-data={(Projects.HidePrivateData ? "enabled" : "disabled")}."
			: null;
		// Two cases always answer in full: a response that has to explain an empty selection
		// names the filters that emptied it, and a max_file_bytes echo reports a value the
		// caller passed on this call rather than session state.
		var notices = serviceNotices.Prepare(
			NoticeIdentity(plan),
			selection.Filters,
			protection,
			alwaysSend: selection.EmptySelection is not null || plan.FileSizeFilter is not null);
		return CombineTrustedNotices(
			notices.Continuation,
			notices.Filters,
			selection.EmptySelection,
			notices.Protection,
			FormatRemoteNotice(plan));
	}

	/// <summary>
	/// The project a set of service notices describes. Remote checkouts are keyed by their safe
	/// address as well as their pinned root, and an unresolvable project yields no identity, which
	/// makes the memo send the full set.
	/// </summary>
	private static string? NoticeIdentity(ProjectContextPlan plan)
	{
		if (string.IsNullOrEmpty(plan.SourceRoot))
			return null;
		return plan.SourceIdentity is { } identity
			? string.Join(" ", plan.SourceRoot, identity.SourceType.ToString(), identity.RepositoryUrl ?? "")
			: plan.SourceRoot;
	}

	private static string? FormatRemoteNotice(ProjectContextPlan plan) =>
		plan.SourceIdentity is { SourceType: ProjectSourceType.GitClone } identity
			? $"[Remote] commit={FormatTrustedCommit(identity.CommitHash)}"
			: null;

	internal static string FormatTrustedCommit(string? commitHash)
	{
		if (commitHash is not { Length: >= 7 and <= 64 })
			return "unknown";

		foreach (var character in commitHash)
		{
			if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
				return "unknown";
		}

		return commitHash;
	}

	private static bool HasItems<T>(IReadOnlyCollection<T>? items) => items is { Count: > 0 };

	// A pattern is matched against the whole project-relative path, and without '/' every remaining
	// wildcard stays inside one segment, so such a pattern can only match a file directly in the
	// project root. '**' is the exception: it spans separators even without a trailing '/', so a
	// pattern carrying it already reaches any depth and must not be offered the same rewrite.
	private static bool HasRootOnlyPattern(IReadOnlyList<string>? includePatterns) =>
		includePatterns?.Any(static pattern =>
			!string.IsNullOrWhiteSpace(pattern) &&
			!pattern.Contains('/', StringComparison.Ordinal) &&
			!pattern.Contains("**", StringComparison.Ordinal)) == true;

	// The exclusions argument exists only on servers started with --allow-agent-exclusions;
	// everywhere else the allowlist rejects it, so a default server keeps the
	// narrowing-only contract byte for byte.
	private static IReadOnlySet<string> Allowed(bool includeExclusions, params string[] names) =>
		McpJsonArguments.FreezeAllowed(includeExclusions ? [.. names, "exclusions"] : names);

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
		_projectOperation.RunAsync(() => RunAndConfirmServiceNoticesAsync(operation), cancellationToken);

	/// <summary>
	/// Service notices count as reported only once they are in the text the caller receives.
	/// A stored pack, a truncated diagnostic tail, or a failed call therefore leaves the memo
	/// where it was, and the next response repeats the full set.
	/// </summary>
	private async Task<CallToolResult> RunAndConfirmServiceNoticesAsync(Func<Task<CallToolResult>> operation)
	{
		try
		{
			var result = await ExecuteAsync(operation).ConfigureAwait(false);
			serviceNotices.CommitDelivered(ResponseText(result));
			return result;
		}
		catch
		{
			serviceNotices.DiscardPending();
			throw;
		}
	}

	private static string ResponseText(CallToolResult result) =>
		string.Join('\n', result.Content.OfType<TextContentBlock>().Select(static block => block.Text));

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

	private static ProjectContextRank? ParseRank(string? token) => token switch
	{
		null => null,
		"importance" => ProjectContextRank.Importance,
		_ => throw new McpToolException(
			McpErrorCodes.InvalidArguments,
			$"{McpErrorCodes.InvalidArguments}: invalid rank '{token}'. Valid value: importance.")
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

	private static DependencyDirection ParseDependencyDirection(string token) => token switch
	{
		"dependencies" => DependencyDirection.Dependencies,
		"dependents" => DependencyDirection.Dependents,
		"both" => DependencyDirection.Both,
		_ => throw new McpToolException(
			McpErrorCodes.InvalidArguments,
			$"{McpErrorCodes.InvalidArguments}: invalid direction '{token}'. Valid values: dependencies, dependents, both.")
	};

	private static void WriteRelatedMessage(
		TextWriter output,
		string protectedBody,
		string? trustedNotices)
	{
		McpSpotlight.Write(output, writer => writer.Write(protectedBody));
		if (trustedNotices is not null)
		{
			output.Write("\n\n");
			output.Write(trustedNotices);
		}
	}

	private static void WriteRelatedFiles(
		TextWriter output,
		DependencyRelatedResult result,
		DependencyDirection direction,
		string? configurationData,
		CancellationToken cancellationToken)
	{
		var hasLine = false;
		void StartLine()
		{
			if (hasLine)
				output.Write(Environment.NewLine);
			hasLine = true;
		}

		foreach (var seed in result.Seeds)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (result.Seeds.Count > 1 || seed.NoFactsReason is not null)
			{
				StartLine();
				output.Write("Seed: ");
				output.Write(McpTextEscaping.EscapeSingleLine(seed.Seed));
			}
			if (seed.NoFactsReason is { Length: > 0 })
			{
				StartLine();
				output.Write("[No facts] ");
				output.Write(IsSafeNoFactsReason(seed.NoFactsReason)
						? "fixed dependency-engine status"
						: McpTextEscaping.EscapeSingleLine(seed.NoFactsReason));
				output.Write('.');
				continue;
			}
			if (direction is DependencyDirection.Dependencies or DependencyDirection.Both)
				WriteRelatedSection(output, StartLine, "Dependencies", seed.Dependencies, cancellationToken);
			if (direction is DependencyDirection.Dependents or DependencyDirection.Both)
				WriteRelatedSection(output, StartLine, "Dependents", seed.Dependents, cancellationToken);
		}
		if (!string.IsNullOrWhiteSpace(configurationData))
		{
			StartLine();
			output.Write(configurationData);
		}
	}

	private static void WriteRelatedSection(
		TextWriter output,
		Action startLine,
		string title,
		IReadOnlyList<RelatedFile> files,
		CancellationToken cancellationToken)
	{
		startLine();
		output.Write(title);
		output.Write(':');
		foreach (var file in files)
		{
			cancellationToken.ThrowIfCancellationRequested();
			startLine();
			output.Write(McpTextEscaping.EscapeSingleLine(file.Path));
			output.Write(" — ");
			for (var index = 0; index < file.Reasons.Count; index++)
			{
				if (index > 0) output.Write(" · ");
				output.Write(McpTextEscaping.EscapeSingleLine(file.Reasons[index]));
			}
			output.Write(" — ");
			output.Write(file.Status.ToString().ToLowerInvariant());
			output.Write(" — ");
			output.Write(file.EstimatedTokens.ToString(CultureInfo.InvariantCulture));
			output.Write(" tokens");
			if (file.CrossScope) output.Write(" — cross-scope");
			if (file.Candidates.Count > 1)
			{
				output.Write(" — candidates: ");
				for (var index = 0; index < file.Candidates.Count; index++)
				{
					if (index > 0) output.Write(", ");
					output.Write(McpTextEscaping.EscapeSingleLine(file.Candidates[index]));
				}
			}
		}
	}

	private static string BuildSpotlightedPackContent(
		string content,
		string? formattedBudgetReport)
	{
		if (formattedBudgetReport is null)
			return McpSpotlight.Wrap(content);

		return McpSpotlight.Wrap(content) + "\n\n" +
		       McpSpotlight.Wrap(formattedBudgetReport);
	}

	private static string BuildStoredPackResponse(
		McpPackDocument pack,
		string tree,
		bool treeWasTruncated,
		ProjectContextTokenBudgetReport? report,
		string? formattedBudgetReport,
		string? unscannableNotice,
		string? planWarnings)
	{
		var header = $"Pack stored as '{pack.Id}' ({pack.Characters} characters, {pack.Lines} lines). " +
		             "Call read_pack with this pack_id to read ranges, or search_project to locate source content.\n";
		var treePreview = TakeCompleteScalarPrefix(tree, MaximumStoredTreePreviewCharacters);
		var treePreviewWasTruncated = treeWasTruncated || treePreview.Length < tree.Length;
		var budgetReport = formattedBudgetReport is null
			? null
			: LimitResponseSegment(
				formattedBudgetReport,
				MaximumStoredBudgetReportCharacters,
				StoredBudgetReportTruncationNotice,
				forceMarker: false);
		var trustedNotices = CombineTrustedNotices(FormatPackEvictions(pack), unscannableNotice, planWarnings);
		if (trustedNotices is not null)
		{
			trustedNotices = LimitResponseSegment(
				trustedNotices,
				MaximumStoredTrustedNoticeCharacters,
				StoredTrustedNoticeTruncationNotice,
				forceMarker: false);
		}

		string Compose()
		{
			var response = AppendTrustedNotices(
				header + McpSpotlight.Wrap(treePreview) +
				(budgetReport is null ? string.Empty : "\n\n" + McpSpotlight.Wrap(budgetReport)),
				treePreviewWasTruncated ? StoredTreePreviewTruncationNotice : null,
				trustedNotices);
			return report is null
				? response
				: AppendBudgetAccounting(
					response,
					report,
					formattedBudgetReport!.Length,
					pack.Characters);
		}

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

	internal static RelatedResolutionCounts CountRelatedResolution(
		DependencyIndexSnapshot index,
		IReadOnlyList<string> seeds,
		DependencyDirection direction)
	{
		var edges = new HashSet<DependencyEdge>();
		if (direction is DependencyDirection.Dependencies or DependencyDirection.Both)
		{
			foreach (var seed in seeds)
				foreach (var edge in index.EdgesBySource.GetValueOrDefault(seed) ?? [])
					edges.Add(edge);
		}
		if (direction is DependencyDirection.Dependents or DependencyDirection.Both)
		{
			foreach (var seed in seeds)
				foreach (var edge in index.EdgesByTarget.GetValueOrDefault(seed) ?? [])
					if (edge.Target is not null && StringComparer.Ordinal.Equals(edge.Target, seed))
						edges.Add(edge);
		}
		var counts = new int[4];
		foreach (var edge in edges)
			counts[(int)edge.Status]++;
		return new RelatedResolutionCounts(
			counts[(int)ResolutionStatus.Resolved],
			counts[(int)ResolutionStatus.Ambiguous],
			counts[(int)ResolutionStatus.Unresolved],
			counts[(int)ResolutionStatus.External]);
	}

	internal readonly record struct RelatedResolutionCounts(
		int Resolved,
		int Ambiguous,
		int Unresolved,
		int External);

	private sealed record McpResolvedFileReadRequest(
		McpGetFileRequest Request,
		string? PhysicalPath);

	private sealed class McpMergedFileReadGroup(
		string? physicalPath,
		string displayPath,
		int startLine,
		int endLine,
		IEnumerable<McpGetFileRange> ranges)
	{
		public string? PhysicalPath { get; } = physicalPath;
		public string DisplayPath { get; } = displayPath;
		public int StartLine { get; set; } = startLine;
		public int EndLine { get; set; } = endLine;
		public List<McpGetFileRange> Ranges { get; } = [.. ranges];
	}

	private sealed record McpBatchFileReadResult(
		string Text,
		string Summary,
		string? UnavailableNotice,
		string? Continuations);

	private static string? FormatPackEvictions(McpPackDocument pack) =>
		pack.EvictedPackCount == 0
			? null
			: $"[Pack quota] evicted={pack.EvictedPackCount.ToString(CultureInfo.InvariantCulture)}";

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

	private static string? FormatCompressionUnavailable(CodeCompressionSnapshot? snapshot)
	{
		if (snapshot?.Availability is not { IsUnavailable: true } availability)
			return null;
		var languages = availability.Failures
			.Where(static failure => failure.LanguageId is not null)
			.Select(static failure => failure.LanguageId!)
			.Distinct(StringComparer.Ordinal)
			.Count();
		return $"[Compression unavailable] failures={availability.Failures.Count.ToString(CultureInfo.InvariantCulture)} · " +
		       $"languages={languages.ToString(CultureInfo.InvariantCulture)}";
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

	/// <summary>
	/// Orders the effective selection for an admission preview. Ranking runs only because a budget
	/// asked for it; without one, neither dependency indexing nor Git history is touched.
	/// </summary>
	private async Task<ImportanceRankingReport> RankForAdmissionAsync(
		ProjectContextPlan plan,
		IReadOnlyList<string>? focus,
		CancellationToken cancellationToken)
	{
		var service = new ImportanceRankingService(
			Projects.DependencyFactsEngine,
			new ProjectGitHistoryReader());
		if (focus is null)
		{
			return await service
				.RankAsync(plan.SourceRoot, plan.IncludedFiles, progress: null, cancellationToken)
				.ConfigureAwait(false);
		}

		var seeds = Projects.ResolveRequestedFiles(plan, focus, cancellationToken)
			.Select((path, index) => new FocusRankingSeedRequest(focus[index], path))
			.ToArray();
		return await service
			.RankAsync(
				plan.SourceRoot,
				plan.IncludedFiles,
				new FocusRankingRequest(seeds),
				progress: null,
				cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// The admission plan a budget produced, bounded the same way the largest-files list is: an
	/// entry cap plus an aggregate character budget, with the flag and the count saying what was cut.
	/// The digest always covers the complete order, so equality with a pack stays checkable even when
	/// the list does not fit.
	/// </summary>
	private static Dictionary<string, object> BuildAdmission(
		ProjectContextTokenBudgetReport report,
		string detailToken)
	{
		var included = new List<Dictionary<string, object>>();
		var characters = 2;
		foreach (var file in report.IncludedFiles)
		{
			var entry = new Dictionary<string, object>(4, StringComparer.Ordinal)
			{
				["path"] = file.Path,
				["tokens"] = file.EstimatedTokens
			};
			if (file.Priority is { } priority)
				entry["priority"] = priority;
			if (file.Hop is { } hop)
				entry["hop"] = hop;
			var entryCharacters = JsonSerializer.Serialize(entry).Length + (included.Count == 0 ? 0 : 1);
			if (characters + entryCharacters > MaximumAdmissionIncludedFilesCharacters)
				break;
			included.Add(entry);
			characters += entryCharacters;
		}

		var skipped = new List<Dictionary<string, object>>(report.LargestSkippedFiles.Count);
		foreach (var file in ConcatenateSkipped(report))
		{
			var entry = new Dictionary<string, object>(4, StringComparer.Ordinal)
			{
				["path"] = file.Path,
				["tokens"] = file.EstimatedTokens,
				["remainingTokens"] = file.RemainingEstimatedTokens ?? 0
			};
			if (file.Priority is { } priority)
				entry["priority"] = priority;
			if (file.Detail is { } fileDetail)
				entry["detail"] = fileDetail;
			skipped.Add(entry);
		}

		return new Dictionary<string, object>(12, StringComparer.Ordinal)
		{
			["budget"] = report.MaximumEstimatedTokens,
			["includedFileCount"] = report.IncludedFileCount,
			["skippedFileCount"] = report.SkippedFileCount,
			["includedEstimatedTokens"] = report.IncludedEstimatedTokens,
			["skippedEstimatedTokens"] = report.SkippedEstimatedTokens,
			["includedFiles"] = included,
			["includedFilesTruncated"] = included.Count < report.IncludedFileCount,
			["additionalIncludedFileCount"] = report.IncludedFileCount - included.Count,
			["includedOrderDigest"] = report.IncludedOrderDigest,
			["skippedFiles"] = skipped,
			["additionalSkippedFileCount"] = Math.Max(0, report.SkippedFileCount - skipped.Count),
			["detail"] = detailToken
		};
	}

	/// <summary>
	/// The largest skipped files, plus the highest-priority skipped files when the call ranked, in
	/// the same shape a pack reports them and without listing any file twice.
	/// </summary>
	private static IEnumerable<ProjectContextTokenBudgetSkippedFile> ConcatenateSkipped(
		ProjectContextTokenBudgetReport report)
	{
		var seen = new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer);
		foreach (var file in report.LargestSkippedFiles)
		{
			if (seen.Add(file.Path))
				yield return file;
		}
		foreach (var file in report.RankedSkippedFiles ?? [])
		{
			if (seen.Add(file.Path))
				yield return file;
		}
	}

	/// <summary>
	/// States the mix a per-file detail call produced. Counts only; a mask that claimed nothing is
	/// named because selection is never widened, so a typo would otherwise look like a level that
	/// simply had no files.
	/// </summary>
	private static string? FormatDetailMix(ContentDetailMix? mix)
	{
		if (mix is null)
			return null;
		var summary =
			$"[Detail] full {mix.FullFileCount.ToString(CultureInfo.InvariantCulture)} · " +
			$"compact {mix.CompactFileCount.ToString(CultureInfo.InvariantCulture)} · " +
			$"signatures {mix.SignaturesFileCount.ToString(CultureInfo.InvariantCulture)} · " +
			$"overrides {mix.MatchedPatternCount.ToString(CultureInfo.InvariantCulture)} of " +
			$"{mix.TotalPatternCount.ToString(CultureInfo.InvariantCulture)} patterns matched";
		if (mix.UnmatchedPatterns.Count == 0)
			return summary;

		var listed = mix.UnmatchedPatterns.Take(MaximumReportedUnmatchedDetailPatterns).ToArray();
		var names = string.Join(
			", ",
			listed.Select(pattern => McpTextEscaping.EscapeSingleLine(Truncate(pattern))));
		var remaining = mix.UnmatchedPatterns.Count - listed.Length;
		return summary +
		       $"\n[Detail] unmatched: {names}" +
		       (remaining > 0
			       ? $" and {remaining.ToString(CultureInfo.InvariantCulture)} more"
			       : string.Empty);
	}

	private static string Truncate(string pattern) =>
		pattern.Length <= MaximumReportedDetailPatternCharacters
			? pattern
			: pattern[..MaximumReportedDetailPatternCharacters] + "…";

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
					.Append(" estimated tokens");
				// Only under a mixed call: an estimate is only actionable next to the level it was
				// measured at.
				if (file.Detail is { } skippedDetail)
					output.Append(" at ").Append(skippedDetail);
				output.Append(")\n");
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
			output.Append(report.RankedSkippedFiles is { Count: > 0 } &&
			              report.LargestSkippedFiles.Any(file => file.EstimatedTokens > report.MaximumEstimatedTokens)
				? "Tip: increase max_tokens or lower detail for a file that is larger than the entire budget."
				: "Tip: use detail=compact or detail=signatures, narrow the selection, or increase max_tokens.");
		}
		return output.ToString().TrimEnd('\r', '\n');
	}

	private static string AppendBudgetAccounting(
		string responseWithoutAccounting,
		ProjectContextTokenBudgetReport report,
		int formattedReportCharacters,
		long? storedDocumentCharacters = null,
		int additionalReplyCharacters = 0)
	{
		var reportTokens = CodeCompressionSnapshot.EstimateTokens(formattedReportCharacters);
		var replyTokens = CodeCompressionSnapshot.EstimateTokens(
			responseWithoutAccounting.Length + additionalReplyCharacters);
		string? result = null;
		for (var attempt = 0; attempt < 8; attempt++)
		{
			var accounting = $"[Budget accounting] content ≈ {report.IncludedEstimatedTokens.ToString(CultureInfo.InvariantCulture)} of " +
			                 $"{report.MaximumEstimatedTokens.ToString(CultureInfo.InvariantCulture)} tokens · budget report ≈ " +
			                 $"{reportTokens.ToString(CultureInfo.InvariantCulture)}" +
			                 (storedDocumentCharacters is { } storedCharacters
				                 ? $" · stored document ≈ {CodeCompressionSnapshot.EstimateTokens(storedCharacters).ToString(CultureInfo.InvariantCulture)}"
				                 : string.Empty) +
			                 $" · reply ≈ {replyTokens.ToString(CultureInfo.InvariantCulture)}";
			result = AppendTrustedNotices(responseWithoutAccounting, accounting);
			var next = CodeCompressionSnapshot.EstimateTokens(result.Length + additionalReplyCharacters);
			if (next == replyTokens)
				break;
			replyTokens = next;
		}
		return result!;
	}

	private static string? FormatDependencyConfigurationDiagnostics(
		IReadOnlyList<DependencyConfigurationDiagnostic> diagnostics)
	{
		if (diagnostics.Count == 0)
			return null;
		var affectedScopes = diagnostics
			.SelectMany(static diagnostic => diagnostic.ScopeIds)
			.Distinct(StringComparer.Ordinal)
			.Count();
		return $"[Dependency configuration] problems={diagnostics.Count.ToString(CultureInfo.InvariantCulture)} · " +
		       $"missing={diagnostics.Count(static diagnostic => diagnostic.State == DependencyConfigurationState.Missing).ToString(CultureInfo.InvariantCulture)} · " +
		       $"corrupt={diagnostics.Count(static diagnostic => diagnostic.State == DependencyConfigurationState.Corrupt).ToString(CultureInfo.InvariantCulture)} · " +
		       $"unsupported-semantics={diagnostics.Count(static diagnostic => diagnostic.State == DependencyConfigurationState.UnsupportedSemantics).ToString(CultureInfo.InvariantCulture)} · " +
		       $"affected-scopes={affectedScopes.ToString(CultureInfo.InvariantCulture)}";
	}

	private static string? FormatDependencyConfigurationData(
		IReadOnlyList<DependencyConfigurationDiagnostic> diagnostics)
	{
		if (diagnostics.Count == 0)
			return null;
		var output = new StringBuilder();
		foreach (var diagnostic in diagnostics.Take(8))
		{
			if (output.Length > 0)
				output.AppendLine();
			output.Append("[Dependency configuration] ")
				.Append(McpTextEscaping.EscapeSingleLine(diagnostic.Path))
				.Append(" · ")
				.Append(diagnostic.State.ToString().ToLowerInvariant());
		}
		if (diagnostics.Count > 8)
		{
			output.AppendLine()
				.Append("[Dependency configuration] and ")
				.Append((diagnostics.Count - 8).ToString(CultureInfo.InvariantCulture))
				.Append(" more");
		}
		return output.ToString();
	}

	internal static string? FormatSafeNoFactsNotice(IReadOnlyList<SeedRelatedFiles> seeds)
	{
		var notices = SafeNoFactsReasons
			.Select(reason => (Reason: reason, Seeds: seeds.Count(seed =>
				string.Equals(seed.NoFactsReason, reason, StringComparison.Ordinal))))
			.Where(static item => item.Seeds > 0)
			.Select(static item => $"[No facts] {item.Reason}." +
				(item.Seeds == 1
					? string.Empty
					: $" seeds={item.Seeds.ToString(CultureInfo.InvariantCulture)}"))
			.ToArray();
		return notices.Length == 0 ? null : string.Join('\n', notices);
	}

	/// <summary>
	/// Points at the form that finds a file by name when this call asked for one in the only way
	/// the tool tolerates: a requested path that the effective tree does not hold.
	/// </summary>
	private static string? FormatNameSearchNotice(
		ProjectContextPlan plan,
		IReadOnlyList<string>? paths) =>
		McpTrustedDiagnosticFormatter.ReportsMissingSelectedPath(plan) && HasBareNamePath(paths)
			? NameSearchNotice
			: null;

	/// <summary>
	/// The search form of the same pointer. A content pattern that searched files and found
	/// nothing while reading like a file name was almost certainly aimed at one, and this tool
	/// never matches a path. A selection that held no file to search explains itself through
	/// <c>[Empty selection]</c> instead, and is left alone.
	/// </summary>
	private static string? FormatNameSearchNotice(
		ProjectContextPlan plan,
		IReadOnlyList<string>? paths,
		string pattern,
		int totalMatches) =>
		(McpTrustedDiagnosticFormatter.ReportsMissingSelectedPath(plan) && HasBareNamePath(paths)) ||
		(totalMatches == 0 && plan.IncludedFiles.Count > 0 && LooksLikeANameSearch(pattern))
			? NameSearchNotice
			: null;

	/// <summary>
	/// A requested path with no separator names one entry directly in the project root, so a
	/// caller who meant "this name, wherever it lives" gets nothing from it.
	/// </summary>
	private static bool HasBareNamePath(IReadOnlyList<string>? paths) =>
		paths?.Any(static path =>
			!string.IsNullOrWhiteSpace(path) &&
			!path.Contains('/', StringComparison.Ordinal) &&
			!path.Contains('\\', StringComparison.Ordinal)) == true;

	/// <summary>
	/// Whether a pattern that matched no content reads as a file name or path: it carries a path
	/// separator, or it ends in what looks like an extension, with or without the regex escape
	/// and the end anchor a caller would write around it. Both are syntactic, both are computed
	/// only for a response that already found nothing, and neither reaches the response text.
	/// </summary>
	private static bool LooksLikeANameSearch(string pattern)
	{
		if (string.IsNullOrWhiteSpace(pattern))
			return false;
		if (pattern.Contains('/', StringComparison.Ordinal))
			return true;

		var end = pattern.Length;
		if (pattern[end - 1] == '$')
			end--;
		var start = end;
		while (start > 0 && char.IsAsciiLetterOrDigit(pattern[start - 1]))
			start--;
		return end - start is > 0 and <= MaximumNameSearchExtensionLength &&
			start > 0 &&
			pattern[start - 1] == '.';
	}

	/// <summary>
	/// Reports what the expansion added, in counts and one constant. The paths themselves are in
	/// the untrusted block with the rest of the pack, as every project path is.
	/// </summary>
	private static string? FormatExpansionNotice(McpRelatedExpansionResult? expansion)
	{
		if (expansion is null)
			return null;
		var reported =
			$"[Expanded] seeds={expansion.SeedCount.ToString(CultureInfo.InvariantCulture)} · " +
			$"hop1=+{expansion.FirstHopCount.ToString(CultureInfo.InvariantCulture)} · " +
			$"hop2=+{expansion.SecondHopCount.ToString(CultureInfo.InvariantCulture)} · " +
			$"seeds-without-facts={expansion.SeedsWithoutFacts.ToString(CultureInfo.InvariantCulture)}";
		return expansion.LimitReached
			? $"{reported}; stopped at the " +
			  $"{McpRelatedExpansion.MaximumExpandedFiles.ToString(CultureInfo.InvariantCulture)}-file " +
			  "expansion limit, so the neighbourhood is incomplete."
			: $"{reported}.";
	}

	private static IReadOnlyList<string>? ParsePaths(McpJsonArguments arguments) =>
		arguments.OptionalStringArray(
			"paths",
			allowWhitespace: true,
			maximumItems: McpProjectService.MaximumRequestedPaths,
			maximumItemScalarValues: McpProjectService.MaximumRequestedPathLength);

	private static void ValidateLineRange(int? start, int? end)
	{
		if (end is null || end >= (start ?? 1))
			return;
		throw new McpToolException(
			McpErrorCodes.InvalidRange,
			$"{McpErrorCodes.InvalidRange}: requested line range {start ?? 1}-{end} is invalid. " +
			"Valid lines start at 1 and start_line must not exceed end_line.");
	}

	private static bool IsSafeNoFactsReason(string? reason) =>
		reason is not null && SafeNoFactsReasons.Contains(reason, StringComparer.Ordinal);

	private static string? FormatRankingReport(
		ImportanceRankingReport? report,
		ProjectContextTokenBudgetReport? tokenBudget)
	{
		if (report is null)
			return null;
		var status = new StringBuilder(512);
		if (report.Focus is { } focus)
			AppendFocusRankingSummary(status, report, focus);
		else
		{
			status.Append("[Ranking] ")
				.Append(report.Algorithm)
				.Append(" · graph ")
				.Append(report.GraphVariant)
				.Append(" · facts ")
				.Append(Math.Round(report.GraphCoverage * 100, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture))
				.Append("% of ")
				.Append(report.CandidateCount.ToString(CultureInfo.InvariantCulture))
				.Append(" sources · git window ")
				.Append(report.GitWindow.ToString(CultureInfo.InvariantCulture))
				.Append(" commits · tests deprioritized");
		}
		status.Append("\n[Ranking coverage] facts ")
			.Append(Math.Round(report.GraphCoverage * 100, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture))
			.Append("% · internal reference resolution ");
		if (report.InternalReferenceCandidates == 0)
		{
			status.Append("unavailable");
		}
		else
		{
			status.Append(report.ResolvedInternalReferences.ToString(CultureInfo.InvariantCulture))
				.Append('/')
				.Append(report.InternalReferenceCandidates.ToString(CultureInfo.InvariantCulture))
				.Append(" (")
				.Append(Math.Round(report.ResolvedInternalReferenceCoverage * 100, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture))
				.Append("%)");
		}
		status.Append(" · unique resolved file pairs ")
			.Append(report.UniqueResolvedFilePairs.ToString(CultureInfo.InvariantCulture))
			.Append(" · files with resolved edges ")
			.Append(report.FilesWithResolvedEdges.ToString(CultureInfo.InvariantCulture));
		if (report.HasMissingSignals)
		{
			status.Append(" · missing signals: ")
				.Append(report.MissingSignalPolicy switch
				{
					ImportanceMissingSignalPolicy.Redistribute => "redistributed",
					ImportanceMissingSignalPolicy.NeutralFill => "neutral fill",
					ImportanceMissingSignalPolicy.ConfidenceLimited => "confidence limited",
					_ => throw new ArgumentOutOfRangeException()
				});
		}
		if (report.GitHistoryIsShallow && !report.GitHistoryIsComplete)
		{
			status.Append("\n[Ranking git] read ")
				.Append(report.GitCommitCount.ToString(CultureInfo.InvariantCulture))
				.Append('/')
				.Append(report.GitWindow.ToString(CultureInfo.InvariantCulture))
				.Append(" commits; shallow history");
		}

		var projectData = new StringBuilder(1_024);
		foreach (var entry in report.TopEntries.Take(10))
		{
			if (projectData.Length > 0)
				projectData.Append('\n');
			projectData.Append("[Ranking top] ")
				.Append(McpTextEscaping.EscapeSingleLine(entry.Path));
			if (report.Focus is not null && entry.IsFocusSeed)
			{
				projectData.Append(" — seed");
				continue;
			}
			projectData.Append(" — ");
			if (report.Focus is not null)
			{
				if (entry.Hop is { } hop)
				{
					projectData.Append("hop ")
						.Append(hop.ToString(CultureInfo.InvariantCulture));
					if (entry.Via is { } via)
					{
						projectData.Append(" · ")
							.Append(FocusRelationText(via.Relation))
							.Append(' ')
							.Append(McpTextEscaping.EscapeSingleLine(via.Path));
					}
					projectData.Append(" · ");
				}
				else
				{
					projectData.Append("unreachable · ");
				}
			}
			projectData.Append("dependents ")
				.Append(entry.Dependents.ToString(CultureInfo.InvariantCulture))
				.Append(" · dependencies ")
				.Append(entry.Dependencies.ToString(CultureInfo.InvariantCulture))
				.Append(" · commits ")
				.Append(entry.Commits?.ToString(CultureInfo.InvariantCulture) ??
				        $"unavailable: {entry.GitUnavailableReason}")
				.Append('/')
				.Append(report.GitWindow.ToString(CultureInfo.InvariantCulture));
			if (entry.Role == ImportanceFileRole.TestSource)
				projectData.Append(" · test source");
			else if (entry.Role == ImportanceFileRole.Manifest)
				projectData.Append(" · manifest");
			else if (entry.Role == ImportanceFileRole.EntryPoint)
				projectData.Append(" · entry point");
			else if (entry.IsCoordinator)
				projectData.Append(" · coordinator");
			projectData.Append(" · priority ")
				.Append(entry.Priority.ToString(CultureInfo.InvariantCulture));
			if (report.Focus is not null)
			{
				projectData.Append(" (importance ")
					.Append(entry.BaseImportancePriority.GetValueOrDefault().ToString(CultureInfo.InvariantCulture))
					.Append(')');
			}
			projectData
				.Append("; graph ")
				.Append(entry.HasGraphFacts ? "available" : "unavailable")
				.Append("; git ")
				.Append(entry.HasGitHistory ? "available" : "unavailable")
				.Append("; main contribution: ")
				.Append(entry.MainContribution.ToString().ToLowerInvariant());
			if (entry.ConfidenceLimited)
				projectData.Append("; confidence limited");
		}
		if (tokenBudget is not null)
		{
			foreach (var file in tokenBudget.RankedSkippedFiles ?? [])
			{
				if (projectData.Length > 0)
					projectData.Append('\n');
				projectData.Append("[Skipped] ")
					.Append(McpTextEscaping.EscapeSingleLine(file.Path))
					.Append(" — ");
				if (report.Focus is not null)
				{
					var skippedEntry = report.Entries.FirstOrDefault(entry => entry.Priority == file.Priority);
					projectData.Append(skippedEntry?.Hop is { } hop
						? $"hop {hop.ToString(CultureInfo.InvariantCulture)}, "
						: "unreachable, ");
				}
				projectData.Append("priority ")
					.Append(file.Priority!.Value.ToString(CultureInfo.InvariantCulture))
					.Append(", ")
					.Append(file.EstimatedTokens.ToString(CultureInfo.InvariantCulture))
					.Append(" tokens, ")
					.Append(file.RemainingEstimatedTokens.GetValueOrDefault().ToString(CultureInfo.InvariantCulture))
					.Append(" remaining: does not fit the remaining budget");
			}
		}
		return projectData.Length == 0
			? status.ToString()
			: status.Append("\n\n").Append(McpSpotlight.Wrap(projectData.ToString())).ToString();
	}

	private static void AppendFocusRankingSummary(
		StringBuilder status,
		ImportanceRankingReport report,
		FocusRankingSummary focus)
	{
		status.Append("[Ranking] ")
			.Append(focus.Algorithm)
			.Append(" · ")
			.Append(focus.Seeds.Count.ToString(CultureInfo.InvariantCulture))
			.Append(focus.Seeds.Count == 1 ? " seed · hops" : " seeds · hops");
		foreach (var hop in focus.Hops.OrderBy(static pair => pair.Key))
		{
			status.Append(' ')
				.Append(hop.Key.ToString(CultureInfo.InvariantCulture))
				.Append(':')
				.Append(hop.Value.ToString(CultureInfo.InvariantCulture));
		}
		if (focus.HopsBeyond > 0)
		{
			status.Append(" 8+:")
				.Append(focus.HopsBeyond.ToString(CultureInfo.InvariantCulture))
				.Append(" · max hop ")
				.Append(focus.MaxHop.ToString(CultureInfo.InvariantCulture));
		}
		status.Append(" · unreachable ")
			.Append(focus.Unreachable.ToString(CultureInfo.InvariantCulture))
			.Append(" · within hop ")
			.Append(focus.WithinHop)
			.Append(" · graph ")
			.Append(report.GraphVariant)
			.Append(" · facts ")
			.Append(Math.Round(report.GraphCoverage * 100, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture))
			.Append("% of ")
			.Append(report.CandidateCount.ToString(CultureInfo.InvariantCulture))
			.Append(" sources · git window ")
			.Append(report.GitWindow.ToString(CultureInfo.InvariantCulture))
			.Append(" commits");
		var degraded = focus.Seeds.Where(static seed => seed.State != FocusSeedState.Resolved).ToArray();
		if (degraded.Length == 0)
			return;
		var reasons = degraded
			.Select(static seed => FocusSeedStateText(seed.State))
			.Distinct(StringComparer.Ordinal)
			.ToArray();
		status.Append(" · focus degraded: ")
			.Append(degraded.Length.ToString(CultureInfo.InvariantCulture))
			.Append(" of ")
			.Append(focus.Seeds.Count.ToString(CultureInfo.InvariantCulture))
			.Append(focus.Seeds.Count == 1 ? " seed" : " seeds")
			.Append(degraded.Length == 1 ? " has no resolved links (" : " have no resolved links (")
			.Append(string.Join(", ", reasons))
			.Append(')');
	}

	private static string FocusSeedStateText(FocusSeedState state) => state switch
	{
		FocusSeedState.Resolved => "resolved links",
		FocusSeedState.NoResolvedNeighbors => "facts but no resolved neighbors",
		FocusSeedState.ExtractionFailed => "fact extraction failed",
		FocusSeedState.Unsupported => "no supported facts",
		_ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
	};

	private static string FocusRelationText(FocusRankingRelation relation) => relation switch
	{
		FocusRankingRelation.DependentOf => "dependent of",
		FocusRankingRelation.DependencyOf => "dependency of",
		FocusRankingRelation.LinkedWith => "linked with",
		_ => throw new ArgumentOutOfRangeException(nameof(relation), relation, null)
	};

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

	internal static McpTreeDepthFit CalculateTreeDepthFit(
		TreeNodeDescriptor root,
		TreeTextFormat format,
		int maximumLines,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(root);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLines);
		cancellationToken.ThrowIfCancellationRequested();

		var nodeDeltas = new List<long> { 0 };
		var jsonDeltas = new List<long> { 0 };
		var xmlDeltas = new List<long> { 0 };
		var directories = new Stack<(TreeNodeDescriptor Node, int Depth)>();
		directories.Push((root, 0));
		var fullDepth = 0;
		while (directories.TryPop(out var current))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!current.Node.IsDirectory || current.Node.Children.Count == 0)
				continue;

			var childDepth = checked(current.Depth + 1);
			while (nodeDeltas.Count <= childDepth)
			{
				nodeDeltas.Add(0);
				jsonDeltas.Add(0);
				xmlDeltas.Add(0);
			}

			var directoryCount = 0;
			var fileCount = 0;
			foreach (var child in current.Node.Children)
			{
				if (child.IsDirectory)
				{
					directoryCount++;
					directories.Push((child, childDepth));
				}
				else
				{
					fileCount++;
				}
			}

			var childCount = checked(directoryCount + fileCount);
			nodeDeltas[childDepth] += childCount;
			fullDepth = Math.Max(fullDepth, childDepth);
			if (current.Depth == 0)
			{
				jsonDeltas[childDepth] += 1L + directoryCount + fileCount + (fileCount > 0 ? 2 : 0);
				xmlDeltas[childDepth] += 1L + directoryCount + fileCount;
			}
			else
			{
				jsonDeltas[childDepth] += 1L + directoryCount + fileCount +
				                          (directoryCount > 0 && fileCount > 0 ? 2 : 0);
				xmlDeltas[childDepth] += 1L + directoryCount + fileCount;
			}
		}

		long lineCount = format switch
		{
			TreeTextFormat.Ascii => 1,
			TreeTextFormat.Markdown => 2,
			TreeTextFormat.Json => 4,
			TreeTextFormat.Xml => 1,
			_ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
		};
		var deepestCompleteDepth = lineCount <= maximumLines ? 0 : -1;
		for (var depth = 1; depth <= fullDepth; depth++)
		{
			lineCount += format switch
			{
				TreeTextFormat.Ascii or TreeTextFormat.Markdown => nodeDeltas[depth],
				TreeTextFormat.Json => jsonDeltas[depth],
				TreeTextFormat.Xml => xmlDeltas[depth],
				_ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
			};
			if (lineCount <= maximumLines)
				deepestCompleteDepth = depth;
		}

		return new McpTreeDepthFit(fullDepth, deepestCompleteDepth);
	}

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

	internal readonly record struct McpTreeDepthFit(int FullDepth, int DeepestCompleteDepth)
	{
		public bool FullTreeFits => DeepestCompleteDepth == FullDepth;
	}

	private static async Task<McpTextPage> ReadFilePageAsync(
		McpPackDocument pack,
		Stream stream,
		int? startLine,
		int? endLine,
		int? startColumn,
		CancellationToken cancellationToken)
	{
		var checkpoint = pack.ResolveLineCheckpoint(startLine ?? 1);
		stream.Seek(checkpoint.ByteOffset, SeekOrigin.Begin);
		return await McpTextRanges.ReadPageAsync(
			stream,
			startLine,
			endLine,
			MaximumPageLines,
			MaximumPageCharacters,
			cancellationToken,
			pack.Lines,
			checkpoint.LineNumber,
			startColumn).ConfigureAwait(false);
	}

	internal static McpSearchAppendResult AppendSearchResult(
		StringBuilder output,
		string relativePath,
		string content,
		McpSearchMatchContext match,
		int maximumCharacters)
	{
		if (match.StartsNewGroup)
		{
			var separator = $"--{Environment.NewLine}";
			var separatorRemaining = maximumCharacters - output.Length;
			if (separator.Length > separatorRemaining)
			{
				AppendBoundedPrefix(output, separator, Math.Max(0, separatorRemaining));
				return new McpSearchAppendResult(0, Truncated: true);
			}
			output.Append(separator);
		}
		var safePath = EscapeSingleLine(relativePath);
		var matchingLines = match.MatchLineNumbers.ToHashSet();
		var writtenMatches = 0;
		foreach (var line in match.Lines)
		{
			var isMatchingLine = matchingLines.Contains(line.LineNumber);
			var marker = isMatchingLine ? ':' : '-';
			var prefix = $"{safePath}{marker}{line.LineNumber}{marker}";
			var remaining = maximumCharacters - output.Length;
			if (prefix.Length > remaining)
			{
				AppendBoundedPrefix(output, prefix, Math.Max(0, remaining));
				return new McpSearchAppendResult(writtenMatches, Truncated: true);
			}
			output.Append(prefix);

			remaining = maximumCharacters - output.Length;
			var fullyEscaped = SingleLineTextEscaping.AppendBounded(
				output,
				content.AsSpan(line.Offset, line.Length),
				Math.Max(0, remaining));
			if (!fullyEscaped)
			{
				return new McpSearchAppendResult(writtenMatches, Truncated: true);
			}
			if (isMatchingLine)
				writtenMatches++;

			remaining = maximumCharacters - output.Length;
			if (Environment.NewLine.Length > remaining)
			{
				AppendBoundedPrefix(output, Environment.NewLine, Math.Max(0, remaining));
				return new McpSearchAppendResult(writtenMatches, Truncated: true);
			}

			output.Append(Environment.NewLine);
		}
		return new McpSearchAppendResult(writtenMatches, Truncated: false);
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
		=> McpRootRegistry.GetProjectName(root);

}
