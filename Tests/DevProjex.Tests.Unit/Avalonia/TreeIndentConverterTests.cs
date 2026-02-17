using System.Globalization;
using Avalonia.Controls;
using DevProjex.Avalonia.Converters;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class TreeIndentConverterTests
{
    [Fact]
    public void Convert_PositiveLevel_ReturnsGridLength()
    {
        var converter = new TreeIndentConverter();

        var result = converter.Convert(3, typeof(GridLength), null, CultureInfo.InvariantCulture);

        Assert.Equal(new GridLength(48), result);
    }

    [Fact]
    public void Convert_NegativeLevel_ClampsToZero()
    {
        var converter = new TreeIndentConverter();

        var result = converter.Convert(-2, typeof(GridLength), null, CultureInfo.InvariantCulture);

        Assert.Equal(new GridLength(0), result);
    }

    [Fact]
    public void Convert_NonIntLevel_UsesZero()
    {
        var converter = new TreeIndentConverter();

        var result = converter.Convert("not-int", typeof(GridLength), null, CultureInfo.InvariantCulture);

        Assert.Equal(new GridLength(0), result);
    }

    [Fact]
    public void Convert_UsesCustomIndentSize()
    {
        var converter = new TreeIndentConverter { IndentSize = 10 };

        var result = converter.Convert(4, typeof(GridLength), null, CultureInfo.InvariantCulture);

        Assert.Equal(new GridLength(40), result);
    }

    [Fact]
    public void ConvertBack_ThrowsNotSupported()
    {
        var converter = new TreeIndentConverter();

        Assert.Throws<NotSupportedException>(
            () => converter.ConvertBack(new GridLength(0), typeof(int), null, CultureInfo.InvariantCulture));
    }
}
