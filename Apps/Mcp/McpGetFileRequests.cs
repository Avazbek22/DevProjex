using System.Globalization;

namespace DevProjex.Mcp;

internal sealed record McpGetFileRange(int RequestIndex, int RangeIndex, int StartLine, int EndLine);

internal sealed record McpGetFileRequest(
	int Index,
	string Path,
	IReadOnlyList<McpGetFileRange> Ranges,
	string? Symbol = null);

internal sealed record McpGetFileRequestSet(bool IsBatch, IReadOnlyList<McpGetFileRequest> Requests)
{
	public const int MaximumFiles = 8;
	public const int MaximumRanges = 16;

	public static McpGetFileRequestSet Parse(McpJsonArguments arguments)
	{
		var hasPath = arguments.Contains("path");
		var hasRequests = arguments.Contains("requests");
		if (hasPath == hasRequests)
			throw Invalid("exactly one of 'path' or 'requests' is required");

		if (hasPath)
		{
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
			if (hasRanges == hasSymbol)
				throw InvalidEntry(requestIndex, "exactly one of ranges or symbol is required");
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
					throw InvalidEntry(requestIndex, $"the call must contain at most {MaximumRanges} ranges or symbols");
			}
			else if (rangesElement.ValueKind != JsonValueKind.Array || rangesElement.GetArrayLength() == 0)
			{
				throw InvalidEntry(requestIndex, "ranges must be a non-empty array");
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
					throw InvalidEntry(requestIndex, $"the call must contain at most {MaximumRanges} ranges");
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
			if (symbol is not null)
				ranges.Add(new McpGetFileRange(requestIndex, 1, 1, int.MaxValue));
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

	private static McpToolException Invalid(string message) =>
		new(McpErrorCodes.InvalidArguments, $"{McpErrorCodes.InvalidArguments}: {message}.");

	private static McpToolException InvalidEntry(int requestIndex, string message) =>
		new(McpErrorCodes.InvalidArguments,
			$"{McpErrorCodes.InvalidArguments}: requests[{requestIndex - 1}] {message}.");

	private static McpToolException InvalidRange(int requestIndex, int rangeIndex, string message) =>
		new(McpErrorCodes.InvalidArguments,
			$"{McpErrorCodes.InvalidArguments}: requests[{requestIndex - 1}].ranges[{rangeIndex - 1}] {message}.");
}
