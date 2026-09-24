using DevProjex.Application.Context;

namespace DevProjex.Tests.Unit;

public sealed class ProjectSelectionResolverProfileStatusTests
{
	[Theory]
	[InlineData(ProjectProfileLookupStatus.Missing, "DPX-CLI-PROFILE-NOT-FOUND")]
	[InlineData(ProjectProfileLookupStatus.TemporarilyUnavailable, "DPX-CLI-PROFILE-BUSY")]
	[InlineData(ProjectProfileLookupStatus.InvalidStorage, "DPX-CLI-PROFILE-CORRUPT")]
	[InlineData(ProjectProfileLookupStatus.UnsupportedFutureSchema, "DPX-CLI-PROFILE-FUTURE-SCHEMA")]
	[InlineData(ProjectProfileLookupStatus.InvalidProjectPath, "DPX-CLI-PROFILE-INVALID")]
	public async Task LocalProfileLookupStatusProducesDistinctFailure(
		ProjectProfileLookupStatus status,
		string expectedCode)
	{
		var resolver = new ProjectSelectionResolver(
			new LookupStore(status),
			static (_, _) => Task.FromResult(ProjectSelectionSpec.Standard));

		var exception = await Assert.ThrowsAsync<ProjectContextValidationException>(() =>
			resolver.ResolveAsync(
				"project",
				ProjectProfileReference.Local,
				new ProjectSelectionSpec(),
				TestContext.Current.CancellationToken));

		Assert.Equal(expectedCode, exception.Code);
	}

	private sealed class LookupStore(ProjectProfileLookupStatus status) : IProjectProfileStore
	{
		public ProjectProfileLookupResult LookupProfile(string localProjectPath, TimeSpan lockTimeout) =>
			new(status, null);
		public bool EnsureStorageExists() => false;
		public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile)
		{
			profile = null!;
			return false;
		}
		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile) => false;
		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile, DateTimeOffset updatedUtc) => false;
		public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile) { }
		public ProjectProfileClearStatus ClearAllProfiles() => ProjectProfileClearStatus.Failed;
	}
}
