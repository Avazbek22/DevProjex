using System.Diagnostics;
using System.Text.Json;

namespace DependencyFactsSpike;

internal static class CbmComparatorCapture
{
	private static readonly (string Kind, string Query)[] Queries =
	[
		("IMPORTS", "MATCH (a)-[r:IMPORTS]->(b) RETURN DISTINCT a.file_path AS source, b.file_path AS target"),
		("USAGE", "MATCH (a)-[r:USAGE]->(b) RETURN DISTINCT a.file_path AS source, b.file_path AS target")
	];

	public static CbmEdgeSet Capture(string executable, string cache, string project)
	{
		var edges = new List<CbmFileEdge>();
		var notes = new List<string>();
		foreach (var (kind, query) in Queries)
		{
			var output = Invoke(executable, cache,
				["cli", "--json", "query_graph", "--project", project, "--query", query, "--max-rows", "100000"]);
			using var envelope = JsonDocument.Parse(output);
			var text = envelope.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
			foreach (var line in text.Split('\n'))
			{
				if (!line.StartsWith("  ", StringComparison.Ordinal))
					continue;
				var columns = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
				if (columns.Length != 2)
				{
					notes.Add($"Could not parse {kind} row: {line.Trim()}");
					continue;
				}
				var source = columns[0].Replace('\\', '/');
				var target = columns[1].Replace('\\', '/');
				if (source != target)
					edges.Add(new CbmFileEdge(kind, source, target));
			}
			if (text.Contains("100000", StringComparison.Ordinal))
				notes.Add($"{kind} may have reached the CBM 100000-row ceiling.");
		}
		return new CbmEdgeSet(
			"0.10.8",
			project,
			edges.Distinct().OrderBy(static edge => edge.Kind, StringComparer.Ordinal)
				.ThenBy(static edge => edge.Source, StringComparer.Ordinal)
				.ThenBy(static edge => edge.Target, StringComparer.Ordinal)
				.ToArray(),
			notes);
	}

	private static string Invoke(string executable, string cache, IReadOnlyList<string> arguments)
	{
		var start = new ProcessStartInfo(executable)
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		start.Environment["CBM_CACHE_DIR"] = cache;
		foreach (var argument in arguments)
			start.ArgumentList.Add(argument);
		using var process = Process.Start(start) ?? throw new InvalidOperationException("CBM process did not start.");
		var stdout = process.StandardOutput.ReadToEndAsync();
		var stderr = process.StandardError.ReadToEndAsync();
		process.WaitForExit();
		Task.WaitAll(stdout, stderr);
		if (process.ExitCode != 0)
			throw new InvalidOperationException($"CBM exited with {process.ExitCode}: {stdout.Result.Trim()} {stderr.Result.Trim()}");
		return stdout.Result;
	}
}
