using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class PersistentSecretMarkOverlayTests
{
	private const string FirstSecret = "manual-value-that-detector-misses-a";
	private const string SecondSecret = "manual-value-that-detector-misses-b";

	[Fact]
	public async Task PendingAdd_SurvivesRepeatedDurableRefreshAndStrictScan()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var path = workspace.CreateFile("project/config.env", $"TOKEN={FirstSecret}\n");
		var store = new MutableMarkStore(new PersistentSecretMarksSnapshot(4, []));
		using var session = new SecretRedactionSession(new EmptyDetector(), store);
		session.ReplacePersistentMarks(project, store.Snapshot);
		var mark = Mark(FirstSecret);
		var delta = PersistentSecretMarkDelta.Add(mark);

		Assert.True(session.StagePersistentMarkDelta(project, delta));
		await session.RefreshPersistentMarksAsync(project, TestContext.Current.CancellationToken);
		await session.RefreshPersistentMarksAsync(project, TestContext.Current.CancellationToken);

		Assert.Equal(mark, Assert.Single(session.GetMarkedSecrets()));
		Assert.DoesNotContain(FirstSecret, Redact(session, project, path), StringComparison.Ordinal);
	}

	[Fact]
	public void TwoPendingAdds_AcknowledgedInReverseOrder_ConvergeWithoutLoss()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		using var session = new SecretRedactionSession(new EmptyDetector());
		session.ReplacePersistentMarks(project, PersistentSecretMarksSnapshot.Empty);
		var first = Mark(FirstSecret);
		var second = Mark(SecondSecret);
		var firstDelta = PersistentSecretMarkDelta.Add(first);
		var secondDelta = PersistentSecretMarkDelta.Add(second);

		Assert.True(session.StagePersistentMarkDelta(project, firstDelta));
		Assert.True(session.StagePersistentMarkDelta(project, secondDelta));
		session.AcknowledgePersistentMarkDelta(
			project,
			secondDelta.OperationId,
			new PersistentSecretMarksSnapshot(2, [first, second]));
		session.AcknowledgePersistentMarkDelta(
			project,
			firstDelta.OperationId,
			new PersistentSecretMarksSnapshot(1, [first]));

		Assert.Equal(
			new[] { first.H, second.H }.Order().ToArray(),
			session.GetMarkedSecrets().Select(static mark => mark.H).Order().ToArray());
	}

	[Fact]
	public async Task RefreshBetweenPendingAdds_PreservesTheirIssueOrder()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var store = new MutableMarkStore(new PersistentSecretMarksSnapshot(7, []));
		using var session = new SecretRedactionSession(new EmptyDetector(), store);
		session.ReplacePersistentMarks(project, store.Snapshot);
		var first = PersistentSecretMarkDelta.Add(Mark(FirstSecret));
		var second = PersistentSecretMarkDelta.Add(Mark(SecondSecret));

		session.StagePersistentMarkDelta(project, first);
		await session.RefreshPersistentMarksAsync(project, TestContext.Current.CancellationToken);
		session.StagePersistentMarkDelta(project, second);
		await session.RefreshPersistentMarksAsync(project, TestContext.Current.CancellationToken);

		Assert.Equal(2, session.GetMarkedSecrets().Count);
	}

	[Fact]
	public async Task PendingRemove_RemainsVisibleToStrictRefreshAndRollbackRestoresDurableMark()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var path = workspace.CreateFile("project/config.env", $"TOKEN={FirstSecret}\n");
		var mark = Mark(FirstSecret);
		var store = new MutableMarkStore(new PersistentSecretMarksSnapshot(3, [mark]));
		using var session = new SecretRedactionSession(new EmptyDetector(), store);
		session.ReplacePersistentMarks(project, store.Snapshot);
		var remove = PersistentSecretMarkDelta.Remove(new PersistentSecretMarkId(mark.H, mark.Length));

		Assert.True(session.StagePersistentMarkDelta(project, remove));
		await session.RefreshPersistentMarksAsync(project, TestContext.Current.CancellationToken);
		Assert.Contains(FirstSecret, Redact(session, project, path), StringComparison.Ordinal);

		Assert.True(session.RollbackPendingPersistentMarkDelta(project, remove.OperationId));
		Assert.DoesNotContain(FirstSecret, Redact(session, project, path), StringComparison.Ordinal);
	}

	[Fact]
	public async Task PendingReplace_AppliesAfterEveryDurableRefresh()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var path = workspace.CreateFile(
			"project/config.env",
			$"OLD={FirstSecret}\nNEW={SecondSecret}\n");
		var oldMark = Mark(FirstSecret);
		var newMark = Mark(SecondSecret);
		var store = new MutableMarkStore(new PersistentSecretMarksSnapshot(5, [oldMark]));
		using var session = new SecretRedactionSession(new EmptyDetector(), store);
		session.ReplacePersistentMarks(project, store.Snapshot);
		var replace = PersistentSecretMarkDelta.Replace(
			new PersistentSecretMarkId(oldMark.H, oldMark.Length),
			newMark);

		Assert.True(session.StagePersistentMarkDelta(project, replace));
		await session.RefreshPersistentMarksAsync(project, TestContext.Current.CancellationToken);
		var output = Redact(session, project, path);

		Assert.Contains(FirstSecret, output, StringComparison.Ordinal);
		Assert.DoesNotContain(SecondSecret, output, StringComparison.Ordinal);
	}

	[Fact]
	public void FailedPendingAdd_DowngradesWithoutAVisibilityGap()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var path = workspace.CreateFile("project/config.env", $"TOKEN={FirstSecret}\n");
		using var session = new SecretRedactionSession(new EmptyDetector());
		session.ReplacePersistentMarks(project, PersistentSecretMarksSnapshot.Empty);
		var mark = Mark(FirstSecret);
		var add = PersistentSecretMarkDelta.Add(mark);
		session.StagePersistentMarkDelta(project, add);

		Assert.True(session.AddSessionMarkedSecret("config.env", "TOKEN=".Length, Value(FirstSecret)));
		Assert.True(session.RollbackPendingPersistentMarkDelta(project, add.OperationId));

		Assert.Empty(session.GetMarkedSecrets());
		Assert.DoesNotContain(FirstSecret, Redact(session, project, path), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RefreshCompletingAfterProjectSwitch_DropsTheOldProjectOverlay()
	{
		using var workspace = new TemporaryDirectory();
		var projectA = workspace.CreateFolder("project-a");
		var projectB = workspace.CreateFolder("project-b");
		var projectBMark = Mark(SecondSecret);
		var store = new BlockingLoadStore(new PersistentSecretMarksSnapshot(2, [Mark(FirstSecret)]));
		using var session = new SecretRedactionSession(new EmptyDetector(), store);
		session.ReplacePersistentMarks(projectA, new PersistentSecretMarksSnapshot(1, []));
		session.StagePersistentMarkDelta(projectA, PersistentSecretMarkDelta.Add(Mark(FirstSecret)));

		var refresh = session.RefreshPersistentMarksAsync(projectA, TestContext.Current.CancellationToken).AsTask();
		await store.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
		session.ReplacePersistentMarks(projectB, new PersistentSecretMarksSnapshot(9, [projectBMark]));
		_ = session.BeginOutput(projectB, Array.Empty<string>());
		store.Release.TrySetResult();
		await refresh;

		Assert.Equal(projectBMark, Assert.Single(session.GetMarkedSecrets()));
	}

	[Fact]
	public void MalformedOrFutureDelta_IsRejectedBeforeItCanEnterTheEffectiveOverlay()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		using var session = new SecretRedactionSession(new EmptyDetector());
		session.ReplacePersistentMarks(project, new PersistentSecretMarksSnapshot(4, []));
		var valid = PersistentSecretMarkDelta.Add(Mark(FirstSecret), observedRevision: 4);

		Assert.Throws<ArgumentException>(() =>
			session.StagePersistentMarkDelta(project, valid with { IssuedUtcTicks = 0 }));
		Assert.Throws<ArgumentException>(() =>
			session.StagePersistentMarkDelta(project, valid with { ObservedRevision = -1 }));
		Assert.Throws<ArgumentException>(() =>
			session.StagePersistentMarkDelta(project, valid with { ObservedRevision = 5 }));
		Assert.Empty(session.GetMarkedSecrets());
	}

	private static string Redact(SecretRedactionSession session, string project, string path)
	{
		var scope = session.BeginOutput(project, [path]);
		var output = scope.Redact(path, File.ReadAllText(path), TestContext.Current.CancellationToken).Text;
		scope.Complete();
		return output;
	}

	private static MarkedSecretProfileEntry Mark(string secret)
	{
		var value = Value(secret);
		return new MarkedSecretProfileEntry(value.Hash, "TOKEN", value.Length);
	}

	private static MarkedSecretValue Value(string secret)
	{
		Assert.True(MarkedSecretValueNormalizer.TryCreate(secret, out var value, out _));
		return value;
	}

	private sealed class EmptyDetector : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) => [];
	}

	private sealed class MutableMarkStore(PersistentSecretMarksSnapshot snapshot) : IPersistentSecretMarkStore
	{
		public PersistentSecretMarksSnapshot Snapshot { get; set; } = snapshot;

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

		public ValueTask<PersistentSecretMarkWriteResult> ApplyMarkDeltaAsync(
			string localProjectPath,
			PersistentSecretMarkDelta delta,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class BlockingLoadStore(PersistentSecretMarksSnapshot snapshot) : IPersistentSecretMarkStore
	{
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async ValueTask<PersistentSecretMarksLoadResult> LoadMarksAsync(
			string localProjectPath,
			CancellationToken cancellationToken = default)
		{
			Started.TrySetResult();
			await Release.Task.WaitAsync(cancellationToken);
			return new PersistentSecretMarksLoadResult(PersistentSecretMarkStoreStatus.Success, snapshot);
		}

		public ValueTask<PersistentSecretMarkWriteResult> AddMarkAsync(
			string localProjectPath,
			MarkedSecretProfileEntry mark,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public ValueTask<PersistentSecretMarkWriteResult> RemoveMarkAsync(
			string localProjectPath,
			PersistentSecretMarkId markId,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public ValueTask<PersistentSecretMarkWriteResult> ApplyMarkDeltaAsync(
			string localProjectPath,
			PersistentSecretMarkDelta delta,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}
}
