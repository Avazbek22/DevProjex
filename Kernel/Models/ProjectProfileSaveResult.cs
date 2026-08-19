namespace DevProjex.Kernel.Models;

public readonly record struct ProjectProfileSaveResult(
	bool Succeeded,
	bool WasTruncated);
