namespace DevProjex.Mcp;

/// <summary>
/// Counts the moments a path is handed to the filesystem while resolving a project.
/// </summary>
/// <remarks>
/// The offline guarantee is an ordering claim: a remote path form has to be refused before anything
/// opens it. An error code cannot check that claim, because it says only which <c>throw</c>
/// answered — a probe that ran and then fell through to the same <c>throw</c> would produce exactly
/// the same code and the same message. So the probes report themselves here, and a test observes
/// the count instead of inferring it from a code. Timing is no substitute either: an unreachable
/// host answers quickly often enough that a duration proves nothing.
/// <para>
/// Counting is per execution context and off by default, so a server carries one null check per
/// probe and tests running side by side never see each other's work.
/// </para>
/// </remarks>
internal static class McpProjectPathProbe
{
	private static readonly AsyncLocal<int[]?> Counter = new();

	/// <summary>
	/// Records that a path is about to be opened, statted, or otherwise resolved by the operating
	/// system. Call this immediately before the call that does it, never after.
	/// </summary>
	internal static void Record()
	{
		if (Counter.Value is { } counter)
			counter[0]++;
	}

	/// <summary>
	/// Counts probes made on this execution context until the returned scope is disposed.
	/// </summary>
	internal static ProbeScope Count() => new(Counter);

	internal sealed class ProbeScope : IDisposable
	{
		private readonly AsyncLocal<int[]?> _counter;
		private readonly int[]? _enclosing;
		private readonly int[] _cell = [0];

		internal ProbeScope(AsyncLocal<int[]?> counter)
		{
			_counter = counter;
			_enclosing = counter.Value;
			counter.Value = _cell;
		}

		/// <summary>How many probes have been recorded since this scope began.</summary>
		internal int Value => _cell[0];

		public void Dispose() => _counter.Value = _enclosing;
	}
}
