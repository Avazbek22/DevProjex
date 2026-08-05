using DevProjex.Application.Context;

namespace DevProjex.Tests.Unit;

public sealed class StartupInteractionControllerSelectionTests
{
	[Fact]
	public async Task ResolveAsync_StandardProfile_ResetsOpenRootAndExtensionSelections()
	{
		using var temp = new TemporaryDirectory();
		var resolver = new ProjectSelectionResolver(
			new ProjectProfileStore(() => temp.Path),
			static (_, _) => throw new InvalidOperationException("Portable profile loading was unexpected."));

		var resolved = await resolver.ResolveAsync(
			temp.Path,
			ProjectProfileReference.Standard,
			new ProjectSelectionSpec(),
			TestContext.Current.CancellationToken);

		var intent = Assert.IsType<ProjectSelectionApplicationIntent>(resolved.ApplicationIntent);
		Assert.Equal(ProjectSelectionApplicationMode.ResetToDefaults, intent.Roots);
		Assert.Equal(ProjectSelectionApplicationMode.ResetToDefaults, intent.Extensions);
		Assert.Equal(ProjectSelectionApplicationMode.ApplyResolvedValue, intent.GitMode);
		Assert.Equal(ProjectSelectionApplicationMode.ApplyResolvedValue, intent.Exclusions);
		Assert.Equal(ProjectSelectionApplicationMode.ApplyResolvedValue, intent.HideSecrets);
	}

	[Fact]
	public async Task ResolveAsync_LocalProfileWithoutOverrides_PreservesDesktopProfileEvolutionIntent()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = Path.Combine(temp.Path, "project");
		Directory.CreateDirectory(projectPath);
		var store = new ProjectProfileStore(() => temp.Path);
		store.SaveProfile(
			projectPath,
			new ProjectSelectionProfile(
				SelectedRootFolders: ["src"],
				SelectedExtensions: [".cs"],
				SelectedIgnoreOptions: [IgnoreOptionId.UseGitIgnore, IgnoreOptionId.DotFolders],
				RootFolderStates: new Dictionary<string, bool> { ["src"] = true },
				ExtensionStates: new Dictionary<string, bool> { [".cs"] = true },
				IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
				{
					[IgnoreOptionId.UseGitIgnore] = true,
					[IgnoreOptionId.DotFolders] = true
				}));
		var resolver = new ProjectSelectionResolver(
			store,
			static (_, _) => throw new InvalidOperationException("Portable profile loading was unexpected."));

		var resolved = await resolver.ResolveAsync(
			projectPath,
			ProjectProfileReference.Local,
			new ProjectSelectionSpec(),
			TestContext.Current.CancellationToken);
		var roundTripped = JsonSerializer.Deserialize<ProjectSelectionSpec>(
			JsonSerializer.Serialize(resolved));

