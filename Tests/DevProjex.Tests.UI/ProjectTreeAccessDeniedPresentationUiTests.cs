using DevProjex.Application.Services;
using DevProjex.Infrastructure.ResourceStore;

namespace DevProjex.Tests.UI;

[Collection("AvaloniaUI")]
public sealed class ProjectTreeAccessDeniedPresentationUiTests
{
	[AvaloniaFact]
	public void AccessDeniedNode_PublishesComposedNameAndOrdinaryFileIcon()
	{
		var presenter = new TreeNodePresentationService(
			new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En),
			new IconMapper());
		var source = new FileSystemNode(
			"root",
			Path.Combine(Path.GetTempPath(), "root"),
			isDirectory: true,
			isAccessDenied: false,
			[
				new FileSystemNode(
					"settings.json",
					Path.Combine(Path.GetTempPath(), "root", "settings.json"),
					isDirectory: false,
					isAccessDenied: true,
					FileSystemNode.EmptyChildren)
			]);

		var descriptor = Assert.Single(presenter.Build(source).Children);
		var viewModel = new TreeNodeViewModel(descriptor, parent: null, icon: null);

		Assert.Equal("settings.json [access denied]", viewModel.DisplayName);
		Assert.Equal("json", viewModel.Descriptor.IconKey);
	}
}
