namespace DevProjex.Kernel.Models;

public sealed record CacheClearResult(int Removed, int Retained, int Failed)
{
	public bool IsComplete => Retained == 0 && Failed == 0;

	public static CacheClearResult operator +(CacheClearResult left, CacheClearResult right) =>
		new(
			left.Removed + right.Removed,
			left.Retained + right.Retained,
			left.Failed + right.Failed);
}
