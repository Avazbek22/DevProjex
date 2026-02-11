using System.Collections.Generic;
using DevProjex.Application.Services;
using DevProjex.Application.UseCases;
using DevProjex.Kernel.Contracts;
using DevProjex.Kernel.Models;
using DevProjex.Tests.Unit.Helpers;
using Xunit;

namespace DevProjex.Tests.Unit;

public sealed class BuildTreeUseCaseTests
{
	// Verifies the use case returns a presented tree with icon mapping applied.
	[Fact]
	public void Execute_ReturnsPresentedTree()
	{
		var treeBuilder = new StubTreeBuilder
		{
			Result = new TreeBuildResult(
				new FileSystemNode("root", "/root", true, false, new List<FileSystemNode>()),
				RootAccessDenied: false,
				HadAccessDenied: false)
		};

		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>()
		});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		var presenter = new TreeNodePresentationService(localization, new StubIconMapper { IconKey = "folder" });

		var useCase = new BuildTreeUseCase(treeBuilder, presenter);

		var result = useCase.Execute(new BuildTreeRequest("/root", new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(),
			AllowedRootFolders: new HashSet<string>(),
			IgnoreRules: new IgnoreRules(IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
				IgnoreDotFiles: false,
				SmartIgnoredFolders: new HashSet<string>(),
				SmartIgnoredFiles: new HashSet<string>()))));

		Assert.Equal("root", result.Root.DisplayName);
		Assert.Equal("folder", result.Root.IconKey);
	}

	// Verifies access denied flags are forwarded from the tree build result.
	[Fact]
	public void Execute_ForwardsAccessDeniedFlags()
	{
		var treeBuilder = new StubTreeBuilder
		{
			Result = new TreeBuildResult(
				new FileSystemNode("root", "/root", true, false, new List<FileSystemNode>()),
				RootAccessDenied: true,
				HadAccessDenied: true)
		};

		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>()
		});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		var presenter = new TreeNodePresentationService(localization, new StubIconMapper { IconKey = "folder" });

		var useCase = new BuildTreeUseCase(treeBuilder, presenter);

		var result = useCase.Execute(new BuildTreeRequest("/root", new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(),
			AllowedRootFolders: new HashSet<string>(),
			IgnoreRules: new IgnoreRules(IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
				IgnoreDotFiles: false,
				SmartIgnoredFolders: new HashSet<string>(),
				SmartIgnoredFiles: new HashSet<string>()))));

		Assert.True(result.RootAccessDenied);
		Assert.True(result.HadAccessDenied);
	}
}




