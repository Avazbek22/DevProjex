using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DevProjex.Application.Services;
using DevProjex.Application.UseCases;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.ViewModels;
using DevProjex.Infrastructure.ResourceStore;
using DevProjex.Kernel.Models;
using DevProjex.Tests.Unit.Helpers;
using Xunit;

namespace DevProjex.Tests.Unit;

public sealed class SelectionSyncCoordinatorAdditionalTests
{
	[Fact]
	public void HandleRootAllChanged_ChecksAllRootFolderOptions()
	{
		var viewModel = CreateViewModel();
		viewModel.RootFolders.Add(new SelectionOptionViewModel("src", false));
		viewModel.RootFolders.Add(new SelectionOptionViewModel("tests", false));

		var coordinator = CreateCoordinator(viewModel);

		coordinator.HandleRootAllChanged(true, currentPath: null);

		Assert.True(viewModel.AllRootFoldersChecked);
		Assert.All(viewModel.RootFolders, option => Assert.True(option.IsChecked));
	}

	[Fact]
	public void HandleExtensionsAllChanged_ChecksAllExtensionOptions()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", false));
		viewModel.Extensions.Add(new SelectionOptionViewModel(".md", false));

		var coordinator = CreateCoordinator(viewModel);

		coordinator.HandleExtensionsAllChanged(true);

