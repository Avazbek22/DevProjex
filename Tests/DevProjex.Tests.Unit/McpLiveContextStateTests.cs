using DevProjex.Application.Context;
using DevProjex.Mcp;
using ModelContextProtocol.Protocol;
using System.Runtime.CompilerServices;

namespace DevProjex.Tests.Unit;

public sealed class McpLiveContextStateTests
{
	[Fact]
	public void ProfileChangeIncrementsRevisionAndReportsFrontierOnce()
	{
		using var temporary = new TemporaryDirectory();
		var store = new SequenceProfileStore(
			Found(Profile(["src"])),
			Found(Profile(["tests", "docs/api"])),
			Found(Profile(["tests", "docs/api"])));
		var state = new McpLiveContextState(
			new McpRootRegistry([temporary.Path]),
			() => store,
			TimeSpan.Zero);

		using (state.BeginInvocation())
		{
			Assert.Equal(1, state.ReadProfile(temporary.Path).Revision);
			var initial = Text(state.AppendNotices(McpToolResults.TextSuccess("ok")));
			Assert.Contains("[Live context] revision 1", initial, StringComparison.Ordinal);
			Assert.DoesNotContain("changed since", initial, StringComparison.Ordinal);
		}

		using (state.BeginInvocation())
		{
			Assert.Equal(2, state.ReadProfile(temporary.Path).Revision);
			var changed = Text(state.AppendNotices(McpToolResults.TextSuccess("ok")));
			Assert.Contains(
				"[Live context] changed since revision 1: +2 paths, -1 path",
				changed,
				StringComparison.Ordinal);
			Assert.Contains("+docs/api", changed, StringComparison.Ordinal);
			Assert.Contains("+tests", changed, StringComparison.Ordinal);
			Assert.Contains("-src", changed, StringComparison.Ordinal);
			AssertMarkerIsInsideUntrustedData(changed, "docs/api");
			AssertMarkerIsInsideUntrustedData(changed, "tests");
			AssertMarkerIsInsideUntrustedData(changed, "src");
		}

		using (state.BeginInvocation())
		{
			Assert.Equal(2, state.ReadProfile(temporary.Path).Revision);
			var repeated = Text(state.AppendNotices(McpToolResults.TextSuccess("ok")));
			Assert.DoesNotContain("changed since", repeated, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void ProfileIsReadOncePerInvocationAndAgainOnTheNextInvocation()
	{
		using var temporary = new TemporaryDirectory();
		var store = new SequenceProfileStore(
			Found(Profile(["src"])),
			Found(Profile(["tests"])));
		var state = new McpLiveContextState(
			new McpRootRegistry([temporary.Path]),
			() => store,
			TimeSpan.Zero);

		using (state.BeginInvocation())
		{
			state.RefreshProfile(temporary.Path);
			var repeated = state.ReadProfile(temporary.Path);

			Assert.Equal(1, repeated.Revision);
			Assert.Equal(["src"], repeated.Profile!.SelectedPaths);
			Assert.Equal(1, store.LookupCount);
		}

		using (state.BeginInvocation())
		{
			var changed = state.ReadProfile(temporary.Path);

			Assert.Equal(2, changed.Revision);
			Assert.Equal(["tests"], changed.Profile!.SelectedPaths);
			Assert.Equal(2, store.LookupCount);
		}
	}

	[Fact]
	public async Task ParallelProfileReadsWithinOneInvocationShareOneLookup()
	{
		using var temporary = new TemporaryDirectory();
		var store = new SequenceProfileStore(Found(Profile(["src"])));
		var state = new McpLiveContextState(
			new McpRootRegistry([temporary.Path]),
			() => store,
			TimeSpan.Zero);

		using (state.BeginInvocation())
		{
			var reads = Enumerable.Range(0, 8)
				.Select(_ => Task.Run(() => state.ReadProfile(temporary.Path)))
				.ToArray();
			var snapshots = await Task.WhenAll(reads);

			Assert.All(snapshots, snapshot => Assert.Equal(["src"], snapshot.Profile!.SelectedPaths));
			Assert.Equal(1, store.LookupCount);
		}
	}

	[Fact]
	public void SelectedFileCountIsRetainedUntilTheProfileRevisionChanges()
	{
		using var temporary = new TemporaryDirectory();
		var store = new SequenceProfileStore(
			Found(Profile(["src"])),
			Found(Profile(["src"])),
			Found(Profile(["tests"])));
		var state = new McpLiveContextState(
			new McpRootRegistry([temporary.Path]),
			() => store,
			TimeSpan.Zero);

		using (state.BeginInvocation())
		{
			var revision = state.ReadProfile(temporary.Path).Revision;
			Assert.False(state.HasSelectedFileCount(temporary.Path, revision));
			state.RecordPlan(temporary.Path, Plan(temporary.Path, 3));
			Assert.True(state.HasSelectedFileCount(temporary.Path, revision));
		}

		using (state.BeginInvocation())
		{
			var revision = state.ReadProfile(temporary.Path).Revision;
			Assert.True(state.HasSelectedFileCount(temporary.Path, revision));
		}

		using (state.BeginInvocation())
		{
			var revision = state.ReadProfile(temporary.Path).Revision;
			Assert.False(state.HasSelectedFileCount(temporary.Path, revision));
		}
	}

	[Fact]
	public void SelectedFileCountRequiresTheSameReliableRootRevision()
	{
		using var temporary = new TemporaryDirectory();
		var state = new McpLiveContextState(
			new McpRootRegistry([temporary.Path]),
			() => new SequenceProfileStore(Found(Profile(["src"]))),
			TimeSpan.Zero);

		using var invocation = state.BeginInvocation();
		var revision = state.ReadProfile(temporary.Path).Revision;
		state.RecordPlan(temporary.Path, Plan(temporary.Path, 1), rootRevision: new McpRootMonitorStamp(1, 4));

		Assert.True(state.HasSelectedFileCount(temporary.Path, revision, rootRevision: new McpRootMonitorStamp(1, 4)));
		Assert.False(state.HasSelectedFileCount(temporary.Path, revision, rootRevision: new McpRootMonitorStamp(1, 5)));
		Assert.False(state.HasSelectedFileCount(temporary.Path, revision, rootRevision: new McpRootMonitorStamp(2, 4)));
		state.RecordPlan(temporary.Path, Plan(temporary.Path, 2), rootRevision: null);
		Assert.False(state.HasSelectedFileCount(temporary.Path, revision, rootRevision: new McpRootMonitorStamp(2, 4)));
	}

	[Fact]
	public void ChangedPathKindsComeOnlyFromTheEffectivePlanTree()
	{
		using var temporary = new TemporaryDirectory();
		_ = temporary.CreateFolder("not-in-plan");
		var store = new SequenceProfileStore(
			Found(Profile(["old-selection"])),
			Found(Profile(["not-in-plan", "virtual-folder", "virtual.cs"])));
		var state = new McpLiveContextState(
			new McpRootRegistry([temporary.Path]),
			() => store,
			TimeSpan.Zero);

		var effectiveTree = new TreeNodeDescriptor(
			"project",
			temporary.Path,
			true,
			false,
			"folder",
			[
				new TreeNodeDescriptor(
					"virtual-folder",
					Path.Combine(temporary.Path, "virtual-folder"),
					true,
					false,
					"folder",
					[]),
				new TreeNodeDescriptor(
					"virtual.cs",
					Path.Combine(temporary.Path, "virtual.cs"),
					false,
					false,
					"csharp",
					[])
			]);
		using (state.BeginInvocation())
		{
			_ = state.ReadProfile(temporary.Path);
			state.RecordPlan(temporary.Path, Plan(temporary.Path, 1, effectiveTree));
			_ = state.AppendNotices(McpToolResults.TextSuccess("initial"));
		}

		using (state.BeginInvocation())
		{
			_ = state.ReadProfile(temporary.Path);
			var response = Text(state.AppendNotices(McpToolResults.TextSuccess("changed")));

			Assert.Contains(
				"[Live context] changed since revision 1: +1 path, +1 folder, +1 file, -1 path",
				response,
				StringComparison.Ordinal);
		}
	}

	[Fact]
	public void RecordedPlanDoesNotKeepItsEffectiveTreeAlive()
	{
		using var temporary = new TemporaryDirectory();
		var state = new McpLiveContextState(
			new McpRootRegistry([temporary.Path]),
			() => new SequenceProfileStore(Found(Profile(["src"]))),
			TimeSpan.Zero);

		WeakReference<TreeNodeDescriptor> treeReference;
		using (state.BeginInvocation())
		{
			_ = state.ReadProfile(temporary.Path);
			treeReference = RecordTemporaryPlan(state, temporary.Path);
		}

		CollectTemporaryPlan();

		Assert.False(treeReference.TryGetTarget(out _));
		GC.KeepAlive(state);
	}

	[Fact]
	public void DynamicRootUsesAPositiveOrdinalAcrossConfiguredAndObservedRoots()
	{
		using var temporary = new TemporaryDirectory();
		var first = temporary.CreateFolder("configured-a");
		var second = temporary.CreateFolder("configured-b");
		var dynamicRoot = temporary.CreateFolder("dynamic-z");
		var state = new McpLiveContextState(
			new McpRootRegistry([first, second]),
			() => new SequenceProfileStore(Found(Profile(null))),
			TimeSpan.Zero);

		using var invocation = state.BeginInvocation();
		_ = state.ReadProfile(dynamicRoot);
		var response = Text(state.AppendNotices(McpToolResults.TextSuccess("ok")));

		Assert.DoesNotContain("root 0 of", response, StringComparison.Ordinal);
		Assert.Contains("root 3 of 3", response, StringComparison.Ordinal);
		AssertMarkerIsInsideUntrustedData(response, "dynamic-z");
	}

	[Fact]
	public void APlanFromAnOlderConcurrentRevisionCannotReplaceTheCurrentCount()
	{
		using var temporary = new TemporaryDirectory();
		var store = new SequenceProfileStore(
			Found(Profile(["src"])),
			Found(Profile(["tests"])),
			Found(Profile(["tests"])));
		var state = new McpLiveContextState(
			new McpRootRegistry([temporary.Path]),
			() => store,
			TimeSpan.Zero);

		using (state.BeginInvocation())
		{
			var firstRevision = state.ReadProfile(temporary.Path).Revision;
			using (state.BeginInvocation())
			{
				var currentRevision = state.ReadProfile(temporary.Path).Revision;
				state.RecordPlan(temporary.Path, Plan(temporary.Path, 2));
				Assert.True(state.HasSelectedFileCount(temporary.Path, currentRevision));
			}

			state.RecordPlan(temporary.Path, Plan(temporary.Path, 7));
			Assert.False(state.HasSelectedFileCount(temporary.Path, firstRevision));
		}

		using (state.BeginInvocation())
		{
			var currentRevision = state.ReadProfile(temporary.Path).Revision;
			Assert.True(state.HasSelectedFileCount(temporary.Path, currentRevision));
			var response = Text(state.AppendNotices(new CallToolResult { Content = [] }));
			Assert.Contains("revision 2 · 2 files selected", response, StringComparison.Ordinal);
		}
	}

	[Theory]
	[InlineData(ProjectProfileLookupStatus.TemporarilyUnavailable)]
	[InlineData(ProjectProfileLookupStatus.InvalidStorage)]
	[InlineData(ProjectProfileLookupStatus.UnsupportedFutureSchema)]
	public void UnreadableProfileKeepsLastSuccessfulRevision(ProjectProfileLookupStatus failureStatus)
	{
		using var temporary = new TemporaryDirectory();
		var store = new SequenceProfileStore(
			Found(Profile(["src"])),
			new ProjectProfileLookupResult(failureStatus, null));
		var state = new McpLiveContextState(
			new McpRootRegistry([temporary.Path]),
			() => store,
			TimeSpan.Zero);

		using (state.BeginInvocation())
			Assert.NotNull(state.ReadProfile(temporary.Path).Profile);

		using (state.BeginInvocation())
		{
			var snapshot = state.ReadProfile(temporary.Path);
			Assert.Equal(1, snapshot.Revision);
			Assert.True(snapshot.IsReadFailure);
			Assert.NotNull(snapshot.Profile);
			var response = Text(state.AppendNotices(McpToolResults.TextSuccess("ok")));
			var expected = failureStatus == ProjectProfileLookupStatus.TemporarilyUnavailable
				? "Saved selection is busy. Using revision 1. Retry this call once."
				: "Saved selection is invalid or incompatible. Using revision 1. Ask the user to repair it or update DevProjex; retry after that.";
			Assert.Contains(expected, response, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void BackupRecoveryUsesTheRecoveredProfileAndReportsRetry()
	{
		using var temporary = new TemporaryDirectory();
		var recovered = new ProjectProfileLookupResult(
			ProjectProfileLookupStatus.Found,
			Profile(["src"]))
		{
			RecoveryStatus = ProjectProfileLookupStatus.InvalidStorage
		};
		var state = new McpLiveContextState(
			new McpRootRegistry([temporary.Path]),
			() => new SequenceProfileStore(recovered),
			TimeSpan.Zero);

		using var invocation = state.BeginInvocation();
		var snapshot = state.ReadProfile(temporary.Path);
		var response = Text(state.AppendNotices(McpToolResults.TextSuccess("ok")));

		Assert.Equal(["src"], snapshot.Profile!.SelectedPaths);
		Assert.True(snapshot.IsReadFailure);
		Assert.Contains(
			"[Live context] Saved selection is invalid or incompatible. Using revision 1. Ask the user to repair it or update DevProjex; retry after that.",
			response,
			StringComparison.Ordinal);
	}

	[Fact]
	public void BackupRecoveryDoesNotReplaceTheLastSuccessfulSnapshot()
	{
		using var temporary = new TemporaryDirectory();
		var recovered = Found(Profile(["docs"])) with
		{
			RecoveryStatus = ProjectProfileLookupStatus.InvalidStorage
		};
		var store = new SequenceProfileStore(Found(Profile(["src"])), recovered);
		var state = new McpLiveContextState(
			new McpRootRegistry([temporary.Path]),
			() => store,
			TimeSpan.Zero);

		using (state.BeginInvocation())
		{
			var initial = state.ReadProfile(temporary.Path);
			Assert.Equal(["src"], initial.Profile!.SelectedPaths);
		}

		using (state.BeginInvocation())
		{
			var snapshot = state.ReadProfile(temporary.Path);
			var response = Text(state.AppendNotices(McpToolResults.TextSuccess("ok")));
			Assert.Equal(["src"], snapshot.Profile!.SelectedPaths);
			Assert.Equal(1, snapshot.Revision);
			Assert.True(snapshot.IsReadFailure);
			Assert.Contains("Saved selection is invalid or incompatible. Using revision 1.", response, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void MissingBackupEntryDoesNotReplaceTheLastSuccessfulSnapshot()
	{
		using var temporary = new TemporaryDirectory();
		var recoveredMissing = new ProjectProfileLookupResult(ProjectProfileLookupStatus.Missing, null)
		{
			RecoveryStatus = ProjectProfileLookupStatus.InvalidStorage
		};
		var store = new SequenceProfileStore(Found(Profile(["src"])), recoveredMissing);
		var state = new McpLiveContextState(
			new McpRootRegistry([temporary.Path]),
			() => store,
			TimeSpan.Zero);

		using (state.BeginInvocation())
			Assert.Equal(["src"], state.ReadProfile(temporary.Path).Profile!.SelectedPaths);

		using (state.BeginInvocation())
		{
			var snapshot = state.ReadProfile(temporary.Path);
			var response = Text(state.AppendNotices(McpToolResults.TextSuccess("ok")));
			Assert.Equal(["src"], snapshot.Profile!.SelectedPaths);
			Assert.Equal(1, snapshot.Revision);
			Assert.True(snapshot.IsReadFailure);
			Assert.Contains("Saved selection is invalid or incompatible. Using revision 1.", response, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void MissingBackupEntryReportsAnUnreadableInitialSnapshot()
	{
		using var temporary = new TemporaryDirectory();
		var recoveredMissing = new ProjectProfileLookupResult(ProjectProfileLookupStatus.Missing, null)
		{
			RecoveryStatus = ProjectProfileLookupStatus.InvalidStorage
		};
		var state = new McpLiveContextState(
			new McpRootRegistry([temporary.Path]),
			() => new SequenceProfileStore(recoveredMissing),
			TimeSpan.Zero);

		using var invocation = state.BeginInvocation();
		var snapshot = state.ReadProfile(temporary.Path);
		var response = Text(state.AppendNotices(McpToolResults.TextSuccess("ok")));

		Assert.True(snapshot.IsMissing);
		Assert.True(snapshot.IsReadFailure);
		Assert.False(snapshot.HasSuccessfulSnapshot);
		Assert.Contains(
			"[Live context] Saved selection is invalid or incompatible. Ask the user to repair it or update DevProjex; retry after that.",
			response,
			StringComparison.Ordinal);
		Assert.DoesNotContain("using revision", response, StringComparison.Ordinal);
	}

	[Fact]
	public void RecoveredMissingProfileDoesNotFallThroughToThePhysicalRootAlias()
	{
		using var temporary = new TemporaryDirectory();
		var project = temporary.CreateFolder("project");
		var alias = Path.Combine(temporary.Path, "project-alias");
		try
		{
			Directory.CreateSymbolicLink(alias, project);
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
			return;
		}

		try
		{
			var recoveredMissing = new ProjectProfileLookupResult(ProjectProfileLookupStatus.Missing, null)
			{
				RecoveryStatus = ProjectProfileLookupStatus.InvalidStorage
			};
			var store = new SequenceProfileStore(
				recoveredMissing,
				Found(Profile(["unexpected-second-lookup"])));
			var roots = new McpRootRegistry([alias]);
			var state = new McpLiveContextState(roots, () => store, TimeSpan.Zero);

			using var invocation = state.BeginInvocation();
			var snapshot = state.ReadProfile(Assert.Single(roots.Roots));

			Assert.Equal(1, store.LookupCount);
			Assert.True(snapshot.IsReadFailure);
			Assert.False(snapshot.HasSuccessfulSnapshot);
			Assert.Null(snapshot.Profile);
		}
		finally
		{
			if (Directory.Exists(alias))
				Directory.Delete(alias);
		}
	}

	[Fact]
	public void SettingsOnlyChangeReportsAnHonestRevisionReason()
	{
		using var temporary = new TemporaryDirectory();
		var first = new ProjectSelectionProfile([], [".cs"], [], SelectedPaths: ["src"]);
		var second = new ProjectSelectionProfile([], [".md"], [], SelectedPaths: ["src"]);
		var store = new SequenceProfileStore(Found(first), Found(second));
		var state = new McpLiveContextState(
			new McpRootRegistry([temporary.Path]),
			() => store,
			TimeSpan.Zero);

		using (state.BeginInvocation())
		{
			_ = state.ReadProfile(temporary.Path);
			_ = state.AppendNotices(McpToolResults.TextSuccess("initial"));
		}

		using (state.BeginInvocation())
		{
			_ = state.ReadProfile(temporary.Path);
			var response = Text(state.AppendNotices(McpToolResults.TextSuccess("changed")));
			Assert.Contains(
				"[Live context] changed since revision 1: selection settings changed",
				response,
				StringComparison.Ordinal);
		}
	}

	[Fact]
	public void LockedProfileFileKeepsTheLastSuccessfulSnapshotAndReportsRetry()
	{
		using var temporary = new TemporaryDirectory();
		var project = temporary.CreateFolder("project");
		var appData = temporary.CreateFolder("app-data");
		var store = new ProjectProfileStore(() => appData);
		Assert.True(store.TrySaveProfile(
			project,
			new ProjectSelectionProfile([], [], [], SelectedPaths: ["src"])));
		var state = new McpLiveContextState(
			new McpRootRegistry([project]),
			() => store,
			TimeSpan.FromMilliseconds(100));

		using (state.BeginInvocation())
		{
			var initial = state.ReadProfile(project);
			Assert.Equal(["src"], initial.Profile!.SelectedPaths);
			Assert.Equal(1, initial.Revision);
		}

		using var held = new FileStream(store.GetPath(), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
		using (state.BeginInvocation())
		{
			var locked = state.ReadProfile(project);
			Assert.Equal(["src"], locked.Profile!.SelectedPaths);
			Assert.Equal(1, locked.Revision);
			Assert.True(locked.IsReadFailure);
			var response = Text(state.AppendNotices(McpToolResults.TextSuccess("ok")));
			Assert.Contains(
				"[Live context] Saved selection is busy. Using revision 1. Retry this call once.",
				response,
				StringComparison.Ordinal);
		}
	}

	[Fact]
	public void ExplicitEmptyProfileReportsNoWindowFiles()
	{
		using var temporary = new TemporaryDirectory();
		var store = new SequenceProfileStore(Found(Profile([])));
		var state = new McpLiveContextState(
			new McpRootRegistry([temporary.Path]),
			() => store,
			TimeSpan.Zero);

		using var invocation = state.BeginInvocation();
		_ = state.ReadProfile(temporary.Path);
		var response = Text(state.AppendNotices(McpToolResults.TextSuccess("ok")));

		Assert.Contains(
			"[Live context] the window selects no files; tick files in the DevProjex window.",
			response,
			StringComparison.Ordinal);
	}

	[Fact]
	public void RemovedProfileReportsTheDefaultExpansionAsARevisionChange()
	{
		using var temporary = new TemporaryDirectory();
		var store = new SequenceProfileStore(
			Found(Profile(["src"])),
			new ProjectProfileLookupResult(ProjectProfileLookupStatus.Missing, null));
		var state = new McpLiveContextState(
			new McpRootRegistry([temporary.Path]),
			() => store,
			TimeSpan.Zero);

		using (state.BeginInvocation())
		{
			_ = state.ReadProfile(temporary.Path);
			_ = state.AppendNotices(McpToolResults.TextSuccess("initial"));
		}

		using (state.BeginInvocation())
		{
			var snapshot = state.ReadProfile(temporary.Path);
			var response = Text(state.AppendNotices(McpToolResults.TextSuccess("after reset")));

			Assert.Equal(2, snapshot.Revision);
			Assert.True(snapshot.IsMissing);
			Assert.Null(snapshot.Profile);
			Assert.Contains(
				"[Live context] no window selection saved for this root; using server defaults.",
				response,
				StringComparison.Ordinal);
			Assert.Contains(
				"[Live context] changed since revision 1: -1 path, +all",
				response,
				StringComparison.Ordinal);
			AssertMarkerIsInsideUntrustedData(response, "src");
		}
	}

	[Fact]
	public void ResponseBeforePlanStillReportsTheCurrentRevision()
	{
		using var temporary = new TemporaryDirectory();
		var state = new McpLiveContextState(
			new McpRootRegistry([temporary.Path]),
			() => new SequenceProfileStore(Found(Profile(["src"]))),
			TimeSpan.Zero);

		using var invocation = state.BeginInvocation();
		var response = Text(state.AppendNotices(McpToolResults.TextSuccess("invalid request")));

		Assert.Contains("[Live context] revision 1 · 0 files selected in the window", response, StringComparison.Ordinal);
	}

	[Fact]
	public void StoredResultAllowsSelectionChangeAndReportsTheOriginalRevision()
	{
		using var temporary = new TemporaryDirectory();
		var store = new SequenceProfileStore(
			Found(Profile(["src"])),
			Found(Profile(["tests"])));
		var state = new McpLiveContextState(
			new McpRootRegistry([temporary.Path]),
			() => store,
			TimeSpan.Zero);
		McpStoredResultContext stored;
		using (state.BeginInvocation())
		{
			_ = state.ReadProfile(temporary.Path);
			stored = Assert.IsType<McpStoredResultContext>(
				state.RecordStoredResult(temporary.Path, McpStoredResultKind.Search));
		}

		using var invocation = state.BeginInvocation();
		Assert.True(state.RefreshStoredResult(stored));
		var response = Text(state.AppendNotices(McpToolResults.TextSuccess("stored")));

		Assert.Contains("search result built at revision 1; window is at revision 2", response, StringComparison.Ordinal);
	}

	[Fact]
	public void StoredResultRejectsAChangedManualProtectionPolicy()
	{
		using var temporary = new TemporaryDirectory();
		var initial = Profile(["src"]);
		var protectedProfile = initial with
		{
			MarkedSecrets = [new MarkedSecretProfileEntry("v2:marked", "secret", 6)]
		};
		var store = new SequenceProfileStore(Found(initial), Found(protectedProfile));
		var state = new McpLiveContextState(
			new McpRootRegistry([temporary.Path]),
			() => store,
			TimeSpan.Zero);
		McpStoredResultContext stored;
		using (state.BeginInvocation())
		{
			_ = state.ReadProfile(temporary.Path);
			stored = Assert.IsType<McpStoredResultContext>(
				state.RecordStoredResult(temporary.Path, McpStoredResultKind.Search));
		}

		using var invocation = state.BeginInvocation();
		var error = Assert.Throws<McpToolException>(() => state.RefreshStoredResult(stored));

		Assert.Equal("DPX-MCP-STORED-PROTECTION-CHANGED", error.Code);
		Assert.Equal(
			"DPX-MCP-STORED-PROTECTION-CHANGED: the saved protection policy changed after this result was stored. " +
			"Call search_project again before read_pack.",
			error.Message);
	}

	[Fact]
	public void StoredResultRejectsAChangedPrivateDataPolicy()
	{
		using var temporary = new TemporaryDirectory();
		var initial = Profile(["src"]);
		var protectedProfile = initial with
		{
			SelectedIgnoreOptions = [IgnoreOptionId.HidePrivateData]
		};
		var store = new SequenceProfileStore(Found(initial), Found(protectedProfile));
		var state = new McpLiveContextState(
			new McpRootRegistry([temporary.Path]),
			() => store,
			TimeSpan.Zero);
		McpStoredResultContext stored;
		using (state.BeginInvocation())
		{
			_ = state.ReadProfile(temporary.Path);
			stored = Assert.IsType<McpStoredResultContext>(
				state.RecordStoredResult(temporary.Path, McpStoredResultKind.Pack));
		}

		using var invocation = state.BeginInvocation();
		var error = Assert.Throws<McpToolException>(() => state.RefreshStoredResult(stored));

		Assert.Equal("DPX-MCP-STORED-PROTECTION-CHANGED", error.Code);
		Assert.Contains("Call pack_context again before read_pack.", error.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(ProjectProfileLookupStatus.TemporarilyUnavailable)]
	[InlineData(ProjectProfileLookupStatus.InvalidStorage)]
	[InlineData(ProjectProfileLookupStatus.UnsupportedFutureSchema)]
	public void StoredResultFailsClosedWhenCurrentProtectionCannotBeVerified(
		ProjectProfileLookupStatus failureStatus)
	{
		using var temporary = new TemporaryDirectory();
		var store = new SequenceProfileStore(
			Found(Profile(["src"])),
			new ProjectProfileLookupResult(failureStatus, null));
		var state = new McpLiveContextState(
			new McpRootRegistry([temporary.Path]),
			() => store,
			TimeSpan.Zero);
		McpStoredResultContext stored;
		using (state.BeginInvocation())
		{
			_ = state.ReadProfile(temporary.Path);
			stored = Assert.IsType<McpStoredResultContext>(
				state.RecordStoredResult(temporary.Path, McpStoredResultKind.Search));
		}

		using var invocation = state.BeginInvocation();
		var error = Assert.Throws<McpToolException>(() => state.RefreshStoredResult(stored));

		Assert.Equal("DPX-MCP-STORED-PROTECTION-UNAVAILABLE", error.Code);
		Assert.Equal(
			"DPX-MCP-STORED-PROTECTION-UNAVAILABLE: the current saved protection policy could not be verified. " +
			"Retry read_pack after the saved selection is readable; do not use this stored result until then.",
			error.Message);
	}

	[Fact]
	public void StoredResultAllowsMissingProfileToBecomeSelectionOnlyProfile()
	{
		using var temporary = new TemporaryDirectory();
		var store = new SequenceProfileStore(
			new ProjectProfileLookupResult(ProjectProfileLookupStatus.Missing, null),
			Found(Profile(["src"])));
		var state = new McpLiveContextState(
			new McpRootRegistry([temporary.Path]),
			() => store,
			TimeSpan.Zero);
		McpStoredResultContext stored;
		using (state.BeginInvocation())
		{
			_ = state.ReadProfile(temporary.Path);
			stored = Assert.IsType<McpStoredResultContext>(
				state.RecordStoredResult(temporary.Path, McpStoredResultKind.Search));
		}

		using var invocation = state.BeginInvocation();

		Assert.True(state.RefreshStoredResult(stored));
		Assert.Contains(
			"search result built at revision 1; window is at revision 2",
			Text(state.AppendNotices(McpToolResults.TextSuccess("stored"))),
			StringComparison.Ordinal);
	}

	private static ProjectSelectionProfile Profile(IReadOnlyCollection<string>? selectedPaths) =>
		new([], [], [], SelectedPaths: selectedPaths);

	private static ProjectProfileLookupResult Found(ProjectSelectionProfile profile) =>
		new(ProjectProfileLookupStatus.Found, profile);

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static WeakReference<TreeNodeDescriptor> RecordTemporaryPlan(McpLiveContextState state, string root)
	{
		var children = Enumerable.Range(0, 10_000)
			.Select(index => new TreeNodeDescriptor(
				$"File{index}.cs",
				Path.Combine(root, $"File{index}.cs"),
				false,
				false,
				"csharp",
				[]))
			.ToArray();
		var tree = new TreeNodeDescriptor("project", root, true, false, "folder", children);
		state.RecordPlan(root, Plan(root, children.Length, tree));
		return new WeakReference<TreeNodeDescriptor>(tree);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void CollectTemporaryPlan()
	{
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
	}

	private static ProjectContextPlan Plan(
		string root,
		int fileCount,
		TreeNodeDescriptor? effectiveTree = null)
	{
		var tree = effectiveTree ?? new TreeNodeDescriptor("project", root, true, false, "folder", []);
		var analysis = new ProjectAnalysisReport(
			ProjectAnalysisReport.CurrentSchemaVersion,
			DateTimeOffset.UnixEpoch,
			root,
			new ProjectAnalysisSelectionReport([], [], []),
			new ProjectAnalysisInventoryReport([], [], new ProjectTreeSummaryReport(1, fileCount, 0)),
			new ProjectAnalysisOutputMetricsReport(ProjectOutputMetricsReport.Empty, ProjectOutputMetricsReport.Empty),
			new ProjectAnalysisTimingReport(0, 0, 0),
			new ProjectAnalysisDiagnosticsReport(false, false, []));
		return new ProjectContextPlan(
			root,
			ProjectSelectionSpec.Standard,
			[],
			[],
			[],
			[],
			tree,
			tree,
			new HashSet<string>(PathComparer.Default),
			Enumerable.Range(0, fileCount).Select(index => Path.Combine(root, $"File{index}.cs")).ToArray(),
			[root],
			analysis,
			[],
			new ProjectContextGitReadiness(GitFilteringMode.None, 0, false),
			"live-context-state");
	}

	private static string Text(CallToolResult result) =>
		string.Join('\n', result.Content.OfType<TextContentBlock>().Select(static block => block.Text));

	private static void AssertMarkerIsInsideUntrustedData(string text, string marker)
	{
		var start = text.IndexOf("<untrusted-data-", StringComparison.Ordinal);
		var markerIndex = text.IndexOf(marker, StringComparison.Ordinal);
		var end = text.IndexOf("</untrusted-data-", StringComparison.Ordinal);
		Assert.True(start >= 0 && markerIndex > start && end > markerIndex, text);
		Assert.Equal(markerIndex, text.LastIndexOf(marker, StringComparison.Ordinal));
	}

	private sealed class SequenceProfileStore(params ProjectProfileLookupResult[] results) : IProjectProfileStore
	{
		private int index;
		public int LookupCount => Volatile.Read(ref index);

		public ProjectProfileLookupResult LookupProfile(string localProjectPath, TimeSpan lockTimeout) =>
			results[Math.Min(Interlocked.Increment(ref index) - 1, results.Length - 1)];

		public bool EnsureStorageExists() => true;
		public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile)
		{
			profile = Profile(null);
			return false;
		}
		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile) => true;
		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile, DateTimeOffset updatedUtc) => true;
		public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile)
		{
		}
		public ProjectProfileClearStatus ClearAllProfiles() => ProjectProfileClearStatus.Cleared;
	}
}
