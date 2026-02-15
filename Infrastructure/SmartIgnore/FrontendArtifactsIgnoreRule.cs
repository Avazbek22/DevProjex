using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DevProjex.Kernel.Abstractions;
using DevProjex.Kernel.Models;

namespace DevProjex.Infrastructure.SmartIgnore;

public sealed class FrontendArtifactsIgnoreRule : ISmartIgnoreRule
{
	private static readonly string[] MarkerFiles =
	{
		"package.json",
		"package-lock.json",
		"pnpm-lock.yaml",
		"yarn.lock",
		"bun.lockb",
		"bun.lock",
		"pnpm-workspace.yaml",
		"npm-shrinkwrap.json"
	};

	private static readonly string[] FolderNames =
	{
		"node_modules",
		"dist",
		"build",
		".next",
		".nuxt",
		".turbo",
		".svelte-kit",
		".angular",
		"coverage",
		".cache",
		".parcel-cache",
		".vite",
		".output",
		".astro",
		"storybook-static",
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
