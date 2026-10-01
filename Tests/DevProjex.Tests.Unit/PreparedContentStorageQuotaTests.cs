using DevProjex.Application.Compression;
using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class PreparedContentStorageQuotaTests
{
	[Fact]
	public void ConcurrentLeasesShareOnePreparedByteQuota()
	{
		var quota = new PreparedContentStorageQuota(
			maximumPreparedBytes: 8,
			freeSpaceReserveBytes: 0,
			_ => long.MaxValue);
		using var first = quota.CreateLease();
		using var second = quota.CreateLease();

		first.ReserveForWrite("first", 6);
		var exception = Assert.Throws<SecretDetectionException>(
			() => second.ReserveForWrite("second", 3));

		Assert.Equal("Prepared content exceeded the shared storage quota.", exception.Message);
		Assert.Equal(6, quota.ReservedBytes);
		first.Dispose();
		second.ReserveForWrite("second", 3);
		Assert.Equal(3, quota.ReservedBytes);
	}

	[Fact]
	public void EveryReservationKeepsTheConfiguredFreeSpaceFloor()
	{
		var available = 20L;
		var quota = new PreparedContentStorageQuota(
			maximumPreparedBytes: 100,
			freeSpaceReserveBytes: 10,
			_ => available);
		using var lease = quota.CreateLease();

		lease.ReserveForWrite("prepared", 6);
		available = 16;
		var exception = Assert.Throws<SecretDetectionException>(
			() => lease.ReserveForWrite("prepared", 1));

		Assert.Equal("Prepared content storage has insufficient free space.", exception.Message);
		Assert.Equal(6, quota.ReservedBytes);
	}

	[Fact]
	public async Task FailedPreparedWriteReleasesQuotaAndDoesNotReturnOutput()
	{
		const string secret = "SENSITIVE_VALUE";
		using var workspace = new TemporaryDirectory();
		var source = workspace.CreateFile("source.txt", "value=" + secret);
		var quota = new PreparedContentStorageQuota(
			maximumPreparedBytes: 4,
			freeSpaceReserveBytes: 0,
			_ => long.MaxValue);
		using var session = new SecretRedactionSession(new ExactValueDetector(secret));
		var preparer = new SecretRedactionOutputPreparer(
			new FileContentAnalyzer(),
			preparedContentAnalyzer: null,
			preparedContentQuota: quota);

		var exception = await Assert.ThrowsAsync<SecretDetectionException>(() => preparer.PrepareAsync(
			new ContentTransformationContext(
				Compression: null,
				Redaction: new SecretRedactionContext(workspace.Path, session)),
			[source],
			TestContext.Current.CancellationToken));

		Assert.Equal("Prepared content exceeded the shared storage quota.", exception.Message);
		Assert.Equal(0, quota.ReservedBytes);
	}

	[Fact]
	public async Task PreparedOutputHoldsActualBytesUntilCleanup()
	{
		const string secret = "SENSITIVE_VALUE";
		using var workspace = new TemporaryDirectory();
		var source = workspace.CreateFile("source.txt", "value=" + secret);
		var quota = new PreparedContentStorageQuota(
			maximumPreparedBytes: 1024 * 1024,
			freeSpaceReserveBytes: 0,
			_ => long.MaxValue);
		using var session = new SecretRedactionSession(new ExactValueDetector(secret));
		var preparer = new SecretRedactionOutputPreparer(
			new FileContentAnalyzer(),
			preparedContentAnalyzer: null,
			preparedContentQuota: quota);

		var prepared = await preparer.PrepareAsync(
			new ContentTransformationContext(
				Compression: null,
				Redaction: new SecretRedactionContext(workspace.Path, session)),
			[source],
			TestContext.Current.CancellationToken);

		Assert.True(quota.ReservedBytes > 0);
		await prepared.DisposeAsync();
		Assert.Equal(0, quota.ReservedBytes);
	}

	[Fact]
	public async Task PreparedWriteFailsClosedWhenFreeSpaceCannotBeReserved()
	{
		const string secret = "SENSITIVE_VALUE";
		using var workspace = new TemporaryDirectory();
		var source = workspace.CreateFile("source.txt", "value=" + secret);
		var quota = new PreparedContentStorageQuota(
			maximumPreparedBytes: 1024 * 1024,
			freeSpaceReserveBytes: 128,
			_ => 128);
		using var session = new SecretRedactionSession(new ExactValueDetector(secret));
		var preparer = new SecretRedactionOutputPreparer(
			new FileContentAnalyzer(),
			preparedContentAnalyzer: null,
			preparedContentQuota: quota);

		var exception = await Assert.ThrowsAsync<SecretDetectionException>(() => preparer.PrepareAsync(
			new ContentTransformationContext(
				Compression: null,
				Redaction: new SecretRedactionContext(workspace.Path, session)),
			[source],
			TestContext.Current.CancellationToken));

		Assert.Equal("Prepared content storage has insufficient free space.", exception.Message);
		Assert.Equal(0, quota.ReservedBytes);
	}

	private sealed class ExactValueDetector(string secret) : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default)
		{
			var index = content.IndexOf(secret, StringComparison.Ordinal);
			return index < 0
				? []
				: [new DetectedSecret("exact", index, secret.Length, secret, RuleOrder: 0)];
		}
	}
}
