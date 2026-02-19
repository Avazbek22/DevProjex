namespace DevProjex.Tests.Unit;

public sealed class DotNetArtifactsIgnoreRuleTests : IDisposable
{
	private readonly string _tempRoot;

	public DotNetArtifactsIgnoreRuleTests()
	{
		_tempRoot = Path.Combine(Path.GetTempPath(), $"dotnet-artifacts-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempRoot);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempRoot))
			Directory.Delete(_tempRoot, recursive: true);
	}

	[Fact]
	public void Evaluate_WithSlnFile_ReturnsBinAndObj()
	{
		File.WriteAllText(Path.Combine(_tempRoot, "Test.sln"), "");
		var rule = new DotNetArtifactsIgnoreRule();

		var result = rule.Evaluate(_tempRoot);

		Assert.Contains("bin", result.FolderNames, StringComparer.OrdinalIgnoreCase);
		Assert.Contains("obj", result.FolderNames, StringComparer.OrdinalIgnoreCase);
	}

	[Fact]
	public void Evaluate_WithCsprojFile_ReturnsBinAndObj()
	{
		File.WriteAllText(Path.Combine(_tempRoot, "Test.csproj"), "");
		var rule = new DotNetArtifactsIgnoreRule();

		var result = rule.Evaluate(_tempRoot);

		Assert.Contains("bin", result.FolderNames, StringComparer.OrdinalIgnoreCase);
		Assert.Contains("obj", result.FolderNames, StringComparer.OrdinalIgnoreCase);
	}

	[Fact]
	public void Evaluate_WithFsprojFile_ReturnsBinAndObj()
	{
		File.WriteAllText(Path.Combine(_tempRoot, "Test.fsproj"), "");
		var rule = new DotNetArtifactsIgnoreRule();

		var result = rule.Evaluate(_tempRoot);

		Assert.Contains("bin", result.FolderNames, StringComparer.OrdinalIgnoreCase);
		Assert.Contains("obj", result.FolderNames, StringComparer.OrdinalIgnoreCase);
	}

	[Fact]
	public void Evaluate_WithVbprojFile_ReturnsBinAndObj()
	{
		File.WriteAllText(Path.Combine(_tempRoot, "Test.vbproj"), "");
		var rule = new DotNetArtifactsIgnoreRule();

		var result = rule.Evaluate(_tempRoot);

		Assert.Contains("bin", result.FolderNames, StringComparer.OrdinalIgnoreCase);
		Assert.Contains("obj", result.FolderNames, StringComparer.OrdinalIgnoreCase);
	}

	[Fact]
	public void Evaluate_WithoutDotNetMarkers_ReturnsEmpty()
	{
		// No .sln, .csproj, etc - just a random folder
		File.WriteAllText(Path.Combine(_tempRoot, "readme.txt"), "");
		var rule = new DotNetArtifactsIgnoreRule();

		var result = rule.Evaluate(_tempRoot);

		Assert.Empty(result.FolderNames);
		Assert.Empty(result.FileNames);
	}

	[Fact]
	public void Evaluate_WithPackageJson_ReturnsEmpty()
	{
		// Node.js project, not .NET
		File.WriteAllText(Path.Combine(_tempRoot, "package.json"), "{}");
		var rule = new DotNetArtifactsIgnoreRule();

		var result = rule.Evaluate(_tempRoot);

		Assert.Empty(result.FolderNames);
	}

	[Fact]
	public void Evaluate_NonExistentPath_ReturnsEmpty()
	{
		var rule = new DotNetArtifactsIgnoreRule();

		var result = rule.Evaluate("/nonexistent/path/12345");

		Assert.Empty(result.FolderNames);
		Assert.Empty(result.FileNames);
	}

	[Fact]
	public void Evaluate_CsprojInSubdirectory_ReturnsEmpty()
	{
		// .csproj in subdirectory should NOT trigger the rule
		// (rule only checks root level)
		var subdir = Path.Combine(_tempRoot, "src");
		Directory.CreateDirectory(subdir);
		File.WriteAllText(Path.Combine(subdir, "Test.csproj"), "");
		var rule = new DotNetArtifactsIgnoreRule();

		var result = rule.Evaluate(_tempRoot);

		Assert.Empty(result.FolderNames);
	}
}
