using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using DevProjex.Application.Selection;

namespace DevProjex.Mcp;

internal sealed class McpLiveContextState(
	McpRootRegistry roots,
	Func<IProjectProfileStore> profileStore,
	TimeSpan? lookupTimeout = null,
	McpToolSet toolSet = McpToolSet.Full)
{
	private static readonly TimeSpan DefaultLookupTimeout = TimeSpan.FromMilliseconds(250);
	private readonly AsyncLocal<InvocationState?> invocation = new();
	private readonly Dictionary<string, RootState> states = new(PathComparer.Default);
	private readonly object sync = new();
	private readonly ConditionalWeakTable<McpStoredResultContext, StoredResultPolicy> storedPolicies = new();
	private readonly TimeSpan profileLookupTimeout = lookupTimeout ?? DefaultLookupTimeout;

	public IDisposable BeginInvocation()
	{
		var previous = invocation.Value;
		invocation.Value = new InvocationState();
		return new InvocationScope(this, previous);
	}

	public McpLiveProfileSnapshot ReadProfile(string projectRoot)
	{
		var snapshot = ReadCurrentProfile(projectRoot);
		return snapshot with
		{
			Profile = snapshot.Profile is null
				? null
				: ProjectSelectionProfileBuilder.Clone(snapshot.Profile)
		};
	}

	public int RefreshProfile(string projectRoot) => ReadCurrentProfile(projectRoot).Revision;

	public int? CurrentInvocationRevision(string projectRoot)
	{
		var normalizedRoot = PathUtility.Normalize(projectRoot);
		lock (sync)
		{
			return states.TryGetValue(normalizedRoot, out var state) && state.Initialized
				? state.Revision
				: null;
		}
	}

	public bool HasSelectedFileCount(
		string projectRoot,
		int revision,
		McpRootMonitorStamp? rootRevision = null)
	{
		var normalizedRoot = PathUtility.Normalize(projectRoot);
		lock (sync)
			return states.TryGetValue(normalizedRoot, out var state) &&
				   state.Revision == revision &&
				   (rootRevision is null || state.SelectedFileRootRevision == rootRevision) &&
				   state.SelectedFileCount.HasValue;
	}

	private McpLiveProfileSnapshot ReadCurrentProfile(string projectRoot)
	{
		var normalizedRoot = PathUtility.Normalize(projectRoot);
		var active = invocation.Value;
		if (active is not null)
		{
			return active.Profiles.GetOrAdd(
				normalizedRoot,
				root => new Lazy<McpLiveProfileSnapshot>(
					() => ReadProfileFromStore(root, active),
					LazyThreadSafetyMode.ExecutionAndPublication)).Value;
		}
		return ReadProfileFromStore(normalizedRoot, active: null);
	}

	private McpLiveProfileSnapshot ReadProfileFromStore(
		string normalizedRoot,
		InvocationState? active)
	{
		var configuredRoot = roots.ResolveConfiguredRoot(normalizedRoot);
		var store = profileStore();
		var lookup = store.LookupProfile(configuredRoot, profileLookupTimeout);
		if (lookup.Status == ProjectProfileLookupStatus.Missing &&
			lookup.RecoveryStatus is null &&
			!PathComparer.Default.Equals(configuredRoot, normalizedRoot))
		{
			lookup = store.LookupProfile(normalizedRoot, profileLookupTimeout);
		}
		lock (sync)
		{
			if (!states.TryGetValue(normalizedRoot, out var state))
			{
				state = new RootState(normalizedRoot);
				states.Add(normalizedRoot, state);
			}

			ApplyLookup(state, lookup);
			active?.Roots.Add(normalizedRoot);
			var snapshot = new McpLiveProfileSnapshot(
				state.Profile,
				state.Revision,
				state.IsMissing,
				state.ReadFailure is not null,
				state.HasSuccessfulSnapshot,
				state.ReadFailure);
			return snapshot;
		}
	}

	public void RecordPlan(
		string projectRoot,
		ProjectContextPlan plan,
		McpRootMonitorStamp? rootRevision = null)
	{
		ArgumentNullException.ThrowIfNull(plan);
		var normalizedRoot = PathUtility.Normalize(projectRoot);
		var active = invocation.Value;
		lock (sync)
		{
			if (!states.TryGetValue(normalizedRoot, out var state) ||
				active is null ||
				!active.Profiles.TryGetValue(normalizedRoot, out var profile) ||
				!profile.IsValueCreated ||
				profile.Value is not { } current ||
				current.Revision != state.Revision)
				return;
			state.EffectiveTree = new WeakReference<TreeNodeDescriptor>(plan.EffectiveTree);
			state.PendingChange = ClassifyPendingChange(state.Root, state.PendingChange, plan.EffectiveTree);
			state.SelectedFileCount = plan.IncludedFiles.Count;
			state.SelectedFileRootRevision = rootRevision;
			active.Roots.Add(normalizedRoot);
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

	public int CountDeliveredOutsideSelection(
		string projectRoot,
		IEnumerable<string> deliveredRelativePaths)
	{
		ArgumentNullException.ThrowIfNull(deliveredRelativePaths);
		var active = invocation.Value;
		if (active is null)
			return 0;
		return deliveredRelativePaths
			.Where(path => active.OutsidePaths.Contains(BuildPathIdentity(projectRoot, path)))
			.Distinct(StringComparer.Ordinal)
			.Count();
	}

	public McpStoredResultContext? RecordPackBuild(string projectRoot, string? packId)
	{
		var normalizedRoot = PathUtility.Normalize(projectRoot);
		lock (sync)
		{
			if (!states.TryGetValue(normalizedRoot, out var state))
				return null;
			invocation.Value?.AdditionalNotices.Add(
				$"[Live context] pack built at revision {state.Revision}.");
			if (string.IsNullOrEmpty(packId))
				return null;
			var stored = new McpStoredResultContext(normalizedRoot, state.Revision, McpStoredResultKind.Pack);
			storedPolicies.Add(stored, new StoredResultPolicy(state.ProtectionRevision));
			return stored;
		}
	}

	public McpStoredResultContext? RecordStoredResult(string projectRoot, McpStoredResultKind kind)
	{
		var normalizedRoot = PathUtility.Normalize(projectRoot);
		lock (sync)
		{
			if (!states.TryGetValue(normalizedRoot, out var state))
				return null;
			var stored = new McpStoredResultContext(normalizedRoot, state.Revision, kind);
			storedPolicies.Add(stored, new StoredResultPolicy(state.ProtectionRevision));
			return stored;
		}
	}

	public bool RefreshStoredResult(McpStoredResultContext? stored)
	{
		if (stored is null)
			return false;

		var current = ReadCurrentProfile(stored.Root);
		if (current.IsReadFailure)
		{
			throw new McpToolException(
				"DPX-MCP-STORED-PROTECTION-UNAVAILABLE",
				"DPX-MCP-STORED-PROTECTION-UNAVAILABLE: the current saved protection policy could not be verified. " +
				"Retry read_pack after the saved selection is readable; do not use this stored result until then.");
		}
		if (storedPolicies.TryGetValue(stored, out var policy))
		{
			lock (sync)
			{
				if (states.TryGetValue(PathUtility.Normalize(stored.Root), out var state) &&
					state.ProtectionRevision != policy.ProtectionRevision)
				{
					var protectionRefreshTool = McpStoredResultAdvice.RefreshTool(toolSet, stored.Kind);
					var remedy = protectionRefreshTool is null
						? "Create a new protected result before reading it."
						: $"Call {protectionRefreshTool} again before read_pack.";
					throw new McpToolException(
						"DPX-MCP-STORED-PROTECTION-CHANGED",
						"DPX-MCP-STORED-PROTECTION-CHANGED: the saved protection policy changed after this result was stored. " +
						remedy);
				}
			}
		}
		if (current.Revision == stored.Revision)
			return false;
		var refreshTool = McpStoredResultAdvice.RefreshTool(toolSet, stored.Kind);
		var advice = refreshTool is null
			? string.Empty
			: $" Call {refreshTool} again to include the current selection.";
		invocation.Value?.AdditionalNotices.Add(
			$"[Live context] {McpStoredResultAdvice.ResultName(stored.Kind)} built at revision {stored.Revision}; " +
			$"window is at revision {current.Revision}.{advice}");
		return true;
	}

	public CallToolResult AppendNotices(CallToolResult result)
	{
		ArgumentNullException.ThrowIfNull(result);
		var active = invocation.Value;
		if (active is { Roots.Count: 0 })
		{
			foreach (var root in roots.Roots)
				_ = ReadCurrentProfile(root);
		}
		var observedRoots = active is { Roots.Count: > 0 }
			? active.Roots.ToArray()
			: roots.Roots.Select(PathUtility.Normalize).ToArray();
		var notices = new List<string>();
		var untrustedDetails = new List<string>();
		lock (sync)
		{
			if (active is not null)
				notices.AddRange(active.AdditionalNotices);
			var configuredRoots = roots.Roots
				.Select(PathUtility.Normalize)
				.Distinct(PathComparer.Default)
				.Order(ProjectTreePathIdentity.CanonicalComparer)
				.ToArray();
			var dynamicRoots = observedRoots
				.Where(root => !configuredRoots.Contains(root, PathComparer.Default))
				.Distinct(PathComparer.Default)
				.Order(ProjectTreePathIdentity.CanonicalComparer);
			var noticeRoots = configuredRoots.Concat(dynamicRoots).ToArray();
			var orderedRoots = observedRoots.Order(ProjectTreePathIdentity.CanonicalComparer).ToArray();
			foreach (var root in orderedRoots)
			{
				if (!states.TryGetValue(root, out var state))
					continue;
				var rootIndex = Array.FindIndex(
					noticeRoots,
					candidate => PathComparer.Default.Equals(candidate, root)) + 1;
				AppendRootNotices(
					notices,
					untrustedDetails,
					state,
					noticeRoots.Length > 1,
					rootIndex,
					noticeRoots.Length);
			}
		}
		if (notices.Count == 0 && untrustedDetails.Count == 0)
			return result;

		var content = result.Content.ToList();
		if (untrustedDetails.Count > 0)
		{
			content.Add(new TextContentBlock
			{
				Text = McpSpotlight.Wrap(string.Join(Environment.NewLine, untrustedDetails))
			});
		}
		if (notices.Count > 0)
			content.Add(new TextContentBlock { Text = string.Join(Environment.NewLine, notices) });
		result.Content = content;
		return result;
	}

	private static void ApplyLookup(RootState state, ProjectProfileLookupResult lookup)
	{
		if (lookup.RecoveryStatus is not null && state.HasSuccessfulSnapshot)
		{
			state.ReadFailure = lookup.RecoveryStatus;
			return;
		}
		if (lookup.Status is ProjectProfileLookupStatus.Found && lookup.Profile is not null)
		{
			var profile = ProjectSelectionProfileBuilder.Clone(lookup.Profile);
			ApplySuccessfulSnapshot(state, profile, isMissing: false);
			state.ReadFailure = lookup.RecoveryStatus;
			return;
		}
		if (lookup.Status == ProjectProfileLookupStatus.Missing && lookup.RecoveryStatus is null)
		{
			ApplySuccessfulSnapshot(state, profile: null, isMissing: true);
			state.ReadFailure = lookup.RecoveryStatus;
			return;
		}

		if (!state.Initialized)
		{
			state.Initialized = true;
			state.Revision = 1;
			state.IsMissing = true;
			state.Fingerprint = "missing";
		}
		state.ReadFailure = lookup.RecoveryStatus ?? lookup.Status;
	}

	private static void ApplySuccessfulSnapshot(
		RootState state,
		ProjectSelectionProfile? profile,
		bool isMissing)
	{
		var fingerprint = profile is null ? "missing" : BuildFingerprint(profile);
		var protectionFingerprint = BuildProtectionFingerprint(profile);
		var frontier = NormalizeFrontier(profile?.SelectedPaths);
		if (!state.Initialized)
		{
			state.Initialized = true;
			state.Revision = 1;
			state.Fingerprint = fingerprint;
			state.ProtectionFingerprint = protectionFingerprint;
			state.ProtectionRevision = 1;
			state.Frontier = frontier;
		}
		if (!StringComparer.Ordinal.Equals(state.ProtectionFingerprint, protectionFingerprint))
		{
			state.ProtectionFingerprint = protectionFingerprint;
			state.ProtectionRevision++;
		}
		if (!StringComparer.Ordinal.Equals(state.Fingerprint, fingerprint))
		{
			var previousRevision = state.Revision;
			state.Revision++;
			state.SelectedFileCount = null;
			state.SelectedFileRootRevision = null;
			var frontierChanges = BuildFrontierChanges(state.Frontier, frontier);
			state.PendingChange = ClassifyPendingChange(
				state.Root,
				new PendingChange(previousRevision, frontierChanges),
				GetEffectiveTree(state));
			state.Fingerprint = fingerprint;
			state.Frontier = frontier;
		}

		state.Profile = profile;
		state.IsMissing = isMissing;
		state.HasSuccessfulSnapshot = true;
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

	private static string BuildProtectionFingerprint(ProjectSelectionProfile? profile)
	{
		var value = new StringBuilder();
		value.Append("hide-private-data:")
			.Append(profile?.SelectedIgnoreOptions.Contains(IgnoreOptionId.HidePrivateData) == true)
			.Append(';');
		foreach (var mark in (profile?.MarkedSecrets ?? []).OrderBy(static item => item.H, StringComparer.Ordinal))
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

	private static IReadOnlyList<FrontierChange> BuildFrontierChanges(
		string[]? before,
		string[]? after)
	{
		if (before is null && after is null)
			return [];
		if (before is null)
			return
			[
				new FrontierChange(Added: false, FrontierChangeKind.All, Path: null),
				.. (after ?? []).Select(path => CreatePathChange(path, added: true))
			];
		if (after is null)
			return
			[
				.. before.Select(path => CreatePathChange(path, added: false)),
				new FrontierChange(Added: true, FrontierChangeKind.All, Path: null)
			];

		var previous = before.ToHashSet(StringComparer.Ordinal);
		var current = after.ToHashSet(StringComparer.Ordinal);
		return current.Except(previous, StringComparer.Ordinal).Order(StringComparer.Ordinal)
			.Select(path => CreatePathChange(path, added: true))
			.Concat(previous.Except(current, StringComparer.Ordinal).Order(StringComparer.Ordinal)
				.Select(path => CreatePathChange(path, added: false)))
			.ToArray();
	}

	private static FrontierChange CreatePathChange(string path, bool added) =>
		new(added, FrontierChangeKind.Path, path);

	private static PendingChange? ClassifyPendingChange(
		string root,
		PendingChange? pendingChange,
		TreeNodeDescriptor? effectiveTree)
	{
		if (pendingChange is null ||
			effectiveTree is null ||
			pendingChange.Changes.All(static change => change.Path is null))
			return pendingChange;

		var remainingPaths = pendingChange.Changes
			.Where(static change => change.Path is not null)
			.Select(static change => change.Path!)
			.ToHashSet(ProjectTreePathIdentity.CanonicalComparer);
		var kindsByPath = new Dictionary<string, FrontierChangeKind>(ProjectTreePathIdentity.CanonicalComparer);
		var pendingNodes = new Stack<TreeNodeDescriptor>();
		pendingNodes.Push(effectiveTree);
		while (remainingPaths.Count > 0 && pendingNodes.TryPop(out var node))
		{
			var relativePath = Path.GetRelativePath(root, node.FullPath).Replace('\\', '/');
			if (remainingPaths.Remove(relativePath) &&
				!relativePath.Equals(".", StringComparison.Ordinal) &&
				!Path.IsPathRooted(relativePath) &&
				!relativePath.Equals("..", StringComparison.Ordinal) &&
				!relativePath.StartsWith("../", StringComparison.Ordinal))
			{
				kindsByPath[relativePath] = node.IsDirectory
					? FrontierChangeKind.Folder
					: FrontierChangeKind.File;
			}

			for (var index = node.Children.Count - 1; index >= 0; index--)
				pendingNodes.Push(node.Children[index]);
		}

		var classified = pendingChange.Changes
			.Select(change => change.Path is not null && kindsByPath.TryGetValue(change.Path, out var kind)
				? change with { Kind = kind }
				: change)
			.ToArray();
		return pendingChange with { Changes = classified };
	}

	private static TreeNodeDescriptor? GetEffectiveTree(RootState state) =>
		state.EffectiveTree is { } reference && reference.TryGetTarget(out var tree)
			? tree
			: null;

	private static void AppendRootNotices(
		List<string> notices,
		List<string> untrustedDetails,
		RootState state,
		bool includeRoot,
		int rootIndex,
		int rootCount)
	{
		if (state.ReadFailure is not null)
		{
			notices.Add(FormatReadFailure(state));
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
			if (changed.Changes.Count == 0)
			{
				notices.Add(
					$"[Live context] changed since revision {changed.PreviousRevision}: selection settings changed");
			}
			else
			{
				var shownPaths = changed.Changes.Where(static change => change.Path is not null).Take(5).ToArray();
				var remainingPaths = changed.Changes.Count(static change => change.Path is not null) - shownPaths.Length;
				var suffix = remainingPaths > 0
					? $" · {shownPaths.Length} names shown, {remainingPaths} more"
					: string.Empty;
				notices.Add(
					$"[Live context] changed since revision {changed.PreviousRevision}: {FormatChangeSummary(changed.Changes)}{suffix}");
				if (shownPaths.Length > 0)
				{
					untrustedDetails.Add(
						$"Live context changed paths since revision {changed.PreviousRevision}:" + Environment.NewLine +
						string.Join(
							Environment.NewLine,
							shownPaths.Select(static change =>
								(change.Added ? "+" : "-") + McpTextEscaping.EscapeSingleLine(change.Path!))));
				}
			}
			state.PendingChange = null;
		}

		var count = state.SelectedFileCount?.ToString(CultureInfo.InvariantCulture) ?? "0";
		var rootSuffix = includeRoot ? $" · root {rootIndex} of {rootCount}" : string.Empty;
		notices.Add($"[Live context] revision {state.Revision} · {count} files selected in the window{rootSuffix}");
		if (includeRoot)
		{
			untrustedDetails.Add(
				$"Live context root {rootIndex} name:" + Environment.NewLine +
				McpTextEscaping.EscapeSingleLine(McpRootRegistry.GetProjectName(state.Root)));
		}
	}

	private static string FormatReadFailure(RootState state)
		=> FormatReadFailure(
			state.ReadFailure,
			state.HasSuccessfulSnapshot ? state.Revision : null);

	internal static string FormatReadFailure(
		ProjectProfileLookupStatus? failure,
		int? revision)
	{
		var revisionNotice = revision.HasValue
			? $" Using revision {revision.Value}."
			: string.Empty;
		return failure == ProjectProfileLookupStatus.TemporarilyUnavailable
			? $"[Live context] Saved selection is busy.{revisionNotice} Retry this call once."
			: $"[Live context] Saved selection is invalid or incompatible.{revisionNotice} " +
			  "Ask the user to repair it or update DevProjex; retry after that.";
	}

	private static string FormatChangeSummary(IReadOnlyList<FrontierChange> changes)
	{
		var parts = new List<string>();
		var written = new HashSet<(bool Added, FrontierChangeKind Kind)>();
		foreach (var change in changes)
		{
			if (change.Kind == FrontierChangeKind.All)
			{
				parts.Add(change.Added ? "+all" : "-all");
				continue;
			}
			if (!written.Add((change.Added, change.Kind)))
				continue;
			var count = changes.Count(candidate => candidate.Added == change.Added && candidate.Kind == change.Kind);
			var name = change.Kind switch
			{
				FrontierChangeKind.File => count == 1 ? "file" : "files",
				FrontierChangeKind.Folder => count == 1 ? "folder" : "folders",
				_ => count == 1 ? "path" : "paths"
			};
			parts.Add($"{(change.Added ? '+' : '-')}{count} {name}");
		}
		return string.Join(", ", parts);
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
		public string ProtectionFingerprint { get; set; } = string.Empty;
		public int ProtectionRevision { get; set; }
		public string[]? Frontier { get; set; }
		public ProjectSelectionProfile? Profile { get; set; }
		public bool IsMissing { get; set; }
		public bool HasSuccessfulSnapshot { get; set; }
		public ProjectProfileLookupStatus? ReadFailure { get; set; }
		public int? SelectedFileCount { get; set; }
		public McpRootMonitorStamp? SelectedFileRootRevision { get; set; }
		public WeakReference<TreeNodeDescriptor>? EffectiveTree { get; set; }
		public PendingChange? PendingChange { get; set; }
	}

	private sealed class InvocationState
	{
		public ConcurrentDictionary<string, Lazy<McpLiveProfileSnapshot>> Profiles { get; } =
			new(PathComparer.Default);
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

	private enum FrontierChangeKind
	{
		All,
		File,
		Folder,
		Path
	}

	private sealed record FrontierChange(bool Added, FrontierChangeKind Kind, string? Path);

	private sealed record PendingChange(int PreviousRevision, IReadOnlyList<FrontierChange> Changes);

	private sealed record StoredResultPolicy(int ProtectionRevision);
}

internal sealed record McpLiveProfileSnapshot(
	ProjectSelectionProfile? Profile,
	int Revision,
	bool IsMissing,
	bool IsReadFailure,
	bool HasSuccessfulSnapshot,
	ProjectProfileLookupStatus? ReadFailure = null);
