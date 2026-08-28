using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DevProjex.Application.Compression;

/// <summary>
/// One immutable selection identity shared by all content transformations in an operation. The
/// ordered paths preserve output order; the fingerprint is order-insensitive like the former
/// per-subsystem selection keys.
/// </summary>
public sealed record ContentSelectionSnapshot(
	long Revision,
	IReadOnlyList<string> OrderedPaths,
	string SelectionFingerprint)
{
	public static ContentSelectionSnapshot Create(
		string projectRoot,
		IReadOnlyList<string> orderedPaths,
		long revision = 0)
		=> CreateCore(projectRoot, orderedPaths, CancellationToken.None, revision);

	public static ContentSelectionSnapshot CreateWithCancellation(
		string projectRoot,
		IReadOnlyList<string> orderedPaths,
		CancellationToken cancellationToken,
		long revision = 0)
		=> CreateCore(projectRoot, orderedPaths, cancellationToken, revision);

	private static ContentSelectionSnapshot CreateCore(
		string projectRoot,
		IReadOnlyList<string> orderedPaths,
		CancellationToken cancellationToken,
		long revision)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		ArgumentNullException.ThrowIfNull(orderedPaths);
		cancellationToken.ThrowIfCancellationRequested();
		var normalizedProjectRoot = PathUtility.Normalize(projectRoot);
		if (ContentPathOrdering.IsStrictlyOrderedUnique(orderedPaths, cancellationToken))
		{
			using var canonicalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
			Append(canonicalHash, normalizedProjectRoot);
			var canonicalPaths = new string[orderedPaths.Count];
			for (var index = 0; index < orderedPaths.Count; index++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var path = orderedPaths[index];
				canonicalPaths[index] = path;
				Append(canonicalHash, path);
			}

			return new ContentSelectionSnapshot(
				revision,
				canonicalPaths,
				Convert.ToHexString(canonicalHash.GetHashAndReset()));
		}

		var unique = new HashSet<string>(PathComparer.Default);
		var paths = new List<string>(orderedPaths.Count);
		var pathsAreSorted = true;
		string? previousPath = null;
		for (var index = 0; index < orderedPaths.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var path = orderedPaths[index];
			if (!string.IsNullOrWhiteSpace(path) && unique.Add(path))
			{
				if (previousPath is not null && PathComparer.Default.Compare(previousPath, path) > 0)
					pathsAreSorted = false;
				paths.Add(path);
				previousPath = path;
			}
		}

		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		Append(hash, normalizedProjectRoot);
		IReadOnlyList<string> fingerprintPaths = paths;
		if (!pathsAreSorted)
		{
			var sortedPaths = paths.ToArray();
			CancellationAwareSort.Sort(sortedPaths, PathComparer.Default, cancellationToken);
			fingerprintPaths = sortedPaths;
		}

		for (var index = 0; index < fingerprintPaths.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var path = fingerprintPaths[index];
			Append(hash, path);
		}
		return new ContentSelectionSnapshot(
			revision,
			paths.ToArray(),
			Convert.ToHexString(hash.GetHashAndReset()));
	}

	public string CreateTransformFingerprint(string transformIdentity)
	{
		if (transformIdentity.Length == 0)
			return SelectionFingerprint;
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		Append(hash, SelectionFingerprint);
		Append(hash, transformIdentity);
		return Convert.ToHexString(hash.GetHashAndReset());
	}

	private static void Append(IncrementalHash hash, string value)
	{
		var byteCount = Encoding.UTF8.GetByteCount(value);
		Span<byte> length = stackalloc byte[sizeof(int)];
		BinaryPrimitives.WriteInt32LittleEndian(length, byteCount);
		hash.AppendData(length);
		if (byteCount <= 512)
		{
			Span<byte> bytes = stackalloc byte[byteCount];
			Encoding.UTF8.GetBytes(value, bytes);
			hash.AppendData(bytes);
			return;
		}

		var rented = ArrayPool<byte>.Shared.Rent(byteCount);
		try
		{
			var written = Encoding.UTF8.GetBytes(value, rented);
			hash.AppendData(rented.AsSpan(0, written));
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(rented, clearArray: true);
		}
	}
}
