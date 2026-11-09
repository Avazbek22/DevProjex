using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.ProjectProfiles;
using DevProjex.Infrastructure.Secrets;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	private const string CacheManualValue = "marked-manual-value-4702";

	[Fact]
	public async Task SearchKeepsTheProtectedBaselineAfterNavigationCachePressure()
	{
		using var workspace = new TemporaryDirectory();
		var project = BuildNavigationPressureProject(workspace);
		var data = workspace.CreateDirectory("data");
		Assert.True(new ProjectProfileStore(() => data).TrySaveProfile(project, new(["src"], [".cs", ".txt", ".dat"], [], SelectedPaths: null)));
		using (var identityProvider = new PersistentSecretIdentityProvider(() => data))
		{
			Assert.Equal(PersistentSecretIdentityAvailability.Ready, await identityProvider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
			Assert.True(PersistentSecretIdentity.TryCreateV2(identityProvider, CacheManualValue, out var identity));
			var source = File.ReadAllText(Path.Combine(project, "src", "ASecrets.cs"));
			var stored = await new ProjectProfileStore(() => data).AddMarkAsync(project,
			 new(identity, null, CacheManualValue.Length, "src/ASecrets.cs", source.IndexOf(CacheManualValue, StringComparison.Ordinal)), TestContext.Current.CancellationToken);
			Assert.True(stored.Succeeded);
		}
		var digests = new List<string>();
		await using (var server = await ActualMcpProcess.StartAsync(project, data, arguments: ["--live"]))
		{
			for (var index = 0; index < 2; index++)
			{
				var result = await CallAsync(server, "search_project", new() { ["pattern"] = "needle", ["max_results"] = 20 });
				Assert.NotEqual(true, result.IsError);
				var text = AllProcessText(result);
				Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
				Assert.DoesNotContain(CacheManualValue, text, StringComparison.Ordinal);
				Assert.Contains("DEVPROJEX_REDACTED[", text, StringComparison.Ordinal);
				Assert.Contains("matches retained=5000/", text, StringComparison.Ordinal);
				digests.Add(CacheResponseDigest(text));
				if (index == 1)
				{
					var pack = Regex.Match(text, @"\[Search stored\] pack_id=(?<id>[0-9a-f]+)", RegexOptions.None, TimeSpan.FromSeconds(2)).Groups["id"].Value;
					Assert.NotEmpty(pack);
					var page = AllProcessText(await CallAsync(server, "read_pack", new() { ["pack_id"] = pack, ["end_line"] = 20 }));
					Assert.DoesNotContain(Secret, page, StringComparison.Ordinal);
					Assert.DoesNotContain(CacheManualValue, page, StringComparison.Ordinal);
					digests.Add(CacheResponseDigest(page, pack));
					var protectedFile = AllProcessText(await CallAsync(server, "get_file", new() { ["path"] = "src/ASecrets.cs" }));
					Assert.Contains("Manual", protectedFile, StringComparison.Ordinal);
					Assert.DoesNotContain(CacheManualValue, protectedFile, StringComparison.Ordinal);
					Assert.DoesNotContain(Secret, protectedFile, StringComparison.Ordinal);
					Assert.True(CountOccurrences(protectedFile, "DEVPROJEX_REDACTED[") >= 2);
				}
			}
		}
		var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
		start.ArgumentList.Add(PublishedApplicationLocator.FindApplicationAssembly());
		foreach (var value in new[] { "search", "needle", project, "--git-mode", "none", "--language", "en", "--plain", "--progress", "never", "--format", "json", "--max", "20", "--hide-secrets", "--profile", "local" }) start.ArgumentList.Add(value);
		start.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = data;
		var cli = TerminalTestProcess.Run(start, TimeSpan.FromMinutes(1));
		Assert.True(cli.ExitCode == 0, cli.StandardError);
		Assert.DoesNotContain(Secret, cli.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain(CacheManualValue, cli.StandardOutput, StringComparison.Ordinal);
		digests.Add(CacheResponseDigest(cli.StandardOutput));
		// Complete protected responses captured before navigation-cache compaction.
		var expected = new[]
		{
			"5A1BEC66EA0ECB800AF35592359BB95ED718D598BC023543AAE3BBACE237929B",
			"856960A05345A9D3E53204D49C63579517AC2C6AF07EC73504D5DB1BC8288888",
			"A0DFC41BBDA9EBAA354DAF8123A485CC5960CA6B1ACEA0BE45F8E1562B5DFEE3",
			"0EFE7F8994C6892D2FA2488073D87854E9A6C600C5F07935323A19DEC35F5354"
		};
		Assert.True(expected.SequenceEqual(digests), string.Join("\n", digests));
	}

	[Fact]
	public async Task SearchUsesChangedRenamedAndDeletedContentAfterCachingNavigation()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Control.txt", "ordinary content");
		var path = workspace.WriteFile("project/Initial.cs", "class First { string Name = \"old-cache-marker\"; }\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		var first = AllProcessText(await CallAsync(server, "search_project", new() { ["pattern"] = "old-cache-marker" }));
		Assert.Contains("Initial.cs", first, StringComparison.Ordinal);
		var stamp = File.GetLastWriteTimeUtc(path);
		var length = new FileInfo(path).Length;
		File.WriteAllText(path, "class Other { string Name = \"new-cache-marker\"; }\n");
		File.SetLastWriteTimeUtc(path, stamp);
		Assert.Equal(length, new FileInfo(path).Length);
		var changed = AllProcessText(await CallAsync(server, "search_project", new() { ["pattern"] = "new-cache-marker" }));
		Assert.Contains("Other", changed, StringComparison.Ordinal);
		var renamed = Path.Combine(project, "Renamed.cs");
		File.Move(path, renamed);
		var moved = AllProcessText(await CallAsync(server, "search_project", new() { ["pattern"] = "new-cache-marker" }));
		Assert.Contains("Renamed.cs", moved, StringComparison.Ordinal);
		Assert.DoesNotContain("Initial.cs", moved, StringComparison.Ordinal);
		File.Delete(renamed);
		var deleted = AllProcessText(await CallAsync(server, "search_project", new() { ["pattern"] = "new-cache-marker" }));
		Assert.Contains("[No matches]", deleted, StringComparison.Ordinal);
		Assert.DoesNotContain("Renamed.cs", deleted, StringComparison.Ordinal);
	}

	private static string BuildNavigationPressureProject(TemporaryDirectory workspace)
	{
		var root = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/ASecrets.cs", "public class SecretValues {\n public const string Automatic = \"" + Secret + "\"; // needle\n public const string Manual = \"" + CacheManualValue + "\"; // needle\n}\n");
		var prefix = "LargeNamespace" + new string('N', 80);
		for (var file = 0; file < 50; file++)
		{
			var text = new StringBuilder("namespace " + prefix + ";\npublic class Container" + file + " {\n");
			for (var member = 0; member < 300; member++) text.Append(" public string Value").Append(member).Append(" => \"needle\";\n");
			workspace.WriteFile($"project/src/File{file:D3}.cs", text.Append("}\n").ToString());
		}
		workspace.WriteFile("project/src/Empty.txt", "");
		workspace.WriteFile("project/src/Boundary.txt", new string('x', 16383) + "needle\n終λ😀needle\r\nlast needle\r");
		workspace.WriteFile("project/src/Long.txt", new string('x', 200_000) + "needle\ntail\n");
		File.WriteAllBytes(Path.Combine(root, "src", "Binary.dat"), [0, 255, 0, 1]);
		return root;
	}

	private static string CacheResponseDigest(string text, string? packId = null)
	{
		text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
		text = Regex.Replace(text, @"(<\/?untrusted-data-)[0-9a-f]{24}(>)", "$1NONCE$2", RegexOptions.None, TimeSpan.FromSeconds(2));
		text = Regex.Replace(text, @"(?<=pack_id=)[0-9a-f]{48,64}", "PACK", RegexOptions.None, TimeSpan.FromSeconds(2));
		if (packId is not null) text = text.Replace(packId, "PACK", StringComparison.Ordinal);
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
	}
}
