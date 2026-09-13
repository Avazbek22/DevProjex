using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpServiceNoticeMemoTests
{
	private const string Project = "project";
	private const string Filters = "[Effective filters] git: gitignore; exclusions: smart-ignore.";
	private const string Protection = "[Protection] secrets=always · private-data=disabled.";

	[Fact]
	public void AContinuationNamesBothLinesOnlyWhenTheResponseWithholdsBoth()
	{
		var memo = new McpServiceNoticeMemo();
		Deliver(memo, Filters, Protection);

		var repeated = memo.Prepare(Project, Filters, Protection, alwaysSend: false);

		Assert.Null(repeated.Filters);
		Assert.Null(repeated.Protection);
		Assert.Equal(McpServiceNoticeMemo.ContinuationNotice, repeated.Continuation);
	}

	[Fact]
	public void AResponseThatCarriesNoFiltersLineNamesOnlyTheProtectionLine()
	{
		var memo = new McpServiceNoticeMemo();
		Deliver(memo, Filters, Protection);

		var protectionOnly = memo.Prepare(Project, filters: null, Protection, alwaysSend: false);

		Assert.Equal(McpServiceNoticeMemo.ProtectionContinuationNotice, protectionOnly.Continuation);
	}

	[Fact]
	public void AResponseThatCarriesNoProtectionLineNamesOnlyTheFiltersLine()
	{
		var memo = new McpServiceNoticeMemo();
		Deliver(memo, Filters, Protection);

		var filtersOnly = memo.Prepare(Project, Filters, protection: null, alwaysSend: false);

		Assert.Equal(McpServiceNoticeMemo.FiltersContinuationNotice, filtersOnly.Continuation);
	}

	[Fact]
	public void ASessionThatOnlyEverReportedProtectionNeverClaimsAnUnchangedFiltersLine()
	{
		var memo = new McpServiceNoticeMemo();

		var first = memo.Prepare(Project, filters: null, Protection, alwaysSend: false);
		Assert.Null(first.Continuation);
		Assert.Equal(Protection, first.Protection);
		memo.CommitDelivered(Protection);

		var second = memo.Prepare(Project, filters: null, Protection, alwaysSend: false);
		Assert.Equal(McpServiceNoticeMemo.ProtectionContinuationNotice, second.Continuation);

		// The filters line was never reported, so the first response that would carry one sends it
		// in full rather than pointing back at something the session never saw.
		var withFilters = memo.Prepare(Project, Filters, Protection, alwaysSend: false);
		Assert.Equal(Filters, withFilters.Filters);
		Assert.Equal(Protection, withFilters.Protection);
		Assert.Null(withFilters.Continuation);
	}

	[Fact]
	public void EveryContinuationStaysShorterThanTheShortestSetItReplaces()
	{
		Assert.True(McpServiceNoticeMemo.ContinuationNotice.Length < Filters.Length + Protection.Length);
		Assert.True(McpServiceNoticeMemo.FiltersContinuationNotice.Length < Filters.Length);
		Assert.True(McpServiceNoticeMemo.ProtectionContinuationNotice.Length < Protection.Length);
	}

	private static void Deliver(McpServiceNoticeMemo memo, string filters, string protection)
	{
		var initial = memo.Prepare(Project, filters, protection, alwaysSend: false);
		Assert.Equal(filters, initial.Filters);
		Assert.Equal(protection, initial.Protection);
		memo.CommitDelivered(filters + "\n" + protection);
	}
}
