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

internal readonly record struct PreviewMarkerLane(
	double X,
	double Width,
	double Opacity);

internal readonly record struct PreviewMarkerScrollMetrics(
	double ExtentHeight,
	double ViewportHeight,
	double ThumbHeight,
	double FirstLineTop,
	double LineHeight);

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

internal static class PreviewMarkerLaneGeometry
{
	private const double SearchOpacity = 0.55;

	public static PreviewMarkerLane Resolve(PreviewMarkerCategory category, double totalWidth)
	{
		var width = double.IsFinite(totalWidth) && totalWidth > 0
			? totalWidth
			: 0;
		var leftWidth = width / 2;
		return category switch
		{
			PreviewMarkerCategory.Redaction => new PreviewMarkerLane(0, leftWidth, 1),
			PreviewMarkerCategory.Search => new PreviewMarkerLane(leftWidth, width - leftWidth, SearchOpacity),
			_ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
		};
	}
}

internal static class PreviewMarkerGeometry
{
	private const double MergeDistance = 1.0;

	public static PreviewMarkerTick[] Build(
		IReadOnlyList<PreviewMarkerSource> markers,
		int totalLineCount,
		double height,
		PreviewMarkerScrollMetrics? scrollMetrics = null)
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
		var clusterStartIndex = 0;
		var clusterStartY = MapLineToY(
			ordered[0].LineNumber,
			totalLineCount,
			height,
			scrollMetrics);

		for (var index = 1; index < ordered.Length; index++)
		{
			var marker = ordered[index];
			var y = MapLineToY(marker.LineNumber, totalLineCount, height, scrollMetrics);
			if (marker.Category == category && y - clusterStartY <= MergeDistance)
				continue;

			AddTick(ticks, ordered, clusterStartIndex, index, totalLineCount, height, scrollMetrics);
			category = marker.Category;
			clusterStartIndex = index;
			clusterStartY = y;
		}

		AddTick(
			ticks,
			ordered,
			clusterStartIndex,
			ordered.Length,
			totalLineCount,
			height,
			scrollMetrics);
		ticks.Sort(static (left, right) =>
		{
			var yComparison = left.Y.CompareTo(right.Y);
			return yComparison != 0
				? yComparison
				: left.Target.Category.CompareTo(right.Target.Category);
		});
		return ticks.ToArray();
	}

	public static PreviewMarkerTarget? FindTargetAt(
		IReadOnlyList<PreviewMarkerTick> ticks,
		double y,
		double maximumDistance)
	{
		ArgumentNullException.ThrowIfNull(ticks);
		if (ticks.Count == 0 ||
		    !double.IsFinite(y) ||
		    !double.IsFinite(maximumDistance) ||
		    maximumDistance < 0)
		{
			return null;
		}

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

		return nearestDistance <= maximumDistance
			? nearest.Target
			: null;
	}

	private static double MapLineToY(
		int lineNumber,
		int totalLineCount,
		double height,
		PreviewMarkerScrollMetrics? scrollMetrics)
	{
		var maximumY = Math.Max(0, height - 1);
		if (scrollMetrics is
		    {
			    ExtentHeight: > 0,
			    ViewportHeight: > 0,
			    ThumbHeight: >= 0,
			    LineHeight: > 0
		    } metrics &&
		    metrics.ExtentHeight > metrics.ViewportHeight)
		{
			var maximumOffset = metrics.ExtentHeight - metrics.ViewportHeight;
			var lineCenter = metrics.FirstLineTop +
			                 ((lineNumber - 0.5) * metrics.LineHeight);
			var targetOffset = Math.Clamp(
				lineCenter - (metrics.ViewportHeight / 2),
				0,
				maximumOffset);
			var thumbHeight = Math.Clamp(metrics.ThumbHeight, 0, height);
			var thumbTravel = Math.Max(0, height - thumbHeight);
			var mapped = (thumbHeight / 2) +
			             ((targetOffset / maximumOffset) * thumbTravel);
			return Math.Clamp(mapped, 0, maximumY);
		}

		var fallbackMapped = ((lineNumber - 0.5) / totalLineCount) * height;
		return Math.Clamp(fallbackMapped, 0, maximumY);
	}

	private static void AddTick(
		List<PreviewMarkerTick> ticks,
		IReadOnlyList<PreviewMarkerSource> markers,
		int startIndex,
		int endIndex,
		int totalLineCount,
		double height,
		PreviewMarkerScrollMetrics? scrollMetrics)
	{
		var ySum = 0d;
		for (var index = startIndex; index < endIndex; index++)
			ySum += MapLineToY(
				markers[index].LineNumber,
				totalLineCount,
				height,
				scrollMetrics);

		var averageY = ySum / (endIndex - startIndex);
		var representative = markers[startIndex];
		var representativeY = MapLineToY(
			representative.LineNumber,
			totalLineCount,
			height,
			scrollMetrics);
		var nearestDistance = Math.Abs(representativeY - averageY);
		for (var index = startIndex + 1; index < endIndex; index++)
		{
			var candidate = markers[index];
			var candidateY = MapLineToY(
				candidate.LineNumber,
				totalLineCount,
				height,
				scrollMetrics);
			var distance = Math.Abs(candidateY - averageY);
			if (distance >= nearestDistance)
				continue;

			representative = candidate;
			representativeY = candidateY;
			nearestDistance = distance;
		}

		ticks.Add(new PreviewMarkerTick(
			representativeY,
			new PreviewMarkerTarget(representative.LineNumber, representative.Category)));
	}
}
