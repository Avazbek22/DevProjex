namespace DevProjex.Application.Services;

public sealed class ExportPathPresentation
{
    public ExportPathPresentation(string displayRootPath, Func<string, string> mapFilePath)
    {
        DisplayRootPath = displayRootPath;
        MapFilePath = mapFilePath;
    }

    public string DisplayRootPath { get; }
    public Func<string, string> MapFilePath { get; }
}

