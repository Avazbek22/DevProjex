namespace DevProjex.Tests.Terminal;

public sealed class ProjectSourceIdentityContractTests
{
	[Fact]
	public async Task LocalIdentityPreservesWhitespaceOnlyPosixProjectName()
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip("Windows does not support this directory name through ordinary APIs.");
			return;
		}

		using var temporary = new TemporaryDirectory();
		var projectPath = temporary.CreateDirectory(" ");
		var services = new TerminalServiceFactory(() => temporary.Path).Create(AppLanguage.En);

		var identity = await services.SourceIdentityResolver.ResolveAsync(
			projectPath,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(" ", identity.DisplayName);
	}

	[Fact]
	public async Task ContextPlanPreservesWhitespaceOnlyUnixProjectName()
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip("Windows does not support this directory name through ordinary APIs.");
			return;
		}

		using var temporary = new TemporaryDirectory();
		var projectPath = temporary.CreateDirectory(" ");
		temporary.WriteFile(" /App.cs", "class App {}\n");
		var services = new TerminalServiceFactory(() => temporary.Path).Create(AppLanguage.En);
		var selection = await services.SelectionResolver.ResolveAsync(
			projectPath,
			ProjectProfileReference.Standard,
			new ProjectSelectionSpec(),
			TestContext.Current.CancellationToken);

		var plan = await services.ContextPlanner.BuildAsync(
			new ProjectContextRequest(projectPath, selection),
			TestContext.Current.CancellationToken);

		var identity = Assert.IsType<ProjectSourceIdentity>(plan.SourceIdentity);
		Assert.Equal(" ", identity.DisplayName);
		Assert.Equal(" ", plan.EffectiveTree.DisplayName);
		Assert.Equal(" ", plan.ProjectedTree.DisplayName);
	}

	[Fact]
	public async Task CloneCacheSuffixNeverLeaksIntoTreeOrContextDocuments()
	{
		using var temporary = new TemporaryDirectory();
		var dataRoot = temporary.CreateDirectory("Data");
		var cachePath = temporary.CreateDirectory(
			"Data/RepoCache/DevProjex_8DEEC71CEE019B1");
		temporary.WriteFile(
			"Data/RepoCache/DevProjex_8DEEC71CEE019B1/src/App.cs",
			"namespace DevProjex;");
		var services = new TerminalServiceFactory(() => dataRoot).Create(AppLanguage.En);
		var selection = await services.SelectionResolver.ResolveAsync(
			cachePath,
			ProjectProfileReference.Standard,
			new ProjectSelectionSpec(),
			TestContext.Current.CancellationToken);
		var identity = ProjectSourceIdentityResolver.CreateCloneIdentity(
			"https://github.com/Avazbek22/DevProjex",
			"DevProjex",
			"main",
			"0123456789abcdef");

		var plan = await services.ContextPlanner.BuildAsync(
			new ProjectContextRequest(cachePath, selection, identity),
			TestContext.Current.CancellationToken);
		var markdown = await CompleteContextDocumentTestHelper.BuildAsync(
			services.ContextDocumentService,
			plan,
			ProjectContextView.TreeContent,
			ProjectContextDocumentFormat.Markdown,
			TestContext.Current.CancellationToken);
		var json = await CompleteContextDocumentTestHelper.BuildAsync(
			services.ContextDocumentService,
			plan,
			ProjectContextView.Tree,
			ProjectContextDocumentFormat.Json,
			TestContext.Current.CancellationToken);

		Assert.Equal("DevProjex", plan.EffectiveTree.DisplayName);
		Assert.Equal("DevProjex", plan.ProjectedTree.DisplayName);
		Assert.StartsWith("# DevProjex", markdown, StringComparison.Ordinal);
		Assert.Contains(
			"https://github.com/Avazbek22/DevProjex",
			json,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"DevProjex_8DEEC71CEE019B1",
			markdown,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"DevProjex_8DEEC71CEE019B1",
			json,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task ContextDocumentsNeverExposeRepositoryCredentialsFromCallerIdentity()
	{
		const string unsafeUrl = "https://user:top-secret@example.com/owner/repository.git";
		const string safeUrl = "https://example.com/owner/repository.git";
		using var temporary = new TemporaryDirectory();
		temporary.WriteFile("src/App.cs", "class App {}\n");
		var services = new TerminalServiceFactory(() => temporary.Path).Create(AppLanguage.En);
		var selection = await services.SelectionResolver.ResolveAsync(
			temporary.Path,
			ProjectProfileReference.Standard,
			new ProjectSelectionSpec(),
			TestContext.Current.CancellationToken);
		var unsafeIdentity = new ProjectSourceIdentity(
			"repository",
			ProjectSourceType.GitClone,
			unsafeUrl,
			unsafeUrl);

		var plan = await services.ContextPlanner.BuildAsync(
			new ProjectContextRequest(temporary.Path, selection, unsafeIdentity),
			TestContext.Current.CancellationToken);

		Assert.Equal(safeUrl, plan.SourceIdentity?.SourceReference);
		Assert.Equal(safeUrl, plan.SourceIdentity?.RepositoryUrl);
		foreach (var format in Enum.GetValues<ProjectContextDocumentFormat>())
		{
			var document = await CompleteContextDocumentTestHelper.BuildAsync(
				services.ContextDocumentService,
				plan,
				ProjectContextView.TreeContent,
				format,
				TestContext.Current.CancellationToken);

			Assert.DoesNotContain("top-secret", document, StringComparison.Ordinal);
			Assert.DoesNotContain("user:", document, StringComparison.Ordinal);
			Assert.Contains(safeUrl, document, StringComparison.Ordinal);
		}
	}
}
