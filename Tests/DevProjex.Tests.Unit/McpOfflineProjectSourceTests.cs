using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

/// <summary>
/// A server that was not given remote permission must not let a client-supplied project string
/// reach the network, and a UNC or device path form reaches it simply by being opened.
/// </summary>
/// <remarks>
/// None of these cases needs a reachable host, and none of them waits. They read the error code
/// instead, because the codes say which step answered. For a path form, exactly three answers are
/// reachable: the refusal below returns <c>DPX-MCP-INVALID-ARGUMENTS</c> without opening anything;
/// resolution against the configured roots returns <c>DPX-MCP-UNKNOWN-PROJECT</c>, and it can only
/// return that after the open it performs has already come back; classification as a repository url
/// returns <c>DPX-MCP-REMOTE-DISABLED</c>. Observing the first code therefore witnesses that neither
/// of the other two steps ran, which is the property under test — a step that had run would have
/// answered, and its answer would have been a different code.
/// <para>
/// Host names here are deliberately unreachable in every environment: <c>.invalid</c> is reserved by
/// RFC 2606 and <c>.example</c> by RFC 6761, so a regression that removes the refusal fails these
/// cases rather than quietly contacting something.
/// </para>
/// </remarks>
public sealed class McpOfflineProjectSourceTests
{
	private const string UncProject = "//host.invalid/share/repository";
	private const string WindowsUncProject = @"\\host.invalid\share\repository";

	/// <summary>
	/// A url-shaped UNC string: the part before its colon carries a dot, which is what the
	/// repository-url classifier reads to call a string a scp-style remote. Without the refusal it
	/// would take the remote branch and answer <c>DPX-MCP-REMOTE-DISABLED</c>.
	/// </summary>
	private const string UrlShapedUncProject = "//host.example/share:repository";

