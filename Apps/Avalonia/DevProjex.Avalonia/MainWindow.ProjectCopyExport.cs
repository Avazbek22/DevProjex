using Avalonia.Platform.Storage;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.Services;
using DevProjex.Kernel;

namespace DevProjex.Avalonia;

public partial class MainWindow
{
    private async void OnExportProjectCopyToFolder(object? sender, RoutedEventArgs e)
    {
        if (!EnsureTreeReady() || StorageProvider is null || !StorageProvider.CanPickFolder)
            return;

        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = _localization["Picker.ProjectCopy.Folder"],
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
        if (!EnsureTreeReady() || StorageProvider is null || !StorageProvider.CanSave)
            return;

        try
        {
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
        var selectedPaths = new HashSet<string>(GetCheckedPaths(), PathComparer.Default);
        var request = new ProjectCopyExportRequest(
            _currentPath,
            GetProjectCopyName(),
            _currentTree.Root,
            selectedPaths,
            destinationPath,
            format);
        var cancellation = new CancellationTokenSource();
        _projectCopyExportCts = cancellation;
        long? operationId = _statusOperations.Begin(
            _localization["Status.Operation.ExportingProjectCopy"],
            indeterminate: false,
            operationType: StatusOperationType.ProjectCopyExport,
            cancelAction: cancellation.Cancel);
        var progress = new Progress<ProjectCopyExportProgress>(value =>
            _statusOperations.UpdateProgress(
                value.Percentage,
                string.Format(
                    CultureInfo.CurrentCulture,
                    _localization["Status.Operation.ExportingProjectCopy.Progress"],
                    value.ProcessedFileCount,
                    value.TotalFileCount),
                operationId));

        try
        {
            var result = await _projectCopyExport.ExportAsync(request, progress, cancellation.Token);
            CompleteStatusOperation(ref operationId);
            var toastKey = format == ProjectCopyExportFormat.Folder
                ? "Toast.ProjectCopy.Folder"
                : "Toast.ProjectCopy.Zip";
            _toastService.Show(string.Format(CultureInfo.CurrentCulture, _localization[toastKey], result.DestinationPath));
        }
        catch (OperationCanceledException)
        {
            CompleteStatusOperation(ref operationId);
            _toastService.Show(_localization["Toast.ProjectCopy.Canceled"]);
        }
        catch (Exception exception)
        {
            CompleteStatusOperation(ref operationId);
            ShowProjectCopyExportError(exception);
        }
        finally
        {
            DisposeIfCurrent(ref _projectCopyExportCts, cancellation);
        }
    }

    private string GetProjectCopyName()
    {
        var projectName = _currentProjectDisplayName;
        if (string.IsNullOrWhiteSpace(projectName) && !string.IsNullOrWhiteSpace(_currentPath))
            projectName = Path.GetFileName(_currentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return ProjectCopyExportPlanBuilder.NormalizeProjectName(projectName ?? string.Empty, _currentPath ?? string.Empty);
    }

    private void ShowProjectCopyExportError(Exception exception)
    {
        var localizationKey = ProjectCopyExportErrorPresentation.ResolveLocalizationKey(exception);
        ShowProjectCopyExportError(_localization[localizationKey]);
    }

    private void ShowProjectCopyExportError(string message) => _toastService.Show(message);
}
