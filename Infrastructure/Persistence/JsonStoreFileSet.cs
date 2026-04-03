namespace DevProjex.Infrastructure.Persistence;

internal readonly record struct JsonStoreFileSet(
    string PrimaryPath,
    string BackupPath,
    string LockPath)
{
    public string DirectoryPath => Path.GetDirectoryName(PrimaryPath) ?? string.Empty;

    public string FileName => Path.GetFileName(PrimaryPath);

    public static JsonStoreFileSet Create(
        Func<string> appDataPathProvider,
        string folderName,
        string fileName)
    {
        var root = appDataPathProvider();
        var directoryPath = Path.Combine(root, folderName);
        var primaryPath = Path.Combine(directoryPath, fileName);
        return new JsonStoreFileSet(
            primaryPath,
            $"{primaryPath}.bak",
            $"{primaryPath}.lock");
    }
}
