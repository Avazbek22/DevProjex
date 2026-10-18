using System.Net.Sockets;
using DevProjex.Terminal.DesktopControl;

namespace DevProjex.Tests.Terminal;

public sealed class FlatpakDesktopRegistryTests
{
	[Fact]
	public async Task DiscoveryUsesTheSharedSocketInsteadOfAnotherNamespacesPid()
	{
		Assert.True(Socket.OSSupportsUnixDomainSockets, "The supported desktop platforms must provide Unix domain sockets.");
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, "io.github.Avazbek22.DevProjex"));
		var directory = Directory.CreateTempSubdirectory("dpx-");
		try
		{
			var paths = new DesktopControlPaths(() => directory.FullName);
			var registry = new DesktopInstanceRegistry(paths);
			const string id = "flatpak-shared";
			var endpoint = paths.GetSocketPath(id);
			Directory.CreateDirectory(Path.GetDirectoryName(endpoint)!);
			using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
			listener.Bind(new UnixDomainSocketEndPoint(endpoint));
			listener.Listen(4);
			var now = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
			await registry.RegisterAsync(new(DesktopProtocol.CurrentVersion, id, int.MaxValue,
				now.Ticks, directory.FullName, now, "unix", endpoint),
				TestContext.Current.CancellationToken);
			var snapshot = await registry.ProbeAsync(false, TestContext.Current.CancellationToken);
			Assert.Equal(id, Assert.Single(snapshot.Instances).InstanceId);
		}
		finally
		{
			directory.Delete(recursive: true);
		}
	}
}
