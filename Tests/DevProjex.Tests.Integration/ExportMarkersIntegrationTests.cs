using System;
using System.Collections.Generic;
using System.IO;
using DevProjex.Application.Services;
using DevProjex.Application.UseCases;
using DevProjex.Infrastructure.FileSystem;
using DevProjex.Kernel.Abstractions;
using DevProjex.Kernel.Contracts;
using DevProjex.Kernel.Models;
using DevProjex.Tests.Integration.Helpers;
using Xunit;

namespace DevProjex.Tests.Integration;

public sealed class ExportMarkersIntegrationTests
{
	[Fact]
	public void Export_FullTree_IncludesMarkersAndSkipsBinary()
	{
		using var temp = new TemporaryDirectory();
		var empty = temp.CreateFile("empty.json", string.Empty);
		var whitespace = temp.CreateFile("space.txt", " \n ");
		var binary = Path.Combine(temp.Path, "image.bin");
		File.WriteAllBytes(binary, new byte[] { 1, 2, 0, 3 });
		var text = temp.CreateFile("note.txt", "Hello");

		var root = BuildPresentedTree(temp.Path, ".json", ".txt", ".bin");
		var service = new TreeAndContentExportService(new TreeExportService(), new SelectedContentExportService());

		var output = service.Build(temp.Path, root, new HashSet<string>());

		Assert.Contains($"{empty}:", output);
		Assert.Contains("[No Content, 0 bytes]", output);
		Assert.Contains($"{whitespace}:", output);
		Assert.Contains("[Whitespace, 3 bytes]", output);
		Assert.Contains($"{text}:", output);
		Assert.Contains("Hello", output);
		Assert.DoesNotContain($"{binary}:", output);
	}

	[Fact]
	public void Export_Selected_IncludesMarkersForSelectedFiles()
	{
		using var temp = new TemporaryDirectory();
		var empty = temp.CreateFile("empty.json", string.Empty);
		var text = temp.CreateFile("note.txt", "Hello");

		var root = BuildPresentedTree(temp.Path, ".json", ".txt");
		var service = new TreeAndContentExportService(new TreeExportService(), new SelectedContentExportService());
		var selected = new HashSet<string> { empty };

		var output = service.Build(temp.Path, root, selected);

		Assert.Contains($"{empty}:", output);
		Assert.Contains("[No Content, 0 bytes]", output);
		Assert.DoesNotContain($"{text}:", output);
	}

	[Fact]
	public void Export_Selected_SkipsBinaryContent()
	{
		using var temp = new TemporaryDirectory();
		var binary = Path.Combine(temp.Path, "image.bin");
		File.WriteAllBytes(binary, new byte[] { 1, 2, 0, 3 });

		var root = BuildPresentedTree(temp.Path, ".bin");
		var service = new TreeAndContentExportService(new TreeExportService(), new SelectedContentExportService());
		var selected = new HashSet<string> { binary };

		var output = service.Build(temp.Path, root, selected);

		Assert.Contains("├──", output);
		Assert.DoesNotContain($"{binary}:", output);
	}

	private static TreeNodeDescriptor BuildPresentedTree(string rootPath, params string[] extensions)
	{
		var allowedExtensions = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
		var options = new TreeFilterOptions(
			AllowedExtensions: allowedExtensions,
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			IgnoreRules: new IgnoreRules(
				IgnoreBinFolders: false,
				IgnoreObjFolders: false,
				IgnoreHiddenFolders: false,
				IgnoreHiddenFiles: false,
				IgnoreDotFolders: false,
				IgnoreDotFiles: false,
				SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
				SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

		var treeBuilder = new TreeBuilder();
		var presenter = new TreeNodePresentationService(
			new LocalizationService(new FakeLocalizationCatalog(), AppLanguage.En),
			new FakeIconMapper());
		var useCase = new BuildTreeUseCase(treeBuilder, presenter);

		var result = useCase.Execute(new BuildTreeRequest(rootPath, options));
		return result.Root;
	}

	private sealed class FakeLocalizationCatalog : ILocalizationCatalog
	{
		public IReadOnlyDictionary<string, string> Get(AppLanguage language)
		{
			return new Dictionary<string, string>
			{
				{ "Tree.AccessDeniedRoot", "Access denied" },
				{ "Tree.AccessDenied", "Access denied" }
			};
		}
	}

	private sealed class FakeIconMapper : IIconMapper
	{
		public string GetIconKey(FileSystemNode node) => node.IsDirectory ? "folder" : "file";
	}
}
