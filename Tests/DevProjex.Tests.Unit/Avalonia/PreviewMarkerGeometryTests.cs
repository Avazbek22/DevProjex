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
		Assert.Equal(1, ticks[0].Y, precision: 6);
		Assert.Equal(99, ticks[1].Y, precision: 6);
		Assert.Equal(1, ticks[0].Target.LineNumber);
		Assert.Equal(100, ticks[1].Target.LineNumber);
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
	public void FindNearestTarget_ReturnsTheMarkerClosestToTheClick()
	{
		PreviewMarkerSource[] markers =
		[
			new(10, PreviewMarkerCategory.Redaction),
			new(90, PreviewMarkerCategory.Search)
		];
		var ticks = PreviewMarkerGeometry.Build(markers, totalLineCount: 100, height: 100);

		var target = PreviewMarkerGeometry.FindNearestTarget(ticks, y: 78);

		Assert.Equal(new PreviewMarkerTarget(90, PreviewMarkerCategory.Search), target);
	}
}
