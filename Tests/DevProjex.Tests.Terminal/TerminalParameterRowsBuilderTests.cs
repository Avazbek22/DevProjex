using System.Collections.ObjectModel;
using DevProjex.Application.Secrets;
using DevProjex.Application.Presentation;
using Terminal.Gui.Input;
using Terminal.Gui.Text;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalParameterRowsBuilderTests
{
	[Theory]
	[InlineData(true, "[x] .cs")]
	[InlineData(false, "[ ] .cs")]
	public void RowsStartAtTheMiniPanelContentEdge(bool isSelected, string expected)
	{
		var row = new TerminalParameterRow(
			"row",
			TerminalParameterRowKind.Extension,
			".cs",
			isSelected);

		Assert.Equal(expected, row.ToString());
		Assert.False(row.ToString().StartsWith(' '));
	}

	[Fact]
	public void RowDisplayText_IsReusedAcrossRepeatedRenders()
	{
		var row = new TerminalParameterRow(
			"row",
			TerminalParameterRowKind.Extension,
			".cs",
			IsSelected: true);
		var expected = row.ToString();
		var checksum = 0;
		var allSame = true;

		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		for (var iteration = 0; iteration < 10_000; iteration++)
		{
			var rendered = row.ToString();
			allSame &= ReferenceEquals(expected, rendered);
			checksum += rendered.Length;
		}
		var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

		Assert.True(allSame);
		Assert.Equal(expected.Length * 10_000, checksum);
		Assert.InRange(allocatedBytes, 0, 256);

		var changed = row with { IsSelected = false, Label = ".fs" };
		Assert.Equal("[ ] .fs", changed.ToString());
	}

	[Fact]
	public void ContentRowsExposeAllTransformationsAndSeparateRedactionCounters()
	{
		var builder = CreateBuilder();
		var selection = ProjectSelectionSpec.Standard with
		{
			HideSecrets = true,
			HidePrivateData = true,
			CompressCode = true,
			StripComments = true,
			StripBlankLines = true
		};
		var snapshot = new SecretRedactionSnapshot(
			"selection",
			DetectedCount: 8,
			RedactedCount: 6,
			PrivateDataDetectedCount: 3,
			PrivateDataRedactedCount: 2);

		var rows = builder.BuildContent(CreatePlan(selection), snapshot);

		Assert.Equal(5, rows.Count);
		Assert.All(rows, static row =>
		{
			Assert.Equal(TerminalParameterRowKind.ContentTransformation, row.Kind);
			Assert.True(row.IsSelected);
		});
		Assert.Contains(rows, static row =>
			row.ContentTransformation == IgnoreOptionId.HideSecrets &&
			row.Label == "HideSecrets:5/4");
		Assert.Contains(rows, static row =>
			row.ContentTransformation == IgnoreOptionId.HidePrivateData &&
			row.Label == "HidePrivateData:3/2");
	}

	[Fact]
	public void ExclusionRowsKeepMutuallyExclusiveGitModesWithoutAggregateRow()
	{
		var builder = CreateBuilder();
		var rows = builder.BuildExclusions(CreatePlan(ProjectSelectionSpec.Standard));

		Assert.DoesNotContain(rows, static row => row.Kind == TerminalParameterRowKind.ToggleAllExclusions);
		Assert.Equal(
			[
				GitFilteringMode.None,
				GitFilteringMode.RespectGitIgnore,
				GitFilteringMode.TrackedFilesOnly,
				GitFilteringMode.Staged,
				GitFilteringMode.Changes
			],
			rows.Take(5).Select(static row => row.GitMode));
		Assert.Equal([false, true, false, false, false], rows.Take(5).Select(static row => row.IsSelected));
		Assert.Equal("( ) Terminal.Tui.GitNone", rows[0].ToString());
		Assert.Equal("(•) Settings.Ignore.UseGitIgnore", rows[1].ToString());
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void MeasuredPlainFolderOmitsGitAxisRegardlessOfOtherExclusions(
		bool hasPathExclusion)
	{
		var plan = CreatePlan(
			ProjectSelectionSpec.Standard,
			hasIgnoreOptionCounts: true,
			ignoreOptionCounts: hasPathExclusion
				? new IgnoreOptionCounts(HiddenFiles: 1)
				: IgnoreOptionCounts.Empty) with
		{
			GitReadiness = new ProjectContextGitReadiness(
				GitFilteringMode.RespectGitIgnore,
				LoadedTrackedIndexCount: 0,
				IsReady: true)
		};

		var rows = CreateBuilder().BuildExclusions(plan);
		var counts = TerminalWorkspaceSession.CountExclusionAxis(rows);

		Assert.DoesNotContain(rows, static row => row.Kind == TerminalParameterRowKind.GitMode);
		Assert.Equal(hasPathExclusion ? (1, 1) : (0, 0), counts);
	}

	[Fact]
	public void MeasuredStandaloneGitIgnoreImpactKeepsGitAxisVisible()
	{
		var plan = CreatePlan(
			ProjectSelectionSpec.Standard,
			hasIgnoreOptionCounts: true,
			ignoreControllerImpactCounts: new IgnoreControllerImpactCounts(GitIgnore: 1)) with
		{
			GitReadiness = new ProjectContextGitReadiness(
				GitFilteringMode.RespectGitIgnore,
				LoadedTrackedIndexCount: 0,
				IsReady: true)
		};

		var rows = CreateBuilder().BuildExclusions(plan);

		Assert.Equal(5, rows.Count(static row => row.Kind == TerminalParameterRowKind.GitMode));
		Assert.All(
			rows.Where(static row => row.GitMode is GitFilteringMode.TrackedFilesOnly or
				GitFilteringMode.Staged or GitFilteringMode.Changes),
			static row => Assert.False(row.IsEnabled));
	}

	[Fact]
	public void RetainedTrackedModeStaysHiddenWithoutLosingItsIntent()
	{
		var plan = CreatePlan(
			ProjectSelectionSpec.Standard with
			{
				GitMode = GitFilteringMode.TrackedFilesOnly,
				Exclusions = [ProjectExclusion.HiddenFiles]
			},
			hasIgnoreOptionCounts: true,
			ignoreOptionCounts: new IgnoreOptionCounts(HiddenFiles: 1)) with
		{
			GitReadiness = new ProjectContextGitReadiness(
				GitFilteringMode.TrackedFilesOnly,
				LoadedTrackedIndexCount: 0,
				IsReady: false)
		};

		var rows = CreateBuilder().BuildExclusions(plan);
		var counts = TerminalWorkspaceSession.CountExclusionAxis(rows);

		Assert.DoesNotContain(rows, static row => row.Kind == TerminalParameterRowKind.GitMode);
		var hiddenFiles = Assert.Single(rows);
		Assert.Equal(ProjectExclusion.HiddenFiles, hiddenFiles.Exclusion);
		Assert.True(hiddenFiles.IsSelected);
		Assert.Equal((1, 1), counts);
		Assert.Equal(GitFilteringMode.TrackedFilesOnly, plan.Selection.GitMode);
	}

	[Theory]
	[InlineData(
		"C Content  X Exclusions  T Types  M Git mode  : Commands",
		"C Content  X Exclusions  T Types  : Commands")]
	[InlineData(
		"C Контент   X Исключения   T Типы   M Git-режим   Enter Изменить   : Команды",
		"C Контент   X Исключения   T Типы   Enter Изменить   : Команды")]
	public void InapplicableGitAxisIsRemovedFromLocalizedControlsFooter(
		string footer,
		string expected)
	{
		Assert.Equal(expected, TerminalWorkspaceSession.RemoveGitFilteringShortcut(footer));
	}

	[Fact]
	public void GitModesRemainEnabledForEmptyRepositoryBoundary()
	{
		var plan = CreatePlan(ProjectSelectionSpec.Standard) with
		{
			GitReadiness = new ProjectContextGitReadiness(
				GitFilteringMode.RespectGitIgnore,
				LoadedTrackedIndexCount: 0,
				IsReady: true,
				UnavailableTrackedIndexCount: 1)
		};

		var rows = CreateBuilder().BuildExclusions(plan);

		Assert.All(
			rows.Where(static row => row.GitMode is GitFilteringMode.TrackedFilesOnly or
				GitFilteringMode.Staged or GitFilteringMode.Changes),
			static row => Assert.True(row.IsEnabled));
	}

	[Fact]
	public void GitStateModesAreDisabledWhenGitCliIsUnavailableDespiteRepositoryBoundary()
	{
		var plan = CreatePlan(ProjectSelectionSpec.Standard) with
		{
			GitReadiness = new ProjectContextGitReadiness(
				GitFilteringMode.RespectGitIgnore,
				LoadedTrackedIndexCount: 1,
				IsReady: true)
		};

		var rows = CreateBuilder().BuildExclusions(plan, gitCliAvailable: false);

		Assert.All(
			rows.Where(static row => row.GitMode is GitFilteringMode.TrackedFilesOnly or
				GitFilteringMode.Staged or GitFilteringMode.Changes),
			static row => Assert.False(row.IsEnabled));
		Assert.All(
			rows.Where(static row => row.GitMode is GitFilteringMode.None or
				GitFilteringMode.RespectGitIgnore),
			static row => Assert.True(row.IsEnabled));
	}

	[Fact]
	public void GitModesUseThePhysicalWorktreeBoundaryBeforeTrackedIndexIsLoaded()
	{
		using var project = new TemporaryDirectory();
		project.WriteFile(".git", "gitdir: ../metadata/worktrees/project\n");
		var plan = CreatePlan(ProjectSelectionSpec.Standard) with
		{
			SourceRoot = project.Path,
			GitReadiness = new ProjectContextGitReadiness(
				GitFilteringMode.RespectGitIgnore,
				LoadedTrackedIndexCount: 0,
				IsReady: true)
		};

		var rows = CreateBuilder().BuildExclusions(plan);

		Assert.All(
			rows.Where(static row => row.GitMode is GitFilteringMode.TrackedFilesOnly or
				GitFilteringMode.Staged or GitFilteringMode.Changes),
			static row => Assert.True(row.IsEnabled));
	}

	[Fact]
	public void CollapsedExclusionCountTreatsGitModesAsOneAxis()
	{
		var rows = CreateBuilder().BuildExclusions(CreatePlan(ProjectSelectionSpec.Standard));

		var counts = TerminalWorkspaceSession.CountExclusionAxis(rows);

		var expected = ProjectPresentationCatalog.Exclusions.Count + 1;
		Assert.Equal((expected, expected), counts);
	}

	[Fact]
	public void ActiveDiffScopeAddsOneReadOnlyRadioRow()
	{
		var rows = CreateBuilder().BuildExclusions(CreatePlan(ProjectSelectionSpec.Standard with
		{
			GitMode = GitFilteringMode.Diff,
			GitDiffRange = "main..feature"
		}));

		var diff = Assert.Single(rows, static row => row.GitMode == GitFilteringMode.Diff);
		Assert.Equal("main..feature", diff.Value);
		Assert.True(diff.IsSelected);
		Assert.Equal("(•) diff: main..feature", diff.ToString());
	}

	[Fact]
	public void ExclusionRowsUseThePlansGuiEquivalentImpactCounts()
	{
		var counts = new IgnoreOptionCounts(
			HiddenFolders: 2,
			HiddenFiles: 3,
			DotFolders: 4,
			DotFiles: 5,
			EmptyFolders: 6,
			ExtensionlessFiles: 7,
			EmptyFiles: 8);
		var rows = CreateBuilder().BuildExclusions(CreatePlan(
			ProjectSelectionSpec.Standard,
			hasIgnoreOptionCounts: true,
			ignoreOptionCounts: counts,
			ignoreControllerImpactCounts: new IgnoreControllerImpactCounts(SmartIgnore: 1)));

		Assert.Equal(
			"Settings.Ignore.HiddenFolders (2)",
			rows.Single(static row => row.Exclusion == ProjectExclusion.HiddenFolders).Label);
		Assert.Equal(
			"Settings.Ignore.DotFiles (5)",
			rows.Single(static row => row.Exclusion == ProjectExclusion.DotFiles).Label);
		Assert.Equal(
			"Settings.Ignore.EmptyFiles (8)",
			rows.Single(static row => row.Exclusion == ProjectExclusion.EmptyFiles).Label);
		Assert.DoesNotContain(
			"(",
			rows.Single(static row => row.Exclusion == ProjectExclusion.SmartIgnore).Label,
			StringComparison.Ordinal);
	}

	[Fact]
	public void EmptyMomentaryScopeDoesNotOfferNoOpPathFilters()
	{
		var plan = CreatePlan(
			ProjectSelectionSpec.Standard with { GitMode = GitFilteringMode.Staged },
			availableExtensions: [],
			selectedExtensions: [],
			hasIgnoreOptionCounts: true);

		var rows = CreateBuilder().BuildExclusions(plan);
		var aggregate = CreateBuilder().BuildExclusionAggregate(plan);

		Assert.Equal(5, rows.Count);
		Assert.All(rows, static row => Assert.Equal(TerminalParameterRowKind.GitMode, row.Kind));
		Assert.DoesNotContain(rows, static row => row.Exclusion is not null);
		Assert.Equal("Settings.All", aggregate.Label);
		Assert.False(aggregate.IsSelected);
	}

	[Fact]
	public void MomentaryScopeOffersOnlyFiltersThatCanChangeItsFiles()
	{
		var plan = CreatePlan(
			ProjectSelectionSpec.Standard with { GitMode = GitFilteringMode.Changes },
			hasIgnoreOptionCounts: true,
			ignoreOptionCounts: new IgnoreOptionCounts(DotFolders: 2),
			ignoreControllerImpactCounts: new IgnoreControllerImpactCounts(SmartIgnore: 0));

		var rows = CreateBuilder().BuildExclusions(plan);
		var aggregate = CreateBuilder().BuildExclusionAggregate(plan);

		var dotFolders = Assert.Single(rows, static row => row.Exclusion == ProjectExclusion.DotFolders);
		Assert.Equal("Settings.Ignore.DotFolders (2)", dotFolders.Label);
		Assert.DoesNotContain(rows, static row => row.Exclusion == ProjectExclusion.DotFiles);
		Assert.DoesNotContain(rows, static row => row.Exclusion == ProjectExclusion.SmartIgnore);
		Assert.Equal("Settings.All (1)", aggregate.Label);
	}

	[Fact]
	public void ContentAggregateCountsAndTogglesAllFiveTransformations()
	{
		var builder = CreateBuilder();
		var allOn = ProjectSelectionSpec.Standard with
		{
			HideSecrets = true,
			HidePrivateData = true,
			CompressCode = true,
			StripComments = true,
			StripBlankLines = true
		};

		var selected = builder.BuildContentAggregate(allOn);
		var cleared = builder.BuildContentAggregate(ProjectSelectionSpec.Standard);

		Assert.Equal(TerminalParameterRowKind.ToggleAllContent, selected.Kind);
		Assert.Equal("Settings.All (5)", selected.Label);
		Assert.True(selected.IsSelected);
		Assert.False(cleared.IsSelected);
	}

	[Fact]
	public void ExclusionAggregateCountsOnlyPathRules()
	{
		var builder = CreateBuilder();

		var selected = builder.BuildExclusionAggregate(CreatePlan(ProjectSelectionSpec.Standard));
		var cleared = builder.BuildExclusionAggregate(CreatePlan(ProjectSelectionSpec.Standard with
		{
			GitMode = GitFilteringMode.None,
			Exclusions = []
		}));

		Assert.Equal(TerminalParameterRowKind.ToggleAllExclusions, selected.Kind);
		Assert.Equal(
			$"Settings.All ({ProjectPresentationCatalog.Exclusions.Count})",
			selected.Label);
		Assert.True(selected.IsSelected);
		Assert.False(cleared.IsSelected);
	}

	[Fact]
	public void ExtensionRowsSilentlyOmitUnavailableRememberedValues()
	{
		var builder = CreateBuilder();
		var selection = ProjectSelectionSpec.Standard with
		{
			Extensions = [".cs", ".removed"]
		};
		var rows = builder.BuildExtensions(CreatePlan(
			selection,
			availableExtensions: [".cs", ".json"],
			selectedExtensions: [".cs"]));

		Assert.DoesNotContain(rows, static row => row.Kind == TerminalParameterRowKind.ToggleAllExtensions);
		Assert.Contains(rows, static row =>
			row.Kind == TerminalParameterRowKind.Extension &&
			row.Value == ".cs" && row.IsSelected == true);
		Assert.DoesNotContain(rows, static row =>
			string.Equals(row.Value, ".removed", StringComparison.OrdinalIgnoreCase) ||
			row.Label.Contains(".removed", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void ExtensionAggregateIgnoresUnavailableRememberedSelections()
	{
		var row = CreateBuilder().BuildExtensionAggregate(CreatePlan(
			ProjectSelectionSpec.Standard,
			availableExtensions: [".cs", ".json"],
			selectedExtensions: [".cs", ".json", ".removed"]));

		Assert.Equal(TerminalParameterRowKind.ToggleAllExtensions, row.Kind);
		Assert.Equal("Settings.All (2)", row.Label);
		Assert.True(row.IsSelected);
	}

	[Fact]
	public void ParameterIslandsNeverEmitInformationalPlaceholderRows()
	{
		var builder = CreateBuilder();
		var plan = CreatePlan(
			ProjectSelectionSpec.Standard with { Extensions = [".missing"] },
			availableExtensions: [".cs"],
			selectedExtensions: []);

		var rows = builder.BuildContent(plan, snapshot: null)
			.Concat(builder.BuildExclusions(plan))
			.Concat(builder.BuildExtensions(plan));

		Assert.All(rows, static row => Assert.NotNull(row.IsSelected));
	}

	[Theory]
	[InlineData(0, 0, false)]
	[InlineData(1, 1, true)]
	[InlineData(1, 0, false)]
	public void ExtensionAggregateHandlesEmptyAndSingleItemSets(
		int availableCount,
		int selectedCount,
		bool expected)
	{
		var available = availableCount == 0 ? Array.Empty<string>() : [".cs"];
		var selected = selectedCount == 0 ? Array.Empty<string>() : [".cs"];

		var row = CreateBuilder().BuildExtensionAggregate(CreatePlan(
			ProjectSelectionSpec.Standard,
			availableExtensions: available,
			selectedExtensions: selected));

		Assert.Equal(expected, row.IsSelected);
		Assert.Equal(
			availableCount == 0 ? "Settings.All" : $"Settings.All ({availableCount})",
			row.Label);
	}

	[Fact]
	public void ExtensionAggregateTreatsUnavailableSelectionsAsOutsideTheAvailableSet()
	{
		var row = CreateBuilder().BuildExtensionAggregate(CreatePlan(
			ProjectSelectionSpec.Standard with { Extensions = [".removed"] },
			availableExtensions: [],
			selectedExtensions: []));

		Assert.False(row.IsSelected);
	}

	[Theory]
	[InlineData(0, 0, 2, true, 0)]
	[InlineData(4, 1, 8, true, 5)]
	[InlineData(0, 2, 2, false, 2)]
	[InlineData(4, 4, 8, false, 8)]
	[InlineData(0, -1, 2, false, -1)]
	public void ParameterListPointerSelectionRejectsRowsOutsideTheSource(
		int viewportTop,
		int pointerRow,
		int itemCount,
		bool expected,
		int expectedIndex)
	{
		var resolved = TerminalParameterListView.TryResolveSelectionIndex(
			viewportTop,
			pointerRow,
			itemCount,
			out var selectionIndex);

		Assert.Equal(expected, resolved);
		Assert.Equal(expectedIndex, selectionIndex);
	}

	[Fact]
	public void TreeAndParameterListsIgnoreButtonReleaseEvents()
	{
		Assert.False(TerminalProjectTreeView.IsPrimaryActivation(MouseFlags.LeftButtonReleased));
		Assert.False(TerminalParameterListView.IsPrimaryActivation(MouseFlags.LeftButtonReleased));
		Assert.True(TerminalProjectTreeView.IsPrimaryActivation(MouseFlags.LeftButtonPressed));
		Assert.True(TerminalParameterListView.IsPrimaryActivation(MouseFlags.LeftButtonClicked));
	}

	[Fact]
	public void ParameterListPreservesDisabledRowsAsNonInteractiveItems()
	{
		var rows = new ObservableCollection<TerminalParameterRow>
		{
			new(
				"git:tracked",
				TerminalParameterRowKind.GitMode,
				"Tracked files only",
				IsSelected: false,
				IsEnabled: false,
				GitMode: GitFilteringMode.TrackedFilesOnly),
			new(
				"git:none",
				TerminalParameterRowKind.GitMode,
				"Off",
				IsSelected: true,
				GitMode: GitFilteringMode.None)
		};
		using var list = new TerminalParameterListView();

		list.SetParameterSource(rows);

		Assert.False(list.IsRowEnabled(0));
		Assert.True(list.IsRowEnabled(1));
		Assert.False(list.IsRowEnabled(-1));
		Assert.False(list.IsRowEnabled(rows.Count));
	}

	[Fact]
	public void DisabledSelectedControlFallsBackToTheActiveEnabledRow()
	{
		TerminalParameterRow[] rows =
		[
			new(
				"git:staged",
				TerminalParameterRowKind.GitMode,
				"Staged",
				IsSelected: false,
				IsEnabled: false,
				GitMode: GitFilteringMode.Staged),
			new(
				"git:gitignore",
				TerminalParameterRowKind.GitMode,
				"Use .gitignore",
				IsSelected: true,
				GitMode: GitFilteringMode.RespectGitIgnore)
		];

		var selectedIndex = TerminalWorkspaceSession.FindPreferredControlRowIndex(
			rows,
			"git:staged");

		Assert.Equal(1, selectedIndex);
	}

	[Theory]
	[InlineData("abcdef", 4, true, "abc…")]
	[InlineData("界界界", 5, true, "界界…")]
	[InlineData("abcdef", 5, false, "ab...")]
	public void FitLabelHonorsTerminalColumns(
		string value,
		int width,
		bool useUnicode,
		string expected)
	{
		var result = TerminalParameterRow.FitLabel(value, width, useUnicode);

		Assert.Equal(expected, result);
		Assert.True(result.GetColumns() <= width);
	}

	[Fact]
	public void StableParameterShapeUpdatesRowsWithoutReplacingTheSource()
	{
		var original = new TerminalParameterRow(
			"content:hide-secrets",
			TerminalParameterRowKind.ContentTransformation,
			"Hide secrets",
			IsSelected: true,
			ContentTransformation: IgnoreOptionId.HideSecrets);
		var source = new ObservableCollection<TerminalParameterRow>([original]);
		_ = original.ToString();
		var replacement = original with { Label = "Hide secrets (2/2)" };

		var updated = TerminalWorkspaceSession.TryUpdateRowsInPlace(source, [replacement]);

		Assert.True(updated);
		Assert.Same(replacement, source[0]);
		Assert.Equal("[x] Hide secrets (2/2)", source[0].ToString());
	}

	[Fact]
	public void RenderingDoesNotChangeParameterRowValueEquality()
	{
		var row = new TerminalParameterRow(
			"extension:.cs",
			TerminalParameterRowKind.Extension,
			".cs",
			IsSelected: true,
			Value: ".cs");
		var equivalent = row with { };

		_ = row.ToString();

		Assert.Equal(equivalent, row);
		Assert.Equal(equivalent.GetHashCode(), row.GetHashCode());
	}

	[Fact]
	public void ChangedParameterShapeLeavesTheExistingSourceUntouchedForFallback()
	{
		var original = new TerminalParameterRow(
			"extension:.cs",
			TerminalParameterRowKind.Extension,
			".cs",
			IsSelected: true,
			Value: ".cs");
		var source = new ObservableCollection<TerminalParameterRow>([original]);
		var replacement = original with { Key = "extension:.json", Label = ".json", Value = ".json" };

		var updated = TerminalWorkspaceSession.TryUpdateRowsInPlace(source, [replacement]);

		Assert.False(updated);
		Assert.Same(original, source[0]);
	}

	[Fact]
	public void RedactionLabelStampUsesSnapshotValuesInsteadOfObjectIdentity()
	{
		var first = new SecretRedactionSnapshot(
			"selection",
			DetectedCount: 5,
			RedactedCount: 4,
			PrivateDataDetectedCount: 2,
			PrivateDataRedactedCount: 1);
		var equivalent = first with { };
		var changed = first with { RedactedCount = 5 };

		Assert.Equal(
			TerminalRedactionLabelStamp.From(first),
			TerminalRedactionLabelStamp.From(equivalent));
		Assert.NotEqual(
			TerminalRedactionLabelStamp.From(first),
			TerminalRedactionLabelStamp.From(changed));
	}

	[Fact]
	public void ControlRefreshDecisionSeparatesPreviewCountersFromStructuralChanges()
	{
		var source = new TerminalControlSourceStamp(
			null!,
			Revision: 7,
			DraftSelection: null,
			Language: AppLanguage.En,
			LayoutMode: TerminalWorkspaceLayoutMode.Wide,
			LabelWidth: 30);
		var redaction = new TerminalRedactionLabelStamp(
			true,
			"selection",
			1,
			1,
			0,
			0,
			true);

		Assert.Equal(
			TerminalControlRefreshKind.None,
			TerminalWorkspaceSession.ResolveControlRefreshKind(
				source,
				source,
				redaction,
				redaction));
		Assert.Equal(
			TerminalControlRefreshKind.RedactionOnly,
			TerminalWorkspaceSession.ResolveControlRefreshKind(
				source,
				source,
				redaction,
				redaction with { SecretDetectedCount = 2 }));
		Assert.Equal(
			TerminalControlRefreshKind.Full,
			TerminalWorkspaceSession.ResolveControlRefreshKind(
				source,
				source with { Revision = 8 },
				redaction,
				redaction));
	}

	[Theory]
	[InlineData("Очень длинная подпись личных данных (12/11)", 24, true, " (12/11)")]
	[InlineData("Maxfiy ma'lumotlarning juda uzun nomi (7)", 22, false, " (7)")]
	[InlineData("界界界界界界界界 (3/2)", 14, true, " (3/2)")]
	public void FitLabelPreservesTrailingCounters(
		string value,
		int width,
		bool useUnicode,
		string counter)
	{
		var result = TerminalParameterRow.FitLabel(value, width, useUnicode);

		Assert.EndsWith(counter, result, StringComparison.Ordinal);
		Assert.Contains(useUnicode ? "…" : "...", result, StringComparison.Ordinal);
		Assert.True(result.GetColumns() <= width);
	}

	private static TerminalParameterRowsBuilder CreateBuilder() =>
		new(
			static key => key,
			static value => value,
			static (option, _, matched, redacted) =>
				$"{option}:{matched}/{redacted}");

	private static ProjectContextPlan CreatePlan(
		ProjectSelectionSpec selection,
		IReadOnlyList<string>? availableExtensions = null,
		IReadOnlyList<string>? selectedExtensions = null,
		bool hasIgnoreOptionCounts = false,
		IgnoreOptionCounts ignoreOptionCounts = default,
		IgnoreControllerImpactCounts ignoreControllerImpactCounts = default)
	{
		var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DevProjexTerminalRows"));
		var tree = new TreeNodeDescriptor("project", root, true, false, "folder", []);
		var analysis = new ProjectAnalysisReport(
			1,
			DateTimeOffset.UnixEpoch,
			root,
			new ProjectAnalysisSelectionReport([], [], []),
			new ProjectAnalysisInventoryReport(
				[],
				availableExtensions ?? [".cs"],
				new ProjectTreeSummaryReport(1, 0, 0)),
			new ProjectAnalysisOutputMetricsReport(
				ProjectOutputMetricsReport.Empty,
				ProjectOutputMetricsReport.Empty),
			new ProjectAnalysisTimingReport(0, 0, 0),
			new ProjectAnalysisDiagnosticsReport(false, false, []));
		return new ProjectContextPlan(
			root,
			selection,
			[],
			[],
			availableExtensions ?? [".cs"],
			selectedExtensions ?? [".cs"],
			tree,
			tree,
			new HashSet<string>(PathComparer.Default),
			[],
			[root],
			analysis,
			[],
			new ProjectContextGitReadiness(
				selection.GitMode ?? GitFilteringMode.None,
				1,
				true),
			"rows",
			HasIgnoreOptionCounts: hasIgnoreOptionCounts,
			IgnoreOptionCounts: ignoreOptionCounts,
			IgnoreControllerImpactCounts: ignoreControllerImpactCounts);
	}
}
