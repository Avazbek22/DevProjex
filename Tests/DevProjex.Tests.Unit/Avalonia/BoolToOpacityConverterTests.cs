using System.Globalization;
using DevProjex.Avalonia.Converters;

namespace DevProjex.Tests.Unit.Avalonia;

/// <summary>
/// Tests for BoolToOpacityConverter used to fix TreeView virtualization expander bug.
/// The converter returns 1.0 for true (visible) and 0.0 for false (hidden but takes space).
/// </summary>
public sealed class BoolToOpacityConverterTests
{
    [Fact]
    public void Instance_IsSingleton()
    {
        var instance1 = BoolToOpacityConverter.Instance;
        var instance2 = BoolToOpacityConverter.Instance;

        Assert.Same(instance1, instance2);
    }

    [Fact]
    public void Instance_IsNotNull()
    {
        Assert.NotNull(BoolToOpacityConverter.Instance);
    }

    [Fact]
    public void Convert_True_ReturnsOne()
    {
        var converter = BoolToOpacityConverter.Instance;

        var result = converter.Convert(true, typeof(double), null, CultureInfo.InvariantCulture);

        Assert.Equal(1.0, result);
    }

    [Fact]
    public void Convert_False_ReturnsZero()
    {
        var converter = BoolToOpacityConverter.Instance;

        var result = converter.Convert(false, typeof(double), null, CultureInfo.InvariantCulture);

        Assert.Equal(0.0, result);
    }

    [Fact]
    public void Convert_BooleanTrue_ReturnsExactlyOnePointZero()
    {
        var converter = BoolToOpacityConverter.Instance;

        var result = converter.Convert(true, typeof(double), null, CultureInfo.InvariantCulture);

        Assert.IsType<double>(result);
        Assert.Equal(1.0, (double)result, precision: 10);
    }

    [Fact]
    public void Convert_BooleanFalse_ReturnsExactlyZeroPointZero()
    {
        var converter = BoolToOpacityConverter.Instance;

        var result = converter.Convert(false, typeof(double), null, CultureInfo.InvariantCulture);

        Assert.IsType<double>(result);
        Assert.Equal(0.0, (double)result, precision: 10);
    }

    [Fact]
    public void Convert_Null_ReturnsZero()
    {
        var converter = BoolToOpacityConverter.Instance;

        var result = converter.Convert(null, typeof(double), null, CultureInfo.InvariantCulture);

        Assert.Equal(0.0, result);
    }

    [Fact]
    public void Convert_NonBooleanValue_ReturnsZero()
    {
        var converter = BoolToOpacityConverter.Instance;

        var result = converter.Convert("not-a-boolean", typeof(double), null, CultureInfo.InvariantCulture);

        Assert.Equal(0.0, result);
    }

    [Fact]
    public void Convert_IntegerOne_ReturnsZero()
    {
        var converter = BoolToOpacityConverter.Instance;

        // Integer 1 is not boolean true
        var result = converter.Convert(1, typeof(double), null, CultureInfo.InvariantCulture);

        Assert.Equal(0.0, result);
    }

    [Fact]
    public void Convert_IntegerZero_ReturnsZero()
    {
        var converter = BoolToOpacityConverter.Instance;

        var result = converter.Convert(0, typeof(double), null, CultureInfo.InvariantCulture);

        Assert.Equal(0.0, result);
    }

    [Fact]
    public void Convert_BoxedTrue_ReturnsOne()
    {
        var converter = BoolToOpacityConverter.Instance;
        object boxedTrue = true;

        var result = converter.Convert(boxedTrue, typeof(double), null, CultureInfo.InvariantCulture);

        Assert.Equal(1.0, result);
    }

    [Fact]
    public void Convert_BoxedFalse_ReturnsZero()
    {
        var converter = BoolToOpacityConverter.Instance;
        object boxedFalse = false;

        var result = converter.Convert(boxedFalse, typeof(double), null, CultureInfo.InvariantCulture);

        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ConvertBack_ThrowsNotSupportedException()
    {
        var converter = BoolToOpacityConverter.Instance;

        Assert.Throws<NotSupportedException>(
            () => converter.ConvertBack(1.0, typeof(bool), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ConvertBack_ThrowsForZero()
    {
        var converter = BoolToOpacityConverter.Instance;

        Assert.Throws<NotSupportedException>(
            () => converter.ConvertBack(0.0, typeof(bool), null, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(true, 1.0)]
    [InlineData(false, 0.0)]
    public void Convert_Theory_ReturnsExpectedOpacity(bool input, double expected)
    {
        var converter = BoolToOpacityConverter.Instance;

        var result = converter.Convert(input, typeof(double), null, CultureInfo.InvariantCulture);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Convert_WithDifferentCultures_ReturnsSameResult()
    {
        var converter = BoolToOpacityConverter.Instance;

        var resultInvariant = converter.Convert(true, typeof(double), null, CultureInfo.InvariantCulture);
        var resultUs = converter.Convert(true, typeof(double), null, new CultureInfo("en-US"));
        var resultRu = converter.Convert(true, typeof(double), null, new CultureInfo("ru-RU"));

        Assert.Equal(resultInvariant, resultUs);
        Assert.Equal(resultInvariant, resultRu);
    }

    [Fact]
    public void Convert_IgnoresParameter()
    {
        var converter = BoolToOpacityConverter.Instance;

        var resultWithNull = converter.Convert(true, typeof(double), null, CultureInfo.InvariantCulture);
        var resultWithParam = converter.Convert(true, typeof(double), "some-param", CultureInfo.InvariantCulture);

        Assert.Equal(resultWithNull, resultWithParam);
    }

    [Fact]
    public void Convert_IgnoresTargetType()
    {
        var converter = BoolToOpacityConverter.Instance;

        var resultDouble = converter.Convert(true, typeof(double), null, CultureInfo.InvariantCulture);
        var resultObject = converter.Convert(true, typeof(object), null, CultureInfo.InvariantCulture);
        var resultString = converter.Convert(true, typeof(string), null, CultureInfo.InvariantCulture);

        Assert.Equal(resultDouble, resultObject);
        Assert.Equal(resultDouble, resultString);
    }

    /// <summary>
    /// Verifies the converter produces values valid for Avalonia Opacity property (0.0 to 1.0).
    /// </summary>
    [Fact]
    public void Convert_ReturnsValidOpacityRange()
    {
        var converter = BoolToOpacityConverter.Instance;

        var trueResult = (double)converter.Convert(true, typeof(double), null, CultureInfo.InvariantCulture);
        var falseResult = (double)converter.Convert(false, typeof(double), null, CultureInfo.InvariantCulture);

        Assert.InRange(trueResult, 0.0, 1.0);
        Assert.InRange(falseResult, 0.0, 1.0);
    }
}
