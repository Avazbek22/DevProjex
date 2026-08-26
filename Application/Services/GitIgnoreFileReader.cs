using System.Security.Cryptography;
using DevProjex.Application.Diagnostics;

namespace DevProjex.Application.Services;

public static class GitIgnoreFileReader
{
	public const long MaximumFileSizeBytes = 100L * 1024 * 1024;

	private static readonly UTF8Encoding GitTextEncoding = new(
		encoderShouldEmitUTF8Identifier: false,
		throwOnInvalidBytes: true);

	public static GitIgnoreFileContent Read(string path) =>
		ReadWithCancellation(path, CancellationToken.None);

	public static GitIgnoreFileContent ReadWithCancellation(
		string path,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		IgnorePipelineDiagnostics.RecordGitIgnoreSourceReadRequest();
		UnixFileTypeInspector.EnsureRegularFile(path);
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
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		var totalBytesRead = 0;
		while (totalBytesRead < bytes.Length)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var bytesRead = stream.Read(bytes.AsSpan(totalBytesRead));
			if (bytesRead == 0)
				throw new IOException("The .gitignore source changed while it was being read.");

			hash.AppendData(bytes, totalBytesRead, bytesRead);
			totalBytesRead += bytesRead;
		}

		cancellationToken.ThrowIfCancellationRequested();
		Span<byte> overflowProbe = stackalloc byte[1];
		if (stream.Read(overflowProbe) != 0 || stream.Length != initialLength)
			throw new IOException("The .gitignore source changed while it was being read.");

		var contentOffset = HasUtf8ByteOrderMark(bytes) ? 3 : 0;
		var content = GitTextEncoding.GetString(bytes.AsSpan(contentOffset));
		IgnorePipelineDiagnostics.RecordGitIgnoreSourceBytes(initialLength);
		return new GitIgnoreFileContent(
			content,
			initialLength,
			Convert.ToHexString(hash.GetHashAndReset()));
	}

	public static string ReadAllText(string path) => Read(path).Content;

	public static IReadOnlyList<string> ReadLines(string path) =>
		SplitLines(ReadAllText(path));

	public static IReadOnlyList<string> SplitLines(string content) =>
		EnumerateLines(content).ToArray();

	public static IEnumerable<string> EnumerateLines(string content) =>
		EnumerateLinesWithCancellation(content, CancellationToken.None);

	public static IEnumerable<string> EnumerateLinesWithCancellation(
		string content,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(content);
		cancellationToken.ThrowIfCancellationRequested();
		if (content.Length == 0)
			yield break;

		var lineStart = 0;
		for (var index = 0; index < content.Length; index++)
		{
			if ((index & 0xFFF) == 0)
				cancellationToken.ThrowIfCancellationRequested();
			if (content[index] != '\n')
				continue;

			var lineEnd = index;
			if (lineEnd > lineStart && content[lineEnd - 1] == '\r')
				lineEnd--;
			cancellationToken.ThrowIfCancellationRequested();
			yield return content[lineStart..lineEnd];
			lineStart = index + 1;
		}

		if (lineStart < content.Length)
		{
			cancellationToken.ThrowIfCancellationRequested();
			yield return content[lineStart..];
		}
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
