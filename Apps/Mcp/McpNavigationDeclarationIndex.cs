using DevProjex.Application.Dependencies;

namespace DevProjex.Mcp;

/// <summary>
/// Maps monotonically increasing source lines to the narrowest containing declaration. Search
/// produces lines in source order, so one sorted declaration pass replaces a scan per hit.
/// </summary>
internal sealed class McpNavigationDeclarationIndex
{
	private readonly IReadOnlyList<NavigationDeclaration> sourceDeclarations;
	private readonly Entry[]? sortedDeclarations;
	private readonly PriorityQueue<Entry, DeclarationPriority> active = new(DeclarationPriorityComparer.Instance);
	private int nextDeclaration;
	private int lastLine = int.MinValue;

	public McpNavigationDeclarationIndex(
		IReadOnlyList<NavigationDeclaration> declarations,
		Action? declarationVisited = null)
	{
		ArgumentNullException.ThrowIfNull(declarations);
		sourceDeclarations = declarations;
		var sorted = true;
		for (var index = 0; index < declarations.Count; index++)
		{
			declarationVisited?.Invoke();
			if (index > 0 && declarations[index - 1].StartLine > declarations[index].StartLine)
				sorted = false;
		}
		if (sorted)
			return;

		sortedDeclarations = new Entry[declarations.Count];
		for (var index = 0; index < declarations.Count; index++)
			sortedDeclarations[index] = new Entry(declarations[index], index);
		Array.Sort(sortedDeclarations, static (left, right) =>
		{
			var start = left.Declaration.StartLine.CompareTo(right.Declaration.StartLine);
			return start != 0 ? start : left.OriginalIndex.CompareTo(right.OriginalIndex);
		});
	}

	public NavigationDeclaration? Find(int line)
	{
		if (line < lastLine)
			Reset();
		lastLine = line;
		while (nextDeclaration < sourceDeclarations.Count &&
			GetDeclaration(nextDeclaration).Declaration.StartLine <= line)
		{
			var entry = GetDeclaration(nextDeclaration++);
			active.Enqueue(entry, DeclarationPriority.Create(entry));
		}
		while (active.TryPeek(out var expired, out _) && expired.Declaration.EndLine < line)
			active.Dequeue();
		return active.TryPeek(out var current, out _) ? current.Declaration : null;
	}

	private void Reset()
	{
		active.Clear();
		nextDeclaration = 0;
	}

	private Entry GetDeclaration(int index) => sortedDeclarations is null
		? new Entry(sourceDeclarations[index], index)
		: sortedDeclarations[index];

	private readonly record struct Entry(NavigationDeclaration Declaration, int OriginalIndex);

	private readonly record struct DeclarationPriority(long LineSpan, long CharacterSpan, int OriginalIndex)
	{
		public static DeclarationPriority Create(Entry entry) => new(
			(long)entry.Declaration.EndLine - entry.Declaration.StartLine,
			(long)entry.Declaration.EndIndex - entry.Declaration.StartIndex,
			entry.OriginalIndex);
	}

	private sealed class DeclarationPriorityComparer : IComparer<DeclarationPriority>
	{
		public static readonly DeclarationPriorityComparer Instance = new();

		public int Compare(DeclarationPriority left, DeclarationPriority right)
		{
			var lineSpan = left.LineSpan.CompareTo(right.LineSpan);
			if (lineSpan != 0)
				return lineSpan;
			var characterSpan = left.CharacterSpan.CompareTo(right.CharacterSpan);
			if (characterSpan != 0)
				return characterSpan;
			return left.OriginalIndex.CompareTo(right.OriginalIndex);
		}
	}
}
