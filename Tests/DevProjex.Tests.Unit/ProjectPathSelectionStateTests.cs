using DevProjex.Application.Context;

namespace DevProjex.Tests.Unit;

public sealed class ProjectPathSelectionStateTests
{
	[Fact]
	public void PlannerDistinguishesSelectedOutsideAndFilteredPaths()
	{
		using var workspace = new TemporaryDirectory();
		var selected = workspace.CreateFile("src/selected.cs", "class Selected {}");
		var outside = workspace.CreateFile("src/outside.cs", "class Outside {}");
		var filtered = workspace.CreateFile("src/filtered.txt", "filtered");
		var source = new TreeNodeDescriptor(
			"src",
			Path.Combine(workspace.Path, "src"),
			true,
			false,
			"folder",
			[
				new TreeNodeDescriptor("selected.cs", selected, false, false, "file", []),
				new TreeNodeDescriptor("outside.cs", outside, false, false, "file", [])
			]);
		var root = new TreeNodeDescriptor(
			"project",
			workspace.Path,
			true,
			false,
			"folder",
			[source]);
		Assert.Equal(
			ProjectPathSelectionState.InsideSelection,
			ProjectContextPlanner.ClassifyPath(root, [selected], selected));
		Assert.Equal(
			ProjectPathSelectionState.OutsideSelection,
			ProjectContextPlanner.ClassifyPath(root, [selected], outside));
		Assert.Equal(
			ProjectPathSelectionState.HiddenByFilters,
			ProjectContextPlanner.ClassifyPath(root, [selected], filtered));
	}
}
