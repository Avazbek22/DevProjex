using Avalonia.Platform.Storage;
using DevProjex.Application.Context;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia;

public partial class MainWindow
{
    private async void OnCopyTree(object? sender, RoutedEventArgs e) =>
        await CopyProjectTextOutputAsync(
            ProjectTextOutputMode.Tree,
            metricsKind: "tree",
            toastKey: "Toast.Copy.Tree");

    private async void OnCopyContent(object? sender, RoutedEventArgs e) =>
        await CopyProjectTextOutputAsync(
            ProjectTextOutputMode.Content,
            metricsKind: "content",
            toastKey: "Toast.Copy.Content");

    private async void OnCopyTreeAndContent(object? sender, RoutedEventArgs e) =>
        await CopyProjectTextOutputAsync(
            ProjectTextOutputMode.TreeAndContent,
            metricsKind: "tree-content",
            toastKey: "Toast.Copy.TreeAndContent");

    private async void OnExportTreeToFile(object? sender, RoutedEventArgs e)
    {
        var format = GetCurrentTreeTextFormat();
        await ExportProjectTextOutputAsync(
            ProjectTextOutputMode.Tree,
            metricsKind: "tree",
            toastKey: "Toast.Export.Tree",
            suggestedFileName: BuildSuggestedExportFileName("tree", GetTreeExportFileExtension(format)),
            dialogTitle: _viewModel.MenuFileExportTree,
            defaultExtension: GetTreeExportFileExtension(format),
            fileTypeChoices: CreateTreeExportFileTypeChoices(format));
    }

    private async void OnExportContentToFile(object? sender, RoutedEventArgs e) =>
        await ExportProjectTextOutputAsync(
            ProjectTextOutputMode.Content,
            metricsKind: "content",
            toastKey: "Toast.Export.Content",
            suggestedFileName: BuildSuggestedExportFileName("content", "txt"),
            dialogTitle: _viewModel.MenuFileExportContent,
            defaultExtension: "txt",
            fileTypeChoices: [CreateTextFileType()]);

    private async void OnExportTreeAndContentToFile(object? sender, RoutedEventArgs e) =>
        await ExportProjectTextOutputAsync(
            ProjectTextOutputMode.TreeAndContent,
            metricsKind: "tree-content",
            toastKey: "Toast.Export.TreeAndContent",
            suggestedFileName: BuildSuggestedExportFileName("tree_content", "txt"),
            dialogTitle: _viewModel.MenuFileExportTreeAndContent,
            defaultExtension: "txt",
            fileTypeChoices: [CreateTextFileType()]);

