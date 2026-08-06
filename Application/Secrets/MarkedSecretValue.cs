using System.Buffers;
using System.Security.Cryptography;

namespace DevProjex.Application.Secrets;

public enum MarkedSecretValidationError
{
	None = 0,
	Empty = 1,
	TooShort = 2,
	TooLong = 3,
	Multiline = 4
}

public sealed record MarkedSecretValue(
	string NormalizedValue,
	string Hash,
	int LeadingCharactersRemoved)
{
	public int Length => NormalizedValue.Length;
}

public static class MarkedSecretValueNormalizer
{
	public const int MinimumLength = 8;
	public const int MaximumLength = 512;
	public const int PersistedHashLength = 12;

	public static bool TryCreate(
		string? value,
		out MarkedSecretValue normalized,
		out MarkedSecretValidationError error)
	{
		var result = Normalize(value, out var start);
		if (result.Length == 0)
		{
			normalized = null!;
			error = MarkedSecretValidationError.Empty;
			return false;
		}
		if (result.Contains('\r') || result.Contains('\n'))
		{
			normalized = null!;
			error = MarkedSecretValidationError.Multiline;
			return false;
		}
		if (result.Length < MinimumLength)
		{
			normalized = null!;
			error = MarkedSecretValidationError.TooShort;
			return false;
		}
		if (result.Length > MaximumLength)
		{
			normalized = null!;
			error = MarkedSecretValidationError.TooLong;
			return false;
		}

		normalized = new MarkedSecretValue(result, ComputeHash(result), start);
		error = MarkedSecretValidationError.None;
		return true;
	}

	public static string Normalize(string? value) => Normalize(value, out _);

	private static string Normalize(string? value, out int leadingCharactersRemoved)
	{
		var source = value ?? string.Empty;
		var start = 0;
		var end = source.Length;
		while (start < end && char.IsWhiteSpace(source[start]))
			start++;
		while (end > start && char.IsWhiteSpace(source[end - 1]))
			end--;

		if (end - start >= 2 &&
		    source[start] is '\'' or '"' &&
		    source[end - 1] == source[start])
		{
			start++;
			end--;
		}

		leadingCharactersRemoved = start;
		return source[start..end].Replace("\r\n", "\n", StringComparison.Ordinal);
	}

	public static string ComputeHash(ReadOnlySpan<char> normalizedValue)
	{
		var maximumByteCount = Encoding.UTF8.GetMaxByteCount(normalizedValue.Length);
		var rented = ArrayPool<byte>.Shared.Rent(maximumByteCount);
		try
		{
			var byteCount = Encoding.UTF8.GetBytes(normalizedValue, rented);
			Span<byte> hash = stackalloc byte[32];
			SHA256.HashData(rented.AsSpan(0, byteCount), hash);
			return Convert.ToHexString(hash[..6]).ToLowerInvariant();
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(rented, clearArray: true);
		}
	}

	public static string? ExtractKey(ReadOnlySpan<char> line, int valueStart)
	{
		var cursor = Math.Clamp(valueStart, 0, line.Length) - 1;
		while (cursor >= 0 && (char.IsWhiteSpace(line[cursor]) || line[cursor] is '\'' or '"'))
			cursor--;
		if (cursor < 0 || line[cursor] is not ('=' or ':'))
			return null;

		cursor--;
		while (cursor >= 0 && (char.IsWhiteSpace(line[cursor]) || line[cursor] is '\'' or '"'))
			cursor--;
		var keyEnd = cursor + 1;
		while (cursor >= 0 && IsKeyCharacter(line[cursor]))
			cursor--;
		var key = line[(cursor + 1)..keyEnd];
		return key.IsEmpty ? null : key.ToString();
	}

	private static bool IsKeyCharacter(char character) =>
		char.IsLetterOrDigit(character) || character is '_' or '-' or '.' or ':';
}
