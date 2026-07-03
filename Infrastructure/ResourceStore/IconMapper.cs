using System.Collections.Frozen;

namespace DevProjex.Infrastructure.ResourceStore;

public sealed class IconMapper : IIconMapper
{
	private readonly Lazy<IconMapping> _mapping = new(LoadMapping);

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
			(mapping.GrayFolderNames ?? []).ToFrozenSet(StringComparer.OrdinalIgnoreCase),
			(mapping.ExtensionIcons ?? [])
			.ToFrozenDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase),
			(mapping.FileNameIcons ?? [])
			.ToFrozenDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase));
	}

	private sealed record IconMapping(
		FrozenSet<string> GrayFolderNames,
		FrozenDictionary<string, string> ExtensionToIconKey,
		FrozenDictionary<string, string> FileNameToIconKey);

	private sealed record IconMappingDefinition(
		List<string>? GrayFolderNames,
		Dictionary<string, string>? ExtensionIcons,
		Dictionary<string, string>? FileNameIcons);
}
