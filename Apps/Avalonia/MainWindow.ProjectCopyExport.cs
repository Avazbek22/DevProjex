using Avalonia.Platform.Storage;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia;

public partial class MainWindow
{
    private static readonly TimeSpan ProjectCopyResultToastDuration = TimeSpan.FromSeconds(3.5);

    private async void OnExportProjectCopyToFolder(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanExportProjectCopy ||
            !EnsureTreeReady() ||
            !EnsureTrackedGitOutputReady() ||
            StorageProvider is null ||
            !StorageProvider.CanPickFolder)
            return;

        try
        {
			if (!await ConfirmRedactedProjectCopyAsync())
				return;

            var folderName = $"{GetProjectCopyName()}-copy";
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = _localization.Format("Picker.ProjectCopy.Folder", folderName),
                SuggestedFileName = folderName,
                AllowMultiple = false
            });
            var destinationParent = folders.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(destinationParent))
            {
                if (folders.Count > 0)
                    ShowProjectCopyExportError(_localization["Error.ProjectCopy.LocalDestinationRequired"]);
                return;
            }

            await ExportProjectCopyAsync(ProjectCopyExportFormat.Folder, destinationParent);
        }
        catch (Exception exception)
        {
            ShowProjectCopyExportError(exception);
        }
    }

    private async void OnExportProjectCopyToZip(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanExportProjectCopy ||
            !EnsureTreeReady() ||
            !EnsureTrackedGitOutputReady() ||
            StorageProvider is null ||
            !StorageProvider.CanSave)
            return;

        try
        {
			if (!await ConfirmRedactedProjectCopyAsync())
				return;

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = _localization["Picker.ProjectCopy.Zip"],
                SuggestedFileName = $"{GetProjectCopyName()}-copy.zip",
                DefaultExtension = "zip",
                ShowOverwritePrompt = true,
                FileTypeChoices =
                [
                    new FilePickerFileType("ZIP")
                    {
                        Patterns = ["*.zip"],
                        MimeTypes = ["application/zip"]
                    }
                ]
            });
            if (file is null)
                return;

            var destinationPath = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                ShowProjectCopyExportError(_localization["Error.ProjectCopy.LocalDestinationRequired"]);
                return;
            }

            if (!destinationPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                destinationPath += ".zip";

            await ExportProjectCopyAsync(ProjectCopyExportFormat.Zip, destinationPath);
        }
        catch (Exception exception)
        {
            ShowProjectCopyExportError(exception);
        }
    }

    private async Task ExportProjectCopyAsync(ProjectCopyExportFormat format, string destinationPath)
    {
        if (_currentTree is null || string.IsNullOrWhiteSpace(_currentPath) || _projectCopyExportCts is not null)
            return;

        _metrics.CancelBackgroundCalculation();
        CancelPreviewRefresh();
        var selectedPaths = new HashSet<string>(GetCheckedPaths(), PathComparer.Default);
        var request = new ProjectCopyExportRequest(
            _currentPath,
            GetProjectCopyName(),
            _currentTree.Root,
            selectedPaths,
            destinationPath,
			format,
			RedactSecrets: CreateSecretRedactionContext() is not null,
			CompressCode: _appliedCompressCodeEnabled,
			StripComments: _appliedStripCommentsEnabled,
			NoticeText: ProjectCopyExportService.BuildProjectCopyNoticeText(_localization));
        var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _projectCopyExportCts = cancellation;
        _projectCopyExportCompletion = completion;
        _viewModel.IsProjectCopyExportInProgress = true;
        _searchFilterController.CancelPending();
        long? operationId = null;

        try
        {
			operationId = _statusOperations.Begin(
				_localization["Status.Operation.ExportingProjectCopy"],
				// Secret inspection has no honest item total. The first measured copy
				// progress event switches this operation to determinate automatically.
				indeterminate: request.RedactSecrets,
                operationType: StatusOperationType.ProjectCopyExport,
                cancelAction: cancellation.Cancel);
            var progress = new Progress<ProjectCopyExportProgress>(value =>
                _statusOperations.UpdateProgress(
                    value.Percentage,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        _localization["Status.Operation.ExportingProjectCopy.Progress"],
                        value.ProcessedEntryCount,
                        value.TotalEntryCount),
                    operationId));
            // Planning and source-path validation are synchronous and scale with the tree.
            // Offloading the complete operation lets Avalonia render the locked controls immediately.
            var result = await Task.Run(
                () => _projectCopyExport.ExportAsync(request, progress, cancellation.Token),
                cancellation.Token);
            CompleteStatusOperation(ref operationId);
            var toastKey = format == ProjectCopyExportFormat.Folder
                ? "Toast.ProjectCopy.Folder"
                : "Toast.ProjectCopy.Zip";
            _toastService.Show(
                string.Format(
                    CultureInfo.CurrentCulture,
                    _localization[toastKey],
                    AddPathWrapOpportunities(result.DestinationPath)),
                ProjectCopyResultToastDuration);
        }
        catch (OperationCanceledException)
        {
            CompleteStatusOperation(ref operationId);
            if (!_projectCopyExportClosePending)
                _toastService.Show(_localization["Toast.ProjectCopy.Canceled"]);
        }
        catch (Exception exception)
        {
            CompleteStatusOperation(ref operationId);
            if (!_projectCopyExportClosePending)
                ShowProjectCopyExportError(exception);
        }
        finally
        {
            _viewModel.IsProjectCopyExportInProgress = false;
            DisposeIfCurrent(ref _projectCopyExportCts, cancellation);
            if (ReferenceEquals(_projectCopyExportCompletion, completion))
                _projectCopyExportCompletion = null;
            completion.TrySetResult(true);
        }
    }

	private async Task<bool> ConfirmRedactedProjectCopyAsync()
	{
		var context = CreateContentTransformationContext();
		if (context is null)
			return true;

		// Both transformations make the copy something other than the project, and the user is told
		// about each one that is actually enabled rather than about the pair.
		var reasons = new List<string>(2);
		if (context.HasRedaction)
			reasons.Add(_localization["Dialog.ProjectCopy.Redaction.Message"]);
		if (context.HasCompression)
			reasons.Add(_localization["Compression.CopyNotice"]);

		return await MessageDialog.ShowConfirmationAsync(
			this,
			_localization["Dialog.ProjectCopy.Redaction.Title"],
			string.Join(Environment.NewLine + Environment.NewLine, reasons),
			_localization["Dialog.ProjectCopy.Redaction.Continue"],
			_localization["Dialog.Cancel"],
			height: reasons.Count > 1 ? 300 : 230);
	}

    private string GetProjectCopyName()
    {
        var projectName = _currentProjectDisplayName;
        if (string.IsNullOrWhiteSpace(projectName) && !string.IsNullOrWhiteSpace(_currentPath))
            projectName = Path.GetFileName(_currentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return ProjectCopyExportPlanBuilder.NormalizeProjectName(projectName ?? string.Empty, _currentPath ?? string.Empty);
    }

    private static string AddPathWrapOpportunities(string path) =>
        path.Replace("\\", "\\\u200B", StringComparison.Ordinal)
            .Replace("/", "/\u200B", StringComparison.Ordinal);

    private void ShowProjectCopyExportError(Exception exception)
    {
        var localizationKey = ProjectCopyExportErrorPresentation.ResolveLocalizationKey(exception);
        ShowProjectCopyExportError(_localization[localizationKey]);
    }

    private void ShowProjectCopyExportError(string message) => _toastService.Show(message);
}
