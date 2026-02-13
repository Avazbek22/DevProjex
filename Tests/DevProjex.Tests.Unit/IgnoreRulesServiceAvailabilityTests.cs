using System;
using System.Collections.Generic;
using System.Linq;
using DevProjex.Application.Services;
using DevProjex.Kernel.Models;
using DevProjex.Infrastructure.SmartIgnore;
using DevProjex.Tests.Unit.Helpers;
using Xunit;

namespace DevProjex.Tests.Unit;

public sealed class IgnoreRulesServiceAvailabilityTests
{
	[Fact]
	public void GetIgnoreOptionsAvailability_SingleProjectWithGitIgnore_HidesSmartOption()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "bin/");
		temp.CreateFile("App.csproj", "<Project />");

		var service = new IgnoreRulesService(new SmartIgnoreService(Array.Empty<DevProjex.Kernel.Abstractions.ISmartIgnoreRule>()));
		var availability = service.GetIgnoreOptionsAvailability(temp.Path, Array.Empty<string>());

		Assert.True(availability.IncludeGitIgnore);
		Assert.False(availability.IncludeSmartIgnore);
	}

	[Fact]
	public void GetIgnoreOptionsAvailability_SingleProjectWithoutGitIgnore_ShowsOnlySmartOption()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("package.json", "{}");

		var service = new IgnoreRulesService(new SmartIgnoreService(Array.Empty<DevProjex.Kernel.Abstractions.ISmartIgnoreRule>()));
		var availability = service.GetIgnoreOptionsAvailability(temp.Path, Array.Empty<string>());

		Assert.False(availability.IncludeGitIgnore);
		Assert.True(availability.IncludeSmartIgnore);
	}

	[Fact]
	public void GetIgnoreOptionsAvailability_MixedWorkspace_ShowsBothGitAndSmartOptions()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("proj-git/.gitignore", "bin/");
		temp.CreateFile("proj-git/App.csproj", "<Project />");
		temp.CreateFile("proj-no-git/package.json", "{}");

		var service = new IgnoreRulesService(new SmartIgnoreService(Array.Empty<DevProjex.Kernel.Abstractions.ISmartIgnoreRule>()));
		var availability = service.GetIgnoreOptionsAvailability(temp.Path, Array.Empty<string>());

		Assert.True(availability.IncludeGitIgnore);
		Assert.True(availability.IncludeSmartIgnore);
	}

	[Fact]
	public void GetIgnoreOptionsAvailability_SelectedRootFolderLimitsScopeDiscovery()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("proj-git/.gitignore", "bin/");
		temp.CreateFile("proj-git/App.csproj", "<Project />");
		temp.CreateFile("proj-no-git/package.json", "{}");

		var service = new IgnoreRulesService(new SmartIgnoreService(Array.Empty<DevProjex.Kernel.Abstractions.ISmartIgnoreRule>()));
		var availability = service.GetIgnoreOptionsAvailability(temp.Path, new[] { "proj-no-git" });

		Assert.False(availability.IncludeGitIgnore);
		Assert.True(availability.IncludeSmartIgnore);
	}

	[Fact]
	public void GetIgnoreOptionsAvailability_NestedProjectInSelectedFolder_ShowsSmartOption()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("Visual Studio 2019/America/America.sln", "");
		temp.CreateFile("Visual Studio 2019/America/America/America.csproj", "<Project />");

		var service = new IgnoreRulesService(new SmartIgnoreService(Array.Empty<DevProjex.Kernel.Abstractions.ISmartIgnoreRule>()));
		var availability = service.GetIgnoreOptionsAvailability(temp.Path, new[] { "Visual Studio 2019" });

		Assert.False(availability.IncludeGitIgnore);
		Assert.True(availability.IncludeSmartIgnore);
	}

	[Fact]
	public void Build_SelectedNestedProjectFolder_ProducesDotNetSmartIgnoreFolders()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("Visual Studio 2019/America/America.sln", "");
		temp.CreateFile("Visual Studio 2019/America/America/America.csproj", "<Project />");

		var smartService = new SmartIgnoreService(new DevProjex.Kernel.Abstractions.ISmartIgnoreRule[]
		{
			new DotNetArtifactsIgnoreRule()
		});
		var service = new IgnoreRulesService(smartService);
		var rules = service.Build(
			temp.Path,
			new[] { IgnoreOptionId.SmartIgnore },
			selectedRootFolders: new[] { "Visual Studio 2019" });

		Assert.True(rules.UseSmartIgnore);
		Assert.Contains("bin", rules.SmartIgnoredFolders);
		Assert.Contains("obj", rules.SmartIgnoredFolders);

		var nestedProjectPath = System.IO.Path.Combine(temp.Path, "Visual Studio 2019", "America", "America");
		Assert.True(rules.ShouldApplySmartIgnore(nestedProjectPath));
		Assert.True(rules.SmartIgnoreScopeRoots.Any());
	}

	[Fact]
	public void GetIgnoreOptionsAvailability_ParentFolderDepthTwoProject_ShowsSmartOption()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("Documents/Visual Studio 2019/America/America.sln", "");
		temp.CreateFile("Documents/Visual Studio 2019/America/America/America.csproj", "<Project />");

		var service = new IgnoreRulesService(new SmartIgnoreService(Array.Empty<DevProjex.Kernel.Abstractions.ISmartIgnoreRule>()));
		var availability = service.GetIgnoreOptionsAvailability(temp.Path, new[] { "Documents" });

		Assert.False(availability.IncludeGitIgnore);
		Assert.True(availability.IncludeSmartIgnore);
	}

	[Fact]
	public void GetIgnoreOptionsAvailability_ParentFolderWithNestedGitIgnoreProject_ShowsGitOption()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("Documents/Visual Studio 2019/America/.gitignore", "bin/\nobj/\n");
		temp.CreateFile("Documents/Visual Studio 2019/America/America/America.csproj", "<Project />");

		var service = new IgnoreRulesService(new SmartIgnoreService(Array.Empty<DevProjex.Kernel.Abstractions.ISmartIgnoreRule>()));
		var availability = service.GetIgnoreOptionsAvailability(temp.Path, new[] { "Documents" });

		Assert.True(availability.IncludeGitIgnore);
		Assert.False(availability.IncludeSmartIgnore);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	public void GetIgnoreOptionsAvailability_NestedDotNetProject_AvailabilityStableAcrossOpenedRootLevels(int rootMode)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("Documents/Visual Studio 2019/America/America.sln", "");
		temp.CreateFile("Documents/Visual Studio 2019/America/America/America.csproj", "<Project />");

		var (openedRootPath, selectedRootFolders) = ResolveRootMode(temp.Path, rootMode);
		var service = new IgnoreRulesService(new SmartIgnoreService(Array.Empty<DevProjex.Kernel.Abstractions.ISmartIgnoreRule>()));
		var availability = service.GetIgnoreOptionsAvailability(openedRootPath, selectedRootFolders);

		Assert.False(availability.IncludeGitIgnore);
		Assert.True(availability.IncludeSmartIgnore);
	}

	[Theory]
	[InlineData(0, false)]
	[InlineData(1, true)]
	[InlineData(2, false)]
	public void GetIgnoreOptionsAvailability_NestedGitIgnoreProject_ReflectsDiscoveredScopesByOpenedRootLevel(
		int rootMode,
		bool expectedIncludeSmartIgnore)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("Documents/Visual Studio 2019/America/.gitignore", "bin/\nobj/\n");
		temp.CreateFile("Documents/Visual Studio 2019/America/America/America.csproj", "<Project />");

		var (openedRootPath, selectedRootFolders) = ResolveRootMode(temp.Path, rootMode);
		var service = new IgnoreRulesService(new SmartIgnoreService(Array.Empty<DevProjex.Kernel.Abstractions.ISmartIgnoreRule>()));
		var availability = service.GetIgnoreOptionsAvailability(openedRootPath, selectedRootFolders);

		Assert.True(availability.IncludeGitIgnore);
		Assert.Equal(expectedIncludeSmartIgnore, availability.IncludeSmartIgnore);
	}

	[Fact]
	public void Build_ParentFolderWithNestedGitIgnoreProject_KeepsSmartToggleExplicitWhenSmartOptionAvailable()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("Documents/Visual Studio 2019/America/.gitignore", "bin/\nobj/\n");
		temp.CreateFile("Documents/Visual Studio 2019/America/America/America.csproj", "<Project />");

		var smartService = new SmartIgnoreService(new DevProjex.Kernel.Abstractions.ISmartIgnoreRule[]
		{
			new DotNetArtifactsIgnoreRule()
		});
		var service = new IgnoreRulesService(smartService);
		var rules = service.Build(
			temp.Path,
			new[] { IgnoreOptionId.UseGitIgnore },
			selectedRootFolders: new[] { "Documents" });

		Assert.True(rules.UseGitIgnore);
		Assert.False(rules.UseSmartIgnore);
		Assert.NotEmpty(rules.ScopedGitIgnoreMatchers);
		Assert.Empty(rules.SmartIgnoredFolders);
		Assert.Empty(rules.SmartIgnoredFiles);
	}

	[Fact]
	public void GetIgnoreOptionsAvailability_DoesNotThrow_WhenSmartIgnoreRuleFails()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("Documents/Visual Studio 2019/America/America.sln", "");

		var smartService = new SmartIgnoreService(new DevProjex.Kernel.Abstractions.ISmartIgnoreRule[]
		{
			new ThrowingSmartIgnoreRule()
		});
		var service = new IgnoreRulesService(smartService);

		var availability = service.GetIgnoreOptionsAvailability(temp.Path, new[] { "Documents" });
		Assert.False(availability.IncludeGitIgnore);
		Assert.True(availability.IncludeSmartIgnore);
	}

	private static (string OpenedRootPath, IReadOnlyCollection<string> SelectedRootFolders) ResolveRootMode(
		string tempPath,
		int rootMode)
	{
		return rootMode switch
		{
			0 => (
				OpenedRootPath: tempPath,
				SelectedRootFolders: new[] { "Documents" }),
			1 => (
				OpenedRootPath: System.IO.Path.Combine(tempPath, "Documents"),
				SelectedRootFolders: new[] { "Visual Studio 2019" }),
			2 => (
				OpenedRootPath: System.IO.Path.Combine(tempPath, "Documents", "Visual Studio 2019"),
				SelectedRootFolders: new[] { "America" }),
			_ => throw new ArgumentOutOfRangeException(nameof(rootMode), rootMode, "Unsupported root mode.")
		};
	}

	private sealed class ThrowingSmartIgnoreRule : DevProjex.Kernel.Abstractions.ISmartIgnoreRule
	{
		public SmartIgnoreResult Evaluate(string rootPath)
		{
			throw new UnauthorizedAccessException("Access denied.");
		}
	}
}
