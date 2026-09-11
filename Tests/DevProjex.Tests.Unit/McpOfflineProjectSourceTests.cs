using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

/// <summary>
/// A server that was not given remote permission must not let a client-supplied project string
/// reach the network, and a UNC or device path form reaches it simply by being opened.
/// </summary>
/// <remarks>
/// No case here needs a reachable host and none of them waits. What they observe is the count of
/// probes: every place that hands a path to the operating system reports itself through
/// <see cref="McpProjectPathProbe"/>, so a refusal that happened before any of them ran is a
/// measurement rather than an inference. An error code cannot make that measurement, because a
/// probe that ran and then fell through to the same refusal would produce the same code and the
/// same message. The codes are still asserted alongside, because they say which refusal answered
/// and the two are worded apart for exactly that reason.
/// <para>
/// Host names are unreachable in every environment by standard: <c>.invalid</c> is reserved by
/// RFC 2606 and <c>.example</c> by RFC 6761. So a regression that removes a refusal fails these
/// cases rather than quietly contacting something.
/// </para>
/// </remarks>
public sealed class McpOfflineProjectSourceTests
{
	private const string UncProject = "//host.invalid/share/repository";
	private const string WindowsUncProject = @"\\host.invalid\share\repository";

	/// <summary>
	/// A url-shaped UNC string: the part before its colon carries a dot, which is what the
	/// repository-url classifier reads to call a string an scp-style remote. Without the refusal it
	/// takes the remote branch, and it is the one literal here that reaches that branch at all — so
	/// it is the only case that can witness the refusal standing ahead of the classifier.
	/// </summary>
	private const string UrlShapedUncProject = "//host.example/share:repository";

	/// <summary>
	/// The NT object namespace carries one leading separator rather than two and still reaches the
	/// redirector, so it is the shape a two-separator test would miss.
	/// </summary>
	private const string NtObjectUncProject = @"\??\UNC\host.invalid\share";

	[Theory]
	[InlineData(UncProject)]
	[InlineData(WindowsUncProject)]
	[InlineData("//host.invalid/share")]
	[InlineData(@"\\host.invalid")]
	[InlineData(@"\\?\UNC\host.invalid\share")]
	[InlineData(NtObjectUncProject)]
	[InlineData(@"\??\GLOBALROOT\Device\Mup\host.invalid\share")]
	[InlineData("/net/host.invalid/share")]
	[InlineData("/Network/Servers/host.invalid/share")]
	public async Task ARemoteFormIsRefusedWithoutAnythingOpeningIt(string project)
	{
		using var workspace = new TemporaryDirectory();
		using var resolver = CreateOfflineResolver(workspace.CreateFolder("local"));

		using var probes = McpProjectPathProbe.Count();
		var refusal = await Assert.ThrowsAsync<McpToolException>(() =>
			resolver.ResolveAsync(project, branch: null, TestContext.Current.CancellationToken));

		Assert.Equal(0, probes.Value);
		Assert.Equal(McpErrorCodes.InvalidArguments, refusal.Code);
	}

	/// <summary>
	/// The refusal that answers is the one in the source resolver, ahead of the repository-url
	/// classifier. That matters because the classifier probes the string with
	/// <c>Directory.Exists</c> before deciding, and a refusal placed after it would be too late.
	/// </summary>
	[Theory]
	[InlineData(UncProject)]
	[InlineData(UrlShapedUncProject)]
	[InlineData(NtObjectUncProject)]
	public async Task TheSourceResolverIsWhatRefuses(string project)
	{
		using var workspace = new TemporaryDirectory();
		using var resolver = CreateOfflineResolver(workspace.CreateFolder("local"));

		using var probes = McpProjectPathProbe.Count();
		var refusal = await Assert.ThrowsAsync<McpToolException>(() =>
			resolver.ResolveAsync(project, branch: null, TestContext.Current.CancellationToken));

		// The two refusals are worded apart on purpose: sharing a code and a message would leave the
		// registry's refusal, which stands after the classifier, indistinguishable from this one.
		Assert.Equal(0, probes.Value);
		Assert.Contains(
			"refused before 'project' is read as a path at all",
			refusal.Message,
			StringComparison.Ordinal);
	}

