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
	bool IsWholeFile = false,
	bool EndLineWasSpecified = true)
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

internal sealed record McpGetFileRequestSet(bool IsBatch, IReadOnlyList<McpGetFileRequest> Requests,
	string? ReadAsNotice = null, int CombinedPathStartIndex = 0)
{
	public const int MaximumFiles = 16;
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

	public static McpGetFileRequestSet Parse(McpJsonArguments arguments, bool deferCombinedPathValidation = false)
	{
		if (arguments.Contains("requests"))
		{
			if (new[] { "start_line", "end_line", "start_column", "symbol" }.Any(arguments.Contains))
				throw Invalid("start_line, end_line, start_column, and symbol cannot accompany requests");
			arguments.TryGetElement("requests", out var value);
			var parsed = ParseCore(Create(new Dictionary<string, JsonElement> { ["requests"] = value }));
			var requests = parsed.Requests.ToList();
			var seen = requests.Select(static item => RequestedIdentity(item.Path))
				.ToHashSet(ProjectTreePathIdentity.CanonicalComparer);
			var added = 0;
			foreach (var path in ReadPaths(arguments))
			{
				if (!seen.Add(RequestedIdentity(path)))
					continue;
				var index = requests.Count + 1;
				requests.Add(new(index, path, [new(index, 1, 1, int.MaxValue, IsWholeFile: true)]));
				added++;
			}
			if (!deferCombinedPathValidation && (requests.Count > MaximumFiles || requests.Sum(static item => item.Ranges.Count) > MaximumRanges))
				throw Invalid($"the call must contain at most {MaximumFiles} files and {MaximumRanges} file selections");
			return new(true, requests, added == 0 ? null :
				$"[Read as] path and requests were combined; {added} file(s) from path are read whole.",
				CombinedPathStartIndex: added == 0 ? 0 : parsed.Requests.Count);
		}
		if (!arguments.Contains("paths") && (!arguments.TryGetElement("path", out var pathValue) ||
			pathValue.ValueKind != JsonValueKind.Array))
			return ParseCore(arguments);
		var paths = ReadPaths(arguments).GroupBy(RequestedIdentity, ProjectTreePathIdentity.CanonicalComparer).Select(static group => group.First()).ToArray();
		if (paths.Length is < 1 or > MaximumFiles)
			throw Invalid($"'paths' must be an array with 1 to {MaximumFiles} entries");
		if (paths.Length == 1)
		{
			var values = new Dictionary<string, JsonElement> { ["path"] = JsonSerializer.SerializeToElement(paths[0]) };
			foreach (var name in new[] { "start_line", "end_line", "start_column", "symbol" })
				if (arguments.TryGetElement(name, out var field)) values[name] = field;
			return ParseCore(Create(values));
		}
		if (paths.Length > 1 && (arguments.Contains("start_column") || arguments.Contains("symbol")))
			throw Invalid("start_column and symbol are valid only with a single 'path'");
		RejectSeveralLineNumbers(arguments, "start_line");
		RejectSeveralLineNumbers(arguments, "end_line");
		var start = arguments.OptionalInteger("start_line", 1, int.MaxValue) ?? 1;
		var end = arguments.OptionalInteger("end_line", 1, int.MaxValue) ?? int.MaxValue;
		var ranged = arguments.Contains("start_line") || arguments.Contains("end_line");
		if (start > end)
			throw Invalid("start_line must not exceed end_line");
		return new(paths.Length > 1, paths.Select((path, index) => new McpGetFileRequest(index + 1, path,
			[new(index + 1, 1, start, end, IsWholeFile: !ranged, EndLineWasSpecified: !ranged || arguments.Contains("end_line"))], arguments.OptionalString("symbol"))).ToArray(),
			paths.Length > 1 && ranged ? $"[Read as] start_line and end_line were applied to each of the {paths.Length} files." : null);
	}

	public McpGetFileRequestSet ResolveCombinedPaths(Func<string, string> identity)
	{
		if (CombinedPathStartIndex == 0)
			return this;
		var requests = Requests.Take(CombinedPathStartIndex).ToList();
		var seen = requests.Select(item => identity(item.Path)).ToHashSet(ProjectTreePathIdentity.CanonicalComparer);
		foreach (var item in Requests.Skip(CombinedPathStartIndex))
		{
			if (!seen.Add(identity(item.Path)))
				continue;
			var index = requests.Count + 1;
			requests.Add(item with { Index = index, Ranges = item.Ranges.Select(range => range with { RequestIndex = index }).ToArray() });
		}
		if (requests.Count > MaximumFiles || requests.Sum(static item => item.Ranges.Count) > MaximumRanges)
			throw Invalid($"the call must contain at most {MaximumFiles} files and {MaximumRanges} file selections");
		var added = requests.Count - CombinedPathStartIndex;
		return this with
		{
			Requests = requests,
			ReadAsNotice = added == 0 ? null : $"[Read as] path and requests were combined; {added} file(s) from path are read whole.",
			CombinedPathStartIndex = 0
		};
	}

	private static McpJsonArguments Create(Dictionary<string, JsonElement> values) =>
		new(values, values.Keys.ToHashSet(StringComparer.Ordinal));

	private static string RequestedIdentity(string path)
	{
		try
		{
			return Path.IsPathRooted(path) ? Path.GetFullPath(path) : ProjectSelectionPath.NormalizeRelative(path);
		}
		catch (Exception exception) when (exception is ArgumentException or ProjectContextValidationException)
		{
			return path;
		}
	}

	private static IEnumerable<string> ReadPaths(McpJsonArguments arguments)
	{
		foreach (var name in new[] { "path", "paths" })
		{
			if (!arguments.TryGetElement(name, out var element))
				continue;
			var values = element.ValueKind switch
			{
				JsonValueKind.Array => element.EnumerateArray().ToArray(),
				JsonValueKind.String => [element],
				_ => throw Invalid($"'{name}' must be a string or an array of strings")
			};
			if (values.Length is < 1 or > MaximumFiles)
				throw Invalid($"'{name}' must be an array with 1 to {MaximumFiles} entries");
			for (var index = 0; index < values.Length; index++)
			{
				var value = values[index];
				if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
					throw Invalid($"{name}[{index}] must be a non-whitespace string");
				var path = value.GetString()!;
				if (McpUnicodeLength.ExceedsScalarValueCount(path, McpProjectService.MaximumRequestedPathLength))
					throw Invalid($"{name}[{index}] must contain at most {McpProjectService.MaximumRequestedPathLength} characters");
				yield return path;
			}
		}
	}

	private static McpGetFileRequestSet ParseCore(McpJsonArguments arguments)
	{
		var hasPath = arguments.Contains("path");
		var hasPaths = arguments.Contains("paths");
		var hasRequests = arguments.Contains("requests");
		var formCount = (hasPath ? 1 : 0) + (hasPaths ? 1 : 0) + (hasRequests ? 1 : 0);
		if (formCount > 1)
			throw Invalid("'path', 'paths' and 'requests' cannot be combined");
		if (formCount == 0)
			throw Invalid("exactly one of 'path', 'paths', or 'requests' is required");

		if (hasPaths)
			return ParseWholeFilePaths(arguments, "paths");
		if (hasPath && arguments.TryGetElement("path", out var pathArgument) &&
			pathArgument.ValueKind == JsonValueKind.Array)
		{
			return ParseWholeFilePaths(arguments, "path");
		}

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
		arguments.TryGetElement("requests", out var requestsElement);
		if (requestsElement.ValueKind == JsonValueKind.String)
		{
			var encoded = requestsElement.GetString()!.Trim();
			if (encoded.StartsWith('['))
			{
				try
				{
					using var document = JsonDocument.Parse(encoded);
					if (document.RootElement.ValueKind == JsonValueKind.Array)
						requestsElement = document.RootElement.Clone();
				}
				catch (JsonException)
				{
				}
			}
		}
		if (requestsElement.ValueKind != JsonValueKind.Array ||
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

	// A path list is the batch form without ranges: every entry reads its whole file under the
	// same file and selection limits as requests.
	private static McpGetFileRequestSet ParseWholeFilePaths(McpJsonArguments arguments, string name)
	{
		if (arguments.Contains("start_line") || arguments.Contains("end_line") || arguments.Contains("start_column"))
			throw Invalid("start_line, end_line, and start_column are valid only with a single 'path'");
		arguments.TryGetElement(name, out var element);
		JsonElement[] entries = element.ValueKind switch
		{
			JsonValueKind.String => [element],
			JsonValueKind.Array => [.. element.EnumerateArray()],
			_ => throw Invalid($"'{name}' must be a string or an array of strings")
		};
		if (entries.Length is < 1 or > MaximumFiles)
			throw Invalid($"'{name}' must be an array with 1 to {MaximumFiles} entries");

		var requests = new List<McpGetFileRequest>(entries.Length);
		for (var index = 0; index < entries.Length; index++)
		{
			var entry = entries[index];
			if (entry.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entry.GetString()))
				throw Invalid($"{name}[{index}] must be a non-whitespace string");
			var path = entry.GetString()!;
			if (McpUnicodeLength.ExceedsScalarValueCount(path, McpProjectService.MaximumRequestedPathLength))
				throw Invalid($"{name}[{index}] must contain at most {McpProjectService.MaximumRequestedPathLength} characters");
			var requestIndex = index + 1;
			requests.Add(new McpGetFileRequest(
				requestIndex,
				path,
				[new McpGetFileRange(requestIndex, 1, 1, int.MaxValue, IsWholeFile: true)]));
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
