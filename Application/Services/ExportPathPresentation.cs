namespace DevProjex.Application.Services;

public sealed class ExportPathPresentation(
    string displayRootPath,
    Func<string, string> mapFilePath,
    string? displayRootName = null)
{
    public string DisplayRootPath { get; } = displayRootPath;
    public Func<string, string> MapFilePath { get; } = mapFilePath;
    public string? DisplayRootName { get; } = displayRootName;
}
