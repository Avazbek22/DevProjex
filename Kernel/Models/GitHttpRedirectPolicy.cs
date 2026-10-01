namespace DevProjex.Kernel.Models;

public static class GitHttpRedirectPolicy
{
	private static readonly AsyncLocal<bool> RedirectsBlocked = new();

	public static bool BlocksRedirects => RedirectsBlocked.Value;

	public static IDisposable BlockForCurrentOperation()
	{
		var previous = RedirectsBlocked.Value;
		RedirectsBlocked.Value = true;
		return new Scope(previous);
	}

	private sealed class Scope(bool previous) : IDisposable
	{
		private int _disposed;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) == 0)
				RedirectsBlocked.Value = previous;
		}
	}
}
