using DevProjex.Infrastructure.Secrets;

namespace DevProjex.Mcp;

internal static class McpSecretDetectors
{
	private static readonly object Sync = new();
	private static readonly List<WeakReference<GitleaksSecretDetector>> Detectors = [];

	public static ISecretDetector Create()
	{
		var detector = new GitleaksSecretDetector();
		lock (Sync)
		{
			Detectors.RemoveAll(static item => !item.TryGetTarget(out _));
			Detectors.Add(new(detector));
		}
		return new TrackedDetector(detector);
	}

	public static void Reset()
	{
		lock (Sync)
			foreach (var reference in Detectors)
				if (reference.TryGetTarget(out var detector)) detector.Reset();
	}

	private sealed class TrackedDetector(GitleaksSecretDetector detector) : ISecretDetector
	{
		public string RulesIdentity
		{
			get
			{
				using var activity = McpMemoryActivity.Enter();
				return detector.RulesIdentity;
			}
		}
		public void WarmUp(CancellationToken cancellationToken = default)
		{
			using var activity = McpMemoryActivity.Enter();
			detector.WarmUp(cancellationToken);
		}
		public IReadOnlyList<DetectedSecret> Detect(string path, string content, CancellationToken cancellationToken = default) =>
			Detect(path, content.AsSpan(), cancellationToken);
		public IReadOnlyList<DetectedSecret> Detect(string path, ReadOnlySpan<char> content, CancellationToken cancellationToken = default)
		{
			using var activity = McpMemoryActivity.Enter();
			return detector.Detect(path, content, cancellationToken);
		}
		public IReadOnlyList<DetectedSecret> Detect(string path, ReadOnlySpan<char> content, SecretFileInspectionBudget budget, CancellationToken cancellationToken = default)
		{
			using var activity = McpMemoryActivity.Enter();
			return detector.Detect(path, content, budget, cancellationToken);
		}
	}
}
