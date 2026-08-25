using DevProjex.Application.DesktopControl;
using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.DesktopControl;

namespace DevProjex.Tests.Terminal;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class DesktopRequestStoreBoundaryTests
{
	[Fact]
	public async Task RequestsUseTheIsolatedPerUserDesktopControlRoot()
	{
		using var workspace = new TemporaryDirectory();
		var previousDataRoot = Environment.GetEnvironmentVariable(
			InvocationEnvironment.InternalDataRootVariable);
		try
		{
			Environment.SetEnvironmentVariable(
				InvocationEnvironment.InternalDataRootVariable,
				workspace.Path);
			var paths = new DesktopControlPaths();

			var launchPath = await DesktopLaunchRequestStore.CreateAsync(
				new DesktopOpenRequest(ProjectPath: workspace.Path),
				TestContext.Current.CancellationToken);
			Assert.True(PathUtility.IsPathInside(launchPath, paths.RequestDirectory));
			Environment.SetEnvironmentVariable(
				InvocationEnvironment.DesktopRequestVariable,
				launchPath);
			var launchRequest = await DesktopLaunchRequestStore.TryConsumeFromEnvironmentAsync(
				TestContext.Current.CancellationToken);
			Assert.Equal(workspace.Path, launchRequest?.ProjectPath);
			Assert.False(File.Exists(launchPath));

			var diagnosticPath = DesktopDiagnosticRequestStore.Create(
				new DesktopDiagnosticRequest(workspace.Path, "report.json", "startup"));
			Assert.True(PathUtility.IsPathInside(diagnosticPath, paths.DiagnosticDirectory));
			Environment.SetEnvironmentVariable(
				DesktopDiagnosticRequestStore.EnvironmentVariable,
				diagnosticPath);
			var diagnosticRequest = DesktopDiagnosticRequestStore.TryConsume();
			Assert.Equal(workspace.Path, diagnosticRequest?.ProjectPath);
			Assert.False(File.Exists(diagnosticPath));
		}
		finally
		{
			Environment.SetEnvironmentVariable(
				InvocationEnvironment.DesktopRequestVariable,
				null);
			Environment.SetEnvironmentVariable(
				DesktopDiagnosticRequestStore.EnvironmentVariable,
				null);
			Environment.SetEnvironmentVariable(
				InvocationEnvironment.InternalDataRootVariable,
				previousDataRoot);
		}
	}

	[Fact]
	public async Task LaunchRequestOutsidePrivateRoot_IsRejectedWithoutDeletingCallerFile()
	{
		var path = CreateExternalEnvelope();
		try
		{
			Environment.SetEnvironmentVariable(InvocationEnvironment.DesktopRequestVariable, path);

			Assert.Null(await DesktopLaunchRequestStore.TryConsumeFromEnvironmentAsync(
				TestContext.Current.CancellationToken));
			Assert.True(File.Exists(path));
		}
		finally
		{
			Environment.SetEnvironmentVariable(InvocationEnvironment.DesktopRequestVariable, null);
			Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
		}
	}

	[Fact]
	public void DiagnosticRequestOutsidePrivateRoot_IsRejectedWithoutDeletingCallerFile()
	{
		var path = CreateExternalEnvelope();
		try
		{
			Environment.SetEnvironmentVariable(DesktopDiagnosticRequestStore.EnvironmentVariable, path);

			Assert.Null(DesktopDiagnosticRequestStore.TryConsume());
			Assert.True(File.Exists(path));
		}
		finally
		{
			Environment.SetEnvironmentVariable(DesktopDiagnosticRequestStore.EnvironmentVariable, null);
			Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
		}
	}

	[Fact]
	public void StructurallyIncompleteDiagnosticRequest_IsRejectedAndDeleted()
	{
		using var workspace = new TemporaryDirectory();
		var previousDataRoot = Environment.GetEnvironmentVariable(
			InvocationEnvironment.InternalDataRootVariable);
		try
		{
			Environment.SetEnvironmentVariable(
				InvocationEnvironment.InternalDataRootVariable,
				workspace.Path);
			var directory = new DesktopControlPaths().DiagnosticDirectory;
			Directory.CreateDirectory(directory);
			var requestPath = Path.Combine(directory, "incomplete.json");
			File.WriteAllText(requestPath, "{}");
			Environment.SetEnvironmentVariable(
				DesktopDiagnosticRequestStore.EnvironmentVariable,
				requestPath);

			Assert.Null(DesktopDiagnosticRequestStore.TryConsume());
			Assert.False(File.Exists(requestPath));
		}
		finally
		{
			Environment.SetEnvironmentVariable(
				DesktopDiagnosticRequestStore.EnvironmentVariable,
				null);
			Environment.SetEnvironmentVariable(
				InvocationEnvironment.InternalDataRootVariable,
				previousDataRoot);
		}
	}

	private static string CreateExternalEnvelope()
	{
		var directory = Path.Combine(
			Path.GetTempPath(),
			"DevProjex-request-boundary-tests",
			Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		var path = Path.Combine(directory, "request.json");
		File.WriteAllText(path, "{}");
		return path;
	}
}
