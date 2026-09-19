using System.Globalization;
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

		SearchResult result;
		try
		{
			result = await SearchAsync(plan, request, cancellationToken).ConfigureAwait(false);
		}
		catch (McpToolException exception) when (exception.Code == McpErrorCodes.InvalidPattern)
		{
			throw new SearchCommandException(
				"DPX-CLI-SEARCH-PATTERN",
				"The search pattern is invalid or exceeded the evaluation limit.",
				exception);
		}

		var payload = request.Format switch
		{
			SearchOutputFormat.Text => RenderText(result),
			SearchOutputFormat.Json => RenderJson(result),
			SearchOutputFormat.Markdown => RenderMarkdown(result),
			_ => throw new ArgumentOutOfRangeException(nameof(request.Format), request.Format, null)
		};
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

	private async Task<SearchResult> SearchAsync(
		ProjectContextPlan plan,
		SearchCommandRequest request,
		CancellationToken cancellationToken)
	{
		var regex = new McpSearchRegex(ToRegexPattern(request.Pattern, request.Mode), ignoreCase: true);
		var inspectedFiles = new List<string>(plan.IncludedFiles.Count);
		long inspectedBytes = 0;
		foreach (var path in plan.IncludedFiles)
		{
			var fileBytes = ResolveFileSize(plan, path);
			if (fileBytes > MaximumInspectedBytes - inspectedBytes)
				break;
			inspectedFiles.Add(path);
			inspectedBytes += fileBytes;
		}

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
			var acceptedMatches = 0;
			var scan = McpSearchTextScanner.ScanEach(
				file.Content,
				regex,
				ContextLines,
				file.ReplacementRanges,
				match =>
				{
					navigation ??= McpSearchSymbols.CaptureNavigation(
						services.DependencyFactsEngine,
						relative,
						file.Content,
						token);
					if (request.Mode == SearchMode.Symbols &&
						!navigation.Any(declaration =>
							declaration.StartLine == match.MatchLineNumber &&
							SymbolNameMatches(declaration.Name, request.Pattern)))
					{
						return;
					}
					acceptedMatches++;
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
				},
				token);
			var effectiveMatches = request.Mode == SearchMode.Symbols
				? acceptedMatches
				: scan.TotalMatches;
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

		var context = CreateTransformationContext(plan);
		if (context is null)
		{
			var analyzer = new FileContentAnalyzer();
			foreach (var path in inspectedFiles)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var read = await analyzer.ReadClassifiedAsync(path, long.MaxValue, cancellationToken)
					.ConfigureAwait(false);
				if (read.Classification != FileContentClassification.Text || read.Content is null)
				{
					unscannableSources++;
					continue;
				}
				await Consume(
					new TransformedTextFile(path, read.Content.Content, []),
					cancellationToken).ConfigureAwait(false);
			}
		}
		else
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
		var rendered = DevProjexMcpTools.RenderSearchGroups(
			ordered,
			request.MaximumResults,
			MaximumContentCharacters);
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
			MaximumContentCharacters);
		if (!namesWritten)
			symbols = symbols with { AnnotatedHits = 0 };
		AppendDeclarationSection(rendered.Output, symbols.Declarations, preview);

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
			inspectedFiles.Count < plan.IncludedFiles.Count,
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

	private static bool SymbolNameMatches(string declaredName, string requestedName)
	{
		if (declaredName.Equals(requestedName, StringComparison.OrdinalIgnoreCase))
			return true;
		return !requestedName.Contains('.') &&
			   !requestedName.Contains('#') &&
			   !requestedName.Contains('/') &&
			   !requestedName.Contains(':') &&
			   LastSymbolSegment(declaredName).Equals(requestedName, StringComparison.OrdinalIgnoreCase);
	}

	private static string LastSymbolSegment(string name)
	{
		var separator = name.LastIndexOfAny(['.', '#', '/', ':']);
		return separator >= 0 ? name[(separator + 1)..] : name;
	}

	private static long ResolveFileSize(ProjectContextPlan plan, string path)
	{
		if (plan.EffectiveFileSizes?.TryGetValue(path, out var size) == true)
			return Math.Max(0, size);
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
		McpSearchDeclarationPreview? preview)
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
			if (output.Length + section.Length + row.Length > MaximumContentCharacters)
				break;
			section.Append(row);
			rows++;
		}
		if (rows == 0)
			return;
		if (preview is { IsAddressable: true, Text.Length: > 0 })
		{
			var arguments = JsonSerializer.Serialize(new
			{
				path = preview.Declaration.RelativePath,
				symbol = preview.Declaration.Name
			});
			var body = new StringBuilder()
				.AppendLine()
				.Append("Best declaration body (1 of ")
				.Append(declarations.Count.ToString(CultureInfo.InvariantCulture))
				.AppendLine("):")
				.Append("get_file ").AppendLine(arguments)
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
			if (output.Length + section.Length + body.Length <= MaximumContentCharacters)
				section.Append(body);
		}
		output.Append(section);
	}

	private static string RenderText(SearchResult result)
	{
		var output = new StringBuilder();
		if (result.Body.Length > 0)
			output.AppendLine(result.Body);
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
					namedDeclarationFiles = result.Boundary.NamedDeclarationFiles,
					limits
				}
			},
			new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
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
