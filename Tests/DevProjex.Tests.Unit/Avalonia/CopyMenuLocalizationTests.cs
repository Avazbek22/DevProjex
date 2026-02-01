using System.Collections.Generic;
using DevProjex.Avalonia.ViewModels;
using DevProjex.Application.Services;
using DevProjex.Infrastructure.ResourceStore;
using DevProjex.Kernel.Models;
using DevProjex.Tests.Unit.Helpers;
using Xunit;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class CopyMenuLocalizationTests
{
	[Fact]
	public void UpdateLocalization_UsesNewCopyMenuKeys()
	{
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Menu.Copy"] = "Copy",
				["Menu.Copy.Tree"] = "Copy tree",
				["Menu.Copy.Content"] = "Copy content",
				["Menu.Copy.TreeAndContent"] = "Copy tree and content"
			}
		});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		var viewModel = new MainWindowViewModel(localization, new HelpContentProvider());

		viewModel.UpdateLocalization();

		Assert.Equal("Copy", viewModel.MenuCopy);
		Assert.Equal("Copy tree", viewModel.MenuCopyTree);
		Assert.Equal("Copy content", viewModel.MenuCopyContent);
		Assert.Equal("Copy tree and content", viewModel.MenuCopyTreeAndContent);
	}
}
