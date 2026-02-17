namespace DevProjex.Tests.Integration.Performance;

[Collection("LocalPerformance")]
public sealed class LocalPerformanceSettingsTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    [InlineData("TRUE", true)]
    [InlineData("YeS", true)]
    public void IsPerformanceRunEnabled_ParsesEnvironmentFlag(string? value, bool expected)
    {
        using var _ = EnvVarScope.Set(LocalPerformanceSettings.RunPerfEnvVar, value);

        Assert.Equal(expected, LocalPerformanceSettings.IsPerformanceRunEnabled);
    }

    [Theory]
    [InlineData(LocalPerformanceSettings.UpdateBaselineEnvVar, "1", true)]
    [InlineData(LocalPerformanceSettings.UpdateBaselineEnvVar, "0", false)]
    [InlineData(LocalPerformanceSettings.EnforceBaselineRegressionEnvVar, "true", true)]
    [InlineData(LocalPerformanceSettings.EnforceBaselineRegressionEnvVar, "false", false)]
    public void BaselineSwitches_RespectEnvironmentFlags(string variableName, string value, bool expected)
    {
        using var _ = EnvVarScope.Set(variableName, value);

        var actual = variableName == LocalPerformanceSettings.UpdateBaselineEnvVar
            ? LocalPerformanceSettings.ShouldUpdateBaseline
            : LocalPerformanceSettings.ShouldEnforceBaselineRegression;

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BaselineFilePath_UsesCustomEnvironmentPath_WhenProvided()
    {
        const string customPath = @"C:\temp\devprojex-perf-baseline.json";
        using var _ = EnvVarScope.Set(LocalPerformanceSettings.BaselinePathEnvVar, customPath);

        Assert.Equal(customPath, LocalPerformanceSettings.BaselineFilePath);
    }

    [Fact]
    public void BaselineFilePath_FallsBackToDefaultLocation_WhenCustomPathMissing()
    {
        using var _ = EnvVarScope.Set(LocalPerformanceSettings.BaselinePathEnvVar, null);

        var path = LocalPerformanceSettings.BaselineFilePath;

        Assert.Contains("DevProjex", path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Performance", path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("perf-baseline.local.json", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LocalPerformanceFactAttribute_Skips_WhenPerformanceFlagIsDisabled()
    {
        using var _ = EnvVarScope.Set(LocalPerformanceSettings.RunPerfEnvVar, null);
        var attribute = new LocalPerformanceFactAttribute();

        Assert.False(string.IsNullOrWhiteSpace(attribute.Skip));
        Assert.Contains(LocalPerformanceSettings.RunPerfEnvVar, attribute.Skip!);
    }

    [Fact]
    public void LocalPerformanceFactAttribute_DoesNotSkip_WhenPerformanceFlagIsEnabled()
    {
        using var _ = EnvVarScope.Set(LocalPerformanceSettings.RunPerfEnvVar, "1");
        var attribute = new LocalPerformanceFactAttribute();

        Assert.Null(attribute.Skip);
    }

    [Fact]
    public void LocalPerformanceTheoryAttribute_Skips_WhenPerformanceFlagIsDisabled()
    {
        using var _ = EnvVarScope.Set(LocalPerformanceSettings.RunPerfEnvVar, null);
        var attribute = new LocalPerformanceTheoryAttribute();

        Assert.False(string.IsNullOrWhiteSpace(attribute.Skip));
        Assert.Contains(LocalPerformanceSettings.RunPerfEnvVar, attribute.Skip!);
    }

    [Fact]
    public void LocalPerformanceTheoryAttribute_DoesNotSkip_WhenPerformanceFlagIsEnabled()
    {
        using var _ = EnvVarScope.Set(LocalPerformanceSettings.RunPerfEnvVar, "true");
        var attribute = new LocalPerformanceTheoryAttribute();

        Assert.Null(attribute.Skip);
    }

    private sealed class EnvVarScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _originalValue;

        private EnvVarScope(string name, string? value)
        {
            _name = name;
            _originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public static EnvVarScope Set(string name, string? value) => new(name, value);

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _originalValue);
        }
    }
}

