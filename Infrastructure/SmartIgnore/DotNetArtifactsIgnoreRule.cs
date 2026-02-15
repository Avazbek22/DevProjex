using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DevProjex.Kernel.Abstractions;
using DevProjex.Kernel.Models;

namespace DevProjex.Infrastructure.SmartIgnore;

/// <summary>
/// Smart ignore rule for .NET build artifacts (bin, obj folders).
/// Activates when .sln, .csproj, .fsproj, or .vbproj files are found.
/// </summary>
public sealed class DotNetArtifactsIgnoreRule : ISmartIgnoreRule
{
	private static readonly string[] MarkerExtensions =
	{
		".sln",
		".csproj",
		".fsproj",
		".vbproj"
	};

	private static readonly string[] FolderNames =
	{
		"bin",
		"obj"
	};

	public SmartIgnoreResult Evaluate(string rootPath)
	{
		if (!Directory.Exists(rootPath))
			return new SmartIgnoreResult(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase),
				new HashSet<string>(StringComparer.OrdinalIgnoreCase));

		bool hasMarker;
		try
		{
			hasMarker = MarkerExtensions.Any(ext =>
				Directory.EnumerateFiles(rootPath, "*" + ext, SearchOption.TopDirectoryOnly).Any());
		}
		catch
		{
			return new SmartIgnoreResult(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase),
				new HashSet<string>(StringComparer.OrdinalIgnoreCase));
		}

		if (!hasMarker)
			return new SmartIgnoreResult(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase),
				new HashSet<string>(StringComparer.OrdinalIgnoreCase));

		return new SmartIgnoreResult(
			new HashSet<string>(FolderNames, StringComparer.OrdinalIgnoreCase),
			new HashSet<string>(StringComparer.OrdinalIgnoreCase));
	}
}
