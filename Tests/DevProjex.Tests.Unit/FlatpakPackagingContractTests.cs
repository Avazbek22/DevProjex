namespace DevProjex.Tests.Unit;

public sealed class FlatpakPackagingContractTests
{
	[Fact]
	public void NativeSourceModulesDeclareTheirArchiveFormatAndKeepTheSharedLock()
	{
		var root = new DirectoryInfo(AppContext.BaseDirectory);
		while (root is not null && !File.Exists(Path.Combine(root.FullName, "DevProjex.sln"))) root = root.Parent;
		Assert.NotNull(root);
		var folder = Path.Combine(root.FullName, "Packaging", "Linux", "flatpak");
		using var modules = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "vendored-grammars.json")));
		using var sourceLock = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName,
			"Infrastructure", "Grammars", "vendored", "vendored-grammars.lock.json")));
		var grammars = sourceLock.RootElement.GetProperty("grammars").EnumerateArray().ToArray();
		var generated = modules.RootElement.GetProperty("modules").EnumerateArray().ToArray();
		Assert.Equal(4, generated.Length);
		foreach (var module in generated)
		{
			var name = module.GetProperty("name").GetString();
			var original = Assert.Single(grammars, grammar => grammar.GetProperty("name").GetString() == name);
			var source = module.GetProperty("sources")[0];
			Assert.Equal("zip", source.GetProperty("archive-type").GetString());
			Assert.Equal(original.GetProperty("source").GetProperty("archiveSha256").GetString(), source.GetProperty("sha256").GetString());
			Assert.Equal(original.GetProperty("source").GetProperty("archiveUrl").GetString(), source.GetProperty("url").GetString());
			Assert.Contains(module.GetProperty("build-commands").EnumerateArray(), command => command.GetString()!.Contains("-fPIC", StringComparison.Ordinal));
			if (original.TryGetProperty("sourcePatches", out var patches))
				foreach (var patch in patches.EnumerateArray())
					Assert.Contains(patch.GetProperty("path").GetString()!, module.GetRawText());
			if (original.TryGetProperty("sourcePatchArtifacts", out var artifacts))
				foreach (var artifact in artifacts.EnumerateArray())
					Assert.Contains(artifact.GetProperty("resultSha256").GetString()!, module.GetRawText());
		}
		var manifest = File.ReadAllText(Path.Combine(folder, "io.github.Avazbek22.DevProjex.yml"));
		Assert.Contains("NO_RUST=YesPlease", manifest);
		Assert.Contains("INSTALL_SYMLINKS=YesPlease", manifest);
		Assert.DoesNotContain("GTK_USE_PORTAL", manifest);
		Assert.DoesNotContain("--filesystem=host", manifest);
		Assert.DoesNotContain("flatpak-spawn", manifest);
	}
}
