using System.Text.Json;
using DevProjex.Kernel.Models;

namespace DevProjex.Tests.Unit;

public sealed class AgentJournalOutcomeTests
{
	[Fact]
	public void LegacyCallsDoNotAcquireANewFieldAndCancellationRoundTrips()
	{
		var call = new AgentJournalCall(1, DateTimeOffset.UnixEpoch, "pack_context", null,
			new Dictionary<string, string>(), null, 1, 0, 0, 0, [], 0, 0, 0, [], null);
		var legacy = JsonSerializer.Serialize(call);
		Assert.DoesNotContain("Outcome", legacy, StringComparison.Ordinal);
		Assert.Equal(legacy, JsonSerializer.Serialize(JsonSerializer.Deserialize<AgentJournalCall>(legacy)));
		var cancelled = call with { Outcome = "cancelled" };
		var json = JsonSerializer.Serialize(cancelled);
		Assert.Equal("cancelled", JsonSerializer.Deserialize<AgentJournalCall>(json)!.Outcome);
		Assert.Equal(legacy, JsonSerializer.Serialize(cancelled with { Outcome = null }));
	}
}
