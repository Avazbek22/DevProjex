namespace DevProjex.Avalonia.ViewModels;

public sealed partial class MainWindowViewModel
{
    private string _flatpakGitVersion = string.Empty;
    private string _flatpakDataPath = string.Empty;

    public void SetFlatpakDiagnostics(string gitVersion, string dataPath)
    {
        _flatpakGitVersion = gitVersion;
        _flatpakDataPath = dataPath;
        UpdateLocalization();
    }
}
