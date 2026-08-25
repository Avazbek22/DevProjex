namespace DevProjex.Avalonia.Services;

public static class ProjectCopyExportErrorPresentation
{
    public static string ResolveLocalizationKey(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            ProjectCopyExportException exportException => ResolveLocalizationKey(exportException.Error),
            UnauthorizedAccessException => "Error.ProjectCopy.AccessDenied",
            IOException => "Error.ProjectCopy.IoFailure",
            _ => "Error.ProjectCopy.UnexpectedFailure"
        };
    }

    public static string ResolveLocalizationKey(ProjectCopyExportError error) => error switch
    {
        ProjectCopyExportError.InvalidRequest => "Error.ProjectCopy.InvalidRequest",
        ProjectCopyExportError.DestinationInsideSource => "Error.ProjectCopy.DestinationInsideSource",
        ProjectCopyExportError.UnsafeSourcePath => "Error.ProjectCopy.UnsafeSourcePath",
        ProjectCopyExportError.SymbolicLinkNotSupported => "Error.ProjectCopy.SymbolicLinkNotSupported",
        ProjectCopyExportError.DestinationUnavailable => "Error.ProjectCopy.DestinationUnavailable",
        ProjectCopyExportError.SourceUnavailable => "Error.ProjectCopy.SourceUnavailable",
        ProjectCopyExportError.AccessDenied => "Error.ProjectCopy.AccessDenied",
        ProjectCopyExportError.IoFailure => "Error.ProjectCopy.IoFailure",
        ProjectCopyExportError.UnsafeDestinationPath => "Error.ProjectCopy.UnsafeDestinationPath",
        ProjectCopyExportError.DestinationConflict => "Error.ProjectCopy.DestinationConflict",
		ProjectCopyExportError.SecretDetectionFailed => "Error.ProjectCopy.SecretDetectionFailed",
		ProjectCopyExportError.SecretScanLimitExceeded => "Error.ProjectCopy.SecretScanLimitExceeded",
		ProjectCopyExportError.ReservedNoticeNameConflict => "Error.ProjectCopy.ReservedNoticeNameConflict",
        ProjectCopyExportError.UnexpectedFailure => "Error.ProjectCopy.UnexpectedFailure",
        _ => "Error.ProjectCopy.UnexpectedFailure"
    };
}
