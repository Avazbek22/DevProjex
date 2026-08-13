using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Secrets;

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

		Assert.True(session.StagePersistentMarkDelta(project, delta).Staged);
		await session.RefreshPersistentMarksAsync(project, TestContext.Current.CancellationToken);
		await session.RefreshPersistentMarksAsync(project, TestContext.Current.CancellationToken);

		Assert.Equal(mark, Assert.Single(session.GetMarkedSecrets()));
		Assert.DoesNotContain(FirstSecret, Redact(session, project, path), StringComparison.Ordinal);
	}

	[Fact]
	public async Task PendingSourceAdd_SurvivesRefreshAndRedactsOnlyItsAnchoredOccurrence()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var appData = workspace.CreateFolder("app-data");
		var content = $"FIRST={FirstSecret}\nSECOND={FirstSecret}\n";
		var path = workspace.CreateFile("project/config.env", content);
		var store = new MutableMarkStore(new PersistentSecretMarksSnapshot(4, []));
		using var identityProvider = new PersistentSecretIdentityProvider(() => appData);
		Assert.Equal(
			PersistentSecretIdentityAvailability.Ready,
			await identityProvider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
		Assert.True(PersistentSecretIdentity.TryCreateV2(
			identityProvider,
			FirstSecret,
			out var identity));
		var mark = new MarkedSecretProfileEntry(
			identity,
			"FIRST",
			FirstSecret.Length,
			"config.env",
			content.IndexOf(FirstSecret, StringComparison.Ordinal));
		using var session = new SecretRedactionSession(new EmptyDetector(), store, identityProvider);
		session.ReplacePersistentMarks(project, store.Snapshot);

		Assert.True(session.StagePersistentMarkDelta(
			project,
			PersistentSecretMarkDelta.Add(mark, observedRevision: 4)).Staged);
		await session.RefreshPersistentMarksAsync(project, TestContext.Current.CancellationToken);
		var output = Redact(session, project, path);

		Assert.Equal(1, CountOccurrences(output, FirstSecret));
		Assert.Equal(1, CountOccurrences(output, "DEVPROJEX_REDACTED[manual-secret#1]"));
	}

	[Fact]
	public async Task PromotedSourceMark_StaleSessionIdResolvesUntilPersistentRemovalIsAcknowledged()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var appData = workspace.CreateFolder("app-data");
		var content = $"TOKEN={FirstSecret}\n";
		var sourceOffset = content.IndexOf(FirstSecret, StringComparison.Ordinal);
		var value = Value(FirstSecret);
		var anchor = new SessionMarkedSecret("config.env", sourceOffset, value.Length, value.Hash);
		using var identityProvider = new PersistentSecretIdentityProvider(() => appData);
		Assert.Equal(
			PersistentSecretIdentityAvailability.Ready,
			await identityProvider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
		Assert.True(PersistentSecretIdentity.TryCreateV2(
			identityProvider,
			FirstSecret,
			out var identity));
		var mark = new MarkedSecretProfileEntry(
			identity,
			"TOKEN",
			value.Length,
			"config.env",
			sourceOffset);
		var markId = new PersistentSecretMarkId(identity, value.Length, "config.env", sourceOffset);
		using var session = new SecretRedactionSession(new EmptyDetector(), persistentIdentityProvider: identityProvider);
		session.ReplacePersistentMarks(project, PersistentSecretMarksSnapshot.Empty);
		Assert.True(session.AddSessionMarkedSecret("config.env", sourceOffset, value));
		var add = PersistentSecretMarkDelta.Add(mark);

		Assert.True(session.TryPromoteSessionMarkToPendingPersistentMark(
			project,
			"config.env",
			sourceOffset,
			value,
			add).Staged);
		session.AcknowledgePersistentMarkDelta(
			project,
			add.OperationId,
			Snapshot(1, [mark], (markId, 1)));

		Assert.True(session.TryResolvePromotedPersistentMarkId(anchor.Id, out var resolvedMarkId));
		Assert.Equal(markId, resolvedMarkId);

		var remove = PersistentSecretMarkDelta.Remove(markId, observedRevision: 1);
		Assert.True(session.StagePersistentMarkDelta(project, remove).Staged);
		session.AcknowledgePersistentMarkDelta(
			project,
			remove.OperationId,
			Snapshot(2, [], (markId, 2)));

		Assert.False(session.TryResolvePromotedPersistentMarkId(anchor.Id, out _));
		Assert.Equal(0, session.PendingPersistentMarkCount);
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

		Assert.True(session.StagePersistentMarkDelta(project, firstDelta).Staged);
		Assert.True(session.StagePersistentMarkDelta(project, secondDelta).Staged);
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

		Assert.True(session.StagePersistentMarkDelta(project, remove).Staged);
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

		Assert.True(session.StagePersistentMarkDelta(project, replace).Staged);
		await session.RefreshPersistentMarksAsync(project, TestContext.Current.CancellationToken);
		var output = Redact(session, project, path);

		Assert.Contains(FirstSecret, output, StringComparison.Ordinal);
		Assert.DoesNotContain(SecondSecret, output, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void RemoveStagedAfterDurableDisappeared_IsStillCompletedAndLeavesNoPhantom(
		bool writeSucceeded)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var mark = Mark(FirstSecret);
		using var session = new SecretRedactionSession(new EmptyDetector());
		session.ReplacePersistentMarks(project, new PersistentSecretMarksSnapshot(1, [mark]));
		var remove = PersistentSecretMarkDelta.Remove(
			new PersistentSecretMarkId(mark.H, mark.Length),
			observedRevision: 2);
		session.ReplacePersistentMarks(project, new PersistentSecretMarksSnapshot(2, []));

		var staged = session.StagePersistentMarkDelta(project, remove);

		Assert.True(staged.Staged);
		Assert.False(staged.EffectiveChanged);
		Assert.Equal(1, session.PendingPersistentMarkCount);
		if (writeSucceeded)
		{
			session.AcknowledgePersistentMarkDelta(
				project,
				remove.OperationId,
				new PersistentSecretMarksSnapshot(3, []));
			session.ReplacePersistentMarks(project, new PersistentSecretMarksSnapshot(2, [mark]));
		}
		else
		{
			Assert.False(session.RollbackPendingPersistentMarkDelta(project, remove.OperationId));
			session.ReplacePersistentMarks(project, new PersistentSecretMarksSnapshot(1, [mark]));
		}

		Assert.Equal(0, session.PendingPersistentMarkCount);
		Assert.Empty(session.GetMarkedSecrets());
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
	public void PendingRemove_IsDiscardedWhenDurableStateHasAdvanced()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var path = workspace.CreateFile("project/config.env", $"TOKEN={FirstSecret}\n");
		var mark = Mark(FirstSecret);
		var identity = new PersistentSecretMarkId(mark.H, mark.Length);
		using var session = new SecretRedactionSession(new EmptyDetector());
		session.ReplacePersistentMarks(project, Snapshot(10, [mark], (identity, 10)));
		var remove = PersistentSecretMarkDelta.Remove(identity, observedRevision: 10);

		Assert.True(session.StagePersistentMarkDelta(project, remove).EffectiveChanged);
		Assert.Contains(FirstSecret, Redact(session, project, path), StringComparison.Ordinal);

		session.ReplacePersistentMarks(project, Snapshot(11, [mark], (identity, 11)));

		Assert.Equal(0, session.PendingPersistentMarkCount);
		Assert.DoesNotContain(FirstSecret, Redact(session, project, path), StringComparison.Ordinal);
		session.AcknowledgePersistentMarkDelta(project, remove.OperationId, Snapshot(11, [mark], (identity, 11)));
		Assert.Equal(0, session.PendingPersistentMarkCount);
		Assert.DoesNotContain(FirstSecret, Redact(session, project, path), StringComparison.Ordinal);
	}

	[Fact]
	public void SameSnapshotRevision_WithNewerPerStateRevisionReevaluatesPendingOverlay()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var path = workspace.CreateFile("project/config.env", $"TOKEN={FirstSecret}\n");
		var mark = Mark(FirstSecret);
		var identity = new PersistentSecretMarkId(mark.H, mark.Length);
		using var session = new SecretRedactionSession(new EmptyDetector());
		session.ReplacePersistentMarks(project, new PersistentSecretMarksSnapshot(11, [mark]));
		var remove = PersistentSecretMarkDelta.Remove(identity, observedRevision: 10);
		Assert.True(session.StagePersistentMarkDelta(project, remove).EffectiveChanged);

		session.ReplacePersistentMarks(project, Snapshot(11, [mark], (identity, 11)));

		Assert.Equal(0, session.PendingPersistentMarkCount);
		Assert.DoesNotContain(FirstSecret, Redact(session, project, path), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void PendingReplace_IsDiscardedWhenEitherDurableStateHasAdvanced(bool targetAdvanced)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var path = workspace.CreateFile(
			"project/config.env",
			$"OLD={FirstSecret}\nNEW={SecondSecret}\n");
		var oldMark = Mark(FirstSecret);
		var newMark = Mark(SecondSecret);
		var oldIdentity = new PersistentSecretMarkId(oldMark.H, oldMark.Length);
		var newIdentity = new PersistentSecretMarkId(newMark.H, newMark.Length);
		using var session = new SecretRedactionSession(new EmptyDetector());
		session.ReplacePersistentMarks(project, Snapshot(
			10,
			[oldMark],
			(oldIdentity, 10),
			(newIdentity, 10)));
		var replace = PersistentSecretMarkDelta.Replace(oldIdentity, newMark, observedRevision: 10);
		Assert.True(session.StagePersistentMarkDelta(project, replace).EffectiveChanged);

		var refreshedRevisions = targetAdvanced
			? new[] { (oldIdentity, 10L), (newIdentity, 11L) }
			: new[] { (oldIdentity, 11L), (newIdentity, 10L) };
		session.ReplacePersistentMarks(
			project,
			new PersistentSecretMarksSnapshot(
				11,
				[oldMark],
				refreshedRevisions.ToDictionary(static item => item.Item1, static item => item.Item2)));

		var output = Redact(session, project, path);
		Assert.Equal(0, session.PendingPersistentMarkCount);
		Assert.DoesNotContain(FirstSecret, output, StringComparison.Ordinal);
		Assert.Contains(SecondSecret, output, StringComparison.Ordinal);
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

	private static PersistentSecretMarksSnapshot Snapshot(
		long revision,
		IReadOnlyCollection<MarkedSecretProfileEntry> marks,
		params (PersistentSecretMarkId Identity, long AppliedRevision)[] stateRevisions) =>
		new(
			revision,
			marks,
			stateRevisions.ToDictionary(
				static state => state.Identity,
				static state => state.AppliedRevision));

	private static MarkedSecretValue Value(string secret)
	{
		Assert.True(MarkedSecretValueNormalizer.TryCreate(secret, out var value, out _));
		return value;
	}

	private static int CountOccurrences(string content, string value)
	{
		var count = 0;
		var offset = 0;
		while ((offset = content.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
		{
			count++;
			offset += value.Length;
		}
		return count;
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
