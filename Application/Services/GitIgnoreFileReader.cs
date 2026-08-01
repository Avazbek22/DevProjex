using System.Security.Cryptography;
using System.Text;
using DevProjex.Application.Diagnostics;

namespace DevProjex.Application.Services;

public static class GitIgnoreFileReader
{
	public const long MaximumFileSizeBytes = 100L * 1024 * 1024;

	private static readonly UTF8Encoding GitTextEncoding = new(
		encoderShouldEmitUTF8Identifier: false,
		throwOnInvalidBytes: true);

	public static GitIgnoreFileContent Read(string path)
	{
		IgnorePipelineDiagnostics.RecordGitIgnoreSourceReadRequest();
		using var stream = new FileStream(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.ReadWrite | FileShare.Delete,
			bufferSize: 4096,
			FileOptions.SequentialScan);
		var initialLength = stream.Length;
		if (initialLength > MaximumFileSizeBytes)
			throw new IOException($"The .gitignore source exceeds {MaximumFileSizeBytes} bytes.");

		var bytes = GC.AllocateUninitializedArray<byte>(checked((int)initialLength));
		var totalBytesRead = 0;
		while (totalBytesRead < bytes.Length)
		{
			var bytesRead = stream.Read(bytes.AsSpan(totalBytesRead));
			if (bytesRead == 0)
				throw new IOException("The .gitignore source changed while it was being read.");

			totalBytesRead += bytesRead;
		}

		Span<byte> overflowProbe = stackalloc byte[1];
		if (stream.Read(overflowProbe) != 0 || stream.Length != initialLength)
			throw new IOException("The .gitignore source changed while it was being read.");

		var contentOffset = HasUtf8ByteOrderMark(bytes) ? 3 : 0;
		var content = GitTextEncoding.GetString(bytes.AsSpan(contentOffset));
		IgnorePipelineDiagnostics.RecordGitIgnoreSourceBytes(initialLength);
		return new GitIgnoreFileContent(
			content,
			initialLength,
			Convert.ToHexString(SHA256.HashData(bytes)));
	}

	public static string ReadAllText(string path) => Read(path).Content;

	public static IReadOnlyList<string> ReadLines(string path) =>
		SplitLines(ReadAllText(path));

	public static IReadOnlyList<string> SplitLines(string content)
	{
		if (content.Length == 0)
			return [];

		var lines = new List<string>();
		var lineStart = 0;
		for (var index = 0; index < content.Length; index++)
		{
			if (content[index] != '\n')
				continue;

			var lineEnd = index;
			if (lineEnd > lineStart && content[lineEnd - 1] == '\r')
				lineEnd--;
			lines.Add(content[lineStart..lineEnd]);
			lineStart = index + 1;
		}

		if (lineStart < content.Length)
			lines.Add(content[lineStart..]);

		return lines;
	}

	private static bool HasUtf8ByteOrderMark(ReadOnlySpan<byte> bytes) =>
		bytes.Length >= 3 &&
		bytes[0] == 0xEF &&
		bytes[1] == 0xBB &&
		bytes[2] == 0xBF;
}

public readonly record struct GitIgnoreFileContent(
	string Content,
	long LengthBytes,
	string ContentFingerprint);
