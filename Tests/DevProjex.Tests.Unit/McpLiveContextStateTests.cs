using DevProjex.Mcp;
using ModelContextProtocol.Protocol;

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
				"[Live context] changed since revision 1: +docs/api, +tests, -src",
				changed,
				StringComparison.Ordinal);
		}

		using (state.BeginInvocation())
		{
			Assert.Equal(2, state.ReadProfile(temporary.Path).Revision);
			var repeated = Text(state.AppendNotices(McpToolResults.TextSuccess("ok")));
			Assert.DoesNotContain("changed since", repeated, StringComparison.Ordinal);
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
			Assert.Contains("saved window selection could not be read; using revision 1. Retry this call.", response, StringComparison.Ordinal);
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
				"[Live context] saved window selection could not be read; using revision 1. Retry this call.",
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
				"[Live context] changed since revision 1: -src, +all",
				response,
				StringComparison.Ordinal);
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

	private static ProjectSelectionProfile Profile(IReadOnlyCollection<string>? selectedPaths) =>
		new([], [], [], SelectedPaths: selectedPaths);

	private static ProjectProfileLookupResult Found(ProjectSelectionProfile profile) =>
		new(ProjectProfileLookupStatus.Found, profile);

	private static string Text(CallToolResult result) =>
		string.Join('\n', result.Content.OfType<TextContentBlock>().Select(static block => block.Text));

	private sealed class SequenceProfileStore(params ProjectProfileLookupResult[] results) : IProjectProfileStore
	{
		private int index;

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