    private async Task CopyProjectTextOutputAsync(
        ProjectTextOutputMode mode,
        string metricsKind,
        string toastKey)
    {
        try
        {
            if (!EnsureTreeReady() || !EnsureTrackedGitOutputReady())
                return;

            var snapshot = CaptureProjectTextOutputSnapshot();
            var result = await PrepareProjectTextOutputAsync(mode, snapshot);
            if (!await EnsureProjectTextOutputAvailableAsync(mode, snapshot, result))
                return;

            await SetClipboardTextAsync(result.Content);
            _sessionMetrics.RecordClipboard(
                metricsKind,
                GetMetricsTreeFormat(mode, snapshot),
                result.Content.Length,
                success: true);
            _toastService.Show(_localization[toastKey]);
        }
        catch (OperationCanceledException) when (_windowLifetimeCts is null)
        {
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ResolveUserFacingOutputErrorMessage(ex));
        }
    }

    private async Task ExportProjectTextOutputAsync(
        ProjectTextOutputMode mode,
        string metricsKind,
        string toastKey,
        string suggestedFileName,
        string dialogTitle,
        string defaultExtension,
        IReadOnlyList<FilePickerFileType> fileTypeChoices)
    {
        try
        {
            if (!EnsureTreeReady() || !EnsureTrackedGitOutputReady())
                return;

            var snapshot = CaptureProjectTextOutputSnapshot();
            using var result = await PrepareProjectTextDocumentOutputAsync(mode, snapshot);
            if (!await EnsureProjectTextOutputAvailableAsync(mode, snapshot, result))
                return;

            var saved = await TryExportTextToFileAsync(
				result.Document,
                snapshot.RootPath,
                suggestedFileName,
                dialogTitle,
                defaultExtension,
                fileTypeChoices);
            if (!saved)
                return;

            _sessionMetrics.RecordFileExport(
                metricsKind,
                GetMetricsTreeFormat(mode, snapshot),
				(int)Math.Min(int.MaxValue, result.Document.CharacterCount),
                success: true);
            _toastService.Show(_localization[toastKey]);
        }
        catch (OperationCanceledException) when (_windowLifetimeCts is null)
        {
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ResolveUserFacingOutputErrorMessage(ex));
        }
    }

    private ProjectTextOutputSnapshot CaptureProjectTextOutputSnapshot() =>
        new(
            _currentPath!,
            _currentTree!.Root,
            GetCheckedPaths(),
            _currentTree.OrderedFilePaths,
            GetCurrentTreeTextFormat(),
			CreateExportPathPresentation(),
			CreateContentTransformationContext());

    private bool EnsureTrackedGitOutputReady()
    {
        if (string.IsNullOrWhiteSpace(_currentPath))
            return true;

        var diagnostic = _selectionCoordinator.GetAppliedGitReadinessDiagnostic(_currentPath);
        if (diagnostic is null)
            return true;

        var localizationKey = diagnostic.Code switch
        {
            ProjectContextGitReadiness.PartialDiagnosticCode =>
                "Terminal.Diagnostic.TrackedIndexPartial",
            _ => "Terminal.Diagnostic.TrackedIndexUnavailable"
        };
        _toastService.Show(_localization[localizationKey]);
        return diagnostic.Severity != ContextDiagnosticSeverity.Error;
    }

    private async Task<ProjectTextOutputResult> PrepareProjectTextOutputAsync(
        ProjectTextOutputMode mode,
        ProjectTextOutputSnapshot snapshot)
    {
        _metrics.CancelBackgroundCalculation();
        var statusOperationId = BeginOutputPreparationStatus();
        try
        {
            var cancellationToken = _windowLifetimeCts?.Token ?? CancellationToken.None;
            return await _textOutputPipeline.BuildAsync(mode, snapshot, cancellationToken);
        }
        finally
        {
            CompleteStatusOperation(ref statusOperationId);
        }
    }

	private async Task<ProjectTextDocumentOutputResult> PrepareProjectTextDocumentOutputAsync(
		ProjectTextOutputMode mode,
		ProjectTextOutputSnapshot snapshot)
	{
		_metrics.CancelBackgroundCalculation();
		var statusOperationId = BeginOutputPreparationStatus();
		try
		{
			var cancellationToken = _windowLifetimeCts?.Token ?? CancellationToken.None;
			return await _textOutputPipeline.BuildDocumentAsync(mode, snapshot, cancellationToken);
		}
		finally
		{
			CompleteStatusOperation(ref statusOperationId);
		}
	}

    private async Task<bool> EnsureProjectTextOutputAvailableAsync(
        ProjectTextOutputMode mode,
        ProjectTextOutputSnapshot snapshot,
        ProjectTextOutputResult result)
		=> await EnsureProjectTextOutputAvailableAsync(
			mode,
			snapshot,
			result.CandidateFileCount,
			!string.IsNullOrWhiteSpace(result.Content));

	private async Task<bool> EnsureProjectTextOutputAvailableAsync(
		ProjectTextOutputMode mode,
		ProjectTextOutputSnapshot snapshot,
		ProjectTextDocumentOutputResult result) =>
		await EnsureProjectTextOutputAvailableAsync(
			mode,
			snapshot,
			result.CandidateFileCount,
			result.Document.CharacterCount > 0);

	private async Task<bool> EnsureProjectTextOutputAvailableAsync(
		ProjectTextOutputMode mode,
		ProjectTextOutputSnapshot snapshot,
		int candidateFileCount,
		bool hasContent)
    {
        if (mode != ProjectTextOutputMode.Content)
            return true;

        if (candidateFileCount == 0)
        {
            var messageKey = snapshot.SelectedPaths.Count > 0
                ? "Msg.NoCheckedFiles"
                : "Msg.NoTextContent";
            await ShowInfoAsync(_localization[messageKey]);
            return false;
        }

		if (hasContent)
            return true;

        await ShowInfoAsync(_localization["Msg.NoTextContent"]);
        return false;
    }

    private static TreeTextFormat? GetMetricsTreeFormat(
        ProjectTextOutputMode mode,
        ProjectTextOutputSnapshot snapshot) =>
        mode == ProjectTextOutputMode.Content ? null : snapshot.TreeFormat;

    private TreeTextFormat GetCurrentTreeTextFormat()
        => _viewModel.SelectedExportFormat switch
        {
            ExportFormat.Json => TreeTextFormat.Json,
            ExportFormat.Xml => TreeTextFormat.Xml,
            ExportFormat.Markdown => TreeTextFormat.Markdown,
            _ => TreeTextFormat.Ascii
        };

    private ExportPathPresentation? CreateExportPathPresentation()
    {
        if (!_viewModel.IsGitMode)
        {
            _cachedPathPresentation = null;
            _cachedPathPresentationProjectPath = null;
            _cachedPathPresentationRepositoryUrl = null;
            return null;
        }

        if (string.IsNullOrWhiteSpace(_currentPath) || string.IsNullOrWhiteSpace(_currentRepositoryUrl))
        {
            _cachedPathPresentation = null;
            _cachedPathPresentationProjectPath = null;
            _cachedPathPresentationRepositoryUrl = null;
            return null;
        }

        if (_cachedPathPresentation is not null &&
            string.Equals(_cachedPathPresentationProjectPath, _currentPath, StringComparison.Ordinal) &&
            string.Equals(_cachedPathPresentationRepositoryUrl, _currentRepositoryUrl, StringComparison.Ordinal))
        {
            return _cachedPathPresentation;
        }

        _cachedPathPresentation = _repositoryWebPathPresentationService.TryCreate(_currentPath, _currentRepositoryUrl);
        _cachedPathPresentationProjectPath = _currentPath;
        _cachedPathPresentationRepositoryUrl = _currentRepositoryUrl;

        return _cachedPathPresentation;
    }

    private async Task<bool> TryExportTextToFileAsync(
		IPreviewTextDocument document,
        string sourceRootPath,
        string suggestedFileName,
        string dialogTitle,
        string defaultExtension,
        IReadOnlyList<FilePickerFileType> fileTypeChoices)
    {
		if (StorageProvider is null || document.CharacterCount == 0)
            return false;

        var windowLifetime = _windowLifetimeCts;
        if (windowLifetime is null)
            return false;
        var cancellationToken = windowLifetime.Token;

        var options = new FilePickerSaveOptions
        {
            Title = dialogTitle,
            SuggestedFileName = suggestedFileName,
            ShowOverwritePrompt = true,
            DefaultExtension = defaultExtension,
            FileTypeChoices = fileTypeChoices
        };

        var file = await StorageProvider.SaveFilePickerAsync(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (file is null)
            return false;

        var destinationPath = file.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(destinationPath) ||
            string.IsNullOrWhiteSpace(sourceRootPath))
        {
            // An opaque provider handle cannot be proven distinct from a loaded source file.
            _toastService.Show(_localization["Error.ProjectCopy.UnsafeDestinationPath"]);
            return false;
        }

        try
        {
            // Text and physical exports share the same canonical guard so an aliased path
            // cannot bypass the read-only boundary of the loaded project.
            _ = await Task.Run(
                () => ProjectCopyExportService.ResolveDestinationOutsideProject(
                    sourceRootPath,
                    destinationPath),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is
                   ProjectCopyExportException or
                   UnauthorizedAccessException or
                   IOException)
        {
            _toastService.Show(_localization[
                ProjectCopyExportErrorPresentation.ResolveLocalizationKey(exception)]);
            return false;
        }

        try
        {
            await Task.Run(
                () => AtomicFileOutput.WriteAsync(
                    Path.GetFullPath(destinationPath),
                    overwrite: true,
                    (stream, writeCancellationToken) =>
                        _textFileExport.WriteAsync(
                            stream,
							document,
                            writeCancellationToken),
                    cancellationToken,
                    path => ProjectCopyExportService.ResolveDestinationOutsideProject(
                        sourceRootPath,
                        path)),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is
                   ProjectCopyExportException or
                   UnauthorizedAccessException or
                   IOException)
        {
            _toastService.Show(_localization[
                ProjectCopyExportErrorPresentation.ResolveLocalizationKey(exception)]);
            return false;
        }

        return true;
    }

    private static IReadOnlyList<FilePickerFileType> CreateTreeExportFileTypeChoices(TreeTextFormat format)
    {
        if (format == TreeTextFormat.Ascii)
            return [CreateTextFileType()];

        var nativeFileType = format switch
        {
            TreeTextFormat.Json => CreateJsonFileType(),
            TreeTextFormat.Xml => CreateXmlFileType(),
            TreeTextFormat.Markdown => CreateMarkdownFileType(),
            _ => CreateTextFileType()
        };

        // Structured tree text is also useful as a generic text artifact. Keep the native
        // extension first while offering TXT as an explicit, semantically honest fallback.
        return [nativeFileType, CreateTextFileType()];
    }

    private static FilePickerFileType CreateTextFileType()
        => new("TXT")
        {
            Patterns = ["*.txt"],
            MimeTypes = ["text/plain"]
        };

    private static FilePickerFileType CreateJsonFileType()
        => new("JSON")
        {
            Patterns = ["*.json"],
            MimeTypes = ["application/json"]
        };

    private static FilePickerFileType CreateXmlFileType()
        => new("XML")
        {
            Patterns = ["*.xml"],
            MimeTypes = ["application/xml", "text/xml"]
        };

    private static FilePickerFileType CreateMarkdownFileType()
        => new("Markdown")
        {
            Patterns = ["*.md"],
            MimeTypes = ["text/markdown", "text/plain"]
        };

    private static string GetTreeExportFileExtension(TreeTextFormat format)
        => format switch
        {
            TreeTextFormat.Json => "json",
            TreeTextFormat.Xml => "xml",
            TreeTextFormat.Markdown => "md",
            _ => "txt"
        };

    private void CompleteStatusOperation(ref long? operationId)
    {
        if (!operationId.HasValue)
            return;

        _statusOperations.Complete(operationId.Value);
        operationId = null;
    }

    private long? BeginOutputPreparationStatus()
    {
        // Clipboard and text-file exports are safe against the captured project tree, but
        // they must not replace the progress or cancellation action of a physical export.
        if (_viewModel.IsProjectCopyExportInProgress)
            return null;

        return _statusOperations.Begin(
            _localization["Status.Operation.PreparingOutput"],
            indeterminate: true,
            presentation: StatusOperationPresentation.Delayed);
    }

    private string BuildSuggestedExportFileName(string suffix, string extension)
    {
        var baseName = _currentProjectDisplayName;
        if (string.IsNullOrWhiteSpace(baseName) && !string.IsNullOrWhiteSpace(_currentPath))
            baseName = Path.GetFileName(_currentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "devprojex";

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new StringBuilder(baseName.Length);
        foreach (var ch in baseName)
            sanitized.Append(invalidChars.Contains(ch) ? '_' : ch);

        return $"{sanitized}_{suffix}.{extension}";
    }
}
