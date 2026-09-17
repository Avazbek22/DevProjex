using System.Globalization;
using DevProjex.Application.Selection;

namespace DevProjex.Mcp;

internal sealed class McpLiveContextState(
	McpRootRegistry roots,
	Func<IProjectProfileStore> profileStore,
	TimeSpan? lookupTimeout = null)
{
	private static readonly TimeSpan DefaultLookupTimeout = TimeSpan.FromMilliseconds(250);
	private readonly AsyncLocal<InvocationState?> invocation = new();
	private readonly Dictionary<string, RootState> states = new(PathComparer.Default);
	private readonly Dictionary<string, StoredResultState> storedResults = new(StringComparer.Ordinal);
	private readonly object sync = new();
	private readonly TimeSpan profileLookupTimeout = lookupTimeout ?? DefaultLookupTimeout;

	public IDisposable BeginInvocation()
	{
		var previous = invocation.Value;
		invocation.Value = new InvocationState();
		return new InvocationScope(this, previous);
	}

	public McpLiveProfileSnapshot ReadProfile(string projectRoot)
	{
		var normalizedRoot = PathUtility.Normalize(projectRoot);
		var lookup = profileStore().LookupProfile(normalizedRoot, profileLookupTimeout);
		lock (sync)
		{
			if (!states.TryGetValue(normalizedRoot, out var state))
			{
				state = new RootState(normalizedRoot);
				states.Add(normalizedRoot, state);
			}

			ApplyLookup(state, lookup);
			invocation.Value?.Roots.Add(normalizedRoot);
			return new McpLiveProfileSnapshot(
				state.Profile is null ? null : ProjectSelectionProfileBuilder.Clone(state.Profile),
				state.Revision,
				state.IsMissing,
				state.ReadFailure is not null);
		}
	}

	public void RecordPlan(string projectRoot, ProjectContextPlan plan)
	{
		ArgumentNullException.ThrowIfNull(plan);
		var normalizedRoot = PathUtility.Normalize(projectRoot);
		lock (sync)
		{
			if (!states.TryGetValue(normalizedRoot, out var state))
				return;
			state.SelectedFileCount = plan.IncludedFiles.Count;
			invocation.Value?.Roots.Add(normalizedRoot);
		}
	}

	public void RecordOutsideSelection(string projectRoot, string relativePath)
	{
		var active = invocation.Value;
		if (active is null)
			return;
		active.OutsidePaths.Add(BuildPathIdentity(projectRoot, relativePath));
	}

	public bool IsOutsideSelection(string projectRoot, string relativePath) =>
		invocation.Value?.OutsidePaths.Contains(BuildPathIdentity(projectRoot, relativePath)) == true;

	public void RecordPackBuild(string projectRoot, string? packId)
	{
		var normalizedRoot = PathUtility.Normalize(projectRoot);
		lock (sync)
		{
			if (!states.TryGetValue(normalizedRoot, out var state))
				return;
			if (!string.IsNullOrEmpty(packId))
				storedResults[packId] = new StoredResultState(normalizedRoot, state.Revision);
			invocation.Value?.AdditionalNotices.Add(
				$"[Live context] pack built at revision {state.Revision}.");
		}
	}

	public void RecordStoredResult(string projectRoot, string packId)
	{
		var normalizedRoot = PathUtility.Normalize(projectRoot);
		lock (sync)
		{
			if (states.TryGetValue(normalizedRoot, out var state))
				storedResults[packId] = new StoredResultState(normalizedRoot, state.Revision);
		}
	}

	public void RefreshStoredResult(string packId)
	{
		StoredResultState stored;
		lock (sync)
		{
			if (!storedResults.TryGetValue(packId, out stored!))
				return;
		}

		var current = ReadProfile(stored.Root);
		if (current.Revision == stored.Revision)
			return;
		invocation.Value?.AdditionalNotices.Add(
			$"[Live context] pack built at revision {stored.Revision}; window is at revision {current.Revision}. " +
			"Call pack_context again to include the current selection.");
	}

	public CallToolResult AppendNotices(CallToolResult result)
	{
		ArgumentNullException.ThrowIfNull(result);
		var active = invocation.Value;
		var observedRoots = active is { Roots.Count: > 0 }
			? active.Roots.ToArray()
			: roots.Roots.Select(PathUtility.Normalize).ToArray();
		var notices = new List<string>();
		lock (sync)
		{
			if (active is not null)
				notices.AddRange(active.AdditionalNotices);
			foreach (var root in observedRoots.Order(ProjectTreePathIdentity.CanonicalComparer))
			{
				if (!states.TryGetValue(root, out var state))
					continue;
				AppendRootNotices(notices, state, roots.Roots.Count > 1);
			}
		}
		if (notices.Count == 0)
			return result;

		result.Content =
		[
			.. result.Content,
			new TextContentBlock { Text = string.Join(Environment.NewLine, notices) }
		];
		return result;
	}

	private static void ApplyLookup(RootState state, ProjectProfileLookupResult lookup)
	{
		if (lookup.Status is ProjectProfileLookupStatus.Found && lookup.Profile is not null)
		{
			var profile = ProjectSelectionProfileBuilder.Clone(lookup.Profile);
			ApplySuccessfulSnapshot(state, profile, isMissing: false);
			state.ReadFailure = null;
			return;
		}
		if (lookup.Status == ProjectProfileLookupStatus.Missing)
		{
			ApplySuccessfulSnapshot(state, profile: null, isMissing: true);
			state.ReadFailure = null;
			return;
		}

		if (!state.Initialized)
		{
			state.Initialized = true;
			state.Revision = 1;
			state.IsMissing = true;
			state.Fingerprint = "missing";
		}
		state.ReadFailure = lookup.Status;
	}

	private static void ApplySuccessfulSnapshot(
		RootState state,
		ProjectSelectionProfile? profile,
		bool isMissing)
	{
		var fingerprint = profile is null ? "missing" : BuildFingerprint(profile);
		var frontier = NormalizeFrontier(profile?.SelectedPaths);
		if (!state.Initialized)
		{
			state.Initialized = true;
			state.Revision = 1;
			state.Fingerprint = fingerprint;
			state.Frontier = frontier;
		}
		else if (!StringComparer.Ordinal.Equals(state.Fingerprint, fingerprint))
		{
			var previousRevision = state.Revision;
			state.Revision++;
			state.PendingChange = new PendingChange(
				previousRevision,
				BuildFrontierChanges(state.Frontier, frontier));
			state.Fingerprint = fingerprint;
			state.Frontier = frontier;
		}

		state.Profile = profile;
		state.IsMissing = isMissing;
	}

	private static string BuildFingerprint(ProjectSelectionProfile profile)
	{
		var value = new StringBuilder();
		AppendCollection(value, profile.SelectedRootFolders, ProjectTreePathIdentity.CanonicalComparer);
		AppendCollection(value, profile.SelectedExtensions, StringComparer.OrdinalIgnoreCase);
		AppendCollection(value, profile.SelectedIgnoreOptions.Select(static item => item.ToString()), StringComparer.Ordinal);
		AppendStates(value, profile.RootFolderStates, ProjectTreePathIdentity.CanonicalComparer);
		AppendStates(value, profile.ExtensionStates, StringComparer.OrdinalIgnoreCase);
		AppendStates(value, profile.IgnoreOptionStates?.ToDictionary(
			static item => item.Key.ToString(),
			static item => item.Value,
			StringComparer.Ordinal), StringComparer.Ordinal);
		if (profile.SelectedPaths is null)
			value.Append("paths:null|");
		else
			AppendCollection(value, NormalizeFrontier(profile.SelectedPaths)!, StringComparer.Ordinal);
		foreach (var mark in (profile.MarkedSecrets ?? []).OrderBy(static item => item.H, StringComparer.Ordinal))
		{
			value.Append(mark.H).Append('|').Append(mark.Key).Append('|').Append(mark.Length).Append('|')
				.Append(mark.RelativePath).Append('|').Append(mark.SourceOffset).Append('|').Append(mark.Class).Append(';');
		}
		return value.ToString();
	}

	private static void AppendCollection(
		StringBuilder value,
		IEnumerable<string> items,
		IComparer<string> comparer)
	{
		foreach (var item in items.Distinct(StringComparer.Ordinal).Order(comparer))
			value.Append(item.Length).Append(':').Append(item).Append(';');
		value.Append('|');
	}

	private static void AppendStates<TKey>(
		StringBuilder value,
		IReadOnlyDictionary<TKey, bool>? states,
		IComparer<TKey> comparer)
		where TKey : notnull
	{
		if (states is null)
		{
			value.Append("null|");
			return;
		}
		foreach (var item in states.OrderBy(static item => item.Key, comparer))
			value.Append(item.Key).Append('=').Append(item.Value ? '1' : '0').Append(';');
		value.Append('|');
	}

	private static string[]? NormalizeFrontier(IReadOnlyCollection<string>? paths)
	{
		if (paths is null)
			return null;
		return paths
			.Select(static path => path.Replace('\\', '/').Trim('/'))
			.Where(static path => path.Length > 0)
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)
			.ToArray();
	}

	private static IReadOnlyList<string> BuildFrontierChanges(string[]? before, string[]? after)
	{
		if (before is null && after is null)
			return [];
		if (before is null)
			return ["-all", .. (after ?? []).Select(static path => $"+{path}")];
		if (after is null)
			return [.. before.Select(static path => $"-{path}"), "+all"];

		var previous = before.ToHashSet(StringComparer.Ordinal);
		var current = after.ToHashSet(StringComparer.Ordinal);
		return current.Except(previous, StringComparer.Ordinal).Order(StringComparer.Ordinal)
			.Select(static path => $"+{path}")
			.Concat(previous.Except(current, StringComparer.Ordinal).Order(StringComparer.Ordinal)
				.Select(static path => $"-{path}"))
			.ToArray();
	}

	private static void AppendRootNotices(List<string> notices, RootState state, bool includeRoot)
	{
		if (state.ReadFailure is not null)
		{
			notices.Add(
				$"[Live context] saved window selection could not be read; using revision {state.Revision}. Retry this call.");
		}
		else if (state.IsMissing)
		{
			var message = "[Live context] no window selection saved for this root; using server defaults.";
			if (IsWslMount(state.Root))
			{
				message += " If the DevProjex window runs on Windows, live context across WSL is not supported yet.";
			}
			notices.Add(message);
		}
		else if (state.Profile?.SelectedPaths is { Count: 0 })
		{
			notices.Add("[Live context] the window selects no files; tick files in the DevProjex window.");
		}

		if (state.PendingChange is { } changed)
		{
			var shown = changed.Changes.Take(5).ToArray();
			var remaining = changed.Changes.Count - shown.Length;
			var suffix = remaining > 0 ? $" and {remaining} more" : string.Empty;
			notices.Add(
				$"[Live context] changed since revision {changed.PreviousRevision}: {string.Join(", ", shown)}{suffix}");
			state.PendingChange = null;
		}

		var count = state.SelectedFileCount?.ToString(CultureInfo.InvariantCulture) ?? "0";
		var rootSuffix = includeRoot ? $" · root {McpRootRegistry.GetProjectName(state.Root)}" : string.Empty;
		notices.Add($"[Live context] revision {state.Revision} · {count} files selected in the window{rootSuffix}");
	}

	private static bool IsWslMount(string path) =>
		path.Length >= 7 &&
		path.StartsWith("/mnt/", StringComparison.Ordinal) &&
		char.IsAsciiLetter(path[5]) &&
		path[6] == '/';

	private static string BuildPathIdentity(string root, string relativePath) =>
		PathUtility.Normalize(root) + "\n" + relativePath.Replace('\\', '/');

	private sealed class RootState(string root)
	{
		public string Root { get; } = root;
		public bool Initialized { get; set; }
		public int Revision { get; set; }
		public string Fingerprint { get; set; } = string.Empty;
		public string[]? Frontier { get; set; }
		public ProjectSelectionProfile? Profile { get; set; }
		public bool IsMissing { get; set; }
		public ProjectProfileLookupStatus? ReadFailure { get; set; }
		public int? SelectedFileCount { get; set; }
		public PendingChange? PendingChange { get; set; }
	}

	private sealed class InvocationState
	{
		public HashSet<string> Roots { get; } = new(PathComparer.Default);
		public HashSet<string> OutsidePaths { get; } = new(StringComparer.Ordinal);
		public List<string> AdditionalNotices { get; } = [];
	}

	private sealed class InvocationScope(McpLiveContextState owner, InvocationState? previous) : IDisposable
	{
		private int disposed;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref disposed, 1) == 0)
				owner.invocation.Value = previous;
		}
	}

	private sealed record PendingChange(int PreviousRevision, IReadOnlyList<string> Changes);
	private sealed record StoredResultState(string Root, int Revision);
}

internal sealed record McpLiveProfileSnapshot(
	ProjectSelectionProfile? Profile,
	int Revision,
	bool IsMissing,
	bool IsReadFailure);
