using DevProjex.Application.Context;

namespace DevProjex.Tests.Unit;

public sealed class ProjectSelectionAdapterTests
{
	[Fact]
	public void LocalProfileKeepsExplicitEmptyPathSelection()
	{
		var profile = new ProjectSelectionProfile([], [], [], SelectedPaths: []);

		var selection = ProjectSelectionAdapter.FromLegacyProfile(
			profile,
			ProjectProfileReference.Local);

		Assert.Empty(Assert.IsAssignableFrom<IReadOnlyCollection<string>>(selection.SelectedPaths));
	}

	[Fact]
	public void LocalProfileWithoutPathSelectionKeepsImplicitFullTree()
	{
		var profile = new ProjectSelectionProfile([], [], [], SelectedPaths: null);

		var selection = ProjectSelectionAdapter.FromLegacyProfile(
			profile,
			ProjectProfileReference.Local);

		Assert.Null(selection.SelectedPaths);
	}
}
