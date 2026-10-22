using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

public sealed class FlatpakDeliveryContractTests
{
	[Fact]
	public void PermissionsAreExactlyTheDocumentedSandboxBoundary()
	{
		var manifest = File.ReadAllText(Path.Combine(Root(), "Packaging/Linux/flatpak/io.github.Avazbek22.DevProjex.yml"));
		var section = manifest.Split("finish-args:", StringSplitOptions.None)[1].Split("build-options:", StringSplitOptions.None)[0];
		var permissions = section.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("- ", StringComparison.Ordinal)).Select(line => line[2..]).ToArray();
		Assert.Equal(new[] { "--filesystem=home", "--socket=x11", "--share=ipc", "--device=dri", "--share=network", "--talk-name=com.canonical.AppMenu.Registrar" }, permissions);
		Assert.Contains("global menu", section);
		foreach (var forbidden in new[] { "--filesystem=host", "flatpak-spawn", "org.freedesktop.Flatpak", "wayland", "GTK_USE_PORTAL" })
			Assert.DoesNotContain(forbidden, manifest, StringComparison.OrdinalIgnoreCase);
		Assert.Matches(@"sha256: [a-f0-9]{64}\s", manifest);
		using var grammars = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "Packaging/Linux/flatpak/vendored-grammars.json")));
		foreach (var module in grammars.RootElement.GetProperty("modules").EnumerateArray())
			foreach (var source in module.GetProperty("sources").EnumerateArray())
				if (source.GetProperty("type").GetString() == "archive") Assert.Matches("^[a-f0-9]{64}$", source.GetProperty("sha256").GetString()!);
	}

	[Fact]
	public void WorkflowUsesBothNativeArchitecturesAndReadOnlyTieredTriggers()
	{
		var workflow = File.ReadAllText(Path.Combine(Root(), ".github/workflows/flatpak-build.yml"));
		Assert.Contains("branches-ignore: [master]", workflow);
		Assert.Contains("branches: [master, 'v*']", workflow);
		Assert.Contains("workflow_dispatch:", workflow);
		Assert.Contains("workflow_call:", workflow);
		Assert.Contains("checkout_ref:", workflow);
		Assert.Contains("contents: read", workflow);
		Assert.DoesNotContain("contents: write", workflow);
		Assert.Contains("arch: x86_64", workflow);
		Assert.Contains("arch: aarch64", workflow);
		Assert.Contains("runner: ubuntu-24.04-arm", workflow);
		Assert.Contains("Packaging/Linux/flatpak/smoke.sh", workflow);
		Assert.Contains("flatpak/flatpak-github-actions/flatpak-builder@", workflow);
		var triggers = workflow.Split("permissions:", StringSplitOptions.None)[0];
		Assert.DoesNotContain("'Apps/**'", triggers);
		Assert.DoesNotContain("'Infrastructure/**'", triggers);
		var candidate = File.ReadAllText(Path.Combine(Root(), ".github/workflows/release-candidate.yml"));
		Assert.Contains("uses: ./.github/workflows/flatpak-build.yml", candidate);
	}

	[Fact]
	public void ContainerOwnsTheCheckoutAndHomeBeforeGitOrSandboxedDotnetRun()
	{
		var workflow = File.ReadAllText(Path.Combine(Root(), ".github/workflows/flatpak-build.yml"));
		var checkout = workflow.IndexOf("uses: actions/checkout@", StringComparison.Ordinal);
		var ownership = workflow.IndexOf("chown -R \"$(id -u):$(id -g)\" \"$GITHUB_WORKSPACE\" \"$HOME\"", StringComparison.Ordinal);
		var trust = workflow.IndexOf("git config --global --add safe.directory \"$GITHUB_WORKSPACE\"", StringComparison.Ordinal);
		// prepare-source.sh runs git archive; regenerate-nuget.sh runs dotnet restore inside a flatpak sandbox.
		var firstGitUse = workflow.IndexOf("Packaging/Linux/flatpak/prepare-source.sh", StringComparison.Ordinal);
		var firstSandboxedDotnet = workflow.IndexOf("Packaging/Linux/flatpak/regenerate-nuget.sh", StringComparison.Ordinal);

		Assert.Contains("container:", workflow);
		Assert.True(checkout >= 0 && ownership > checkout, "The ownership step must follow the checkout.");
		Assert.True(trust > ownership, "Git configuration is written to HOME after HOME belongs to the container user.");
		Assert.True(firstGitUse > trust, "The checkout must be trusted before prepare-source.sh runs git archive.");
		Assert.True(firstSandboxedDotnet > ownership, "Sandboxed dotnet needs a writable HOME and checkout.");
		Assert.Contains("git archive", File.ReadAllText(Path.Combine(Root(), "Packaging/Linux/flatpak/prepare-source.sh")));
	}

	private static string Root()
	{
		var root = new DirectoryInfo(AppContext.BaseDirectory);
		while (root is not null && !File.Exists(Path.Combine(root.FullName, "DevProjex.sln"))) root = root.Parent;
		Assert.NotNull(root);
		return root.FullName;
	}
}
