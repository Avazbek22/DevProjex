namespace DevProjex.Avalonia.Views;

internal static class SettingsPanelMeasurementHelper
{
    private static readonly Size InfiniteMeasureSize = new(double.PositiveInfinity, double.PositiveInfinity);

    public static double MeasureControlWidth(Control? control)
    {
        if (control is null)
            return 0;

        try
        {
            control.Measure(InfiniteMeasureSize);
            return control.DesiredSize.Width + control.Margin.Left + control.Margin.Right;
        }
        catch (Exception ex) when (IsTransientFontMeasurementFailure(ex))
        {
            // Headless test sessions can hit a short-lived font initialization gap while the
            // fluent theme and text infrastructure are still warming up. Falling back to the
            // current layout hints keeps the settings pane measurable without poisoning the
            // whole UI test session with a startup-only font exception.
            return GetFallbackWidth(control);
        }
    }

    internal static bool IsTransientFontMeasurementFailure(Exception exception)
        => exception is KeyNotFoundException keyNotFoundException &&
           keyNotFoundException.Message.Contains("fonts:SystemFonts", StringComparison.Ordinal);

    private static double GetFallbackWidth(Control control)
    {
        var explicitWidth = !double.IsNaN(control.Width) && control.Width > 0
            ? control.Width
            : 0;
        var desiredWidth = control.DesiredSize.Width > 0
            ? control.DesiredSize.Width
            : 0;
        // Bounds.Width is layout output, not intrinsic content width. Using it here can feed a
        // stretched parent width back into the panel minimum and permanently lock the splitter.
        var baseWidth = Math.Max(control.MinWidth, Math.Max(explicitWidth, desiredWidth));
        return baseWidth + control.Margin.Left + control.Margin.Right;
    }
}
