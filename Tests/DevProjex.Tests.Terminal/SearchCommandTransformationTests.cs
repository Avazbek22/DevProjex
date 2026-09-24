using System.Diagnostics;
using DevProjex.Infrastructure.ProjectProfiles;

namespace DevProjex.Tests.Terminal;

public sealed class SearchCommandTransformationTests
{
	[Fact]
	public void CompressionOnlyLocalProfileStillFeedsSearch()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile(
			"project/VisibleType.cs",
			"sealed class VisibleType { string Run() => \"implementation\"; }\n");
		var dataRoot = workspace.CreateDirectory("data");
		new ProjectProfileStore(() => dataRoot).SaveProfile(
			project,
			new ProjectSelectionProfile(
				[],
				[".cs"],
				[IgnoreOptionId.CompressCode],
				IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
				{
					[IgnoreOptionId.CompressCode] = true
				}));
		var start = new ProcessStartInfo("dotnet")
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		start.ArgumentList.Add(PublishedApplicationLocator.FindApplicationAssembly());
		foreach (var argument in new[]
				 {
					 "search", "VisibleType", project, "--profile", "local", "--git-mode", "none",
					 "--exclude", "none", "--language", "en", "--plain", "--progress", "never"
				 })
		{
			start.ArgumentList.Add(argument);
		}
		start.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = dataRoot;

		var result = TerminalTestProcess.Run(start, TimeSpan.FromMinutes(1));

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("VisibleType.cs", result.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("VisibleType", result.StandardOutput, StringComparison.Ordinal);
	}
}
