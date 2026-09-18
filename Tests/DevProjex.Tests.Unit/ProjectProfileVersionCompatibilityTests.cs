using System.Text.Json.Nodes;

namespace DevProjex.Tests.Unit;

public sealed class ProjectProfileVersionCompatibilityTests
{
	[Fact]
	public void Version51EmptySelectionFixture_LoadsAsFullTree()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var appData = workspace.CreateFolder("app-data");
		InstallFixture("v5.1-local-full-tree.json", project, appData);

		var lookup = new ProjectProfileStore(() => appData)
			.LookupProfile(project, TimeSpan.FromSeconds(1));

		Assert.Equal(ProjectProfileLookupStatus.Found, lookup.Status);
		Assert.Null(lookup.Profile!.SelectedPaths);
	}

	[Fact]
	public void Version52MarkedEmptySelectionFixture_LoadsAsExplicitEmpty()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var appData = workspace.CreateFolder("app-data");
		InstallFixture("v5.2-local-explicit-empty.json", project, appData);

		var lookup = new ProjectProfileStore(() => appData)
			.LookupProfile(project, TimeSpan.FromSeconds(1));

		Assert.Equal(ProjectProfileLookupStatus.Found, lookup.Status);
		Assert.Empty(Assert.IsAssignableFrom<IReadOnlyCollection<string>>(lookup.Profile!.SelectedPaths));
	}

	[Fact]
	public void CurrentWriter_EmitsTheSelectionSemanticsMarker()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var appData = workspace.CreateFolder("app-data");
		var store = new ProjectProfileStore(() => appData);

		Assert.True(store.TrySaveProfile(
			project,
			new ProjectSelectionProfile([], [], [], SelectedPaths: [])));

		using var document = JsonDocument.Parse(File.ReadAllText(store.GetPath()));
		var profile = document.RootElement.GetProperty("profiles").GetProperty(PathUtility.Normalize(project));
		Assert.Equal(1, profile.GetProperty("selectedPathsSemanticsVersion").GetInt32());
		Assert.Empty(profile.GetProperty("selectedPaths").EnumerateArray());
	}

	[Fact]
	public void LegacyRewriteWithoutTheMarker_FallsBackToFullTree()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var appData = workspace.CreateFolder("app-data");
		InstallFixture("v5.2-local-explicit-empty.json", project, appData);
		var path = new ProjectProfileStore(() => appData).GetPath();
		var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
		var profiles = document["profiles"]!.AsObject();
		profiles[PathUtility.Normalize(project)]!.AsObject().Remove("selectedPathsSemanticsVersion");
		File.WriteAllText(path, document.ToJsonString());

		var lookup = new ProjectProfileStore(() => appData)
			.LookupProfile(project, TimeSpan.FromSeconds(1));

		Assert.Equal(ProjectProfileLookupStatus.Found, lookup.Status);
		Assert.Null(lookup.Profile!.SelectedPaths);
	}

	[Fact]
	public void UnknownSelectionSemanticsVersion_IsReportedAsInvalidStorage()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var appData = workspace.CreateFolder("app-data");
		InstallFixture("v5.2-local-explicit-empty.json", project, appData);
		var store = new ProjectProfileStore(() => appData);
		var document = JsonNode.Parse(File.ReadAllText(store.GetPath()))!.AsObject();
		var profiles = document["profiles"]!.AsObject();
		profiles[PathUtility.Normalize(project)]!["selectedPathsSemanticsVersion"] = 2;
		File.WriteAllText(store.GetPath(), document.ToJsonString());

		var lookup = store.LookupProfile(project, TimeSpan.FromSeconds(1));

		Assert.Equal(ProjectProfileLookupStatus.InvalidStorage, lookup.Status);
	}

	private static void InstallFixture(string name, string project, string appData)
	{
		var fixturePath = Path.Combine(FindRepositoryRoot(), "Tests", "Fixtures", "Profiles", name);
		var document = JsonNode.Parse(File.ReadAllText(fixturePath))!.AsObject();
		var profiles = document["profiles"]!.AsObject();
		var profile = profiles["${PROJECT_ROOT}"];
		profiles.Remove("${PROJECT_ROOT}");
		profiles[PathUtility.Normalize(project)] = profile;
		var directory = Path.Combine(appData, "DevProjex");
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, "project-profiles.json"), document.ToJsonString());
	}

	private static string FindRepositoryRoot()
	{
		var current = new DirectoryInfo(AppContext.BaseDirectory);
		while (current is not null)
		{
			if (File.Exists(Path.Combine(current.FullName, "DevProjex.sln")))
				return current.FullName;
			current = current.Parent;
		}
		throw new DirectoryNotFoundException("Repository root was not found.");
	}
}
