using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevProjex.Application.Secrets;
using DevProjex.Mcp;
using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Rendering;

namespace DevProjex.Terminal.Execution;

public sealed class SearchCommandHandler(
	TerminalServices services,
	ITerminalEnvironment environment)
{
	private const int ContextLines = 2;
	private const int MaximumContentCharacters = 16_000;
	private const int MaximumStoredMatches = 5_000;
	private const int MaximumStoredCharacters = 2_000_000;
	private const int MaximumDeclarationsReported = 20;
	private const long MaximumInspectedBytes = 64L * 1024 * 1024;
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	public async Task<int> ExecuteAsync(
		SearchCommandRequest request,
		CancellationToken cancellationToken)
	{
		var status = new StatusRenderer(environment, request.Output);
		var plan = await status.RunAsync(
			services.Localization["Terminal.Status.AnalyzingProject"],
			() => services.ContextFactory.BuildAsync(
				request.ProjectPath,
				request.Selection,
				includeOutputMetrics: false,
				cancellationToken: cancellationToken,
				repositorySourceUrl: request.RepositorySourceUrl)).ConfigureAwait(false);
		new ContextDiagnosticRenderer(environment, request.Output, services.Localization)
			.Write(plan.Diagnostics);
		if (plan.HasErrors)
			return CommandLineExitCodes.PolicyFailure;

		string payload;
		try
		{
			payload = await RenderSearchForPlanAsync(
				plan,
				request,
				MaximumInspectedBytes,
				cancellationToken).ConfigureAwait(false);
		}
		catch (McpToolException exception) when (exception.Code == McpErrorCodes.InvalidPattern)
		{
			throw new SearchCommandException(
				"DPX-CLI-SEARCH-PATTERN",
				"The search pattern is invalid or exceeded the evaluation limit.",
				exception);
		}

		if (request.OutputPath is null or "-")
		{
			await environment.Output.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
			return CommandLineExitCodes.Success;
		}

		var requestedPath = Path.GetFullPath(request.OutputPath);
		_ = ExactOutputDestinationValidator.ValidateAnalysis(plan.SourceRoot, requestedPath, overwrite: false);
		var writtenPath = await AtomicOutputWriter.WriteAsync(
			requestedPath,
			overwrite: false,
			async (destination, token) =>
			{
				await using var writer = new StreamWriter(
					destination,
					new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
					bufferSize: 16 * 1024,
					leaveOpen: true);
				await writer.WriteAsync(payload.AsMemory(), token).ConfigureAwait(false);
				await writer.FlushAsync(token).ConfigureAwait(false);
			},
			cancellationToken,
			path => ExactOutputDestinationValidator.ValidateAnalysis(plan.SourceRoot, path, overwrite: false))
			.ConfigureAwait(false);
		TerminalTextEscaping.WriteSingleLine(environment.Output, writtenPath);
		return CommandLineExitCodes.Success;
	}

	internal async Task<string> RenderSearchForPlanAsync(
		ProjectContextPlan plan,
		SearchCommandRequest request,
		long maximumInspectedBytes,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(request);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumInspectedBytes);
		var result = await SearchAsync(plan, request, maximumInspectedBytes, cancellationToken)
			.ConfigureAwait(false);
		return Render(result, request.Format);
	}

	private async Task<SearchResult> SearchAsync(
		ProjectContextPlan plan,
		SearchCommandRequest request,
		long maximumInspectedBytes,
		CancellationToken cancellationToken)
	{
		var regex = new McpSearchRegex(ToRegexPattern(request.Pattern, request.Mode), ignoreCase: true);
		var context = CreateTransformationContext(plan);
		var inspectedFiles = new List<string>(plan.IncludedFiles.Count);
		long inspectedBytes = 0;
		foreach (var path in plan.IncludedFiles)
		{
			var fileBytes = ResolveFileSize(plan, path, recheckPlannedSize: context is not null);
			if (fileBytes > maximumInspectedBytes - inspectedBytes)
				break;
			inspectedFiles.Add(path);
			inspectedBytes += fileBytes;
		}
		var inspectionByteLimitReached = inspectedFiles.Count < plan.IncludedFiles.Count;

		var candidates = new McpSearchCandidateCollector(MaximumStoredMatches, MaximumStoredCharacters);
		var navigationByFile = new Dictionary<string, IReadOnlyList<NavigationDeclaration>>(StringComparer.Ordinal);
		var totalMatches = 0;
		var inspectedSources = 0;
		var matchingFiles = 0;
		var unscannableSources = 0;
		ValueTask Consume(TransformedTextFile file, CancellationToken token)
		{
			inspectedSources++;
			var relative = PathUtility.GetPortableRelativePath(plan.SourceRoot, file.Path);
			IReadOnlyList<NavigationDeclaration>? navigation = null;
			McpSearchDeclarationPreviewCache? declarationPreviews = null;
			var priorityState = new McpSearchFilePriorityState();
			void AcceptMatch(McpSearchMatchContext match)
			{
				navigation ??= McpSearchSymbols.CaptureNavigation(
					services.DependencyFactsEngine,
					relative,
					file.Content,
					token);
				if (request.SearchBodyCharacters > 0)
				{
					declarationPreviews ??= new McpSearchDeclarationPreviewCache(
						relative,
						file.Content,
						navigation,
						request.SearchBodyCharacters,
						token);
				}
				DevProjexMcpTools.AddSearchCandidates(
					candidates,
					relative,
					file.Path,
					file.Content,
					[match],
					navigation,
					regex,
					ContextLines,
					explicitScope: plan.Selection.SelectedPaths is { Count: > 0 },
					priorityState,
					declarationPreviews);
			}

			int effectiveMatches;
			if (request.Mode == SearchMode.Symbols)
			{
				navigation = McpSearchSymbols.CaptureNavigation(
					services.DependencyFactsEngine,
					relative,
					file.Content,
					token);
				var declarationMatches = McpSearchSymbols.FindNamedDeclarationMatches(
					file.Content,
					regex,
					ContextLines,
					file.ReplacementRanges,
					navigation,
					request.Pattern,
					token);
				foreach (var match in declarationMatches)
					AcceptMatch(match);
				effectiveMatches = declarationMatches.Count;
			}
			else
			{
				var scan = McpSearchTextScanner.ScanEach(
					file.Content,
					regex,
					ContextLines,
					file.ReplacementRanges,
					AcceptMatch,
					token);
				effectiveMatches = scan.TotalMatches;
			}
			totalMatches += effectiveMatches;
			if (effectiveMatches > 0)
			{
				matchingFiles++;
				var annotatedFiles = candidates.SelectFilesForAnnotation(
					McpSearchSymbols.MaximumAnnotatedFiles);
				foreach (var stale in navigationByFile.Keys
							 .Where(path => !annotatedFiles.Contains(path)).ToArray())
				{
					navigationByFile.Remove(stale);
				}
				if (navigation is not null && annotatedFiles.Contains(relative))
					navigationByFile[relative] = navigation;
			}
			return ValueTask.CompletedTask;
		}

		if (context is null)
		{
			var analyzer = new FileContentAnalyzer();
			long inspectedActualBytes = 0;
			foreach (var path in inspectedFiles)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var read = await analyzer.ReadClassifiedAsync(
						path,
						maximumInspectedBytes - inspectedActualBytes,
						cancellationToken)
					.ConfigureAwait(false);
				if (read.Classification == FileContentClassification.TooLarge)
				{
					unscannableSources++;
					inspectionByteLimitReached = true;
					break;
				}
				if (read.Classification != FileContentClassification.Text || read.Content is null)
				{
					unscannableSources++;
					continue;
				}
				inspectedActualBytes += read.Content.SizeBytes;
				await Consume(
					new TransformedTextFile(path, read.Content.Content, []),
					cancellationToken).ConfigureAwait(false);
			}
		}
		else if (inspectedFiles.Count > 0)
		{
			await using var searched = await services.SecretRedactionOutputPreparer
				.ConsumeTransformedTextAsync(context, inspectedFiles, Consume, cancellationToken)
				.ConfigureAwait(false);
			unscannableSources = searched.UnscannableFiles.Count;
		}

		var candidateSnapshot = candidates.Snapshot();
		var ordered = DevProjexMcpTools.BuildOrderedSearchGroups(candidateSnapshot);
		var retainedMatchingFiles = ordered.Select(group => group.RelativePath)
			.Distinct(StringComparer.Ordinal).Count();
		SearchResult BuildResult(int contentCharacters)
		{
			var rendered = DevProjexMcpTools.RenderSearchGroups(
				ordered,
				request.MaximumResults,
				contentCharacters);
			var symbols = McpSearchSymbols.Resolve(
				rendered.WrittenHits,
				navigationByFile,
				cancellationToken);
			var preview = request.SearchBodyCharacters == 0
				? null
				: McpSearchSymbols.SelectDeclarationBodyPreview(
					candidateSnapshot,
					symbols.Declarations,
					regex);
			var layout = DevProjexMcpTools.PlanSearchDeclarationBody(
				rendered,
				symbols.Declarations,
				preview);
			rendered = layout.Rendered;
			preview = layout.Preview;

			var namesWritten = InsertDeclarationHeaders(
				rendered.Output,
				rendered.RenderedLines,
				symbols,
				contentCharacters);
			if (!namesWritten)
				symbols = symbols with { AnnotatedHits = 0 };
			AppendDeclarationSection(rendered.Output, symbols.Declarations, preview, request, contentCharacters);

			var matches = rendered.WrittenHits.Select(hit =>
			{
				var candidate = candidateSnapshot.First(item =>
					item.Group.RelativePath == hit.RelativePath && item.MatchLine == hit.Line);
				var renderedMatch = candidate.Group.Lines.First(line => line.IsMatch).Text;
				var separator = renderedMatch.IndexOf(':');
				return new SearchMatch(
					hit.RelativePath,
					hit.Line,
					separator >= 0 ? renderedMatch[(separator + 1)..] : renderedMatch,
					namesWritten
						? symbols.Names.GetValueOrDefault(new McpSearchHitKey(hit.RelativePath, hit.Line))
						: null);
			}).ToArray();
			var resolution = new SearchResolution(
				Resolved: symbols.AnnotatedHits,
				Ambiguous: 0,
				Unresolved: Math.Max(0, matches.Length - symbols.AnnotatedHits),
				External: 0);
			var boundary = new McpSearchBoundary(
				plan.IncludedFiles.Count,
				inspectedSources,
				totalMatches,
				candidates.Count,
				rendered.ShownMatches,
				namesWritten
					? symbols.Names.Keys.Select(key => key.RelativePath).Distinct(StringComparer.Ordinal).Count()
					: 0,
				inspectionByteLimitReached,
				candidates.MatchCapacityReached,
				retainedMatchingFiles > McpSearchSymbols.MaximumAnnotatedFiles,
				rendered.Truncated || !namesWritten,
				rendered.ShownMatches < candidates.Count && rendered.ShownMatches >= request.MaximumResults,
				candidates.CharacterCapacityReached,
				StoredCharacterLimitReached: false,
				unscannableSources);
			return new SearchResult(
				request.Pattern,
				request.Mode,
				matches,
				symbols.Declarations.Select(declaration => new SearchDeclaration(
					declaration.RelativePath,
					declaration.Name,
					declaration.StartLine,
					declaration.EndLine,
					preview?.Declaration == declaration ? preview.Text : null,
					preview?.Declaration == declaration ? preview.RemainingLines : 0)).ToArray(),
				resolution,
				boundary,
				rendered.Output.ToString().TrimEnd(),
				matchingFiles);
		}

		var maximumBudget = MaximumContentCharacters;
		var minimumBudget = 0;
		var fittingResult = BuildResult(0);
		while (minimumBudget <= maximumBudget)
		{
			var contentBudget = minimumBudget + ((maximumBudget - minimumBudget) / 2);
			var result = BuildResult(contentBudget);
			var fitsEveryFormat = Enum.GetValues<SearchOutputFormat>()
				.All(format => Render(result, format).Length <= MaximumContentCharacters);
			if (fitsEveryFormat)
			{
				fittingResult = result;
				minimumBudget = contentBudget + 1;
			}
			else
			{
				maximumBudget = contentBudget - 1;
			}
		}
		return fittingResult;
	}

	private ContentTransformationContext? CreateTransformationContext(ProjectContextPlan plan)
	{
		var transformKinds = CodeTransformIdentity.Resolve(
			plan.Selection.CompressCode == true,
			plan.Selection.StripComments == true,
			plan.Selection.StripBlankLines == true);
		var redactionFeatures = SecretRedactionFeatureSelection.Resolve(
			plan.Selection.HideSecrets == true,
			plan.Selection.HidePrivateData == true);
		return ContentTransformationContext.For(
			transformKinds == CodeTransformKinds.None
				? null
				: new CodeCompressionContext(plan.SourceRoot, services.CodeCompressionSession, transformKinds),
			redactionFeatures == SecretRedactionFeatures.None
				? null
				: new SecretRedactionContext(plan.SourceRoot, services.SecretRedactionSession, redactionFeatures));
	}

	private static string ToRegexPattern(string pattern, SearchMode mode) => mode switch
	{
		SearchMode.Regex => pattern,
		SearchMode.Text => Regex.Escape(pattern),
		SearchMode.Symbols =>
			$"(?<![\\p{{L}}\\p{{N}}_]){Regex.Escape(LastSymbolSegment(pattern))}(?![\\p{{L}}\\p{{N}}_])",
		_ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
	};

	private static string LastSymbolSegment(string name)
	{
		var separator = name.LastIndexOfAny(['.', '#', '/', ':']);
		return separator >= 0 ? name[(separator + 1)..] : name;
	}

	private static long ResolveFileSize(
		ProjectContextPlan plan,
		string path,
		bool recheckPlannedSize)
	{
		if (plan.EffectiveFileSizes?.TryGetValue(path, out var size) == true)
		{
			var plannedSize = Math.Max(0, size);
			return recheckPlannedSize
				? Math.Max(plannedSize, ReadCurrentFileSize(path))
				: plannedSize;
		}
		return ReadCurrentFileSize(path);
	}

	private static long ReadCurrentFileSize(string path)
	{
		try
		{
			return Math.Max(0, new FileInfo(path).Length);
		}
		catch
		{
			return 0;
		}
	}

	private static bool InsertDeclarationHeaders(
		StringBuilder output,
		IReadOnlyList<McpSearchRenderedLine> rendered,
		McpSearchSymbolResult symbols,
		int maximumCharacters)
	{
		if (rendered.Count == 0 || symbols.Names.Count == 0)
			return true;
		var insertions = new List<(int Offset, string Text)>();
		var cost = 0;
		string? file = null;
		string? named = null;
		var groupOffset = 0;
		var groupNamed = false;
		foreach (var line in rendered)
		{
			if (!string.Equals(line.RelativePath, file, StringComparison.Ordinal))
			{
				file = line.RelativePath;
				named = null;
			}
			if (line.StartsGroup)
			{
				groupOffset = line.Offset;
				groupNamed = false;
			}
			if (!line.IsMatch)
				continue;
			if (!symbols.Names.TryGetValue(
					new McpSearchHitKey(line.RelativePath, line.LineNumber),
					out var name))
			{
				if (named is not null)
				{
					var outside = $"in (no declaration){Environment.NewLine}";
					insertions.Add((line.Offset, outside));
					cost += outside.Length;
					named = null;
				}
				groupNamed = true;
				continue;
			}
			if (string.Equals(name, named, StringComparison.Ordinal))
				continue;
			var header = $"in {McpTextEscaping.EscapeSingleLine(name)}{Environment.NewLine}";
			insertions.Add((groupNamed ? line.Offset : groupOffset, header));
			cost += header.Length;
			groupNamed = true;
			named = name;
		}
		if (output.Length + cost > maximumCharacters)
			return false;
		for (var index = insertions.Count - 1; index >= 0; index--)
			output.Insert(insertions[index].Offset, insertions[index].Text);
		return true;
	}

	private static void AppendDeclarationSection(
		StringBuilder output,
		IReadOnlyList<McpSearchDeclaration> declarations,
		McpSearchDeclarationPreview? preview,
		SearchCommandRequest request,
		int maximumCharacters)
	{
		if (declarations.Count == 0)
			return;
		const string heading = "Declarations found (path, symbol, lines):";
		var section = new StringBuilder().AppendLine().AppendLine(heading);
		var rows = 0;
		foreach (var declaration in declarations.Take(MaximumDeclarationsReported))
		{
			var row = $"{McpTextEscaping.EscapeSingleLine(declaration.RelativePath)} " +
					  $"{McpTextEscaping.EscapeSingleLine(declaration.Name)} " +
					  $"{declaration.StartLine.ToString(CultureInfo.InvariantCulture)}-" +
					  $"{declaration.EndLine.ToString(CultureInfo.InvariantCulture)}{Environment.NewLine}";
			if (output.Length + section.Length + row.Length > maximumCharacters)
				break;
			section.Append(row);
			rows++;
		}
		if (rows == 0)
			return;
		if (preview is { IsAddressable: true, Text.Length: > 0 })
		{
			var commandArguments = BuildDeclarationReadArguments(
				request,
				preview.Declaration.RelativePath);
			var readInstruction = commandArguments is null
				? "no standalone read command can preserve this remote profile's manual secret marks."
				: string.Join(' ', commandArguments.Select(QuoteArgument));
			var body = new StringBuilder()
				.AppendLine()
				.Append("Best declaration body (1 of ")
				.Append(declarations.Count.ToString(CultureInfo.InvariantCulture))
				.AppendLine("):")
				.Append("Read declaration file: ").AppendLine(readInstruction)
				.Append("lines ").Append(preview.Declaration.StartLine.ToString(CultureInfo.InvariantCulture))
				.Append('-').Append(preview.Declaration.EndLine.ToString(CultureInfo.InvariantCulture)).AppendLine()
				.Append(preview.Text);
			if (preview.RemainingLines > 0)
			{
				body.AppendLine().Append("[Declaration body truncated: ")
					.Append(preview.RemainingLines.ToString(CultureInfo.InvariantCulture))
					.Append(" line(s) remain.]");
			}
			body.AppendLine();
			if (output.Length + section.Length + body.Length <= maximumCharacters)
				section.Append(body);
		}
		output.Append(section);
	}

	internal static string ResolveDeclarationReadSource(SearchCommandRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (string.IsNullOrWhiteSpace(request.RepositorySourceUrl))
			return request.ProjectPath;

		var safeSource = RepositoryUrlUtility.ToSafeDisplay(request.RepositorySourceUrl);
		return safeSource.Length > 0 ? safeSource : request.ProjectPath;
	}

	internal static IReadOnlyList<string>? BuildDeclarationReadArguments(
		SearchCommandRequest request,
		string relativePath)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
		var useLocalProfile = request.Selection.ProfileSource?.Kind == ProjectProfileSourceKind.Local &&
		                      ProjectSelectionMarkedSecretsResolver.Resolve(request.Selection).Count > 0;
		if (useLocalProfile && !string.IsNullOrWhiteSpace(request.RepositorySourceUrl))
			return null;
		var arguments = new List<string>
		{
			"devprojex", "export", "context", ResolveDeclarationReadSource(request)
		};
		if (!string.IsNullOrWhiteSpace(request.RepositoryBranch))
		{
			arguments.Add("--branch");
			arguments.Add(request.RepositoryBranch);
		}
		arguments.AddRange(
		[
			"--view", "content", "--format", "text", "-o", "-",
			"--profile", useLocalProfile ? "local" : "standard", "--select", relativePath
		]);
		if (request.Selection.GitMode is { } gitMode)
		{
			arguments.Add("--git-mode");
			arguments.Add(GitScopeSelection.ToToken(gitMode, request.Selection.GitDiffRange));
		}
		foreach (var exclusion in request.Selection.Exclusions ?? [])
		{
			arguments.Add("--exclude");
			arguments.Add(ProjectSelectionTokens.ToToken(exclusion));
		}
		if (request.Selection.Exclusions is { Count: 0 })
		{
			arguments.Add("--exclude");
			arguments.Add("none");
		}
		if (request.Selection.HideSecrets == true)
			arguments.Add("--hide-secrets");
		if (request.Selection.HidePrivateData == true)
			arguments.Add("--hide-private-data");
		if (request.Selection.CompressCode == true)
			arguments.Add("--compress-code");
		if (request.Selection.StripComments == true)
			arguments.Add("--strip-comments");
		if (request.Selection.StripBlankLines == true)
			arguments.Add("--strip-blank-lines");
		foreach (var root in request.Selection.Roots ?? [])
		{
			arguments.Add("--root");
			arguments.Add(root);
		}
		foreach (var extension in request.Selection.Extensions ?? [])
		{
			arguments.Add("--extension");
			arguments.Add(extension);
		}
		return arguments;
	}

	private static string QuoteArgument(string value)
	{
		if (value.Length > 0 && value.All(static character =>
			char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or '/' or ':'))
		{
			return value;
		}
		return OperatingSystem.IsWindows()
			? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
			: "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
	}

	private static string RenderText(SearchResult result)
	{
		var output = new StringBuilder();
		if (result.Body.Length > 0)
			output.AppendLine(result.Body);
		else if (result.Boundary.EncounteredMatches > 0)
			output.AppendLine("[Matches omitted] Matches were found, but no complete result line fit within the output budget.");
		else if (result.Boundary.InspectedSources < result.Boundary.EligibleSources)
			output.Append("[Search partial] No matches were found in ")
				.Append(result.Boundary.InspectedSources)
				.Append(" inspected selected file(s); ")
				.Append(result.Boundary.EligibleSources - result.Boundary.InspectedSources)
				.AppendLine(" selected file(s) were not inspected.");
		else
			output.AppendLine($"[No matches] The pattern matched nothing in {result.Boundary.InspectedSources} inspected selected file(s).");
		output.Append("[Resolution] resolved=").Append(result.Resolution.Resolved)
			.Append(" · ambiguous=").Append(result.Resolution.Ambiguous)
			.Append(" · unresolved=").Append(result.Resolution.Unresolved)
			.Append(" · external=").Append(result.Resolution.External).AppendLine();
		if (result.Boundary.EncounteredMatches > result.Boundary.WrittenMatches)
		{
			output.Append("[Search observed] matches=").Append(result.Boundary.EncounteredMatches)
				.Append(" · matching-files=").Append(result.MatchingFiles)
				.AppendLine(" within inspected sources");
		}
		output.AppendLine(DevProjexMcpTools.FormatSearchBoundaryNotice(result.Boundary, hasStoredContinuation: false));
		return output.ToString();
	}

	private static string Render(SearchResult result, SearchOutputFormat format) => format switch
	{
		SearchOutputFormat.Text => RenderText(result),
		SearchOutputFormat.Json => RenderJson(result),
		SearchOutputFormat.Markdown => RenderMarkdown(result),
		_ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
	};

	private static string RenderJson(SearchResult result)
	{
		var limits = BoundaryLimits(result.Boundary);
		return JsonSerializer.Serialize(
			new
			{
				schemaVersion = 1,
				kind = "devprojex-search-results",
				query = new { pattern = result.Pattern, mode = ModeToken(result.Mode) },
				matches = result.Matches.Select(match => new
				{
					path = match.Path,
					line = match.Line,
					text = match.Text,
					declaration = match.Declaration
				}),
				declarations = result.Declarations.Select(declaration => new
				{
					path = declaration.Path,
					symbol = declaration.Symbol,
					startLine = declaration.StartLine,
					endLine = declaration.EndLine,
					body = declaration.Body,
					remainingBodyLines = declaration.RemainingBodyLines
				}),
				resolution = new
				{
					resolved = result.Resolution.Resolved,
					ambiguous = result.Resolution.Ambiguous,
					unresolved = result.Resolution.Unresolved,
					external = result.Resolution.External
				},
				searchBoundary = new
				{
					complete = result.Boundary.IsComplete,
					eligibleSources = result.Boundary.EligibleSources,
					inspectedSources = result.Boundary.InspectedSources,
					encounteredMatches = result.Boundary.EncounteredMatches,
					retainedMatches = result.Boundary.RetainedMatches,
					writtenMatches = result.Boundary.WrittenMatches,
					omittedMatches = Math.Max(
						0,
						result.Boundary.EncounteredMatches - result.Boundary.WrittenMatches),
					namedDeclarationFiles = result.Boundary.NamedDeclarationFiles,
					limits
				}
			},
			JsonOptions) + Environment.NewLine;
	}

	private static string RenderMarkdown(SearchResult result)
	{
		var text = RenderText(result).TrimEnd();
		var longest = Regex.Matches(text, "`+").Select(match => match.Length).DefaultIfEmpty(2).Max();
		var fence = new string('`', Math.Max(3, longest + 1));
		return $"# Search results{Environment.NewLine}{Environment.NewLine}" +
			   $"{fence}text{Environment.NewLine}{text}{Environment.NewLine}{fence}{Environment.NewLine}";
	}

	private static IReadOnlyList<string> BoundaryLimits(McpSearchBoundary boundary)
	{
		var limits = new List<string>();
		if (boundary.InspectionByteLimitReached) limits.Add("inspection-bytes");
		if (boundary.RetainedMatchLimitReached) limits.Add("retained-matches");
		if (boundary.AnnotationFileLimitReached) limits.Add("annotation-files");
		if (boundary.ResponseCharacterLimitReached) limits.Add("response-characters");
		if (boundary.RequestResultLimitReached) limits.Add("max-results");
		if (boundary.RetainedCharacterLimitReached) limits.Add("retained-characters");
		if (boundary.StoredCharacterLimitReached) limits.Add("stored-characters");
		if (boundary.UnscannableSources > 0) limits.Add("unscannable-sources");
		return limits;
	}

	private static string ModeToken(SearchMode mode) => mode switch
	{
		SearchMode.Text => "text",
		SearchMode.Regex => "regex",
		SearchMode.Symbols => "symbols",
		_ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
	};

	private sealed record SearchResult(
		string Pattern,
		SearchMode Mode,
		IReadOnlyList<SearchMatch> Matches,
		IReadOnlyList<SearchDeclaration> Declarations,
		SearchResolution Resolution,
		McpSearchBoundary Boundary,
		string Body,
		int MatchingFiles);

	private sealed record SearchMatch(string Path, int Line, string Text, string? Declaration);

	private sealed record SearchDeclaration(
		string Path,
		string Symbol,
		int StartLine,
		int EndLine,
		string? Body,
		int RemainingBodyLines);

	private readonly record struct SearchResolution(int Resolved, int Ambiguous, int Unresolved, int External);
}

internal sealed class SearchCommandException(string code, string message, Exception? innerException = null)
	: Exception(message, innerException)
{
	public string Code { get; } = code;
}
