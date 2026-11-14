using DevProjex.Application.Dependencies;
using DevProjex.Application.Secrets;

namespace DevProjex.Mcp;

internal sealed partial class DevProjexMcpTools
{
	internal static bool ShouldPrepareBatchFallbackOutlines(
		IReadOnlyList<McpResolvedFileReadRequest> requests,
		IReadOnlyDictionary<string, TransformedTextFile> transformed,
		int maximumCharacters = MaximumPageCharacters)
	{
		long estimatedCharacters = 8_000;
		foreach (var request in requests)
		{
			estimatedCharacters += (long)McpTextEscaping.EscapeSingleLine(request.Request.Path).Length *
				Math.Max(1, request.Request.Ranges.Count);
			if (request.UnavailableReason is null && request.PhysicalPath is { } physical &&
				transformed.TryGetValue(physical, out var file))
				estimatedCharacters += file.Content.Length;
			if (estimatedCharacters > maximumCharacters)
				return true;
		}
		return estimatedCharacters > maximumCharacters;
	}

	internal static McpBatchFileReadResult RenderBatchFileReads(
		IReadOnlyList<McpResolvedFileReadRequest> requests,
		IReadOnlyDictionary<string, TransformedTextFile> transformed,
		IReadOnlyDictionary<string, string> testsLines,
		McpDeclarationReadContext continuationContext,
		string? profile,
		IReadOnlyList<ProjectExclusion>? exclusions,
		CancellationToken cancellationToken,
		IReadOnlyDictionary<string, IReadOnlyList<DependencyFileImport>>? importsByPath = null,
		IReadOnlyDictionary<string, McpLargeFileOutline>? outlinesByPath = null,
		int maximumCharacters = MaximumPageCharacters,
		int? statusBudgetCredit = null)
	{
		var status = requests
			.SelectMany(static request => request.Request.Ranges)
			.ToDictionary(static range => (range.RequestIndex, range.RangeIndex), static _ => "unavailable");
		var unavailableReasons = new Dictionary<(int RequestIndex, int RangeIndex), string>();
		foreach (var request in requests)
		{
			var reason = request.UnavailableReason ?? (request.PhysicalPath is null
				? McpProjectService.OutsideEffectiveSelectionReason
				: transformed.ContainsKey(request.PhysicalPath)
					? null
					: McpErrorCodes.PayloadTruncated);
			if (reason is null)
				continue;
			foreach (var range in request.Request.Ranges)
				unavailableReasons[(range.RequestIndex, range.RangeIndex)] = reason;
		}
		var groups = BuildMergedReadGroups(requests);
		var lineViews = new Dictionary<string, McpBatchTextLineView>(PathComparer.Default);
		var invalidRangeReasons = new Dictionary<(int RequestIndex, int RangeIndex), string>();
		foreach (var group in groups)
		{
			if (group.PhysicalPath is null || !transformed.TryGetValue(group.PhysicalPath, out var file) ||
				group.Ranges.All(static range => range.IsWholeFile))
				continue;
			var lineView = GetLineView(group.PhysicalPath, file);
			foreach (var range in group.Ranges)
			{
				if (lineView.DescribeInvalidRange(range) is not { } reason)
					continue;
				var path = McpTextEscaping.EscapeSingleLine(McpToolResults.ProtectMetadataString(group.DisplayPath));
				if (path.Length > 256)
					path = TakeCompleteScalarPrefix(path, 255) + "…";
				invalidRangeReasons[(range.RequestIndex, range.RangeIndex)] = $"{path}: {reason}";
			}
		}
		foreach (var group in groups)
		{
			if (group.PhysicalPath is null || !transformed.TryGetValue(group.PhysicalPath, out var source)) continue;
			var total = GetLineView(group.PhysicalPath, source).TotalLines;
			foreach (var range in group.Ranges)
			{
				status[(range.RequestIndex, range.RangeIndex)] = "not-returned";
				invalidRangeReasons.TryAdd((range.RequestIndex, range.RangeIndex), $"no room left in this response, {total} lines");
			}
		}
		var statusReserve = "Requests:\n" + string.Join('\n', status.Keys.Select(key =>
			$"{key.RequestIndex}.{key.RangeIndex} — not-returned" +
			FormatReason(key)));
		var optimisticStatus = "Requests:\n" + string.Join('\n', status.Keys.Select(key => $"{key.RequestIndex}.{key.RangeIndex} — ok"));
		var credit = statusBudgetCredit ?? statusReserve.Length - optimisticStatus.Length;
		var sectionBudgetCharacters = maximumCharacters - statusReserve.Length - 2 + credit;
		var sections = new StringBuilder();
		var continuations = new List<string>();
		var rangeNotices = new List<string>();
		var deliveredRanges = new List<McpDeliveredFileRange>();
		var structuredPaths = new List<string>();
		var minimumSections = groups.Select(MinimumSectionCharacters).ToArray();
		var groupIndex = 0;

		foreach (var group in groups)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var reservedFollowing = minimumSections.Skip(++groupIndex).Sum();
			if (group.PhysicalPath is null)
				continue;
			if (!transformed.TryGetValue(group.PhysicalPath, out var file))
				continue;
			var isEmptyContent = file.Content.Length == 0;
			IReadOnlyList<McpGetFileRange> deliveredRequests = isEmptyContent
				? group.Ranges.Where(static range => range.IsWholeFile).ToArray()
				: group.Ranges;
			if (deliveredRequests.Count == 0)
			{
				foreach (var range in group.Ranges)
					status[(range.RequestIndex, range.RangeIndex)] = "not-returned";
				continue;
			}

			var separator = sections.Length == 0 ? string.Empty : "\n\n";
			// The budget reserves a header naming every candidate range; the written header can
			// only be shorter, because it names just the ranges the page serves.
			var escapedPath = McpTextEscaping.EscapeSingleLine(group.DisplayPath);
			var headerPrefix = $"File: {escapedPath}\nRequests: {FormatBatchRequestIds(deliveredRequests)}\n";
			var availableLines = int.MaxValue - Math.Max(1, group.StartLine);
			var totalLines = GetLineView(group.PhysicalPath, file).TotalLines;
			var anticipatedHeader = $"Status: ok\nLines: {Math.Min(group.StartLine, totalLines)}-{Math.Min(group.EndLine, totalLines)} of {totalLines}\n";
			var availableCharacters = sectionBudgetCharacters - sections.Length - separator.Length -
									  headerPrefix.Length - anticipatedHeader.Length - reservedFollowing;
			if (availableLines <= 0 || availableCharacters <= 0)
			{
				foreach (var range in group.Ranges)
					status[(range.RequestIndex, range.RangeIndex)] = "not-returned";
				continue;
			}

			var whole = group.Ranges.All(static range => range.IsWholeFile);
			var outline = whole ? outlinesByPath?.GetValueOrDefault(group.PhysicalPath) : null;
			var large = outline is not null && McpLargeFileOutlineBuilder.Applies(file.Content);
			var mapCost = outline is null ? 0 : FormatOutline(outline).Length + 1;
			var pageCharacters = Math.Max(1, availableCharacters - (large ? mapCost : 0));
			McpTextPage page;
			try
			{
				var lineView = GetLineView(group.PhysicalPath, file);
				page = lineView.Slice(
					isEmptyContent ? null : group.StartLine,
					isEmptyContent ? null : large ? outline!.HeadLines : group.EndLine,
					availableLines,
					pageCharacters,
					cancellationToken);
			}
			catch (McpToolException exception) when (exception.Code == McpErrorCodes.InvalidRange)
			{
				foreach (var range in group.Ranges)
					status[(range.RequestIndex, range.RangeIndex)] = "not-returned";
				continue;
			}
			var fitsFraming = true;
			while (true)
			{
				var hasStructure = outline is not null && (large || page.IsTruncated);
				var onlyStructure = hasStructure && !large;
				var ids = FormatBatchRequestIds(group.Ranges.Where(range =>
					range.ClassifyDelivery(page) != McpGetFileRangeDeliveryStatus.NotReturned));
				var actual = onlyStructure
					? $"File: {escapedPath}\nRequests: {ids}\nStatus: structure\nLines: none of {page.TotalLines}\n" + FormatOutline(outline!)
					: FormatFileReadHeader(escapedPath, page, ids, hasStructure ? "structure" : page.IsTruncated ? "partial" : "ok", pathIsEscaped: true) + page.Text;
				if (hasStructure && !onlyStructure) actual = AppendSectionLine(actual, FormatOutline(outline!));
				var over = sections.Length + separator.Length + actual.Length + reservedFollowing - sectionBudgetCharacters;
				if (over <= 0) break;
				if (onlyStructure || pageCharacters <= over)
				{
					fitsFraming = false;
					break;
				}
				pageCharacters -= over;
				page = GetLineView(group.PhysicalPath, file).Slice(isEmptyContent ? null : group.StartLine,
					isEmptyContent ? null : large ? outline!.HeadLines : group.EndLine, availableLines, pageCharacters, cancellationToken);
			}
			if (!fitsFraming) continue;

			// Each merged range is judged against what the page really holds, so the header names
			// only the ranges it serves and a range past the end of the file is not listed as read.
			var deliveries = group.Ranges
				.Select(range => (Range: range, Status: range.ClassifyDelivery(page)))
				.ToArray();
			var served = deliveries
				.Where(static delivery => delivery.Status != McpGetFileRangeDeliveryStatus.NotReturned)
				.Select(static delivery => delivery.Range)
				.ToArray();
			if (served.Length == 0)
			{
				foreach (var range in group.Ranges)
					status[(range.RequestIndex, range.RangeIndex)] = "not-returned";
				continue;
			}

			var structure = outline is not null && (large || page.IsTruncated);
			var structureOnly = structure && !large;
			if (whole && !structure && page.IsTruncated && page.EndLine - page.StartLine + 1 < 20)
				continue;
			var sectionStatus = structure ? "structure" : page.IsTruncated ? "partial" : "ok";
			var header = FormatFileReadHeader(
				escapedPath,
				page,
				FormatBatchRequestIds(served),
				sectionStatus,
				pathIsEscaped: true);
			var section = structureOnly
				? separator + $"File: {escapedPath}\nRequests: {FormatBatchRequestIds(served)}\nStatus: structure\nLines: none of {page.TotalLines}\n" + FormatOutline(outline!)
				: separator + header + page.Text;
			if (structure && !structureOnly)
				section = AppendSectionLine(section, FormatOutline(outline!));
			if (!structureOnly && importsByPath?.GetValueOrDefault(group.PhysicalPath) is { } imports &&
				FormatImportsLine(imports, page.Text, page.StartLine) is { } importsLine)
			{
				var withImports = AppendSectionLine(section, importsLine);
				if (sections.Length + withImports.Length + reservedFollowing <= sectionBudgetCharacters)
					section = withImports;
			}
			if (!structureOnly && (!page.IsTruncated || structure) &&
				group.Ranges.All(static range => range.IsWholeFile) &&
				testsLines.TryGetValue(group.PhysicalPath, out var testsLine))
			{
				var withTests = AppendTestsLine(section, testsLine);
				if (sections.Length + withTests.Length + reservedFollowing <= sectionBudgetCharacters)
					section = withTests;
			}
			if (sections.Length + section.Length + reservedFollowing > sectionBudgetCharacters)
			{
				foreach (var range in group.Ranges)
					status[(range.RequestIndex, range.RangeIndex)] = "not-returned";
				continue;
			}

			sections.Append(section);
			if (!structureOnly && page.TotalLines > 0)
				deliveredRanges.Add(new McpDeliveredFileRange(
					group.PhysicalPath,
					page.StartLine,
					page.EndLine));
			if (structure)
			{
				structuredPaths.Add(group.PhysicalPath);
				foreach (var range in group.Ranges)
					invalidRangeReasons[(range.RequestIndex, range.RangeIndex)] = large
						? $"large file, {page.TotalLines} lines"
						: $"no room left in this response, {page.TotalLines} lines";
			}
			foreach (var delivery in deliveries)
			{
				status[(delivery.Range.RequestIndex, delivery.Range.RangeIndex)] = structure ? "structure" : delivery.Status switch
				{
					McpGetFileRangeDeliveryStatus.Ok => "ok",
					McpGetFileRangeDeliveryStatus.Partial => "partial",
					McpGetFileRangeDeliveryStatus.NotReturned => "not-returned",
					_ => throw new ArgumentOutOfRangeException()
				};
			}
			if (page.IsTruncated && !structure)
			{
				continuations.Add(FormatBatchContinuation(
					requests,
					group,
					page,
					continuationContext,
					profile,
					exclusions));
			}
			else if (page.TotalLines > 0)
			{
				// Only an explicit end_line can be clamped: a whole-file read asked for no end, and
				// a range that starts after the last line returned nothing and is reported only
				// as not returned.
				var clamped = served
					.Where(range => !range.IsWholeFile && range.EndLineWasSpecified && range.EndLine > page.TotalLines)
					.ToArray();
				if (clamped.Length > 0)
				{
					rangeNotices.Add(
						$"[Range clamped] requests={FormatBatchRequestIds(clamped)}; end_line exceeded the file; " +
						$"returned through line {page.TotalLines}.");
				}
			}
		}

