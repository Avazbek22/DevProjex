using System.Globalization;
using DevProjex.Application.Context;
using DevProjex.Application.Dependencies;
using DevProjex.Application.Diff;
using DevProjex.Application.Secrets;
using DevProjex.Kernel;

namespace DevProjex.Mcp;

internal sealed record McpRelatedNameUse(string Name, IReadOnlyList<int> Lines);

/// <param name="Within">Top-level declarations of the file that hold the uses.</param>
/// <param name="Via">The re-exporting file the uses reach the seed through, or null for a direct dependent.</param>
internal sealed record McpRelatedFocusedFile(
	RelatedFile File,
	IReadOnlyList<McpRelatedNameUse> Uses,
	IReadOnlyList<string> Within,
	string? Via);

/// <summary>
/// The dependents of one changed seed, grouped by whether they use a declaration the change
/// touched. Every name was confirmed in protected text; <see cref="Others"/> keeps the
/// dependents that import the seed for something else, or whose text could not be inspected.
/// </summary>
internal sealed record McpRelatedChangeFocus(
	string Seed,
	IReadOnlyList<DependencyChangedDeclaration> Changed,
	IReadOnlyList<McpRelatedFocusedFile> Using,
	IReadOnlyList<McpRelatedFocusedFile> Tests,
	IReadOnlyList<string> ReExporters,
	IReadOnlyList<RelatedFile> Others,
	int UninspectedThroughReExports);

internal sealed partial class DevProjexMcpTools
{
	internal const string RelatedChangeFocusNotice =
		"[Change focus] dependents of changed seeds are grouped by use of the declarations the Git changes touch; " +
		"use lines come from protected text.";
	private const string UninspectedDependentReason = "not inspected for changed declarations";
	private const int MaximumFocusedFilesPerGroup = 25;
	private const int MaximumListedChangedDeclarations = 10;

	private sealed class RelatedFocusBudget
	{
		public long AdmittedBytes;
	}

	private sealed record FocusCandidate(
		SeedRelatedFiles Seed,
		FileFacts Facts,
		IReadOnlyList<DependencyChangedDeclaration> Changed);

	private sealed record SeedFocusText(
		IReadOnlyList<DependencyChangedDeclaration> Changed,
		IReadOnlySet<string> WildcardNames);

	private sealed record DependentFocusText(
		DependencyNameUses Uses,
		IReadOnlyList<string> Within,
		IReadOnlySet<string> WildcardNames);

