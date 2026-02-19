namespace DevProjex.Infrastructure.ResourceStore;

public sealed class IconMapper : IIconMapper
{
	private readonly Lazy<IconMapping> _mapping;

	public IconMapper()
	{
		_mapping = new Lazy<IconMapping>(LoadMapping);
	}

	public string GetIconKey(FileSystemNode node)
	{
		var mapping = _mapping.Value;

		if (node.IsDirectory)
		{
			if (node.IsAccessDenied || mapping.GrayFolderNames.Contains(node.Name))
				return "grayFolder";

			return "folder";
		}

		var fileName = node.Name;
		if (mapping.FileNameToIconKey.TryGetValue(fileName, out var fileIcon))
			return fileIcon;

		var ext = Path.GetExtension(fileName);
		if (!string.IsNullOrWhiteSpace(ext) && mapping.ExtensionToIconKey.TryGetValue(ext, out var icon))
			return icon;

		return "unknownFile";
	}

	private static IconMapping LoadMapping()
	{
		var assembly = typeof(Marker).Assembly;
		var resourceName = "DevProjex.Assets.IconPacks.Configuration.mapping.json";
		using var stream = assembly.GetManifestResourceStream(resourceName)
			?? throw new InvalidOperationException($"Icon mapping not found: {resourceName}");

		var mapping = JsonSerializer.Deserialize<IconMappingDefinition>(stream, new JsonSerializerOptions
		{
			PropertyNameCaseInsensitive = true
		}) ?? throw new InvalidOperationException("Icon mapping is empty.");

		return new IconMapping(
			new HashSet<string>(mapping.GrayFolderNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase),
			new Dictionary<string, string>(mapping.ExtensionIcons ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
			new Dictionary<string, string>(mapping.FileNameIcons ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase));
	}

	private sealed record IconMapping(
		HashSet<string> GrayFolderNames,
		Dictionary<string, string> ExtensionToIconKey,
		Dictionary<string, string> FileNameToIconKey);

	private sealed record IconMappingDefinition(
		List<string>? GrayFolderNames,
		Dictionary<string, string>? ExtensionIcons,
		Dictionary<string, string>? FileNameIcons);
}
