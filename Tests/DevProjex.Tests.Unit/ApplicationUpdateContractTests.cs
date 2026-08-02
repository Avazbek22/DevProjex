using DevProjex.Application.Updates;

namespace DevProjex.Tests.Unit;

public sealed class ApplicationUpdateContractTests
{
    [Theory]
    [InlineData("4", "4")]
    [InlineData("v4.9", "4.9")]
    [InlineData("V4.9.0", "4.9.0")]
    [InlineData("4.9.0.12", "4.9.0.12")]
    [InlineData("4.9.0+build.123", "4.9.0")]
    [InlineData(" v04.009.0 ", "4.9.0")]
    public void ReleaseVersion_StableForms_NormalizeWithoutChangingPrecedence(
        string value,
        string expected)
    {
        Assert.True(ApplicationReleaseVersion.TryParse(value, out var parsed));

        Assert.Equal(expected, parsed.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("4.9.0-preview.1")]
    [InlineData("4..9")]
    [InlineData("4.9.0.0.1")]
    [InlineData("4.9.invalid")]
    [InlineData("2147483648.0")]
    public void ReleaseVersion_NonStableOrMalformedForms_AreRejected(string? value)
        => Assert.False(ApplicationReleaseVersion.TryParse(value, out _));

    [Theory]
    [InlineData("4.9", "4.9.0", 0)]
    [InlineData("4.9.0", "4.9.1", -1)]
    [InlineData("4.10", "4.9.99", 1)]
    [InlineData("5.0.0", "4.99.99.99", 1)]
    public void ReleaseVersion_Comparison_IsNumeric(
        string leftValue,
        string rightValue,
        int expectedSign)
    {
        Assert.True(ApplicationReleaseVersion.TryParse(leftValue, out var left));
        Assert.True(ApplicationReleaseVersion.TryParse(rightValue, out var right));

        Assert.Equal(expectedSign, Math.Sign(left.CompareTo(right)));
    }

    [Fact]
    public void AutomaticSchedule_RequiresOptInAndSevenElapsedDays()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

        Assert.False(ApplicationUpdateSchedule.IsDue(false, null, now));
        Assert.True(ApplicationUpdateSchedule.IsDue(true, null, now));
        Assert.False(ApplicationUpdateSchedule.IsDue(true, now.AddDays(-7).AddTicks(1), now));
        Assert.True(ApplicationUpdateSchedule.IsDue(true, now.AddDays(-7), now));
        Assert.True(ApplicationUpdateSchedule.IsDue(true, now.AddMonths(-1), now));
        Assert.True(ApplicationUpdateSchedule.IsDue(true, now.AddMinutes(1), now));
    }
}
