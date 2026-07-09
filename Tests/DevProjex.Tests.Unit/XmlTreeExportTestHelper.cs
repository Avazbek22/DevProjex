using System.Xml.Linq;

namespace DevProjex.Tests.Unit;

internal static class XmlTreeExportTestHelper
{
	public static XDocument Parse(string xml)
	{
		Assert.False(xml.TrimStart().StartsWith("<?xml", StringComparison.Ordinal));
		var document = XDocument.Parse(xml);
		Assert.NotNull(document.Root);
		Assert.Equal("t", document.Root!.Name.LocalName);
		return document;
	}

	public static void AssertRootPath(XDocument document, string expectedRootPath)
	{
		var actual = document.Root!.Attribute("r")?.Value;
		Assert.NotNull(actual);
		Assert.Equal(expectedRootPath.Replace('\\', '/'), actual);
		Assert.DoesNotContain("\\", actual!, StringComparison.Ordinal);
	}

	public static void AssertXmlTreeContract(XDocument document)
	{
		Assert.Equal("t", document.Root!.Name.LocalName);
		Assert.Single(document.Root.Attributes());
		Assert.NotNull(document.Root.Attribute("r"));

		foreach (var element in document.Descendants())
		{
			Assert.Contains(element.Name.LocalName, new[] { "t", "d", "f" });
			if (element.Name.LocalName == "d")
			{
				Assert.Single(element.Attributes());
				Assert.NotNull(element.Attribute("n"));
			}
			else if (element.Name.LocalName == "f")
			{
				Assert.Empty(element.Attributes());
				Assert.Empty(element.Elements());
			}
		}
	}

	public static string[] ExtractFilePaths(XDocument document)
	{
		var paths = new List<string>();
		ExtractFilePaths(document.Root!, prefix: string.Empty, paths);
		return paths.ToArray();
	}

	public static string[] ExtractEmptyFolderPaths(XDocument document)
	{
		var paths = new List<string>();
		ExtractEmptyFolderPaths(document.Root!, prefix: string.Empty, paths);
		return paths.ToArray();
	}

	private static void ExtractFilePaths(XElement node, string prefix, List<string> paths)
	{
		foreach (var child in node.Elements())
		{
			if (child.Name.LocalName == "f")
			{
				paths.Add(Combine(prefix, child.Value));
				continue;
			}

			var folderName = child.Attribute("n")?.Value ?? string.Empty;
			ExtractFilePaths(child, Combine(prefix, folderName), paths);
		}
	}

	private static void ExtractEmptyFolderPaths(XElement node, string prefix, List<string> paths)
	{
		foreach (var child in node.Elements("d"))
		{
			var folderName = child.Attribute("n")?.Value ?? string.Empty;
			var folderPath = Combine(prefix, folderName);
			if (!child.Elements().Any())
				paths.Add(folderPath);

			ExtractEmptyFolderPaths(child, folderPath, paths);
		}
	}

	private static string Combine(string prefix, string name)
		=> string.IsNullOrEmpty(prefix) ? name : $"{prefix}/{name}";
}
