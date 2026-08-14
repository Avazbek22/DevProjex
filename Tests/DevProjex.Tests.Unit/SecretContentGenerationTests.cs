using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit;

public sealed class SecretContentGenerationTests
{
	private const string SecretContent = "SENSITIVEVALUE01";
	private const string SafeContent = "harmless-value-1";

	[Fact]
	public async Task SameMetadataReplacement_ReusesWithinGenerationAndRevalidatesAfterRefresh()
	{
		Assert.Equal(SecretContent.Length, SafeContent.Length);
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var path = workspace.CreateFile("project/value.txt", SecretContent);
		var originalWriteTime = File.GetLastWriteTimeUtc(path);
		var detector = new CountingDetector();
		using var session = new SecretRedactionSession(detector);
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());
		var context = new SecretRedactionContext(project, session);

		var initial = await preparer.DiscoverAsync(
			context,
			[path],
			SecretDiscoveryCacheMode.RevalidateContent,
			TestContext.Current.CancellationToken);
		await File.WriteAllTextAsync(path, SafeContent, TestContext.Current.CancellationToken);
		File.SetLastWriteTimeUtc(path, originalWriteTime);
		var reused = await preparer.DiscoverAsync(
			context,
			[path],
			SecretDiscoveryCacheMode.ReuseValidatedContent,
			TestContext.Current.CancellationToken);

		Assert.Equal(1, initial.DetectedCount);
		Assert.Equal(1, reused.DetectedCount);
		Assert.Equal(1, detector.CallCount);

		session.AdvanceContentGeneration(project);
		var refreshed = await preparer.DiscoverAsync(
			context,
			[path],
			SecretDiscoveryCacheMode.ReuseValidatedContent,
			TestContext.Current.CancellationToken);

		Assert.Equal(0, refreshed.DetectedCount);
		Assert.Equal(2, detector.CallCount);
	}

	private sealed class CountingDetector : ISecretDetector
	{
		private int _callCount;

		public int CallCount => Volatile.Read(ref _callCount);

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _callCount);
			var start = content.IndexOf(SecretContent, StringComparison.Ordinal);
			return start < 0
				? []
				: [new DetectedSecret("test-secret", start, SecretContent.Length, SecretContent, 0)];
		}
	}
}
