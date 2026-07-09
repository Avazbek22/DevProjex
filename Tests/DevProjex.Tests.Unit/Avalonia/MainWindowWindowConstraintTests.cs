namespace DevProjex.Tests.Unit.Avalonia;

public sealed class MainWindowWindowConstraintTests
{
    [Theory]
    [InlineData(882.0, 1.0)]
    [InlineData(882.0, 1.25)]
    [InlineData(882.0, 1.5)]
    [InlineData(882.0, 1.75)]
    [InlineData(882.0, 2.0)]
    public void AlignWindowConstraintToPhysicalPixels_ProducesWholePixelTrackSize(
        double constraint,
        double renderScaling)
    {
        var alignedConstraint = MainWindow.AlignWindowConstraintToPhysicalPixels(constraint, renderScaling);
        var physicalPixels = alignedConstraint * renderScaling;

        Assert.True(alignedConstraint >= constraint);
        Assert.True(alignedConstraint - constraint < 1.0 / renderScaling);
        Assert.InRange(Math.Abs(physicalPixels - Math.Round(physicalPixels)), 0, 0.000_001);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AlignWindowConstraintToPhysicalPixels_InvalidScalingFallsBackToOne(double renderScaling)
    {
        var alignedConstraint = MainWindow.AlignWindowConstraintToPhysicalPixels(882.25, renderScaling);

        Assert.Equal(883.0, alignedConstraint);
    }
}
