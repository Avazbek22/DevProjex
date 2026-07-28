namespace DevProjex.Tests.Terminal;

public sealed class ProjectSourceIdentityContractTests
{
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
		var markdown = await services.ContextDocumentService.BuildAsync(
			plan,
			ProjectContextView.TreeContent,
			ProjectContextDocumentFormat.Markdown,
			TestContext.Current.CancellationToken);
		var json = await services.ContextDocumentService.BuildAsync(
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
}
