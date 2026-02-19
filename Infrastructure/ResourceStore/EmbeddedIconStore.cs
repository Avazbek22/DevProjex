namespace DevProjex.Infrastructure.ResourceStore;

public sealed class EmbeddedIconStore : IIconStore
{
	private readonly Lazy<IconPack> _pack;

	public EmbeddedIconStore()
	{
		_pack = new Lazy<IconPack>(LoadPack);
	}

	public IReadOnlyCollection<string> Keys => _pack.Value.IconMap.Keys;

	public byte[] GetIconBytes(string key)
	{
		if (!_pack.Value.IconMap.TryGetValue(key, out var resourceName))
			throw new KeyNotFoundException($"Icon not found: {key}");

		using var stream = _pack.Value.Assembly.GetManifestResourceStream(resourceName)
			?? throw new InvalidOperationException($"Icon resource not found: {resourceName}");
		using var ms = new MemoryStream();
		stream.CopyTo(ms);
		return ms.ToArray();
	}

	private static IconPack LoadPack()
	{
		var assembly = typeof(Marker).Assembly;
		var manifestResource = "DevProjex.Assets.IconPacks.Configuration.manifest.json";
		using var stream = assembly.GetManifestResourceStream(manifestResource)
			?? throw new InvalidOperationException($"Icon manifest not found: {manifestResource}");

		var manifest = JsonSerializer.Deserialize<IconManifest>(stream, new JsonSerializerOptions
		{
			PropertyNameCaseInsensitive = true
		})
			?? throw new InvalidOperationException("Icon manifest is empty.");

		var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var entry in manifest.Icons)
		{
			var resourceName = $"DevProjex.Assets.IconPacks.Default.{entry.Value}";
			map[entry.Key] = resourceName;
		}

		return new IconPack(assembly, map);
	}

	private sealed record IconManifest(Dictionary<string, string> Icons);

	private sealed record IconPack(Assembly Assembly, Dictionary<string, string> IconMap);
}
