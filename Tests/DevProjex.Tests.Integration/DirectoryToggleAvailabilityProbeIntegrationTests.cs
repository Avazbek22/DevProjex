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
			includeDirectoryToggleProbeRoots: true, cancellationToken: TestContext.Current.CancellationToken);

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
			effectiveAllowedExtensions: null, cancellationToken: TestContext.Current.CancellationToken);

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
			includeDirectoryToggleProbeRoots: true, cancellationToken: TestContext.Current.CancellationToken);

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
			includeDirectoryToggleProbeRoots: true, cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(1, counts.Value.DotFolders);
	}

	[Fact]
	public void IgnoreSectionSnapshot_UnixDotRootKeepsDotFoldersAvailableWhenHiddenFoldersAlsoMatches()
	{
		if (OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/app.py", "print('ok')");
		temp.CreateFile(".idea/workspace.xml", "<project />");

		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var rules = CreateBaseRules() with
		{
			IgnoreHiddenFolders = true,
			IgnoreDotFolders = true,
			IgnoreEmptyFolders = true
		};

		var snapshot = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			["src"],
			BuildExtensionDiscoveryRules(rules),
			rules,
			effectiveAllowedExtensions: null,
			includeDirectoryToggleProbeRoots: true, cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(1, snapshot.Value.EffectiveIgnoreOptionCounts.DotFolders);
		Assert.Equal(0, snapshot.Value.EffectiveIgnoreOptionCounts.HiddenFolders);
		Assert.DoesNotContain(".xml", snapshot.Value.Extensions);

		var dotFoldersOff = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			["src"],
			BuildExtensionDiscoveryRules(rules with { IgnoreDotFolders = false }),
			rules with { IgnoreDotFolders = false },
			effectiveAllowedExtensions: null,
			includeDirectoryToggleProbeRoots: true, cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(1, dotFoldersOff.Value.EffectiveIgnoreOptionCounts.DotFolders);
		Assert.Equal(0, dotFoldersOff.Value.EffectiveIgnoreOptionCounts.HiddenFolders);
	}

	[Fact]
	public void ScannerAndTree_UnixDotEntriesRemainVisibleWhenDotTogglesAreOffAndHiddenTogglesAreOn()
	{
		if (OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/app.py", "print('ok')");
		temp.CreateFile(".idea/workspace.xml", "<project />");
		temp.CreateFile(".env", "APP_ENV=dev");

		var rules = CreateBaseRules() with
		{
			IgnoreHiddenFolders = true,
			IgnoreHiddenFiles = true,
			IgnoreDotFolders = false,
			IgnoreDotFiles = false
		};

		var scanner = new FileSystemScanner();
		var rootFolders = scanner.GetRootFolderNames(temp.Path, rules, cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(".idea", rootFolders.Value);

		var rootFiles = scanner.GetRootFileExtensions(temp.Path, rules, cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(".env", rootFiles.Value);

		var tree = new TreeBuilder().Build(
			temp.Path,
			new TreeFilterOptions(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".env", ".py", ".xml" },
				new HashSet<string>(PathComparer.Default) { ".idea", "src" },
				rules), cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(tree.Root.Children, child => child.Name == ".idea");
		Assert.Contains(tree.Root.Children, child => child.Name == ".env");
	}

	[Fact]
	public void IgnoreSectionSnapshot_WindowsHiddenDotRoot_ExposesHiddenFoldersOnlyWhenDotFoldersNoLongerHidesIt()
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/app.cs", "class App {}");
		temp.CreateFile(".idea/workspace.xml", "<project />");
		temp.CreateFile(".git/config.txt", "[core]\n");
		MarkHidden(Path.Combine(temp.Path, ".git"));

		var bothDirectoryRulesOn = BuildDirectoryToggleSnapshot(
			temp.Path,
			ignoreHiddenFolders: true,
			ignoreDotFolders: true);
		Assert.Equal(0, bothDirectoryRulesOn.Value.EffectiveIgnoreOptionCounts.HiddenFolders);
		Assert.Equal(2, bothDirectoryRulesOn.Value.EffectiveIgnoreOptionCounts.DotFolders);

		var dotFoldersOff = BuildDirectoryToggleSnapshot(
			temp.Path,
			ignoreHiddenFolders: true,
			ignoreDotFolders: false);
		Assert.Equal(1, dotFoldersOff.Value.EffectiveIgnoreOptionCounts.HiddenFolders);
		Assert.Equal(1, dotFoldersOff.Value.EffectiveIgnoreOptionCounts.DotFolders);

		var hiddenFoldersOff = BuildDirectoryToggleSnapshot(
			temp.Path,
			ignoreHiddenFolders: false,
			ignoreDotFolders: true);
		Assert.Equal(0, hiddenFoldersOff.Value.EffectiveIgnoreOptionCounts.HiddenFolders);
		Assert.Equal(2, hiddenFoldersOff.Value.EffectiveIgnoreOptionCounts.DotFolders);
	}

	[Fact]
	public void IgnoreSectionSnapshot_WindowsHiddenDotRootWithFilteredContent_DoesNotExposeHiddenFolders()
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/app.cs", "class App {}");
		temp.CreateFile(".idea/workspace.xml", "<project />");
		temp.CreateFile(".git/config", "[core]\n");
		MarkHidden(Path.Combine(temp.Path, ".git"));

		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var rules = CreateBaseRules() with
		{
			IgnoreHiddenFolders = true,
			IgnoreDotFolders = false,
			IgnoreExtensionlessFiles = true
		};

		var snapshot = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			[".idea", "src"],
			BuildExtensionDiscoveryRules(rules),
			rules,
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".xml" },
			includeDirectoryToggleProbeRoots: true, cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(0, snapshot.Value.EffectiveIgnoreOptionCounts.HiddenFolders);
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
			includeDirectoryToggleProbeRoots: true, cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(0, snapshot.Value.EffectiveIgnoreOptionCounts.DotFolders);
		Assert.Equal(0, snapshot.Value.EffectiveIgnoreOptionCounts.ExtensionlessFiles);
		Assert.Contains(".py", snapshot.Value.Extensions);
		Assert.DoesNotContain("README", snapshot.Value.Extensions);
	}

	[Fact]
	public void IgnoreSectionSnapshot_EmptyUnselectedDotRoot_DoesNotKeepDotFoldersVisible()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/app.py", "print('ok')");
		temp.CreateDirectory(".empty-cache");

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
			includeDirectoryToggleProbeRoots: true, cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(0, snapshot.Value.EffectiveIgnoreOptionCounts.DotFolders);
		Assert.Contains(".py", snapshot.Value.Extensions);
	}

	[Fact]
	public void IgnoreSectionSnapshot_DotFolderMaskedByEmptyParent_AppearsOnlyAfterEmptyFolderMaskIsRemoved()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/app.py", "print('ok')");
		temp.CreateFile("generated/.cache/settings.json", "{}");

		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var maskedRules = CreateBaseRules() with
		{
			IgnoreDotFolders = true,
			IgnoreEmptyFolders = true
		};

		var masked = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			["src"],
			BuildExtensionDiscoveryRules(maskedRules),
			maskedRules,
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".py", ".json" },
			includeDirectoryToggleProbeRoots: true, cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(0, masked.Value.EffectiveIgnoreOptionCounts.DotFolders);
		Assert.Equal(0, masked.Value.EffectiveIgnoreOptionCounts.EmptyFolders);

		var unmaskedRules = maskedRules with { IgnoreEmptyFolders = false };
		var unmasked = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			["src", "generated"],
			BuildExtensionDiscoveryRules(unmaskedRules),
			unmaskedRules,
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".py", ".json" },
			includeDirectoryToggleProbeRoots: true, cancellationToken: TestContext.Current.CancellationToken);

		// EmptyFolders can mask a parent before nested dot folders participate in the
		// effective tree. Once that parent is selected, both toggles are real decisions:
		// DotFolders hides .cache, then EmptyFolders can hide the now-empty parent.
		Assert.Equal(1, unmasked.Value.EffectiveIgnoreOptionCounts.DotFolders);
		Assert.Equal(1, unmasked.Value.EffectiveIgnoreOptionCounts.EmptyFolders);
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
			includeDirectoryToggleProbeRoots: true, cancellationToken: TestContext.Current.CancellationToken);

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
			includeDirectoryToggleProbeRoots: true, cancellationToken: TestContext.Current.CancellationToken);

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

	private static ScanResult<IgnoreSectionScanData> BuildDirectoryToggleSnapshot(
		string rootPath,
		bool ignoreHiddenFolders,
		bool ignoreDotFolders)
	{
		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var rules = CreateBaseRules() with
		{
			IgnoreHiddenFolders = ignoreHiddenFolders,
			IgnoreDotFolders = ignoreDotFolders,
			IgnoreEmptyFolders = true
		};

		return scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			rootPath,
			["src"],
			BuildExtensionDiscoveryRules(rules),
			rules,
			effectiveAllowedExtensions: null,
			includeDirectoryToggleProbeRoots: true);
	}

	private static void MarkHidden(string path)
	{
		var attributes = File.GetAttributes(path);
		File.SetAttributes(path, attributes | FileAttributes.Hidden);
	}
}
