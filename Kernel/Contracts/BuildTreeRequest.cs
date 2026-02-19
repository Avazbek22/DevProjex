namespace DevProjex.Kernel.Contracts;

public sealed record BuildTreeRequest(
	string RootPath,
	TreeFilterOptions Filter);
