using System.Security.Cryptography;
using DevProjex.Application.Compression;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class PersistentSecretIdentityTests
{
	private const string Secret = "persistent-secret-value-012345";

	[Fact]
	public void V2Identity_IsDeterministicFullHmacAndMatcherFindsIt()
	{
		var provider = new TestIdentityProvider();
		Assert.True(PersistentSecretIdentity.TryCreateV2(provider, Secret, out var first));
		Assert.True(PersistentSecretIdentity.TryCreateV2(provider, Secret, out var second));
		Assert.Equal(first, second);
		Assert.True(PersistentSecretIdentity.IsV2(first));
		Assert.Equal(PersistentSecretIdentity.V2IdentifierLength, first.Length);
		var mark = new MarkedSecretProfileEntry(first, "TOKEN", Secret.Length);
		var matcher = new MarkedSecretsMatcher([mark], [], provider);

		var finding = Assert.Single(matcher.Match(
			"config.env",
			$"TOKEN={Secret}",
			TestContext.Current.CancellationToken));

		Assert.Equal(first, finding.PersistentMarkHash);
	}

	[Fact]
	public void V2SourceBoundMark_MatchesOnlyExactOccurrenceAcrossTransformMap()
	{
		const string relativePath = "src/config.cs";
		var source = $"// shift me\nFIRST={Secret}\nSECOND={Secret}\n";
		var sourceOffset = source.IndexOf(Secret, StringComparison.Ordinal);
		var provider = new TestIdentityProvider();
		Assert.True(PersistentSecretIdentity.TryCreateV2(provider, Secret, out var identity));
		var mark = new MarkedSecretProfileEntry(
			identity,
			"FIRST",
			Secret.Length,
			relativePath,
			sourceOffset);
		var matcher = new MarkedSecretsMatcher([mark], [], provider);

		var sourceFinding = Assert.Single(matcher.Match(
			relativePath,
			source,
			TestContext.Current.CancellationToken));
		Assert.Equal(sourceOffset, sourceFinding.Start);
		Assert.Equal(
			new PersistentSecretMarkId(identity, Secret.Length, relativePath, sourceOffset),
			sourceFinding.PersistentMarkId);
		Assert.Empty(matcher.Match(
			"src/other.cs",
			source,
			TestContext.Current.CancellationToken));

		var removedPrefixLength = "// shift me\n".Length;
		var plan = CodeCompressionPlan.Create(
			relativePath,
			"csharp",
			[new CodeCompressionEdit(0, removedPrefixLength, string.Empty)],
			source.Length,
			"source-bound-test");
		var transformed = plan.Apply(source);
		var transformedFinding = Assert.Single(matcher.Match(
			relativePath,
			transformed.Text,
			transformed.Map,
			TestContext.Current.CancellationToken));
		Assert.Equal(sourceOffset - removedPrefixLength, transformedFinding.Start);

		var changed = source.Remove(sourceOffset, Secret.Length).Insert(sourceOffset, new string('x', Secret.Length));
		Assert.Empty(matcher.Match(relativePath, changed, TestContext.Current.CancellationToken));
	}

	[Fact]
	public void V2SourceBoundMark_UsesUtf16SourceCoordinatesWithoutSplittingUnicode()
	{
		const string relativePath = "src/unicode.txt";
		const string secret = "пароль-🔐-42";
		var source = $"😀 prefix\nfirst={secret}\nsecond={secret}\n";
		var sourceOffset = source.IndexOf(secret, StringComparison.Ordinal);
		var provider = new TestIdentityProvider();
		Assert.True(PersistentSecretIdentity.TryCreateV2(provider, secret, out var identity));
		var matcher = new MarkedSecretsMatcher(
			[
				new MarkedSecretProfileEntry(
					identity,
					"first",
					secret.Length,
					relativePath,
					sourceOffset)
			],
			[],
			provider);

		var finding = Assert.Single(matcher.Match(
			relativePath,
			source,
			TestContext.Current.CancellationToken));

		Assert.Equal(sourceOffset, finding.Start);
		Assert.Equal(secret.Length, finding.Length);
		Assert.Equal(secret, source.Substring(finding.Start, finding.Length));
	}

	[Fact]
	public void V2Mark_WithUnavailableInstallationKeyFailsClosed()
	{
		var available = new TestIdentityProvider();
		Assert.True(PersistentSecretIdentity.TryCreateV2(available, Secret, out var identity));
		var mark = new MarkedSecretProfileEntry(identity, null, Secret.Length);

		Assert.Throws<SecretDetectionException>(() =>
			new MarkedSecretsMatcher([mark], [], new UnavailableIdentityProvider()));
	}

	[Fact]
	public async Task IdentityReadiness_IgnoresPersistentMarksFromDisabledClass()
	{
		var available = new TestIdentityProvider();
		Assert.True(PersistentSecretIdentity.TryCreateV2(available, Secret, out var identity));
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("config.txt", "ordinary content");
		using var session = SecretRedactionSession.CreateWithPrivateData(
			new EmptyDetector(),
			new EmptyDetector(),
			persistentIdentityProvider: new UnavailableIdentityProvider());
		session.ReplaceMarkedSecrets([
			new MarkedSecretProfileEntry(
				identity,
				null,
				Secret.Length,
				Class: ManualRedactionClass.PrivateData)
		]);

		Assert.Equal(
			PersistentSecretIdentityAvailability.Ready,
			await session.EnsureCurrentPersistentIdentityReadyAsync(
				SecretRedactionFeatures.Secrets,
				TestContext.Current.CancellationToken));
		Assert.Equal(
			PersistentSecretIdentityAvailability.PermanentlyUnavailable,
			await session.EnsureCurrentPersistentIdentityReadyAsync(
				SecretRedactionFeatures.PrivateData,
				TestContext.Current.CancellationToken));

		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());
		var secretsOnly = await preparer.AnalyzeAsync(
			new SecretRedactionContext(
				workspace.Path,
				session,
				SecretRedactionFeatures.Secrets),
			[path],
			TestContext.Current.CancellationToken);
		Assert.Equal(0, secretsOnly.DetectedCount);
		await Assert.ThrowsAsync<SecretDetectionException>(() => preparer.AnalyzeAsync(
			new SecretRedactionContext(
				workspace.Path,
				session,
				SecretRedactionFeatures.PrivateData),
			[path],
			TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task LegacyMatch_IsAtomicallyMigratedToV2Once()
	{
		using var workspace = new TemporaryDirectory();
		var appData = workspace.CreateFolder("app-data");
		var project = workspace.CreateFolder("project");
		var path = workspace.CreateFile("project/config.env", $"TOKEN={Secret}\n");
		var store = new ProjectProfileStore(() => appData);
		var legacy = new MarkedSecretProfileEntry(
			MarkedSecretValueNormalizer.ComputeHash(Secret),
			"TOKEN",
			Secret.Length);
		Assert.True((await store.AddMarkAsync(
			project,
			legacy,
			TestContext.Current.CancellationToken)).Succeeded);
		using var identityProvider = new PersistentSecretIdentityProvider(() => appData);
		using var session = new SecretRedactionSession(
			new EmptyDetector(),
			store,
			identityProvider);
		var loaded = await store.LoadMarksAsync(project, TestContext.Current.CancellationToken);
		session.ReplacePersistentMarks(project, loaded.Snapshot!);
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());

		var snapshot = await preparer.AnalyzeAsync(
			new SecretRedactionContext(project, session),
			[path],
			TestContext.Current.CancellationToken);
		var migrated = await store.LoadMarksAsync(project, TestContext.Current.CancellationToken);

		Assert.Equal(1, snapshot.RedactedCount);
		var v2 = Assert.Single(migrated.Snapshot!.Marks);
		Assert.True(PersistentSecretIdentity.IsV2(v2.H));
		Assert.Equal(legacy.Key, v2.Key);
		Assert.Equal(legacy.Length, v2.Length);
		var revision = migrated.Snapshot.Revision;

		await preparer.AnalyzeAsync(
			new SecretRedactionContext(project, session),
			[path],
			TestContext.Current.CancellationToken);
		var repeated = await store.LoadMarksAsync(project, TestContext.Current.CancellationToken);
		Assert.Equal(revision, repeated.Snapshot!.Revision);
	}

	[Fact]
	public async Task LegacyMigrationCompletingAfterProjectSwitch_DoesNotRebindOldProjectMarks()
	{
		using var workspace = new TemporaryDirectory();
		var projectA = workspace.CreateFolder("project-a");
		var projectB = workspace.CreateFolder("project-b");
		var pathA = workspace.CreateFile("project-a/config.env", $"TOKEN={Secret}\n");
		var legacy = new MarkedSecretProfileEntry(
			MarkedSecretValueNormalizer.ComputeHash(Secret),
			"TOKEN",
			Secret.Length);
		var provider = new TestIdentityProvider();
		Assert.True(PersistentSecretIdentity.TryCreateV2(provider, Secret, out var migratedIdentity));
		var migrated = legacy with { H = migratedIdentity };
		var store = new BlockingMarkStore(new PersistentSecretMarksSnapshot(2, [migrated]));
		using var session = new SecretRedactionSession(new EmptyDetector(), store, provider);
		session.ReplacePersistentMarks(projectA, new PersistentSecretMarksSnapshot(1, [legacy]));
		var scopeA = session.BeginOutput(projectA, [pathA]);
		_ = scopeA.Redact(pathA, File.ReadAllText(pathA), TestContext.Current.CancellationToken);

		var flush = session
			.FlushPendingPersistentMarkMigrationsAsync(projectA, TestContext.Current.CancellationToken)
			.AsTask();
		await store.ApplyStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
		var projectBMark = migrated with { H = "v2:" + new string('b', 64), Key = "PROJECT-B" };
		session.ReplacePersistentMarks(projectB, new PersistentSecretMarksSnapshot(7, [projectBMark]));
		_ = session.BeginOutput(projectB, Array.Empty<string>());
		store.AllowApplyToComplete.TrySetResult();
		await flush;

		Assert.Equal(projectBMark, Assert.Single(session.GetMarkedSecrets()));
	}

	[Fact]
	public async Task PersistentMarkRefreshCompletingAfterDispose_DoesNotRepopulateSession()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var mark = new MarkedSecretProfileEntry(
			"v2:" + new string('a', 64),
			"TOKEN",
			Secret.Length);
		var store = new BlockingLoadMarkStore(new PersistentSecretMarksSnapshot(2, [mark]));
		var session = new SecretRedactionSession(new EmptyDetector(), store, new TestIdentityProvider());
		session.ReplacePersistentMarks(project, new PersistentSecretMarksSnapshot(1, []));

		var refresh = session
			.RefreshPersistentMarksAsync(project, TestContext.Current.CancellationToken)
			.AsTask();
		await store.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
		session.Dispose();
		store.AllowLoadToComplete.TrySetResult();

		await Assert.ThrowsAsync<ObjectDisposedException>(() => refresh);
		Assert.Empty(session.GetMarkedSecrets());
	}

	[Fact]
	public async Task PersistentMarkRefreshCompletingAfterProjectSwitch_DoesNotRepopulateTheNewProject()
	{
		using var workspace = new TemporaryDirectory();
		var projectA = workspace.CreateFolder("project-a");
		var projectB = workspace.CreateFolder("project-b");
		var projectAMark = new MarkedSecretProfileEntry(
			"v2:" + new string('a', 64),
			"PROJECT-A",
			Secret.Length);
		var projectBMark = new MarkedSecretProfileEntry(
			"v2:" + new string('b', 64),
			"PROJECT-B",
			Secret.Length);
		var store = new BlockingLoadMarkStore(new PersistentSecretMarksSnapshot(2, [projectAMark]));
		using var session = new SecretRedactionSession(new EmptyDetector(), store, new TestIdentityProvider());
		session.ReplacePersistentMarks(projectA, new PersistentSecretMarksSnapshot(1, []));

		var refresh = session
			.RefreshPersistentMarksAsync(projectA, TestContext.Current.CancellationToken)
			.AsTask();
		await store.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
		session.ReplacePersistentMarks(projectB, new PersistentSecretMarksSnapshot(7, [projectBMark]));
		_ = session.BeginOutput(projectB, Array.Empty<string>());
		store.AllowLoadToComplete.TrySetResult();
		await refresh;

		Assert.Equal(projectBMark, Assert.Single(session.GetMarkedSecrets()));
	}

	[Fact]
	public async Task InstallationKey_IsReusedAndPrimaryCorruptionRecoversFromBackup()
	{
		using var workspace = new TemporaryDirectory();
		var appData = workspace.CreateFolder("app-data");
		string first;
		using (var provider = new PersistentSecretIdentityProvider(() => appData))
		{
			Assert.Equal(PersistentSecretIdentityAvailability.Ready, await provider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
			Assert.True(PersistentSecretIdentity.TryCreateV2(provider, Secret, out first));
		}
		using (var provider = new PersistentSecretIdentityProvider(() => appData))
		{
			Assert.Equal(PersistentSecretIdentityAvailability.Ready, await provider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
			Assert.True(PersistentSecretIdentity.TryCreateV2(provider, Secret, out var second));
			Assert.Equal(first, second);
		}

		var keyPath = Path.Combine(appData, "DevProjex", "secret-mark-hmac.key");
		File.WriteAllBytes(keyPath, [1, 2, 3]);
		using var recovered = new PersistentSecretIdentityProvider(() => appData);
		Assert.Equal(PersistentSecretIdentityAvailability.Ready, await recovered.EnsureAvailableAsync(TestContext.Current.CancellationToken));
		Assert.True(PersistentSecretIdentity.TryCreateV2(recovered, Secret, out var afterRecovery));
		Assert.Equal(first, afterRecovery);
	}

	[Fact]
	public async Task KeyFileCreateReadAndRecovery_ClearEveryReleasedSensitiveBuffer()
	{
		using var workspace = new TemporaryDirectory();
		var appData = workspace.CreateFolder("app-data");
		var cleared = new System.Collections.Concurrent.ConcurrentBag<byte[]>();
		using (var created = new PersistentSecretIdentityProvider(
			       () => appData,
			       lockTimeout: null,
			       TimeSpan.Zero,
			       sensitiveBufferClearedObserver: cleared.Add))
		{
			Assert.Equal(
				PersistentSecretIdentityAvailability.Ready,
				await created.EnsureAvailableAsync(TestContext.Current.CancellationToken));
		}

		var keyPath = Path.Combine(appData, "DevProjex", "secret-mark-hmac.key");
		File.WriteAllBytes(keyPath, [1, 2, 3]);
		using (var recovered = new PersistentSecretIdentityProvider(
			       () => appData,
			       lockTimeout: null,
			       TimeSpan.Zero,
			       sensitiveBufferClearedObserver: cleared.Add))
		{
			Assert.Equal(
				PersistentSecretIdentityAvailability.Ready,
				await recovered.EnsureAvailableAsync(TestContext.Current.CancellationToken));
		}

		Assert.NotEmpty(cleared);
		Assert.Contains(cleared, static buffer => buffer.Length == 32);
		Assert.Contains(cleared, static buffer => buffer.Length > 32);
		Assert.All(cleared, static buffer =>
			Assert.All(buffer, static value => Assert.Equal(0, value)));
	}

	[Fact]
	public async Task BothInstallationKeyCopiesCorrupt_FailsClosedWithoutGeneratingAReplacement()
	{
		using var workspace = new TemporaryDirectory();
		var appData = workspace.CreateFolder("app-data");
		using (var provider = new PersistentSecretIdentityProvider(() => appData))
			Assert.Equal(PersistentSecretIdentityAvailability.Ready, await provider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
		var keyPath = Path.Combine(appData, "DevProjex", "secret-mark-hmac.key");
		var backupPath = keyPath + ".bak";
		File.WriteAllBytes(keyPath, [1, 2, 3]);
		File.WriteAllBytes(backupPath, [4, 5, 6]);
		var primaryBefore = File.ReadAllBytes(keyPath);
		var backupBefore = File.ReadAllBytes(backupPath);

		using var corrupted = new PersistentSecretIdentityProvider(() => appData);
		Assert.Equal(PersistentSecretIdentityAvailability.PermanentlyUnavailable, await corrupted.EnsureAvailableAsync(TestContext.Current.CancellationToken));
		Assert.Equal(PersistentSecretIdentityProviderState.PermanentFault, corrupted.State);
		Assert.False(PersistentSecretIdentity.TryCreateV2(corrupted, Secret, out _));
		Assert.Equal(primaryBefore, File.ReadAllBytes(keyPath));
		Assert.Equal(backupBefore, File.ReadAllBytes(backupPath));
	}

	[Fact]
	public void StableLengthProbe_RejectsKeyFileThatGrewAfterOpening()
	{
		using var stream = new StaleLengthMemoryStream(
			[1, 2, 3, 4, 5],
			reportedLength: 4);
		stream.Position = 4;

		var stable = PersistentSecretIdentityProvider.HasStableLengthAfterRead(
			stream,
			expectedLength: 4);

		Assert.False(stable);
		Assert.Equal(5, stream.Position);
	}

	[Fact]
	public async Task Provider_DoesNotTouchStorageUntilAnIdentityIsRequired()
	{
		using var workspace = new TemporaryDirectory();
		var appData = Path.Combine(workspace.Path, "app-data");
		var keyPath = Path.Combine(appData, "DevProjex", "secret-mark-hmac.key");

		using var provider = new PersistentSecretIdentityProvider(() => appData);

		Assert.False(File.Exists(keyPath));
		Assert.False(File.Exists(keyPath + ".lock"));
		Assert.Equal(PersistentSecretIdentityAvailability.Ready, await provider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
		Assert.True(PersistentSecretIdentity.TryCreateV2(provider, Secret, out _));
		Assert.True(File.Exists(keyPath));
	}

	[Fact]
	public async Task UnixInstallationKey_WithSharedPrimaryPermissionsRecoversFromBackup()
	{
		if (OperatingSystem.IsWindows())
			return;

		using var workspace = new TemporaryDirectory();
		var appData = workspace.CreateFolder("app-data");
		using (var provider = new PersistentSecretIdentityProvider(() => appData))
			Assert.Equal(PersistentSecretIdentityAvailability.Ready, await provider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
		var keyPath = Path.Combine(appData, "DevProjex", "secret-mark-hmac.key");
		File.SetUnixFileMode(
			keyPath,
			UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

		using var reopened = new PersistentSecretIdentityProvider(() => appData);

		Assert.Equal(PersistentSecretIdentityAvailability.Ready, await reopened.EnsureAvailableAsync(TestContext.Current.CancellationToken));
		Assert.Equal(PersistentSecretIdentityProviderState.Ready, reopened.State);
		Assert.True(PersistentSecretIdentity.TryCreateV2(reopened, Secret, out _));
		Assert.Equal(
			UnixFileMode.UserRead | UnixFileMode.UserWrite,
			File.GetUnixFileMode(keyPath));
	}

	[Fact]
	public async Task UnixInstallationKey_WithSharedPermissionsOnBothCopiesIsRejected()
	{
		if (OperatingSystem.IsWindows())
			return;

		using var workspace = new TemporaryDirectory();
		var appData = workspace.CreateFolder("app-data");
		using (var provider = new PersistentSecretIdentityProvider(() => appData))
			Assert.Equal(PersistentSecretIdentityAvailability.Ready, await provider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
		var keyPath = Path.Combine(appData, "DevProjex", "secret-mark-hmac.key");
		var sharedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
		File.SetUnixFileMode(keyPath, sharedMode);
		File.SetUnixFileMode(keyPath + ".bak", sharedMode);

		using var reopened = new PersistentSecretIdentityProvider(() => appData);

		Assert.Equal(PersistentSecretIdentityAvailability.PermanentlyUnavailable, await reopened.EnsureAvailableAsync(TestContext.Current.CancellationToken));
		Assert.Equal(PersistentSecretIdentityProviderState.PermanentFault, reopened.State);
	}

	[Fact]
	public async Task TransientLockFailure_RetriesAndReadyStateIsCached()
	{
		using var workspace = new TemporaryDirectory();
		var appData = workspace.CreateFolder("app-data");
		var directory = workspace.CreateFolder("app-data/DevProjex");
		var lockPath = Path.Combine(directory, "secret-mark-hmac.key.lock");
		using var provider = new PersistentSecretIdentityProvider(
			() => appData,
			TimeSpan.FromMilliseconds(30),
			TimeSpan.Zero);

		using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
		{
			Assert.Equal(PersistentSecretIdentityAvailability.TemporarilyUnavailable, await provider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
			Assert.Equal(PersistentSecretIdentityProviderState.TransientFault, provider.State);
		}
		Assert.Equal(PersistentSecretIdentityAvailability.Ready, await provider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
		Assert.Equal(PersistentSecretIdentityProviderState.Ready, provider.State);
		Assert.Equal(2, provider.InitializationAttemptCount);
		Assert.Equal(PersistentSecretIdentityAvailability.Ready, await provider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
		Assert.Equal(2, provider.InitializationAttemptCount);
	}

	[Fact]
	public async Task UnexpectedNonFatalInitializationFailure_ExitsInitializingAndCanRetry()
	{
		using var workspace = new TemporaryDirectory();
		var appData = workspace.CreateFolder("app-data");
		var pathAvailable = false;
		using var provider = new PersistentSecretIdentityProvider(
			() => pathAvailable
				? appData
				: throw new System.Security.SecurityException("ACL lookup is temporarily unavailable."),
			TimeSpan.FromMilliseconds(30),
			TimeSpan.Zero);

		var first = await provider.EnsureAvailableAsync(TestContext.Current.CancellationToken);

		Assert.Equal(PersistentSecretIdentityAvailability.TemporarilyUnavailable, first);
		Assert.Equal(PersistentSecretIdentityProviderState.TransientFault, provider.State);
		pathAvailable = true;
		Assert.Equal(
			PersistentSecretIdentityAvailability.Ready,
			await provider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
		Assert.Equal(PersistentSecretIdentityProviderState.Ready, provider.State);
		Assert.Equal(2, provider.InitializationAttemptCount);
	}

	[Fact]
	public async Task TransientRetryCooldown_UsesMonotonicTimeAcrossUtcClockAdjustments()
	{
		using var workspace = new TemporaryDirectory();
		var appData = workspace.CreateFolder("app-data");
		var pathAvailable = false;
		var timeProvider = new IndependentTimeProvider(
			new DateTimeOffset(2026, 8, 20, 3, 0, 0, TimeSpan.Zero));
		using var provider = new PersistentSecretIdentityProvider(
			() => pathAvailable
				? appData
				: throw new System.Security.SecurityException("Storage is temporarily unavailable."),
			TimeSpan.FromMilliseconds(30),
			TimeSpan.FromSeconds(1),
			timeProvider);

		Assert.Equal(
			PersistentSecretIdentityAvailability.TemporarilyUnavailable,
			await provider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
		pathAvailable = true;
		timeProvider.MoveUtcForward(TimeSpan.FromDays(1));
		Assert.Equal(
			PersistentSecretIdentityAvailability.TemporarilyUnavailable,
			await provider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
		Assert.Equal(1, provider.InitializationAttemptCount);
		timeProvider.MoveUtcBackward(TimeSpan.FromDays(2));
		timeProvider.AdvanceMonotonic(TimeSpan.FromSeconds(2));

		Assert.Equal(
			PersistentSecretIdentityAvailability.Ready,
			await provider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
		Assert.Equal(2, provider.InitializationAttemptCount);
	}

	[Fact]
	public async Task ConcurrentFirstUse_PerformsOneInitialization()
	{
		using var workspace = new TemporaryDirectory();
		var appData = workspace.CreateFolder("app-data");
		using var provider = new PersistentSecretIdentityProvider(
			() => appData,
			TimeSpan.FromSeconds(1),
			TimeSpan.Zero);

		var callers = Enumerable.Range(0, 16)
			.Select(_ => provider.EnsureAvailableAsync(TestContext.Current.CancellationToken).AsTask())
			.ToArray();
		var results = await Task.WhenAll(callers);

		Assert.All(results, static result => Assert.Equal(PersistentSecretIdentityAvailability.Ready, result));
		Assert.Equal(1, provider.InitializationAttemptCount);
	}

	[Fact]
	public async Task ConcurrentDigestAndDispose_NeverObserveClearedKeyMaterial()
	{
		using var workspace = new TemporaryDirectory();
		var appData = workspace.CreateFolder("app-data");
		var provider = new PersistentSecretIdentityProvider(() => appData);
		Assert.Equal(
			PersistentSecretIdentityAvailability.Ready,
			await provider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
		var candidate = new string('s', MarkedSecretValueNormalizer.MaximumLength);
		var expected = new byte[PersistentSecretIdentity.V2DigestByteLength];
		Assert.True(provider.TryComputeDigest(candidate, expected));
		using var ready = new CountdownEvent(8);
		using var start = new ManualResetEventSlim();
		var mismatches = 0;
		var disposed = 0;
		var workers = Enumerable.Range(0, 8)
			.Select(_ => Task.Factory.StartNew(
				() => RunDigestUntilDisposed(
					provider,
					candidate,
					expected,
					ready,
					start,
					ref mismatches,
					ref disposed),
				CancellationToken.None,
				TaskCreationOptions.LongRunning,
				TaskScheduler.Default))
			.ToArray();
		Assert.True(ready.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
		start.Set();
		await Task.Delay(20, TestContext.Current.CancellationToken);

		provider.Dispose();
		await Task.WhenAll(workers).WaitAsync(TestContext.Current.CancellationToken);

		Assert.Equal(0, Volatile.Read(ref mismatches));
		Assert.Equal(workers.Length, Volatile.Read(ref disposed));
		Assert.Throws<ObjectDisposedException>(() =>
			provider.TryComputeDigest(candidate, new byte[PersistentSecretIdentity.V2DigestByteLength]));
	}

	[Fact]
	public async Task DisposeDuringInitialization_DoesNotPublishOrRetainTheKey()
	{
		using var workspace = new TemporaryDirectory();
		var appData = workspace.CreateFolder("app-data");
		var directory = workspace.CreateFolder("app-data/DevProjex");
		var lockPath = Path.Combine(directory, "secret-mark-hmac.key.lock");
		using var heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
		var provider = new PersistentSecretIdentityProvider(
			() => appData,
			TimeSpan.FromMilliseconds(100),
			TimeSpan.Zero);

		var initialization = provider.EnsureAvailableAsync(TestContext.Current.CancellationToken).AsTask();
		Assert.True(SpinWait.SpinUntil(
			() => provider.State == PersistentSecretIdentityProviderState.Initializing,
			TimeSpan.FromSeconds(1)));
		provider.Dispose();

		Assert.Equal(PersistentSecretIdentityAvailability.PermanentlyUnavailable, await initialization);
		Assert.Equal(PersistentSecretIdentityProviderState.Disposed, provider.State);
		Assert.Throws<ObjectDisposedException>(() =>
			provider.TryComputeDigest(Secret, new byte[PersistentSecretIdentity.V2DigestByteLength]));
	}

	private static void RunDigestUntilDisposed(
		PersistentSecretIdentityProvider provider,
		string candidate,
		ReadOnlySpan<byte> expected,
		CountdownEvent ready,
		ManualResetEventSlim start,
		ref int mismatches,
		ref int disposed)
	{
		ready.Signal();
		start.Wait();
		Span<byte> digest = stackalloc byte[PersistentSecretIdentity.V2DigestByteLength];
		while (true)
		{
			try
			{
				if (!provider.TryComputeDigest(candidate, digest) || !digest.SequenceEqual(expected))
					Interlocked.Increment(ref mismatches);
			}
			catch (ObjectDisposedException)
			{
				Interlocked.Increment(ref disposed);
				return;
			}
		}
	}

	private sealed class IndependentTimeProvider(DateTimeOffset utcNow) : TimeProvider
	{
		private DateTimeOffset _utcNow = utcNow;
		private long _timestamp;

		public override DateTimeOffset GetUtcNow() => _utcNow;

		public override long GetTimestamp() => _timestamp;

		public override long TimestampFrequency => TimeSpan.TicksPerSecond;

		public void MoveUtcForward(TimeSpan duration) => _utcNow += duration;

		public void MoveUtcBackward(TimeSpan duration) => _utcNow -= duration;

		public void AdvanceMonotonic(TimeSpan duration) => _timestamp += duration.Ticks;
	}

	private sealed class StaleLengthMemoryStream(byte[] buffer, long reportedLength) :
		MemoryStream(buffer, writable: false)
	{
		public override long Length => reportedLength;
	}

	[Fact]
	public async Task UnixRawKey_IsMigratedOnceToValidatedEnvelopeAndBackup()
	{
		if (OperatingSystem.IsWindows())
			return;
		using var workspace = new TemporaryDirectory();
		var appData = workspace.CreateFolder("app-data");
		var directory = workspace.CreateFolder("app-data/DevProjex");
		var keyPath = Path.Combine(directory, "secret-mark-hmac.key");
		File.WriteAllBytes(keyPath, Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray());
		File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

		using var provider = new PersistentSecretIdentityProvider(() => appData);
		Assert.Equal(PersistentSecretIdentityAvailability.Ready, await provider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
		var migrated = File.ReadAllBytes(keyPath);
		var backup = File.ReadAllBytes(keyPath + ".bak");

		Assert.True(migrated.Length > 32);
		Assert.Equal(migrated, backup);
		using var reopened = new PersistentSecretIdentityProvider(() => appData);
		Assert.Equal(PersistentSecretIdentityAvailability.Ready, await reopened.EnsureAvailableAsync(TestContext.Current.CancellationToken));
		Assert.Equal(1, reopened.InitializationAttemptCount);
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
		private static readonly byte[] Key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

		public bool IsAvailable => true;

		public bool TryComputeDigest(ReadOnlySpan<char> normalizedValue, Span<byte> destination)
		{
			var bytes = Encoding.UTF8.GetBytes(normalizedValue.ToString());
			HMACSHA256.HashData(Key, bytes, destination);
			return true;
		}
	}

	private sealed class UnavailableIdentityProvider : IPersistentSecretIdentityProvider
	{
		public bool IsAvailable => false;
		public bool TryComputeDigest(ReadOnlySpan<char> normalizedValue, Span<byte> destination) => false;
	}

	private sealed class BlockingMarkStore(PersistentSecretMarksSnapshot resultSnapshot) :
		IPersistentSecretMarkStore
	{
		public TaskCompletionSource ApplyStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource AllowApplyToComplete { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public ValueTask<PersistentSecretMarksLoadResult> LoadMarksAsync(
			string localProjectPath,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(new PersistentSecretMarksLoadResult(
				PersistentSecretMarkStoreStatus.Success,
				resultSnapshot));

		public ValueTask<PersistentSecretMarkWriteResult> AddMarkAsync(
			string localProjectPath,
			MarkedSecretProfileEntry mark,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<PersistentSecretMarkWriteResult> RemoveMarkAsync(
			string localProjectPath,
			PersistentSecretMarkId markId,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public async ValueTask<PersistentSecretMarkWriteResult> ApplyMarkDeltaAsync(
			string localProjectPath,
			PersistentSecretMarkDelta delta,
			CancellationToken cancellationToken = default)
		{
			ApplyStarted.TrySetResult();
			await AllowApplyToComplete.Task.WaitAsync(cancellationToken);
			return new PersistentSecretMarkWriteResult(
				PersistentSecretMarkStoreStatus.Success,
				resultSnapshot);
		}
	}

	private sealed class BlockingLoadMarkStore(PersistentSecretMarksSnapshot resultSnapshot) :
		IPersistentSecretMarkStore
	{
		public TaskCompletionSource LoadStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource AllowLoadToComplete { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async ValueTask<PersistentSecretMarksLoadResult> LoadMarksAsync(
			string localProjectPath,
			CancellationToken cancellationToken = default)
		{
			LoadStarted.TrySetResult();
			await AllowLoadToComplete.Task.WaitAsync(cancellationToken);
			return new PersistentSecretMarksLoadResult(
				PersistentSecretMarkStoreStatus.Success,
				resultSnapshot);
		}

		public ValueTask<PersistentSecretMarkWriteResult> AddMarkAsync(
			string localProjectPath,
			MarkedSecretProfileEntry mark,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<PersistentSecretMarkWriteResult> RemoveMarkAsync(
			string localProjectPath,
			PersistentSecretMarkId markId,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<PersistentSecretMarkWriteResult> ApplyMarkDeltaAsync(
			string localProjectPath,
			PersistentSecretMarkDelta delta,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();
	}
}
