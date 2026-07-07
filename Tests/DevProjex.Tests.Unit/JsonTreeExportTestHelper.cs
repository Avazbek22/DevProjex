namespace DevProjex.Tests.Unit;

internal static class JsonTreeExportTestHelper
{
	public static JsonElement GetTree(JsonDocument document)
	{
		var root = document.RootElement;
		Assert.Equal(JsonValueKind.Object, root.ValueKind);
		Assert.True(root.TryGetProperty("tree", out var tree), "JSON tree export must contain top-level 'tree'.");
		Assert.Equal(JsonValueKind.Object, tree.ValueKind);
		return tree;
	}

	public static string[] ExtractFilePaths(JsonElement tree)
	{
		var paths = new List<string>();
		ExtractFilePaths(tree, prefix: string.Empty, paths);
		return paths.ToArray();
	}

	public static int CountFiles(JsonElement tree) => ExtractFilePaths(tree).Length;

	public static bool ContainsFilePath(JsonElement tree, string relativePath)
		=> ExtractFilePaths(tree).Contains(relativePath, StringComparer.Ordinal);

	public static string[] ExtractEmptyFolderPaths(JsonElement tree)
	{
		var paths = new List<string>();
		ExtractEmptyFolderPaths(tree, prefix: string.Empty, paths);
		return paths.ToArray();
	}

	public static void AssertOnlyRootPathAndTree(JsonElement root)
	{
		var propertyNames = root.EnumerateObject().Select(static property => property.Name).ToArray();
		Assert.Equal(["rootPath", "tree"], propertyNames);
	}

	public static void AssertNoLegacyTreeContract(JsonElement root)
	{
		foreach (var forbidden in new[] { "root", "schemaVersion", "version", "format", "selection", "counts", "warnings", "diagnostics" })
			Assert.False(root.TryGetProperty(forbidden, out _), $"Unexpected top-level JSON tree property '{forbidden}'.");

		AssertNoLegacyNodeShape(root);
		AssertJsonTreeStructure(GetTreeFromRoot(root));
	}

	public static void AssertJsonTreeStructure(JsonElement tree) => AssertJsonTreeObject(tree);

	public static string NormalizeJsonPath(string path)
		=> Path.GetFullPath(path).Replace('\\', '/');

	private static void ExtractFilePaths(JsonElement node, string prefix, List<string> paths)
	{
		Assert.Equal(JsonValueKind.Object, node.ValueKind);

		foreach (var property in node.EnumerateObject())
		{
			if (property.Name == "/")
			{
				AppendArrayFiles(property.Value, prefix, paths);
				continue;
			}

			var folderPath = Combine(prefix, property.Name);
			if (property.Value.ValueKind == JsonValueKind.Array)
			{
				AppendArrayFiles(property.Value, folderPath, paths);
			}
			else
			{
				Assert.Equal(JsonValueKind.Object, property.Value.ValueKind);
				ExtractFilePaths(property.Value, folderPath, paths);
			}
		}
	}

	private static void ExtractEmptyFolderPaths(JsonElement node, string prefix, List<string> paths)
	{
		Assert.Equal(JsonValueKind.Object, node.ValueKind);

		foreach (var property in node.EnumerateObject())
		{
			if (property.Name == "/")
				continue;

			var folderPath = Combine(prefix, property.Name);
			if (property.Value.ValueKind == JsonValueKind.Array)
			{
				if (property.Value.GetArrayLength() == 0)
					paths.Add(folderPath);
			}
			else
			{
				Assert.Equal(JsonValueKind.Object, property.Value.ValueKind);
				ExtractEmptyFolderPaths(property.Value, folderPath, paths);
			}
		}
	}

	private static void AppendArrayFiles(JsonElement array, string prefix, List<string> paths)
	{
		Assert.Equal(JsonValueKind.Array, array.ValueKind);
		foreach (var item in array.EnumerateArray())
		{
			Assert.Equal(JsonValueKind.String, item.ValueKind);
			var fileName = item.GetString() ?? string.Empty;
			paths.Add(Combine(prefix, fileName));
		}
	}

	private static void AssertJsonTreeObject(JsonElement node)
	{
		Assert.Equal(JsonValueKind.Object, node.ValueKind);
		foreach (var property in node.EnumerateObject())
		{
			if (property.Name == "/")
			{
				AssertStringArray(property.Value);
				Assert.True(property.Value.GetArrayLength() > 0, "'/' must not be empty.");
				continue;
			}

			if (property.Value.ValueKind == JsonValueKind.Array)
			{
				AssertStringArray(property.Value);
				continue;
			}

			Assert.Equal(JsonValueKind.Object, property.Value.ValueKind);
			AssertJsonTreeObject(property.Value);
		}
	}

	private static void AssertStringArray(JsonElement array)
	{
		Assert.Equal(JsonValueKind.Array, array.ValueKind);
		foreach (var item in array.EnumerateArray())
			Assert.Equal(JsonValueKind.String, item.ValueKind);
	}

	private static void AssertNoLegacyNodeShape(JsonElement element)
	{
		if (element.ValueKind == JsonValueKind.Null)
			throw new Xunit.Sdk.XunitException("JSON tree must not contain null values.");

		if (element.ValueKind != JsonValueKind.Object)
			return;

		if (element.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
		    element.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String)
		{
			throw new Xunit.Sdk.XunitException("Unexpected legacy name/path JSON tree node.");
		}

		if (element.TryGetProperty("accessDenied", out _))
			throw new Xunit.Sdk.XunitException("Unexpected accessDenied scanner metadata in JSON tree export.");

		foreach (var property in element.EnumerateObject())
			AssertNoLegacyNodeShape(property.Value);
	}

	private static JsonElement GetTreeFromRoot(JsonElement root)
		=> root.GetProperty("tree");

	private static string Combine(string prefix, string name)
		=> string.IsNullOrEmpty(prefix) ? name : $"{prefix}/{name}";
}