		var statusText = "Requests:\n" + string.Join('\n', status.Select(pair =>
			$"{pair.Key.RequestIndex}.{pair.Key.RangeIndex} — {pair.Value}" +
			FormatReason(pair.Key)));
		if (statusText.Length > maximumCharacters)
			throw new McpToolException(McpErrorCodes.PayloadTruncated,
				$"{McpErrorCodes.PayloadTruncated}: response metadata exceeds the batch character limit.");
		var body = sections.Length == 0 ? statusText : statusText + "\n\n" + sections;
		var counts = status.Values.GroupBy(static value => value, StringComparer.Ordinal)
			.ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
		var deliveredPaths = requests
			.Where(request => request.Request.Ranges.Any(range =>
				status[(range.RequestIndex, range.RangeIndex)] is "ok" or "partial" or "structure"))
			.Select(static request => request.Request.Path)
			.Distinct(ProjectTreePathIdentity.CanonicalComparer)
			.ToArray();
		var summary = $"[Batch read] ok={GetCount("ok")} · structure={GetCount("structure")} · partial={GetCount("partial")} · " +
					  $"not-returned={GetCount("not-returned")} · unavailable={GetCount("unavailable")}.";
		if (GetCount("structure") > 0)
			continuations.Insert(0, $"[Batch structure] {GetCount("structure")} file(s) are shown as structure instead of text: a large file keeps its start, and a file that did not fit keeps only its declarations. Read the lines you need with requests ranges; a file that did not fit can also be read whole in another call.");
		var result = new McpBatchFileReadResult(
			body,
			summary,
			GetCount("unavailable") == 0
				? null
				: $"[Batch unavailable] files={CountUnavailableFiles()} · ranges={GetCount("unavailable")}; " +
				  $"{McpErrorCodes.PayloadTruncated} marks mandatory-inspection failures; other entries have per-request path or symbol failures.",
			continuations.Count == 0 && rangeNotices.Count == 0
				? null
				: string.Join('\n', continuations.Concat(rangeNotices)),
			deliveredPaths,
			deliveredRanges)
		{ StructuredPaths = structuredPaths };
		// Reconcile status framing against the actual result, without letting diagnostics exceed the cap.
		if (result.Text.Length > maximumCharacters)
			return RenderBatchFileReads(requests, transformed, testsLines, continuationContext, profile,
				exclusions, cancellationToken, importsByPath, outlinesByPath, maximumCharacters,
				credit - (result.Text.Length - maximumCharacters));
		return result;

