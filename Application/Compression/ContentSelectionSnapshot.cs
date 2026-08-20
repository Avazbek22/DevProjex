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
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		ArgumentNullException.ThrowIfNull(orderedPaths);
		var unique = new HashSet<string>(PathComparer.Default);
		var paths = new List<string>(orderedPaths.Count);
		foreach (var path in orderedPaths)
		{
			if (!string.IsNullOrWhiteSpace(path) && unique.Add(path))
				paths.Add(path);
		}

		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		Append(hash, PathUtility.Normalize(projectRoot));
		foreach (var path in paths.OrderBy(static path => path, PathComparer.Default))
			Append(hash, path);
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
			ArrayPool<byte>.Shared.Return(rented);
		}
	}
}
