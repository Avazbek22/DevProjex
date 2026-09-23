using DevProjex.Mcp;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task PrivateRootPolicyProtectsTreeAndActionableReadHints(bool hidePrivateData)
	{
		var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (string.IsNullOrWhiteSpace(userProfile))
			Assert.Skip("The environment does not expose a user profile directory.");

		var project = Path.Combine(userProfile, "DevProjexMcpMetadata-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(project);
		try
		{
			var physicalProject = McpRootRegistry.ResolvePhysicalExistingPath(project, requireDirectory: true);
			var protectedProject = OutputRootPathPresentation.MaskLocalUserSegment(physicalProject);
			if (protectedProject == physicalProject)
				Assert.Skip("The user profile path does not use a supported local-user layout.");

			File.WriteAllText(
				Path.Combine(project, "App.cs"),
				"namespace P;\npublic sealed class App\n{\n" +
				"    string First() { return \"body-marker-a\"; }\n" +
				"    string Second() { return \"body-marker-b\"; }\n}\n");
			File.WriteAllText(Path.Combine(project, "Long.txt"), new string('x', 60_000) + "\n");
			using var workspace = new TemporaryDirectory();
			var otherProject = workspace.CreateDirectory("other-project");
			await using var server = await McpTestServer.StartAsync(
				[project, otherProject],
				workspace.Path,
				hidePrivateData);

			var projectArgument = new Dictionary<string, object?> { ["project"] = "#1" };
			var tree = await server.CallAsync("get_tree", new Dictionary<string, object?>(projectArgument)
			{
				["format"] = "text"
			});
			var batch = await server.CallAsync("get_file", new Dictionary<string, object?>(projectArgument)
			{
				["requests"] = new object[]
				{
					new
					{
						path = "Long.txt",
						ranges = new[] { new { start_line = 1, end_line = 1 } }
					}
				}
			});
			var search = await server.CallAsync("search_project", new Dictionary<string, object?>(projectArgument)
			{
				["pattern"] = "body-marker-(a|b)",
				["context_lines"] = 0
			});

			foreach (var result in new[] { tree, batch, search })
				Assert.NotEqual(true, result.IsError);
			Assert.Contains("Best declaration body", Text(search), StringComparison.Ordinal);
			Assert.Contains("[Batch continuation]", Text(batch), StringComparison.Ordinal);
			if (hidePrivateData)
			{
				Assert.Contains(protectedProject, Text(tree), StringComparison.Ordinal);
				foreach (var result in new[] { tree, batch, search })
					Assert.DoesNotContain(physicalProject, Text(result), StringComparison.Ordinal);
				Assert.Contains("\"project\":\"#1\"", Text(batch), StringComparison.Ordinal);
				Assert.Contains("get_file {\"project\":\"#1\"", Text(search), StringComparison.Ordinal);
			}
			else
			{
				Assert.Contains(physicalProject, Text(tree), StringComparison.Ordinal);
				Assert.Contains(JsonSerializer.Serialize(physicalProject), Text(batch), StringComparison.Ordinal);
				Assert.Contains(JsonSerializer.Serialize(physicalProject), Text(search), StringComparison.Ordinal);
			}
		}
		finally
		{
			if (Directory.Exists(project))
				Directory.Delete(project, recursive: true);
		}
	}
}
