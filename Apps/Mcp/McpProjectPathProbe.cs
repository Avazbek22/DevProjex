namespace DevProjex.Mcp;

/// <summary>
/// Counts the moments a path is handed to the filesystem while resolving a project.
/// </summary>
/// <remarks>
/// The offline guarantee is an ordering claim: a path that could name a host has to be refused
/// before anything opens it. An error code cannot check that claim, because it says only which
/// <c>throw</c> answered — a probe that ran and then fell through to the same <c>throw</c> would
/// produce exactly the same code and the same message. So the probes report themselves here, and a
/// test observes the count instead of inferring it from a code. Timing is no substitute either: an
/// unreachable host answers quickly often enough that a duration proves nothing.
/// <para>
/// What this counts is the recorded call sites, not the filesystem itself, so it is only as good as
/// its coverage. <c>McpFilesystemDoorTests</c> is what keeps that honest: it fails when a
/// filesystem call appears on the project-resolution path without a <see cref="Record"/> in front
/// of it, which is the way this measurement would otherwise rot.
/// </para>
/// <para>
/// Counting is per execution context and off by default, so a server carries one null check per
/// probe. A nested scope counts into its enclosing scopes as well, so wrapping part of a test does
/// not blind the whole of it, and increments are atomic because every assertion made on this is
/// that the count is zero — a lost increment would read as success.
/// </para>
/// </remarks>
internal static class McpProjectPathProbe
{
	private static readonly AsyncLocal<Cell?> Innermost = new();

	/// <summary>
	/// Records that a path is about to be opened, statted, or otherwise resolved by the operating
	/// system. Call this immediately before the call that does it, never after.
	/// </summary>
	internal static void Record()
	{
		for (var cell = Innermost.Value; cell is not null; cell = cell.Enclosing)
			Interlocked.Increment(ref cell.Count);
	}

	/// <summary>
	/// Counts probes made on this execution context until the returned scope is disposed.
	/// </summary>
	internal static ProbeScope Count() => new();

	internal sealed class ProbeScope : IDisposable
	{
		private readonly Cell _cell;

		internal ProbeScope()
		{
			_cell = new Cell { Enclosing = Innermost.Value };
			Innermost.Value = _cell;
		}

		internal int Value => Volatile.Read(ref _cell.Count);

		public void Dispose() => Innermost.Value = _cell.Enclosing;
	}

	private sealed class Cell
	{
		internal int Count;
		internal Cell? Enclosing;
	}
}
