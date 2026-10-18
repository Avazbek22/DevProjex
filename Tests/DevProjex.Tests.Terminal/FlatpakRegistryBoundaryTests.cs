using System.Net.Sockets;
using System.Text.Json;
using DevProjex.Terminal.DesktopControl;

namespace DevProjex.Tests.Terminal;

public sealed class FlatpakRegistryBoundaryTests
{
	[Fact]
	public async Task FortyEightConcurrentRegistrationsKeepTheirEndpointsIndependent()
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, "test.app"));
		var directory = Directory.CreateTempSubdirectory("dpx-");
		var listeners = new List<Socket>();
		try
		{
			var paths = new DesktopControlPaths(() => directory.FullName);
			var registry = new DesktopInstanceRegistry(paths);
			var now = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
			var ids = Enumerable.Range(1, 48).Select(index => index.ToString("x16", System.Globalization.CultureInfo.InvariantCulture) + new string('0', 16)).ToArray();
			foreach (var id in ids)
			{
				var endpoint = paths.GetSocketPath(id);
				Directory.CreateDirectory(Path.GetDirectoryName(endpoint)!);
				var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
				listeners.Add(listener);
				listener.Bind(new UnixDomainSocketEndPoint(endpoint));
				listener.Listen(4);
			}
			await Task.WhenAll(ids.Select(id => registry.RegisterAsync(new(DesktopProtocol.CurrentVersion, id,
				int.MaxValue, now.Ticks, directory.FullName, now, "unix", paths.GetSocketPath(id)), TestContext.Current.CancellationToken)));
			var snapshot = await registry.ProbeAsync(false, TestContext.Current.CancellationToken);
			Assert.Equal(0, snapshot.StaleEntryCount);
			Assert.Equal(ids, snapshot.Instances.Select(instance => instance.InstanceId));
			await Task.WhenAll(ids.Select(registry.UnregisterAsync));
			Assert.Empty(await registry.ListAsync(TestContext.Current.CancellationToken));
			Assert.Empty(Directory.GetFiles(paths.RegistryDirectory, "*.tmp"));
		}
		finally
		{
			foreach (var listener in listeners) listener.Dispose();
			directory.Delete(true);
		}
	}

	[Fact]
	public async Task SymbolicRegistrationCannotPublishAnExternalFileAsAnOwnedInstance()
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, "test.app"));
		var directory = Directory.CreateTempSubdirectory("dpx-");
		try
		{
			var paths = new DesktopControlPaths(() => directory.FullName);
			const string id = "11111111111111111111111111111111";
			var endpoint = paths.GetSocketPath(id);
			Directory.CreateDirectory(Path.GetDirectoryName(endpoint)!);
			using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
			listener.Bind(new UnixDomainSocketEndPoint(endpoint));
			listener.Listen(4);
			var registry = new DesktopInstanceRegistry(paths);
			var now = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
			await registry.RegisterAsync(new(DesktopProtocol.CurrentVersion, id, int.MaxValue, now.Ticks,
				directory.FullName, now, "unix", endpoint), TestContext.Current.CancellationToken);
			var owned = paths.GetRegistrationPath(id);
			var external = Path.Combine(directory.FullName, "external.json");
			var bytes = File.ReadAllBytes(owned);
			File.Move(owned, external);
			if (OperatingSystem.IsWindows())
			{
				var target = Directory.CreateDirectory(Path.Combine(directory.FullName, "external-directory"));
				var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
				{
					Arguments = $"/d /c mklink /J \"{owned}\" \"{target.FullName}\"", UseShellExecute = false,
					CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
				};
				using var process = System.Diagnostics.Process.Start(start)!;
				await process.WaitForExitAsync(TestContext.Current.CancellationToken);
				Assert.Equal(0, process.ExitCode);
			}
			else File.CreateSymbolicLink(owned, external);
			Assert.Empty((await registry.ProbeAsync(false, TestContext.Current.CancellationToken)).Instances);
			Assert.Empty((await registry.ProbeAsync(true, TestContext.Current.CancellationToken)).Instances);
			Assert.Equal(bytes, File.ReadAllBytes(external));
			Assert.True(File.Exists(endpoint));
		}
		finally
		{
			var link = Path.Combine(directory.FullName, "DevProjex", "desktop-control", "instances", "11111111111111111111111111111111.json");
			if (OperatingSystem.IsWindows() && Directory.Exists(link)) Directory.Delete(link);
			directory.Delete(true);
		}
	}

	[Theory]
	[InlineData(97)]
	[InlineData(104)]
	[InlineData(108)]
	[InlineData(180)]
	public void LongUtf8SocketRootsFailClearlyWithoutFallingBackToPrivateTemp(int length)
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, "test.app"));
		var directory = Directory.CreateTempSubdirectory("dpx-");
		try
		{
			var root = Path.Combine(directory.FullName, new string('é', length / 2));
			var paths = new DesktopControlPaths(() => root);
			var error = Assert.Throws<IOException>(() => paths.GetSocketPath("11111111111111111111111111111111"));
			Assert.Contains("socket path", error.Message);
			Assert.Contains("limit", error.Message);
		}
		finally { directory.Delete(true); }
	}
}
