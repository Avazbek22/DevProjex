namespace DevProjex.Tests.Unit;

public sealed class IgnoreOptionRefreshPlannerTests
{
	[Theory]
	[InlineData(IgnoreOptionId.HiddenFolders, (int)IgnoreOptionRefreshImpact.RootStructure)]
	[InlineData(IgnoreOptionId.DotFolders, (int)IgnoreOptionRefreshImpact.RootStructure)]
	[InlineData(IgnoreOptionId.EmptyFolders, (int)IgnoreOptionRefreshImpact.RootStructure)]
	[InlineData(IgnoreOptionId.HiddenFiles, (int)IgnoreOptionRefreshImpact.FileVisibility)]
	[InlineData(IgnoreOptionId.DotFiles, (int)IgnoreOptionRefreshImpact.FileVisibility)]
	[InlineData(IgnoreOptionId.EmptyFiles, (int)IgnoreOptionRefreshImpact.FileVisibility)]
	[InlineData(IgnoreOptionId.ExtensionlessFiles, (int)IgnoreOptionRefreshImpact.FileVisibility)]
	[InlineData(IgnoreOptionId.UseGitIgnore, (int)IgnoreOptionRefreshImpact.RootStructure)]
	[InlineData(IgnoreOptionId.SmartIgnore, (int)IgnoreOptionRefreshImpact.RootStructure)]
	public void GetImpact_ClassifiesEachIgnoreOption(IgnoreOptionId optionId, int expectedImpact)
	{
		var impact = IgnoreOptionRefreshPlanner.GetImpact(optionId);

		Assert.Equal((IgnoreOptionRefreshImpact)expectedImpact, impact);
	}

	[Theory]
	[MemberData(nameof(SelectionChangeCases))]
	public void ClassifyChangedSelection_CombinesOnlyActuallyChangedOptions(
		IgnoreOptionId[] beforeSelection,
		IgnoreOptionId[] afterSelection,
		int expectedImpact)
	{
		var impact = IgnoreOptionRefreshPlanner.ClassifyChangedSelection(
			new HashSet<IgnoreOptionId>(beforeSelection),
			new HashSet<IgnoreOptionId>(afterSelection));

		Assert.Equal((IgnoreOptionRefreshImpact)expectedImpact, impact);
	}

	public static IEnumerable<object[]> SelectionChangeCases()
	{
		yield return
		[
			Array.Empty<IgnoreOptionId>(),
			Array.Empty<IgnoreOptionId>(),
			(int)IgnoreOptionRefreshImpact.None
		];

		yield return
		[
			Array.Empty<IgnoreOptionId>(),
			new[] { IgnoreOptionId.EmptyFiles },
			(int)IgnoreOptionRefreshImpact.FileVisibility
		];

		yield return
		[
			new[] { IgnoreOptionId.EmptyFiles },
			Array.Empty<IgnoreOptionId>(),
			(int)IgnoreOptionRefreshImpact.FileVisibility
		];

		yield return
		[
			Array.Empty<IgnoreOptionId>(),
			new[] { IgnoreOptionId.DotFolders },
			(int)IgnoreOptionRefreshImpact.RootStructure
		];

		yield return
		[
			new[] { IgnoreOptionId.DotFolders },
			Array.Empty<IgnoreOptionId>(),
			(int)IgnoreOptionRefreshImpact.RootStructure
		];

		yield return
		[
			new[] { IgnoreOptionId.HiddenFiles },
			new[] { IgnoreOptionId.HiddenFiles, IgnoreOptionId.EmptyFiles },
			(int)IgnoreOptionRefreshImpact.FileVisibility
		];

		yield return
		[
			new[] { IgnoreOptionId.HiddenFiles },
			new[] { IgnoreOptionId.HiddenFiles, IgnoreOptionId.DotFolders },
			(int)IgnoreOptionRefreshImpact.RootStructure
		];

		yield return
		[
			new[] { IgnoreOptionId.EmptyFiles, IgnoreOptionId.ExtensionlessFiles },
			new[] { IgnoreOptionId.DotFolders, IgnoreOptionId.EmptyFiles },
			(int)(IgnoreOptionRefreshImpact.FileVisibility | IgnoreOptionRefreshImpact.RootStructure)
		];

		yield return
		[
			new[] { IgnoreOptionId.UseGitIgnore, IgnoreOptionId.HiddenFiles },
			new[] { IgnoreOptionId.UseGitIgnore, IgnoreOptionId.HiddenFiles },
			(int)IgnoreOptionRefreshImpact.None
		];

		yield return
		[
			new[] { IgnoreOptionId.SmartIgnore },
			new[] { IgnoreOptionId.SmartIgnore, IgnoreOptionId.ExtensionlessFiles },
			(int)IgnoreOptionRefreshImpact.FileVisibility
		];
	}
}
