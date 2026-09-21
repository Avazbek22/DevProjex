using DevProjex.Application.Context;
using DevProjex.Mcp;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Unit;

public sealed class McpAgentContractLiveContextTests
{
	[Fact]
	public void OutsideFocusCountIncludesOnlyPathsThatWereActuallyDelivered()
	{
		using var temporary = new TemporaryDirectory();
		var state = new McpLiveContextState(
			new McpRootRegistry([temporary.Path]),
			() => new FixedProfileStore(new ProjectProfileLookupResult(ProjectProfileLookupStatus.Missing, null)),
			TimeSpan.Zero);

		using var invocation = state.BeginInvocation();
		state.RecordOutsideSelection(temporary.Path, "src/returned.cs");
		state.RecordOutsideSelection(temporary.Path, "src/not-returned.cs");

		Assert.Equal(
			1,
			state.CountDeliveredOutsideSelection(
				temporary.Path,
				["src/returned.cs", "src/inside-selection.cs", "src/returned.cs"]));
		Assert.Equal(0, state.CountDeliveredOutsideSelection(temporary.Path, ["src/inside-selection.cs"]));
	}

	[Theory]
	[InlineData(ProjectProfileLookupStatus.TemporarilyUnavailable, "Saved selection is busy.", "Retry this call once.")]
	[InlineData(ProjectProfileLookupStatus.InvalidStorage, "Saved selection is invalid or incompatible.", "Ask the user to repair it or update DevProjex; retry after that.")]
	[InlineData(ProjectProfileLookupStatus.UnsupportedFutureSchema, "Saved selection is invalid or incompatible.", "Ask the user to repair it or update DevProjex; retry after that.")]
	public void ReadFailuresGiveAnActionThatMatchesTheirKind(
		ProjectProfileLookupStatus failure,
		string classification,
		string action)
	{
		using var temporary = new TemporaryDirectory();
		var state = new McpLiveContextState(
			new McpRootRegistry([temporary.Path]),
			() => new FixedProfileStore(new ProjectProfileLookupResult(failure, null)),
			TimeSpan.Zero);

		using var invocation = state.BeginInvocation();
		var snapshot = state.ReadProfile(temporary.Path);
		var response = Text(state.AppendNotices(McpToolResults.TextSuccess("ok")));

		Assert.Equal(failure, snapshot.ReadFailure);
		Assert.Contains(classification, response, StringComparison.Ordinal);
		Assert.Contains(action, response, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(ProjectProfileLookupStatus.TemporarilyUnavailable, "Saved selection is busy.")]
	[InlineData(ProjectProfileLookupStatus.InvalidStorage, "Saved selection is invalid or incompatible.")]
	public void LastSuccessfulSnapshotReportsTheRevisionActuallyUsed(
		ProjectProfileLookupStatus failure,
		string classification)
	{
		using var temporary = new TemporaryDirectory();
		var store = new SequenceProfileStore(
			new ProjectProfileLookupResult(ProjectProfileLookupStatus.Found, Profile(["src"])),
			new ProjectProfileLookupResult(failure, null));
		var state = new McpLiveContextState(
			new McpRootRegistry([temporary.Path]),
			() => store,
			TimeSpan.Zero);

		using (state.BeginInvocation())
			Assert.Equal(1, state.ReadProfile(temporary.Path).Revision);
		using (state.BeginInvocation())
		{
			var snapshot = state.ReadProfile(temporary.Path);
			var response = Text(state.AppendNotices(McpToolResults.TextSuccess("ok")));

			Assert.Equal(1, snapshot.Revision);
			Assert.Contains(classification, response, StringComparison.Ordinal);
			Assert.Contains("Using revision 1.", response, StringComparison.Ordinal);
		}
	}

	private static ProjectSelectionProfile Profile(IReadOnlyCollection<string>? selectedPaths) =>
		new([], [], [], SelectedPaths: selectedPaths);

	private static string Text(CallToolResult result) =>
		string.Join('\n', result.Content.OfType<TextContentBlock>().Select(static block => block.Text));

	private sealed class FixedProfileStore(ProjectProfileLookupResult result) : IProjectProfileStore
	{
		public ProjectProfileLookupResult LookupProfile(string localProjectPath, TimeSpan lockTimeout) => result;
		public bool EnsureStorageExists() => true;
		public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile)
		{
			profile = Profile(null);
			return false;
		}
		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile) => true;
		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile, DateTimeOffset updatedUtc) => true;
		public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile) { }
		public ProjectProfileClearStatus ClearAllProfiles() => ProjectProfileClearStatus.Cleared;
	}

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
		public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile) { }
		public ProjectProfileClearStatus ClearAllProfiles() => ProjectProfileClearStatus.Cleared;
	}
}
