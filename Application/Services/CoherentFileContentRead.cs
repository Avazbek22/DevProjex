using System.Security;

namespace DevProjex.Application.Services;

internal static class SourceFileReadPolicy
{
	internal const FileShare Share = FileShare.Read | FileShare.Delete;
}

/// <summary>
/// Identifies the file version observed through the same handle that supplied its content.
/// Length and last-write time deliberately avoid another content hash. A same-length rewrite with
/// a restored timestamp is therefore indistinguishable; callers must treat that as the accepted
/// metadata-only freshness tradeoff.
/// </summary>
internal readonly record struct FileContentIdentity(long Length, long LastWriteTimeUtcTicks)
{
	internal static FileContentIdentity? TryCapture(FileStream stream)
	{
		try
		{
			return new FileContentIdentity(
				RandomAccess.GetLength(stream.SafeFileHandle),
				File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks);
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
		{
			return null;
		}
	}

	internal bool IsCurrent(string path)
	{
		try
		{
			using var handle = File.OpenHandle(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete,
				FileOptions.RandomAccess);
			return RandomAccess.GetLength(handle) == Length &&
			       File.GetLastWriteTimeUtc(handle).Ticks == LastWriteTimeUtcTicks;
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
		{
			return false;
		}
	}
}

internal readonly record struct IdentifiedFileContentMetricsResult(
	FileContentMetricsResult Result,
	FileContentIdentity? Identity);

internal readonly record struct IdentifiedContentReadFact(
	ContentReadFact Fact,
	FileContentIdentity? Identity)
{
	internal static IdentifiedContentReadFact Unidentified(FileContentClassification classification) =>
		new(new ContentReadFact(null, classification, null, null), null);
}

internal readonly record struct IdentifiedCompleteTextFileBuffer(
	ICompleteTextFileBuffer Buffer,
	FileContentIdentity? Identity);

internal readonly record struct BudgetedContentReadResult(
	ContentReadFact Fact,
	FileContentIdentity? Identity,
	WeightedByteBudget.Lease? Lease);

/// <summary>
/// Internal prewarm contract that opens once, reserves from that handle's length, and only then
/// materializes content. Other analyzer implementations use the conservative fallback budget.
/// </summary>
internal interface IPrewarmFileContentAnalyzer
{
	ValueTask<IdentifiedFileContentMetricsResult> GetClassifiedMetricsWithIdentityAsync(
		string path,
		CancellationToken cancellationToken = default);

	ValueTask<BudgetedContentReadResult> ReadFactWithBudgetAsync(
		string path,
		long maximumReadBytes,
		WeightedByteBudget byteBudget,
		SemaphoreSlim decodeScratchGate,
		CancellationToken cancellationToken = default);
}

/// <summary>
/// Internal file-reading contract for consumers whose cache metadata must describe the same
/// handle that supplied the inspected bytes.
/// </summary>
internal interface ICoherentFileContentAnalyzer
{
	ValueTask<IdentifiedContentReadFact> ReadFactWithIdentityAsync(
		string path,
		long maximumReadBytes,
		CancellationToken cancellationToken = default);

	ValueTask<IdentifiedCompleteTextFileBuffer> OpenCompleteTextBufferWithIdentityAsync(
		string path,
		long maximumBytes,
		CancellationToken cancellationToken = default);
}
