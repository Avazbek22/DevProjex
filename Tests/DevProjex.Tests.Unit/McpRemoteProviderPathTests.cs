using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

/// <summary>
/// The classifier is a pure string function, so these cases settle what it answers without any
/// path existing, any host being reachable, or any call waiting on one.
/// </summary>
public sealed class McpRemoteProviderPathTests
{
	/// <summary>
	/// Two separators and a host name, in either slash, on either platform.
	/// </summary>
	[Theory]
	[InlineData(@"\\server\share")]
	[InlineData(@"\\server\share\repository")]
	[InlineData("//server/share")]
	[InlineData("//server/share/repository")]
	[InlineData(@"\\server")]
	[InlineData("//server")]
	[InlineData(@"\/server/share")]
	[InlineData(@"/\server\share")]
	[InlineData(@"\\\\server\share")]
	[InlineData(@"  \\server\share")]
	[InlineData("//")]
	public void AHostNameFormIsRecognised(string path) =>
		Assert.True(McpRemoteProviderPath.ReachesRemoteProvider(path));

	/// <summary>
	/// The one entry in the Win32 device namespace that leaves the machine, and the NT object
	/// namespace, which is refused whole because it reaches the redirector by more than one
	/// spelling and nothing legitimate addresses a project through it.
	/// </summary>
	[Theory]
	[InlineData(@"\\?\UNC\server\share")]
	[InlineData(@"\\.\UNC\server\share")]
	[InlineData("//?/UNC/server/share")]
	[InlineData(@"\\?\unc\server\share")]
	[InlineData(@"\??\UNC\server\share")]
	[InlineData(@"\??\unc\server\share")]
	[InlineData(@"\??\GLOBALROOT\Device\Mup\server\share")]
	[InlineData(@"\??\C:\repository")]
	[InlineData("/??/UNC/server/share")]
	public void ADeviceNamespaceFormThatLeavesTheMachineIsRecognised(string path) =>
		Assert.True(McpRemoteProviderPath.ReachesRemoteProvider(path));

	/// <summary>
	/// The automount host maps mount on first access, so naming a host under one contacts it.
	/// </summary>
	[Theory]
	[InlineData("/net/server/share")]
	[InlineData("/net/server")]
	[InlineData("/Network/Servers/server/share")]
	[InlineData("/network/servers/server/share")]
	public void AnAutomountHostMapIsRecognised(string path) =>
		Assert.True(McpRemoteProviderPath.ReachesRemoteProvider(path));

	/// <summary>
	/// The Win32 device namespace otherwise addresses this machine. An extended-length path to a
	/// drive is an ordinary local directory written the long way, and a named pipe is local
	/// machinery; refusing either would reject paths that resolve perfectly well.
	/// </summary>
	[Theory]
	[InlineData(@"\\?\C:\repository")]
	[InlineData(@"\\.\C:\repository")]
	[InlineData("//?/C:/repository")]
	[InlineData(@"\\.\pipe\name")]
	[InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\repository")]
	[InlineData(@"\\?\UNCertain\repository")]
	[InlineData(@"\\?\")]
	public void ALocalDeviceFormIsNotARemoteForm(string path) =>
		Assert.False(McpRemoteProviderPath.ReachesRemoteProvider(path));

	/// <summary>
	/// Ordinary paths and repository urls are left alone. The host-map names count only as whole
	/// segments with something after them: the mount point itself is local, and a directory whose
	/// name merely starts with those letters is not one of them.
	/// </summary>
	[Theory]
	[InlineData(@"C:\repository")]
	[InlineData("C:/repository")]
	[InlineData("/home/user/repository")]
	[InlineData("/")]
	[InlineData("repository")]
	[InlineData("./repository")]
	[InlineData("../repository")]
	[InlineData(".")]
	[InlineData("src/models")]
	[InlineData(@"src\models")]
	[InlineData("/net")]
	[InlineData("/nethack/repository")]
	[InlineData("/Networking/repository")]
	[InlineData("/Network/Shares/server/share")]
	[InlineData(@"\?\UNC\server\share")]
	[InlineData("https://example.test/team/repository.git")]
	[InlineData("ssh://git@example.test/team/repository.git")]
	[InlineData("git@example.test:team/repository.git")]
	[InlineData("example.test:team/repository.git")]
	[InlineData("")]
	[InlineData("   ")]
	public void AnOrdinaryPathOrUrlIsNotARemoteForm(string path) =>
		Assert.False(McpRemoteProviderPath.ReachesRemoteProvider(path));

	[Fact]
	public void AMissingValueIsNotARemoteForm() =>
		Assert.False(McpRemoteProviderPath.ReachesRemoteProvider(null));

	/// <summary>
	/// A separator pair anywhere other than the start is ordinary text: the scheme separator in a
	/// url and a doubled separator inside a path both have to stay accepted.
	/// </summary>
	[Theory]
	[InlineData(@"C:\repository\\nested")]
	[InlineData("/home/user//nested")]
	[InlineData("http://example.test/x")]
	public void ASeparatorPairAwayFromTheStartIsNotARemoteForm(string path) =>
		Assert.False(McpRemoteProviderPath.ReachesRemoteProvider(path));
}
