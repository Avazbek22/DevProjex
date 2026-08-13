using System.Buffers;
using System.Security.Cryptography;

namespace DevProjex.Application.Secrets;

public interface IPersistentSecretIdentityProvider
{
	bool IsAvailable { get; }

	bool TryComputeDigest(ReadOnlySpan<char> normalizedValue, Span<byte> destination);
}

public static class PersistentSecretIdentity
{
	public const string V2Prefix = "v2:";
	public const int V2DigestByteLength = 32;
	public const int V2IdentifierLength = 67;

	public static bool TryCreateV2(
		IPersistentSecretIdentityProvider? provider,
		ReadOnlySpan<char> normalizedValue,
		out string identifier)
	{
		Span<byte> digest = stackalloc byte[V2DigestByteLength];
		try
		{
			if (provider is null || !provider.IsAvailable ||
			    !provider.TryComputeDigest(normalizedValue, digest))
			{
				identifier = string.Empty;
				return false;
			}

			identifier = V2Prefix + Convert.ToHexString(digest).ToLowerInvariant();
			return true;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(digest);
		}
	}

	public static bool IsLegacy(string? identity) =>
		identity is { Length: MarkedSecretValueNormalizer.PersistedHashLength } &&
		identity.All(char.IsAsciiHexDigit);

	public static bool IsV2(string? identity) =>
		identity is { Length: V2IdentifierLength } &&
		identity.StartsWith(V2Prefix, StringComparison.Ordinal) &&
		identity.AsSpan(V2Prefix.Length).ContainsOnlyAsciiHexDigits();

	public static bool IsSupported(string? identity) => IsLegacy(identity) || IsV2(identity);

	public static bool TryDecodeDigest(string identity, Span<byte> destination)
	{
		var hex = IsV2(identity) ? identity.AsSpan(V2Prefix.Length) : identity.AsSpan();
		return Convert.FromHexString(
			hex,
			destination,
			out var consumed,
			out var written) == OperationStatus.Done &&
		       consumed == hex.Length &&
		       written == hex.Length / 2;
	}

	private static bool ContainsOnlyAsciiHexDigits(this ReadOnlySpan<char> value)
	{
		foreach (var character in value)
		{
			if (!char.IsAsciiHexDigit(character))
				return false;
		}
		return true;
	}
}
