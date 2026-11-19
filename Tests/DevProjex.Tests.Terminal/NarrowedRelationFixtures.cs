namespace DevProjex.Tests.Terminal;

/// <summary>
/// Projects whose relations a narrowed selection cannot answer by itself, shared by the MCP, CLI,
/// and Terminal Workspace tests so every surface is held to the same answer.
/// </summary>
internal static class NarrowedRelationFixtures
{
	public const string LibrarySeed = "Core/Logger.cs";
	public const string LibraryFolder = "Core";
	public const string PackageSeed = "pkg/_transports/default.py";
	public const string PackageFolder = "pkg/_transports";

	public static readonly string[] LibraryDependenciesInside =
	[
		"Core/Enrichers/FixedEnricher.cs",
		"Core/LevelSwitch.cs",
		"Core/Pipeline/SilentLogger.cs"
	];

	/// <summary>
	/// A C# library whose core folder reaches two of its own namespaces only through a global using
	/// kept outside that folder, as Serilog's <c>src/Serilog/GlobalUsings.cs</c> does.
	/// </summary>
	public static void WriteGlobalUsingLibrary(TemporaryDirectory workspace, string root)
	{
		workspace.WriteFile($"{root}/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
		workspace.WriteFile($"{root}/GlobalUsings.cs",
			"global using Lib.Core.Enrichers;\nglobal using Lib.Core.Pipeline;\n");
		workspace.WriteFile($"{root}/Core/Logger.cs",
			"using Lib.Events;\nnamespace Lib.Core;\npublic sealed class Logger\n{\n" +
			"\tFixedEnricher First;\n\tSilentLogger Second;\n\tLevelSwitch Third;\n\tLogEvent Fourth;\n}\n");
		workspace.WriteFile($"{root}/Core/Enrichers/FixedEnricher.cs",
			"namespace Lib.Core.Enrichers;\npublic sealed class FixedEnricher { }\n");
		workspace.WriteFile($"{root}/Core/Pipeline/SilentLogger.cs",
			"namespace Lib.Core.Pipeline;\npublic sealed class SilentLogger { }\n");
		workspace.WriteFile($"{root}/Core/LevelSwitch.cs",
			"namespace Lib.Core;\npublic sealed class LevelSwitch { }\n");
		workspace.WriteFile($"{root}/Events/LogEvent.cs",
			"namespace Lib.Events;\npublic sealed class LogEvent { }\n");
		workspace.WriteFile($"{root}/Configuration.cs",
			"using Lib.Core;\nnamespace Lib;\npublic sealed class Configuration { Logger Created; }\n");
	}

	/// <summary>
	/// A Python package whose transports folder imports the package itself, as httpx's
	/// <c>httpx/_transports/default.py</c> does; the package's <c>__init__.py</c> lies outside it.
	/// </summary>
	public static void WritePackageImportingItself(TemporaryDirectory workspace, string root)
	{
		workspace.WriteFile($"{root}/pyproject.toml", "[project]\nname = \"pkg\"\n");
		workspace.WriteFile($"{root}/pkg/__init__.py", "from ._client import Client\n");
		workspace.WriteFile($"{root}/pkg/_client.py",
			"from ._transports.default import Transport\nclass Client: pass\n");
		workspace.WriteFile($"{root}/pkg/_config.py", "class Limits: pass\n");
		workspace.WriteFile($"{root}/pkg/_transports/__init__.py", "from .default import *\n");
		workspace.WriteFile($"{root}/pkg/_transports/default.py",
			"import pkg\nfrom .._config import Limits\nfrom .base import Base\nclass Transport(Base): pass\n");
		workspace.WriteFile($"{root}/pkg/_transports/base.py", "class Base: pass\n");
	}

	/// <summary>
	/// The rows of one titled section of a related text payload, in order. A section ends at the
	/// next line that is a title rather than a relation row.
	/// </summary>
	public static string[] SectionPaths(string text, string title)
	{
		var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
		var start = Array.IndexOf(lines, title + ":");
		if (start < 0)
			return [];
		return lines
			.Skip(start + 1)
			.TakeWhile(static line => line.Contains(" — ", StringComparison.Ordinal))
			.Select(static line => line[..line.IndexOf(" — ", StringComparison.Ordinal)])
			.ToArray();
	}
}
