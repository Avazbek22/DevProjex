using System;
using System.Collections.Generic;
using System.IO;
using DevProjex.Kernel.Abstractions;
using DevProjex.Kernel.Models;

namespace DevProjex.Infrastructure.SmartIgnore;

/// <summary>
/// Smart ignore rule for Rust build output folders.
/// Activates when Cargo.toml exists in the scope root.
/// </summary>
public sealed class RustArtifactsIgnoreRule : ISmartIgnoreRule
{
	private static readonly string[] FolderNames =
	{
		"target"
	};

	public SmartIgnoreResult Evaluate(string rootPath)
	{
		if (!Directory.Exists(rootPath))
			return new SmartIgnoreResult(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase),
				new HashSet<string>(StringComparer.OrdinalIgnoreCase));

		if (!File.Exists(Path.Combine(rootPath, "Cargo.toml")))
			return new SmartIgnoreResult(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase),
				new HashSet<string>(StringComparer.OrdinalIgnoreCase));

		return new SmartIgnoreResult(
			new HashSet<string>(FolderNames, StringComparer.OrdinalIgnoreCase),
			new HashSet<string>(StringComparer.OrdinalIgnoreCase));
	}
}
