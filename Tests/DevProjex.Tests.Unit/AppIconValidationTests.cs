using System.Text.RegularExpressions;

namespace DevProjex.Tests.Unit;

/// <summary>
/// Unit tests for validating icon file formats and structures.
/// These tests can work with any ICO/PNG files, not just the app icons.
/// </summary>
public sealed class AppIconValidationTests
{
    #region ICO Format Validation

    [Theory]
    [InlineData(new byte[] { 0, 0, 1, 0, 1, 0 }, true)]  // Valid ICO header
    [InlineData(new byte[] { 0, 0, 2, 0, 1, 0 }, false)] // CUR file, not ICO
    [InlineData(new byte[] { 0, 0, 0, 0, 1, 0 }, false)] // Invalid type
    [InlineData(new byte[] { 1, 0, 1, 0, 1, 0 }, false)] // Non-zero reserved
    public void IcoHeader_ValidatesCorrectly(byte[] header, bool expectedValid)
    {
        var isValid = IsValidIcoHeader(header);
        Assert.Equal(expectedValid, isValid);
    }

    [Theory]
    [InlineData(0, 256)]   // 0 in ICO means 256
    [InlineData(16, 16)]
    [InlineData(32, 32)]
    [InlineData(48, 48)]
    [InlineData(128, 128)]
    [InlineData(255, 255)]
    public void IcoSize_ParsesCorrectly(byte rawSize, int expectedSize)
    {
        var actualSize = ParseIcoSize(rawSize);
        Assert.Equal(expectedSize, actualSize);
    }

    [Fact]
    public void IcoDirectory_RequiresMinimumFields()
    {
        // ICO directory entry is exactly 16 bytes
        var entrySize = 16;
        Assert.Equal(16, entrySize);

        // Fields: width(1), height(1), colors(1), reserved(1),
        //         planes(2), bpp(2), size(4), offset(4)
        var fieldSizes = new[] { 1, 1, 1, 1, 2, 2, 4, 4 };
        Assert.Equal(16, fieldSizes.Sum());
    }

    [Theory]
    [InlineData(1, true)]   // 1bpp monochrome
    [InlineData(4, true)]   // 16 colors
    [InlineData(8, true)]   // 256 colors
    [InlineData(24, true)]  // True color
    [InlineData(32, true)]  // True color + alpha
    [InlineData(0, false)]  // Invalid
    [InlineData(3, false)]  // Non-standard
    public void IconBitDepth_ValidatesKnownValues(int bpp, bool expectedValid)
    {
        var validBpps = new[] { 1, 4, 8, 24, 32 };
        var isValid = validBpps.Contains(bpp);
        Assert.Equal(expectedValid, isValid);
    }

    #endregion

    #region PNG Format Validation

