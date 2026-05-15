using global::Avalonia;
using global::Avalonia.Controls;
using DevProjex.Avalonia.Views;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class SettingsPanelMeasurementHelperTests
{
    [Fact]
    public void IsTransientFontMeasurementFailure_ReturnsTrue_ForSystemFontsKey()
    {
        var exception = new KeyNotFoundException("The given key 'fonts:SystemFonts' was not present in the dictionary.");

        Assert.True(SettingsPanelMeasurementHelper.IsTransientFontMeasurementFailure(exception));
    }

    [Fact]
    public void MeasureControlWidth_FallsBackToExplicitWidth_WhenTransientFontFailureOccurs()
    {
        var control = new BrokenMeasureControl
        {
            Width = 128,
            Margin = new Thickness(4, 0, 6, 0)
        };

        var width = SettingsPanelMeasurementHelper.MeasureControlWidth(control);

        Assert.Equal(138, width);
    }

    [Fact]
    public void MeasureControlWidth_DoesNotTreatLayoutBoundsAsIntrinsicWidth_WhenFallbackRuns()
    {
        var control = new ToggleBrokenMeasureControl(42)
        {
            MinWidth = 42,
            Margin = new Thickness(3, 0, 5, 0)
        };
        control.Measure(new Size(400, 24));
        control.Arrange(new Rect(0, 0, 400, 24));
        control.ThrowOnMeasure = true;

        var width = SettingsPanelMeasurementHelper.MeasureControlWidth(control);

        Assert.True(width < 400);
    }

    [Fact]
    public void MeasureControlWidth_UsesDesiredSize_WhenMeasurementSucceeds()
    {
        var control = new FixedMeasureControl(96)
        {
            Margin = new Thickness(3, 0, 5, 0)
        };

        var width = SettingsPanelMeasurementHelper.MeasureControlWidth(control);
        var expectedWidth = control.DesiredSize.Width + control.Margin.Left + control.Margin.Right;

        Assert.Equal(expectedWidth, width);
    }

    private sealed class BrokenMeasureControl : Control
    {
        protected override Size MeasureOverride(Size availableSize)
            => throw new KeyNotFoundException("The given key 'fonts:SystemFonts' was not present in the dictionary.");
    }

    private sealed class FixedMeasureControl(double width) : Control
    {
        protected override Size MeasureOverride(Size availableSize)
            => new(width, 20);
    }

    private sealed class ToggleBrokenMeasureControl(double width) : Control
    {
        public bool ThrowOnMeasure { get; set; }

        protected override Size MeasureOverride(Size availableSize)
            => ThrowOnMeasure
                ? throw new KeyNotFoundException("The given key 'fonts:SystemFonts' was not present in the dictionary.")
                : new Size(width, 24);
    }
}
