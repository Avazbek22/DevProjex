namespace DevProjex.Tests.Unit;

public sealed class TreeNodePresentationServiceParallelProjectionTests
{
	[Fact]
	public void Build_PreservesRootChildOrder_WhenRootProjectionRunsInParallel()
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		var iconMapper = new StubIconMapper { IconKey = "folder" };
		var service = new TreeNodePresentationService(localization, iconMapper);
		var children = Enumerable.Range(0, 32)
			.Select(index => new FileSystemNode(
				$"child-{index:D2}",
				$"/root/child-{index:D2}",
				isDirectory: true,
				isAccessDenied: false,
				children: []))
			.ToList();
		var root = new FileSystemNode(
			"root",
			"/root",
			isDirectory: true,
			isAccessDenied: false,
			children);

		var result = service.Build(root);

		Assert.Equal(
			children.Select(child => child.Name).ToArray(),
			result.Children.Select(child => child.DisplayName).ToArray());
	}

	private static StubLocalizationCatalog CreateCatalog()
	{
		var data = new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Tree.AccessDeniedRoot"] = "Access denied",
				["Tree.AccessDenied"] = "Access denied"
			}
		};

		return new StubLocalizationCatalog(data);
	}
}
