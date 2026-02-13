using System;
using System.Collections.Generic;
using System.Linq;
using DevProjex.Application.Services;
using DevProjex.Infrastructure.FileSystem;
using DevProjex.Kernel.Abstractions;
using DevProjex.Kernel.Models;
using DevProjex.Tests.Integration.Helpers;
using Xunit;

namespace DevProjex.Tests.Integration;

public sealed class ScopedIgnoreRulesIntegrationTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void SingleProjectWithGitIgnore_SmartIgnoreFollowsUseGitIgnoreToggle(bool useGitIgnore)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "# marker");
		temp.CreateFile("App.csproj", "<Project />");
		temp.CreateFile("src/app.cs", "class App {}");
		temp.CreateFile("bin/output.txt", "artifact");

		var smartService = new SmartIgnoreService(new ISmartIgnoreRule[]
		{
			new FixedSmartIgnoreRule(new[] { "bin" }, Array.Empty<string>())
		});
		var ignoreRulesService = new IgnoreRulesService(smartService);
		var selected = new List<IgnoreOptionId> { IgnoreOptionId.SmartIgnore };
		if (useGitIgnore)
			selected.Add(IgnoreOptionId.UseGitIgnore);

		var rules = ignoreRulesService.Build(temp.Path, selected);
		var treeBuilder = new TreeBuilder();
		var result = treeBuilder.Build(temp.Path, new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".txt", ".csproj" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src", "bin" },
			IgnoreRules: rules));

		Assert.Equal(useGitIgnore, rules.UseGitIgnore);
		Assert.Equal(useGitIgnore, rules.UseSmartIgnore);
		if (useGitIgnore)
			Assert.DoesNotContain(result.Root.Children, child => child.Name == "bin");
		else
			Assert.Contains(result.Root.Children, child => child.Name == "bin");
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	[InlineData(true, true)]
	public void MixedWorkspace_AppliesGitIgnoreAndSmartIgnorePerSelectedOptions(bool useGitIgnore, bool useSmartIgnore)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("proj-git/.gitignore", "git_only/");
		temp.CreateFile("proj-git/App.csproj", "<Project />");
		temp.CreateFile("proj-git/git_only/data.txt", "git ignored");
		temp.CreateFile("proj-git/keep/data.txt", "keep");

		temp.CreateFile("proj-no-git/package.json", "{}");
		temp.CreateFile("proj-no-git/smart_only/data.txt", "smart ignored");
		temp.CreateFile("proj-no-git/keep/data.txt", "keep");

		var smartService = new SmartIgnoreService(new ISmartIgnoreRule[]
		{
			new FixedSmartIgnoreRule(new[] { "smart_only" }, Array.Empty<string>())
		});
		var ignoreRulesService = new IgnoreRulesService(smartService);
		var selected = new List<IgnoreOptionId>();
		if (useGitIgnore)
			selected.Add(IgnoreOptionId.UseGitIgnore);
		if (useSmartIgnore)
			selected.Add(IgnoreOptionId.SmartIgnore);

		var rules = ignoreRulesService.Build(temp.Path, selected);
		var treeBuilder = new TreeBuilder();
		var result = treeBuilder.Build(temp.Path, new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt", ".json", ".csproj" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "proj-git", "proj-no-git" },
			IgnoreRules: rules));

		var projGit = result.Root.Children.Single(child => child.Name == "proj-git");
		var projNoGit = result.Root.Children.Single(child => child.Name == "proj-no-git");

		Assert.Equal(useGitIgnore, rules.UseGitIgnore);
		Assert.Equal(useSmartIgnore, rules.UseSmartIgnore);

		if (useGitIgnore)
			Assert.DoesNotContain(projGit.Children, child => child.Name == "git_only");
		else
			Assert.Contains(projGit.Children, child => child.Name == "git_only");

		if (useSmartIgnore)
			Assert.DoesNotContain(projNoGit.Children, child => child.Name == "smart_only");
		else
			Assert.Contains(projNoGit.Children, child => child.Name == "smart_only");
	}

	private sealed class FixedSmartIgnoreRule : ISmartIgnoreRule
	{
		private readonly IReadOnlyCollection<string> _folders;
		private readonly IReadOnlyCollection<string> _files;

		public FixedSmartIgnoreRule(IReadOnlyCollection<string> folders, IReadOnlyCollection<string> files)
		{
			_folders = folders;
			_files = files;
		}

		public SmartIgnoreResult Evaluate(string rootPath)
		{
			return new SmartIgnoreResult(
				new HashSet<string>(_folders, StringComparer.OrdinalIgnoreCase),
				new HashSet<string>(_files, StringComparer.OrdinalIgnoreCase));
		}
	}
}
