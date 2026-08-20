namespace DevProjex.Avalonia.Controls;

internal sealed class PreviewMarkerInvokedEventArgs(PreviewMarkerTarget target) : EventArgs
{
	public PreviewMarkerTarget Target { get; } = target;
}

public sealed class PreviewMarkerBar : Control
{
	private const double TickHorizontalInset = 2;
	private const double TickHeight = 2;
	private PreviewMarkerSnapshot _snapshot = PreviewMarkerSnapshot.Empty;
	private PreviewMarkerTick[] _ticks = [];
	private double _geometryHeight = double.NaN;

	public static readonly StyledProperty<bool> IsMarkerDisplayEnabledProperty =
		AvaloniaProperty.Register<PreviewMarkerBar, bool>(nameof(IsMarkerDisplayEnabled));

	public static readonly StyledProperty<IBrush?> RedactionBrushProperty =
		AvaloniaProperty.Register<PreviewMarkerBar, IBrush?>(nameof(RedactionBrush));

	public static readonly StyledProperty<IBrush?> SearchBrushProperty =
		AvaloniaProperty.Register<PreviewMarkerBar, IBrush?>(nameof(SearchBrush));

	internal event EventHandler<PreviewMarkerInvokedEventArgs>? MarkerInvoked;

	static PreviewMarkerBar()
	{
		AffectsRender<PreviewMarkerBar>(RedactionBrushProperty, SearchBrushProperty);
		IsMarkerDisplayEnabledProperty.Changed.AddClassHandler<PreviewMarkerBar>(
			static (control, _) => control.UpdateVisibility());
	}

	public bool IsMarkerDisplayEnabled
	{
		get => GetValue(IsMarkerDisplayEnabledProperty);
		set => SetValue(IsMarkerDisplayEnabledProperty, value);
	}

	public IBrush? RedactionBrush
	{
		get => GetValue(RedactionBrushProperty);
		set => SetValue(RedactionBrushProperty, value);
	}

	public IBrush? SearchBrush
	{
		get => GetValue(SearchBrushProperty);
		set => SetValue(SearchBrushProperty, value);
	}

	internal void SetMarkers(PreviewMarkerSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		_snapshot = snapshot;
		RebuildGeometry(Bounds.Height);
		UpdateVisibility();
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		var arranged = base.ArrangeOverride(finalSize);
		if (Math.Abs(_geometryHeight - finalSize.Height) > 0.1)
			RebuildGeometry(finalSize.Height);

		return arranged;
	}

	public override void Render(DrawingContext context)
	{
		base.Render(context);
		context.FillRectangle(Brushes.Transparent, Bounds);
		if (_ticks.Length == 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
			return;

		var width = Math.Max(1, Bounds.Width - (TickHorizontalInset * 2));
		var maximumTop = Math.Max(0, Bounds.Height - TickHeight);
		foreach (var tick in _ticks)
		{
			var brush = tick.Target.Category == PreviewMarkerCategory.Search
				? SearchBrush
				: RedactionBrush;
			if (brush is null)
				continue;

			var top = Math.Clamp(tick.Y - (TickHeight / 2), 0, maximumTop);
			context.DrawRectangle(
				brush,
				null,
				new RoundedRect(
					new Rect(TickHorizontalInset, top, width, TickHeight),
					TickHeight / 2));
		}
	}

	protected override void OnPointerPressed(PointerPressedEventArgs e)
	{
		base.OnPointerPressed(e);
		if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
			return;

		var target = PreviewMarkerGeometry.FindNearestTarget(_ticks, e.GetPosition(this).Y);
		if (target is null)
			return;

		MarkerInvoked?.Invoke(this, new PreviewMarkerInvokedEventArgs(target.Value));
		e.Handled = true;
	}

	private void RebuildGeometry(double height)
	{
		_geometryHeight = height;
		_ticks = PreviewMarkerGeometry.Build(
			_snapshot.Markers,
			_snapshot.TotalLineCount,
			height);
		InvalidateVisual();
	}

	private void UpdateVisibility()
	{
		IsVisible = IsMarkerDisplayEnabled && _snapshot.Markers.Length > 0;
	}
}
