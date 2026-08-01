using DevProjex.Avalonia.Views;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class TopMenuBarPopupPlacementTests
{
    [Theory]
    [InlineData(700, 80, 600, 0, 1500, 0)]
    [InlineData(700, 80, 600, 0, 850, -198)]
    [InlineData(20, 80, 300, 0, 850, 98)]
    [InlineData(650, 80, 600, 50, 850, -98)]
    public void CalculateLargePopupHorizontalOffset_UsesOnlyRequiredViewportCorrection(
        double anchorX,
        double anchorWidth,
        double popupWidth,
        double viewportX,
        double viewportWidth,
        double expectedOffset)
    {
        var offset = TopMenuBarView.CalculateLargePopupHorizontalOffset(
            new Rect(anchorX, 0, anchorWidth, 30),
            popupWidth,
            new Rect(viewportX, 0, viewportWidth, 600));

        Assert.Equal(expectedOffset, offset, precision: 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void CalculateLargePopupHorizontalOffset_InvalidPopupWidth_DoesNotMovePopup(
        double popupWidth)
    {
        var offset = TopMenuBarView.CalculateLargePopupHorizontalOffset(
            new Rect(700, 0, 80, 30),
            popupWidth,
            new Rect(0, 0, 850, 600));

        Assert.Equal(0, offset);
    }
}
