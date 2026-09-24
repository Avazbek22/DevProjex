using DevProjex.Infrastructure.AppInstances;

namespace DevProjex.Tests.Unit.AppInstances;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class AppInstanceLauncherTests
{
	[Fact]
	public void BuildCurrentContext_UsesExistingAppImagePath()
	{
		using var temp = new TemporaryDirectory();
		var appImagePath = temp.CreateFile(
			"DevProjex-5.2-x86_64.AppImage",
			"appimage fixture");
		var previous = Environment.GetEnvironmentVariable("APPIMAGE");
		try
		{
			Environment.SetEnvironmentVariable("APPIMAGE", appImagePath);

			var context = AppInstanceLauncher.BuildCurrentContext();

			Assert.Equal(appImagePath, context.ProcessPath);
			Assert.Equal(Path.GetDirectoryName(appImagePath), context.WorkingDirectory);
		}
		finally
		{
			Environment.SetEnvironmentVariable("APPIMAGE", previous);
		}
	}
}
