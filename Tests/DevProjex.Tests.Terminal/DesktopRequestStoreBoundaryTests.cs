using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.DesktopControl;

namespace DevProjex.Tests.Terminal;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class DesktopRequestStoreBoundaryTests
{
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
