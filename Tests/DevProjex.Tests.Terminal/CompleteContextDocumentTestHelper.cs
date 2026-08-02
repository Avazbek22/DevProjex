namespace DevProjex.Tests.Terminal;

internal static class CompleteContextDocumentTestHelper
{
	public static async Task<string> BuildAsync(
		ProjectContextDocumentService service,
		ProjectContextPlan plan,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		CancellationToken cancellationToken,
		bool plain = false)
	{
		using var destination = new MemoryStream();
		await service.WriteCompleteAsync(
				plan,
				view,
				format,
				destination,
				cancellationToken,
				plain)
			.ConfigureAwait(false);
		return Encoding.UTF8.GetString(
			destination.GetBuffer(),
			0,
			checked((int)destination.Length));
	}
}
