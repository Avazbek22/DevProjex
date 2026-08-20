using DevProjex.Avalonia.Controls;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class PreviewMarkerGeometryTests
{
	[Fact]
	public void Build_MapsFirstAndLastLinesToStableStripeBoundaries()
	{
		PreviewMarkerSource[] markers =
		[
			new(1, PreviewMarkerCategory.Redaction),
			new(100, PreviewMarkerCategory.Search)
		];

		var ticks = PreviewMarkerGeometry.Build(markers, totalLineCount: 100, height: 100);

		Assert.Equal(2, ticks.Length);
		Assert.Equal(0.5, ticks[0].Y, precision: 6);
		Assert.Equal(99, ticks[1].Y, precision: 6);
		Assert.Equal(1, ticks[0].Target.LineNumber);
		Assert.Equal(100, ticks[1].Target.LineNumber);
	}

	[Fact]
	public void Build_MapsSingleLineDocumentToStripeCenter()
	{
		PreviewMarkerSource[] markers = [new(1, PreviewMarkerCategory.Redaction)];

		var tick = Assert.Single(PreviewMarkerGeometry.Build(markers, totalLineCount: 1, height: 100));

		Assert.Equal(50, tick.Y, precision: 6);
	}

	[Fact]
	public void Build_DoesNotTransitivelyMergeMarkersAcrossTheStripe()
	{
		PreviewMarkerSource[] markers =
		[
			new(10, PreviewMarkerCategory.Redaction),
			new(11, PreviewMarkerCategory.Redaction),
			new(12, PreviewMarkerCategory.Redaction)
		];

		var ticks = PreviewMarkerGeometry.Build(markers, totalLineCount: 100, height: 100);

		Assert.Equal(2, ticks.Length);
		Assert.Equal([10, 12], ticks.Select(static tick => tick.Target.LineNumber));
	}

	[Fact]
	public void Build_MergedTickTargetsAnExistingMarkerLine()
	{
		PreviewMarkerSource[] markers =
		[
			new(10, PreviewMarkerCategory.Redaction),
			new(12, PreviewMarkerCategory.Redaction)
		];

		var tick = Assert.Single(PreviewMarkerGeometry.Build(markers, totalLineCount: 100, height: 40));

		Assert.Contains(tick.Target.LineNumber, markers.Select(static marker => marker.LineNumber));
	}

	[Fact]
	public void Build_WithScrollMetricsAlignsTickToTheCenteredThumbPosition()
	{
		PreviewMarkerSource[] markers = [new(80, PreviewMarkerCategory.Redaction)];
		var metrics = new PreviewMarkerScrollMetrics(
			ExtentHeight: 1_020,
			ViewportHeight: 220,
			ThumbHeight: 20,
			FirstLineTop: 10,
			LineHeight: 10);

		var tick = Assert.Single(PreviewMarkerGeometry.Build(
			markers,
			totalLineCount: 100,
			height: 100,
			metrics));

		var expectedOffset = 695;
		var expectedY = 10 + ((expectedOffset / 800d) * 80);
		Assert.Equal(expectedY, tick.Y, precision: 6);
	}

	[Fact]
	public void Build_MergesPixelAdjacentLinesButKeepsDistantLinesSeparate()
	{
		PreviewMarkerSource[] markers =
		[
			new(10, PreviewMarkerCategory.Redaction),
			new(11, PreviewMarkerCategory.Redaction),
			new(60, PreviewMarkerCategory.Redaction)
		];

		var ticks = PreviewMarkerGeometry.Build(markers, totalLineCount: 100, height: 10);

		Assert.Equal(2, ticks.Length);
		Assert.Equal(10, ticks[0].Target.LineNumber);
		Assert.Equal(60, ticks[1].Target.LineNumber);
	}

	[Fact]
	public void Build_DoesNotMergeDifferentCategoriesAtTheSamePosition()
	{
		PreviewMarkerSource[] markers =
		[
			new(50, PreviewMarkerCategory.Redaction),
			new(50, PreviewMarkerCategory.Search)
		];

		var ticks = PreviewMarkerGeometry.Build(markers, totalLineCount: 100, height: 100);

		Assert.Equal(2, ticks.Length);
		Assert.Contains(ticks, tick => tick.Target.Category == PreviewMarkerCategory.Redaction);
		Assert.Contains(ticks, tick => tick.Target.Category == PreviewMarkerCategory.Search);
	}

	[Fact]
	public void Build_ReturnsEmptyGeometryForEmptyInput()
	{
		var ticks = PreviewMarkerGeometry.Build([], totalLineCount: 100, height: 100);

		Assert.Empty(ticks);
	}

	[Fact]
	public void FindTargetAt_ReturnsTheMarkerClosestToTheClickWithinHitRadius()
	{
		PreviewMarkerSource[] markers =
		[
			new(10, PreviewMarkerCategory.Redaction),
			new(90, PreviewMarkerCategory.Search)
		];
		var ticks = PreviewMarkerGeometry.Build(markers, totalLineCount: 100, height: 100);

		var target = PreviewMarkerGeometry.FindTargetAt(ticks, y: 88, maximumDistance: 4);

		Assert.Equal(new PreviewMarkerTarget(90, PreviewMarkerCategory.Search), target);
	}

	[Fact]
	public void FindTargetAt_ReturnsNullOutsideMarkerHitRadius()
	{
		PreviewMarkerSource[] markers = [new(50, PreviewMarkerCategory.Redaction)];
		var ticks = PreviewMarkerGeometry.Build(markers, totalLineCount: 100, height: 100);

		var target = PreviewMarkerGeometry.FindTargetAt(ticks, y: 70, maximumDistance: 4);

		Assert.Null(target);
	}
}
