using DevProjex.Application.Context;

namespace DevProjex.Tests.Unit;

public sealed class PortableProjectProfileServiceTests
{
	[Fact]
	public async Task LoadAsyncRejectsProfileThatGrowsPastDocumentLimit()
	{
		using var workspace = new TemporaryDirectory();
		var path = Path.Combine(workspace.Path, "oversized.json");
		var prefix = """
		             {
		               "schemaVersion": 1,
		               "kind": "devprojex-profile",
		               "selection": { "gitMode": "none", "exclusions": [] }
		             }
		             """;
		await File.WriteAllTextAsync(
			path,
			prefix + new string(' ', checked((int)PortableProjectProfileService.MaximumDocumentBytes)),
			TestContext.Current.CancellationToken);

		var exception = await Assert.ThrowsAsync<PortableProjectProfileException>(() =>
			new PortableProjectProfileService().LoadAsync(
				path,
				TestContext.Current.CancellationToken));

		Assert.Equal("DPX-CLI-PROFILE-INVALID", exception.Code);
		Assert.IsType<IOException>(exception.InnerException);
	}

	[Fact]
	public async Task SelectedPathsRoundTripSignificantWhitespace()
	{
		using var workspace = new TemporaryDirectory();
		var sourceRoot = workspace.CreateFolder("project");
		var destination = Path.Combine(workspace.Path, "portable.json");
		string[] selectedPaths = [" ", " folder/file .cs "];
		string[] roots = [" ", " source "];
		string[] extensions = [". x", ".cs "];
		var service = new PortableProjectProfileService();

		await service.SaveAsync(
			sourceRoot,
			destination,
			new ProjectSelectionSpec(
				Roots: roots,
				Extensions: extensions,
				SelectedPaths: selectedPaths,
				GitMode: GitFilteringMode.None,
				Exclusions: []),
			overwrite: false,
			TestContext.Current.CancellationToken);

		var loaded = await service.LoadAsync(
			destination,
			TestContext.Current.CancellationToken);

		Assert.Equal(
			selectedPaths.OrderBy(static path => path, PathComparer.Default),
			loaded.SelectedPaths);
		Assert.Equal(
			roots.OrderBy(static path => path, PathComparer.Default),
			loaded.Roots);
		Assert.Equal(
			extensions.OrderBy(static extension => extension, StringComparer.OrdinalIgnoreCase),
			loaded.Extensions);
	}

	[Fact]
	public async Task SaveAsyncMapsSharedDestinationConflictToProfileContract()
	{
		using var workspace = new TemporaryDirectory();
		var sourceRoot = workspace.CreateFolder("project");
		var profileDirectory = workspace.CreateFolder("profiles");
		var destination = Path.Combine(profileDirectory, "portable.json");
		const string competingContent = "created by another process";
		File.WriteAllText(destination, competingContent);
		var service = new PortableProjectProfileService();

		var exception = await Assert.ThrowsAsync<PortableProjectProfileException>(() =>
			service.SaveAsync(
				sourceRoot,
				destination,
				new ProjectSelectionSpec(
					GitMode: GitFilteringMode.None,
					Exclusions: []),
				overwrite: false,
				TestContext.Current.CancellationToken));

		Assert.Equal("DPX-PROFILE-DESTINATION-EXISTS", exception.Code);
		Assert.Equal(competingContent, await File.ReadAllTextAsync(
			destination,
			TestContext.Current.CancellationToken));
		Assert.Empty(Directory.EnumerateFiles(
			profileDirectory,
			".portable.json.*.tmp",
			SearchOption.TopDirectoryOnly));
	}

	[Fact]
	public async Task SaveAsyncReportsStableRequestedAliasAfterPhysicalCommit()
	{
		using var workspace = new TemporaryDirectory();
		var sourceRoot = workspace.CreateFolder("project");
		var profileDirectory = workspace.CreateFolder("profiles");
		var alias = Path.Combine(workspace.Path, "profile-alias");
		try
		{
			Directory.CreateSymbolicLink(alias, profileDirectory);
		}
		catch (Exception exception) when (exception is
			       UnauthorizedAccessException or
			       IOException or
			       PlatformNotSupportedException)
		{
			Assert.Skip(
				$"Directory symbolic links are unavailable: {exception.GetType().Name}.");
		}

		try
		{
			var requestedPath = Path.Combine(alias, "portable.json");
			var service = new PortableProjectProfileService();

			var writtenPath = await service.SaveAsync(
				sourceRoot,
				requestedPath,
				new ProjectSelectionSpec(
					GitMode: GitFilteringMode.None,
					Exclusions: []),
				overwrite: false,
				TestContext.Current.CancellationToken);

			Assert.Equal(Path.GetFullPath(requestedPath), writtenPath);
			Assert.True(File.Exists(Path.Combine(profileDirectory, "portable.json")));
		}
		finally
		{
			Directory.Delete(alias);
		}
	}

