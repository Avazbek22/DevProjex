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
		var store = new ProjectProfileStore(() => appData);

		var lookup = store.LookupProfile(project, TimeSpan.FromSeconds(1));

		Assert.Equal(ProjectProfileLookupStatus.Found, lookup.Status);
		Assert.Null(lookup.Profile!.SelectedPaths);
		Assert.True(store.TrySaveProfile(project, lookup.Profile));
		using var document = JsonDocument.Parse(File.ReadAllText(store.GetPath()));
		Assert.Equal(4, document.RootElement.GetProperty("schemaVersion").GetInt32());
		var persisted = document.RootElement.GetProperty("profiles").GetProperty(PathUtility.Normalize(project));
		Assert.Equal(1, persisted.GetProperty("selectedPathsSemanticsVersion").GetInt32());
		Assert.Equal(JsonValueKind.Null, persisted.GetProperty("selectedPaths").ValueKind);
	}

	[Fact]
	public void Version51FrontierFixture_RoundTripsThroughTheCurrentWriter()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var appData = workspace.CreateFolder("app-data");
		InstallFixture("v5.1-local-frontier.json", project, appData);
		var store = new ProjectProfileStore(() => appData);

		var lookup = store.LookupProfile(project, TimeSpan.FromSeconds(1));

		Assert.Equal(ProjectProfileLookupStatus.Found, lookup.Status);
		Assert.Equal(["src/Inside.cs"], lookup.Profile!.SelectedPaths);
		Assert.Equal(["src"], lookup.Profile.SelectedRootFolders);
		Assert.Equal([".cs"], lookup.Profile.SelectedExtensions);
		Assert.False(lookup.Profile.RootFolderStates!["docs"]);
		Assert.False(lookup.Profile.ExtensionStates![".md"]);
		Assert.True(store.TrySaveProfile(project, lookup.Profile));

		using var document = JsonDocument.Parse(File.ReadAllText(store.GetPath()));
		Assert.Equal(4, document.RootElement.GetProperty("schemaVersion").GetInt32());
		var persisted = document.RootElement.GetProperty("profiles").GetProperty(PathUtility.Normalize(project));
		Assert.Equal(1, persisted.GetProperty("selectedPathsSemanticsVersion").GetInt32());
		Assert.Equal(
			["src/Inside.cs"],
			persisted.GetProperty("selectedPaths").EnumerateArray().Select(static item => item.GetString()));
		Assert.False(persisted.GetProperty("rootFolderStates").GetProperty("docs").GetBoolean());
		Assert.False(persisted.GetProperty("extensionStates").GetProperty(".md").GetBoolean());
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
		Assert.Equal(4, document.RootElement.GetProperty("schemaVersion").GetInt32());
		var profile = document.RootElement.GetProperty("profiles").GetProperty(PathUtility.Normalize(project));
		Assert.Equal(1, profile.GetProperty("selectedPathsSemanticsVersion").GetInt32());
		Assert.Empty(profile.GetProperty("selectedPaths").EnumerateArray());

		var roundTrip = store.LookupProfile(project, TimeSpan.FromSeconds(1));
		Assert.Equal(ProjectProfileLookupStatus.Found, roundTrip.Status);
		Assert.Empty(Assert.IsAssignableFrom<IReadOnlyCollection<string>>(roundTrip.Profile!.SelectedPaths));
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
	public void CurrentWriter_PreservesNullForTheFullTree()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var appData = workspace.CreateFolder("app-data");
		var store = new ProjectProfileStore(() => appData);

		Assert.True(store.TrySaveProfile(
			project,
			new ProjectSelectionProfile([], [], [], SelectedPaths: null)));

		using var document = JsonDocument.Parse(File.ReadAllText(store.GetPath()));
		var profile = document.RootElement.GetProperty("profiles").GetProperty(PathUtility.Normalize(project));
		Assert.Equal(JsonValueKind.Null, profile.GetProperty("selectedPaths").ValueKind);
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
