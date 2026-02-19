namespace DevProjex.Tests.Integration.Performance;

public enum PerfIgnoreProfile
{
    None,
    SmartIgnore,
    DotAndHidden
}

public sealed record FileSystemPerfCase(
    int Depth,
    int BranchFactor,
    int FilesPerDirectory,
    PerfIgnoreProfile IgnoreProfile)
{
    public override string ToString()
        => $"d{Depth}-b{BranchFactor}-f{FilesPerDirectory}-{IgnoreProfile}";
}

public sealed record DirectoryTreeDataset(
    IReadOnlyList<string> CreatedDirectories,
    IReadOnlyList<string> TextFiles,
    IReadOnlySet<string> RootFolderNames,
    IReadOnlySet<string> AllowedExtensions);

internal static class PerfDatasetBuilder
{
    private static readonly string[] SmartIgnoreFolderCycle = ["bin", "obj", "node_modules", ".git", ".idea", ".vs"];

    public static DirectoryTreeDataset CreateDirectoryTree(
        string rootPath,
        FileSystemPerfCase perfCase)
    {
        Directory.CreateDirectory(rootPath);

        var directoriesByDepth = new List<string>[perfCase.Depth + 1];
        directoriesByDepth[0] = [rootPath];

        var createdDirectories = new List<string>(capacity: 256);
        var directoryId = 0;

        for (var depth = 1; depth <= perfCase.Depth; depth++)
        {
            var currentLevel = new List<string>();
            foreach (var parentPath in directoriesByDepth[depth - 1])
            {
                for (var branch = 0; branch < perfCase.BranchFactor; branch++)
                {
                    var dirName = BuildDirectoryName(perfCase.IgnoreProfile, depth, branch, directoryId);
                    var dirPath = Path.Combine(parentPath, dirName);
                    Directory.CreateDirectory(dirPath);
                    currentLevel.Add(dirPath);
                    createdDirectories.Add(dirPath);
                    directoryId++;
                }
            }

            directoriesByDepth[depth] = currentLevel;
        }

        var allDirectories = new List<string>(1 + createdDirectories.Count) { rootPath };
        allDirectories.AddRange(createdDirectories);

        var createdTextFiles = new List<string>(allDirectories.Count * perfCase.FilesPerDirectory);
        foreach (var (directoryPath, directoryIndex) in allDirectories.Select((path, index) => (path, index)))
        {
            for (var fileIndex = 0; fileIndex < perfCase.FilesPerDirectory; fileIndex++)
            {
                var fileName = BuildFileName(perfCase.IgnoreProfile, fileIndex, directoryIndex);
                var filePath = Path.Combine(directoryPath, fileName);
                var payload = BuildFileContent(fileName, directoryIndex, fileIndex);

                if (filePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    // Binary marker with null byte to mimic real image payload.
                    File.WriteAllBytes(filePath, [0x89, 0x50, 0x4E, 0x47, 0x00, 0x0D, 0x0A, 0x1A]);
                }
                else
                {
                    File.WriteAllText(filePath, payload);
                    createdTextFiles.Add(filePath);
                }
            }
        }

        var rootFolders = Directory.GetDirectories(rootPath)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs",
            ".json",
            ".md",
            ".txt",
            ".yml",
            ".yaml",
            ".xml",
            ".png"
        };

        return new DirectoryTreeDataset(
            CreatedDirectories: createdDirectories,
            TextFiles: createdTextFiles,
            RootFolderNames: rootFolders,
            AllowedExtensions: allowedExtensions);
    }

    public static IgnoreRules BuildIgnoreRules(PerfIgnoreProfile profile)
    {
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var smartFolders = profile == PerfIgnoreProfile.SmartIgnore
            ? new HashSet<string>(SmartIgnoreFolderCycle, StringComparer.OrdinalIgnoreCase)
            : empty;

        var smartFiles = profile == PerfIgnoreProfile.SmartIgnore
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Thumbs.db", ".DS_Store" }
            : empty;

        return new IgnoreRules(
            IgnoreHiddenFolders: profile == PerfIgnoreProfile.DotAndHidden,
            IgnoreHiddenFiles: profile == PerfIgnoreProfile.DotAndHidden,
            IgnoreDotFolders: profile == PerfIgnoreProfile.DotAndHidden,
            IgnoreDotFiles: profile == PerfIgnoreProfile.DotAndHidden,
            SmartIgnoredFolders: smartFolders,
            SmartIgnoredFiles: smartFiles)
        {
            UseGitIgnore = false,
            UseSmartIgnore = profile == PerfIgnoreProfile.SmartIgnore,
            IgnoreExtensionlessFiles = false
        };
    }

    private static string BuildDirectoryName(PerfIgnoreProfile profile, int depth, int branch, int directoryId)
    {
        if (profile == PerfIgnoreProfile.SmartIgnore && depth == 1 && branch < SmartIgnoreFolderCycle.Length)
            return SmartIgnoreFolderCycle[branch];

        if (profile == PerfIgnoreProfile.DotAndHidden && depth == 1 && branch == 0)
            return $".cache_{directoryId:D4}";

        return $"dir_{depth:D2}_{branch:D2}_{directoryId:D4}";
    }

    private static string BuildFileName(PerfIgnoreProfile profile, int fileIndex, int directoryIndex)
    {
        if (profile == PerfIgnoreProfile.DotAndHidden && fileIndex == 0)
            return $".env_{directoryIndex:D4}.txt";

        var slot = fileIndex % 8;
        if (slot == 0) return $"model_{directoryIndex:D4}_{fileIndex:D2}.cs";
        if (slot == 1) return $"config_{directoryIndex:D4}_{fileIndex:D2}.json";
        if (slot == 2) return $"readme_{directoryIndex:D4}_{fileIndex:D2}.md";
        if (slot == 3) return $"notes_{directoryIndex:D4}_{fileIndex:D2}.txt";
        if (slot == 4) return $"pipeline_{directoryIndex:D4}_{fileIndex:D2}.yml";
        if (slot == 5) return $"schema_{directoryIndex:D4}_{fileIndex:D2}.xml";
        if (slot == 6) return $"icon_{directoryIndex:D4}_{fileIndex:D2}.png";

        return fileIndex % 2 == 0 ? "Dockerfile" : "LICENSE";
    }

    private static string BuildFileContent(string fileName, int directoryIndex, int fileIndex)
    {
        return $"# {fileName}{Environment.NewLine}" +
               $"Directory={directoryIndex}{Environment.NewLine}" +
               $"File={fileIndex}{Environment.NewLine}" +
               "The quick brown fox jumps over the lazy dog.";
    }
}
