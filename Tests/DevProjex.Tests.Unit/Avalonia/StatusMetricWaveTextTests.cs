using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using DevProjex.Avalonia.Controls;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class StatusMetricWaveTextTests
{
    [AvaloniaFact]
    public void Measure_ShorterMetricUpdate_KeepsLargestPublishedWidth()
    {
        var widestText = FormattableString.CurrentCulture(
            $"Lines: {999_999:N0} · Characters: {9_999_999:N0} · Tokens: {999_999:N0}");
        var control = new StatusMetricWaveText
        {
            IsAnimationEnabled = false,
            Label = "Content",
            Text = widestText
        };

        control.Measure(Size.Infinity);
        var widestSize = control.DesiredSize;

        control.Text = "Lines: 1 · Characters: 1 · Tokens: 1";
        control.Measure(Size.Infinity);

        Assert.Equal(widestSize.Width, control.DesiredSize.Width);
    }

    [AvaloniaFact]
    public void CompactMetricShape_ReleasesFullMetricWidthWhenAnimationIsDisabled()
    {
        var fullText = FormattableString.CurrentCulture(
            $"Lines: {999_999:N0} · Characters: {9_999_999:N0} · Tokens: {999_999:N0}");
        var control = new StatusMetricWaveText
        {
            IsAnimationEnabled = false,
            Label = "Content",
            Text = fullText
        };

        control.Measure(Size.Infinity);
        var fullWidth = control.DesiredSize.Width;

        control.Text = "Lines: 1";
        control.Measure(Size.Infinity);

        Assert.True(control.DesiredSize.Width < fullWidth);
    }

    [AvaloniaFact]
    public async Task PresentationChange_DuringMetricRoll_CompletesAgainstCurrentAppearance()
    {
        var control = new StatusMetricWaveText
        {
            IsAnimationEnabled = false,
            Label = "Content",
            Text = "Lines: 1"
        };
        var window = new Window { Content = control };

        try
        {
            window.Show();
            await FlushUiAsync();
            control.IsAnimationEnabled = true;
            control.Text = "Lines: 2";
            Assert.True(control.IsAnimationActive);

            control.TextBrush = Brushes.Orange;

            Assert.False(control.IsAnimationActive);
            control.Measure(Size.Infinity);
            Assert.True(control.DesiredSize.Width > 0);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CompactMetricShape_ReleasesFullMetricWidthWithoutRollingUnrelatedValues()
    {
        var fullText = FormattableString.CurrentCulture(
            $"Lines: {999_999:N0} · Characters: {9_999_999:N0} · Tokens: {999_999:N0}");
        var control = new StatusMetricWaveText
        {
            IsAnimationEnabled = false,
            Label = "Content",
            Text = fullText
        };
        var window = new Window { Content = control };

        try
        {
            window.Show();
            await FlushUiAsync();
            control.Measure(Size.Infinity);
            var fullWidth = control.DesiredSize.Width;
            control.IsAnimationEnabled = true;

            control.Text = "Lines: 1";
            control.Measure(Size.Infinity);

            Assert.False(control.IsAnimationActive);
            Assert.True(control.DesiredSize.Width < fullWidth);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DetachDuringInitialReveal_CompletesAnimation()
    {
        var control = new StatusMetricWaveText
        {
            Label = "Tree",
            Text = "Lines: 10 · Characters: 100 · Tokens: 25"
        };
        var window = new Window { Content = control };

        window.Show();
        await FlushUiAsync();
        Assert.True(control.IsAnimationActive);

        window.Close();

        Assert.False(control.IsAnimationActive);
    }

    [AvaloniaFact]
    public async Task ProjectSwitch_AfterInitialReveal_UsesMetricRollWithoutRepeatingWave()
    {
        var control = new StatusMetricWaveText
        {
            Label = "Tree",
            Text = "Lines: 10 · Characters: 100 · Tokens: 25"
        };
        var window = new Window { Content = control };

        try
        {
            window.Show();
            await FlushUiAsync();
            Assert.True(control.IsInitialRevealActive);

            control.IsAnimationEnabled = false;
            control.IsAnimationEnabled = true;
            control.IsVisible = false;
            control.Text = string.Empty;
            control.Text = "Lines: 20 · Characters: 200 · Tokens: 50";

            Assert.False(control.IsAnimationActive);

            control.IsVisible = true;
            await FlushUiAsync();

            Assert.True(control.IsAnimationActive);
            Assert.False(control.IsInitialRevealActive);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task FlushUiAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }
}
