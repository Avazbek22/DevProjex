using System.Text.Json;

namespace DevProjex.Tests.Mcp;

internal static class RecordedProgressAssertions
{
	public static float[] ReadCompletedCall(IReadOnlyList<JsonElement> messages, string token)
	{
		var resultIndices = messages.Select((message, index) => (message, index))
			.Where(static item => item.message.TryGetProperty("result", out var result) &&
				result.ValueKind == JsonValueKind.Object && result.TryGetProperty("content", out _))
			.Select(static item => item.index).ToArray();
		Assert.True(resultIndices.Length == 1, "The server never wrote a unique result for the call.");
		var resultIndex = resultIndices[0];
		var values = new List<float>();
		foreach (var (message, index) in messages.Select((message, index) => (message, index)))
		{
			if (!message.TryGetProperty("method", out var method) ||
				method.GetString() != "notifications/progress")
				continue;
			var parameters = message.GetProperty("params");
			var progressToken = parameters.GetProperty("progressToken");
			if (progressToken.ValueKind != JsonValueKind.String || progressToken.GetString() != token)
				continue;
			Assert.True(index < resultIndex,
				$"The result was written at line {resultIndex + 1}, ahead of the terminal progress " +
				$"notification at line {index + 1}.");
			Assert.Equal(100f, parameters.GetProperty("total").GetSingle());
			values.Add(parameters.GetProperty("progress").GetSingle());
		}
		Assert.Contains(100f, values);
		return values.ToArray();
	}

	public static void AssertThrottled(IReadOnlyList<float> values)
	{
		Assert.InRange(values.Count, 2, 20);
		Assert.Equal(5f, values[0]);
		Assert.Equal(100f, values[^1]);
		for (var index = 1; index < values.Count; index++)
			Assert.True(values[index] > values[index - 1], "Progress must increase in transport order.");
	}

	public static JsonElement[] Parse(string transport) => transport
		.Split('\n', StringSplitOptions.RemoveEmptyEntries)
		.Select(static line =>
		{
			using var document = JsonDocument.Parse(line);
			return document.RootElement.Clone();
		}).ToArray();
}
