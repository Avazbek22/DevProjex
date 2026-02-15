using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DevProjex.Kernel.Abstractions;
using DevProjex.Kernel.Models;

namespace DevProjex.Infrastructure.SmartIgnore;

/// <summary>
/// Smart ignore rule for Java/Kotlin/Gradle build output folders.
/// Activates when Maven/Gradle markers are present in the scope root.
/// </summary>
public sealed class JvmArtifactsIgnoreRule : ISmartIgnoreRule
{
	private static readonly string[] MarkerFiles =
	{
		"pom.xml",
		"build.gradle",
		"build.gradle.kts",
		"settings.gradle",
		"settings.gradle.kts"
	};

	private static readonly string[] FolderNames =
	{
		"target",
		".gradle",
		"build",
		"out"
	};

	public SmartIgnoreResult Evaluate(string rootPath)
	{
		if (!Directory.Exists(rootPath))
			return new SmartIgnoreResult(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase),
				new HashSet<string>(StringComparer.OrdinalIgnoreCase));

		bool hasMarker = MarkerFiles.Any(marker => File.Exists(Path.Combine(rootPath, marker)));
		if (!hasMarker)
			return new SmartIgnoreResult(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase),
				new HashSet<string>(StringComparer.OrdinalIgnoreCase));

		return new SmartIgnoreResult(
			new HashSet<string>(FolderNames, StringComparer.OrdinalIgnoreCase),
			new HashSet<string>(StringComparer.OrdinalIgnoreCase));
	}
}
