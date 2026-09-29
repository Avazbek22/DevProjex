using System.Globalization;

namespace DevProjex.Mcp;

internal enum McpGetFileRangeDeliveryStatus
{
	Ok,
	Partial,
	NotReturned
}

internal sealed record McpGetFileRange(
	int RequestIndex,
	int RangeIndex,
	int StartLine,
	int EndLine,
	bool IsWholeFile = false)
{
	public McpGetFileRangeDeliveryStatus ClassifyDelivery(McpTextPage page)
	{
		if (page.TotalLines == 0)
			return IsWholeFile ? McpGetFileRangeDeliveryStatus.Ok : McpGetFileRangeDeliveryStatus.NotReturned;
		if (page.StartLine <= 0 || page.EndLine < page.StartLine)
			return McpGetFileRangeDeliveryStatus.NotReturned;
		var effectiveEndLine = Math.Min(EndLine, page.TotalLines);
		if (StartLine > effectiveEndLine || page.EndLine < StartLine || page.StartLine > effectiveEndLine)
			return McpGetFileRangeDeliveryStatus.NotReturned;
		var entireRangeReturned = page.StartLine <= StartLine && page.EndLine >= effectiveEndLine;
		var endLineWasReturnedOnlyInPart = page.CharacterLimitReached && page.StartLine == page.EndLine &&
			StartLine <= page.EndLine && effectiveEndLine >= page.EndLine;
		return entireRangeReturned && !endLineWasReturnedOnlyInPart
			? McpGetFileRangeDeliveryStatus.Ok
			: McpGetFileRangeDeliveryStatus.Partial;
	}

	public McpGetFileRange? ContinueAtLine(int nextLine)
	{
		if (nextLine <= 0)
			throw new ArgumentOutOfRangeException(nameof(nextLine));
		if (nextLine > EndLine)
			return null;
		return this with { StartLine = Math.Max(StartLine, nextLine) };
	}
}

internal sealed record McpGetFileRequest(
	int Index,
	string Path,
	IReadOnlyList<McpGetFileRange> Ranges,
	string? Symbol = null);

internal sealed record McpGetFileRequestSet(bool IsBatch, IReadOnlyList<McpGetFileRequest> Requests)
{
	public const int MaximumFiles = 8;
	public const int MaximumRanges = 16;

	// Agents that want several ranges guess a list in start_line or invent start_range; both
	// refusals end with this sentence so the next call can use the batch form directly.
	internal const string SeveralRangesHint =
		"For several ranges, call get_file once with requests: [{\"path\":\"src/a.cs\",\"ranges\":" +
		"[{\"start_line\":140,\"end_line\":160},{\"start_line\":200,\"end_line\":241}]}].";

