using System;
using DevProjex.Application.Services;
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
}
