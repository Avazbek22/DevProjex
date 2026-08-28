using System.Text;

namespace DevProjex.Kernel;

public static class TrailingLineEndingTrimming
{
	public static int GetTrimmedLength(ReadOnlySpan<char> value)
	{
		var length = value.Length;
		while (length > 0 && value[length - 1] is '\r' or '\n')
			length--;
		return length;
	}

	public static void Trim(StringBuilder value)
	{
		ArgumentNullException.ThrowIfNull(value);
		var length = value.Length;
		while (length > 0 && value[length - 1] is '\r' or '\n')
			length--;
		value.Length = length;
	}
}
