using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class ManualSecretRedactionSessionTests
{
	private const string Secret = "manual-secret-value-42";

	[Fact]
	public void PersistentMark_RedactsTheSameTokenAcrossFiles()
	{
		using var workspace = new TemporaryDirectory();
		var firstPath = workspace.CreateFile("first.env", $"FIRST={Secret}");
		var secondPath = workspace.CreateFile("nested/second.env", $"SECOND='{Secret}'");
		using var session = new SecretRedactionSession(new EmptyDetector());
		session.ReplaceMarkedSecrets([CreateProfileMark(Secret, "FIRST")]);
		var scope = session.BeginOutput(workspace.Path, [firstPath, secondPath]);

		var first = scope.Redact(firstPath, File.ReadAllText(firstPath), TestContext.Current.CancellationToken);
		var second = scope.Redact(secondPath, File.ReadAllText(secondPath), TestContext.Current.CancellationToken);
		var snapshot = scope.Complete();

		Assert.DoesNotContain(Secret, first.Text, StringComparison.Ordinal);
		Assert.DoesNotContain(Secret, second.Text, StringComparison.Ordinal);
		Assert.Equal(2, snapshot.RedactedCount);
		Assert.Equal(2, snapshot.MarkedSecretCounts![CreateValue(Secret).Hash]);
	}

	[Fact]
	public void SessionMark_DoesNotEnterProfileOrSurviveANewSession()
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("config.env", $"TOKEN={Secret}");
		using var session = new SecretRedactionSession(new EmptyDetector());
		Assert.True(session.AddSessionMarkedSecret("config.env", "TOKEN=".Length, CreateValue(Secret)));
		var scope = session.BeginOutput(workspace.Path, [path]);
		var hidden = scope.Redact(path, File.ReadAllText(path), TestContext.Current.CancellationToken);
		scope.Complete();

		Assert.Empty(session.GetMarkedSecrets());
		Assert.DoesNotContain(Secret, hidden.Text, StringComparison.Ordinal);

		using var restarted = new SecretRedactionSession(new EmptyDetector());
		var restartedScope = restarted.BeginOutput(workspace.Path, [path]);
		var visible = restartedScope.Redact(path, File.ReadAllText(path), TestContext.Current.CancellationToken);
		restartedScope.Complete();
		Assert.Contains(Secret, visible.Text, StringComparison.Ordinal);
		Assert.Empty(visible.Spans);
	}

	[Fact]
	public void RemovingSessionMark_UnmarksOnlyTheSelectedOccurrence()
	{
		using var workspace = new TemporaryDirectory();
		var content = $"FIRST={Secret}\nSECOND={Secret}";
		var path = workspace.CreateFile("config.env", content);
		using var session = new SecretRedactionSession(new EmptyDetector());
		Assert.True(session.AddSessionMarkedSecret(
			"config.env",
			content.IndexOf(Secret, StringComparison.Ordinal),
			CreateValue(Secret)));
		Assert.True(session.AddSessionMarkedSecret(
			"config.env",
			content.LastIndexOf(Secret, StringComparison.Ordinal),
			CreateValue(Secret)));

		var initialScope = session.BeginOutput(workspace.Path, [path]);
		var initial = initialScope.Redact(path, content, TestContext.Current.CancellationToken);
		initialScope.Complete();
		var firstSessionMarkId = Assert.IsType<string>(initial.Spans[0].SessionMarkId);
		var secondSessionMarkId = Assert.IsType<string>(initial.Spans[1].SessionMarkId);

		Assert.True(session.RemoveSessionMarkedSecret(firstSessionMarkId));
		var updatedScope = session.BeginOutput(workspace.Path, [path]);
		var updated = updatedScope.Redact(path, content, TestContext.Current.CancellationToken);
		updatedScope.Complete();

		Assert.Contains($"FIRST={Secret}", updated.Text, StringComparison.Ordinal);
		Assert.DoesNotContain($"SECOND={Secret}", updated.Text, StringComparison.Ordinal);
		var remaining = Assert.Single(updated.Spans);
		Assert.Equal(secondSessionMarkId, remaining.SessionMarkId);
		Assert.True(remaining.Source.HasFlag(SecretFindingSource.SessionMark));
	}

	[Fact]
	public void RemovingPersistentMark_LeavesAnEngineFindingActive()
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("config.env", $"TOKEN={Secret}");
		using var session = new SecretRedactionSession(new ExactDetector());
		var mark = CreateProfileMark(Secret, "TOKEN");
		session.ReplaceMarkedSecrets([mark]);
		var markedScope = session.BeginOutput(workspace.Path, [path]);
		var marked = markedScope.Redact(path, File.ReadAllText(path), TestContext.Current.CancellationToken);
		markedScope.Complete();
		var span = Assert.Single(marked.Spans);
		Assert.True(span.Source.HasFlag(SecretFindingSource.PersistentMark));
		Assert.True(span.Source.HasFlag(SecretFindingSource.Detector));

		Assert.True(session.RemoveMarkedSecret(mark.H));
		var detectorScope = session.BeginOutput(workspace.Path, [path]);
		var detected = detectorScope.Redact(path, File.ReadAllText(path), TestContext.Current.CancellationToken);
		detectorScope.Complete();

		Assert.DoesNotContain(Secret, detected.Text, StringComparison.Ordinal);
		Assert.Equal(SecretFindingSource.Detector, Assert.Single(detected.Spans).Source);
	}

	[Fact]
	public void CompoundPersistentRemoval_PreservesSameDigestWithDifferentLength()
	{
		using var session = new SecretRedactionSession(new EmptyDetector());
		var first = new MarkedSecretProfileEntry("001122334455", "FIRST", 8);
		var second = new MarkedSecretProfileEntry("001122334455", "SECOND", 9);
		Assert.True(session.AddMarkedSecret(first));
		Assert.True(session.AddMarkedSecret(second));

		Assert.True(session.RemoveMarkedSecret(new PersistentSecretMarkId(first.H, first.Length)));

		Assert.Equal(second, Assert.Single(session.GetMarkedSecrets()));
	}

	[Fact]
	public void RemovingCombinedManualMark_RemovesBothSourcesWithOneInvalidationAndLeavesDetectorActive()
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("config.env", $"TOKEN={Secret}");
		using var session = new SecretRedactionSession(new ExactDetector());
		var value = CreateValue(Secret);
		var persistentMark = CreateProfileMark(Secret, "TOKEN");
		session.ReplaceMarkedSecrets([persistentMark]);
		Assert.True(session.AddSessionMarkedSecret("config.env", "TOKEN=".Length, value));
		var markedScope = session.BeginOutput(workspace.Path, [path]);
		var marked = markedScope.Redact(path, File.ReadAllText(path), TestContext.Current.CancellationToken);
		markedScope.Complete();
		var markedSpan = Assert.Single(marked.Spans);
		Assert.True(markedSpan.Source.HasFlag(SecretFindingSource.PersistentMark));
		Assert.True(markedSpan.Source.HasFlag(SecretFindingSource.SessionMark));
		Assert.True(markedSpan.Source.HasFlag(SecretFindingSource.Detector));
		var sessionMarkId = Assert.IsType<string>(markedSpan.SessionMarkId);
		var invalidationCount = 0;
		session.OverridesChanged += (_, _) => invalidationCount++;

		var removal = session.RemoveManualSecret(persistentMark.H, sessionMarkId);

		Assert.True(removal.PersistentMarkRemoved);
		Assert.True(removal.SessionMarkRemoved);
		Assert.Equal(1, invalidationCount);
		var detectorScope = session.BeginOutput(workspace.Path, [path]);
		var detected = detectorScope.Redact(path, File.ReadAllText(path), TestContext.Current.CancellationToken);
		detectorScope.Complete();
		Assert.Equal(SecretFindingSource.Detector, Assert.Single(detected.Spans).Source);
	}

	[Fact]
	public void AddingMark_InvalidatesTheMergedFindingCache()
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("config.env", $"TOKEN={Secret}");
		var detector = new CountingEmptyDetector();
		using var session = new SecretRedactionSession(detector);
		Assert.Contains(Secret, Redact(session, workspace.Path, path), StringComparison.Ordinal);
		Assert.Equal(1, detector.CallCount);

		Assert.True(session.AddMarkedSecret(CreateProfileMark(Secret, "TOKEN")));
		Assert.DoesNotContain(Secret, Redact(session, workspace.Path, path), StringComparison.Ordinal);
		Assert.Equal(2, detector.CallCount);
	}

	private static string Redact(SecretRedactionSession session, string root, string path)
	{
		var scope = session.BeginOutput(root, [path]);
		var result = scope.Redact(path, File.ReadAllText(path), TestContext.Current.CancellationToken);
		scope.Complete();
		return result.Text;
	}

	private static MarkedSecretValue CreateValue(string value)
	{
		Assert.True(MarkedSecretValueNormalizer.TryCreate(value, out var result, out var error), error.ToString());
		return result;
	}

	private static MarkedSecretProfileEntry CreateProfileMark(string value, string? key)
	{
		var normalized = CreateValue(value);
		return new MarkedSecretProfileEntry(normalized.Hash, key, normalized.Length);
	}

	private sealed class EmptyDetector : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) => [];
	}

	private sealed class ExactDetector : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default)
		{
			var start = content.IndexOf(Secret, StringComparison.Ordinal);
			return start < 0 ? [] : [new DetectedSecret("engine-rule", start, Secret.Length, Secret, 0)];
		}
	}

	private sealed class CountingEmptyDetector : ISecretDetector
	{
		public int CallCount { get; private set; }

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default)
		{
			CallCount++;
			return [];
		}
	}
}
