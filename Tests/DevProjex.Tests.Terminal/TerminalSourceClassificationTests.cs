using DevProjex.Mcp;
using DevProjex.Terminal.Execution;

namespace DevProjex.Tests.Terminal;

/// <summary>
/// The command line decides what a project argument is before opening it, so that naming a host
/// cannot make the classification itself contact that host.
/// </summary>
/// <remarks>
/// The proof is the same one the server-side resolver carries: every place that hands a path to the
/// operating system reports itself through <see cref="McpProjectPathProbe"/>, and these cases count
/// those reports rather than inferring anything from a result. No host is named that could answer —
/// <c>.invalid</c> is reserved by RFC 2606 — and nothing waits.
/// </remarks>
public sealed class TerminalSourceClassificationTests
{
	[Theory]
	[InlineData(@"\\host.invalid\share\repository")]
	[InlineData("//host.invalid/share/repository")]
	[InlineData(@"\\?\UNC\host.invalid\share")]
	[InlineData(@"\??\UNC\host.invalid\share")]
	[InlineData(@"\\.\pipe\..\UNC\host.invalid\share")]
	[InlineData("/net/host.invalid/share")]
	public async Task APathThatNamesAHostIsClassifiedWithoutOpeningIt(string source)
	{
		using var data = new TemporaryDirectory();
		var resolver = CreateResolver(data);

		using var probes = McpProjectPathProbe.Count();
		await using var resolved = await resolver.ResolveAsync(
			source,
			branch: null,
			TestContext.Current.CancellationToken);

		Assert.Equal(0, probes.Value);
		// Settled as a local path, exactly as it was before: the probe only ever told an existing
		// directory from an scp-style remote, and for these forms it never changed the answer.
		Assert.False(resolved.IsRepositoryUrl);
		Assert.Equal(source, resolved.ProjectPath);
	}

	/// <summary>
	/// An ordinary path is still opened to classify it. Without this the cases above would be
	/// satisfied by a resolver that had stopped probing altogether, which would prove nothing.
	/// </summary>
	[Fact]
	public async Task AnOrdinaryPathIsStillProbed()
	{
		using var data = new TemporaryDirectory();
		var resolver = CreateResolver(data);

		using var probes = McpProjectPathProbe.Count();
		await using var resolved = await resolver.ResolveAsync(
			Path.Combine(data.Path, "project"),
			branch: null,
			TestContext.Current.CancellationToken);

		Assert.True(probes.Value > 0, "Classifying an ordinary path no longer opens it, so the cases that assert nothing was opened prove nothing.");
		Assert.False(resolved.IsRepositoryUrl);
	}

	/// <summary>
	/// A url is settled by its scheme before anything is opened, as it always was.
	/// </summary>
	[Fact]
	public async Task AUrlIsClassifiedBeforeAnythingIsOpened()
	{
		using var data = new TemporaryDirectory();
		var resolver = CreateResolver(data);

		using var probes = McpProjectPathProbe.Count();
		var failure = await Assert.ThrowsAsync<TerminalProjectSourceException>(() =>
			resolver.ResolveAsync(
				"http://host.invalid/team/repository.git",
				branch: null,
				TestContext.Current.CancellationToken));

		// Recognised as a url and refused for its transport, which happens before any clone and
		// without the classifier opening anything.
		Assert.Equal(0, probes.Value);
		Assert.Equal("DPX-CLI-GIT-URL-INVALID", failure.Code);
	}

	/// <summary>
	/// An scp-style source keeps being read as a repository rather than as a path.
	/// </summary>
	[Fact]
	public async Task AnScpStyleSourceIsStillARepository()
	{
		using var data = new TemporaryDirectory();
		var resolver = CreateResolver(data);

		var failure = await Assert.ThrowsAsync<TerminalProjectSourceException>(() =>
			resolver.ResolveAsync(
				"git@host.invalid:team/repository.git",
				branch: "not a branch",
				TestContext.Current.CancellationToken));

		// The branch is rejected, which is only reached once the source has been accepted as a
		// supported repository url.
		Assert.Equal("DPX-CLI-GIT-BRANCH-INVALID", failure.Code);
	}

	private static TerminalProjectSourceResolver CreateResolver(TemporaryDirectory data) =>
		new(
			new TerminalServiceFactory(() => data.Path).Create(AppLanguage.En),
			new TestTerminalEnvironment(),
			new TerminalOutputOptions());
}
