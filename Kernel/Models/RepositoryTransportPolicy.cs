namespace DevProjex.Kernel.Models;

/// <summary>
/// The Git transports a shipped DevProjex build accepts.
/// </summary>
/// <remarks>
/// Any assembly may ask whether the local <c>file://</c> transport is permitted, but only this
/// assembly and its declared test hosts can grant it: <see cref="AllowLocalFileTransport"/> is
/// internal, no shipped assembly is granted access to it, and no environment variable, profile,
/// configuration file, or command-line value reaches it. A test host opens the scope around the
/// operation that needs a synthetic <c>file://</c> remote and restores the previous state when the
/// scope is disposed.
/// <para>
/// A grant belongs to the operation that opened it and to the work that operation starts, because
/// the state travels with the execution context. It never reaches an unrelated operation, and it
/// is never revoked underneath work that was started while it was held.
/// </para>
/// </remarks>
public static class RepositoryTransportPolicy
{
	private static readonly AsyncLocal<bool> LocalFileTransportAllowed = new();

	/// <summary>
	/// Whether the current operation may treat a <c>file://</c> URL as a Git transport. A shipped
	/// build has no code path that opens a scope, so this is always <see langword="false"/> there.
	/// </summary>
	public static bool AllowsLocalFileTransport => LocalFileTransportAllowed.Value;

	/// <summary>
	/// Permits the local <c>file://</c> transport for the current operation until the returned
	/// scope is disposed. Intended for test hosts that build a synthetic local remote.
	/// </summary>
	internal static LocalFileTransportScope AllowLocalFileTransport()
	{
		var previous = LocalFileTransportAllowed.Value;
		LocalFileTransportAllowed.Value = true;
		return new LocalFileTransportScope(previous);
	}

	internal sealed class LocalFileTransportScope : IDisposable
	{
		private readonly bool _previous;
		private int _disposed;

		internal LocalFileTransportScope(bool previous) => _previous = previous;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) != 0)
				return;

			LocalFileTransportAllowed.Value = _previous;
		}
	}
}
