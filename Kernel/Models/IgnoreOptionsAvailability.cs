namespace DevProjex.Kernel.Models;

public sealed record IgnoreOptionsAvailability(
	bool IncludeGitIgnore,
	bool IncludeSmartIgnore);
