using System;
using System.Collections.Generic;
using System.Linq;
using DevProjex.Application.Services;
using DevProjex.Kernel.Models;
using DevProjex.Tests.Unit.Helpers;
using Xunit;

namespace DevProjex.Tests.Unit;

public sealed class IgnoreRulesServiceToggleMatrixTests
{
	public static IEnumerable<object[]> OptionMatrix()
	{
		for (var bits = 0; bits < 64; bits++)
			yield return new object[] { bits };
	}

	[Theory]
	[MemberData(nameof(OptionMatrix))]
	public void Build_SingleProjectWithGitIgnore_SmartFollowsUseGitIgnore(int bits)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "*.log");
		temp.CreateFile("Sample.csproj", "<Project />");

		var smartResult = new SmartIgnoreResult(
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin", "obj" },
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".DS_Store", "Thumbs.db" });
		var smartService = new SmartIgnoreService(new[] { new StubSmartIgnoreRule(smartResult) });
		var service = new IgnoreRulesService(smartService);

		var selected = BuildSelectedOptions(bits);
		var rules = service.Build(temp.Path, selected);
		var useGitIgnoreSelected = selected.Contains(IgnoreOptionId.UseGitIgnore);
		var expectedUseSmartIgnore = useGitIgnoreSelected;

		Assert.Equal(useGitIgnoreSelected, rules.UseGitIgnore);
		Assert.Equal(expectedUseSmartIgnore, rules.UseSmartIgnore);
		Assert.Equal(selected.Contains(IgnoreOptionId.HiddenFolders), rules.IgnoreHiddenFolders);
		Assert.Equal(selected.Contains(IgnoreOptionId.HiddenFiles), rules.IgnoreHiddenFiles);
		Assert.Equal(selected.Contains(IgnoreOptionId.DotFolders), rules.IgnoreDotFolders);
		Assert.Equal(selected.Contains(IgnoreOptionId.DotFiles), rules.IgnoreDotFiles);

		if (expectedUseSmartIgnore)
		{
			Assert.Contains("bin", rules.SmartIgnoredFolders);
			Assert.Contains("obj", rules.SmartIgnoredFolders);
			Assert.Contains(".DS_Store", rules.SmartIgnoredFiles);
			Assert.Contains("Thumbs.db", rules.SmartIgnoredFiles);
		}
		else
		{
			Assert.Empty(rules.SmartIgnoredFolders);
			Assert.Empty(rules.SmartIgnoredFiles);
		}
	}

	private static IReadOnlyCollection<IgnoreOptionId> BuildSelectedOptions(int bits)
	{
		var selected = new List<IgnoreOptionId>(capacity: 6);
		if ((bits & 0b00001) != 0)
			selected.Add(IgnoreOptionId.UseGitIgnore);
		if ((bits & 0b00010) != 0)
			selected.Add(IgnoreOptionId.SmartIgnore);
		if ((bits & 0b00100) != 0)
			selected.Add(IgnoreOptionId.HiddenFolders);
		if ((bits & 0b01000) != 0)
			selected.Add(IgnoreOptionId.HiddenFiles);
		if ((bits & 0b10000) != 0)
			selected.Add(IgnoreOptionId.DotFolders);
		if ((bits & 0b100000) != 0)
			selected.Add(IgnoreOptionId.DotFiles);

		return selected;
	}
}