	/// <summary>
	/// Remote permission does not buy a client these forms. They are refused in the same place and
	/// for the same reason, and no remote machinery is built for them.
	/// </summary>
	[Theory]
	[InlineData(UncProject)]
	[InlineData(WindowsUncProject)]
	[InlineData(UrlShapedUncProject)]
	[InlineData(NtObjectUncProject)]
	public async Task ARemoteFormIsRefusedEvenWhenRemoteProjectsArePermitted(string project)
	{
		using var workspace = new TemporaryDirectory();
		using var resolver = new McpProjectSourceResolver(
			new McpRootRegistry([workspace.CreateFolder("local")]),
			allowRemote: true,
			static () => throw new InvalidOperationException(
				"Remote services must not be created for a refused path form."));

		using var probes = McpProjectPathProbe.Count();
		var refusal = await Assert.ThrowsAsync<McpToolException>(() =>
			resolver.ResolveAsync(project, branch: null, TestContext.Current.CancellationToken));

		Assert.Equal(0, probes.Value);
		Assert.Equal(McpErrorCodes.InvalidArguments, refusal.Code);
		Assert.Contains("written as a path that names a host", refusal.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// The same refusal guards the registry, so a caller that resolves a project without going
	/// through the source resolver is covered too.
	/// </summary>
	[Theory]
	[InlineData(UncProject)]
	[InlineData(WindowsUncProject)]
	[InlineData(NtObjectUncProject)]
	[InlineData(@"\\?\UNC\host.invalid\share")]
	public void TheRootRegistryRefusesARemoteFormWithoutResolvingIt(string project)
	{
		using var workspace = new TemporaryDirectory();
		var registry = new McpRootRegistry([workspace.CreateFolder("local")]);

		using var probes = McpProjectPathProbe.Count();
		var refusal = Assert.Throws<McpToolException>(() => registry.ResolveProject(project));

		Assert.Equal(0, probes.Value);
		Assert.Equal(McpErrorCodes.InvalidArguments, refusal.Code);
		Assert.Contains(
			"refused before 'project' is resolved against the allowed roots",
			refusal.Message,
			StringComparison.Ordinal);
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
		var expected = PhysicalPathOf(local);
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
	/// The Win32 device namespace addresses this machine, so an extended-length path to an ordinary
	/// local directory is not a remote form and must keep resolving. It is how a path longer than
	/// <c>MAX_PATH</c> is written, and real Windows tooling emits it.
	/// </summary>
	[Fact]
	public async Task AnExtendedLengthLocalPathStillResolves()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("The extended-length prefix is Windows-only.");

		using var workspace = new TemporaryDirectory();
		var local = workspace.CreateFolder("local");
		var outside = workspace.CreateFolder("outside");
		using var resolver = CreateOfflineResolver(local);

		var resolved = await resolver.ResolveAsync(
			$@"\\?\{local}",
			branch: null,
			TestContext.Current.CancellationToken);
		var unknown = await Assert.ThrowsAsync<McpToolException>(() =>
			resolver.ResolveAsync($@"\\?\{outside}", branch: null, TestContext.Current.CancellationToken));

		Assert.Equal(PhysicalPathOf(local), resolved.Root);
		// Not the refusal: an unlisted local directory is unknown, exactly as it was before.
		Assert.Equal(McpErrorCodes.UnknownProject, unknown.Code);
	}

	/// <summary>
	/// The operator keeps the last word. A root listed at startup stays addressable in any spelling
	/// that resolves to the recorded one, because deciding that is lexical work over the startup
	/// table and opens nothing.
	/// </summary>
	/// <remarks>
	/// A doubled leading separator is the one refused spelling that can be pointed at an ordinary
	/// local directory without a share, which is what makes this runnable. Unix collapses it, so the
	/// canonical comparison is what has to recognise it.
	/// </remarks>
	[Fact]
	public async Task ARootListedAtStartupStaysAddressableUnderAnySpellingOfItself()
	{
		if (OperatingSystem.IsWindows())
			Assert.Skip("A doubled leading separator names a host on Windows, not a local directory.");

		using var workspace = new TemporaryDirectory();
		var local = workspace.CreateFolder("local");
		var doubled = "/" + local;
		Assert.True(McpRemoteProviderPath.ReachesRemoteProvider(doubled));
		using var resolver = CreateOfflineResolver(local);

		var resolved = await resolver.ResolveAsync(
			doubled,
			branch: null,
			TestContext.Current.CancellationToken);

		Assert.Equal(PhysicalPathOf(local), resolved.Root);
	}

	/// <summary>
	/// The trailing separator, the forward-slash spelling and the extended-length spelling of a
	/// listed root all name the same recorded root. This settles the comparison itself, which is
	/// pure, so it needs no share to stand behind the path.
	/// </summary>
	[Fact]
	public void EverySpellingOfAListedRootIsRecognised()
	{
		using var workspace = new TemporaryDirectory();
		var local = workspace.CreateFolder("local");
		var registry = new McpRootRegistry([local]);
		var separator = Path.DirectorySeparatorChar;

		using var probes = McpProjectPathProbe.Count();

		Assert.True(registry.IsConfiguredRootSpelling(local));
		Assert.True(registry.IsConfiguredRootSpelling(local + separator));
		Assert.True(registry.IsConfiguredRootSpelling("  " + local + "  "));
		Assert.True(registry.IsConfiguredRootSpelling(local.Replace(separator, '/')));
		Assert.False(registry.IsConfiguredRootSpelling(Path.Combine(local, "nested")));
		if (OperatingSystem.IsWindows())
			Assert.True(registry.IsConfiguredRootSpelling($@"\\?\{local}"));

		// Deciding any of this opened nothing, which is what allows it to be asked before the shape
		// has been cleared to touch the filesystem.
		Assert.Equal(0, probes.Value);
	}

	private static string PhysicalPathOf(string path) =>
		McpRootJailFileStreamOpener.ResolveDirectoryPath(
			McpRootRegistry.ResolvePhysicalExistingPath(path, requireDirectory: true));

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
