using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SecretRedactionTempDirectoryTestCollection
{
	public const string Name = "Secret redaction temporary directory";
}

[Collection(SecretRedactionTempDirectoryTestCollection.Name)]
public sealed class SecretRedactionTempDirectoryTests
{
	[Fact]
	public void ProductionCreate_UsesScavengerFormatAndCrashResidueIsRemoved()
	{
		var now = DateTime.UtcNow;
		var abandoned = SecretRedactionTempDirectory.Create();
		var path = abandoned.Path;
		try
		{
			Assert.True(SecretRedactionTempDirectory.HasExpectedDirectoryName(path));
			abandoned.AbandonForTest();
			MakeStale(path, now);

			Assert.True(SecretRedactionTempDirectory.Scavenge(Path.GetTempPath(), now) >= 1);
			Assert.False(Directory.Exists(path));
		}
		finally
		{
			abandoned.Dispose();
		}
	}

	[Fact]
	public void Create_RetriesWhenGeneratedDirectoryNameAlreadyExists()
	{
		using var root = new TemporaryDirectory();
		var collision = Guid.ParseExact("11111111111111111111111111111111", "N");
		var available = Guid.ParseExact("22222222222222222222222222222222", "N");
		Directory.CreateDirectory(Path.Combine(
			root.Path,
			$"{SecretRedactionTempDirectory.DirectoryPrefix}{collision:N}"));
		var generated = new Queue<Guid>([collision, available]);

		using var created = SecretRedactionTempDirectory.Create(root.Path, generated.Dequeue);

		Assert.EndsWith(available.ToString("N"), created.Path, StringComparison.Ordinal);
		Assert.True(SecretRedactionTempDirectory.HasExpectedDirectoryName(created.Path));
	}

	[Fact]
	public void Scavenger_RemovesOnlyStaleUnleasedOwnedDirectory()
	{
		using var root = new TemporaryDirectory();
		var now = DateTime.UtcNow;
		var stale = SecretRedactionTempDirectory.Create(root.Path);
		var stalePath = stale.Path;
		stale.AbandonForTest();
		MakeStale(stalePath, now);

		var removed = SecretRedactionTempDirectory.Scavenge(root.Path, now);

		Assert.Equal(1, removed);
		Assert.False(Directory.Exists(stalePath));
	}

	[Fact]
	public void Scavenger_LeavesLiveDirectoryUntilLeaseIsReleased()
	{
		using var root = new TemporaryDirectory();
		var now = DateTime.UtcNow;
		var live = SecretRedactionTempDirectory.Create(root.Path);
		var livePath = live.Path;
		MakeStale(livePath, now);

		Assert.Equal(0, SecretRedactionTempDirectory.Scavenge(root.Path, now));
		Assert.True(Directory.Exists(livePath));

		live.AbandonForTest();
		Assert.Equal(1, SecretRedactionTempDirectory.Scavenge(root.Path, now));
		Assert.False(Directory.Exists(livePath));
	}

	[Fact]
	public void Scavenger_IgnoresForeignFormatAndOwnerMarker()
	{
		using var root = new TemporaryDirectory();
		var now = DateTime.UtcNow;
		var foreignFormat = Directory.CreateDirectory(Path.Combine(root.Path, "unrelated-output"));
		var foreignOwner = Directory.CreateDirectory(
			Path.Combine(root.Path, $"{SecretRedactionTempDirectory.DirectoryPrefix}foreign"));
		File.WriteAllText(Path.Combine(foreignOwner.FullName, SecretRedactionTempDirectory.OwnerFileName), "other");
		File.WriteAllText(Path.Combine(foreignOwner.FullName, SecretRedactionTempDirectory.LeaseFileName), "");
		File.SetLastWriteTimeUtc(
			Path.Combine(foreignOwner.FullName, SecretRedactionTempDirectory.OwnerFileName),
			now - SecretRedactionTempDirectory.MinimumScavengeAge - TimeSpan.FromHours(1));

		Assert.Equal(0, SecretRedactionTempDirectory.Scavenge(root.Path, now));
		Assert.True(Directory.Exists(foreignFormat.FullName));
		Assert.True(Directory.Exists(foreignOwner.FullName));
	}

	[Fact]
	public void Scavenger_IgnoresOwnedDirectoryWithMalformedIdentifier()
	{
		using var root = new TemporaryDirectory();
		var now = DateTime.UtcNow;
		var owned = SecretRedactionTempDirectory.Create(root.Path);
		var malformedPath = Path.Combine(
			root.Path,
			$"{SecretRedactionTempDirectory.DirectoryPrefix}not-a-guid");
		owned.AbandonForTest();
		Directory.Move(owned.Path, malformedPath);
		MakeStale(malformedPath, now);

		Assert.Equal(0, SecretRedactionTempDirectory.Scavenge(root.Path, now));
		Assert.True(Directory.Exists(malformedPath));
	}

	[Fact]
	public void Scavenger_RemovesStaleOwnedDirectoryWhenCrashPrecededLeaseCreation()
	{
		using var root = new TemporaryDirectory();
		var now = DateTime.UtcNow;
		var abandoned = SecretRedactionTempDirectory.Create(root.Path);
		var abandonedPath = abandoned.Path;
		abandoned.AbandonForTest();
		File.Delete(Path.Combine(abandonedPath, SecretRedactionTempDirectory.LeaseFileName));
		MakeStale(abandonedPath, now);

		Assert.Equal(1, SecretRedactionTempDirectory.Scavenge(root.Path, now));
		Assert.False(Directory.Exists(abandonedPath));
	}

	private static void MakeStale(string directory, DateTime now) =>
		File.SetLastWriteTimeUtc(
			Path.Combine(directory, SecretRedactionTempDirectory.OwnerFileName),
			now - SecretRedactionTempDirectory.MinimumScavengeAge - TimeSpan.FromHours(1));
}
