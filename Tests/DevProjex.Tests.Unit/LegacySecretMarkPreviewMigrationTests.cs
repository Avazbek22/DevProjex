using System.Security.Cryptography;
using System.Runtime.InteropServices;
using DevProjex.Application.Compression;
using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class LegacySecretMarkPreviewMigrationTests
{
	private const string Secret = "preview-only-legacy-secret";

	[Fact]
	public async Task PreviewOnlyMatch_FlushesLegacyMigrationDurablyAndOnlyOnce()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var path = workspace.CreateFile("project/config.txt", $"token={Secret}\n");
		var legacy = CreateLegacyMark();
		var store = new MigrationStore(new PersistentSecretMarksSnapshot(1, [legacy]));
		using var session = CreateSession(store);
		session.ReplacePersistentMarks(project, store.Snapshot);
		var builder = new PreviewDocumentBuilder(new FileContentAnalyzer());
		var context = ContentTransformationContext.For(
			null,
			new SecretRedactionContext(project, session));

		using (var preview = await builder.BuildContentDocumentAsync(
			       [path],
			       TestContext.Current.CancellationToken,
			       Path.GetFileName,
			       transformationContext: context))
		{
			Assert.DoesNotContain(Secret, preview!.GetFullText(), StringComparison.Ordinal);
			Assert.Equal("config.txt", Assert.Single(preview.Redactions).RelativePath);
		}
		await session.WaitForPreviewMigrationFlushAsync().WaitAsync(TestContext.Current.CancellationToken);
		Assert.Equal(1, store.WriteCount);
		Assert.True(PersistentSecretIdentity.IsV2(Assert.Single(store.Snapshot.Marks).H));

		using var secondPreview = await builder.BuildContentDocumentAsync(
			[path],
			TestContext.Current.CancellationToken,
			Path.GetFileName,
			transformationContext: context);
		await session.WaitForPreviewMigrationFlushAsync().WaitAsync(TestContext.Current.CancellationToken);

		Assert.Equal(1, store.WriteCount);
		Assert.DoesNotContain(Secret, secondPreview!.GetFullText(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task ProjectSwitchDuringPreviewFlush_DoesNotApplyOldMigrationToNewProject()
	{
		using var workspace = new TemporaryDirectory();
		var projectA = workspace.CreateFolder("project-a");
		var projectB = workspace.CreateFolder("project-b");
		var path = workspace.CreateFile("project-a/config.txt", $"token={Secret}\n");
		var legacy = CreateLegacyMark();
		var projectBMark = legacy with { H = "v2:" + new string('b', 64), Key = "PROJECT_B" };
		var store = new MigrationStore(new PersistentSecretMarksSnapshot(1, [legacy]), blockWrites: true);
		using var session = CreateSession(store);
		session.ReplacePersistentMarks(projectA, store.Snapshot);
		var context = ContentTransformationContext.For(
			null,
			new SecretRedactionContext(projectA, session));

		using var preview = await new PreviewDocumentBuilder(new FileContentAnalyzer())
			.BuildContentDocumentAsync(
				[path],
				TestContext.Current.CancellationToken,
				Path.GetFileName,
				transformationContext: context);
		await store.WriteStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
		session.ReplacePersistentMarks(projectB, new PersistentSecretMarksSnapshot(7, [projectBMark]));
		var projectBScope = session.BeginOutput(projectB, Array.Empty<string>());
		projectBScope.Complete();
		store.ReleaseWrite.TrySetResult();
		await session.WaitForPreviewMigrationFlushAsync().WaitAsync(TestContext.Current.CancellationToken);

		Assert.Equal(projectBMark, Assert.Single(session.GetMarkedSecrets()));
		Assert.Equal(1, store.WriteCount);
	}

	private static SecretRedactionSession CreateSession(MigrationStore store) =>
		new(new EmptyDetector(), store, new TestIdentityProvider());

	private static MarkedSecretProfileEntry CreateLegacyMark()
	{
		Assert.True(MarkedSecretValueNormalizer.TryCreate(Secret, out var value, out _));
		return new MarkedSecretProfileEntry(value.Hash, "TOKEN", value.Length);
	}

	private sealed class EmptyDetector : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) => [];
	}

	private sealed class TestIdentityProvider : IPersistentSecretIdentityProvider
	{
		private static readonly byte[] Key = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();

		public bool IsAvailable => true;

		public bool TryComputeDigest(ReadOnlySpan<char> normalizedValue, Span<byte> destination)
		{
			HMACSHA256.HashData(Key, MemoryMarshal.AsBytes(normalizedValue), destination);
			return true;
		}
	}

	private sealed class MigrationStore(
		PersistentSecretMarksSnapshot snapshot,
		bool blockWrites = false) : IPersistentSecretMarkStore
	{
		private PersistentSecretMarksSnapshot _snapshot = snapshot;

		public PersistentSecretMarksSnapshot Snapshot => Volatile.Read(ref _snapshot);
		public int WriteCount => Volatile.Read(ref _writeCount);
		public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private int _writeCount;

		public ValueTask<PersistentSecretMarksLoadResult> LoadMarksAsync(
			string localProjectPath,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(new PersistentSecretMarksLoadResult(
				PersistentSecretMarkStoreStatus.Success,
				Snapshot));

		public ValueTask<PersistentSecretMarkWriteResult> AddMarkAsync(
			string localProjectPath,
			MarkedSecretProfileEntry mark,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public ValueTask<PersistentSecretMarkWriteResult> RemoveMarkAsync(
			string localProjectPath,
			PersistentSecretMarkId markId,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public async ValueTask<PersistentSecretMarkWriteResult> ApplyMarkDeltaAsync(
			string localProjectPath,
			PersistentSecretMarkDelta delta,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _writeCount);
			WriteStarted.TrySetResult();
			if (blockWrites)
				await ReleaseWrite.Task.WaitAsync(cancellationToken);
			var replacement = delta.Mark ?? throw new InvalidOperationException("A migration must carry its replacement mark.");
			var current = Snapshot;
			var updated = new PersistentSecretMarksSnapshot(current.Revision + 1, [replacement]);
			Volatile.Write(ref _snapshot, updated);
			return new PersistentSecretMarkWriteResult(PersistentSecretMarkStoreStatus.Success, updated);
		}
	}
}
