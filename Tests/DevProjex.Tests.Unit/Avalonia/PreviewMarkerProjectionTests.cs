using Avalonia.Controls;
using Avalonia.Media;
using DevProjex.Application.Preview;
using DevProjex.Application.Secrets;
using DevProjex.Avalonia.Controls;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class PreviewMarkerProjectionTests
{
	[AvaloniaFact]
	public void MarkerSnapshot_ContainsOnlyVisibleRedactionLinesAndUniqueSearchLines()
	{
		using var document = new InMemoryPreviewTextDocument(
			"one\ntwo\nthree\nfour\nfive",
			redactions:
			[
				Redaction("redacted-a", line: 2, SecretPreviewSpanState.Redacted),
				Redaction("redacted-b", line: 2, SecretPreviewSpanState.Redacted),
				Redaction("kept", line: 3, SecretPreviewSpanState.KeptAsIs),
				Redaction(
					"generated-path",
					line: 3,
					SecretPreviewSpanState.Redacted,
					SecretFindingSource.GeneratedPath),
				Redaction("redacted-c", line: 4, SecretPreviewSpanState.Redacted),
				Redaction(
					"generated-path-kept",
					line: 5,
					SecretPreviewSpanState.KeptAsIs,
					SecretFindingSource.GeneratedPath)
			]);
		var control = new VirtualizedPreviewTextControl { Document = document };

		control.SetSearchMatches(
			[
				new PreviewSearchMatch(1, 0, 3),
				new PreviewSearchMatch(1, 1, 2),
				new PreviewSearchMatch(4, 0, 4)
			],
			activateNearestToViewport: false,
			scrollIntoView: false);

		Assert.Equal(5, control.MarkerSnapshot.TotalLineCount);
		Assert.Equal(
			[
				new PreviewMarkerSource(2, PreviewMarkerCategory.Redaction),
				new PreviewMarkerSource(4, PreviewMarkerCategory.Redaction),
				new PreviewMarkerSource(1, PreviewMarkerCategory.Search),
				new PreviewMarkerSource(4, PreviewMarkerCategory.Search)
			],
			control.MarkerSnapshot.Markers);
	}

	[AvaloniaFact]
	public void DocumentReplacement_PublishesOnlyTheFinalSnapshotWithoutStaleMarkers()
	{
		using var first = new InMemoryPreviewTextDocument(
			"old\nvalue",
			redactions: [Redaction("old", line: 2, SecretPreviewSpanState.Redacted)]);
		using var replacement = new InMemoryPreviewTextDocument(
			"new\nplain\nvalue",
			redactions: [Redaction("new", line: 1, SecretPreviewSpanState.Redacted)]);
		var control = new VirtualizedPreviewTextControl { Document = first };
		control.SetSearchMatches(
			[new PreviewSearchMatch(2, 0, 5)],
			activateNearestToViewport: false,
			scrollIntoView: false);
		var publications = new List<PreviewMarkerSnapshot>();
		control.PreviewMarkersChanged += (_, eventArgs) => publications.Add(eventArgs.Snapshot);

		control.Document = replacement;

		var snapshot = Assert.Single(publications);
		Assert.Equal(3, snapshot.TotalLineCount);
		Assert.Equal(
			[new PreviewMarkerSource(1, PreviewMarkerCategory.Redaction)],
			snapshot.Markers);
		Assert.Equal(snapshot, control.MarkerSnapshot);
		Assert.Equal(0, control.SearchMatchCount);
	}

	[AvaloniaFact]
	public void ClearSearchMatches_PreservesRedactionsAndPublishesOneUpdatedSnapshot()
	{
		using var document = new InMemoryPreviewTextDocument(
			"one\ntwo\nthree",
			redactions: [Redaction("secret", line: 2, SecretPreviewSpanState.Redacted)]);
		var control = new VirtualizedPreviewTextControl { Document = document };
		control.SetSearchMatches(
			[new PreviewSearchMatch(3, 0, 5)],
			activateNearestToViewport: false,
			scrollIntoView: false);
		var publicationCount = 0;
		control.PreviewMarkersChanged += (_, _) => publicationCount++;

		control.ClearSearchMatches();

		Assert.Equal(1, publicationCount);
		Assert.Equal(
			[new PreviewMarkerSource(2, PreviewMarkerCategory.Redaction)],
			control.MarkerSnapshot.Markers);
		control.ClearSearchMatches();
		Assert.Equal(1, publicationCount);
	}

	[AvaloniaFact]
	public void NavigateToSearchMarker_SelectsTheNearestMatchWithStableBackwardTieBreak()
	{
		using var document = new InMemoryPreviewTextDocument(
			string.Join('\n', Enumerable.Range(1, 9).Select(static line => $"line-{line}")));
		var control = new VirtualizedPreviewTextControl { Document = document };
		control.SetSearchMatches(
			[
				new PreviewSearchMatch(3, 0, 6),
				new PreviewSearchMatch(7, 0, 6)
			],
			activateNearestToViewport: false,
			scrollIntoView: false);

		control.NavigateToMarker(new PreviewMarkerTarget(5, PreviewMarkerCategory.Search));

		Assert.Equal(0, control.ActiveSearchMatchIndex);
		Assert.Equal("line-3", control.GetSelectedText());
	}

	[AvaloniaFact]
	public void MarkerBar_VisibilityRequiresBothDisplayAvailabilityAndMarkers()
	{
		var markerBar = new PreviewMarkerBar
		{
			Width = 15,
			Height = 120,
			RedactionBrush = Brushes.Orange,
			SearchBrush = Brushes.Blue
		};
		var window = new Window
		{
			Width = 80,
			Height = 160,
			Content = markerBar
		};

		try
		{
			window.Show();
			AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
			markerBar.SetMarkers(new PreviewMarkerSnapshot(
				100,
				[new PreviewMarkerSource(50, PreviewMarkerCategory.Redaction)]));
			Assert.False(markerBar.IsVisible);

			markerBar.IsMarkerDisplayEnabled = true;
			markerBar.Measure(new Size(15, 120));
			markerBar.Arrange(new Rect(0, 0, 15, 120));
			Assert.True(markerBar.IsVisible);
			Assert.Single(markerBar.MarkerTicks);

			markerBar.SetMarkers(PreviewMarkerSnapshot.Empty);
			Assert.False(markerBar.IsVisible);
			Assert.Empty(markerBar.MarkerTicks);
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaFact]
	public void MarkerBar_RebuildsGeometryWhenScrollMetricsChangeWithoutResize()
	{
		var markerBar = new PreviewMarkerBar
		{
			Width = 15,
			Height = 100,
			IsMarkerDisplayEnabled = true
		};
		markerBar.SetMarkers(new PreviewMarkerSnapshot(
			100,
			[new PreviewMarkerSource(80, PreviewMarkerCategory.Redaction)]));
		var window = new Window
		{
			Width = 80,
			Height = 140,
			Content = markerBar
		};

		try
		{
			window.Show();
			AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
			var fallbackY = Assert.Single(markerBar.MarkerTicks).Y;

			markerBar.SetScrollMetrics(new PreviewMarkerScrollMetrics(
				ExtentHeight: 1_500,
				ViewportHeight: 300,
				ThumbHeight: 20,
				FirstLineTop: 100,
				LineHeight: 10));

			var alignedY = Assert.Single(markerBar.MarkerTicks).Y;
			Assert.NotEqual(fallbackY, alignedY);
			var expectedY = 10 + ((745 / 1_200d) * 80);
			Assert.Equal(expectedY, alignedY, precision: 6);
		}
		finally
		{
			window.Close();
		}
	}

	private static PreviewRedactionSpan Redaction(
		string occurrenceId,
		int line,
		SecretPreviewSpanState state,
		SecretFindingSource source = SecretFindingSource.Detector)
		=> new(
			occurrenceId,
			"test-rule",
			line,
			0,
			1,
			state,
			Source: source);
}
