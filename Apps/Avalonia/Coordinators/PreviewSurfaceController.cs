using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia.Coordinators;

internal sealed record PreviewSurfaceControls(
    ScrollViewer TextScrollViewer,
    VirtualizedPreviewTextControl TextControl,
    VirtualizedLineNumbersControl LineNumbersControl,
    PreviewMarkerBar MarkerBar,
    Border StickyHeaderCap,
    Border StickyHeaderContainer,
    TextBlock StickyHeaderText);

internal sealed class PreviewSurfaceController : IDisposable
{
    private const int PreviewWarmupTreeNodeLimit = 192;
    private const int PreviewWarmupCandidateFileLimit = 24;
    private const int PreviewWarmupCandidateNodeVisitLimit = 512;
    private const int PreviewWarmupContentFileLimit = 6;
    private const int PreviewWarmupMaxFileBytes = 64 * 1024;
    private const int PreviewWarmupMaxCharacters = 96 * 1024;
    private static readonly IReadOnlySet<string> EmptySelectedPaths =
        new HashSet<string>(PathComparer.Default);
    private static readonly TimeSpan SelectionMetricsDebounceInterval =
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(80));

    private readonly Window _window;
    private readonly MainWindowViewModel _viewModel;
    private readonly PreviewSurfaceControls _controls;
    private readonly LocalizationService _localization;
    private readonly IToastService _toastService;
    private readonly PreviewDocumentBuilder _previewDocumentBuilder;
	private readonly SecretRedactionOutputPreparer _secretRedactionPreparer;
	private readonly SecretRedactionSession _secretRedactionSession;
    private readonly SelectedContentExportService _contentExport;
    private readonly ProjectTextOutputPipeline _textOutputPipeline;
    private readonly TreeExportService _treeExport;
    private readonly MetricsPipeline _metrics;
    private readonly PreviewWorkspacePipeline _previewPipeline;
    private readonly Func<bool> _ensureClipboardOutputReady;
    private readonly Func<string, Task> _setClipboardTextAsync;
    private readonly Func<string, Task> _showErrorAsync;
	private readonly Func<ContentTransformationContext?> _transformationContextProvider;
	private readonly Func<string?> _projectRootProvider;
	private readonly Action _requestRedactionRefresh;
	private readonly Func<ManualRedactionClass, bool> _ensureManualRedactionClassEnabled;
	private readonly Func<PersistentSecretMarkDelta, Task<PersistentSecretMarkWriteResult>> _applyPersistentMarkDelta;
	private readonly Func<CancellationToken, Task> _persistProjectProfile;
	private readonly object _manualMarkOperationsSync = new();
	private readonly HashSet<Task> _manualMarkOperations = [];
	private readonly SemaphoreSlim _manualMarkMutationGate = new(1, 1);
    private readonly Thickness _stickyHeaderBaseMargin;

    private CancellationTokenSource? _selectionMetricsCts;
    private DispatcherTimer? _selectionMetricsDebounceTimer;
    private int _selectionMetricsVersion;
    private ExportOutputMetrics _lastSelectionMetrics =
        ExportOutputMetrics.Empty;
    private bool _hasSelectionMetricsSnapshot;
    private bool _scrollSyncActive;
    private ScrollBar? _verticalScrollBar;
	private Cursor? _previewMarkerHandCursor;
	private Cursor? _previewMarkerDragCursor;
	private InputElement? _previewMarkerCursorTarget;
	private IPointer? _previewMarkerDragPointer;
	private double _previewMarkerDragStartY;
	private double _previewMarkerDragStartOffsetY;
	private bool _previewMarkerDragging;
	private bool _previewMarkerViewerAllowAutoHide;
	private bool _previewMarkerScrollBarAllowAutoHide;
	private bool _previewMarkerScrollBarInteractionActive;
    private double _stickyHeaderScrollBarInset = -1;
	private Vector? _pendingRedactionViewportOffset;
	private PersistentSecretMarkId? _pendingMarkedSecretId;
    private bool _disposed;

    public PreviewSurfaceController(
        Window window,
        MainWindowViewModel viewModel,
        PreviewSurfaceControls controls,
        LocalizationService localization,
        IToastService toastService,
        PreviewDocumentBuilder previewDocumentBuilder,
		SecretRedactionOutputPreparer secretRedactionPreparer,
		SecretRedactionSession secretRedactionSession,
        SelectedContentExportService contentExport,
        ProjectTextOutputPipeline textOutputPipeline,
        TreeExportService treeExport,
        MetricsPipeline metrics,
        PreviewWorkspacePipeline previewPipeline,
        Func<bool> ensureClipboardOutputReady,
        Func<string, Task> setClipboardTextAsync,
		Func<string, Task> showErrorAsync,
		Func<string?> projectRootProvider,
		Func<ContentTransformationContext?> transformationContextProvider,
		Action requestRedactionRefresh,
		Func<ManualRedactionClass, bool> ensureManualRedactionClassEnabled,
		Func<PersistentSecretMarkDelta, Task<PersistentSecretMarkWriteResult>> applyPersistentMarkDelta,
		Func<CancellationToken, Task> persistProjectProfile)
    {
        _window = window;
        _viewModel = viewModel;
        _controls = controls;
        _localization = localization;
        _toastService = toastService;
        _previewDocumentBuilder = previewDocumentBuilder;
		_secretRedactionPreparer = secretRedactionPreparer;
		_secretRedactionSession = secretRedactionSession;
        _contentExport = contentExport;
        _textOutputPipeline = textOutputPipeline;
        _treeExport = treeExport;
        _metrics = metrics;
        _previewPipeline = previewPipeline;
        _ensureClipboardOutputReady = ensureClipboardOutputReady;
        _setClipboardTextAsync = setClipboardTextAsync;
		_showErrorAsync = showErrorAsync;
		_projectRootProvider = projectRootProvider;
		_transformationContextProvider = transformationContextProvider;
		_requestRedactionRefresh = requestRedactionRefresh;
		_ensureManualRedactionClassEnabled = ensureManualRedactionClassEnabled;
		_applyPersistentMarkDelta = applyPersistentMarkDelta;
		_persistProjectProfile = persistProjectProfile;
        _stickyHeaderBaseMargin = controls.StickyHeaderContainer.Margin;

        controls.TextControl.VerticalOffset =
            Math.Max(0, controls.TextScrollViewer.Offset.Y);
        controls.TextControl.ViewportHeight =
            Math.Max(0, controls.TextScrollViewer.Viewport.Height);
        controls.TextControl.ViewportWidth =
            Math.Max(0, controls.TextScrollViewer.Viewport.Width);
        controls.TextControl.CopyingToClipboard += OnCopyingToClipboard;
        controls.TextControl.CopiedToClipboard += OnCopiedToClipboard;
        controls.TextControl.PreviewSelectionChanged +=
            OnSelectionChanged;
		controls.TextControl.RedactionToggleRequested += OnRedactionToggleRequested;
		controls.TextControl.BulkRedactionToggleRequested += OnBulkRedactionToggleRequested;
		controls.TextControl.ManualSecretMarkRequested += OnManualSecretMarkRequested;
		controls.TextControl.ManualSecretUnmarkRequested += OnManualSecretUnmarkRequested;
		controls.TextControl.ManualSecretMarkRejected += OnManualSecretMarkRejected;
		controls.TextControl.PreviewMarkersChanged += OnPreviewMarkersChanged;
		controls.MarkerBar.SetMarkers(controls.TextControl.MarkerSnapshot);
		controls.TextScrollViewer.AddHandler(
			InputElement.PointerPressedEvent,
			OnPreviewMarkerPointerPressed,
			RoutingStrategies.Tunnel,
			handledEventsToo: true);
		controls.TextScrollViewer.AddHandler(
			InputElement.PointerMovedEvent,
			OnPreviewMarkerPointerMoved,
			RoutingStrategies.Tunnel,
			handledEventsToo: true);
		controls.TextScrollViewer.AddHandler(
			InputElement.PointerReleasedEvent,
			OnPreviewMarkerPointerReleased,
			RoutingStrategies.Tunnel,
			handledEventsToo: true);
		controls.TextScrollViewer.PointerExited += OnPreviewMarkerPointerExited;
		controls.TextScrollViewer.PointerCaptureLost += OnPreviewMarkerPointerCaptureLost;
        controls.TextScrollViewer.LayoutUpdated += OnTextScrollViewerLayoutUpdated;
    }

	private void OnPreviewMarkersChanged(
		object? sender,
		PreviewMarkersChangedEventArgs e)
	{
		_controls.MarkerBar.SetMarkers(e.Snapshot);
		UpdateStickyHeaderScrollBarInset();
	}

	private void OnPreviewMarkerPointerPressed(
		object? sender,
		PointerPressedEventArgs e)
	{
		if (!e.GetCurrentPoint(_controls.TextScrollViewer).Properties.IsLeftButtonPressed)
			return;

		var target = _controls.MarkerBar.FindTargetAt(e.GetPosition(_controls.MarkerBar));
		if (target is null)
			return;

		_controls.TextControl.NavigateToMarker(target.Value);
		_previewMarkerDragPointer = e.Pointer;
		_previewMarkerDragStartY = e.GetPosition(_controls.MarkerBar).Y;
		_previewMarkerDragStartOffsetY = _controls.TextScrollViewer.Offset.Y;
		_previewMarkerDragging = false;
		e.Pointer.Capture(_controls.TextScrollViewer);
		BeginPreviewMarkerScrollBarInteraction();
		SetPreviewMarkerCursor(_controls.TextScrollViewer, isDragging: true);
		e.Handled = true;
	}

	private void OnPreviewMarkerPointerMoved(
		object? sender,
		PointerEventArgs e)
	{
		if (ReferenceEquals(_previewMarkerDragPointer, e.Pointer) &&
		    e.GetCurrentPoint(_controls.TextScrollViewer).Properties.IsLeftButtonPressed)
		{
			var deltaY = e.GetPosition(_controls.MarkerBar).Y - _previewMarkerDragStartY;
			_previewMarkerDragging |= Math.Abs(deltaY) >= 2;
			if (_previewMarkerDragging)
			{
				KeepPreviewMarkerScrollBarActive();
				SetPreviewMarkerCursor(_controls.TextScrollViewer, isDragging: true);
				ScrollFromPreviewMarkerDrag(deltaY);
			}

			e.Handled = true;
			return;
		}

		SetPreviewMarkerCursor(
			_controls.MarkerBar.FindTargetAt(e.GetPosition(_controls.MarkerBar)) is not null
				? e.Source as InputElement
				: null);
	}

	private void OnPreviewMarkerPointerReleased(
		object? sender,
		PointerReleasedEventArgs e)
	{
		if (!ReferenceEquals(_previewMarkerDragPointer, e.Pointer))
			return;

		EndPreviewMarkerDrag(releaseCapture: true);
		SetPreviewMarkerCursor(
			_controls.MarkerBar.FindTargetAt(e.GetPosition(_controls.MarkerBar)) is not null
				? e.Source as InputElement
				: null);
		e.Handled = true;
	}

	private void OnPreviewMarkerPointerCaptureLost(
		object? sender,
		PointerCaptureLostEventArgs e)
	{
		if (ReferenceEquals(_previewMarkerDragPointer, e.Pointer))
		{
			EndPreviewMarkerDrag(releaseCapture: false);
			SetPreviewMarkerCursor(null);
		}
	}

	private void ScrollFromPreviewMarkerDrag(double deltaY)
	{
		if (_verticalScrollBar?.GetVisualDescendants().OfType<Track>().FirstOrDefault() is not { } track ||
		    track.GetVisualDescendants().OfType<Thumb>().FirstOrDefault() is not { } thumb)
		{
			return;
		}

		var maximumOffset = Math.Max(
			0,
			_controls.TextScrollViewer.Extent.Height - _controls.TextScrollViewer.Viewport.Height);
		var thumbTravel = Math.Max(1, track.Bounds.Height - thumb.Bounds.Height);
		var targetY = Math.Clamp(
			_previewMarkerDragStartOffsetY + ((deltaY / thumbTravel) * maximumOffset),
			0,
			maximumOffset);
		_controls.TextScrollViewer.Offset = new Vector(
			_controls.TextScrollViewer.Offset.X,
			targetY);
	}

	private void EndPreviewMarkerDrag(bool releaseCapture)
	{
		var pointer = _previewMarkerDragPointer;
		_previewMarkerDragPointer = null;
		_previewMarkerDragging = false;
		if (releaseCapture && ReferenceEquals(pointer?.Captured, _controls.TextScrollViewer))
			pointer.Capture(null);

		EndPreviewMarkerScrollBarInteraction();
	}

	private void BeginPreviewMarkerScrollBarInteraction()
	{
		_previewMarkerViewerAllowAutoHide = _controls.TextScrollViewer.AllowAutoHide;
		_previewMarkerScrollBarAllowAutoHide = _verticalScrollBar?.AllowAutoHide ?? true;
		_previewMarkerScrollBarInteractionActive = true;
		_controls.TextScrollViewer.SetCurrentValue(ScrollViewer.AllowAutoHideProperty, false);
		if (_verticalScrollBar is not null)
			_verticalScrollBar.SetCurrentValue(ScrollBar.AllowAutoHideProperty, false);
	}

	private void KeepPreviewMarkerScrollBarActive()
	{
		_controls.TextScrollViewer.SetCurrentValue(ScrollViewer.AllowAutoHideProperty, false);
		_verticalScrollBar?.SetCurrentValue(ScrollBar.AllowAutoHideProperty, false);
	}

	private void EndPreviewMarkerScrollBarInteraction()
	{
		if (!_previewMarkerScrollBarInteractionActive)
			return;

		_previewMarkerScrollBarInteractionActive = false;
		_controls.TextScrollViewer.SetCurrentValue(
			ScrollViewer.AllowAutoHideProperty,
			_previewMarkerViewerAllowAutoHide);
		if (_verticalScrollBar is null)
			return;

		_verticalScrollBar.SetCurrentValue(
			ScrollBar.AllowAutoHideProperty,
			_previewMarkerScrollBarAllowAutoHide);
	}

	private void OnPreviewMarkerPointerExited(
		object? sender,
		PointerEventArgs e)
		=> SetPreviewMarkerCursor(null);

	private void SetPreviewMarkerCursor(
		InputElement? target,
		bool isDragging = false)
	{
		var cursor = isDragging
			? _previewMarkerDragCursor ??= new Cursor(StandardCursorType.Arrow)
			: _previewMarkerHandCursor ??= new Cursor(StandardCursorType.Hand);
		if (ReferenceEquals(_previewMarkerCursorTarget, target) &&
		    ReferenceEquals(target?.Cursor, cursor))
		{
			return;
		}

		_previewMarkerCursorTarget?.ClearValue(InputElement.CursorProperty);
		_previewMarkerCursorTarget = target;
		if (target is null)
			return;

		target.Cursor = cursor;
	}

    private void OnVerticalScrollBarPropertyChanged(
        object? sender,
        AvaloniaPropertyChangedEventArgs e)
    {
        if (!_disposed &&
            (e.Property == ScrollBar.IsExpandedProperty ||
             e.Property == Visual.IsVisibleProperty ||
             e.Property == Layoutable.BoundsProperty))
        {
            UpdateStickyHeaderScrollBarInset();
        }
    }

    private void OnTextScrollViewerLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_disposed)
            UpdateStickyHeaderScrollBarInset();
    }

	private async void OnManualSecretMarkRejected(
		object? sender,
		PreviewManualSecretMarkRejectedEventArgs e)
	{
		if (!_disposed)
			await _showErrorAsync(e.Message);
	}

	private void OnRedactionToggleRequested(
		object? sender,
		PreviewRedactionToggleRequestedEventArgs e)
	{
		var context = _transformationContextProvider()?.Redaction;
		if (context is null)
			return;

		_pendingRedactionViewportOffset = _controls.TextScrollViewer.Offset;
		if (e.RestoreOccurrenceIds is { Count: > 0 })
			context.Session.SetKeepAsIs(e.RestoreOccurrenceIds, keep: false);
		else
			context.Session.ToggleKeepAsIs(e.OccurrenceId);
		_requestRedactionRefresh();
	}

	private void OnBulkRedactionToggleRequested(
		object? sender,
		PreviewBulkRedactionToggleRequestedEventArgs e)
	{
		var context = _transformationContextProvider()?.Redaction;
		if (context is null)
			return;

		var changedCount = context.Session.SetKeepAsIs(e.OccurrenceIds, e.Keep);
		if (changedCount == 0)
			return;

		_pendingRedactionViewportOffset = _controls.TextScrollViewer.Offset;
		_requestRedactionRefresh();
		_toastService.Show(_localization.Format(
			e.Keep ? "Toast.Secret.KeptCount" : "Toast.Secret.RehiddenCount",
			changedCount));
	}

	private void OnManualSecretMarkRequested(
		object? sender,
		PreviewManualSecretMarkRequestedEventArgs e)
	{
		var operation = ApplyManualSecretMarkAsync(e);
		TrackManualMarkOperation(operation);
	}

	private async Task ApplyManualSecretMarkAsync(
		PreviewManualSecretMarkRequestedEventArgs e)
	{
		string? operationProjectRoot = null;
		try
		{
			operationProjectRoot = _projectRootProvider();
			var document = _controls.TextControl.Document ?? _viewModel.PreviewDocument;
			if (document is null || string.IsNullOrWhiteSpace(operationProjectRoot) ||
			    !TryResolveManualMarkLocation(document, e, out var location))
			{
				if (!_disposed)
					await _showErrorAsync(_localization["Error.Secret.MarkApplyFailed"]);
				return;
			}

			var sessionMarkAdded = TryApplySessionOnlyManualMark(location, e.Value, e.Class);
			if (!sessionMarkAdded && !e.Persistent)
			{
				_toastService.Show(_localization["Toast.Secret.AlreadyHidden"]);
				return;
			}
			if (!sessionMarkAdded && !_ensureManualRedactionClassEnabled(e.Class))
				_requestRedactionRefresh();
			await _persistProjectProfile(CancellationToken.None);
			var mark = e.Persistent
				? await _secretRedactionSession.CreatePersistentMarkedSecretAsync(
					e.Value,
					location.Key,
					e.Class,
					CancellationToken.None)
				: await _secretRedactionSession.CreatePersistentSourceMarkedSecretAsync(
					e.Value,
					location.Key,
					location.RelativePath,
					location.SourceOffset,
					e.Class,
					CancellationToken.None);
			if (mark is null)
			{
				if (IsCurrentProject(operationProjectRoot))
					await _showErrorAsync(_localization["Terminal.Error.ProfileWriteFailed"]);
				return;
			}
			if (_disposed || !IsCurrentProject(operationProjectRoot))
				return;
			PersistentSecretMarkDelta delta;
			PersistentMarkStageResult promotion;
			await _manualMarkMutationGate.WaitAsync(CancellationToken.None);
			try
			{
				if (_disposed || !IsCurrentProject(operationProjectRoot))
					return;
				delta = PersistentSecretMarkDelta.Add(
					mark,
					_secretRedactionSession.PersistentMarksStoreRevision);
				promotion = _secretRedactionSession.TryPromoteSessionMarkToPendingPersistentMark(
					operationProjectRoot,
					location.RelativePath,
					location.SourceOffset,
					e.Value,
					delta);
			}
			finally
			{
				_manualMarkMutationGate.Release();
			}
			if (!promotion.Staged)
				return;

			PersistentSecretMarkWriteResult write;
			try
			{
				write = await _applyPersistentMarkDelta(delta);
			}
			catch
			{
				if (!_disposed && IsCurrentProject(operationProjectRoot))
					DowngradePersistentMarkToSession(
						operationProjectRoot,
						delta.OperationId,
						location,
						e.Value,
						e.Class);
				throw;
			}
			if (_disposed)
				return;
			if (write.Succeeded)
			{
				if (IsCurrentProject(operationProjectRoot))
				{
					_pendingMarkedSecretId = new PersistentSecretMarkId(
						mark.H,
						mark.Length,
						mark.RelativePath,
						mark.SourceOffset,
						mark.Class);
					_requestRedactionRefresh();
				}
				return;
			}

			if (IsCurrentProject(operationProjectRoot))
				DowngradePersistentMarkToSession(
					operationProjectRoot,
					delta.OperationId,
					location,
					e.Value,
					e.Class);
			if (IsCurrentProject(operationProjectRoot))
				await _showErrorAsync(_localization["Terminal.Error.ProfileWriteFailed"]);
		}
		catch (OperationCanceledException)
		{
			// Window teardown can cancel pending UI work after the durable store has already decided it.
		}
		catch (Exception exception)
		{
			if (!_disposed && IsCurrentProject(operationProjectRoot))
				await _showErrorAsync(exception.Message);
		}
	}

	private void TrackManualMarkOperation(Task operation)
	{
		if (operation.IsCompleted)
			return;
		lock (_manualMarkOperationsSync)
			_manualMarkOperations.Add(operation);
		_ = operation.ContinueWith(
			completed =>
			{
				lock (_manualMarkOperationsSync)
					_manualMarkOperations.Remove(completed);
			},
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);
	}

	internal bool HasPendingManualMarkOperations
	{
		get
		{
			lock (_manualMarkOperationsSync)
				return _manualMarkOperations.Count > 0;
		}
	}

	internal async Task WaitForPendingManualMarkOperationsAsync()
	{
		while (true)
		{
			Task pending;
			lock (_manualMarkOperationsSync)
			{
				if (_manualMarkOperations.Count == 0)
					return;
				pending = Task.WhenAll(_manualMarkOperations);
			}
			await pending.ConfigureAwait(true);
		}
	}

	private Task[] CapturePendingManualMarkOperations()
	{
		lock (_manualMarkOperationsSync)
			return _manualMarkOperations.ToArray();
	}

	private void DowngradePersistentMarkToSession(
		string projectRoot,
		Guid operationId,
		ManualSecretLocation location,
		MarkedSecretValue value,
		ManualRedactionClass classification)
	{
		TryApplySessionOnlyManualMark(location, value, classification);
		_secretRedactionSession.RollbackPendingPersistentMarkDelta(projectRoot, operationId);
	}

	private bool TryApplySessionOnlyManualMark(
		ManualSecretLocation location,
		MarkedSecretValue value,
		ManualRedactionClass classification)
	{
		if (!_secretRedactionSession.AddSessionMarkedSecret(
			    location.RelativePath,
			    location.SourceOffset,
			    value,
			    classification))
		{
			return false;
		}

		_pendingRedactionViewportOffset = _controls.TextScrollViewer.Offset;
		if (!_ensureManualRedactionClassEnabled(classification))
			_requestRedactionRefresh();
		return true;
	}

	private void OnManualSecretUnmarkRequested(
		object? sender,
		PreviewManualSecretUnmarkRequestedEventArgs e)
	{
		var operation = RemoveManualSecretMarkAsync(e);
		TrackManualMarkOperation(operation);
	}

	private async Task RemoveManualSecretMarkAsync(
		PreviewManualSecretUnmarkRequestedEventArgs e)
	{
		string? operationProjectRoot = null;
		try
		{
			var precedingOperations = CapturePendingManualMarkOperations();
			operationProjectRoot = _projectRootProvider();
			if (string.IsNullOrWhiteSpace(operationProjectRoot))
				return;
			PersistentSecretMarkId? persistentMarkId = e.PersistentMarkId;
			if (persistentMarkId is null && !string.IsNullOrWhiteSpace(e.PersistentMarkHash))
			{
				if (!PersistentSecretIdentity.IsSupported(e.PersistentMarkHash) ||
				    e.PersistentMarkLength is < MarkedSecretValueNormalizer.MinimumLength or
					    > MarkedSecretValueNormalizer.MaximumLength)
				{
					await _showErrorAsync(_localization["Error.Secret.MarkApplyFailed"]);
					return;
				}

				persistentMarkId = new PersistentSecretMarkId(
					e.PersistentMarkHash,
					e.PersistentMarkLength);
			}
			else if (persistentMarkId is { } suppliedMarkId &&
			         (!PersistentSecretIdentity.IsSupported(suppliedMarkId.Hash) ||
			          suppliedMarkId.Length is < MarkedSecretValueNormalizer.MinimumLength or
				          > MarkedSecretValueNormalizer.MaximumLength))
			{
				await _showErrorAsync(_localization["Error.Secret.MarkApplyFailed"]);
				return;
			}
			var sessionRemoved = false;
			var waitsForPersistentAdd = persistentMarkId is not null;
			PersistentSecretMarkDelta? pendingRemove = null;
			var persistentStage = default(PersistentMarkStageResult);
			await _manualMarkMutationGate.WaitAsync(CancellationToken.None);
			try
			{
				if (persistentMarkId is null &&
				    !string.IsNullOrWhiteSpace(e.SessionMarkId) &&
				    _secretRedactionSession.TryResolvePromotedPersistentMarkId(
					    e.SessionMarkId,
					    out var promotedMarkId))
				{
					persistentMarkId = promotedMarkId;
					waitsForPersistentAdd = true;
				}
				sessionRemoved = !string.IsNullOrWhiteSpace(e.SessionMarkId) &&
				                 _secretRedactionSession.RemoveSessionMarkedSecret(e.SessionMarkId);
			}
			finally
			{
				_manualMarkMutationGate.Release();
			}

			if (persistentMarkId is null)
			{
				if (sessionRemoved)
				{
					_pendingRedactionViewportOffset = _controls.TextScrollViewer.Offset;
					_requestRedactionRefresh();
				}
				return;
			}

			if (waitsForPersistentAdd && precedingOperations.Length > 0)
				await Task.WhenAll(precedingOperations);
			if (_disposed || !IsCurrentProject(operationProjectRoot))
				return;

			await _manualMarkMutationGate.WaitAsync(CancellationToken.None);
			try
			{
				if (_disposed || !IsCurrentProject(operationProjectRoot))
					return;
				if (!string.IsNullOrWhiteSpace(e.SessionMarkId))
				{
					if (_secretRedactionSession.TryResolvePromotedPersistentMarkId(
					    e.SessionMarkId,
					    out var promotedMarkId))
					{
						persistentMarkId = promotedMarkId;
					}
					sessionRemoved |= _secretRedactionSession.RemoveSessionMarkedSecret(e.SessionMarkId);
				}

				pendingRemove = PersistentSecretMarkDelta.Remove(
					persistentMarkId.Value,
					_secretRedactionSession.PersistentMarksStoreRevision);
				persistentStage = _secretRedactionSession.StagePersistentMarkDelta(
					operationProjectRoot,
					pendingRemove);
			}
			finally
			{
				_manualMarkMutationGate.Release();
			}
			if (!persistentStage.Staged && !sessionRemoved)
				return;

			if (persistentStage.EffectiveChanged || sessionRemoved)
			{
				_pendingRedactionViewportOffset = _controls.TextScrollViewer.Offset;
				_requestRedactionRefresh();
			}
			if (persistentStage.Staged && pendingRemove is not null)
			{
				PersistentSecretMarkWriteResult write;
				try
				{
					write = await _applyPersistentMarkDelta(pendingRemove);
				}
				catch
				{
					if (!_disposed && IsCurrentProject(operationProjectRoot))
					{
						_secretRedactionSession.RollbackPendingPersistentMarkDelta(
							operationProjectRoot,
							pendingRemove.OperationId);
						_requestRedactionRefresh();
					}
					throw;
				}
				if (_disposed)
					return;
				if (!write.Succeeded)
				{
					if (IsCurrentProject(operationProjectRoot))
					{
						_secretRedactionSession.RollbackPendingPersistentMarkDelta(
							operationProjectRoot,
							pendingRemove.OperationId);
						_requestRedactionRefresh();
					}
					if (IsCurrentProject(operationProjectRoot))
						await _showErrorAsync(_localization["Terminal.Error.ProfileWriteFailed"]);
					return;
				}
			}

			if (IsCurrentProject(operationProjectRoot))
			{
				_toastService.Show(_localization[
					e.AlsoDetected
						? "Toast.Secret.MarkRemovedStillDetected"
						: "Toast.Secret.MarkRemoved"]);
			}
		}
		catch (OperationCanceledException)
		{
			// Window teardown can cancel pending UI work after the durable store has already decided it.
		}
		catch (Exception exception)
		{
			if (!_disposed && IsCurrentProject(operationProjectRoot))
				await _showErrorAsync(exception.Message);
		}
	}

	private bool IsCurrentProject(string? expectedProjectRoot)
	{
		if (string.IsNullOrWhiteSpace(expectedProjectRoot))
			return false;
		var currentProjectRoot = _projectRootProvider();
		if (string.IsNullOrWhiteSpace(currentProjectRoot))
			return false;
		try
		{
			return PathComparer.Default.Equals(
				Path.GetFullPath(expectedProjectRoot),
				Path.GetFullPath(currentProjectRoot));
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
		{
			return false;
		}
	}

	private bool TryResolveManualMarkLocation(
		IPreviewTextDocument document,
		PreviewManualSecretMarkRequestedEventArgs request,
		out ManualSecretLocation location)
	{
		location = default;
		var selection = request.Selection.Normalize();
		if (selection.StartLine != selection.EndLine)
			return false;
		var section = PreviewDocumentSectionLookup.FindContainingSection(
			document.Sections,
			selection.StartLine);
		if (section is null || selection.StartLine < section.ContentStartLine)
			return false;

		var previewLine = document.GetLineText(selection.StartLine);
		var key = MarkedSecretValueNormalizer.ExtractKey(
			previewLine,
			selection.StartColumn + request.Value.LeadingCharactersRemoved);
		if (section.CoordinateMap is null ||
		    !section.CoordinateMap.TryToSourceOffset(
			    selection.StartLine - section.ContentStartLine,
			    selection.StartColumn + request.Value.LeadingCharactersRemoved,
			    out var sourceOffset))
		{
			return false;
		}

		location = new ManualSecretLocation(
			ResolveManualMarkRelativePath(
				section,
				_projectRootProvider()),
			sourceOffset,
			key);
		return true;
	}

	private static string ResolveManualMarkRelativePath(
		PreviewDocumentSection section,
		string? projectRoot)
	{
		var sourcePath = section.SourcePath;
		if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(projectRoot))
			return section.DisplayPath;

		return Path.GetRelativePath(projectRoot, sourcePath).Replace('\\', '/');
	}

    public bool HasSelectionMetricsSnapshot =>
        _hasSelectionMetricsSnapshot;

    public void HandleTextScrollChanged(
        object? sender,
        ScrollChangedEventArgs _)
    {
        if (_scrollSyncActive ||
            sender is not ScrollViewer textScrollViewer)
        {
            return;
        }

        _controls.TextControl.HorizontalOffset =
            Math.Max(0, textScrollViewer.Offset.X);
        _controls.TextControl.VerticalOffset =
            Math.Max(0, textScrollViewer.Offset.Y);
        _controls.TextControl.ViewportHeight =
            Math.Max(0, textScrollViewer.Viewport.Height);
        _controls.TextControl.ViewportWidth =
            Math.Max(0, textScrollViewer.Viewport.Width);

        _controls.LineNumbersControl.ExtentHeight =
            Math.Max(0, textScrollViewer.Extent.Height);
        _controls.LineNumbersControl.ViewportHeight =
            Math.Max(0, textScrollViewer.Viewport.Height);

        var targetY = textScrollViewer.Offset.Y;
        if (Math.Abs(
                _controls.LineNumbersControl.VerticalOffset -
                targetY) >= 0.1)
        {
            try
            {
                _scrollSyncActive = true;
                _controls.LineNumbersControl.VerticalOffset = targetY;
            }
            finally
            {
                _scrollSyncActive = false;
            }
        }

        UpdateStickyPath();
    }

    public async Task CopyVisibleFilePathAsync()
    {
        if (!_ensureClipboardOutputReady() ||
            !await WaitForClipboardSourceReadyAsync()
                .ConfigureAwait(true) ||
            !TryBuildCurrentStickySectionCopyPayload(out var payload))
        {
            return;
        }

        try
        {
            await _setClipboardTextAsync(payload);
            _toastService.Show(
                _localization["Toast.Copy.Preview"]);
        }
        catch (Exception ex)
        {
            await _showErrorAsync(ex.Message);
        }
    }

    public async Task CopyCurrentPreviewAsync()
    {
        if (!_ensureClipboardOutputReady() ||
            !await WaitForClipboardSourceReadyAsync()
                .ConfigureAwait(true) ||
            !TryBuildCurrentPreviewCopyPayload(out var payload))
        {
            return;
        }

        try
        {
            await _setClipboardTextAsync(payload);
            var toastKey =
                _viewModel.SelectedPreviewContentMode switch
                {
                    PreviewContentMode.Tree => "Toast.Copy.Tree",
                    PreviewContentMode.Content =>
                        "Toast.Copy.Content",
                    _ => "Toast.Copy.TreeAndContent"
                };
            _toastService.Show(_localization[toastKey]);
        }
        catch (Exception ex)
        {
            await _showErrorAsync(ex.Message);
        }
    }

    public void RefreshStickyPath()
        => UpdateStickyPath();

    public void ScrollCurrentStickySectionToStart()
    {
        if (!TryGetCurrentStickySection(out var currentSection))
            return;

        var scrollViewer = _controls.TextScrollViewer;
        var maximumY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var targetOffset = new Vector(
            scrollViewer.Offset.X,
            Math.Clamp(
                _controls.TextControl.GetVerticalOffsetForLine(currentSection.HeaderLine),
                0,
                maximumY));

        try
        {
            _scrollSyncActive = true;
            scrollViewer.Offset = targetOffset;
            _controls.LineNumbersControl.VerticalOffset = targetOffset.Y;
            _controls.TextControl.HorizontalOffset = targetOffset.X;
            _controls.TextControl.VerticalOffset = targetOffset.Y;
        }
        finally
        {
            _scrollSyncActive = false;
        }

        _controls.TextControl.Focus();
        UpdateStickyPath();
    }

    public void HandleScrollViewerPointerPressed(
        PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_controls.TextScrollViewer)
                .Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is Visual sourceVisual)
        {
            if (sourceVisual is VirtualizedPreviewTextControl ||
                sourceVisual.FindAncestorOfType<
                    VirtualizedPreviewTextControl>() is not null)
            {
                return;
            }

            if (sourceVisual is ScrollBar or Thumb or RepeatButton ||
                sourceVisual.FindAncestorOfType<ScrollBar>() is not null)
            {
                return;
            }
        }

        var viewportPoint =
            e.GetPosition(_controls.TextScrollViewer);
        var handledByPreview =
            _controls.TextControl.TryHandleViewportSelectionStart(
                e.Pointer,
                viewportPoint,
                e.KeyModifiers);

        if (!handledByPreview)
            _controls.TextControl.ClearSelection();

        e.Handled = true;
    }

    public bool TryBuildCurrentPreviewCopyPayload(
        out string previewPayload)
    {
        previewPayload = string.Empty;
        var document =
            _controls.TextControl.Document ??
            _viewModel.PreviewDocument;
        if (document is null)
            return false;

        previewPayload =
            PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(
                document);
        return !string.IsNullOrWhiteSpace(previewPayload);
    }

    public bool TryBuildCurrentStickySectionCopyPayload(
        out string sectionPayload)
    {
        sectionPayload = string.Empty;
        if (!TryGetCurrentStickySection(out var currentSection))
            return false;

        var document =
            _controls.TextControl.Document ??
            _viewModel.PreviewDocument;
        if (document is null)
            return false;

        sectionPayload =
            PreviewClipboardPayloadBuilder.BuildSectionPayload(
                document,
                currentSection);
        return !string.IsNullOrWhiteSpace(sectionPayload);
    }

    public bool TryGetCurrentStickySection(
        out PreviewDocumentSection currentSection)
    {
        currentSection = null!;
        if (!_viewModel.IsAnyPreviewVisible)
            return false;

        var document =
            _controls.TextControl.Document ??
            _viewModel.PreviewDocument;
        if (document?.Sections is not { Count: > 0 } sections)
            return false;

        var verticalOffset = _controls.TextScrollViewer.Offset.Y;
        var topLine =
            _controls.TextControl.GetLineNumberAtVerticalOffset(
                verticalOffset);
        if (topLine < sections[0].StartLine)
            return false;

        currentSection =
            PreviewDocumentSectionLookup.FindContainingSection(
                sections,
                topLine) ??
            PreviewDocumentSectionLookup.FindContainingOrNextSection(
                sections,
                topLine) ??
            sections[^1];
        return true;
    }

    public async Task<PreviewWarmupSnapshot?>
        TryBuildWarmupSnapshotAsync(
            PreviewContentMode mode,
            TreeTextFormat treeFormat,
            bool hasSelection,
            IReadOnlySet<string> selectedPaths,
            string? currentPath,
            TreeNodeDescriptor? currentTreeRoot,
            IReadOnlyList<string>? currentTreeOrderedFilePaths,
            ExportPathPresentation? pathPresentation,
            string noTextContentText,
            string noCheckedFilesText,
            CancellationToken cancellationToken)
    {
        var transformationContext = _transformationContextProvider();
        // A partial warmup cannot assign the same deterministic secret indexes as the full
        // selection. Compression is safe here: its plans are file-local and reused by the full build.
        if (!PreviewWarmupPolicy.SupportsTransformationContext(transformationContext))
            return null;
        var compressionContext = transformationContext?.Compression;

        if (!PreviewWarmupPolicy.ShouldBuildPreviewWarmup(
                mode,
                hasSelection,
                selectedPaths,
                currentTreeRoot))
        {
            return null;
        }

        return await Task.Run<PreviewWarmupSnapshot?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selectionPlan = PreviewWarmupPolicy.CreateSelectionPlan(
                currentTreeRoot,
                hasSelection ? selectedPaths : EmptySelectedPaths);
            var files = mode == PreviewContentMode.Tree
                ? []
                : PreviewWarmupPolicy.CollectInitialPreviewFiles(
                    selectionPlan,
                    PreviewWarmupCandidateFileLimit,
                    PreviewWarmupCandidateNodeVisitLimit,
                    currentTreeOrderedFilePaths);

            if (mode == PreviewContentMode.Content)
            {
                if (files.Count == 0)
                {
                    var fallbackText = hasSelection
                        ? noCheckedFilesText
                        : noTextContentText;
                    return CreateWarmupSnapshot(fallbackText);
                }

                var contentText = _contentExport.BuildBoundedPreviewAsync(
                        files,
                        PreviewWarmupContentFileLimit,
                        PreviewWarmupMaxFileBytes,
                        PreviewWarmupMaxCharacters,
                        cancellationToken,
						pathPresentation?.MapFilePath,
						compressionContext)
                    .GetAwaiter()
                    .GetResult();
                if (string.IsNullOrWhiteSpace(contentText))
                    contentText = noTextContentText;

                return CreateWarmupSnapshot(contentText);
            }

            if (string.IsNullOrWhiteSpace(currentPath) ||
                currentTreeRoot is null)
            {
                return null;
            }

            var projectedTree = PreviewWarmupPolicy.CreateBoundedTreeProjection(
                selectionPlan,
                PreviewWarmupTreeNodeLimit);
            if (projectedTree is null)
                return null;

            var treeText = _textOutputPipeline.BuildTree(
                new ProjectTextOutputSnapshot(
                    currentPath,
                    projectedTree,
                    EmptySelectedPaths,
                    OrderedFilePaths: null,
                    treeFormat,
                    pathPresentation,
					transformationContext),
                cancellationToken);
            if (string.IsNullOrWhiteSpace(treeText))
                return null;

            if (mode == PreviewContentMode.Tree)
                return CreateWarmupSnapshot(treeText);

            if (mode != PreviewContentMode.TreeAndContent)
                return null;

            if (files.Count == 0)
                return CreateWarmupSnapshot(treeText);

            var combinedContent = _contentExport.BuildBoundedPreviewAsync(
                    files,
                    PreviewWarmupContentFileLimit,
                    PreviewWarmupMaxFileBytes,
                    PreviewWarmupMaxCharacters,
                    cancellationToken,
                    TreeAndContentExportService
                        .CreateRelativeContentHeaderPathMapper(
                            currentPath),
                    compressionContext)
                .GetAwaiter()
                .GetResult();
            if (string.IsNullOrWhiteSpace(combinedContent))
                return CreateWarmupSnapshot(treeText);

            var combinedBuilder =
                new StringBuilder(
                    treeText.Length +
                    combinedContent.Length +
                    16);
            combinedBuilder.Append(treeText.TrimEnd('\r', '\n'));
            combinedBuilder.AppendLine("\u00A0");
            combinedBuilder.AppendLine("\u00A0");
            combinedBuilder.Append(combinedContent);
            return CreateWarmupSnapshot(combinedBuilder.ToString());
        }, cancellationToken).ConfigureAwait(false);
    }

    public PreviewBuildResult BuildDocument(
        PreviewContentMode selectedMode,
        IReadOnlySet<string> selectedPaths,
        bool hasSelection,
        TreeTextFormat treeFormat,
        string noCheckedFilesText,
        string noTextContentText,
        string noDataText,
        string? currentPath,
        TreeNodeDescriptor? currentTreeRoot,
        IReadOnlyList<string>? currentTreeOrderedFilePaths,
        ExportPathPresentation? pathPresentation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
		var transformationContext = _transformationContextProvider();
		var outputPathRedaction = OutputRootPathPresentation.CaptureRedactionDecision(
			transformationContext);

        if (selectedMode == PreviewContentMode.Tree)
        {
			if (transformationContext?.Redaction is not null && currentTreeRoot is not null)
			{
				var selectedFiles = ResolvePreviewFiles(
					selectedPaths,
					hasSelection,
					currentTreeRoot,
					currentTreeOrderedFilePaths);
				_secretRedactionPreparer
					.AnalyzeAsync(transformationContext, selectedFiles, cancellationToken)
					.GetAwaiter()
					.GetResult();
			}

			var treeRootPresentation = OutputRootPathPresentation.ResolveWithRedaction(
				currentPath ?? string.Empty,
				pathPresentation,
				outputPathRedaction);
			var treePreviewText =
                !string.IsNullOrWhiteSpace(currentPath) &&
                currentTreeRoot is not null
                    ? _textOutputPipeline.BuildTree(
                        new ProjectTextOutputSnapshot(
                            currentPath,
                            currentTreeRoot,
                            selectedPaths,
                            currentTreeOrderedFilePaths,
                            treeFormat,
                            pathPresentation,
							transformationContext),
						cancellationToken,
						outputPathRedaction)
                    : string.Empty;
            var effectiveTreeText =
                string.IsNullOrEmpty(treePreviewText)
                    ? noDataText
                    : treePreviewText;
            return new PreviewBuildResult(
				_previewDocumentBuilder.CreateInMemoryWithGeneratedPathRedaction(
					effectiveTreeText,
					treeRootPresentation));
        }

        var files = ResolvePreviewFiles(
            selectedPaths,
            hasSelection,
            currentTreeRoot,
            currentTreeOrderedFilePaths);

        if (selectedMode == PreviewContentMode.Content)
        {
            if (files.Count == 0)
            {
                var fallbackText = hasSelection
                    ? noCheckedFilesText
                    : noTextContentText;
                return new PreviewBuildResult(
                    _previewDocumentBuilder.CreateInMemory(
                        fallbackText));
            }

			var contentDocument =
                _previewDocumentBuilder.BuildContentDocumentAsync(
                        files,
                        cancellationToken,
						pathPresentation?.MapFilePath,
						transformationContext: transformationContext,
						includeSourceCoordinateMaps: true,
						displayRootPath: null,
						outputPathRedaction: outputPathRedaction)
                    .GetAwaiter()
                    .GetResult();
            return new PreviewBuildResult(
                contentDocument ??
                _previewDocumentBuilder.CreateInMemory(
                    noTextContentText));
        }

        if (string.IsNullOrWhiteSpace(currentPath) ||
            currentTreeRoot is null)
        {
            return new PreviewBuildResult(
                _previewDocumentBuilder.CreateInMemory(
                    noTextContentText));
        }

		var treeRootPathPresentation = OutputRootPathPresentation.ResolveWithRedaction(
			currentPath,
			pathPresentation,
			outputPathRedaction);
		var displayRootPath = treeRootPathPresentation.Text;
		var treeText = selectedPaths.Count > 0
            ? _treeExport.BuildSelectedTree(
                currentPath,
                currentTreeRoot,
                selectedPaths,
                treeFormat,
                displayRootPath,
                pathPresentation?.DisplayRootName)
            : _treeExport.BuildFullTree(
                currentPath,
                currentTreeRoot,
                treeFormat,
                displayRootPath,
                pathPresentation?.DisplayRootName);

        if (selectedPaths.Count > 0 &&
            string.IsNullOrWhiteSpace(treeText))
        {
            treeText = _treeExport.BuildFullTree(
                currentPath,
                currentTreeRoot,
                treeFormat,
                displayRootPath,
                pathPresentation?.DisplayRootName);
        }

        if (string.IsNullOrWhiteSpace(treeText))
        {
            return new PreviewBuildResult(
                _previewDocumentBuilder.CreateInMemory(noDataText));
        }

        if (files.Count == 0)
        {
            return new PreviewBuildResult(
				_previewDocumentBuilder.CreateInMemoryWithGeneratedPathRedaction(
					treeText,
					treeRootPathPresentation));
        }

        var document =
            _previewDocumentBuilder
                .BuildTreeAndContentDocumentAsync(
                    treeText,
                    files,
                    cancellationToken,
                    TreeAndContentExportService
                        .CreateRelativeContentHeaderPathMapper(
                            currentPath),
                    transformationContext: transformationContext,
					includeSourceCoordinateMaps: true,
					outputPathRedaction: outputPathRedaction,
					treeRootPresentation: treeRootPathPresentation)
                .GetAwaiter()
                .GetResult();
        return new PreviewBuildResult(document);
    }

    public void ApplyText(string text)
    {
        var effectiveText = string.IsNullOrEmpty(text)
            ? _viewModel.PreviewNoDataText
            : text;
        ApplyText(
            effectiveText,
            PreviewFileCollectionPolicy.CountPreviewLines(
                effectiveText));
    }

    public void ApplyText(string text, int lineCount)
    {
        InvalidateCache();
        ApplyDocument(
            _previewDocumentBuilder.CreateInMemory(text),
            lineCount);
    }

    public void ApplyDocument(IPreviewTextDocument document)
        => ApplyDocument(document, document.LineCount);

    public void ClearDocument()
    {
		_pendingRedactionViewportOffset = null;
        ClearSelectionMetrics();
        var previousDocument = _viewModel.PreviewDocument;
        _viewModel.PreviewDocument = null;
        _viewModel.PreviewText = string.Empty;
        _viewModel.PreviewLineCount = 1;
        previousDocument?.Dispose();
        HideStickyPath();
    }

    public bool IsCurrentCacheHit(PreviewCacheKeyData key)
        => _previewPipeline.IsCurrentCacheHit(
            key,
            _viewModel.PreviewDocument);

    public void Cache(PreviewCacheKeyData key)
        => _previewPipeline.CachePreview(key);

    public void InvalidateCache()
        => _previewPipeline.InvalidateCache();

    public void RefreshSelectionMetricsPresentation()
        => RenderSelectionMetrics();

    public void ScheduleSelectionMetricsUpdate(
        bool immediate = false)
    {
        if (!_viewModel.IsAnyPreviewVisible ||
            !_controls.TextControl.TryGetSelectionRange(out _))
        {
            ClearSelectionMetrics();
            return;
        }

        if (immediate)
        {
            _selectionMetricsDebounceTimer?.Stop();
            RecalculateSelectionMetrics();
            return;
        }

        if (_selectionMetricsDebounceTimer is null)
        {
            _selectionMetricsDebounceTimer =
                new DispatcherTimer(
                    DispatcherPriority.Background,
                    _window.Dispatcher)
                {
                    Interval = SelectionMetricsDebounceInterval
                };
            _selectionMetricsDebounceTimer.Tick +=
                OnSelectionMetricsDebounceTick;
        }

        _selectionMetricsDebounceTimer.Stop();
        _selectionMetricsDebounceTimer.Start();
    }

    public void ClearSelectionMetrics()
    {
        _selectionMetricsDebounceTimer?.Stop();
        var previousCts =
            Interlocked.Exchange(ref _selectionMetricsCts, null);
        previousCts?.Cancel();
        previousCts?.Dispose();
        Interlocked.Increment(ref _selectionMetricsVersion);

        _lastSelectionMetrics = ExportOutputMetrics.Empty;
        _hasSelectionMetricsSnapshot = false;
        _viewModel.StatusPreviewSelectionVisible = false;
        _viewModel.StatusPreviewSelectionStatsText = string.Empty;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
		_controls.TextControl.CopyingToClipboard -=
			OnCopyingToClipboard;
		_controls.TextControl.RedactionToggleRequested -= OnRedactionToggleRequested;
		_controls.TextControl.BulkRedactionToggleRequested -= OnBulkRedactionToggleRequested;
		_controls.TextControl.CopiedToClipboard -=
            OnCopiedToClipboard;
        _controls.TextControl.PreviewSelectionChanged -=
            OnSelectionChanged;
		_controls.TextControl.ManualSecretMarkRequested -= OnManualSecretMarkRequested;
		_controls.TextControl.ManualSecretUnmarkRequested -= OnManualSecretUnmarkRequested;
		_controls.TextControl.ManualSecretMarkRejected -= OnManualSecretMarkRejected;
		_controls.TextControl.PreviewMarkersChanged -= OnPreviewMarkersChanged;
		_controls.TextScrollViewer.RemoveHandler(
			InputElement.PointerPressedEvent,
			OnPreviewMarkerPointerPressed);
		_controls.TextScrollViewer.RemoveHandler(
			InputElement.PointerMovedEvent,
			OnPreviewMarkerPointerMoved);
		_controls.TextScrollViewer.RemoveHandler(
			InputElement.PointerReleasedEvent,
			OnPreviewMarkerPointerReleased);
		_controls.TextScrollViewer.PointerExited -= OnPreviewMarkerPointerExited;
		_controls.TextScrollViewer.PointerCaptureLost -= OnPreviewMarkerPointerCaptureLost;
		EndPreviewMarkerDrag(releaseCapture: true);
		SetPreviewMarkerCursor(null);
        _controls.TextScrollViewer.LayoutUpdated -= OnTextScrollViewerLayoutUpdated;
        if (_verticalScrollBar is not null)
            _verticalScrollBar.PropertyChanged -= OnVerticalScrollBarPropertyChanged;

        if (_selectionMetricsDebounceTimer is not null)
        {
            _selectionMetricsDebounceTimer.Stop();
            _selectionMetricsDebounceTimer.Tick -=
                OnSelectionMetricsDebounceTick;
            _selectionMetricsDebounceTimer = null;
        }

        var metricsCts =
            Interlocked.Exchange(ref _selectionMetricsCts, null);
        metricsCts?.Cancel();
        metricsCts?.Dispose();
    }

    private void ApplyDocument(
        IPreviewTextDocument document,
        int lineCount)
    {
		var preservedOffset = _pendingRedactionViewportOffset;
		_pendingRedactionViewportOffset = null;
        ClearSelectionMetrics();
        var previousDocument = _viewModel.PreviewDocument;
        _viewModel.PreviewDocument = document;
        _viewModel.PreviewText = string.Empty;
        _viewModel.PreviewLineCount = Math.Max(1, lineCount);

		if (preservedOffset is null)
			_controls.TextScrollViewer.Offset = default;
		_controls.LineNumbersControl.VerticalOffset = preservedOffset?.Y ?? 0;
        _controls.LineNumbersControl.ExtentHeight =
            Math.Max(0, _controls.TextScrollViewer.Extent.Height);
        _controls.LineNumbersControl.ViewportHeight =
            Math.Max(0, _controls.TextScrollViewer.Viewport.Height);
		_controls.TextControl.VerticalOffset = preservedOffset?.Y ?? 0;
        _controls.TextControl.ViewportHeight =
            Math.Max(0, _controls.TextScrollViewer.Viewport.Height);
        _controls.TextControl.ViewportWidth =
            Math.Max(0, _controls.TextScrollViewer.Viewport.Width);

        if (!ReferenceEquals(previousDocument, document))
            previousDocument?.Dispose();

		if (_pendingMarkedSecretId is { } markId)
		{
			_pendingMarkedSecretId = null;
			var hiddenCount = document.Redactions
				.Where(span =>
					span.State == SecretPreviewSpanState.Redacted &&
					span.PersistentMarkId == markId)
				.Select(static span => span.OccurrenceId)
				.Distinct(StringComparer.Ordinal)
				.Count();
			_toastService.Show(_localization.Format("Toast.Secret.HiddenCount", hiddenCount));
		}

        UpdateStickyPath();
        Dispatcher.UIThread.Post(
			() =>
			{
				if (preservedOffset is { } offset)
					RestoreViewportAfterRedaction(offset);
				else
					UpdateStickyPath();
			},
            DispatcherPriority.Render);
    }

	private readonly record struct ManualSecretLocation(
		string RelativePath,
		int SourceOffset,
		string? Key);

	private void RestoreViewportAfterRedaction(Vector requestedOffset)
	{
		var scrollViewer = _controls.TextScrollViewer;
		var maximumX = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
		var maximumY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
		var restoredOffset = new Vector(
			Math.Clamp(requestedOffset.X, 0, maximumX),
			Math.Clamp(requestedOffset.Y, 0, maximumY));

		try
		{
			_scrollSyncActive = true;
			scrollViewer.Offset = restoredOffset;
			_controls.LineNumbersControl.VerticalOffset = restoredOffset.Y;
			_controls.TextControl.HorizontalOffset = restoredOffset.X;
			_controls.TextControl.VerticalOffset = restoredOffset.Y;
		}
		finally
		{
			_scrollSyncActive = false;
		}

		UpdateStickyPath();
	}

    private IReadOnlyList<string> ResolvePreviewFiles(
        IReadOnlySet<string> selectedPaths,
        bool hasSelection,
        TreeNodeDescriptor? currentTreeRoot,
        IReadOnlyList<string>? currentTreeOrderedFilePaths)
    {
        if (currentTreeRoot is null)
            return [];

        var effectiveSelectedPaths =
            ProjectTreeSelectionProjection.NormalizeSelectedPaths(
                currentTreeRoot,
                selectedPaths);
        if (hasSelection && effectiveSelectedPaths.Count > 0)
        {
            return PreviewFileCollectionPolicy
                .BuildOrderedSelectedFilePaths(
                    effectiveSelectedPaths,
                    currentTreeRoot);
        }

        return currentTreeOrderedFilePaths ??
               _metrics.GetOrBuildAllOrderedFilePaths(
                   currentTreeRoot);
    }

    private static PreviewWarmupSnapshot CreateWarmupSnapshot(
        string text)
        => new(
            text,
            PreviewFileCollectionPolicy.CountPreviewLines(text));

    private void UpdateStickyPath()
    {
        UpdateStickyHeaderScrollBarInset();
        if (!TryGetCurrentStickySection(out var currentSection))
        {
            HideStickyPath();
            return;
        }

        _controls.StickyHeaderText.Text =
            currentSection.DisplayPath;
        _controls.StickyHeaderContainer.IsVisible = true;
        _controls.StickyHeaderCap.IsVisible = true;
        SetStickyHeaderClipHeight(
            ResolveStickyHeaderOverlayHeight());

        _controls.TextControl.StickyHeaderReserved = false;
        _controls.TextControl.StickyHeaderVisible = false;
        _controls.TextControl.StickyHeaderText = string.Empty;
        _controls.LineNumbersControl.StickyHeaderReserved = false;
        _controls.LineNumbersControl.StickyHeaderVisible = false;
    }

    private void UpdateStickyHeaderScrollBarInset()
    {
        if (_verticalScrollBar is null)
        {
            _verticalScrollBar = _controls.TextScrollViewer
                .GetVisualDescendants()
                .OfType<ScrollBar>()
                .FirstOrDefault(static scrollBar => scrollBar.Orientation == Orientation.Vertical);
            if (_verticalScrollBar is not null)
            {
                _verticalScrollBar.PropertyChanged += OnVerticalScrollBarPropertyChanged;
            }
        }

		UpdatePreviewMarkerTrackMargin();

        var inset = 0.0;
        if (_verticalScrollBar is
            {
                IsVisible: true,
                IsExpanded: true,
                Bounds.Width: > 0
            } scrollBar)
        {
            var origin = scrollBar.TranslatePoint(default, _controls.TextScrollViewer);
            inset = origin is { } scrollBarOrigin
                ? Math.Clamp(
                    _controls.TextScrollViewer.Bounds.Width - scrollBarOrigin.X,
                    0,
                    _controls.TextScrollViewer.Bounds.Width)
                : scrollBar.Bounds.Width;
        }

        if (Math.Abs(_stickyHeaderScrollBarInset - inset) < 0.1)
            return;

        _stickyHeaderScrollBarInset = inset;
        _controls.StickyHeaderContainer.Margin = new Thickness(
            _stickyHeaderBaseMargin.Left,
            _stickyHeaderBaseMargin.Top,
            _stickyHeaderBaseMargin.Right + inset,
            _stickyHeaderBaseMargin.Bottom);
    }

	private void UpdatePreviewMarkerTrackMargin()
	{
		var margin = default(Thickness);
		if (_verticalScrollBar is { IsVisible: true } scrollBar &&
		    scrollBar.GetVisualDescendants().OfType<Track>().FirstOrDefault() is { } track &&
		    track.GetVisualDescendants().OfType<Thumb>().FirstOrDefault() is { } thumb &&
		    track.TranslatePoint(default, _controls.TextScrollViewer) is { } origin)
		{
			var availableHeight = Math.Max(0, _controls.TextScrollViewer.Bounds.Height);
			var top = Math.Clamp(origin.Y, 0, availableHeight);
			var bottom = Math.Clamp(
				availableHeight - top - track.Bounds.Height,
				0,
				availableHeight);
			margin = new Thickness(0, top, 0, bottom);

			var lineCount = _controls.TextControl.MarkerSnapshot.TotalLineCount;
			var firstLineTop = _controls.TextControl.GetVerticalOffsetForLine(1);
			var lineHeight = lineCount > 1
				? _controls.TextControl.GetVerticalOffsetForLine(2) - firstLineTop
				: Math.Max(1, _controls.TextScrollViewer.Extent.Height - firstLineTop);
			_controls.MarkerBar.SetScrollMetrics(new PreviewMarkerScrollMetrics(
				_controls.TextScrollViewer.Extent.Height,
				_controls.TextScrollViewer.Viewport.Height,
				thumb.Bounds.Height,
				firstLineTop,
				lineHeight));
		}
		else
		{
			_controls.MarkerBar.SetScrollMetrics(null);
		}

		var current = _controls.MarkerBar.Margin;
		if (Math.Abs(current.Top - margin.Top) < 0.1 &&
		    Math.Abs(current.Bottom - margin.Bottom) < 0.1)
		{
			return;
		}

		_controls.MarkerBar.Margin = margin;
	}

    private void HideStickyPath()
    {
        _controls.StickyHeaderText.Text = string.Empty;
        _controls.StickyHeaderContainer.IsVisible = false;
        _controls.StickyHeaderCap.IsVisible = false;
        SetStickyHeaderClipHeight(0);

        _controls.TextControl.StickyHeaderReserved = false;
        _controls.TextControl.StickyHeaderVisible = false;
        _controls.TextControl.StickyHeaderText = string.Empty;
        _controls.LineNumbersControl.StickyHeaderReserved = false;
        _controls.LineNumbersControl.StickyHeaderVisible = false;
    }

    private void SetStickyHeaderClipHeight(double height)
    {
        var normalizedHeight = Math.Max(0, height);
        _controls.TextControl.TopOverlayClipHeight =
            normalizedHeight;
        _controls.LineNumbersControl.TopOverlayClipHeight =
            normalizedHeight;
    }

    private double ResolveStickyHeaderOverlayHeight()
        => Math.Max(
            24.0,
            Math.Ceiling(
                _controls.TextControl.TextFontSize + 12.0));

    private async Task<bool> WaitForClipboardSourceReadyAsync()
    {
        if (!_viewModel.IsAnyPreviewVisible)
            return false;

        if (!_viewModel.IsPreviewLoading)
        {
            return (_controls.TextControl.Document ??
                    _viewModel.PreviewDocument) is not null;
        }

        var timeout = TimeSpan.FromSeconds(10);
        var stopwatch = Stopwatch.StartNew();
        while (_viewModel.IsAnyPreviewVisible &&
               _viewModel.IsPreviewLoading &&
               stopwatch.Elapsed < timeout)
        {
            await DispatcherTaskSchedulerProvider.YieldAsync(
                DispatcherPriority.Background);
            await Task.Delay(15).ConfigureAwait(true);
        }

        return !_viewModel.IsPreviewLoading &&
               (_controls.TextControl.Document ??
                _viewModel.PreviewDocument) is not null;
    }

    private void OnCopyingToClipboard(object? sender, CancelEventArgs e)
        => e.Cancel = !_ensureClipboardOutputReady();

    private void OnCopiedToClipboard(object? sender, EventArgs e)
    {
        if (_viewModel.IsAnyPreviewVisible)
            _toastService.Show(_localization["Toast.Copy.Preview"]);
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
        => ScheduleSelectionMetricsUpdate();

    private void OnSelectionMetricsDebounceTick(
        object? sender,
        EventArgs e)
    {
        _selectionMetricsDebounceTimer?.Stop();
        RecalculateSelectionMetrics();
    }

    private void RecalculateSelectionMetrics()
    {
        if (!TryCaptureSelectionMetricsSnapshot(out var snapshot))
        {
            ClearSelectionMetrics();
            return;
        }

        if (_metrics.TryGetCachedPreviewSelectionMetrics(
                _viewModel.SelectedPreviewContentMode,
                snapshot.Document,
                snapshot.SelectionRange,
                out var cachedMetrics))
        {
            ReplaceSelectionMetricsWithCached(cachedMetrics);
            return;
        }

        var metricsCts = ReplaceSelectionMetricsCancellation();
        var version =
            Interlocked.Increment(ref _selectionMetricsVersion);
        _ = RecalculateSelectionMetricsCoreAsync(
            snapshot,
            metricsCts,
            metricsCts.Token,
            version);
    }

    private void ReplaceSelectionMetricsWithCached(
        ExportOutputMetrics cachedMetrics)
    {
        _selectionMetricsDebounceTimer?.Stop();
        var previousCts =
            Interlocked.Exchange(ref _selectionMetricsCts, null);
        previousCts?.Cancel();
        previousCts?.Dispose();
        Interlocked.Increment(ref _selectionMetricsVersion);
        _lastSelectionMetrics = cachedMetrics;
        _hasSelectionMetricsSnapshot = true;
        RenderSelectionMetrics();
    }

    private CancellationTokenSource
        ReplaceSelectionMetricsCancellation()
    {
        var replacement = new CancellationTokenSource();
        var previous = Interlocked.Exchange(
            ref _selectionMetricsCts,
            replacement);
        previous?.Cancel();
        previous?.Dispose();
        return replacement;
    }

    private async Task RecalculateSelectionMetricsCoreAsync(
        PreviewSelectionMetricsSnapshot snapshot,
        CancellationTokenSource metricsCts,
        CancellationToken cancellationToken,
        int version)
    {
        try
        {
            var metrics = await Task.Run(
                () => PreviewSelectionMetricsCalculator.Calculate(
                    snapshot.Document,
                    snapshot.SelectionRange,
                    cancellationToken),
                cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested ||
                    version != Volatile.Read(
                        ref _selectionMetricsVersion))
                {
                    return;
                }

                if (!TryCaptureSelectionMetricsSnapshot(
                        out var currentSnapshot) ||
                    !ReferenceEquals(
                        currentSnapshot.Document,
                        snapshot.Document) ||
                    currentSnapshot.SelectionRange !=
                    snapshot.SelectionRange)
                {
                    return;
                }

                _lastSelectionMetrics = metrics;
                _hasSelectionMetricsSnapshot =
                    metrics != ExportOutputMetrics.Empty;
                RenderSelectionMetrics();
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _selectionMetricsCts,
                        null,
                        metricsCts),
                    metricsCts))
            {
                metricsCts.Dispose();
            }
        }
    }

    private bool TryCaptureSelectionMetricsSnapshot(
        out PreviewSelectionMetricsSnapshot snapshot)
    {
        snapshot = default;
        if (!_viewModel.IsAnyPreviewVisible)
            return false;

        var document =
            _controls.TextControl.Document ??
            _viewModel.PreviewDocument;
        if (document is null ||
            !_controls.TextControl.TryGetSelectionRange(
                out var selectionRange))
        {
            return false;
        }

        snapshot =
            new PreviewSelectionMetricsSnapshot(
                document,
                selectionRange);
        return true;
    }

    private void RenderSelectionMetrics()
    {
        if (!_hasSelectionMetricsSnapshot)
        {
            _viewModel.StatusPreviewSelectionVisible = false;
            _viewModel.StatusPreviewSelectionStatsText =
                string.Empty;
            return;
        }

        _viewModel.StatusPreviewSelectionStatsText =
            PreviewSelectionMetricsPolicy.FormatStatusMetricsText(
                _lastSelectionMetrics,
                BuildStatusMetricLabels(),
                useCompactMode: false);
        _viewModel.StatusPreviewSelectionVisible = true;
    }

    private StatusMetricLabels BuildStatusMetricLabels()
    {
        var linesLabel =
            _localization.Format("Status.Metric.Lines", "{0}");
        var charsLabel =
            _localization.Format("Status.Metric.Chars", "{0}");
        var tokensLabel =
            _localization.Format("Status.Metric.Tokens", "{0}");
        return new StatusMetricLabels(
            RemoveMetricPlaceholder(linesLabel),
            RemoveMetricPlaceholder(charsLabel),
            RemoveMetricPlaceholder(tokensLabel));
    }

    private static string RemoveMetricPlaceholder(string value)
        => value.Replace("{0}", string.Empty).Trim();

    private readonly record struct PreviewSelectionMetricsSnapshot(
        IPreviewTextDocument Document,
        PreviewSelectionRange SelectionRange);
}
