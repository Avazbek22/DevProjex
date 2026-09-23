using DevProjex.Mcp;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	[Fact(Timeout = 60_000)]
	public async Task ListProjectsDoesNotChangeTheRevisionOfAnInFlightLiveSearch()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var oldFolder = Path.Combine(project, "old");
		var newFolder = Path.Combine(project, "new");
		Directory.CreateDirectory(oldFolder);
		Directory.CreateDirectory(newFolder);
		File.WriteAllText(Path.Combine(oldFolder, "Old.cs"), "class Old { string marker = \"needle\"; }\n");
		File.WriteAllText(Path.Combine(newFolder, "New.cs"), "class New {}\n");
		var profiles = new ProjectProfileStore(() => Path.Combine(workspace.Path, "app-data"));
		profiles.SaveProfile(project, new ProjectSelectionProfile([], [], [], SelectedPaths: ["old"]));
		await using var server = await McpTestServer.StartAsync(project, workspace.Path, live: true);
		var scanEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var releaseScan = new ManualResetEventSlim();
		McpSearchExecutionHooks.AfterScan = path =>
		{
			if (path != "old/Old.cs")
				return;
			scanEntered.TrySetResult();
			if (!releaseScan.Wait(TimeSpan.FromSeconds(20)))
				throw new TimeoutException("The test did not release the search scan.");
		};
		try
		{
			var search = server.CallAsync("search_project", new Dictionary<string, object?>
			{
				["pattern"] = "needle",
				["context_lines"] = 0
			});
			await scanEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
			profiles.SaveProfile(project, new ProjectSelectionProfile([], [], [], SelectedPaths: ["new"]));
			using var canceledCallToken = CancellationTokenSource.CreateLinkedTokenSource(
				TestContext.Current.CancellationToken);
			var canceledCall = server.Client.CallToolAsync(
				"list_projects",
				new Dictionary<string, object?>(),
				cancellationToken: canceledCallToken.Token).AsTask();
			Assert.NotSame(
				canceledCall,
				await Task.WhenAny(
					canceledCall,
					Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken)));
			canceledCallToken.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledCall);

			var list = server.CallAsync("list_projects");
			var listCompletedDuringSearch = await Task.WhenAny(
				list,
				Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)) == list;
			releaseScan.Set();

			var searchText = AllText(await search.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
			var listText = AllText(await list.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
			Assert.False(listCompletedDuringSearch);
			Assert.Contains("Old.cs", searchText, StringComparison.Ordinal);
			Assert.Contains("[Live context] revision 1 · 1 files selected", searchText, StringComparison.Ordinal);
			Assert.Contains("[Live context] revision 2 · 1 files selected", listText, StringComparison.Ordinal);
		}
		finally
		{
			releaseScan.Set();
			McpSearchExecutionHooks.AfterScan = null;
		}
	}
}