	/// <summary>
	/// The change focus of every seed the Git changes touch, keyed by seed. The changes are the
	/// call's own momentary Git scope, or the uncommitted changes when the call has none. Any
	/// failure to read them leaves the answer without a focus rather than failing the call.
	/// </summary>
	private async Task<IReadOnlyDictionary<string, McpRelatedChangeFocus>> BuildRelatedChangeFocusAsync(
		ProjectContextPlan plan,
		Func<Task<ProjectContextPlan>> buildChangesPlan,
		DependencyRelatedResult related,
		DependencyDirection direction,
		CancellationToken cancellationToken)
	{
		var none = new Dictionary<string, McpRelatedChangeFocus>(StringComparer.Ordinal);
		if (direction == DependencyDirection.Dependencies ||
			related.Seeds.All(static seed => seed.Dependents.Count == 0 || seed.NoFactsReason is not null))
			return none;

		ProjectDiffResult diff;
		try
		{
			var diffPlan = GitScopeSelection.IsMomentary(plan.Selection.GitMode ?? GitFilteringMode.None)
				? plan
				: await buildChangesPlan().ConfigureAwait(false);
			if (diffPlan.IncludedFiles.Count == 0)
				return none;
			diff = await Projects.CompareVersionsAsync(diffPlan, 0, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is McpToolException or ProjectDiffException or IOException or
											  UnauthorizedAccessException or InvalidOperationException)
		{
			return none;
		}

		var candidates = FindFocusCandidates(diff, related);
		if (candidates.Count == 0)
			return none;

		var budget = new RelatedFocusBudget();
		var seedTexts = new Dictionary<string, SeedFocusText>(StringComparer.Ordinal);
		await ConsumeFocusSourcesAsync(plan, candidates.Keys.ToArray(), budget, (path, file) =>
		{
			var candidate = candidates[path];
			var isProtected = ProtectionCheck(file);
			var confirmed = candidate.Changed
				.Where(declaration => declaration.DeclarationLine == 0 ||
					DependencyChangeFocus.LineContainsToken(file.Content, declaration.DeclarationLine, declaration.Name, isProtected))
				.ToArray();
			seedTexts[path] = new SeedFocusText(confirmed, WildcardNames(candidate.Facts, file.Content, confirmed.Select(static item => item.Name)));
		}, cancellationToken).ConfigureAwait(false);

		var index = related.Index;
		var dependentRequests = new Dictionary<string, List<(string Seed, IReadOnlyList<DependencyNameBinding> Bindings, IReadOnlyList<DependencyEdge> References)>>(StringComparer.Ordinal);
		foreach (var (seedPath, seedText) in seedTexts)
		{
			if (seedText.Changed.Count == 0)
				continue;
			var names = seedText.Changed.Select(static item => item.Name).ToHashSet(StringComparer.Ordinal);
			foreach (var dependent in candidates[seedPath].Seed.Dependents)
			{
				if (dependent.Status != ResolutionStatus.Resolved || !index.FileByPath.TryGetValue(dependent.Path, out var facts))
					continue;
				var edges = ResolvedEdges(index, dependent.Path, seedPath);
				var bindings = DependencyChangeFocus.Bindings(facts, seedPath, ImportLines(edges, dependent.Path));
				var references = edges
					.Where(edge => edge.Layer == EvidenceLayer.TypeReference && names.Contains(edge.Reference))
					.ToArray();
				AddRequest(dependentRequests, dependent.Path, (seedPath, bindings, references));
			}
		}

		var dependentTexts = new Dictionary<(string Seed, string Path), DependentFocusText>();
		var inspectedDependents = await ConsumeFocusSourcesAsync(plan, dependentRequests.Keys.ToArray(), budget, (path, file) =>
		{
			var facts = index.FileByPath[path];
			var isProtected = ProtectionCheck(file);
			foreach (var request in dependentRequests[path])
			{
				var names = seedTexts[request.Seed].Changed.Select(static item => item.Name).ToArray();
				var uses = DependencyChangeFocus.FindUses(
					file.Content,
					facts.LanguageId,
					request.Bindings,
					names,
					seedTexts[request.Seed].WildcardNames,
					isProtected,
					cancellationToken);
				uses = AddReferenceUses(uses, file.Content, request.References, isProtected);
				if (uses.UsesAny)
					uses = AddMemberUses(uses, file.Content, facts.LanguageId, seedTexts[request.Seed].Changed, isProtected);
				var within = WithinDeclarations(facts, file.Content, uses, isProtected);
				var reExported = uses.OnlyReExports
					? WildcardNames(facts, file.Content, uses.Bound)
					: new HashSet<string>(StringComparer.Ordinal);
				dependentTexts[(request.Seed, path)] = new DependentFocusText(uses, within, reExported);
			}
		}, cancellationToken).ConfigureAwait(false);

		// One hop past a file that only re-exports changed names, as a package entry point does.
		var hopRequests = new Dictionary<string, List<(string Seed, string Via, IReadOnlyList<DependencyNameBinding> Bindings, IReadOnlyList<string> Names, IReadOnlySet<string> WildcardNames)>>(StringComparer.Ordinal);
		foreach (var ((seedPath, viaPath), text) in dependentTexts)
		{
			if (!text.Uses.OnlyReExports)
				continue;
			var direct = candidates[seedPath].Seed.Dependents.Select(static file => file.Path).ToHashSet(StringComparer.Ordinal);
			var names = text.Uses.Bound.ToArray();
			if (!index.EdgesByTarget.TryGetValue(viaPath, out var incoming))
				continue;
			foreach (var source in incoming
				.Where(edge => edge.Status == ResolutionStatus.Resolved && edge.Layer == EvidenceLayer.ExplicitImport)
				.Select(static edge => edge.Source)
				.Distinct(StringComparer.Ordinal))
			{
				if (source == seedPath || direct.Contains(source) || !index.FileByPath.TryGetValue(source, out var facts))
					continue;
				var bindings = DependencyChangeFocus.Bindings(facts, viaPath, ImportLines(ResolvedEdges(index, source, viaPath), source));
				if (bindings.Count == 0)
					continue;
				AddRequest(hopRequests, source, (seedPath, viaPath, bindings, names, text.WildcardNames));
			}
		}

		var hopUses = new Dictionary<(string Seed, string Path), (string Via, DependencyNameUses Uses, IReadOnlyList<string> Within)>();
		var inspectedHops = await ConsumeFocusSourcesAsync(plan, hopRequests.Keys.ToArray(), budget, (path, file) =>
		{
			var facts = index.FileByPath[path];
			var isProtected = ProtectionCheck(file);
			foreach (var request in hopRequests[path])
			{
				if (hopUses.ContainsKey((request.Seed, path)))
					continue;
				var uses = DependencyChangeFocus.FindUses(
					file.Content,
					facts.LanguageId,
					request.Bindings,
					request.Names,
					request.WildcardNames,
					isProtected,
					cancellationToken);
				if (uses.UsesAny)
					hopUses[(request.Seed, path)] = (request.Via, uses, WithinDeclarations(facts, file.Content, uses, isProtected));
			}
		}, cancellationToken).ConfigureAwait(false);

		var result = new Dictionary<string, McpRelatedChangeFocus>(StringComparer.Ordinal);
		foreach (var (seedPath, seedText) in seedTexts)
		{
			if (seedText.Changed.Count == 0)
				continue;
			var seed = candidates[seedPath].Seed;
			var usingFiles = new List<McpRelatedFocusedFile>();
			var tests = new List<McpRelatedFocusedFile>();
			var reExporters = new List<string>();
			var others = new List<RelatedFile>();
			foreach (var dependent in seed.Dependents)
			{
				if (!dependentTexts.TryGetValue((seedPath, dependent.Path), out var text))
				{
					others.Add(dependentRequests.ContainsKey(dependent.Path) && !inspectedDependents.Contains(dependent.Path)
						? dependent with { Reasons = [UninspectedDependentReason] }
						: dependent);
					continue;
				}
				if (text.Uses.UsesAny)
				{
					var focused = new McpRelatedFocusedFile(dependent, ToNameUses(text.Uses), text.Within, Via: null);
					(DependencyTestFiles.IsTestPath(dependent.Path) ? tests : usingFiles).Add(focused);
				}
				else if (text.Uses.OnlyReExports)
					reExporters.Add(dependent.Path);
				else
					others.Add(dependent);
			}
			var uninspectedHops = 0;
			foreach (var (path, requests) in hopRequests)
			{
				if (!requests.Any(request => request.Seed == seedPath))
					continue;
				if (!inspectedHops.Contains(path))
				{
					uninspectedHops++;
					continue;
				}
				if (!hopUses.TryGetValue((seedPath, path), out var hop))
					continue;
				var facts = index.FileByPath[path];
				var file = new RelatedFile(
					path,
					ResolutionStatus.Resolved,
					[],
					[],
					CrossScope: false,
					(facts.CharacterCount + 3L) / 4L);
				var focused = new McpRelatedFocusedFile(file, ToNameUses(hop.Uses), hop.Within, hop.Via);
				(DependencyTestFiles.IsTestPath(path) ? tests : usingFiles).Add(focused);
			}
			result[seedPath] = new McpRelatedChangeFocus(
				seedPath,
				seedText.Changed,
				Ordered(usingFiles),
				Ordered(tests),
				reExporters,
				others,
				uninspectedHops);
		}
		return result;
	}

	private static Dictionary<string, FocusCandidate> FindFocusCandidates(ProjectDiffResult diff, DependencyRelatedResult related)
	{
		var changedFiles = diff.Files
			.Where(static file => file.Status != ProjectDiffFileStatus.Deleted &&
								  file.Omission == ProjectDiffOmission.None &&
								  file.Hunks.Count > 0)
			.GroupBy(static file => file.RelativePath, StringComparer.Ordinal)
			.ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
		var candidates = new Dictionary<string, FocusCandidate>(StringComparer.Ordinal);
		foreach (var seed in related.Seeds)
		{
			if (seed.NoFactsReason is not null || seed.Dependents.Count == 0 ||
				!changedFiles.TryGetValue(seed.Seed, out var changed) ||
				!related.Index.FileByPath.TryGetValue(seed.Seed, out var facts))
				continue;
			var (declarations, _) = DependencyChangeFocus.FindChangedDeclarations(
				facts,
				DependencyChangeFocus.ChangedLines(changed.Hunks));
			if (declarations.Count > 0)
				candidates[seed.Seed] = new FocusCandidate(seed, facts, declarations);
		}
		return candidates;
	}

	private async Task<IReadOnlySet<string>> ConsumeFocusSourcesAsync(
		ProjectContextPlan plan,
		IReadOnlyCollection<string> relativePaths,
		RelatedFocusBudget budget,
		Action<string, TransformedTextFile> consume,
		CancellationToken cancellationToken)
	{
		var processed = new HashSet<string>(StringComparer.Ordinal);
		if (relativePaths.Count == 0)
			return processed;
		var relativeByFullPath = new Dictionary<string, string>(PathComparer.Default);
		var admitted = new List<string>(relativePaths.Count);
		foreach (var relativePath in relativePaths.Order(StringComparer.Ordinal))
		{
			var fullPath = Path.GetFullPath(relativePath, plan.SourceRoot);
			long sourceBytes;
			try
			{
				sourceBytes = new FileInfo(fullPath).Length;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				continue;
			}
			if (!TryAdmitRelatedEvidenceSource(sourceBytes, ref budget.AdmittedBytes))
				continue;
			admitted.Add(fullPath);
			relativeByFullPath[fullPath] = relativePath;
		}
		if (admitted.Count == 0)
			return processed;
		await using var protectedSources = await Projects.ConsumeSearchTextAsync(
			plan with { IncludedFiles = admitted },
			(file, _) =>
			{
				if (relativeByFullPath.TryGetValue(file.Path, out var relativePath))
				{
					processed.Add(relativePath);
					consume(relativePath, file);
				}
				return ValueTask.CompletedTask;
			},
			cancellationToken).ConfigureAwait(false);
		return processed;
	}

	private static Func<int, int, bool> ProtectionCheck(TransformedTextFile file)
	{
		var ranges = new McpRelatedEvidenceRangeIndex(file.ReplacementRanges);
		return ranges.Intersects;
	}

	private static IReadOnlySet<string> WildcardNames(FileFacts facts, string content, IEnumerable<string> names) =>
		facts.LanguageId == LanguageId.Python
			? DependencyChangeFocus.PythonWildcardNames(content, names, facts.Aliases.ContainsKey("$dynamic-all")) ??
			  new HashSet<string>(StringComparer.Ordinal)
			: names.ToHashSet(StringComparer.Ordinal);

	private static IReadOnlyList<DependencyEdge> ResolvedEdges(DependencyIndexSnapshot index, string source, string target) =>
		index.EdgesBySource.TryGetValue(source, out var edges)
			? edges.Where(edge => edge.Status == ResolutionStatus.Resolved && edge.Target == target).ToArray()
			: [];

	private static IReadOnlySet<int> ImportLines(IEnumerable<DependencyEdge> edges, string source) =>
		edges
			.Where(static edge => edge.Layer == EvidenceLayer.ExplicitImport)
			.SelectMany(static edge => edge.Evidence)
			.Where(site => site.File == source)
			.Select(static site => site.Line)
			.ToHashSet();

	private static DependencyNameUses AddReferenceUses(
		DependencyNameUses uses,
		string content,
		IReadOnlyList<DependencyEdge> references,
		Func<int, int, bool> isProtected)
	{
		if (references.Count == 0)
			return uses;
		var lines = uses.Uses.ToDictionary(
			static pair => pair.Key,
			static pair => new SortedSet<int>(pair.Value),
			StringComparer.Ordinal);
		foreach (var reference in references)
		{
			foreach (var site in reference.Evidence)
			{
				if (!DependencyChangeFocus.LineContainsToken(content, site.Line, reference.Reference, isProtected))
					continue;
				if (!lines.TryGetValue(reference.Reference, out var set))
					lines.Add(reference.Reference, set = []);
				set.Add(site.Line);
			}
		}
		return uses with
		{
			Uses = lines.ToDictionary(
				static pair => pair.Key,
				static pair => (IReadOnlyList<int>)pair.Value.ToArray(),
				StringComparer.Ordinal)
		};
	}

	/// <summary>
	/// Adds, under ".member", the lines that access a changed member of a changed type the file
	/// uses, so a caller of the changed method stands out from a file that only names the type.
	/// </summary>
	private static DependencyNameUses AddMemberUses(
		DependencyNameUses uses,
		string content,
		LanguageId language,
		IReadOnlyList<DependencyChangedDeclaration> changed,
		Func<int, int, bool> isProtected)
	{
		var members = changed
			.Where(declaration => uses.Uses.ContainsKey(declaration.Name))
			.SelectMany(DependencyChangeFocus.MemberNames)
			.Distinct(StringComparer.Ordinal)
			.ToArray();
		if (members.Length == 0)
			return uses;
		var lines = new Dictionary<string, IReadOnlyList<int>>(uses.Uses, StringComparer.Ordinal);
		foreach (var member in members)
		{
			var accesses = DependencyChangeFocus.MemberAccessLines(content, language, member, isProtected);
			if (accesses.Count > 0)
				lines["." + member] = accesses;
		}
		return uses with { Uses = lines };
	}

	private static IReadOnlyList<string> WithinDeclarations(
		FileFacts facts,
		string content,
		DependencyNameUses uses,
		Func<int, int, bool> isProtected)
	{
		var topLevel = facts.NavigationDeclarations
			.Where(static declaration => declaration.Kind != NavigationSymbolKind.Module)
			.ToArray();
		var names = new List<string>();
		foreach (var line in uses.Uses.Values.SelectMany(static lines => lines).Distinct().Order())
		{
			var outermost = topLevel
				.Where(declaration => declaration.StartLine <= line && line <= declaration.EndLine)
				.OrderByDescending(static declaration => declaration.EndLine - declaration.StartLine)
				.FirstOrDefault();
			if (outermost is null)
				continue;
			var name = outermost.Owner is { Length: > 0 } owner && outermost.Name.StartsWith(owner, StringComparison.Ordinal)
				? outermost.Name[owner.Length..].TrimStart('.', ':', '#')
				: outermost.Name;
			if (names.Contains(name, StringComparer.Ordinal) ||
				!DependencyChangeFocus.LineContainsToken(content, outermost.StartLine, name, isProtected))
				continue;
			names.Add(name);
		}
		return names;
	}

	private static IReadOnlyList<McpRelatedNameUse> ToNameUses(DependencyNameUses uses) =>
		uses.Uses
			.OrderBy(static pair => pair.Key.StartsWith('.') ? 0 : 1)
			.ThenBy(static pair => pair.Value[0])
			.ThenBy(static pair => pair.Key, StringComparer.Ordinal)
			.Select(static pair => new McpRelatedNameUse(pair.Key, pair.Value))
			.ToArray();

	private static IReadOnlyList<McpRelatedFocusedFile> Ordered(List<McpRelatedFocusedFile> files) =>
		files
			.OrderByDescending(static file => file.Uses.Any(static use => use.Name.StartsWith('.')))
			.ThenByDescending(static file => file.Uses.Sum(static use => use.Lines.Count))
			.ThenBy(static file => file.File.Path, StringComparer.Ordinal)
			.ToArray();

	private static void AddRequest<T>(Dictionary<string, List<T>> requests, string path, T request)
	{
		if (!requests.TryGetValue(path, out var list))
			requests.Add(path, list = []);
		list.Add(request);
	}

	private static void WriteRelatedChangeFocus(
		TextWriter output,
		Action startLine,
		McpRelatedChangeFocus focus,
		CancellationToken cancellationToken,
		Action<string> recordPath)
	{
		startLine();
		output.Write("Changed declarations: ");
		for (var index = 0; index < Math.Min(focus.Changed.Count, MaximumListedChangedDeclarations); index++)
		{
			if (index > 0) output.Write(", ");
			var declaration = focus.Changed[index];
			output.Write(McpTextEscaping.EscapeSingleLine(declaration.Name));
			output.Write(declaration.DeclarationLine == 0 ? " (removed) at line " : " at line ");
			output.Write(declaration.FirstLine.ToString(CultureInfo.InvariantCulture));
			if (declaration.Members.Count > 0)
			{
				output.Write(" (");
				output.Write(McpTextEscaping.EscapeSingleLine(string.Join(", ", declaration.Members.Take(3))));
				if (declaration.Members.Count > 3)
					output.Write($" +{(declaration.Members.Count - 3).ToString(CultureInfo.InvariantCulture)} more");
				output.Write(')');
			}
		}
		if (focus.Changed.Count > MaximumListedChangedDeclarations)
			output.Write($" (+{(focus.Changed.Count - MaximumListedChangedDeclarations).ToString(CultureInfo.InvariantCulture)} more)");

		WriteFocusedGroup(output, startLine, "Dependents using changed declarations", focus.Using, 0, cancellationToken, recordPath);
		WriteFocusedGroup(output, startLine, "Tests using changed declarations", focus.Tests, focus.UninspectedThroughReExports, cancellationToken, recordPath);
		if (focus.ReExporters.Count > 0)
		{
			startLine();
			output.Write("Re-exported by: ");
			output.Write(string.Join(", ", focus.ReExporters.Select(McpTextEscaping.EscapeSingleLine)));
		}
		if (focus.Others.Count > 0)
			WriteRelatedSection(
				output,
				startLine,
				"Other dependents, using no changed declaration",
				focus.Others.Select(static file => file with { Reasons = file.Reasons.Take(1).ToArray() }).ToArray(),
				cancellationToken,
				file => recordPath(file.Path));
	}

	private static void WriteFocusedGroup(
		TextWriter output,
		Action startLine,
		string title,
		IReadOnlyList<McpRelatedFocusedFile> files,
		int uninspected,
		CancellationToken cancellationToken,
		Action<string> recordPath)
	{
		startLine();
		output.Write(title);
		output.Write(':');
		if (files.Count == 0)
			output.Write(" none reference them directly");
		if (uninspected > 0)
			output.Write($" ({uninspected.ToString(CultureInfo.InvariantCulture)} files reached through re-exports not inspected)");
		foreach (var file in files.Take(MaximumFocusedFilesPerGroup))
		{
			cancellationToken.ThrowIfCancellationRequested();
			startLine();
			output.Write(McpTextEscaping.EscapeSingleLine(file.File.Path));
			recordPath(file.File.Path);
			output.Write(" — ");
			if (file.Via is not null)
			{
				output.Write("via ");
				output.Write(McpTextEscaping.EscapeSingleLine(file.Via));
				output.Write(" · ");
			}
			for (var index = 0; index < file.Uses.Count; index++)
			{
				if (index > 0) output.Write(" · ");
				output.Write(McpTextEscaping.EscapeSingleLine(file.Uses[index].Name));
				output.Write(FormatLines(file.Uses[index].Lines));
			}
			if (file.Within.Count > 0)
			{
				output.Write(" · in ");
				output.Write(McpTextEscaping.EscapeSingleLine(string.Join(", ", file.Within.Take(3))));
				if (file.Within.Count > 3)
					output.Write($" (+{(file.Within.Count - 3).ToString(CultureInfo.InvariantCulture)} more)");
			}
			output.Write(" — ");
			output.Write(file.File.Status.ToString().ToLowerInvariant());
			output.Write(" — ");
			output.Write(file.File.EstimatedTokens.ToString(CultureInfo.InvariantCulture));
			output.Write(" tokens");
			if (file.File.CrossScope) output.Write(" — cross-scope");
		}
		if (files.Count > MaximumFocusedFilesPerGroup)
		{
			startLine();
			output.Write($"(+{(files.Count - MaximumFocusedFilesPerGroup).ToString(CultureInfo.InvariantCulture)} more)");
		}
	}

	private static string FormatLines(IReadOnlyList<int> lines)
	{
		const int listed = 5;
		var text = string.Join(", ", lines.Take(listed).Select(static line => line.ToString(CultureInfo.InvariantCulture)));
		var prefix = lines.Count == 1 ? " at line " : " at lines ";
		return lines.Count > listed
			? $"{prefix}{text} (+{(lines.Count - listed).ToString(CultureInfo.InvariantCulture)} more)"
			: prefix + text;
	}
}
