using System;
using System.Collections.Generic;
using System.Linq;
using DevProjex.Application.Services;
using DevProjex.Application.UseCases;
using DevProjex.Infrastructure.FileSystem;
using DevProjex.Kernel.Abstractions;
using DevProjex.Kernel.Models;
using DevProjex.Tests.Integration.Helpers;
using Xunit;

namespace DevProjex.Tests.Integration;

public sealed class IgnorePipelineMatrixIntegrationTests
{
	public enum RootSelectionMode
	{
		All = 0,
		OnlyGitProject = 1,
		OnlyNoGitProject = 2
	}

	public enum ExtensionMode
	{
		SourceOnly = 0,
		Empty = 1
	}

	public static IEnumerable<object[]> MatrixCases()
	{
		foreach (var useGitIgnore in new[] { false, true })
		{
			foreach (var useSmartIgnore in new[] { false, true })
			{
				foreach (RootSelectionMode rootSelection in Enum.GetValues(typeof(RootSelectionMode)))
				{
					foreach (ExtensionMode extensionMode in Enum.GetValues(typeof(ExtensionMode)))
					{
						yield return new object[] { useGitIgnore, useSmartIgnore, rootSelection, extensionMode };
					}
				}
			}
		}
	}

	[Theory]
	[MemberData(nameof(MatrixCases))]
	public void IgnorePipeline_Matrix_CoversScopedGitAndSmartBehavior(
		bool useGitIgnore,
		bool useSmartIgnore,
		RootSelectionMode rootSelectionMode,
		ExtensionMode extensionMode)
	{
		using var temp = new TemporaryDirectory();

		temp.CreateFile("proj-git/.gitignore", "bin/\nobj/\n");
		temp.CreateFile("proj-git/App.csproj", "<Project />");
		temp.CreateFile("proj-git/src/app.cs", "class App {}");
		temp.CreateFile("proj-git/bin/Debug/app.dll", "binary");
		temp.CreateFile("proj-git/obj/Debug/cache.txt", "cache");

		temp.CreateFile("proj-no-git/package.json", "{}");
		temp.CreateFile("proj-no-git/src/index.ts", "export const x = 1;");
		temp.CreateFile("proj-no-git/node_modules/lib.js", "module.exports = {};");
		temp.CreateFile("proj-no-git/coverage/report.txt", "coverage");

		var smartService = new SmartIgnoreService(new ISmartIgnoreRule[]
		{
			new FixedSmartIgnoreRule(
				new[] { "node_modules", "coverage" },
				Array.Empty<string>())
		});
		var rulesService = new IgnoreRulesService(smartService);

		var selectedRootFolders = BuildSelectedRootFolders(rootSelectionMode);
		var selectedOptions = BuildSelectedOptions(useGitIgnore, useSmartIgnore);
		var rules = rulesService.Build(temp.Path, selectedOptions, selectedRootFolders);

		var allowedExtensions = extensionMode == ExtensionMode.SourceOnly
			? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".ts", ".json" }
			: new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		var treeBuilder = new TreeBuilder();
		var treeResult = treeBuilder.Build(temp.Path, new TreeFilterOptions(
			AllowedExtensions: allowedExtensions,
			AllowedRootFolders: new HashSet<string>(selectedRootFolders, StringComparer.OrdinalIgnoreCase),
			IgnoreRules: rules));

		var rootNames = treeResult.Root.Children.Select(child => child.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var includesGitProject = selectedRootFolders.Contains("proj-git", StringComparer.OrdinalIgnoreCase);
		var includesNoGitProject = selectedRootFolders.Contains("proj-no-git", StringComparer.OrdinalIgnoreCase);

		Assert.Equal(includesGitProject, rootNames.Contains("proj-git"));
		Assert.Equal(includesNoGitProject, rootNames.Contains("proj-no-git"));

		if (includesGitProject)
		{
			var gitProjectNode = treeResult.Root.Children.Single(child => child.Name == "proj-git");
			Assert.Equal(!rules.UseGitIgnore, gitProjectNode.Children.Any(child => child.Name == "bin"));
			Assert.Equal(!rules.UseGitIgnore, gitProjectNode.Children.Any(child => child.Name == "obj"));
		}

		if (includesNoGitProject)
		{
			var noGitProjectNode = treeResult.Root.Children.Single(child => child.Name == "proj-no-git");
			var smartApplies = rules.ShouldApplySmartIgnore(System.IO.Path.Combine(temp.Path, "proj-no-git", "src"));
			var shouldHideSmartIgnoredDirs = rules.UseSmartIgnore && smartApplies;

			Assert.Equal(!shouldHideSmartIgnoredDirs, noGitProjectNode.Children.Any(child => child.Name == "node_modules"));
			Assert.Equal(!shouldHideSmartIgnoredDirs, noGitProjectNode.Children.Any(child => child.Name == "coverage"));
		}

		var scanUseCase = new ScanOptionsUseCase(new FileSystemScanner());
		var extensionScan = scanUseCase.GetExtensionsForRootFolders(temp.Path, selectedRootFolders, rules);
		var extensions = extensionScan.Value;

		var expectedCs = includesGitProject;
		var expectedDll = includesGitProject && !rules.UseGitIgnore;
		var smartAppliesNoGit = rules.ShouldApplySmartIgnore(System.IO.Path.Combine(temp.Path, "proj-no-git", "src"));
		var expectedTs = includesNoGitProject;
		var expectedJs = includesNoGitProject && !(rules.UseSmartIgnore && smartAppliesNoGit);
		var expectedTxt = (includesGitProject && !rules.UseGitIgnore) ||
		                  (includesNoGitProject && !(rules.UseSmartIgnore && smartAppliesNoGit));

		Assert.Equal(expectedCs, extensions.Contains(".cs"));
		Assert.Equal(expectedDll, extensions.Contains(".dll"));
		Assert.Equal(expectedTs, extensions.Contains(".ts"));
		Assert.Equal(expectedJs, extensions.Contains(".js"));
		Assert.Equal(expectedTxt, extensions.Contains(".txt"));
	}

	private static IReadOnlyCollection<IgnoreOptionId> BuildSelectedOptions(bool useGitIgnore, bool useSmartIgnore)
	{
		var selected = new List<IgnoreOptionId>();
		if (useGitIgnore)
			selected.Add(IgnoreOptionId.UseGitIgnore);
		if (useSmartIgnore)
			selected.Add(IgnoreOptionId.SmartIgnore);
		return selected;
	}

	private static IReadOnlyCollection<string> BuildSelectedRootFolders(RootSelectionMode mode)
	{
		return mode switch
		{
			RootSelectionMode.All => new[] { "proj-git", "proj-no-git" },
			RootSelectionMode.OnlyGitProject => new[] { "proj-git" },
			_ => new[] { "proj-no-git" }
		};
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
