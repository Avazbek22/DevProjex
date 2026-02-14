using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DevProjex.Application.Services;
using DevProjex.Application.UseCases;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.ViewModels;
using DevProjex.Infrastructure.ResourceStore;
using DevProjex.Kernel.Models;
using DevProjex.Tests.Unit.Helpers;
using Xunit;

namespace DevProjex.Tests.Unit;

public sealed class SelectionSyncCoordinatorExtensionlessMatrixTests
{
	[Theory]
	[MemberData(nameof(ExtensionScanCases))]
	public void ApplyExtensionScan_FiltersExtensionlessFromUiAndControlsIgnoreOption(
		string[] scanEntries,
		string[] expectedVisibleEntries,
		bool expectExtensionlessIgnoreOption)
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel, @"C:\Temp\Project");

		coordinator.ApplyExtensionScan(scanEntries);
		coordinator.PopulateIgnoreOptionsForRootSelection(Array.Empty<string>(), @"C:\Temp\Project");

		var visible = viewModel.Extensions.Select(option => option.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
		Assert.Equal(expectedVisibleEntries.Length, visible.Count);
		foreach (var entry in expectedVisibleEntries)
			Assert.Contains(entry, visible);

		Assert.DoesNotContain(viewModel.Extensions, option => IsExtensionlessEntry(option.Name));

		var hasExtensionlessOption = viewModel.IgnoreOptions.Any(option => option.Id == IgnoreOptionId.ExtensionlessFiles);
		Assert.Equal(expectExtensionlessIgnoreOption, hasExtensionlessOption);
	}

	public static IEnumerable<object[]> ExtensionScanCases()
	{
		yield return new object[] { new[] { ".cs", ".md" }, new[] { ".cs", ".md" }, false };
		yield return new object[] { new[] { "Dockerfile", ".cs" }, new[] { ".cs" }, true };
		yield return new object[] { new[] { "Dockerfile", "Makefile" }, Array.Empty<string>(), true };
		yield return new object[] { new[] { ".env", ".cs" }, new[] { ".env", ".cs" }, false };
		yield return new object[] { new[] { ".gitignore", "README", ".txt" }, new[] { ".gitignore", ".txt" }, true };
		yield return new object[] { new[] { "LICENSE", ".json", ".yml" }, new[] { ".json", ".yml" }, true };
		yield return new object[] { new[] { ".axaml", ".cs", ".json" }, new[] { ".axaml", ".cs", ".json" }, false };
		yield return new object[] { new[] { "WORKSPACE", ".csproj", ".sln" }, new[] { ".csproj", ".sln" }, true };
		yield return new object[] { new[] { ".dockerignore", "Jenkinsfile", ".yaml" }, new[] { ".dockerignore", ".yaml" }, true };
		yield return new object[] { new[] { ".rules", ".props", ".targets" }, new[] { ".rules", ".props", ".targets" }, false };
		yield return new object[] { new[] { "Taskfile", ".txt", ".log", ".md" }, new[] { ".txt", ".log", ".md" }, true };
		yield return new object[] { new[] { ".env", ".gitignore", ".editorconfig" }, new[] { ".env", ".gitignore", ".editorconfig" }, false };
	}

	private static SelectionSyncCoordinator CreateCoordinator(MainWindowViewModel viewModel, string currentPath)
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		var scanner = new StubFileSystemScanner();
		var scanOptions = new ScanOptionsUseCase(scanner);
		var filterSelectionService = new FilterOptionSelectionService();
		var ignoreOptionsService = new IgnoreOptionsService(localization);

		return new SelectionSyncCoordinator(
			viewModel,
			scanOptions,
			filterSelectionService,
			ignoreOptionsService,
			(rootPath, _, _) => new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()),
			(rootPath, _) => new IgnoreOptionsAvailability(IncludeGitIgnore: false, IncludeSmartIgnore: false),
			_ => false,
			() => currentPath);
	}

	private static MainWindowViewModel CreateViewModel()
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		return new MainWindowViewModel(localization, new HelpContentProvider());
	}

	private static StubLocalizationCatalog CreateCatalog()
	{
		var data = new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Settings.Ignore.SmartIgnore"] = "Smart ignore",
				["Settings.Ignore.UseGitIgnore"] = "Use .gitignore",
				["Settings.Ignore.HiddenFolders"] = "Hidden folders",
				["Settings.Ignore.HiddenFiles"] = "Hidden files",
				["Settings.Ignore.DotFolders"] = "dot folders",
				["Settings.Ignore.DotFiles"] = "dot files",
				["Settings.Ignore.ExtensionlessFiles"] = "Files without extension"
			}
		};

		return new StubLocalizationCatalog(data);
	}

	private static bool IsExtensionlessEntry(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return false;

		var extension = Path.GetExtension(value);
		return string.IsNullOrEmpty(extension) || extension == ".";
	}
}
