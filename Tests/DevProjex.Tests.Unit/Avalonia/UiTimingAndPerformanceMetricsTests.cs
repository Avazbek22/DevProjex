using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class UiTimingAndPerformanceMetricsTests
{
	[Fact]
	public void UiTimingProfile_ScaleMatchesCurrentRuntimeProfile()
	{
		var duration = TimeSpan.FromMilliseconds(100);

		var scaled = UiTimingProfile.Scale(duration);

		if (UiTimingProfile.AreFastTimingsEnabled)
		{
			Assert.Equal(TimeSpan.FromMilliseconds(8), scaled);
			Assert.Equal(TimeSpan.FromMilliseconds(4), UiTimingProfile.AnimationSettleBuffer);
		}
		else
		{
			Assert.Equal(duration, scaled);
			Assert.Equal(TimeSpan.FromMilliseconds(24), UiTimingProfile.AnimationSettleBuffer);
		}
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	public void UiTimingProfile_ScaleLeavesNonPositiveDurationsUnchanged(int milliseconds)
	{
		var duration = TimeSpan.FromMilliseconds(milliseconds);

		var scaled = UiTimingProfile.Scale(duration);

		Assert.Equal(duration, scaled);
	}

	[Fact]
	public void UiTimingProfile_FastProfileNeverScalesPositiveDurationsBelowOneMillisecond()
	{
		var duration = TimeSpan.FromTicks(1);

		var scaled = UiTimingProfile.Scale(duration);

		Assert.True(scaled > TimeSpan.Zero);
		if (UiTimingProfile.AreFastTimingsEnabled)
			Assert.Equal(TimeSpan.FromMilliseconds(1), scaled);
		else
			Assert.Equal(duration, scaled);
	}

	[Fact]
	public void PerformanceMetrics_MeasureReturnsSafeDisposableForRepeatedDispose()
	{
		var measurement = PerformanceMetrics.Measure("unit-test-operation");

		measurement.Dispose();
		measurement.Dispose();
	}

	[Fact]
	public void PerformanceMetrics_MeasureDoesNotThrowForEmptyOperationName()
	{
		using var measurement = PerformanceMetrics.Measure(string.Empty);

		Assert.NotNull(measurement);
	}
}
