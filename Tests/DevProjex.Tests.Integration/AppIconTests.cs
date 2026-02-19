using System.Xml.Linq;

namespace DevProjex.Tests.Integration;

/// <summary>
/// Tests for application icon configuration and validity.
/// These tests ensure icons are properly configured for Windows, Linux, and cross-platform Avalonia.
/// </summary>
public sealed class AppIconTests
{
    private static readonly Lazy<string> RepoRoot = new(() => FindRepositoryRoot());

    #region Helper Methods

    private static string FindRepositoryRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) ||
                File.Exists(Path.Combine(dir, "*.sln")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Could not find repository root");
    }

    private static string? FindFileByPattern(string baseDir, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(baseDir, pattern, SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> FindFilesByPattern(string baseDir, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(baseDir, pattern, SearchOption.AllDirectories);
        }
        catch
        {
            return Enumerable.Empty<string>();
        }
    }

    private static string? FindAvaloniaMainProject()
    {
        var csprojFiles = FindFilesByPattern(RepoRoot.Value, "*.csproj");
        foreach (var csproj in csprojFiles)
        {
            try
            {
                var content = File.ReadAllText(csproj);
                if (content.Contains("Avalonia") && content.Contains("WinExe"))
                {
                    var dir = Path.GetDirectoryName(csproj)!;
                    if (FindFileByPattern(dir, "MainWindow.axaml") != null ||
                        FindFileByPattern(dir, "Program.cs") != null)
                    {
                        return csproj;
                    }
                }
            }
            catch { }
        }
        return null;
    }

    #endregion

    #region ICO File Structure Tests

    [Fact]
    public void WindowsIcoFile_Exists()
    {
        var icoFile = FindFileByPattern(RepoRoot.Value, "app.ico");

        Assert.NotNull(icoFile);
        Assert.True(File.Exists(icoFile), "app.ico should exist in the repository");
    }

    [Fact]
    public void WindowsIcoFile_HasValidIcoHeader()
    {
        var icoFile = FindFileByPattern(RepoRoot.Value, "app.ico");
        Assert.NotNull(icoFile);

        using var stream = File.OpenRead(icoFile);
        using var reader = new BinaryReader(stream);

        // ICO header: reserved (2 bytes) = 0, type (2 bytes) = 1, count (2 bytes) > 0
        var reserved = reader.ReadUInt16();
        var type = reader.ReadUInt16();
        var count = reader.ReadUInt16();

        Assert.Equal(0, reserved);
        Assert.Equal(1, type); // 1 = ICO, 2 = CUR
        Assert.True(count > 0, "ICO file should contain at least one image");
    }

    [Fact]
    public void WindowsIcoFile_ContainsMultipleResolutions()
    {
        var icoFile = FindFileByPattern(RepoRoot.Value, "app.ico");
        Assert.NotNull(icoFile);

        var sizes = ReadIcoSizes(icoFile);

        Assert.True(sizes.Count >= 3, "ICO should contain at least 3 different sizes");
    }

    [Fact]
    public void WindowsIcoFile_ContainsTaskbarSize()
    {
        var icoFile = FindFileByPattern(RepoRoot.Value, "app.ico");
        Assert.NotNull(icoFile);

        var sizes = ReadIcoSizes(icoFile);

        // Windows taskbar uses 32x32 or 48x48
        var hasTaskbarSize = sizes.Any(s => s is 32 or 48);
        Assert.True(hasTaskbarSize, "ICO should contain 32x32 or 48x48 for Windows taskbar");
    }

    [Fact]
    public void WindowsIcoFile_ContainsAltTabSize()
    {
        var icoFile = FindFileByPattern(RepoRoot.Value, "app.ico");
        Assert.NotNull(icoFile);

        var sizes = ReadIcoSizes(icoFile);

        // Alt+Tab uses 64x64 or larger
        var hasAltTabSize = sizes.Any(s => s >= 64);
        Assert.True(hasAltTabSize, "ICO should contain 64x64 or larger for Alt+Tab");
    }

    [Fact]
    public void WindowsIcoFile_ContainsLargeSize()
    {
        var icoFile = FindFileByPattern(RepoRoot.Value, "app.ico");
        Assert.NotNull(icoFile);

        var sizes = ReadIcoSizes(icoFile);

        // Should have 256x256 for high-DPI displays
        var hasLargeSize = sizes.Any(s => s >= 256);
        Assert.True(hasLargeSize, "ICO should contain 256x256 for high-DPI displays");
    }

    [Fact]
    public void WindowsIcoFile_AllImagesHave32BitColor()
    {
        var icoFile = FindFileByPattern(RepoRoot.Value, "app.ico");
        Assert.NotNull(icoFile);

        using var stream = File.OpenRead(icoFile);
        using var reader = new BinaryReader(stream);

        reader.ReadUInt16(); // reserved
        reader.ReadUInt16(); // type
        var count = reader.ReadUInt16();

        for (int i = 0; i < count; i++)
        {
            reader.ReadByte();  // width
            reader.ReadByte();  // height
            reader.ReadByte();  // colors
            reader.ReadByte();  // reserved
            reader.ReadUInt16(); // planes
            var bpp = reader.ReadUInt16();
            reader.ReadUInt32(); // size
            reader.ReadUInt32(); // offset

            Assert.True(bpp >= 24, $"Image {i} should have at least 24-bit color (has {bpp})");
        }
    }

    [Fact]
    public void WindowsIcoFile_HasReasonableFileSize()
    {
        var icoFile = FindFileByPattern(RepoRoot.Value, "app.ico");
        Assert.NotNull(icoFile);

        var fileInfo = new FileInfo(icoFile);

        // Multi-resolution ICO should be at least 10KB
        Assert.True(fileInfo.Length >= 10 * 1024, "ICO file seems too small for multi-resolution");
        // But not unreasonably large (< 500KB)
        Assert.True(fileInfo.Length < 500 * 1024, "ICO file seems too large");
    }

    private static List<int> ReadIcoSizes(string icoPath)
    {
        var sizes = new List<int>();
        using var stream = File.OpenRead(icoPath);
        using var reader = new BinaryReader(stream);

        reader.ReadUInt16(); // reserved
        reader.ReadUInt16(); // type
        var count = reader.ReadUInt16();

        for (int i = 0; i < count; i++)
        {
            var width = reader.ReadByte();
            reader.ReadByte();  // height
            reader.ReadByte();  // colors
            reader.ReadByte();  // reserved
            reader.ReadUInt16(); // planes
            reader.ReadUInt16(); // bpp
            reader.ReadUInt32(); // size
            reader.ReadUInt32(); // offset

            // 0 means 256
            sizes.Add(width == 0 ? 256 : width);
        }

        return sizes;
    }

    #endregion

    #region PNG Icon Tests

    [Fact]
    public void PngIconForAvaloniaWindow_Exists()
    {
        // Find PNG icons suitable for Avalonia Window.Icon (128x128 or larger)
        var pngFiles = FindFilesByPattern(Path.Combine(RepoRoot.Value, "Assets"), "*.png")
            .Where(f => !f.Contains("extracted_")) // Skip test extracts
            .ToList();

        var hasLargePng = pngFiles.Any(f =>
        {
            var name = Path.GetFileNameWithoutExtension(f);
            return int.TryParse(name, out var size) && size >= 128;
        });

        Assert.True(pngFiles.Count > 0 || hasLargePng, "Should have PNG icons for Avalonia");
    }

    [Fact]
    public void PngIcons_HaveValidPngSignature()
    {
        var pngFiles = FindFilesByPattern(Path.Combine(RepoRoot.Value, "Assets"), "*.png")
            .Where(f => f.Contains("AppIcon") && !f.Contains("extracted_"))
            .Take(5); // Check a sample

        foreach (var pngFile in pngFiles)
        {
            using var stream = File.OpenRead(pngFile);
            var header = new byte[8];
            stream.ReadExactly(header);

            // PNG signature: 89 50 4E 47 0D 0A 1A 0A
            byte[] pngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            Assert.True(header.SequenceEqual(pngSignature),
                $"File {Path.GetFileName(pngFile)} should have valid PNG signature");
        }
    }

    [Fact]
    public void MasterIconSource_Exists()
    {
        // Should have a master/source icon file
        var masterFiles = FindFilesByPattern(RepoRoot.Value, "*master*.png")
            .Concat(FindFilesByPattern(RepoRoot.Value, "*source*.png"))
            .Where(f => f.Contains("AppIcon") || f.Contains("Icon"))
            .ToList();

        Assert.True(masterFiles.Count > 0, "Should have a master source icon PNG");
    }

    #endregion

    #region Project Configuration Tests

    [Fact]
    public void AvaloniaProject_HasApplicationIconConfigured()
    {
        var csproj = FindAvaloniaMainProject();
        Assert.NotNull(csproj);

        var content = File.ReadAllText(csproj);

        // Should reference ApplicationIcon
        var hasAppIcon = content.Contains("ApplicationIcon") ||
                         content.Contains("<ApplicationIcon");

        Assert.True(hasAppIcon, "Avalonia project should have ApplicationIcon configured");
    }

    [Fact]
    public void AvaloniaProject_ApplicationIconPointsToIcoFile()
    {
        var csproj = FindAvaloniaMainProject();
        Assert.NotNull(csproj);

        var doc = XDocument.Load(csproj);
        var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

        var appIconElement = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "ApplicationIcon");

        if (appIconElement != null)
        {
            var value = appIconElement.Value;
            Assert.True(value.EndsWith(".ico", StringComparison.OrdinalIgnoreCase),
                "ApplicationIcon should point to an .ico file");
        }
    }

    [Fact]
    public void AvaloniaProject_HasWindowIconResource()
    {
        var csproj = FindAvaloniaMainProject();
        Assert.NotNull(csproj);

        var content = File.ReadAllText(csproj);

        // Should have AvaloniaResource for window icon
        var hasResource = content.Contains("AvaloniaResource") &&
                          (content.Contains(".png") || content.Contains("icon"));

        Assert.True(hasResource, "Avalonia project should have AvaloniaResource for window icon");
    }

    [Fact]
    public void MainWindow_HasIconAttribute()
    {
        var csproj = FindAvaloniaMainProject();
        Assert.NotNull(csproj);

        var projectDir = Path.GetDirectoryName(csproj)!;
        var mainWindowFile = FindFileByPattern(projectDir, "MainWindow.axaml");

        Assert.NotNull(mainWindowFile);

        var content = File.ReadAllText(mainWindowFile);

        // Should have Icon attribute
        Assert.Contains("Icon=", content);
    }

    [Fact]
    public void MainWindow_IconUsesAvaresProtocol()
    {
        var csproj = FindAvaloniaMainProject();
        Assert.NotNull(csproj);

        var projectDir = Path.GetDirectoryName(csproj)!;
        var mainWindowFile = FindFileByPattern(projectDir, "MainWindow.axaml");

        Assert.NotNull(mainWindowFile);

        var content = File.ReadAllText(mainWindowFile);

        // Icon should use avares:// protocol
        var iconMatch = Regex.Match(content, @"Icon\s*=\s*""([^""]+)""");
        Assert.True(iconMatch.Success, "MainWindow should have Icon attribute");

        var iconValue = iconMatch.Groups[1].Value;
        Assert.StartsWith("avares://", iconValue);
    }

    #endregion

    #region Cross-Platform Icon Tests

    [Fact]
    public void LinuxIcons_Exist()
    {
        var linuxPngFiles = FindFilesByPattern(RepoRoot.Value, "*.png")
            .Where(f => f.Contains("Linux", StringComparison.OrdinalIgnoreCase) &&
                        f.Contains("AppIcon", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(linuxPngFiles.Count > 0, "Should have Linux PNG icons");
    }

    [Fact]
    public void LinuxIcons_HaveStandardSizes()
    {
        var linuxPngDir = FindFilesByPattern(RepoRoot.Value, "*.png")
            .Where(f => f.Contains("Linux", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetDirectoryName(f))
            .FirstOrDefault();

        if (linuxPngDir == null) return;

        var pngFiles = Directory.GetFiles(linuxPngDir, "*.png");
        var sizes = pngFiles
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Where(n => int.TryParse(n, out _))
            .Select(int.Parse)
            .ToList();

        // Linux hicolor theme standard sizes
        var standardSizes = new[] { 16, 22, 24, 32, 48, 64, 128, 256, 512 };
        var hasStandardSizes = sizes.Any(s => standardSizes.Contains(s));

        Assert.True(hasStandardSizes, "Linux icons should include standard hicolor sizes");
    }

    [Fact]
    public void MacOSIcons_Exist()
    {
        var macFiles = FindFilesByPattern(RepoRoot.Value, "*.png")
            .Where(f => f.Contains("MacOS", StringComparison.OrdinalIgnoreCase) &&
                        f.Contains("AppIcon", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(macFiles.Count > 0, "Should have macOS PNG icons");
    }

    [Fact]
    public void DesktopEntryFile_ExistsForLinux()
    {
        var desktopFile = FindFileByPattern(RepoRoot.Value, "*.desktop");

        Assert.NotNull(desktopFile);
        Assert.True(File.Exists(desktopFile), "Should have .desktop file for Linux");
    }

    [Fact]
    public void DesktopEntryFile_HasIconEntry()
    {
        var desktopFile = FindFileByPattern(RepoRoot.Value, "*.desktop");
        if (desktopFile == null) return;

        var content = File.ReadAllText(desktopFile);

        Assert.Contains("Icon=", content);
    }

    #endregion

    #region Icon Generation Script Tests

    [Fact]
    public void IconGenerationScript_Exists()
    {
        var scripts = FindFilesByPattern(RepoRoot.Value, "generate-app-ico.*")
            .Where(f => f.EndsWith(".py") || f.EndsWith(".ps1") || f.EndsWith(".sh"))
            .ToList();

        Assert.True(scripts.Count > 0, "Should have icon generation script");
    }

    [Fact]
    public void IconGenerationScript_GeneratesRequiredSizes()
    {
        var script = FindFileByPattern(RepoRoot.Value, "generate-app-ico.py");
        if (script == null)
            script = FindFileByPattern(RepoRoot.Value, "generate-app-ico.ps1");

        Assert.NotNull(script);

        var content = File.ReadAllText(script);

        // Should generate standard Windows ICO sizes
        Assert.Contains("16", content);
        Assert.Contains("32", content);
        Assert.Contains("48", content);
        Assert.Contains("256", content);
    }

    #endregion

    #region Icon Consistency Tests

    [Fact]
    public void AllIconFormats_ExistTogether()
    {
        var hasIco = FindFileByPattern(RepoRoot.Value, "app.ico") != null;
        var hasPng = FindFilesByPattern(RepoRoot.Value, "*.png")
            .Any(f => f.Contains("AppIcon"));
        var hasMaster = FindFilesByPattern(RepoRoot.Value, "*master*.png")
            .Any(f => f.Contains("AppIcon") || f.Contains("Icon"));

        Assert.True(hasIco, "Should have ICO file");
        Assert.True(hasPng, "Should have PNG icons");
        Assert.True(hasMaster, "Should have master source icon");
    }

    [Fact]
    public void IcoFile_IsInCorrectLocation()
    {
        var icoFile = FindFileByPattern(RepoRoot.Value, "app.ico");
        Assert.NotNull(icoFile);

        // Should be in Assets/AppIcon/Windows or similar path
        var relativePath = icoFile.Replace(RepoRoot.Value, "").TrimStart(Path.DirectorySeparatorChar);

        Assert.True(
            relativePath.Contains("Assets") || relativePath.Contains("Resources"),
            "ICO file should be in Assets or Resources folder");
    }

    #endregion
}
