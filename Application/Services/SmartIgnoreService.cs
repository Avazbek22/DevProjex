namespace DevProjex.Application.Services;

public sealed class SmartIgnoreService
{
	private readonly IReadOnlyList<ISmartIgnoreRule> _rules;

	public SmartIgnoreService(IEnumerable<ISmartIgnoreRule> rules)
	{
		_rules = rules.ToList();
	}

	public SmartIgnoreResult Build(string rootPath)
	{
		var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var rule in _rules)
		{
			SmartIgnoreResult result;
			try
			{
				result = rule.Evaluate(rootPath);
			}
			catch
			{
				// Fault isolation: one problematic rule or inaccessible folder
				// must not break the whole ignore pipeline.
				continue;
			}

			foreach (var folder in result.FolderNames)
				folders.Add(folder);
			foreach (var file in result.FileNames)
				files.Add(file);
		}

		return new SmartIgnoreResult(folders, files);
	}
}
