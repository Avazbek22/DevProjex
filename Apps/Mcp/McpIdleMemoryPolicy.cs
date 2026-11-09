using System.Diagnostics;

namespace DevProjex.Mcp;

internal sealed record McpIdleMemoryOptions
{
	public TimeSpan Delay { get; init; } = TimeSpan.FromSeconds(45);
	public long Threshold { get; init; } = 256L * 1024 * 1024;
	public TimeProvider Clock { get; init; } = TimeProvider.System;
	public Func<long> PrivateBytes { get; init; } = static () =>
	{
		using var process = Process.GetCurrentProcess();
		return process.PrivateMemorySize64;
	};
	public Action Collect { get; init; } = static () => GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
	public Action Reset { get; init; } = McpSecretDetectors.Reset;
	public Func<TimeSpan, TimeProvider, CancellationToken, Task> Wait { get; init; } = Task.Delay;
}

internal sealed class McpIdleMemoryPolicy(McpIdleMemoryOptions options) : IAsyncDisposable
{
	private readonly object sync = new();
	private readonly List<Task> pending = [];
	private CancellationTokenSource? delay;
	private int active;
	private bool disposed;

	public IDisposable EnterCall()
	{
		lock (sync)
		{
			ObjectDisposedException.ThrowIf(disposed, this);
			delay?.Cancel();
			active++;
		}
		return new Call(this, McpMemoryActivity.Enter());
	}

	internal Task Completion { get { lock (sync) return Task.WhenAll(pending); } }

	private void Leave()
	{
		lock (sync)
		{
			if (--active != 0 || disposed) return;
			delay?.Dispose();
			delay = new();
			pending.RemoveAll(static task => task.IsCompletedSuccessfully);
			pending.Add(ReclaimAsync(delay.Token));
		}
	}

	private async Task ReclaimAsync(CancellationToken token)
	{
		try
		{
			await options.Wait(options.Delay, options.Clock, token).ConfigureAwait(false);
			using var maintenance = await McpMemoryActivity.EnterMaintenanceAsync(token).ConfigureAwait(false);
			token.ThrowIfCancellationRequested();
			if (options.PrivateBytes() <= options.Threshold) return;
			options.Collect();
			if (options.PrivateBytes() > options.Threshold)
			{
				options.Reset();
				options.Collect();
			}
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested) { }
		catch (Exception exception)
		{
			Trace.TraceWarning("MCP idle memory reclamation failed: {0}", exception.GetType().Name);
		}
	}

	public async ValueTask DisposeAsync()
	{
		Task[] tasks;
		lock (sync)
		{
			if (disposed) return;
			disposed = true;
			delay?.Cancel();
			tasks = pending.ToArray();
		}
		await Task.WhenAll(tasks).ConfigureAwait(false);
		delay?.Dispose();
	}

	private sealed class Call(McpIdleMemoryPolicy owner, IDisposable activity) : IDisposable
	{
		private int disposed;
		public void Dispose()
		{
			if (Interlocked.Exchange(ref disposed, 1) != 0) return;
			activity.Dispose();
			owner.Leave();
		}
	}
}