		var intent = Assert.IsType<ProjectSelectionApplicationIntent>(roundTripped?.ApplicationIntent);
		Assert.Equal(ProjectSelectionApplicationMode.Preserve, intent.Roots);
		Assert.Equal(ProjectSelectionApplicationMode.Preserve, intent.Extensions);
		Assert.Equal(ProjectSelectionApplicationMode.Preserve, intent.GitMode);
		Assert.Equal(ProjectSelectionApplicationMode.Preserve, intent.Exclusions);
		Assert.Equal(ProjectSelectionApplicationMode.Preserve, intent.HideSecrets);
	}

	[Fact]
	public async Task ResolveAsync_LocalGitOverride_AppliesOnlyGitComponentAcrossDesktopBoundary()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = Path.Combine(temp.Path, "project");
		Directory.CreateDirectory(projectPath);
		var store = new ProjectProfileStore(() => temp.Path);
		store.SaveProfile(
			projectPath,
			new ProjectSelectionProfile(
				SelectedRootFolders: ["src"],
				SelectedExtensions: [".cs"],
				SelectedIgnoreOptions: [IgnoreOptionId.UseGitIgnore, IgnoreOptionId.DotFolders]));
		var resolver = new ProjectSelectionResolver(
			store,
			static (_, _) => throw new InvalidOperationException("Portable profile loading was unexpected."));

		var resolved = await resolver.ResolveAsync(
			projectPath,
			ProjectProfileReference.Local,
			new ProjectSelectionSpec(GitMode: GitFilteringMode.TrackedFilesOnly),
			TestContext.Current.CancellationToken);

		var intent = Assert.IsType<ProjectSelectionApplicationIntent>(resolved.ApplicationIntent);
		Assert.Equal(ProjectSelectionApplicationMode.Preserve, intent.Roots);
		Assert.Equal(ProjectSelectionApplicationMode.Preserve, intent.Extensions);
		Assert.Equal(ProjectSelectionApplicationMode.ApplyResolvedValue, intent.GitMode);
		Assert.Equal(ProjectSelectionApplicationMode.Preserve, intent.Exclusions);
		Assert.Equal(ProjectSelectionApplicationMode.Preserve, intent.HideSecrets);
	}

	[Fact]
	public async Task ResolveAsync_LocalHideSecretsOverride_AppliesOnlyContentTransformationAcrossDesktopBoundary()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = Path.Combine(temp.Path, "project");
		Directory.CreateDirectory(projectPath);
		var store = new ProjectProfileStore(() => temp.Path);
		store.SaveProfile(
			projectPath,
			new ProjectSelectionProfile(
				SelectedRootFolders: ["src"],
				SelectedExtensions: [".cs"],
				SelectedIgnoreOptions: [IgnoreOptionId.UseGitIgnore, IgnoreOptionId.SmartIgnore]));
		var resolver = new ProjectSelectionResolver(
			store,
			static (_, _) => throw new InvalidOperationException("Portable profile loading was unexpected."));

		var resolved = await resolver.ResolveAsync(
			projectPath,
			ProjectProfileReference.Local,
			new ProjectSelectionSpec(HideSecrets: true),
			TestContext.Current.CancellationToken);

		var intent = Assert.IsType<ProjectSelectionApplicationIntent>(resolved.ApplicationIntent);
		Assert.Equal(ProjectSelectionApplicationMode.Preserve, intent.Roots);
		Assert.Equal(ProjectSelectionApplicationMode.Preserve, intent.Extensions);
		Assert.Equal(ProjectSelectionApplicationMode.Preserve, intent.GitMode);
		Assert.Equal(ProjectSelectionApplicationMode.Preserve, intent.Exclusions);
		Assert.Equal(ProjectSelectionApplicationMode.ApplyResolvedValue, intent.HideSecrets);
		Assert.True(resolved.HideSecrets);
		Assert.Contains(ProjectExclusion.SmartIgnore, resolved.Exclusions!);
	}

	[Fact]
	public void ResolveIgnoreSelectionOverride_GitOnly_InheritsEveryExclusionState()
	{
		var inherited = new HashSet<IgnoreOptionId>
		{
			IgnoreOptionId.UseGitIgnore,
			IgnoreOptionId.DotFolders,
			IgnoreOptionId.HiddenFiles
		};

		var resolved = StartupInteractionController.ResolveIgnoreSelectionOverride(
			new ProjectSelectionSpec(GitMode: GitFilteringMode.TrackedFilesOnly),
			inherited);

		Assert.True(resolved.SetEquals(
			new HashSet<IgnoreOptionId>
			{
				IgnoreOptionId.TrackedGitFilesOnly,
				IgnoreOptionId.DotFolders,
				IgnoreOptionId.HiddenFiles
			}));
	}

	[Fact]
	public void ResolveIgnoreSelectionOverride_ExclusionsOnly_InheritsGitAndTreatsEmptyAsClosedSet()
	{
		var inherited = new HashSet<IgnoreOptionId>
		{
			IgnoreOptionId.UseGitIgnore,
			IgnoreOptionId.DotFolders,
			IgnoreOptionId.HiddenFiles
		};

		var resolved = StartupInteractionController.ResolveIgnoreSelectionOverride(
			new ProjectSelectionSpec(Exclusions: []),
			inherited);

		Assert.True(resolved.SetEquals([IgnoreOptionId.UseGitIgnore]));
	}

	[Fact]
	public void ResolveIgnoreSelectionOverride_PathExclusionsOnly_PreservesHideSecrets()
	{
		var inherited = new HashSet<IgnoreOptionId>
		{
			IgnoreOptionId.UseGitIgnore,
			IgnoreOptionId.DotFolders,
			IgnoreOptionId.HideSecrets
		};

		var resolved = StartupInteractionController.ResolveIgnoreSelectionOverride(
			new ProjectSelectionSpec(Exclusions: []),
			inherited);

		Assert.True(resolved.SetEquals(
			[IgnoreOptionId.UseGitIgnore, IgnoreOptionId.HideSecrets]));
	}
}
