namespace DevProjex.Application.Diff;

/// <summary>
/// A line diff for two versions of one file: Myers' O(ND) greedy algorithm over line identities,
/// after the common prefix and suffix are removed. Pure and bounded: a version above
/// <see cref="MaximumLines"/> lines, or a change whose search would need more than
/// <see cref="MaximumEditDistance"/> added plus removed lines, is reported as too large instead
/// of being approximated, so every hunk returned belongs to a minimal edit. A change that shares
/// no line between the versions needs no search and is not limited by the edit distance.
/// </summary>
public static class LineDiff
{
	public const int MaximumLines = 50_000;
	public const int MaximumEditDistance = 2_000;
	public const int MaximumContextLines = 100;

	public static LineDiffResult Compare(
		string oldText,
		string newText,
		int contextLines,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(oldText);
		ArgumentNullException.ThrowIfNull(newText);
		ArgumentOutOfRangeException.ThrowIfNegative(contextLines);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(contextLines, MaximumContextLines);
		if (string.Equals(oldText, newText, StringComparison.Ordinal))
			return LineDiffResult.Unchanged;
		if (DiffTextLines.CountLines(oldText) > MaximumLines || DiffTextLines.CountLines(newText) > MaximumLines)
			return LineDiffResult.TooLarge;

		var oldLines = DiffTextLines.Split(oldText, cancellationToken);
		var newLines = DiffTextLines.Split(newText, cancellationToken);
		var (oldIds, newIds) = IdentifyLines(oldLines, newLines, cancellationToken);
		// The raw texts differ, so equal lines mean the difference is in bytes no line shows.
		if (oldIds.AsSpan().SequenceEqual(newIds))
			return LineDiffResult.LineEndingsOnly;

		var script = BuildEditScript(oldIds, newIds, cancellationToken);
		return script is null
			? LineDiffResult.TooLarge
			: BuildResult(script, oldLines, newLines, contextLines, cancellationToken);
	}

	/// <summary>
	/// Maps every distinct line to a small integer, so the search compares integers. The last
	/// line without a terminator gets its own identity: "b" and "b\n" are different lines, which
	/// is what lets the output carry the no-newline marker exactly where git does.
	/// </summary>
	private static (int[] Old, int[] New) IdentifyLines(
		DiffTextLines oldLines,
		DiffTextLines newLines,
		CancellationToken cancellationToken)
	{
		var identities = new Dictionary<string, int>(StringComparer.Ordinal);
		return (Identify(oldLines), Identify(newLines));

		int[] Identify(DiffTextLines lines)
		{
			var result = new int[lines.Count];
			for (var index = 0; index < result.Length; index++)
			{
				if ((index & 1023) == 0)
					cancellationToken.ThrowIfCancellationRequested();
				// No line contains '\n', so the suffix cannot collide with a terminated line.
				var key = lines.IsUnterminated(index) ? lines.Lines[index] + "\n" : lines.Lines[index];
				if (!identities.TryGetValue(key, out var identity))
				{
					identity = identities.Count;
					identities.Add(key, identity);
				}
				result[index] = identity;
			}
			return result;
		}
	}

	internal static IReadOnlyList<LineEdit>? BuildEditScript(
		int[] oldIds,
		int[] newIds,
		CancellationToken cancellationToken)
	{
		var prefix = 0;
		var limit = Math.Min(oldIds.Length, newIds.Length);
		while (prefix < limit && oldIds[prefix] == newIds[prefix])
			prefix++;
		var suffix = 0;
		while (suffix < limit - prefix &&
			   oldIds[oldIds.Length - 1 - suffix] == newIds[newIds.Length - 1 - suffix])
		{
			suffix++;
		}

		var oldMiddle = oldIds.AsSpan(prefix, oldIds.Length - prefix - suffix);
		var newMiddle = newIds.AsSpan(prefix, newIds.Length - prefix - suffix);
		var middle = HasCommonLine(oldMiddle, newMiddle)
			? FindShortestEdit(oldMiddle, newMiddle, MaximumEditDistance, cancellationToken)
			: Replacement(oldMiddle.Length, newMiddle.Length);
		if (middle is null)
			return null;

		var script = new List<LineEdit>(prefix + suffix + middle.Count);
		for (var index = 0; index < prefix; index++)
			script.Add(new LineEdit(LineEditKind.Equal, index, index));
		foreach (var edit in middle)
		{
			script.Add(edit with
			{
				OldIndex = edit.OldIndex < 0 ? -1 : edit.OldIndex + prefix,
				NewIndex = edit.NewIndex < 0 ? -1 : edit.NewIndex + prefix
			});
		}
		for (var offset = suffix; offset > 0; offset--)
		{
			script.Add(new LineEdit(
				LineEditKind.Equal,
				oldIds.Length - offset,
				newIds.Length - offset));
		}
		return script;
	}

