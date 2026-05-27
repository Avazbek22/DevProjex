namespace DevProjex.Kernel.Models;

public readonly record struct IgnoreControllerImpactCounts(
	int GitIgnore = 0,
	int SmartIgnore = 0)
{
	public static readonly IgnoreControllerImpactCounts Empty = new();

	public IgnoreControllerImpactCounts Add(in IgnoreControllerImpactCounts other)
	{
		return new IgnoreControllerImpactCounts(
			GitIgnore + other.GitIgnore,
			SmartIgnore + other.SmartIgnore);
	}
}
