using DevProjex.Avalonia.Services;
using DevProjex.Terminal.DesktopControl;

namespace DevProjex.Tests.Unit;

public sealed class CommandLineBenchmarkResourceLifetimeTests
{
	[Fact]
	public void BenchmarkAnalysisServicesDisposesItsOwnedLifetimeOnce()
	{
		var lifetime = new CountingDisposable();
		var services = new BenchmarkAnalysisServices(null!, null!, null!, null!, lifetime);

		services.Dispose();
		services.Dispose();

		Assert.Equal(1, lifetime.DisposeCount);
	}

	[Fact]
	public void DesktopDiagnosticRequestIsDeletedWhenBenchmarkRequestIsDisposed()
	{
		using var project = new TemporaryDirectory();
		var request = DesktopDiagnosticProcessRequestFactory.Create(
			project.Path,
			Path.Combine(project.Path, "session.json"),
			"standard");
		var requestPath = request.Environment![DesktopDiagnosticRequestStore.EnvironmentVariable]!;
		Assert.True(File.Exists(requestPath));

		request.Dispose();

		Assert.False(File.Exists(requestPath));
	}

	private sealed class CountingDisposable : IDisposable
	{
		public int DisposeCount { get; private set; }

		public void Dispose() => DisposeCount++;
	}
}