	private static bool HasCommonLine(ReadOnlySpan<int> oldIds, ReadOnlySpan<int> newIds)
	{
		if (oldIds.IsEmpty || newIds.IsEmpty)
			return false;
		var present = new HashSet<int>(oldIds.Length);
		foreach (var identity in oldIds)
			present.Add(identity);
		foreach (var identity in newIds)
		{
			if (present.Contains(identity))
				return true;
		}
		return false;
	}

	// With no line in common the minimal edit is removing every old line and adding every new
	// one, which the search would only reach after exploring every diagonal.
	private static List<LineEdit> Replacement(int oldCount, int newCount)
	{
		var edits = new List<LineEdit>(oldCount + newCount);
		for (var index = 0; index < oldCount; index++)
			edits.Add(new LineEdit(LineEditKind.Delete, index, -1));
		for (var index = 0; index < newCount; index++)
			edits.Add(new LineEdit(LineEditKind.Insert, -1, index));
		return edits;
	}

	/// <summary>
	/// Myers' greedy forward search. The furthest-reaching x on every diagonal is kept for each
	/// edit distance so the path can be walked back; that history costs about D²/2 integers,
	/// which <paramref name="maximumDistance"/> bounds.
	/// </summary>
	private static List<LineEdit>? FindShortestEdit(
		ReadOnlySpan<int> oldIds,
		ReadOnlySpan<int> newIds,
		int maximumDistance,
		CancellationToken cancellationToken)
	{
		var oldCount = oldIds.Length;
		var newCount = newIds.Length;
		var bound = (int)Math.Min((long)oldCount + newCount, maximumDistance);
		var offset = bound + 1;
		var furthest = new int[2 * bound + 3];
		var history = new List<int[]>();
		for (var distance = 0; distance <= bound; distance++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			for (var diagonal = -distance; diagonal <= distance; diagonal += 2)
			{
				var x = diagonal == -distance ||
						diagonal != distance && furthest[offset + diagonal - 1] < furthest[offset + diagonal + 1]
					? furthest[offset + diagonal + 1]
					: furthest[offset + diagonal - 1] + 1;
				var y = x - diagonal;
				while (x < oldCount && y < newCount && oldIds[x] == newIds[y])
				{
					x++;
					y++;
				}
				furthest[offset + diagonal] = x;
				if (x >= oldCount && y >= newCount)
				{
					history.Add(Snapshot(furthest, offset, distance));
					return WalkBack(history, distance, oldCount, newCount);
				}
			}
			history.Add(Snapshot(furthest, offset, distance));
		}
		return null;
	}

	private static int[] Snapshot(int[] furthest, int offset, int distance)
	{
		var snapshot = new int[distance + 1];
		for (var diagonal = -distance; diagonal <= distance; diagonal += 2)
			snapshot[(diagonal + distance) / 2] = furthest[offset + diagonal];
		return snapshot;
	}

	private static List<LineEdit> WalkBack(
		IReadOnlyList<int[]> history,
		int distance,
		int oldCount,
		int newCount)
	{
		var reversed = new List<LineEdit>(oldCount + newCount);
		var x = oldCount;
		var y = newCount;
		for (var step = distance; step > 0; step--)
		{
			var previous = history[step - 1];
			var diagonal = x - y;
			var inserted = diagonal == -step ||
						   diagonal != step &&
						   At(previous, step - 1, diagonal - 1) < At(previous, step - 1, diagonal + 1);
			var previousDiagonal = inserted ? diagonal + 1 : diagonal - 1;
			var previousX = At(previous, step - 1, previousDiagonal);
			var previousY = previousX - previousDiagonal;
			var snakeStartX = inserted ? previousX : previousX + 1;
			var snakeStartY = inserted ? previousY + 1 : previousY;
			while (x > snakeStartX && y > snakeStartY)
			{
				x--;
				y--;
				reversed.Add(new LineEdit(LineEditKind.Equal, x, y));
			}
			reversed.Add(inserted
				? new LineEdit(LineEditKind.Insert, -1, previousY)
				: new LineEdit(LineEditKind.Delete, previousX, -1));
			x = previousX;
			y = previousY;
		}
		while (x > 0 && y > 0)
		{
			x--;
			y--;
			reversed.Add(new LineEdit(LineEditKind.Equal, x, y));
		}
		if (x != 0 || y != 0)
			throw new InvalidOperationException("The line diff path did not return to the start of both versions.");
		reversed.Reverse();
		return OrderRemovalsFirst(reversed);
	}

	private static int At(int[] snapshot, int distance, int diagonal) => snapshot[(diagonal + distance) / 2];

