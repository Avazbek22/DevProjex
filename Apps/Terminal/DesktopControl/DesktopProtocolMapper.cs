using System.Text.Json;

namespace DevProjex.Terminal.DesktopControl;

internal static class DesktopProtocolMapper
{
	public static DesktopInteractionRequest Map(DesktopProtocolRequest request)
	{
		return request.Action switch
		{
			"status" => new DesktopStatusRequest(),
			"activate" => new DesktopActivateRequest(),
			"open" => new DesktopOpenProjectRequest(Deserialize<DesktopOpenRequest>(request.Payload)),
			"preview.open" => new DesktopPreviewRequest(true, ReadOptionalPreviewView(request.Payload)),
			"preview.close" => new DesktopPreviewRequest(false),
			"preview.set-view" => new DesktopPreviewViewRequest(ReadPreviewView(request.Payload)),
			"tree.set-format" => new DesktopTreeFormatRequest(ReadTreeFormat(request.Payload)),
			"filter.set" => new DesktopFilterRequest(ReadRequiredString(request.Payload, "query")),
			"filter.clear" => new DesktopFilterRequest(null),
			"search.set" => new DesktopSearchRequest(
				DesktopSearchOperation.Set,
				ReadRequiredString(request.Payload, "query")),
			"search.next" => new DesktopSearchRequest(DesktopSearchOperation.Next),
			"search.previous" => new DesktopSearchRequest(DesktopSearchOperation.Previous),
			"search.clear" => new DesktopSearchRequest(DesktopSearchOperation.Clear),
			_ => throw new DesktopControlException(
				"DPX-DESKTOP-UNKNOWN-ACTION",
				"The desktop action is not supported.",
				CommandLineExitCodes.UsageError)
		};
	}

	private static T Deserialize<T>(JsonElement payload)
	{
		try
		{
			return payload.Deserialize<T>(new JsonSerializerOptions
			{
				PropertyNameCaseInsensitive = true
			}) ?? throw new JsonException();
		}
		catch (JsonException exception)
		{
			throw new DesktopControlException(
				"DPX-DESKTOP-INVALID-PAYLOAD",
				"The desktop request payload is invalid.",
				CommandLineExitCodes.UsageError,
				exception);
		}
	}

	private static DesktopPreviewView? ReadOptionalPreviewView(JsonElement payload)
	{
		if (!payload.TryGetProperty("view", out var value) || value.ValueKind == JsonValueKind.Null)
			return null;
		return ParsePreviewView(value.GetString());
	}

	private static DesktopPreviewView ReadPreviewView(JsonElement payload) =>
		ParsePreviewView(ReadRequiredString(payload, "view"));

	private static DesktopPreviewView ParsePreviewView(string? value) =>
		value?.ToLowerInvariant() switch
		{
			"tree" => DesktopPreviewView.Tree,
			"content" => DesktopPreviewView.Content,
			"tree-content" => DesktopPreviewView.TreeContent,
			_ => throw new DesktopControlException(
				"DPX-DESKTOP-INVALID-PAYLOAD",
				"The preview view is invalid.",
				CommandLineExitCodes.UsageError)
		};

	private static TreeTextFormat ReadTreeFormat(JsonElement payload) =>
		ReadRequiredString(payload, "format").ToLowerInvariant() switch
		{
			"text" => TreeTextFormat.Ascii,
			"markdown" => TreeTextFormat.Markdown,
			"json" => TreeTextFormat.Json,
			"xml" => TreeTextFormat.Xml,
			_ => throw new DesktopControlException(
				"DPX-DESKTOP-INVALID-PAYLOAD",
				"The tree format is invalid.",
				CommandLineExitCodes.UsageError)
		};

	private static string ReadRequiredString(JsonElement payload, string name)
	{
		if (payload.ValueKind == JsonValueKind.Object &&
		    payload.TryGetProperty(name, out var value) &&
		    value.ValueKind == JsonValueKind.String &&
		    !string.IsNullOrWhiteSpace(value.GetString()))
		{
			return value.GetString()!;
		}

		throw new DesktopControlException(
			"DPX-DESKTOP-INVALID-PAYLOAD",
			"The desktop request payload is invalid.",
			CommandLineExitCodes.UsageError);
	}
}