	[Fact]
	public async Task SaveAsyncWritesHideSecretsSeparatelyFromPathExclusions()
	{
		using var workspace = new TemporaryDirectory();
		var sourceRoot = workspace.CreateFolder("project");
		var destination = Path.Combine(workspace.Path, "portable.json");
		var service = new PortableProjectProfileService();

		await service.SaveAsync(
			sourceRoot,
			destination,
			new ProjectSelectionSpec(
				GitMode: GitFilteringMode.None,
				Exclusions: [ProjectExclusion.SmartIgnore, ProjectExclusion.HideSecrets],
				HideSecrets: true),
			overwrite: false,
			TestContext.Current.CancellationToken);

		using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
			destination,
			TestContext.Current.CancellationToken));
		var selection = document.RootElement.GetProperty("selection");
		Assert.True(selection.GetProperty("hideSecrets").GetBoolean());
		Assert.Equal(
			["smart-ignore"],
			selection.GetProperty("exclusions")
				.EnumerateArray()
				.Select(static value => value.GetString()));
	}

	[Fact]
	public async Task HidePrivateDataRoundTripsWithoutLegacyExclusionToken()
	{
		using var workspace = new TemporaryDirectory();
		var sourceRoot = workspace.CreateFolder("project");
		var destination = Path.Combine(workspace.Path, "portable.json");
		var service = new PortableProjectProfileService();

		await service.SaveAsync(
			sourceRoot,
			destination,
			new ProjectSelectionSpec(
				GitMode: GitFilteringMode.None,
				Exclusions: [],
				HidePrivateData: true),
			overwrite: false,
			TestContext.Current.CancellationToken);

		using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(
			       destination,
			       TestContext.Current.CancellationToken)))
		{
			var selection = document.RootElement.GetProperty("selection");
			Assert.True(selection.GetProperty("hidePrivateData").GetBoolean());
			Assert.Empty(selection.GetProperty("exclusions").EnumerateArray());
		}

		var loaded = await service.LoadAsync(destination, TestContext.Current.CancellationToken);
		Assert.True(loaded.HidePrivateData);
	}

	[Fact]
	public async Task ProfileWithoutHidePrivateDataLoadsWithPrivacyRedactionDisabled()
	{
		using var workspace = new TemporaryDirectory();
		var path = Path.Combine(workspace.Path, "profile.json");
		await File.WriteAllTextAsync(
			path,
			"""
			{
			  "schemaVersion": 1,
			  "kind": "devprojex-profile",
			  "selection": {
			    "gitMode": "none",
			    "exclusions": []
			  }
			}
			""",
			TestContext.Current.CancellationToken);

		var selection = await new PortableProjectProfileService().LoadAsync(
			path,
			TestContext.Current.CancellationToken);

		Assert.False(selection.HidePrivateData);
	}

	[Fact]
	public async Task CompressCodeRoundTripsAsAnIndependentContentTransformation()
	{
		using var workspace = new TemporaryDirectory();
		var sourceRoot = workspace.CreateFolder("project");
		var destination = Path.Combine(workspace.Path, "portable.json");
		var service = new PortableProjectProfileService();

		await service.SaveAsync(
			sourceRoot,
			destination,
			new ProjectSelectionSpec(
				GitMode: GitFilteringMode.None,
				Exclusions: [],
				CompressCode: true),
			overwrite: false,
			TestContext.Current.CancellationToken);

		using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(
			       destination,
			       TestContext.Current.CancellationToken)))
		{
			Assert.True(document.RootElement
				.GetProperty("selection")
				.GetProperty("compressCode")
				.GetBoolean());
		}

		var loaded = await service.LoadAsync(destination, TestContext.Current.CancellationToken);
		Assert.True(loaded.CompressCode);
	}

	[Fact]
	public async Task ProfileWithoutCompressCodeLoadsWithCompressionDisabled()
	{
		using var workspace = new TemporaryDirectory();
		var path = Path.Combine(workspace.Path, "profile.json");
		await File.WriteAllTextAsync(
			path,
			"""
			{
			  "schemaVersion": 1,
			  "kind": "devprojex-profile",
			  "selection": {
			    "gitMode": "none",
			    "exclusions": []
			  }
			}
			""",
			TestContext.Current.CancellationToken);

		var selection = await new PortableProjectProfileService().LoadAsync(
			path,
			TestContext.Current.CancellationToken);

		Assert.False(selection.CompressCode);
	}

	[Fact]
	public async Task StripCommentsRoundTripsAsAnIndependentContentTransformation()
	{
		using var workspace = new TemporaryDirectory();
		var sourceRoot = workspace.CreateFolder("project");
		var destination = Path.Combine(workspace.Path, "portable.json");
		var service = new PortableProjectProfileService();

		await service.SaveAsync(
			sourceRoot,
			destination,
			new ProjectSelectionSpec(
				GitMode: GitFilteringMode.None,
				Exclusions: [],
				StripComments: true),
			overwrite: false,
			TestContext.Current.CancellationToken);

		using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(
		       destination,
		       TestContext.Current.CancellationToken)))
		{
			Assert.True(document.RootElement
				.GetProperty("selection")
				.GetProperty("stripComments")
				.GetBoolean());
		}

		var loaded = await service.LoadAsync(destination, TestContext.Current.CancellationToken);
		Assert.True(loaded.StripComments);
		Assert.False(loaded.CompressCode);
	}

	[Fact]
	public async Task ProfileWithoutStripCommentsLoadsWithCommentRemovalDisabled()
	{
		using var workspace = new TemporaryDirectory();
		var path = Path.Combine(workspace.Path, "profile.json");
		await File.WriteAllTextAsync(
			path,
			"""
			{
			  "schemaVersion": 1,
			  "kind": "devprojex-profile",
			  "selection": {
			    "gitMode": "none",
			    "exclusions": []
			  }
			}
			""",
			TestContext.Current.CancellationToken);

		var selection = await new PortableProjectProfileService().LoadAsync(
			path,
			TestContext.Current.CancellationToken);

		Assert.False(selection.StripComments);
	}

	[Fact]
	public async Task StripBlankLinesRoundTripsAsAnIndependentContentTransformation()
	{
		using var workspace = new TemporaryDirectory();
		var sourceRoot = workspace.CreateFolder("project");
		var destination = Path.Combine(workspace.Path, "portable.json");
		var service = new PortableProjectProfileService();

		await service.SaveAsync(
			sourceRoot,
			destination,
			new ProjectSelectionSpec(
				GitMode: GitFilteringMode.None,
				Exclusions: [],
				StripBlankLines: true),
			overwrite: false,
			TestContext.Current.CancellationToken);

		using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(
		       destination,
		       TestContext.Current.CancellationToken)))
		{
			Assert.True(document.RootElement
				.GetProperty("selection")
				.GetProperty("stripBlankLines")
				.GetBoolean());
		}

		var loaded = await service.LoadAsync(destination, TestContext.Current.CancellationToken);
		Assert.True(loaded.StripBlankLines);
		Assert.False(loaded.CompressCode);
		Assert.False(loaded.StripComments);
	}

	[Fact]
	public async Task ProfileWithoutStripBlankLinesLoadsWithBlankLineRemovalDisabled()
	{
		using var workspace = new TemporaryDirectory();
		var path = Path.Combine(workspace.Path, "profile.json");
		await File.WriteAllTextAsync(
			path,
			"""
			{
			  "schemaVersion": 1,
			  "kind": "devprojex-profile",
			  "selection": {
			    "gitMode": "none",
			    "exclusions": []
			  }
			}
			""",
			TestContext.Current.CancellationToken);

		var selection = await new PortableProjectProfileService().LoadAsync(
			path,
			TestContext.Current.CancellationToken);

		Assert.False(selection.StripBlankLines);
	}

	[Fact]
	public async Task LoadAsyncMigratesLegacyHideSecretsExclusion()
	{
		using var workspace = new TemporaryDirectory();
		var path = Path.Combine(workspace.Path, "legacy.json");
		await File.WriteAllTextAsync(
			path,
			"""
			{
			  "schemaVersion": 1,
			  "kind": "devprojex-profile",
			  "selection": {
			    "gitMode": "none",
			    "exclusions": ["smart-ignore", "hide-secrets"]
			  }
			}
			""",
			TestContext.Current.CancellationToken);

		var selection = await new PortableProjectProfileService().LoadAsync(
			path,
			TestContext.Current.CancellationToken);

		Assert.True(selection.HideSecrets);
		Assert.Equal([ProjectExclusion.SmartIgnore], selection.Exclusions);
	}

	[Fact]
	public async Task LoadAsyncExplicitHideSecretsValueOverridesLegacyToken()
	{
		using var workspace = new TemporaryDirectory();
		var path = Path.Combine(workspace.Path, "explicit.json");
		await File.WriteAllTextAsync(
			path,
			"""
			{
			  "schemaVersion": 1,
			  "kind": "devprojex-profile",
			  "selection": {
			    "gitMode": "none",
			    "exclusions": ["hide-secrets"],
			    "hideSecrets": false
			  }
			}
			""",
			TestContext.Current.CancellationToken);

		var selection = await new PortableProjectProfileService().LoadAsync(
			path,
			TestContext.Current.CancellationToken);

		Assert.False(selection.HideSecrets);
		Assert.Empty(selection.Exclusions!);
	}
}
