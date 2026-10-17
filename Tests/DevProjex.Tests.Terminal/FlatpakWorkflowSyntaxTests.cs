namespace DevProjex.Tests.Terminal;

public sealed class FlatpakWorkflowSyntaxTests
{
	[Fact]
	public void FoldedWorkflowLintCommandHasUniformIndentationAndOnlyExistingInputs()
	{
		var root = new DirectoryInfo(AppContext.BaseDirectory);
		while (root is not null && !File.Exists(Path.Combine(root.FullName, ".github", "workflows", "dotnet.yml")))
			root = root.Parent;
		Assert.NotNull(root);
		var lines = File.ReadAllLines(Path.Combine(root.FullName, ".github", "workflows", "dotnet.yml"));
		var step = Array.FindIndex(lines, line => line.Trim() == "- name: Lint GitHub Actions workflows");
		Assert.True(step >= 0);
		var start = Array.FindIndex(lines, step, line => line.Trim() == "run: >") + 1;
		Assert.True(start > step);
		var body = lines.Skip(start).TakeWhile(line => !string.IsNullOrWhiteSpace(line)).ToArray();
		Assert.NotEmpty(body);
		var indent = body[0].Length - body[0].TrimStart().Length;
		Assert.All(body, line => Assert.Equal(indent, line.Length - line.TrimStart().Length));
		Assert.Equal("go run github.com/rhysd/actionlint/cmd/actionlint@v1.7.7", body[0].Trim());
		Assert.Equal("-shellcheck=", body[1].Trim());
		var workflows = body.Skip(2).Select(line => line.Trim()).ToArray();
		Assert.Contains(".github/workflows/flatpak-build.yml", workflows);
		Assert.Equal(workflows.Length, workflows.Distinct(StringComparer.Ordinal).Count());
		Assert.All(workflows, workflow => Assert.True(File.Exists(Path.Combine(root.FullName, workflow)), workflow));
	}
}
