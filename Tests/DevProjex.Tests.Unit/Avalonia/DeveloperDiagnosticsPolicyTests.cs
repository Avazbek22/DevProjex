namespace DevProjex.Tests.Unit.Avalonia;

public sealed class DeveloperDiagnosticsPolicyTests
{
	[Fact]
	public void UiBenchmarkIdleSettle_DefaultCoversDeferredCleanupWindow()
	{
		using var idleOverride = TemporaryEnvironmentVariable.Set(
			"DEVPROJEX_UI_BENCHMARK_IDLE_SECONDS",
			string.Empty);

		var duration = StartupInteractionController.ResolveBenchmarkIdleSettleDuration();

		Assert.Equal(TimeSpan.FromSeconds(8), duration);
		Assert.True(duration > TimeSpan.FromSeconds(2));
	}

	[Theory]
	[InlineData("15", 15)]
	[InlineData("0", 1)]
	[InlineData("120", 60)]
	public void UiBenchmarkIdleSettle_UsesBoundedEnvironmentOverride(
		string configuredSeconds,
		int expectedSeconds)
	{
		using var idleOverride = TemporaryEnvironmentVariable.Set(
			"DEVPROJEX_UI_BENCHMARK_IDLE_SECONDS",
			configuredSeconds);

		Assert.Equal(
			TimeSpan.FromSeconds(expectedSeconds),
			StartupInteractionController.ResolveBenchmarkIdleSettleDuration());
	}

	[Theory]
	[InlineData("", 3)]
	[InlineData("0", 1)]
	[InlineData("5", 5)]
	[InlineData("20", 8)]
	public void UiBenchmarkProjectReloadCount_UsesBoundedEnvironmentOverride(
		string configuredCount,
		int expectedCount)
	{
		using var reloadOverride = TemporaryEnvironmentVariable.Set(
			"DEVPROJEX_UI_BENCHMARK_PROJECT_RELOADS",
			configuredCount);

		Assert.Equal(
			expectedCount,
			StartupInteractionController.ResolveBenchmarkProjectReloadCount());
	}

	private sealed class TemporaryEnvironmentVariable : IDisposable
	{
		private readonly string _name;
		private readonly string? _previousValue;

		private TemporaryEnvironmentVariable(string name, string value)
		{
			_name = name;
			_previousValue = Environment.GetEnvironmentVariable(name);
			Environment.SetEnvironmentVariable(name, value);
		}

		public static TemporaryEnvironmentVariable Set(string name, string value) => new(name, value);

		public void Dispose() => Environment.SetEnvironmentVariable(_name, _previousValue);
	}
}
