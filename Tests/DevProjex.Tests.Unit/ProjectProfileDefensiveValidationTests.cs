using System.Text.Json.Nodes;
using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class ProjectProfileDefensiveValidationTests
{
	[Fact]
	public void LegacyMarks_MalformedEntriesAreDroppedAndCompoundIdentitiesSurvive()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var appData = workspace.CreateFolder("app-data");
		var hugeKey = new string('k', ProjectProfileStorageLimits.MaximumMarkedSecretKeyLength + 1);
		var marks = new JsonArray
		{
			CreateLegacyMark(null, "NULL", 8),
			new JsonObject { ["h"] = 42, ["key"] = "WRONG-TYPE", ["length"] = 8 },
			CreateLegacyMark("001122334455", "FIRST", 8),
			CreateLegacyMark("001122334455", "SECOND", 9),
			CreateLegacyMark("aabbccddeeff", hugeKey, 12)
		};
		WriteSelectionStore(appData, new JsonObject
		{
			[project] = CreateSelectionProfile(marks)
		});
		var store = new ProjectProfileStore(() => appData);

		var lookup = store.LookupProfile(project, TimeSpan.FromSeconds(1));

		Assert.Equal(ProjectProfileLookupStatus.Found, lookup.Status);
		var loadedMarks = lookup.Profile!.MarkedSecrets!.OrderBy(mark => mark.Length).ToArray();
		Assert.Equal(3, loadedMarks.Length);
		Assert.Equal([8, 9, 12], loadedMarks.Select(mark => mark.Length).ToArray());
		Assert.Null(loadedMarks.Single(mark => mark.H == "aabbccddeeff").Key);
	}

	[Fact]
	public void MalformedProject_DoesNotInvalidateNeighboringProfile()
	{
		using var workspace = new TemporaryDirectory();
		var validProject = workspace.CreateFolder("valid");
		var invalidProject = workspace.CreateFolder("invalid");
		var appData = workspace.CreateFolder("app-data");
		WriteSelectionStore(appData, new JsonObject
		{
			[invalidProject] = new JsonObject { ["selectedExtensions"] = 42 },
			[validProject] = CreateSelectionProfile(new JsonArray())
		});
		var store = new ProjectProfileStore(() => appData);

		var valid = store.LookupProfile(validProject, TimeSpan.FromSeconds(1));
		var invalid = store.LookupProfile(invalidProject, TimeSpan.FromSeconds(1));

		Assert.Equal(ProjectProfileLookupStatus.Found, valid.Status);
		Assert.Equal(ProjectProfileLookupStatus.Missing, invalid.Status);
	}

	[Fact]
	public void HundredThousandMalformedLegacyMarks_DoNotDestroyNeighboringProject()
	{
		using var workspace = new TemporaryDirectory();
		var noisyProject = workspace.CreateFolder("noisy");
		var validProject = workspace.CreateFolder("valid");
		var appData = workspace.CreateFolder("app-data");
		var malformedMarks = new JsonArray();
		for (var index = 0; index < 100_000; index++)
			malformedMarks.Add(new JsonObject { ["h"] = null, ["length"] = 8 });
		WriteSelectionStore(appData, new JsonObject
		{
			[noisyProject] = CreateSelectionProfile(malformedMarks),
			[validProject] = CreateSelectionProfile(new JsonArray())
		});
		var store = new ProjectProfileStore(() => appData);

		var lookup = store.LookupProfile(validProject, TimeSpan.FromSeconds(1));
		var noisy = store.LookupProfile(noisyProject, TimeSpan.FromSeconds(1));

		Assert.Equal(ProjectProfileLookupStatus.Found, lookup.Status);
		Assert.Empty(lookup.Profile!.MarkedSecrets!);
		Assert.Equal(ProjectProfileLookupStatus.Missing, noisy.Status);
	}

	[Fact]
	public void TruncatedPrimary_RecoversFromValidBackup()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var appData = workspace.CreateFolder("app-data");
		var store = new ProjectProfileStore(() => appData);
		store.SaveProfile(project, new ProjectSelectionProfile([], [".cs"], []));
		File.WriteAllText(store.GetPath(), "{\"schemaVersion\":3,\"profiles\":");

		var lookup = new ProjectProfileStore(() => appData)
			.LookupProfile(project, TimeSpan.FromSeconds(1));

		Assert.Equal(ProjectProfileLookupStatus.Found, lookup.Status);
		Assert.Equal([".cs"], lookup.Profile!.SelectedExtensions.ToArray());
	}

	[Fact]
	public void OversizedPrimaryAndBackup_AreRejectedWithoutAllocationDrivenFailure()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var appData = workspace.CreateFolder("app-data");
		var store = new ProjectProfileStore(() => appData);
		var path = store.GetPath();
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		using (var primary = new FileStream(path, FileMode.Create, FileAccess.Write))
			primary.SetLength(ProjectProfileStorageLimits.MaximumJsonBytes + 1);
		using (var backup = new FileStream(path + ".bak", FileMode.Create, FileAccess.Write))
			backup.SetLength(ProjectProfileStorageLimits.MaximumJsonBytes + 1);

		var lookup = store.LookupProfile(project, TimeSpan.FromSeconds(1));

		Assert.Equal(ProjectProfileLookupStatus.InvalidStorage, lookup.Status);
	}

	[Fact]
	public async Task PersistentMarkStates_AreValidatedIndividually()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var appData = workspace.CreateFolder("app-data");
		var states = new JsonArray
		{
			CreateState(null, 8, "NULL"),
			new JsonObject { ["hash"] = 42, ["length"] = 8 },
			new JsonObject
			{
				["hash"] = "112233445566",
				["length"] = 8,
				["removed"] = false,
				["issuedUtcTicks"] = 0,
				["operationId"] = Guid.Empty
			},
			CreateState("001122334455", 8, "FIRST"),
			CreateState("001122334455", 9, "SECOND"),
			CreateState(
				"aabbccddeeff",
				12,
				new string('k', ProjectProfileStorageLimits.MaximumMarkedSecretKeyLength + 1))
		};
		WriteMarkStore(appData, project, states);
		var store = new ProjectProfileStore(() => appData);

		var result = await store.LoadMarksAsync(project, TestContext.Current.CancellationToken);

		Assert.True(result.Succeeded);
		var marks = result.Snapshot!.Marks.OrderBy(mark => mark.Length).ToArray();
		Assert.Equal([8, 9, 12], marks.Select(mark => mark.Length).ToArray());
		Assert.Null(marks.Single(mark => mark.H == "aabbccddeeff").Key);
	}

	[Fact]
	public async Task MalformedPersistentMarkProject_IsInvalidWithoutAffectingItsNeighbor()
	{
		using var workspace = new TemporaryDirectory();
		var invalidProject = workspace.CreateFolder("invalid");
		var validProject = workspace.CreateFolder("valid");
		var appData = workspace.CreateFolder("app-data");
		WriteMarkStore(
			appData,
			new JsonObject
			{
				[invalidProject] = new JsonObject
				{
					["revision"] = 1,
					["states"] = 42
				},
				[validProject] = CreateMarkProject(new JsonArray())
			});
		var store = new ProjectProfileStore(() => appData);

		var invalid = await store.LoadMarksAsync(
			invalidProject,
			TestContext.Current.CancellationToken);
		var valid = await store.LoadMarksAsync(
			validProject,
			TestContext.Current.CancellationToken);

		Assert.Equal(PersistentSecretMarkStoreStatus.InvalidStorage, invalid.Status);
		Assert.True(valid.Succeeded);
		Assert.Empty(valid.Snapshot!.Marks);
	}

	[Fact]
	public async Task ExcessivePersistentMarkStates_AreRejectedWithoutInvalidatingNeighboringProject()
	{
		using var workspace = new TemporaryDirectory();
		var noisyProject = workspace.CreateFolder("noisy");
		var validProject = workspace.CreateFolder("valid");
		var appData = workspace.CreateFolder("app-data");
		var states = new JsonArray();
		for (var index = 0;
		     index <= ProjectProfileStorageLimits.MaximumPersistentMarkStatesPerProject;
		     index++)
		{
			states.Add(CreateState(index.ToString("x12", CultureInfo.InvariantCulture), 8, null));
		}
		WriteMarkStore(
			appData,
			new JsonObject
			{
				[noisyProject] = CreateMarkProject(states),
				[validProject] = CreateMarkProject(new JsonArray())
			});
		var store = new ProjectProfileStore(() => appData);

		var noisy = await store.LoadMarksAsync(noisyProject, TestContext.Current.CancellationToken);
		var valid = await store.LoadMarksAsync(validProject, TestContext.Current.CancellationToken);

		Assert.Equal(PersistentSecretMarkStoreStatus.InvalidStorage, noisy.Status);
		Assert.True(valid.Succeeded);
		Assert.Empty(valid.Snapshot!.Marks);
	}

	[Fact]
	public void SessionReplacement_DefensivelyNormalizesMarksAndRejectsExcessiveValidInput()
	{
		using var session = new SecretRedactionSession(new EmptySecretDetector());
		var oversizedKey = new string('k', SecretInspectionLimits.MaximumPersistentMarkKeyLength + 1);
		session.ReplaceMarkedSecrets(
		[
			new MarkedSecretProfileEntry(null!, "INVALID", 8),
			new MarkedSecretProfileEntry("AABBCCDDEEFF", oversizedKey, 8),
			new MarkedSecretProfileEntry("AABBCCDDEEFF", "SECOND", 9)
		]);

		var marks = session.GetMarkedSecrets().OrderBy(static mark => mark.Length).ToArray();
		Assert.Equal(2, marks.Length);
		Assert.Equal([8, 9], marks.Select(static mark => mark.Length).ToArray());
		Assert.All(marks, static mark => Assert.Equal("aabbccddeeff", mark.H));
		Assert.Null(marks[0].Key);
		Assert.Equal("SECOND", marks[1].Key);
		Assert.Throws<ArgumentException>(() =>
			session.AddMarkedSecret(new MarkedSecretProfileEntry("invalid", null, 8)));

		var excessive = Enumerable
			.Range(0, SecretInspectionLimits.MaximumPersistentMarksPerProject + 1)
			.Select(static index => new MarkedSecretProfileEntry(
				index.ToString("x12", CultureInfo.InvariantCulture),
				null,
				8));
		Assert.Throws<SecretInspectionBudgetExceededException>(() =>
			session.ReplaceMarkedSecrets(excessive));
	}

	private static JsonObject CreateSelectionProfile(JsonArray marks) => new()
	{
		["selectedRootFolders"] = new JsonArray(),
		["selectedExtensions"] = new JsonArray(".cs"),
		["selectedIgnoreOptions"] = new JsonArray(),
		["rootFolderStates"] = new JsonObject(),
		["extensionStates"] = new JsonObject { [".cs"] = true },
		["ignoreOptionStates"] = new JsonObject(),
		["selectedPaths"] = new JsonArray(),
		["markedSecrets"] = marks,
		["updatedUtc"] = DateTimeOffset.UtcNow
	};

	private static JsonObject CreateLegacyMark(string? hash, string? key, int length) => new()
	{
		["h"] = hash,
		["key"] = key,
		["length"] = length
	};

	private static JsonObject CreateState(string? hash, int length, string? key) => new()
	{
		["hash"] = hash,
		["length"] = length,
		["key"] = key,
		["removed"] = false,
		["issuedUtcTicks"] = DateTime.UtcNow.Ticks,
		["operationId"] = Guid.NewGuid()
	};

	private static void WriteSelectionStore(string appData, JsonObject profiles)
	{
		var directory = Path.Combine(appData, "DevProjex");
		Directory.CreateDirectory(directory);
		File.WriteAllText(
			Path.Combine(directory, "project-profiles.json"),
			new JsonObject
			{
				["schemaVersion"] = 3,
				["profiles"] = profiles
			}.ToJsonString());
	}

	private static void WriteMarkStore(string appData, string project, JsonArray states)
		=> WriteMarkStore(
			appData,
			new JsonObject
			{
				[project] = CreateMarkProject(states)
			});

	private static JsonObject CreateMarkProject(JsonArray states) => new()
	{
		["revision"] = 1,
		["states"] = states
	};

	private static void WriteMarkStore(string appData, JsonObject projects)
	{
		var directory = Path.Combine(appData, "DevProjex");
		Directory.CreateDirectory(directory);
		File.WriteAllText(
			Path.Combine(directory, "project-secret-marks.json"),
			new JsonObject
			{
				["schemaVersion"] = 1,
				["projects"] = projects
			}.ToJsonString());
	}

	private sealed class EmptySecretDetector : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) => [];
	}
}
