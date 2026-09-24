using DevProjex.Infrastructure.ProjectProfiles;

namespace DevProjex.Tests.Unit;

public sealed class ProjectProfileStoreReadCacheTests
{
	[Fact]
	public void RepeatedLookupsReuseOneValidatedDocumentSnapshot()
	{
		using var temporary = new TemporaryDirectory();
		var appData = temporary.CreateFolder("data");
		var project = temporary.CreateFolder("project");
		var store = new ProjectProfileStore(() => appData);
		Assert.True(store.TrySaveProfile(project, Profile(["src"])));

		var first = store.LookupProfile(project, TimeSpan.FromSeconds(1));
		var parsesAfterFirst = store.DocumentParseCount;
		for (var iteration = 0; iteration < 1_000; iteration++)
		{
			var repeated = store.LookupProfile(project, TimeSpan.FromSeconds(1));
			Assert.Equal(ProjectProfileLookupStatus.Found, repeated.Status);
			Assert.Equal(first.Profile!.SelectedPaths, repeated.Profile!.SelectedPaths);
		}

		Assert.Equal(2, parsesAfterFirst);
		Assert.Equal(parsesAfterFirst, store.DocumentParseCount);
	}

	[Fact]
	public void AWriteFromAnotherStoreInvalidatesTheValidatedSnapshotImmediately()
	{
		using var temporary = new TemporaryDirectory();
		var appData = temporary.CreateFolder("data");
		var project = temporary.CreateFolder("project");
		var reader = new ProjectProfileStore(() => appData);
		var writer = new ProjectProfileStore(() => appData);
		Assert.True(writer.TrySaveProfile(project, Profile(["src"])));
		Assert.Equal(["src"], reader.LookupProfile(project, TimeSpan.FromSeconds(1)).Profile!.SelectedPaths);
		var initialParses = reader.DocumentParseCount;

		Assert.True(writer.TrySaveProfile(project, Profile(["tests/changed-by-another-process"])));
		var changed = reader.LookupProfile(project, TimeSpan.FromSeconds(1));

		Assert.Equal(["tests/changed-by-another-process"], changed.Profile!.SelectedPaths);
		Assert.Equal(initialParses + 2, reader.DocumentParseCount);
	}

	[Fact]
	public void HeaderFingerprintDetectsSameLengthWriteWithRestoredTimestamp()
	{
		using var temporary = new TemporaryDirectory();
		var appData = temporary.CreateFolder("data");
		var project = temporary.CreateFolder("project");
		var store = new ProjectProfileStore(() => appData);
		Assert.True(store.TrySaveProfile(project, Profile(["src"])));
		Assert.Equal(["src"], store.LookupProfile(project, TimeSpan.FromSeconds(1)).Profile!.SelectedPaths);
		var initialParses = store.DocumentParseCount;
		var primary = store.GetPath();
		var backup = primary + ".bak";
		RewritePreservingLengthAndTimestamp(primary, "src", "tst");
		RewritePreservingLengthAndTimestamp(backup, "src", "tst");

		var changed = store.LookupProfile(project, TimeSpan.FromSeconds(1));

		Assert.Equal(["tst"], changed.Profile!.SelectedPaths);
		Assert.Equal(initialParses + 2, store.DocumentParseCount);
	}

	[Fact]
	public void AtomicReplacementBeyondHeaderWithRestoredTimestampInvalidatesSnapshot()
	{
		using var temporary = new TemporaryDirectory();
		var appData = temporary.CreateFolder("data");
		var projects = Enumerable.Range(0, 100)
			.Select(index => temporary.CreateFolder($"project-{index:D3}"))
			.ToArray();
		var writer = new ProjectProfileStore(() => appData);
		var requests = projects
			.Select((project, index) => new ProjectProfileSaveRequest(
				project,
				Profile([index == projects.Length - 1 ? "alpha" : $"src/{index:D3}"]),
				DateTimeOffset.UnixEpoch.AddSeconds(index + 1)))
			.ToArray();
		Assert.Equal(
			projects.Length,
			writer.TrySaveProfilesWithResult(requests, TimeSpan.FromSeconds(5)).SavedProjectPaths.Count);

		var reader = new ProjectProfileStore(() => appData);
		var targetProject = projects[^1];
		Assert.Equal(["alpha"], reader.LookupProfile(targetProject, TimeSpan.FromSeconds(1)).Profile!.SelectedPaths);
		var initialParses = reader.DocumentParseCount;
		var primary = writer.GetPath();
		var original = File.ReadAllText(primary);
		Assert.True(original.IndexOf("alpha", StringComparison.Ordinal) > 4 * 1024);
		var modified = original.Replace("alpha", "bravo", StringComparison.Ordinal);
		Assert.Equal(original.Length, modified.Length);
		Assert.Equal(original[..(4 * 1024)], modified[..(4 * 1024)]);
		var creationTime = File.GetCreationTimeUtc(primary);
		var timestamp = File.GetLastWriteTimeUtc(primary);
		var replacement = Path.Combine(Path.GetDirectoryName(primary)!, "replacement.json");
		File.WriteAllText(replacement, modified);
		File.SetLastWriteTimeUtc(replacement, timestamp);
		File.Move(replacement, primary, overwrite: true);
		Assert.Equal(timestamp, File.GetLastWriteTimeUtc(primary));
		if (creationTime == File.GetCreationTimeUtc(primary))
			Assert.Skip("This filesystem does not distinguish replacement-file creation times.");

		var changed = reader.LookupProfile(targetProject, TimeSpan.FromSeconds(1));
		Assert.Equal(["bravo"], changed.Profile!.SelectedPaths);
		Assert.Equal(initialParses + 2, reader.DocumentParseCount);
	}

	[Fact]
	public void DifferentProjectLookupsShareTheValidatedDatabaseSnapshot()
	{
		using var temporary = new TemporaryDirectory();
		var appData = temporary.CreateFolder("data");
		var projects = Enumerable.Range(0, 100)
			.Select(index => temporary.CreateFolder($"project-{index:D3}"))
			.ToArray();
		var writer = new ProjectProfileStore(() => appData);
		var requests = projects
			.Select((project, index) => new ProjectProfileSaveRequest(
				project,
				Profile([$"src/{index:D3}"]),
				DateTimeOffset.UnixEpoch.AddSeconds(index + 1)))
			.ToArray();
		Assert.Equal(
			projects.Length,
			writer.TrySaveProfilesWithResult(requests, TimeSpan.FromSeconds(5)).SavedProjectPaths.Count);
		var reader = new ProjectProfileStore(() => appData);

		for (var index = 0; index < projects.Length; index++)
		{
			var lookup = reader.LookupProfile(projects[index], TimeSpan.FromSeconds(1));
			Assert.Equal([$"src/{index:D3}"], lookup.Profile!.SelectedPaths);
		}

		Assert.Equal(2, reader.DocumentParseCount);
	}

	private static ProjectSelectionProfile Profile(IReadOnlyCollection<string> selectedPaths) =>
		new([], [], [], SelectedPaths: selectedPaths);

	private static void RewritePreservingLengthAndTimestamp(string path, string before, string after)
	{
		Assert.Equal(before.Length, after.Length);
		var timestamp = File.GetLastWriteTimeUtc(path);
		var content = File.ReadAllText(path);
		var rewritten = content.Replace(before, after, StringComparison.Ordinal);
		Assert.NotEqual(content, rewritten);
		Assert.Equal(content.Length, rewritten.Length);
		File.WriteAllText(path, rewritten);
		File.SetLastWriteTimeUtc(path, timestamp);
	}
}
