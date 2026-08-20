namespace DevProjex.Avalonia.Controls;

internal enum PreviewMarkerCategory : byte
{
	Redaction = 0,
	Search = 1
}

internal readonly record struct PreviewMarkerSource(
	int LineNumber,
	PreviewMarkerCategory Category);

internal readonly record struct PreviewMarkerTarget(
	int LineNumber,
	PreviewMarkerCategory Category);

internal readonly record struct PreviewMarkerTick(
	double Y,
	PreviewMarkerTarget Target);

internal sealed record PreviewMarkerSnapshot(
	int TotalLineCount,
	PreviewMarkerSource[] Markers)
{
	public static PreviewMarkerSnapshot Empty { get; } = new(0, []);
}

internal sealed class PreviewMarkersChangedEventArgs(PreviewMarkerSnapshot snapshot) : EventArgs
{
	public PreviewMarkerSnapshot Snapshot { get; } = snapshot;
}

internal static class PreviewMarkerGeometry
{
	private const double MergeDistance = 1.0;

	public static PreviewMarkerTick[] Build(
		IReadOnlyList<PreviewMarkerSource> markers,
		int totalLineCount,
		double height)
	{
		ArgumentNullException.ThrowIfNull(markers);
		if (markers.Count == 0 ||
		    totalLineCount <= 0 ||
		    !double.IsFinite(height) ||
		    height <= 0)
		{
			return [];
		}

		var ordered = markers
			.Where(marker => marker.LineNumber is > 0 && marker.LineNumber <= totalLineCount)
			.OrderBy(static marker => marker.Category)
			.ThenBy(static marker => marker.LineNumber)
			.ToArray();
		if (ordered.Length == 0)
			return [];

		var initialCapacity = Math.Min(
			ordered.Length,
			(int)Math.Clamp(Math.Ceiling(height) * 2, 1, 8192));
		var ticks = new List<PreviewMarkerTick>(initialCapacity);
		var category = ordered[0].Category;
		var firstLine = ordered[0].LineNumber;
		var lastLine = firstLine;
		var lastY = MapLineToY(firstLine, totalLineCount, height);
		var ySum = lastY;
		var mergedCount = 1;

		for (var index = 1; index < ordered.Length; index++)
		{
			var marker = ordered[index];
			var y = MapLineToY(marker.LineNumber, totalLineCount, height);
			if (marker.Category == category && y - lastY <= MergeDistance)
			{
				lastLine = marker.LineNumber;
				lastY = y;
				ySum += y;
				mergedCount++;
				continue;
			}

			AddTick(ticks, category, firstLine, lastLine, ySum, mergedCount);
			category = marker.Category;
			firstLine = marker.LineNumber;
			lastLine = firstLine;
			lastY = y;
			ySum = y;
			mergedCount = 1;
		}

		AddTick(ticks, category, firstLine, lastLine, ySum, mergedCount);
		ticks.Sort(static (left, right) =>
		{
			var yComparison = left.Y.CompareTo(right.Y);
			return yComparison != 0
				? yComparison
				: left.Target.Category.CompareTo(right.Target.Category);
		});
		return ticks.ToArray();
	}

	public static PreviewMarkerTarget? FindNearestTarget(
		IReadOnlyList<PreviewMarkerTick> ticks,
		double y)
	{
		ArgumentNullException.ThrowIfNull(ticks);
		if (ticks.Count == 0 || !double.IsFinite(y))
			return null;

		var nearest = ticks[0];
		var nearestDistance = Math.Abs(y - nearest.Y);
		for (var index = 1; index < ticks.Count; index++)
		{
			var candidate = ticks[index];
			var distance = Math.Abs(y - candidate.Y);
			if (distance >= nearestDistance)
				continue;

			nearest = candidate;
			nearestDistance = distance;
		}

		return nearest.Target;
	}

	private static double MapLineToY(
		int lineNumber,
		int totalLineCount,
		double height)
	{
		var mapped = lineNumber / (double)totalLineCount * height;
		return Math.Clamp(mapped, 0, Math.Max(0, height - 1));
	}

	private static void AddTick(
		List<PreviewMarkerTick> ticks,
		PreviewMarkerCategory category,
		int firstLine,
		int lastLine,
		double ySum,
		int mergedCount)
	{
		var targetLine = firstLine + ((lastLine - firstLine) / 2);
		ticks.Add(new PreviewMarkerTick(
			ySum / mergedCount,
			new PreviewMarkerTarget(targetLine, category)));
	}
}