		Assert.True(viewModel.AllExtensionsChecked);
		Assert.All(viewModel.Extensions, option => Assert.True(option.IsChecked));
	}

	[Fact]
	public void HandleIgnoreAllChanged_ChecksAllIgnoreOptions()
	{
		var viewModel = CreateViewModel();
		viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(IgnoreOptionId.HiddenFolders, "hidden folders", false));
		viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(IgnoreOptionId.DotFolders, "dot folders", false));

		var coordinator = CreateCoordinator(viewModel);

		coordinator.HandleIgnoreAllChanged(true, currentPath: null);

		Assert.True(viewModel.AllIgnoreChecked);
		Assert.All(viewModel.IgnoreOptions, option => Assert.True(option.IsChecked));
	}

	[Fact]
	public async Task PopulateExtensionsForRootSelectionAsync_EmptyPath_DoesNotChangeExtensions()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));

		var coordinator = CreateCoordinator(viewModel);

		await coordinator.PopulateExtensionsForRootSelectionAsync(string.Empty, new List<string> { "src" });

		Assert.Single(viewModel.Extensions);
		Assert.Equal(".cs", viewModel.Extensions[0].Name);
	}

	[Fact]
	public async Task PopulateRootFoldersAsync_EmptyPath_DoesNotChangeRootFolders()
	{
		var viewModel = CreateViewModel();
		viewModel.RootFolders.Add(new SelectionOptionViewModel("src", true));

		var coordinator = CreateCoordinator(viewModel);

		await coordinator.PopulateRootFoldersAsync(string.Empty);

		Assert.Single(viewModel.RootFolders);
		Assert.Equal("src", viewModel.RootFolders[0].Name);
	}

	[Fact]
	public async Task UpdateLiveOptionsFromRootSelectionAsync_EmptyPath_DoesNotChangeOptions()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
		viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(IgnoreOptionId.HiddenFolders, "hidden folders", true));

		var coordinator = CreateCoordinator(viewModel);

		await coordinator.UpdateLiveOptionsFromRootSelectionAsync(null);

		Assert.Single(viewModel.Extensions);
		Assert.Single(viewModel.IgnoreOptions);
	}

	[Fact]
	public void PopulateExtensionsForRootSelectionAsync_DoesNotDropCachedSelections()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", false));
		viewModel.Extensions.Add(new SelectionOptionViewModel(".md", true));

		var coordinator = CreateCoordinator(viewModel);
		coordinator.UpdateExtensionsSelectionCache();

		coordinator.ApplyExtensionScan(new[] { ".cs" });
		coordinator.ApplyExtensionScan(new[] { ".cs", ".md" });

		var md = viewModel.Extensions.Single(option => option.Name == ".md");
		Assert.True(md.IsChecked);
	}

	[Fact]
	public void PopulateExtensionsForRootSelectionAsync_EmptyRoots_DoesNotClearCachedSelections()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", false));
		viewModel.Extensions.Add(new SelectionOptionViewModel(".md", true));

		var coordinator = CreateCoordinator(viewModel);
		coordinator.UpdateExtensionsSelectionCache();

		coordinator.ApplyExtensionScan(Array.Empty<string>());
		coordinator.ApplyExtensionScan(new[] { ".cs", ".md" });

		var md = viewModel.Extensions.Single(option => option.Name == ".md");
		Assert.True(md.IsChecked);
	}

	[Fact]
	public void ApplyExtensionScan_UpdatesExtensionsFromScanResults()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".old", true));

		var coordinator = CreateCoordinator(viewModel);

		coordinator.ApplyExtensionScan(new[] { ".cs", ".md", ".root" });

		var names = viewModel.Extensions.Select(option => option.Name).ToList();
		Assert.Contains(".root", names);
		Assert.Contains(".cs", names);
		Assert.Contains(".md", names);
		Assert.DoesNotContain(".old", names);
	}

	[Fact]
	public void ApplyExtensionScan_PreservesCachedExtensionSelections()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".md", true));
		viewModel.Extensions.Add(new SelectionOptionViewModel(".txt", false));
		viewModel.AllExtensionsChecked = false;

		var coordinator = CreateCoordinator(viewModel);
		coordinator.UpdateExtensionsSelectionCache();

		coordinator.ApplyExtensionScan(new[] { ".md", ".txt" });

		var md = viewModel.Extensions.Single(option => option.Name == ".md");
		var txt = viewModel.Extensions.Single(option => option.Name == ".txt");
		Assert.True(md.IsChecked);
		Assert.False(txt.IsChecked);
	}

	[Fact]
	public void ApplyExtensionScan_EmptyScan_ClearsExtensionsAndAllFlag()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
		viewModel.AllExtensionsChecked = true;

		var coordinator = CreateCoordinator(viewModel);

		coordinator.ApplyExtensionScan(Array.Empty<string>());

		Assert.Empty(viewModel.Extensions);
		Assert.False(viewModel.AllExtensionsChecked);
	}

	[Fact]
	public void PopulateIgnoreOptionsForRootSelection_EmptyRoots_StillPopulatesIgnoreOptions()
	{
		// After Problem 1 fix: even with empty folder selection, we still scan root files
		// so ignore options should be populated, not cleared
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);

		coordinator.PopulateIgnoreOptionsForRootSelection(Array.Empty<string>());

		// Ignore options are populated for root-level files
		Assert.NotEmpty(viewModel.IgnoreOptions);
	}

	[Fact]
	public void PopulateIgnoreOptionsForRootSelection_PreservesIgnoreSelections()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		coordinator.PopulateIgnoreOptionsForRootSelection(new[] { "src" });
		coordinator.HandleIgnoreAllChanged(false, currentPath: null);
		viewModel.IgnoreOptions[0].IsChecked = true;
		viewModel.IgnoreOptions[1].IsChecked = false;
		coordinator.UpdateIgnoreSelectionCache();

		coordinator.PopulateIgnoreOptionsForRootSelection(new[] { "src" });

		var hiddenFolders = viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.HiddenFolders);
		var hiddenFiles = viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.HiddenFiles);
		Assert.True(hiddenFolders.IsChecked);
		Assert.False(hiddenFiles.IsChecked);
	}

	[Fact]
	public void PopulateIgnoreOptionsForRootSelection_WhenGitIgnoreExists_AddsUseGitIgnoreOption()
	{
		var tempRoot = Path.Combine(Path.GetTempPath(), $"devprojex-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempRoot);
		File.WriteAllText(Path.Combine(tempRoot, ".gitignore"), "bin/");
		try
		{
			var viewModel = CreateViewModel();
			var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
			var scanner = new StubFileSystemScanner();
			var scanOptions = new ScanOptionsUseCase(scanner);
			var coordinator = new SelectionSyncCoordinator(
				viewModel,
				scanOptions,
				new FilterOptionSelectionService(),
				new IgnoreOptionsService(localization),
				_ => new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()),
				_ => false,
				() => tempRoot);

			coordinator.PopulateIgnoreOptionsForRootSelection(new[] { "src" }, tempRoot);

			Assert.Contains(viewModel.IgnoreOptions, option => option.Id == IgnoreOptionId.UseGitIgnore);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void PopulateIgnoreOptionsForRootSelection_WhenGitIgnoreMissing_DoesNotAddUseGitIgnoreOption()
	{
		var tempRoot = Path.Combine(Path.GetTempPath(), $"devprojex-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempRoot);
		try
		{
			var viewModel = CreateViewModel();
			var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
			var scanner = new StubFileSystemScanner();
			var scanOptions = new ScanOptionsUseCase(scanner);
			var coordinator = new SelectionSyncCoordinator(
				viewModel,
				scanOptions,
				new FilterOptionSelectionService(),
				new IgnoreOptionsService(localization),
				_ => new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()),
				_ => false,
				() => tempRoot);

			coordinator.PopulateIgnoreOptionsForRootSelection(new[] { "src" }, tempRoot);

			Assert.DoesNotContain(viewModel.IgnoreOptions, option => option.Id == IgnoreOptionId.UseGitIgnore);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	private static MainWindowViewModel CreateViewModel()
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		return new MainWindowViewModel(localization, new HelpContentProvider());
	}

	private static SelectionSyncCoordinator CreateCoordinator(MainWindowViewModel viewModel, StubFileSystemScanner? scanner = null)
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		scanner ??= new StubFileSystemScanner();
		var scanOptions = new ScanOptionsUseCase(scanner);
		var filterService = new FilterOptionSelectionService();
		var ignoreService = new IgnoreOptionsService(localization);
		Func<string, IgnoreRules> buildIgnoreRules = _ => new IgnoreRules(false,
			false,
			false,
			false,
			new HashSet<string>(),
			new HashSet<string>());

		return new SelectionSyncCoordinator(
			viewModel,
			scanOptions,
			filterService,
			ignoreService,
			buildIgnoreRules,
			_ => false,
			() => null);
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
				["Settings.Ignore.DotFiles"] = "dot files"
			}
		};

		return new StubLocalizationCatalog(data);
	}
}

