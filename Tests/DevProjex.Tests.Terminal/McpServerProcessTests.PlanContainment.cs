namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	/// <summary>
	/// The first plan of a session confirms every included file inside the root in one pass over
	/// shared directories. Through the real binary, files at every depth stay searchable, a sibling
	/// folder outside the root never appears, and a secret is masked on the first and the cached
	/// call alike.
	/// </summary>
	[Fact]
	public async Task RealProcessFirstPlanKeepsNestedFilesSearchableAndSecretsMasked()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("containment-project");
		for (var directory = 0; directory < 3; directory++)
		{
			for (var file = 0; file < 4; file++)
			{
				workspace.WriteFile(
					$"containment-project/src/area{directory}/deep/nested/File{file}.cs",
					$"// containment-marker area{directory} file{file}\nclass File{directory}{file} {{ }}\n");
			}
		}
		workspace.WriteFile(
			"containment-project/config/settings.cs",
			$"// containment-marker settings\nvar token = \"{Secret}\";\n");
		workspace.WriteFile("containment-sibling/src/Escaped.cs", "// containment-marker escaped\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var tree = await server.Client.CallToolAsync(
			"get_tree",
			new Dictionary<string, object?> { ["format"] = "text" },
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var arguments = new Dictionary<string, object?>
		{
			["pattern"] = "containment-marker|token",
			["context_lines"] = 0,
			["max_results"] = 50
		};
		var first = await server.Client.CallToolAsync(
			"search_project",
			arguments,
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var repeated = await server.Client.CallToolAsync(
			"search_project",
			arguments,
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);

		Assert.NotEqual(true, tree.IsError);
		Assert.Contains("File3.cs", AllProcessText(tree), StringComparison.Ordinal);
		foreach (var result in new[] { first, repeated })
		{
			var text = AllProcessText(result);
			Assert.NotEqual(true, result.IsError);
			for (var directory = 0; directory < 3; directory++)
			{
				for (var file = 0; file < 4; file++)
				{
					Assert.Contains(
						$"src/area{directory}/deep/nested/File{file}.cs",
						text,
						StringComparison.Ordinal);
				}
			}
			Assert.Contains("config/settings.cs", text, StringComparison.Ordinal);
			Assert.Contains("DEVPROJEX_REDACTED[", text, StringComparison.Ordinal);
			Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
			Assert.DoesNotContain("Escaped.cs", text, StringComparison.Ordinal);
			Assert.DoesNotContain("containment-marker escaped", text, StringComparison.Ordinal);
		}
	}
}
