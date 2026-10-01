using DevProjex.Application.Compression;
using DevProjex.Application.Context;

namespace DevProjex.Tests.Unit;

public sealed class ProfileContentKindsResolutionTests
{
	private static ProjectSelectionResolver ResolverFor(ProjectSelectionSpec portableProfile) =>
		new(new UnusedStore(), (_, _) => Task.FromResult(portableProfile));

	[Fact]
	public async Task TheStandardProfileMandatesNoTransformations()
	{
		var resolved = await ResolverFor(ProjectSelectionSpec.Standard).ResolveAsync(
			"project",
			ProjectProfileReference.Standard,
			new ProjectSelectionSpec(),
			TestContext.Current.CancellationToken);

		Assert.Equal(CodeTransformKinds.None, resolved.ProfileContentKinds);
	}

	[Fact]
	public async Task AProfileThatCompressesIsRecordedSeparatelyFromTheCall()
	{
		var profile = ProjectSelectionSpec.Standard with { CompressCode = true };

		var resolved = await ResolverFor(profile).ResolveAsync(
			"project",
			new ProjectProfileReference(ProjectProfileSourceKind.Portable, "profile.json"),
			new ProjectSelectionSpec { StripComments = true },
			TestContext.Current.CancellationToken);

		// The effective selection is profile OR call, but the profile's own share stays visible so a
		// per-file override back to full cannot escape what the profile mandates.
		Assert.True(resolved.CompressCode);
		Assert.True(resolved.StripComments);
		Assert.Equal(CodeTransformKinds.Bodies, resolved.ProfileContentKinds);
	}

	[Fact]
	public async Task AnExplicitCallFlagIsNotMistakenForAProfileMandate()
	{
		var resolved = await ResolverFor(ProjectSelectionSpec.Standard).ResolveAsync(
			"project",
			ProjectProfileReference.Standard,
			new ProjectSelectionSpec { CompressCode = true, StripComments = true },
			TestContext.Current.CancellationToken);

		// On the command line the three toggles are the call level. Treating them as a profile
		// mandate would make a per-file override back to full a silent no-op.
		Assert.True(resolved.CompressCode);
		Assert.Equal(CodeTransformKinds.None, resolved.ProfileContentKinds);
	}

	[Fact]
	public async Task AProfileMandateSurvivesAnExplicitCallOptOut()
	{
		var profile = ProjectSelectionSpec.Standard with { CompressCode = true };

		var resolved = await ResolverFor(profile).ResolveAsync(
			"project",
			new ProjectProfileReference(ProjectProfileSourceKind.Portable, "profile.json"),
			new ProjectSelectionSpec { CompressCode = false },
			TestContext.Current.CancellationToken);

		// An explicit opt-out still wins for the effective selection, exactly as today; the record
		// of what the profile asked for is separate from what the call resolved to.
		Assert.False(resolved.CompressCode);
		Assert.Equal(CodeTransformKinds.Bodies, resolved.ProfileContentKinds);
	}

	private sealed class UnusedStore : IProjectProfileStore
	{
		public ProjectProfileLookupResult LookupProfile(string localProjectPath, TimeSpan lockTimeout) =>
			new(ProjectProfileLookupStatus.Missing, null);
		public bool EnsureStorageExists() => false;
		public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile)
		{
			profile = null!;
			return false;
		}
		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile) => false;
		public bool TrySaveProfile(
			string localProjectPath,
			ProjectSelectionProfile profile,
			DateTimeOffset updatedUtc) => false;
		public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile) { }
		public ProjectProfileClearStatus ClearAllProfiles() => ProjectProfileClearStatus.Failed;
	}
}
