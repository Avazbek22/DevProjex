using System;
using System.Collections.Generic;
using System.IO;
using DevProjex.Infrastructure.FileSystem;
using DevProjex.Kernel.Models;
using DevProjex.Tests.Integration.Helpers;
using Xunit;

namespace DevProjex.Tests.Integration;

public sealed class ExtensionlessScanningMatrixIntegrationTests
{
	[Theory]
	[MemberData(nameof(FileNameTokenMatrix))]
	public void GetRootFileExtensions_ReturnsExpectedToken_ForFileNameMatrix(string fileName, string expectedToken)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(fileName, "content");

		var scanner = new FileSystemScanner();
		var result = scanner.GetRootFileExtensions(temp.Path, CreateRules(ignoreExtensionlessFiles: false));

		Assert.Single(result.Value);
		Assert.Contains(expectedToken, result.Value);
	}

	[Theory]
	[MemberData(nameof(FileNameIgnoreMatrix))]
	public void GetRootFileExtensions_IgnoreExtensionlessFiles_FiltersOnlyExtensionless(
		string fileName,
		string expectedToken,
		bool shouldRemain)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(fileName, "content");

		var scanner = new FileSystemScanner();
		var result = scanner.GetRootFileExtensions(temp.Path, CreateRules(ignoreExtensionlessFiles: true));

		if (shouldRemain)
		{
			Assert.Single(result.Value);
			Assert.Contains(expectedToken, result.Value);
		}
		else
		{
			Assert.Empty(result.Value);
		}
	}

	public static IEnumerable<object[]> FileNameTokenMatrix()
	{
		foreach (var name in ExtensionlessFileNames)
			yield return new object[] { name, name };

		foreach (var name in ExtensionFileNames)
			yield return new object[] { name, Path.GetExtension(name) };
	}

	public static IEnumerable<object[]> FileNameIgnoreMatrix()
	{
		foreach (var name in ExtensionlessFileNames)
			yield return new object[] { name, name, false };

		foreach (var name in ExtensionFileNames)
			yield return new object[] { name, Path.GetExtension(name), true };
	}

	private static IgnoreRules CreateRules(bool ignoreExtensionlessFiles) => new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(),
		SmartIgnoredFiles: new HashSet<string>())
	{
		IgnoreExtensionlessFiles = ignoreExtensionlessFiles
	};

	private static readonly string[] ExtensionlessFileNames =
	{
		"Makefile",
		"Dockerfile",
		"README",
		"LICENSE",
		"Rakefile",
		"Gemfile",
		"Procfile",
		"Jenkinsfile",
		"Brewfile",
		"Vagrantfile",
		"CMakeLists",
		"Justfile",
		"Taskfile",
		"CHANGELOG",
		"NOTICE",
		"COPYING",
		"AUTHORS",
		"VERSION",
		"BUILD",
		"WORKSPACE",
		"gradlew",
		"pnpm-lock",
		"npmrc",
		"editorconfig",
		"gitattributes",
		"docker-compose",
		"workspace",
		"server",
		"client",
		"module",
		"service",
		"runner",
		"worker",
		"package",
		"manifest",
		"profile",
		"config",
		"settings",
		"seed"
	};

	private static readonly string[] ExtensionFileNames =
	{
		".env",
		".gitignore",
		".editorconfig",
		".dockerignore",
		".npmrc",
		"app.cs",
		"main.txt",
		"data.json",
		"photo.png",
		"archive.tar.gz",
		"gradlew.bat.noext",
		"component.axaml",
		"project.csproj",
		"solution.sln",
		"readme.md",
		"notes.log",
		"config.yaml",
		"script.ps1",
		"index.html",
		"styles.css",
		"bundle.js",
		"appsettings.Development.json",
		"global.json",
		"Directory.Build.props",
		"Directory.Build.targets",
		"NuGet.Config",
		"test.runsettings",
		"docker-compose.yml",
		"kustomization.yaml",
		"launchSettings.json",
		"web.config",
		"icon.ico",
		"report.csv",
		"template.xml",
		"plugin.dll",
		"symbols.pdb",
		"nuget.config",
		"pipeline.yml",
		"workflow.yaml",
		"translation.po",
		"locale.resx"
	};
}
