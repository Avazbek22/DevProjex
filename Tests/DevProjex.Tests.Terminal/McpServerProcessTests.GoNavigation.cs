using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task GoPackageVariableSearchSelectorRoundTripsToTheExactDeclaration()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("go-navigation-project");
		workspace.WriteFile(
			"go-navigation-project/flags.go",
			"""
			package cobra

			var (
				EnablePrefixMatching = false
				OtherFlag = true
			)

			func configure() {
				EnablePrefixMatching := true
				_ = EnablePrefixMatching
			}
			""");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var search = Normalize(AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?>
			{
				["pattern"] = "EnablePrefixMatching",
				["context_lines"] = 0
			})));
		var selector = Regex.Match(
			search,
			@"^flags\.go (?<symbol>EnablePrefixMatching) (?<start>[0-9]+)-(?<end>[0-9]+)$",
			RegexOptions.Multiline,
			TimeSpan.FromSeconds(2));
		Assert.True(selector.Success, search);
		Assert.Contains("get_file {\"path\":\"flags.go\",\"symbol\":\"EnablePrefixMatching\"}", search,
			StringComparison.Ordinal);
		Assert.Contains("EnablePrefixMatching = false", search, StringComparison.Ordinal);

		var file = Normalize(AllProcessText(await CallAsync(
			server,
			"get_file",
			new Dictionary<string, object?>
			{
				["path"] = "flags.go",
				["symbol"] = selector.Groups["symbol"].Value
			})));
		Assert.Contains(
			$"Lines: {selector.Groups["start"].Value}-{selector.Groups["end"].Value} of ",
			file,
			StringComparison.Ordinal);
		Assert.Contains("EnablePrefixMatching = false", file, StringComparison.Ordinal);
		Assert.DoesNotContain("EnablePrefixMatching := true", file, StringComparison.Ordinal);
	}
}
