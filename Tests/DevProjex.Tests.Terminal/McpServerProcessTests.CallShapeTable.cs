using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Theory]
	[MemberData(nameof(NormalizedCallShapes))]
	public async Task RealProcessAcceptsEachSupportedCallShape(int ordinal, string tool, string json)
	{
		TestContext.Current.TestOutputHelper?.WriteLine($"Call shape {ordinal}");
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("shape-project");
		var source = "public class Demo {\n public void Run() { }\n}\n" + string.Concat(Enumerable.Repeat("// line\n", 10_000));
		for (var index = 0; index < 16; index++)
		{
			workspace.WriteFile($"shape-project/file{index}.cs", source);
			workspace.WriteFile($"shape-project/folder{index}/seed.cs", source);
		}
		using var document = JsonDocument.Parse(json);
		var arguments = document.RootElement.EnumerateObject().ToDictionary(static field => field.Name, static field => (object?)field.Value.Clone());
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		var result = await CallAsync(server, tool, arguments);
		Assert.NotEqual(true, result.IsError);
		var text = AllProcessText(result);
		if (tool == "get_file") Assert.Contains("File:", text, StringComparison.Ordinal);
		if (tool == "related_files") Assert.Contains("[Read as]", text, StringComparison.Ordinal);
		if (tool == "search_project") Assert.Contains(DevProjex.Mcp.McpSearchSymbols.RegularExpressionFallbackNotice, text, StringComparison.Ordinal);
	}

	public static IEnumerable<object[]> NormalizedCallShapes()
	{
		yield return new object[] { 1, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\"],\"start_line\":1,\"end_line\":50}" };
		yield return new object[] { 2, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\"],\"end_line\":50}" };
		yield return new object[] { 3, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\"],\"start_line\":1,\"end_line\":5}" };
		yield return new object[] { 4, "search_project", "{\"pattern\":\"^export\\\\s+(type\\\\s+)?(?:class|interface|function|const|async\\\\s+function|{\\\\s*|.*?=)\",\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\",\"file5.cs\"],\"symbols\":true,\"max_results\":50}" };
		yield return new object[] { 5, "search_project", "{\"pattern\":\"^export\\\\s+(type\\\\s+)?GetConnInfo\",\"paths\":\"file0.cs\",\"symbols\":true}" };
		yield return new object[] { 6, "search_project", "{\"pattern\":\"^export\\\\s+(const|function)\\\\s+serveStatic\",\"paths\":\"file0.cs\",\"symbols\":true}" };
		yield return new object[] { 7, "search_project", "{\"pattern\":\"^export\\\\s+(const|function)\\\\s+toSSG|^export\\\\s+type\\\\s+FileSystemModule\",\"paths\":\"file0.cs\",\"symbols\":true}" };
		yield return new object[] { 8, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\"],\"start_line\":1,\"end_line\":50}" };
		yield return new object[] { 9, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\"],\"start_line\":1,\"end_line\":50}" };
		yield return new object[] { 10, "search_project", "{\"pattern\":\"export\\\\s+(?:type\\\\s+)?(?:class\\\\s+)?(?:interface\\\\s+)?(Context|GetConnInfo|MiddlewareHandler|Env|FileSystemModule|ToSSGAdaptorInterface|UpgradeWebSocket|WSEvents|WSMessageReceive|createWSMessageEvent|defineWebSocketHelper|WSContext)\",\"symbols\":true,\"max_results\":20,\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\",\"file5.cs\"]}" };
		yield return new object[] { 11, "search_project", "{\"pattern\":\"(BarColumn|PercentageColumn|SpinnerColumn|TimeRemainingColumn)\",\"symbols\":true,\"max_results\":20}" };
		yield return new object[] { 12, "get_file", "{\"path\":\"file0.cs\",\"requests\":[{\"path\":\"file0.cs\",\"symbol\":\"Demo.Run\"},{\"path\":\"file0.cs\",\"symbol\":\"Demo.Run\"}]}" };
		yield return new object[] { 13, "search_project", "{\"pattern\":\"class \\\\w+Column\",\"paths\":\"file0.cs\",\"symbols\":true}" };
		yield return new object[] { 14, "get_file", "{\"path\":\"file0.cs\",\"requests\":[{\"path\":\"file0.cs\",\"ranges\":[{\"start_line\":688,\"end_line\":697}]},{\"path\":\"file0.cs\",\"ranges\":[{\"start_line\":772,\"end_line\":817}]},{\"path\":\"file0.cs\",\"ranges\":[{\"start_line\":507,\"end_line\":546}]}]}" };
		yield return new object[] { 15, "search_project", "{\"pattern\":\"func Test.*ClientIP\",\"symbols\":true,\"max_results\":20}" };
		yield return new object[] { 16, "get_file", "{\"paths\":\"file0.cs\",\"start_line\":1495,\"end_line\":1570}" };
		yield return new object[] { 17, "search_project", "{\"pattern\":\"func TestContext.*ClientIP|func Test.*ForwardedByClientIP\",\"max_results\":20,\"symbols\":true}" };
		yield return new object[] { 18, "search_project", "{\"pattern\":\"func Test.*ClientIP\",\"symbols\":true}" };
		yield return new object[] { 19, "search_project", "{\"pattern\":\"def request\\\\(|def get\\\\(|def post\\\\(|def put\\\\(|def delete\\\\(|def patch\\\\(|def head\\\\(|def options\\\\(\",\"symbols\":true}" };
		yield return new object[] { 20, "search_project", "{\"pattern\":\"def test.*request|def test.*follow_redirect|def test.*timeout\",\"include_patterns\":[\"**\"],\"symbols\":true,\"max_results\":30}" };
		yield return new object[] { 21, "search_project", "{\"pattern\":\"def test.*request\\\\(|def test.*stream\\\\(\",\"paths\":\"file0.cs\",\"symbols\":true,\"max_results\":20}" };
		yield return new object[] { 22, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\"],\"start_line\":1,\"end_line\":50}" };
		yield return new object[] { 23, "search_project", "{\"pattern\":\"export.*GetConnInfo|GetConnInfo.*=\",\"paths\":\"file0.cs\",\"symbols\":true}" };
		yield return new object[] { 24, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\"],\"end_line\":1}" };
		yield return new object[] { 25, "get_file", "{\"paths\":[\"file0.cs\"],\"start_line\":1,\"end_line\":100}" };
		yield return new object[] { 26, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\"],\"start_line\":1,\"end_line\":1}" };
		yield return new object[] { 27, "get_file", "{\"paths\":\"file0.cs\",\"start_line\":1,\"end_line\":100}" };
		yield return new object[] { 28, "get_file", "{\"paths\":\"file0.cs\",\"start_line\":1,\"end_line\":5}" };
		yield return new object[] { 29, "get_file", "{\"paths\":\"file0.cs\",\"start_line\":1,\"end_line\":5}" };
		yield return new object[] { 30, "get_file", "{\"paths\":\"file0.cs\",\"start_line\":1,\"end_line\":5}" };
		yield return new object[] { 31, "related_files", "{\"path\":\"folder0\",\"direction\":\"dependencies\"}" };
		yield return new object[] { 32, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\"],\"start_line\":1,\"end_line\":50}" };
		yield return new object[] { 33, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\",\"file5.cs\"],\"start_line\":1,\"end_line\":50}" };
		yield return new object[] { 34, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\"],\"start_line\":1,\"end_line\":30}" };
		yield return new object[] { 35, "search_project", "{\"pattern\":\"^export (type |interface |const |class )?.*Context\",\"paths\":\"file0.cs\",\"symbols\":true}" };
		yield return new object[] { 36, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\"],\"start_line\":1,\"end_line\":30}" };
		yield return new object[] { 37, "related_files", "{\"path\":\"folder0\",\"direction\":\"dependencies\"}" };
		yield return new object[] { 38, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\"],\"start_line\":1,\"end_line\":60}" };
		yield return new object[] { 39, "search_project", "{\"pattern\":\"fn json_content_type|struct.*Json.*\\\\{|impl.*FromRequest.*for.*Json\",\"max_results\":20,\"symbols\":true}" };
		yield return new object[] { 40, "search_project", "{\"pattern\":\"def test.*auth|def test.*header\",\"context_lines\":0,\"symbols\":true,\"include_patterns\":\"**\"}" };
		yield return new object[] { 41, "search_project", "{\"pattern\":\"def request\\\\(\",\"symbols\":true}" };
		yield return new object[] { 42, "search_project", "{\"pattern\":\"class Client.*:|def __init__.*auth.*params\",\"include_patterns\":[\"**\"],\"context_lines\":5,\"symbols\":true}" };
		yield return new object[] { 43, "search_project", "{\"pattern\":\"test_.*merge\",\"symbols\":true}" };
		yield return new object[] { 44, "search_project", "{\"pattern\":\"json\\\\s*\\\\([^)]*\\\\)\\\\s*[:{]\",\"symbols\":true,\"context_lines\":5,\"include_patterns\":\"**\",\"exclude_patterns\":\"missing/**\"}" };
		yield return new object[] { 45, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\"],\"end_line\":80}" };
		yield return new object[] { 46, "search_project", "{\"pattern\":\"^export\",\"paths\":\"file0.cs\",\"symbols\":true,\"max_results\":20}" };
		yield return new object[] { 47, "search_project", "{\"pattern\":\"public class \\\\w+ValidatorTests\",\"include_patterns\":\"**\",\"symbols\":true,\"max_results\":15}" };
		yield return new object[] { 48, "search_project", "{\"pattern\":\"class \\\\w+\\\\(ProgressColumn\\\\)\",\"symbols\":true,\"max_results\":20}" };
		yield return new object[] { 49, "search_project", "{\"pattern\":\"class.*Column.*:\",\"include_patterns\":\"**\",\"symbols\":true}" };
		yield return new object[] { 50, "search_project", "{\"pattern\":\"test_.*progress\",\"include_patterns\":\"**\",\"symbols\":true,\"max_results\":5}" };
		yield return new object[] { 51, "search_project", "{\"pattern\":\"class\\\\s+\\\\w+\\\\(ProgressColumn\\\\)\",\"symbols\":true}" };
		yield return new object[] { 52, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\",\"file5.cs\",\"file6.cs\",\"file7.cs\",\"file8.cs\"]}" };
		yield return new object[] { 53, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\",\"file5.cs\",\"file6.cs\",\"file7.cs\",\"file8.cs\"]}" };
		yield return new object[] { 54, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\",\"file5.cs\",\"file6.cs\",\"file7.cs\",\"file8.cs\"]}" };
		yield return new object[] { 55, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\",\"file5.cs\"],\"start_line\":45,\"end_line\":100}" };
		yield return new object[] { 56, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\",\"file5.cs\",\"file6.cs\",\"file7.cs\",\"file8.cs\"]}" };
		yield return new object[] { 57, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\"],\"start_line\":1,\"end_line\":150}" };
		yield return new object[] { 58, "get_file", "{\"paths\":[\"file1.cs\",\"file0.cs\",\"file2.cs\",\"file3.cs\"],\"requests\":[{\"path\":\"file0.cs\",\"symbol\":\"Demo.Run\"},{\"path\":\"file1.cs\",\"symbol\":\"Demo.Run\"},{\"path\":\"file2.cs\",\"symbol\":\"Demo.Run\"},{\"path\":\"file0.cs\",\"symbol\":\"Demo.Run\"}]}" };
		yield return new object[] { 59, "get_file", "{\"paths\":[\"file1.cs\",\"file2.cs\",\"file1.cs\"],\"requests\":[{\"path\":\"file0.cs\",\"ranges\":[{\"start_line\":960,\"end_line\":1035}]},{\"path\":\"file1.cs\",\"ranges\":[{\"start_line\":120,\"end_line\":165},{\"start_line\":195,\"end_line\":235}]},{\"path\":\"file2.cs\",\"ranges\":[{\"start_line\":1120,\"end_line\":1320}]}]}" };
		yield return new object[] { 60, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\"],\"requests\":[{\"path\":\"file0.cs\",\"ranges\":[{\"start_line\":970,\"end_line\":1035}]},{\"path\":\"file1.cs\",\"ranges\":[{\"start_line\":120,\"end_line\":230},{\"start_line\":400,\"end_line\":470}]},{\"path\":\"file2.cs\",\"ranges\":[{\"start_line\":1080,\"end_line\":1250}]}]}" };
		yield return new object[] { 61, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\",\"file5.cs\",\"file6.cs\",\"file7.cs\",\"file8.cs\",\"file9.cs\"]}" };
		yield return new object[] { 62, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\",\"file5.cs\"],\"start_line\":35,\"end_line\":115}" };
		yield return new object[] { 63, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\",\"file5.cs\"],\"start_line\":1,\"end_line\":30}" };
		yield return new object[] { 64, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\",\"file5.cs\"],\"start_line\":1,\"end_line\":70}" };
		yield return new object[] { 65, "related_files", "{\"path\":\"folder0\",\"direction\":\"dependencies\",\"include_patterns\":[\"**\"]}" };
		yield return new object[] { 66, "related_files", "{\"path\":\"folder0\",\"direction\":\"dependencies\"}" };
		yield return new object[] { 67, "related_files", "{\"path\":\"folder0\",\"direction\":\"dependencies\"}" };
		yield return new object[] { 68, "related_files", "{\"path\":\"folder0\",\"direction\":\"dependencies\",\"include_patterns\":\"**\"}" };
		yield return new object[] { 69, "related_files", "{\"path\":\"folder0\",\"direction\":\"dependencies\"}" };
		yield return new object[] { 70, "related_files", "{\"path\":\"folder0\",\"direction\":\"dependencies\",\"include_patterns\":\"**\"}" };
		yield return new object[] { 71, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\",\"file5.cs\",\"file6.cs\",\"file7.cs\",\"file8.cs\"]}" };
		yield return new object[] { 72, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\"],\"requests\":[{\"path\":\"file0.cs\",\"symbol\":\"Demo.Run\"},{\"path\":\"file0.cs\",\"symbol\":\"Demo.Run\"},{\"path\":\"file1.cs\",\"symbol\":\"Demo.Run\"}]}" };
		yield return new object[] { 73, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\",\"file5.cs\",\"file6.cs\",\"file7.cs\",\"file8.cs\"]}" };
		yield return new object[] { 74, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\"],\"start_line\":1,\"end_line\":500}" };
		yield return new object[] { 75, "search_project", "{\"pattern\":\"get_default_columns|def __init__\\\\(\",\"include_patterns\":[\"**\"],\"symbols\":true,\"max_results\":12,\"context_lines\":1}" };
		yield return new object[] { 76, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\",\"file5.cs\",\"file6.cs\",\"file7.cs\",\"file8.cs\"]}" };
		yield return new object[] { 77, "search_project", "{\"pattern\":\"def _merge_(headers|cookies|queryparams)|def __init__\",\"paths\":[\"file0.cs\"],\"symbols\":true,\"max_results\":20}" };
		yield return new object[] { 78, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\",\"file5.cs\"],\"start_line\":1,\"end_line\":110}" };
		yield return new object[] { 79, "get_file", "{\"paths\":[\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file0.cs\"],\"requests\":[{\"path\":\"file0.cs\",\"ranges\":[{\"start_line\":800,\"end_line\":865},{\"start_line\":930,\"end_line\":960}]}]}" };
		yield return new object[] { 80, "get_file", "{\"paths\":[\"file1.cs\",\"file2.cs\",\"file3.cs\"],\"requests\":[{\"path\":\"file0.cs\",\"ranges\":[{\"start_line\":800,\"end_line\":865},{\"start_line\":930,\"end_line\":960}]}]}" };
		yield return new object[] { 81, "get_file", "{\"paths\":[\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\"],\"requests\":[{\"path\":\"file0.cs\",\"ranges\":[{\"start_line\":68,\"end_line\":98},{\"start_line\":430,\"end_line\":457}]}]}" };
		yield return new object[] { 82, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\",\"file5.cs\",\"file6.cs\",\"file7.cs\",\"file8.cs\"]}" };
		yield return new object[] { 83, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\"],\"start_line\":1,\"end_line\":150}" };
		yield return new object[] { 84, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\"],\"start_line\":1,\"end_line\":200}" };
		yield return new object[] { 85, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\",\"file5.cs\",\"file6.cs\",\"file7.cs\",\"file8.cs\",\"file9.cs\",\"file10.cs\",\"file11.cs\",\"file12.cs\"]}" };
		yield return new object[] { 86, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\",\"file5.cs\",\"file6.cs\",\"file7.cs\",\"file8.cs\",\"file9.cs\"]}" };
		yield return new object[] { 87, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\",\"file5.cs\",\"file6.cs\",\"file7.cs\",\"file8.cs\",\"file9.cs\",\"file10.cs\",\"file11.cs\"]}" };
		yield return new object[] { 88, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\",\"file5.cs\",\"file6.cs\",\"file7.cs\",\"file8.cs\"]}" };
		yield return new object[] { 89, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\",\"file5.cs\",\"file6.cs\",\"file7.cs\",\"file8.cs\"]}" };
		yield return new object[] { 90, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\",\"file5.cs\",\"file6.cs\",\"file7.cs\",\"file8.cs\",\"file9.cs\",\"file10.cs\",\"file11.cs\"]}" };
		yield return new object[] { 91, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\",\"file5.cs\",\"file6.cs\",\"file7.cs\",\"file8.cs\"]}" };
		yield return new object[] { 92, "get_file", "{\"requests\":[{\"path\":\"file0.cs\"},{\"path\":\"file1.cs\"},{\"path\":\"file2.cs\",\"ranges\":[{\"start_line\":55,\"end_line\":95}]},{\"path\":\"file3.cs\",\"ranges\":[{\"start_line\":88,\"end_line\":98}]},{\"path\":\"file4.cs\"},{\"path\":\"file5.cs\",\"ranges\":[{\"start_line\":140,\"end_line\":160}]},{\"path\":\"file6.cs\",\"ranges\":[{\"start_line\":44,\"end_line\":54}]},{\"path\":\"file7.cs\",\"ranges\":[{\"start_line\":52,\"end_line\":70}]},{\"path\":\"file8.cs\",\"ranges\":[{\"start_line\":50,\"end_line\":66}]}]}" };
		yield return new object[] { 93, "get_file", "{\"requests\":[{\"path\":\"file0.cs\"},{\"path\":\"file1.cs\"},{\"path\":\"file2.cs\",\"ranges\":[{\"start_line\":18,\"end_line\":90}]},{\"path\":\"file3.cs\",\"ranges\":[{\"start_line\":85,\"end_line\":100}]},{\"path\":\"file4.cs\",\"ranges\":[{\"start_line\":50,\"end_line\":62}]},{\"path\":\"file5.cs\",\"ranges\":[{\"start_line\":142,\"end_line\":159}]},{\"path\":\"file6.cs\",\"ranges\":[{\"start_line\":53,\"end_line\":66}]},{\"path\":\"file7.cs\",\"ranges\":[{\"start_line\":44,\"end_line\":53}]},{\"path\":\"file8.cs\",\"ranges\":[{\"start_line\":55,\"end_line\":67}]}]}" };
		yield return new object[] { 94, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\",\"file5.cs\",\"file6.cs\"],\"start_line\":1,\"end_line\":100}" };
		yield return new object[] { 95, "related_files", "{\"path\":\"folder0\",\"direction\":\"dependencies\"}" };
		yield return new object[] { 96, "related_files", "{\"path\":\"folder0\",\"direction\":\"dependencies\",\"include_patterns\":\"**\"}" };
		yield return new object[] { 97, "related_files", "{\"path\":\"folder0\",\"direction\":\"dependencies\"}" };
		yield return new object[] { 98, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\"],\"start_line\":1,\"end_line\":20}" };
		yield return new object[] { 99, "search_project", "{\"pattern\":\"test_.*(headers|cookies|queryparams|build_request)\",\"paths\":[\"file0.cs\",\"file1.cs\"],\"symbols\":true,\"max_results\":40}" };
		yield return new object[] { 100, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\",\"file2.cs\",\"file3.cs\",\"file4.cs\"],\"start_line\":1,\"end_line\":220}" };
		yield return new object[] { 101, "get_file", "{\"paths\":[\"file0.cs\",\"file1.cs\"],\"start_line\":1,\"end_line\":110}" };
		yield return new object[] { 102, "get_file", "{\"requests\":[{\"path\":\"file0.cs\"},{\"path\":\"file1.cs\"},{\"path\":\"file2.cs\"},{\"path\":\"file3.cs\"},{\"path\":\"file4.cs\"},{\"path\":\"file5.cs\"},{\"path\":\"file6.cs\"},{\"path\":\"file7.cs\"},{\"path\":\"file8.cs\"}]}" };
	}
}