	[Theory]
	[InlineData(UncProject)]
	[InlineData(WindowsUncProject)]
	[InlineData("//host.invalid/share")]
	[InlineData(@"\\host.invalid")]
	[InlineData(@"\\?\UNC\host.invalid\share")]
	[InlineData(@"\\.\pipe\devprojex")]
	public async Task ARemoteFormIsRefusedBeforeAnythingOpensIt(string project)
	{
		using var workspace = new TemporaryDirectory();
		using var resolver = CreateOfflineResolver(workspace.CreateFolder("local"));

		var refusal = await Assert.ThrowsAsync<McpToolException>(() =>
			resolver.ResolveAsync(project, branch: null, TestContext.Current.CancellationToken));

		// Not DPX-MCP-UNKNOWN-PROJECT: that code is only reachable once the configured-root
		// resolution has opened the path and the open has returned.
		Assert.Equal(McpErrorCodes.InvalidArguments, refusal.Code);
		Assert.Contains("UNC or device path form", refusal.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// The refusal also precedes the repository-url classifier, which probes the string with
	/// <c>Directory.Exists</c> before deciding. That probe is itself an open, so a refusal placed
	/// after it would be too late.
	/// </summary>
	[Fact]
	public async Task AUrlShapedRemoteFormIsRefusedBeforeTheRepositoryUrlClassifier()
	{
		using var workspace = new TemporaryDirectory();
		using var resolver = CreateOfflineResolver(workspace.CreateFolder("local"));

		var refusal = await Assert.ThrowsAsync<McpToolException>(() =>
			resolver.ResolveAsync(UrlShapedUncProject, branch: null, TestContext.Current.CancellationToken));

		// Not DPX-MCP-REMOTE-DISABLED: that is what the classifier's branch answers, and reaching it
		// means the classifier had already run its probe.
		Assert.Equal(McpErrorCodes.InvalidArguments, refusal.Code);
		Assert.Contains("UNC or device path form", refusal.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// Remote permission does not buy a client the UNC forms: they are not a supported clone source
	/// in either state, and the shape is refused the same way with permission granted.
	/// </summary>
	[Theory]
	[InlineData(UncProject)]
	[InlineData(WindowsUncProject)]
	[InlineData(UrlShapedUncProject)]
	public async Task ARemoteFormIsRefusedEvenWhenRemoteProjectsArePermitted(string project)
	{
		using var workspace = new TemporaryDirectory();
		using var resolver = new McpProjectSourceResolver(
			new McpRootRegistry([workspace.CreateFolder("local")]),
			allowRemote: true,
			static () => throw new InvalidOperationException(
				"Remote services must not be created for a refused path form."));

		var refusal = await Assert.ThrowsAsync<McpToolException>(() =>
			resolver.ResolveAsync(project, branch: null, TestContext.Current.CancellationToken));

		// The message is asserted, not only the code: a permitted server rejects an unsupported clone
		// source under the same code further down, so the code alone would not say which step
		// answered, and the step is the point.
		Assert.Equal(McpErrorCodes.InvalidArguments, refusal.Code);
		Assert.Contains("UNC or device path form", refusal.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// The same refusal guards the registry itself, so a caller that resolves a project without
	/// going through the source resolver is covered as well.
	/// </summary>
	[Theory]
	[InlineData(UncProject)]
	[InlineData(WindowsUncProject)]
	[InlineData(@"\\?\UNC\host.invalid\share")]
	public void TheRootRegistryRefusesARemoteFormBeforeResolvingIt(string project)
	{
		using var workspace = new TemporaryDirectory();
		var registry = new McpRootRegistry([workspace.CreateFolder("local")]);

		var refusal = Assert.Throws<McpToolException>(() => registry.ResolveProject(project));

		// Not DPX-MCP-UNKNOWN-PROJECT, which is the code this method reaches only by way of the open.
		Assert.Equal(McpErrorCodes.InvalidArguments, refusal.Code);
		Assert.Contains("UNC or device path form", refusal.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// Ordinary local projects keep answering exactly as before: by path, by name, and with the same
	/// code for a path that is simply not a configured root.
	/// </summary>
	[Fact]
	public async Task OrdinaryLocalProjectsResolveUnchanged()
	{
		using var workspace = new TemporaryDirectory();
		var local = workspace.CreateFolder("local");
		var outside = workspace.CreateFolder("outside");
		var expected = McpRootJailFileStreamOpener.ResolveDirectoryPath(
			McpRootRegistry.ResolvePhysicalExistingPath(local, requireDirectory: true));
		using var resolver = CreateOfflineResolver(local);

		var byPath = await resolver.ResolveAsync(local, branch: null, TestContext.Current.CancellationToken);
		var byName = await resolver.ResolveAsync("local", branch: null, TestContext.Current.CancellationToken);
		var byRelative = await resolver.ResolveAsync(
			Path.Combine(local, "."),
			branch: null,
			TestContext.Current.CancellationToken);
		var unknown = await Assert.ThrowsAsync<McpToolException>(() =>
			resolver.ResolveAsync(outside, branch: null, TestContext.Current.CancellationToken));

		Assert.Equal(expected, byPath.Root);
		Assert.Equal(expected, byName.Root);
		Assert.Equal(expected, byRelative.Root);
		Assert.Equal(McpErrorCodes.UnknownProject, unknown.Code);
	}

	/// <summary>
	/// The operator keeps the last word. A root listed at startup stays addressable by the spelling
	/// it was listed with, even when that spelling is one of the refused forms, because deciding it
	/// is a comparison against the startup list rather than an open.
	/// </summary>
	/// <remarks>
	/// The device prefix is the one refused form that can be pointed at a local directory, so it is
	/// what makes this case runnable without a share. The prefix is Windows-only.
	/// </remarks>
	[Fact]
	public async Task ARootListedAtStartupStaysAddressableByItsListedSpelling()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("The device path prefix that makes this case runnable is Windows-only.");

		using var workspace = new TemporaryDirectory();
		var local = workspace.CreateFolder("local");
		var deviceSpelling = $@"\\?\{local}";
		Assert.True(McpRemoteProviderPath.ReachesRemoteProvider(deviceSpelling));
		using var resolver = CreateOfflineResolver(deviceSpelling);

		var resolved = await resolver.ResolveAsync(
			deviceSpelling,
			branch: null,
			TestContext.Current.CancellationToken);
		var refusal = await Assert.ThrowsAsync<McpToolException>(() =>
			resolver.ResolveAsync(
				$@"\\?\{workspace.CreateFolder("unlisted")}",
				branch: null,
				TestContext.Current.CancellationToken));

		Assert.Equal(
			McpRootJailFileStreamOpener.ResolveDirectoryPath(
				McpRootRegistry.ResolvePhysicalExistingPath(local, requireDirectory: true)),
			resolved.Root);
		Assert.Equal(McpErrorCodes.InvalidArguments, refusal.Code);
	}

	/// <summary>
	/// A server without remote permission never builds its remote machinery, so the factory throws:
	/// any case that reaches it fails loudly instead of quietly doing remote work.
	/// </summary>
	private static McpProjectSourceResolver CreateOfflineResolver(string root) =>
		new(
			new McpRootRegistry([root]),
			allowRemote: false,
			static () => throw new InvalidOperationException(
				"An offline server must not create remote services."));
}
