using DevProjex.Application.Secrets;
using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpPackDeliveryTests
{
	[Fact]
	public void NewlyUnscannablePreparedFileIsReportedAndNotJournaledAsDelivered()
	{
		var unchanged = Path.GetFullPath("Unchanged.txt");
		var newlyUnavailable = Path.GetFullPath("NewlyUnavailable.txt");
		var earlierUnavailable = Path.GetFullPath("EarlierUnavailable.txt");
		var admissionOnly = Path.GetFullPath("AdmissionOnly.txt");
		var admission = new[]
		{
			new UnscannableFile(earlierUnavailable, FileContentClassification.TooLarge),
			new UnscannableFile(admissionOnly, FileContentClassification.TooLarge)
		};
		var prepared = new[]
		{
			new UnscannableFile(newlyUnavailable, FileContentClassification.AccessDenied),
			new UnscannableFile(earlierUnavailable, FileContentClassification.AccessDenied)
		};

		var merged = McpPackDelivery.MergeUnscannableFiles(admission, prepared);
		Assert.Equal([earlierUnavailable, admissionOnly, newlyUnavailable],
			merged.Select(static file => file.Path));
		Assert.Equal(FileContentClassification.AccessDenied,
			merged.Single(file => file.Path == earlierUnavailable).Classification);
		Assert.Equal(FileContentClassification.AccessDenied,
			merged.Single(file => file.Path == newlyUnavailable).Classification);
		Assert.Equal(FileContentClassification.TooLarge,
			merged.Single(file => file.Path == admissionOnly).Classification);
		Assert.Equal([unchanged], McpPackDelivery.DeliveredPaths(
			[unchanged, newlyUnavailable, earlierUnavailable, admissionOnly], merged));
	}
}
