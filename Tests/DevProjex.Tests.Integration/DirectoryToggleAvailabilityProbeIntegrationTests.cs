namespace DevProjex.Tests.Integration;

public sealed class DirectoryToggleAvailabilityProbeIntegrationTests
{
	[Fact]
	public void IgnoreSectionSnapshot_DotRootHiddenByDotFolders_KeepsToggleAvailableWithoutLeakingExtensions()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/app.py", "print('ok')");
		temp.CreateFile(".idea/workspace.xml", "<project />");

		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var rules = CreateBaseRules() with
		{
			IgnoreDotFolders = true,
			IgnoreEmptyFolders = true
		};

		var snapshot = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			["src"],
			BuildExtensionDiscoveryRules(rules),
			rules,
			effectiveAllowedExtensions: null,
			includeDirectoryToggleProbeRoots: true);

		Assert.Equal(0, snapshot.Value.RawIgnoreOptionCounts.DotFolders);
		Assert.Equal(1, snapshot.Value.EffectiveIgnoreOptionCounts.DotFolders);
		Assert.Contains(".py", snapshot.Value.Extensions);
		Assert.DoesNotContain(".xml", snapshot.Value.Extensions);
	}

	[Fact]
	public void IgnoreSectionSnapshot_ProbeDisabled_DoesNotCountUnselectedDotRoot()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/app.py", "print('ok')");
		temp.CreateFile(".idea/workspace.xml", "<project />");

		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var rules = CreateBaseRules() with
		{
			IgnoreDotFolders = true,
			IgnoreEmptyFolders = true
		};

		var snapshot = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			["src"],
			BuildExtensionDiscoveryRules(rules),
			rules,
			effectiveAllowedExtensions: null);

		Assert.Equal(0, snapshot.Value.RawIgnoreOptionCounts.DotFolders);
		Assert.Equal(0, snapshot.Value.EffectiveIgnoreOptionCounts.DotFolders);
		Assert.Contains(".py", snapshot.Value.Extensions);
		Assert.DoesNotContain(".xml", snapshot.Value.Extensions);
	}

	[Fact]
	public void IgnoreSectionSnapshot_DotRootHiddenWithUnselectedExtension_KeepsToggleAvailableWithoutLeakingExtension()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/app.py", "print('ok')");
		temp.CreateFile(".idea/workspace.xml", "<project />");

		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var rules = CreateBaseRules() with
		{
			IgnoreDotFolders = true,
			IgnoreEmptyFolders = true
		};

		var snapshot = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			["src"],
			BuildExtensionDiscoveryRules(rules),
			rules,
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".py" },
			includeDirectoryToggleProbeRoots: true);

		Assert.Equal(0, snapshot.Value.RawIgnoreOptionCounts.DotFolders);
		Assert.Equal(1, snapshot.Value.EffectiveIgnoreOptionCounts.DotFolders);
		Assert.Contains(".py", snapshot.Value.Extensions);
		Assert.DoesNotContain(".xml", snapshot.Value.Extensions);
	}

	[Fact]
	public void EffectiveCounts_DotRootHiddenByDotFolders_KeepsToggleAvailableForExplicitCountPipeline()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/app.py", "print('ok')");
		temp.CreateFile(".idea/workspace.xml", "<project />");

		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var rules = CreateBaseRules() with
		{
			IgnoreDotFolders = true,
			IgnoreEmptyFolders = true
		};

		var counts = scanOptions.GetEffectiveIgnoreOptionCountsForRootFolders(
			temp.Path,
			["src"],
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".py", ".xml" },
			rules,
			IgnoreOptionCounts.Empty,
			includeDirectoryToggleProbeRoots: true);

		Assert.Equal(1, counts.Value.DotFolders);
	}

	[Fact]
	public void IgnoreSectionSnapshot_DotRootWithOnlyFilteredNoise_DoesNotKeepDotFoldersVisible()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/app.py", "print('ok')");
		temp.CreateFile(".cache/nested/README", "extensionless noise");

		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var rules = CreateBaseRules() with
		{
			IgnoreDotFolders = true,
			IgnoreExtensionlessFiles = true
		};

		var snapshot = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			["src"],
			BuildExtensionDiscoveryRules(rules),
			rules,
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".py" },
			includeDirectoryToggleProbeRoots: true);

		Assert.Equal(0, snapshot.Value.EffectiveIgnoreOptionCounts.DotFolders);
		Assert.Equal(0, snapshot.Value.EffectiveIgnoreOptionCounts.ExtensionlessFiles);
		Assert.Contains(".py", snapshot.Value.Extensions);
		Assert.DoesNotContain("README", snapshot.Value.Extensions);
	}

	[Fact]
	public void IgnoreSectionSnapshot_GitIgnoredDotRoot_DoesNotKeepDotFoldersVisible()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", ".idea/");
		temp.CreateFile("src/app.py", "print('ok')");
		temp.CreateFile(".idea/workspace.xml", "<project />");

		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var rules = CreateBaseRules() with
		{
			UseGitIgnore = true,
			GitIgnoreMatcher = GitIgnoreMatcher.Build(temp.Path, [".idea/"]),
			IgnoreDotFolders = true,
			IgnoreEmptyFolders = true
		};

		var snapshot = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			["src"],
			BuildExtensionDiscoveryRules(rules),
			rules,
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".py", ".xml" },
			includeDirectoryToggleProbeRoots: true);

		Assert.Equal(0, snapshot.Value.EffectiveIgnoreOptionCounts.DotFolders);
		Assert.DoesNotContain(".xml", snapshot.Value.Extensions);
	}

	[Fact]
	public void IgnoreSectionSnapshot_SmartIgnoredDotRoot_DoesNotKeepDotFoldersVisible()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/app.py", "print('ok')");
		temp.CreateFile(".venv/pyvenv.cfg", "home = python");

		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var rules = CreateBaseRules() with
		{
			UseSmartIgnore = true,
			SmartIgnoredFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".venv" },
			IgnoreDotFolders = true,
			IgnoreEmptyFolders = true
		};

		var snapshot = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			["src"],
			BuildExtensionDiscoveryRules(rules),
			rules,
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".py", ".cfg" },
			includeDirectoryToggleProbeRoots: true);

		Assert.Equal(0, snapshot.Value.EffectiveIgnoreOptionCounts.DotFolders);
		Assert.DoesNotContain(".cfg", snapshot.Value.Extensions);
	}

	private static IgnoreRules CreateBaseRules() => new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
		SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

	private static IgnoreRules BuildExtensionDiscoveryRules(IgnoreRules effectiveRules)
	{
		return effectiveRules with
		{
			IgnoreHiddenFiles = false,
			IgnoreDotFiles = false,
			IgnoreEmptyFiles = false,
			IgnoreExtensionlessFiles = false
		};
	}
}