    [Fact]
    public void PngSignature_IsCorrect()
    {
        byte[] expectedSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        // "PNG" should be at positions 1-3
        Assert.Equal((byte)'P', expectedSignature[1]);
        Assert.Equal((byte)'N', expectedSignature[2]);
        Assert.Equal((byte)'G', expectedSignature[3]);
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, true)]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0B }, false)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 }, false)] // JPEG
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x00, 0x00 }, false)] // GIF
    public void PngHeader_ValidatesCorrectly(byte[] header, bool expectedValid)
    {
        byte[] pngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var isValid = header.Length >= 8 && header.Take(8).SequenceEqual(pngSignature);
        Assert.Equal(expectedValid, isValid);
    }

    #endregion

    #region Icon Size Standards

    [Fact]
    public void WindowsStandardSizes_AreCorrect()
    {
        var windowsSizes = new[] { 16, 24, 32, 48, 64, 128, 256 };

        // All sizes should be powers of 2 (except 24 and 48)
        var powersOfTwo = windowsSizes.Where(s => s != 24 && s != 48);
        foreach (var size in powersOfTwo)
        {
            Assert.True(IsPowerOfTwo(size), $"{size} should be power of 2");
        }

        // Should include taskbar sizes
        Assert.Contains(32, windowsSizes);
        Assert.Contains(48, windowsSizes);

        // Should include high-DPI size
        Assert.Contains(256, windowsSizes);
    }

    [Fact]
    public void LinuxHicolorSizes_AreCorrect()
    {
        var hicolorSizes = new[] { 16, 22, 24, 32, 48, 64, 128, 256, 512 };

        // Must include common sizes
        Assert.Contains(48, hicolorSizes);  // Common panel size
        Assert.Contains(128, hicolorSizes); // App grid
        Assert.Contains(256, hicolorSizes); // High-DPI

        // 22 is standard for panel icons
        Assert.Contains(22, hicolorSizes);
    }

    [Fact]
    public void MacOSIconsetSizes_AreCorrect()
    {
        // macOS requires specific sizes with @2x variants
        var macSizes = new[] { 16, 32, 64, 128, 256, 512, 1024 };

        // 1024 is required for Retina displays
        Assert.Contains(1024, macSizes);

        // Each size should have a @2x variant (which is the next size up)
        Assert.Equal(macSizes[1], macSizes[0] * 2); // 32 = 16*2
    }

    #endregion

    #region Multi-Resolution ICO Validation

    [Fact]
    public void MultiResolutionIco_ShouldHaveMinimumSizes()
    {
        // A good multi-resolution ICO should have at least these
        var requiredSizes = new HashSet<int> { 16, 32, 48, 256 };

        Assert.True(requiredSizes.Count >= 4,
            "Multi-resolution ICO needs at least 4 sizes");

        // 16 for small icons
        Assert.Contains(16, requiredSizes);
        // 32/48 for taskbar
        Assert.True(requiredSizes.Contains(32) || requiredSizes.Contains(48));
        // 256 for high-DPI
        Assert.Contains(256, requiredSizes);
    }

    [Theory]
    [InlineData(new[] { 16 }, false)]                    // Too few
    [InlineData(new[] { 16, 32 }, false)]                // No large size
    [InlineData(new[] { 16, 32, 256 }, true)]            // Minimum viable
    [InlineData(new[] { 16, 24, 32, 48, 64, 128, 256 }, true)] // Complete
    public void IcoSizeSet_ValidatesCompleteness(int[] sizes, bool expectedComplete)
    {
        var isComplete = ValidateIcoSizeSet(sizes);
        Assert.Equal(expectedComplete, isComplete);
    }

    #endregion

    #region File Path Validation

    [Theory]
    [InlineData("app.ico", true)]
    [InlineData("icon.ico", true)]
    [InlineData("AppIcon.ico", true)]
    [InlineData("icon.png", false)]
    [InlineData("icon.icns", false)]
    [InlineData("icon", false)]
    public void IcoFilePath_HasCorrectExtension(string filename, bool expectedIco)
    {
        var isIco = filename.EndsWith(".ico", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expectedIco, isIco);
    }

    [Theory]
    [InlineData("avares://MyApp/Assets/icon.png", true)]
    [InlineData("avares://MyApp.Desktop/Resources/app.ico", true)]
    [InlineData("/Assets/icon.png", false)]
    [InlineData("Assets/icon.png", false)]
    [InlineData("file:///C:/icon.png", false)]
    public void AvaloniaResourceUri_HasCorrectProtocol(string uri, bool expectedValid)
    {
        var isValid = uri.StartsWith("avares://", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expectedValid, isValid);
    }

    #endregion

    #region Helper Methods

    private static bool IsValidIcoHeader(byte[] header)
    {
        if (header.Length < 6) return false;

        // Reserved must be 0
        if (header[0] != 0 || header[1] != 0) return false;

        // Type must be 1 (ICO)
        var type = BitConverter.ToUInt16(header, 2);
        if (type != 1) return false;

        // Count must be > 0
        var count = BitConverter.ToUInt16(header, 4);
        if (count == 0) return false;

        return true;
    }

    private static int ParseIcoSize(byte rawSize)
    {
        // In ICO format, 0 means 256
        return rawSize == 0 ? 256 : rawSize;
    }

    private static bool IsPowerOfTwo(int n)
    {
        return n > 0 && (n & (n - 1)) == 0;
    }

    private static bool ValidateIcoSizeSet(int[] sizes)
    {
        var set = new HashSet<int>(sizes);

        // Must have small icon (16)
        if (!set.Contains(16)) return false;

        // Must have taskbar size (32 or 48)
        if (!set.Contains(32) && !set.Contains(48)) return false;

        // Must have high-DPI size (128+)
        if (!set.Any(s => s >= 128)) return false;

        return true;
    }

    #endregion

    #region CSPROJ Configuration Validation

    [Theory]
    [InlineData("<ApplicationIcon>path/to/icon.ico</ApplicationIcon>", true)]
    [InlineData("<ApplicationIcon Condition=\"Exists('icon.ico')\">icon.ico</ApplicationIcon>", true)]
    [InlineData("<!-- <ApplicationIcon>icon.ico</ApplicationIcon> -->", false)]
    [InlineData("<Win32Icon>icon.ico</Win32Icon>", false)] // Old format
    public void CsprojApplicationIcon_ParsesCorrectly(string xml, bool expectedHasIcon)
    {
        var hasIcon = xml.Contains("<ApplicationIcon") &&
                      xml.Contains("</ApplicationIcon>") &&
                      !xml.TrimStart().StartsWith("<!--");
        Assert.Equal(expectedHasIcon, hasIcon);
    }

    [Theory]
    [InlineData("<AvaloniaResource Include=\"icon.png\" />", true)]
    [InlineData("<AvaloniaResource Include=\"Assets/icon.png\"><Link>icon.png</Link></AvaloniaResource>", true)]
    [InlineData("<Content Include=\"icon.png\" />", false)]
    [InlineData("<EmbeddedResource Include=\"icon.png\" />", false)]
    public void CsprojAvaloniaResource_ParsesCorrectly(string xml, bool expectedIsAvaloniaResource)
    {
        var isAvaloniaResource = xml.Contains("<AvaloniaResource");
        Assert.Equal(expectedIsAvaloniaResource, isAvaloniaResource);
    }

    #endregion

    #region XAML Icon Attribute Validation

    [Theory]
    [InlineData("Icon=\"avares://App/icon.png\"", true, "avares://App/icon.png")]
    [InlineData("Icon = \"avares://App/icon.png\"", true, "avares://App/icon.png")]
    [InlineData("Icon='{Binding IconPath}'", true, "{Binding IconPath}")]
    [InlineData("Title=\"My App\"", false, null)]
    public void XamlIconAttribute_ParsesCorrectly(string xaml, bool expectedHasIcon, string? expectedValue)
    {
        var match = Regex.Match(xaml, @"Icon\s*=\s*[""']([^""']+)[""']");
        var hasIcon = match.Success;
        var value = hasIcon ? match.Groups[1].Value : null;

        Assert.Equal(expectedHasIcon, hasIcon);
        Assert.Equal(expectedValue, value);
    }

    #endregion
}