	// The search interleaves removals and additions inside one change; unified output lists the
	// removed lines of a change before the added ones.
	private static List<LineEdit> OrderRemovalsFirst(List<LineEdit> edits)
	{
		var ordered = new List<LineEdit>(edits.Count);
		var additions = new List<LineEdit>();
		foreach (var edit in edits)
		{
			switch (edit.Kind)
			{
				case LineEditKind.Delete:
					ordered.Add(edit);
					break;
				case LineEditKind.Insert:
					additions.Add(edit);
					break;
				default:
					ordered.AddRange(additions);
					additions.Clear();
					ordered.Add(edit);
					break;
			}
		}
		ordered.AddRange(additions);
		return ordered;
	}

	private static LineDiffResult BuildResult(
		IReadOnlyList<LineEdit> script,
		DiffTextLines oldLines,
		DiffTextLines newLines,
		int contextLines,
		CancellationToken cancellationToken)
	{
		var added = 0;
		var removed = 0;
		var ranges = new List<(int Start, int End)>();
		var first = -1;
		var last = -1;
		for (var index = 0; index < script.Count; index++)
		{
			var kind = script[index].Kind;
			if (kind == LineEditKind.Equal)
				continue;
			if (kind == LineEditKind.Insert)
				added++;
			else
				removed++;
			if (first < 0)
			{
				first = index;
			}
			// Hunks whose context would touch or overlap become one, as in git.
			else if (index - last - 1 > 2 * contextLines)
			{
				ranges.Add(ContextRange(first, last, contextLines, script.Count));
				first = index;
			}
			last = index;
		}
		if (first < 0)
			return LineDiffResult.LineEndingsOnly;
		ranges.Add(ContextRange(first, last, contextLines, script.Count));

		var hunks = new List<UnifiedDiffHunk>(ranges.Count);
		var consumedOld = 0;
		var consumedNew = 0;
		var position = 0;
		foreach (var (start, end) in ranges)
		{
			cancellationToken.ThrowIfCancellationRequested();
			for (; position < start; position++)
				Consume(script[position].Kind, ref consumedOld, ref consumedNew);
			hunks.Add(BuildHunk(script, start, end, consumedOld, consumedNew, oldLines, newLines));
			for (; position <= end; position++)
				Consume(script[position].Kind, ref consumedOld, ref consumedNew);
		}
		return new LineDiffResult(LineDiffOutcome.Changed, added, removed, hunks);
	}

	private static (int Start, int End) ContextRange(int firstChange, int lastChange, int contextLines, int count) =>
		(Math.Max(0, firstChange - contextLines), Math.Min(count - 1, lastChange + contextLines));

	private static void Consume(LineEditKind kind, ref int consumedOld, ref int consumedNew)
	{
		if (kind != LineEditKind.Insert)
			consumedOld++;
		if (kind != LineEditKind.Delete)
			consumedNew++;
	}

	private static UnifiedDiffHunk BuildHunk(
		IReadOnlyList<LineEdit> script,
		int start,
		int end,
		int oldBefore,
		int newBefore,
		DiffTextLines oldLines,
		DiffTextLines newLines)
	{
		var lines = new List<UnifiedDiffLine>(end - start + 1);
		var oldCount = 0;
		var newCount = 0;
		for (var index = start; index <= end; index++)
		{
			var edit = script[index];
			switch (edit.Kind)
			{
				case LineEditKind.Equal:
					oldCount++;
					newCount++;
					lines.Add(new UnifiedDiffLine(
						UnifiedDiffLineKind.Context,
						oldLines.Lines[edit.OldIndex],
						oldLines.IsUnterminated(edit.OldIndex)));
					break;
				case LineEditKind.Delete:
					oldCount++;
					lines.Add(new UnifiedDiffLine(
						UnifiedDiffLineKind.Removed,
						oldLines.Lines[edit.OldIndex],
						oldLines.IsUnterminated(edit.OldIndex)));
					break;
				case LineEditKind.Insert:
					newCount++;
					lines.Add(new UnifiedDiffLine(
						UnifiedDiffLineKind.Added,
						newLines.Lines[edit.NewIndex],
						newLines.IsUnterminated(edit.NewIndex)));
					break;
			}
		}
		return new UnifiedDiffHunk(
			oldCount == 0 ? oldBefore : oldBefore + 1,
			oldCount,
			newCount == 0 ? newBefore : newBefore + 1,
			newCount,
			lines);
	}
}

internal enum LineEditKind
{
	Equal,
	Delete,
	Insert
}

/// <summary>An index of -1 marks the side the edit does not touch.</summary>
internal readonly record struct LineEdit(LineEditKind Kind, int OldIndex, int NewIndex);
