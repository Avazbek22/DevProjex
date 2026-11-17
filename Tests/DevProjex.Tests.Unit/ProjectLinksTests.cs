using System.Reflection;
using System.Text.RegularExpressions;
using DevProjex.Application;

namespace DevProjex.Tests.Unit;

public sealed class ProjectLinksTests
{
	private static readonly Regex VersionedDocumentLink = new(
		@"DevProjex/(?:blob|tree)/v\d",
		RegexOptions.CultureInvariant);

	[Fact]
	public void DocumentationLinks_UseTheReleaseTagOfTheRunningVersion()
	{
		var informationalVersion = typeof(ProjectLinks).Assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
			.InformationalVersion;
		var expectedTag = "v" + informationalVersion.Split('+')[0];

		Assert.Equal(expectedTag, ProjectLinks.ReleaseTag);
		Assert.Equal(
			$"https://github.com/Avazbek22/DevProjex/blob/{expectedTag}/Docs/McpServer.md",
			ProjectLinks.McpDocumentationUrl);
		Assert.Equal(
			$"https://github.com/Avazbek22/DevProjex/blob/{expectedTag}/Docs/Installation.md",
			ProjectLinks.InstallationDocumentationUrl);
	}

	[Theory]
	[InlineData("5.2.5", "v5.2.5")]
	[InlineData("5.2", "v5.2")]
	[InlineData("5.2.5+0123abcd", "v5.2.5")]
	[InlineData("5.3.0-rc.1+0123abcd", "v5.3.0-rc.1")]
	[InlineData(" 5.2.5 ", "v5.2.5")]
	[InlineData("", "master")]
	[InlineData(null, "master")]
	public void ResolveReleaseTag_DropsBuildMetadata(string? informationalVersion, string expected)
	{
		Assert.Equal(expected, ProjectLinks.ResolveReleaseTag(informationalVersion));
	}

	[Fact]
	public void ProductSources_DoNotPinDocumentLinksToAFixedRelease()
	{
		var root = FindRepositoryRoot();
		var offenders = new[] { "Application", "Apps", "Assets", "Infrastructure", "Kernel" }
			.Select(directory => Path.Combine(root, directory))
			.Where(Directory.Exists)
			.SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
			.Where(static path => Path.GetExtension(path) is ".cs" or ".json" or ".txt" or ".axaml")
			.Where(static path => !IsBuildOutput(path))
			.Where(path => VersionedDocumentLink.IsMatch(File.ReadAllText(path)))
			.Select(path => Path.GetRelativePath(root, path))
			.ToArray();

		Assert.Empty(offenders);
	}

	private static bool IsBuildOutput(string path)
	{
		var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return segments.Contains("bin", StringComparer.OrdinalIgnoreCase) ||
			   segments.Contains("obj", StringComparer.OrdinalIgnoreCase);
	}

	private static string FindRepositoryRoot()
	{
		var current = new DirectoryInfo(AppContext.BaseDirectory);
		while (current is not null)
		{
			if (File.Exists(Path.Combine(current.FullName, "DevProjex.sln")))
				return current.FullName;

			current = current.Parent;
		}

		throw new DirectoryNotFoundException("Repository root was not found.");
	}
}
