using System.Reflection;

namespace DevProjex.Application;

public static class ProjectLinks
{
	public const string RepositoryUrl = "https://github.com/Avazbek22/DevProjex";
	public const string SupportUrl = "https://boosty.to/avazbek22";

	// Every release tag is "v" + DevProjexVersion, the same value the assemblies carry as their
	// informational version, so documentation links open the document of the installed release.
	public static string ReleaseTag { get; } = ResolveReleaseTag(
		typeof(ProjectLinks).Assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
			.InformationalVersion);

	public static string McpDocumentationUrl { get; } = DocumentationUrl("McpServer.md");

	public static string InstallationDocumentationUrl { get; } = DocumentationUrl("Installation.md");

	internal static string ResolveReleaseTag(string? informationalVersion)
	{
		var version = informationalVersion?.Split('+', 2)[0].Trim();
		// A build without version metadata has no matching tag; the default branch is the closest document.
		return string.IsNullOrEmpty(version) ? "master" : "v" + version;
	}

	private static string DocumentationUrl(string documentName) =>
		$"{RepositoryUrl}/blob/{ReleaseTag}/Docs/{documentName}";
}