		int MinimumSectionCharacters(McpMergedFileReadGroup group)
		{
			if (group.PhysicalPath is not { } physical || !transformed.TryGetValue(physical, out var file)) return 0;
			var view = GetLineView(physical, file);
			if (group.Ranges.All(range => view.DescribeInvalidRange(range) is not null)) return 0;
			if (outlinesByPath?.GetValueOrDefault(physical) is { } map && group.Ranges.All(static range => range.IsWholeFile))
			{
				if (McpLargeFileOutlineBuilder.Applies(file.Content))
				{
					var head = view.Slice(1, 1, 1, 2, cancellationToken);
					return AppendSectionLine(FormatFileReadHeader(group.DisplayPath, head, FormatBatchRequestIds(group.Ranges), "structure") + head.Text,
						FormatOutline(map)).Length + 2;
				}
				var minimal = $"File: {McpTextEscaping.EscapeSingleLine(group.DisplayPath)}\nRequests: {FormatBatchRequestIds(group.Ranges)}\nStatus: structure\nLines: none of {view.TotalLines}\n{FormatOutline(map)}".Length + 2;
				if (file.Content.Length <= minimal * 2)
				{
					var complete = view.Slice(null, null, int.MaxValue - 1, maximumCharacters, cancellationToken);
					if (!complete.IsTruncated) minimal = Math.Min(minimal,
						FormatFileReadHeader(group.DisplayPath, complete, FormatBatchRequestIds(group.Ranges), "ok").Length + complete.Text.Length + 2);
				}
				return minimal;
			}
			var whole = group.Ranges.All(static range => range.IsWholeFile);
			var page = view.Slice(file.Content.Length == 0 ? null : group.StartLine,
				file.Content.Length == 0 ? null : group.EndLine, Math.Min(20, int.MaxValue - Math.Max(1, group.StartLine)),
				whole ? maximumCharacters : Math.Min(100, maximumCharacters), cancellationToken);
			if (whole && page.IsTruncated && page.EndLine - page.StartLine + 1 < 20) return 0;
			return FormatFileReadHeader(group.DisplayPath, page, FormatBatchRequestIds(group.Ranges), page.IsTruncated ? "partial" : "ok").Length + page.Text.Length + 2;
		}

		string FormatReason((int RequestIndex, int RangeIndex) key) =>
			unavailableReasons.TryGetValue(key, out var unavailable) ? $" — {unavailable}" :
			status[key] is "not-returned" or "structure" or "partial" && invalidRangeReasons.TryGetValue(key, out var invalid) ? $" — {invalid}" : string.Empty;

		McpBatchTextLineView GetLineView(string physicalPath, TransformedTextFile file)
		{
			if (!lineViews.TryGetValue(physicalPath, out var lineView))
			{
				lineView = new McpBatchTextLineView(
					file.Content,
					groups.Where(candidate => PathComparer.Default.Equals(candidate.PhysicalPath, physicalPath))
						.Select(static candidate => candidate.StartLine),
					cancellationToken);
				lineViews.Add(physicalPath, lineView);
			}
			return lineView;
		}

		int GetCount(string value) => counts.GetValueOrDefault(value);

		int CountUnavailableFiles() => unavailableReasons.Keys
			.Select(static key => key.RequestIndex)
			.Distinct()
			.Count();
	}

}
