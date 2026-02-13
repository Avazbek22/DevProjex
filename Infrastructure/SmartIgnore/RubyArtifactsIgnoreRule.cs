using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DevProjex.Kernel.Abstractions;
using DevProjex.Kernel.Models;

namespace DevProjex.Infrastructure.SmartIgnore;

/// <summary>
/// Smart ignore rule for Ruby/Rails generated and dependency folders.
/// Activates when Gemfile or Gemfile.lock exists in the scope root.
/// </summary>
public sealed class RubyArtifactsIgnoreRule : ISmartIgnoreRule
{
	private static readonly string[] MarkerFiles =
	{
		"Gemfile",
		"Gemfile.lock"
	};

	private static readonly string[] FolderNames =
	{
		".bundle",
		"vendor",
		"log",
		"tmp"
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
