namespace DevProjex.Tests.Unit;

public sealed class IgnoreRulesServiceMixedWorkspaceMatrixTests
{
	public static IEnumerable<object[]> OptionMatrix()
	{
		for (var bits = 0; bits < 64; bits++)
			yield return new object[] { bits };
	}

	[Theory]
	[MemberData(nameof(OptionMatrix))]
	public void Build_MixedWorkspace_GitAndSmartTogglesAreIndependent(int bits)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("proj-git/.gitignore", "git_only/");
		temp.CreateFile("proj-git/App.csproj", "<Project />");
		temp.CreateFile("proj-no-git/package.json", "{}");

		var smartResult = new SmartIgnoreResult(
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "smart_only" },
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Thumbs.db" });
		var service = new IgnoreRulesService(new SmartIgnoreService(new[] { new StubSmartIgnoreRule(smartResult) }));
		var selected = BuildSelectedOptions(bits);

		var rules = service.Build(temp.Path, selected);

		var expectedUseGitIgnore = selected.Contains(IgnoreOptionId.UseGitIgnore);
		var expectedUseSmartIgnore = selected.Contains(IgnoreOptionId.SmartIgnore);
		Assert.Equal(expectedUseGitIgnore, rules.UseGitIgnore);
		Assert.Equal(expectedUseSmartIgnore, rules.UseSmartIgnore);
		Assert.Equal(selected.Contains(IgnoreOptionId.HiddenFolders), rules.IgnoreHiddenFolders);
		Assert.Equal(selected.Contains(IgnoreOptionId.HiddenFiles), rules.IgnoreHiddenFiles);
		Assert.Equal(selected.Contains(IgnoreOptionId.DotFolders), rules.IgnoreDotFolders);
		Assert.Equal(selected.Contains(IgnoreOptionId.DotFiles), rules.IgnoreDotFiles);

		if (expectedUseGitIgnore)
		{
			Assert.Single(rules.ScopedGitIgnoreMatchers);

			var gitProjectGitOnlyPath = Path.Combine(temp.Path, "proj-git", "git_only");
			var plainProjectGitOnlyPath = Path.Combine(temp.Path, "proj-no-git", "git_only");

			var gitMatcher = rules.ResolveGitIgnoreMatcher(gitProjectGitOnlyPath);
			Assert.False(ReferenceEquals(gitMatcher, GitIgnoreMatcher.Empty));
			Assert.True(gitMatcher.IsIgnored(gitProjectGitOnlyPath, isDirectory: true, "git_only"));

			var plainMatcher = rules.ResolveGitIgnoreMatcher(plainProjectGitOnlyPath);
			Assert.True(ReferenceEquals(plainMatcher, GitIgnoreMatcher.Empty));
		}
		else
		{
			Assert.Empty(rules.ScopedGitIgnoreMatchers);
		}

		if (expectedUseSmartIgnore)
		{
			Assert.Contains("smart_only", rules.SmartIgnoredFolders);
			Assert.Contains("Thumbs.db", rules.SmartIgnoredFiles);
			Assert.True(rules.ShouldApplySmartIgnore(Path.Combine(temp.Path, "proj-git", "src")));
			Assert.True(rules.ShouldApplySmartIgnore(Path.Combine(temp.Path, "proj-no-git", "src")));
		}
		else
		{
			Assert.Empty(rules.SmartIgnoredFolders);
			Assert.Empty(rules.SmartIgnoredFiles);
			Assert.False(rules.ShouldApplySmartIgnore(Path.Combine(temp.Path, "proj-git", "src")));
			Assert.False(rules.ShouldApplySmartIgnore(Path.Combine(temp.Path, "proj-no-git", "src")));
		}
	}

	[Fact]
	public void GetIgnoreOptionsAvailability_SelectedGitProjectOnly_DisablesSmartOption()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("proj-git/.gitignore", "bin/");
		temp.CreateFile("proj-git/App.csproj", "<Project />");
		temp.CreateFile("proj-no-git/package.json", "{}");

		var service = new IgnoreRulesService(new SmartIgnoreService(Array.Empty<ISmartIgnoreRule>()));
		var availability = service.GetIgnoreOptionsAvailability(temp.Path, new[] { "proj-git" });

		Assert.True(availability.IncludeGitIgnore);
		Assert.False(availability.IncludeSmartIgnore);
	}

	[Fact]
	public void GetIgnoreOptionsAvailability_SelectedNonGitProjectOnly_DisablesGitOption()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("proj-git/.gitignore", "bin/");
		temp.CreateFile("proj-git/App.csproj", "<Project />");
		temp.CreateFile("proj-no-git/package.json", "{}");

		var service = new IgnoreRulesService(new SmartIgnoreService(Array.Empty<ISmartIgnoreRule>()));
		var availability = service.GetIgnoreOptionsAvailability(temp.Path, new[] { "proj-no-git" });

		Assert.False(availability.IncludeGitIgnore);
		Assert.True(availability.IncludeSmartIgnore);
	}

	[Fact]
	public void Build_SelectedGitProjectOnly_EnablesHiddenSmartMode()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("proj-git/.gitignore", "git_only/");
		temp.CreateFile("proj-git/App.csproj", "<Project />");
		temp.CreateFile("proj-no-git/package.json", "{}");

		var smartResult = new SmartIgnoreResult(
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin" },
			new HashSet<string>(StringComparer.OrdinalIgnoreCase));
		var service = new IgnoreRulesService(new SmartIgnoreService(new[] { new StubSmartIgnoreRule(smartResult) }));

		var rules = service.Build(
			temp.Path,
			new[] { IgnoreOptionId.UseGitIgnore, IgnoreOptionId.SmartIgnore },
			selectedRootFolders: new[] { "proj-git" });

		Assert.True(rules.UseGitIgnore);
		Assert.True(rules.UseSmartIgnore);
		Assert.Single(rules.ScopedGitIgnoreMatchers);
		Assert.Contains("bin", rules.SmartIgnoredFolders);
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