	public static string? HintForUnknownArguments(IReadOnlyList<string> unknownNames) =>
		unknownNames.Any(static name =>
			name.Contains("range", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("lines", StringComparison.OrdinalIgnoreCase))
			? SeveralRangesHint
			: null;

	public static McpGetFileRequestSet Parse(McpJsonArguments arguments)
	{
		var hasPath = arguments.Contains("path");
		var hasRequests = arguments.Contains("requests");
		if (hasPath == hasRequests)
			throw Invalid("exactly one of 'path' or 'requests' is required");

		if (hasPath)
		{
			RejectSeveralLineNumbers(arguments, "start_line");
			RejectSeveralLineNumbers(arguments, "end_line");
			var start = arguments.OptionalInteger("start_line", 1, int.MaxValue) ?? 1;
			var end = arguments.OptionalInteger("end_line", 1, int.MaxValue) ?? int.MaxValue;
			return new McpGetFileRequestSet(false,
			[
				new McpGetFileRequest(
					1,
					arguments.RequiredString("path", allowWhitespace: true),
					[new McpGetFileRange(1, 1, start, end)])
			]);
		}

		if (arguments.Contains("start_line") || arguments.Contains("end_line") || arguments.Contains("start_column"))
			throw Invalid("start_line, end_line, and start_column are valid only with 'path'");
		if (!arguments.TryGetElement("requests", out var requestsElement) ||
			requestsElement.ValueKind != JsonValueKind.Array ||
			requestsElement.GetArrayLength() is < 1 or > MaximumFiles)
		{
			throw Invalid($"'requests' must be an array with 1 to {MaximumFiles} entries");
		}

		var requests = new List<McpGetFileRequest>(requestsElement.GetArrayLength());
		var totalRanges = 0;
		var requestIndex = 0;
		foreach (var requestElement in requestsElement.EnumerateArray())
		{
			requestIndex++;
			if (requestElement.ValueKind != JsonValueKind.Object)
				throw InvalidEntry(requestIndex, "must be an object");
			ValidatePropertyNames(requestElement, requestIndex, "path", "ranges", "symbol");
			if (!requestElement.TryGetProperty("path", out var pathElement) ||
				pathElement.ValueKind != JsonValueKind.String ||
				string.IsNullOrWhiteSpace(pathElement.GetString()))
			{
				throw InvalidEntry(requestIndex, "path must be a non-whitespace string");
			}
			var path = pathElement.GetString()!;
			if (McpUnicodeLength.ExceedsScalarValueCount(path, McpProjectService.MaximumRequestedPathLength))
				throw InvalidEntry(requestIndex, $"path must contain at most {McpProjectService.MaximumRequestedPathLength} characters");
			var hasRanges = requestElement.TryGetProperty("ranges", out var rangesElement);
			var hasSymbol = requestElement.TryGetProperty("symbol", out var symbolElement);
			if (hasRanges && hasSymbol)
				throw InvalidEntry(requestIndex, "ranges and symbol cannot be combined");
			string? symbol = null;
			if (hasSymbol)
			{
				if (symbolElement.ValueKind != JsonValueKind.String ||
					string.IsNullOrWhiteSpace(symbolElement.GetString()))
					throw InvalidEntry(requestIndex, "symbol must be a non-whitespace string");
				symbol = symbolElement.GetString()!;
				if (McpUnicodeLength.ExceedsScalarValueCount(symbol, 512))
					throw InvalidEntry(requestIndex, "symbol must contain at most 512 characters");
				totalRanges++;
				if (totalRanges > MaximumRanges)
					throw InvalidEntry(requestIndex, $"the call must contain at most {MaximumRanges} file selections");
			}
			else if (hasRanges &&
					 (rangesElement.ValueKind != JsonValueKind.Array || rangesElement.GetArrayLength() == 0))
			{
				throw InvalidEntry(requestIndex, "ranges must be a non-empty array");
			}
			else if (!hasRanges)
			{
				totalRanges++;
				if (totalRanges > MaximumRanges)
					throw InvalidEntry(requestIndex, $"the call must contain at most {MaximumRanges} file selections");
			}

			var ranges = new List<McpGetFileRange>(hasRanges ? rangesElement.GetArrayLength() : 1);
			var rangeIndex = 0;
			foreach (var rangeElement in hasRanges
					 ? rangesElement.EnumerateArray().ToArray()
					 : Array.Empty<JsonElement>())
			{
				rangeIndex++;
				totalRanges++;
				if (totalRanges > MaximumRanges)
					throw InvalidEntry(requestIndex, $"the call must contain at most {MaximumRanges} file selections");
				if (rangeElement.ValueKind != JsonValueKind.Object)
					throw InvalidRange(requestIndex, rangeIndex, "must be an object");
				ValidatePropertyNames(
					rangeElement,
					requestIndex,
					"start_line",
					"end_line",
					rangeIndex: rangeIndex);
				var start = ReadPositiveInteger(rangeElement, requestIndex, rangeIndex, "start_line");
				var end = ReadPositiveInteger(rangeElement, requestIndex, rangeIndex, "end_line");
				if (start > end)
					throw InvalidRange(requestIndex, rangeIndex, "start_line must not exceed end_line");
				ranges.Add(new McpGetFileRange(requestIndex, rangeIndex, start, end));
			}
			if (symbol is not null || !hasRanges)
				ranges.Add(new McpGetFileRange(
					requestIndex,
					1,
					1,
					int.MaxValue,
					IsWholeFile: !hasRanges && symbol is null));
			requests.Add(new McpGetFileRequest(requestIndex, path, ranges, symbol));
		}

		return new McpGetFileRequestSet(true, requests);
	}

	private static int ReadPositiveInteger(
		JsonElement owner,
		int requestIndex,
		int rangeIndex,
		string name)
	{
		if (!owner.TryGetProperty(name, out var value))
			throw InvalidRange(requestIndex, rangeIndex, $"{name} is required");
		if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0)
			return number;
		if (value.ValueKind == JsonValueKind.String &&
			int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number) &&
			number > 0)
		{
			return number;
		}
		throw InvalidRange(requestIndex, rangeIndex, $"{name} must be a positive integer or numeric string");
	}

	private static void ValidatePropertyNames(
		JsonElement element,
		int requestIndex,
		string first,
		string second,
		string? third = null,
		int? rangeIndex = null)
	{
		foreach (var property in element.EnumerateObject())
		{
			if (property.NameEquals(first) || property.NameEquals(second) ||
				third is not null && property.NameEquals(third))
				continue;
			throw rangeIndex is null
				? InvalidEntry(requestIndex, $"contains unknown property '{property.Name}'")
				: InvalidRange(requestIndex, rangeIndex.Value, $"contains unknown property '{property.Name}'");
		}
	}

	private static void RejectSeveralLineNumbers(McpJsonArguments arguments, string name)
	{
		if (!arguments.TryGetElement(name, out var value) || !HoldsSeveralNumbers(value))
			return;
		throw new McpToolException(
			McpErrorCodes.InvalidRange,
			$"{McpErrorCodes.InvalidRange}: '{name}' takes one line number. {SeveralRangesHint}");
	}

	private static bool HoldsSeveralNumbers(JsonElement value)
	{
		if (value.ValueKind == JsonValueKind.Array)
			return true;
		if (value.ValueKind != JsonValueKind.String)
			return false;
		var digitRuns = 0;
		var previousWasDigit = false;
		foreach (var character in value.GetString() ?? string.Empty)
		{
			var isDigit = char.IsAsciiDigit(character);
			if (isDigit && !previousWasDigit)
				digitRuns++;
			previousWasDigit = isDigit;
		}
		return digitRuns >= 2;
	}

	private static McpToolException Invalid(string message) =>
		new(McpErrorCodes.InvalidArguments, $"{McpErrorCodes.InvalidArguments}: {message}.");

	private static McpToolException InvalidEntry(int requestIndex, string message) =>
		new(McpErrorCodes.InvalidArguments,
			$"{McpErrorCodes.InvalidArguments}: requests[{requestIndex - 1}] {message}.");

	private static McpToolException InvalidRange(int requestIndex, int rangeIndex, string message) =>
		new(McpErrorCodes.InvalidArguments,
			$"{McpErrorCodes.InvalidArguments}: requests[{requestIndex - 1}].ranges[{rangeIndex - 1}] {message}.");
}
