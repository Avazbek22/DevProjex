namespace DevProjex.Application.Secrets;

public static class SecretTokenBoundary
{
	public static bool HasBoundaries(ReadOnlySpan<char> content, int start, int length) =>
		start >= 0 &&
		length > 0 &&
		start <= content.Length - length &&
		IsBoundary(content, start) &&
		IsBoundary(content, start + length);

	public static bool IsContinuation(char character) =>
		char.IsLetterOrDigit(character) || character == '_';

	internal static bool IsBoundary(ReadOnlySpan<char> content, int position) =>
		position == 0 ||
		position == content.Length ||
		!IsContinuation(content[position - 1]) ||
		!IsContinuation(content[position]);
}
