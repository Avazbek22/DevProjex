using DevProjex.Application.Secrets;
using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpStoredJournalContextTests
{
	[Fact]
	public void RelatedEvidenceAdmissionBoundsTheWholePass()
	{
		long admitted = 0;

		Assert.True(DevProjexMcpTools.TryAdmitRelatedEvidenceSource(40L * 1024 * 1024, ref admitted));
		Assert.False(DevProjexMcpTools.TryAdmitRelatedEvidenceSource(30L * 1024 * 1024, ref admitted));
		Assert.True(DevProjexMcpTools.TryAdmitRelatedEvidenceSource(24L * 1024 * 1024, ref admitted));
		Assert.Equal(64L * 1024 * 1024, admitted);
	}

	[Fact]
	public void AbsentPreparationIsAKnownZeroProtectionCount()
	{
		var count = DevProjexMcpTools.TryGetPreparedRedactionCount(prepared: null, path: "related.cs");

		Assert.Equal(0, count.Count);
		Assert.True(count.Known);
	}

	[Fact]
	public async Task MissingPreparedSourceProducesUnknownProtectionCount()
	{
		await using var prepared = new PreparedSecretRedactionOutput(
			workingDirectory: null,
			new Dictionary<string, PreparedSecretFile>(StringComparer.Ordinal),
			snapshot: null);

		var count = DevProjexMcpTools.TryGetPreparedRedactionCount(prepared, "disappeared.cs");

		Assert.Equal(0, count.Count);
		Assert.False(count.Known);
	}

	[Fact]
	public void StoredJournalPageKeepsUnknownProtectionDistinctFromZero()
	{
		var context = new McpStoredJournalContext(
			"root",
			revision: 1,
			[
				new McpStoredJournalPath("known.cs", 0, 0, [new McpStoredLineRange(1, 5)]),
				new McpStoredJournalPath("unknown.cs", 0, 0, [new McpStoredLineRange(6, 10)], ProtectionKnown: false)
			]);

		Assert.True(Assert.Single(context.PathsForPage(1, 5)).ProtectionKnown);
		Assert.False(Assert.Single(context.PathsForPage(6, 10)).ProtectionKnown);
	}
}
