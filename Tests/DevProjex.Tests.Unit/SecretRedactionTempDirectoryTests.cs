using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class SecretRedactionTempDirectoryTests
{
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
