using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpResponseAdviceTests
{
	public static TheoryData<McpToolSet, bool, string> ProfileCases => new()
	{
		{ McpToolSet.Full, false, "use profile 'standard'." },
		{ McpToolSet.Reduced, false, "use profile 'standard'." },
		{ McpToolSet.Full, true, "retry with 'profile' omitted or set to 'local' after persistent secret storage is available." },
		{ McpToolSet.Reduced, true, "retry with 'profile' omitted or set to 'local' after persistent secret storage is available." }
	};

	public static TheoryData<McpToolSet, bool, int, string?> StoredResultCases => new()
	{
		{ McpToolSet.Full, false, (int)McpStoredResultKind.Pack, null },
		{ McpToolSet.Full, false, (int)McpStoredResultKind.Search, null },
		{ McpToolSet.Full, false, (int)McpStoredResultKind.Related, null },
		{ McpToolSet.Reduced, false, (int)McpStoredResultKind.Pack, null },
		{ McpToolSet.Reduced, false, (int)McpStoredResultKind.Search, null },
		{ McpToolSet.Reduced, false, (int)McpStoredResultKind.Related, null },
		{ McpToolSet.Full, true, (int)McpStoredResultKind.Pack, "pack_context" },
		{ McpToolSet.Full, true, (int)McpStoredResultKind.Search, "search_project" },
		{ McpToolSet.Full, true, (int)McpStoredResultKind.Related, "related_files" },
		{ McpToolSet.Reduced, true, (int)McpStoredResultKind.Pack, null },
		{ McpToolSet.Reduced, true, (int)McpStoredResultKind.Search, "search_project" },
		{ McpToolSet.Reduced, true, (int)McpStoredResultKind.Related, "related_files" }
	};

	[Theory]
	[MemberData(nameof(StoredResultCases))]
	public void StoredResultRefreshAdviceNamesAnAvailableProducer(
		McpToolSet toolSet,
		bool live,
		int kindValue,
		string? expected)
	{
		var kind = (McpStoredResultKind)kindValue;
		var actual = live ? McpStoredResultAdvice.RefreshTool(toolSet, kind) : null;

		Assert.Equal(expected, actual);
	}

	[Theory]
	[MemberData(nameof(ProfileCases))]
	public void ProfileRecoveryAdviceIsValidForTheServerMode(
		McpToolSet toolSet,
		bool live,
		string expected)
	{
		_ = McpServerHost.BuildInstructions(1, toolSet, live: live);

		var remedy = McpProjectService.PersistentRedactionIdentityRemedy(live);

		Assert.Equal(expected, remedy);
		if (live)
			Assert.DoesNotContain("profile 'standard'", remedy, StringComparison.Ordinal);
	}
}
