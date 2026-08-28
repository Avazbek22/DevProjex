using DevProjex.Application.Compression;
using DevProjex.Application.Context;

namespace DevProjex.Tests.Unit;

public sealed class ProjectContextDocumentReadAheadTests
{
	[Fact]
	public async Task CompleteSnapshotBudgetSerializesFilesLargerThanHalfTheBudget()
	{
		using var workspace = new TemporaryDirectory();
		var path = Path.Combine(workspace.Path, "large.txt");
		await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
			file.SetLength(3L * 1024 * 1024);
		var reservation = ProjectContextDocumentService.EstimateCompleteSnapshotRetainedBytes(path);
		using var budget = new WeightedByteBudget(
			ProjectContextDocumentService.MaximumCompleteSnapshotReadAheadRetainedBytes);
		using var first = await budget.AcquireAsync(
			reservation,
			TestContext.Current.CancellationToken);
		var secondRequest = budget.AcquireAsync(
			reservation,
			TestContext.Current.CancellationToken).AsTask();

		Assert.False(secondRequest.IsCompleted);
		first.Dispose();
		using var second = await secondRequest.WaitAsync(TestContext.Current.CancellationToken);
	}
}
