namespace DevProjex.Application.Services;

public sealed class SmartIgnoreService
{
	private readonly IReadOnlyList<ISmartIgnoreRule> _rules;
	private readonly IReadOnlyList<SmartIgnoreRuleDescriptor> _descriptors;

	public SmartIgnoreService(IEnumerable<ISmartIgnoreRule> rules)
	{
		var ruleList = rules.ToList();
		_rules = ruleList;
		_descriptors = ruleList
			.OfType<ISmartIgnoreRuleDescriptorProvider>()
			.Select(provider => provider.Descriptor)
			.ToArray();
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

	public bool HasKnownProjectMarker(string rootPath)
	{
		if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
			return false;

		foreach (var descriptor in _descriptors)
		{
			foreach (var markerFile in descriptor.MarkerFiles)
			{
				try
				{
					if (File.Exists(Path.Combine(rootPath, markerFile)))
						return true;
				}
				catch
				{
					// Marker probing is best-effort and must never break loading.
				}
			}

			if (descriptor.MarkerExtensions.Count == 0)
				continue;

			try
			{
				foreach (var filePath in Directory.EnumerateFiles(rootPath, "*", SearchOption.TopDirectoryOnly))
				{
					var extension = Path.GetExtension(filePath);
					if (!string.IsNullOrWhiteSpace(extension) && descriptor.MarkerExtensions.Contains(extension))
						return true;
				}
			}
			catch
			{
				// Continue with other descriptors.
			}
		}

		return false;
	}
}
