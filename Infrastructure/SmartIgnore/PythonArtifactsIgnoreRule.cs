using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DevProjex.Kernel.Abstractions;
using DevProjex.Kernel.Models;

namespace DevProjex.Infrastructure.SmartIgnore;

/// <summary>
/// Smart ignore rule for Python cache and virtual environment folders.
/// Activates only when Python project markers are detected in the scope root.
/// </summary>
public sealed class PythonArtifactsIgnoreRule : ISmartIgnoreRule
{
	private static readonly string[] MarkerFiles =
	{
		"pyproject.toml",
		"requirements.txt",
		"requirements-dev.txt",
		"setup.py",
		"setup.cfg",
		"Pipfile",
		"poetry.lock",
		"environment.yml"
	};

	private static readonly string[] FolderNames =
	{
		"__pycache__",
		".pytest_cache",
		".mypy_cache",
		".ruff_cache",
		".tox",
		".nox",
		".venv",
		"venv",
		"env",
		".hypothesis",
		".ipynb_checkpoints",
		".pyre"
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
