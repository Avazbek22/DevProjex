using System.Text.Json;
using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.DesktopControl;

internal static class DesktopRequestEnvelopeReader
{
	public static T? Read<T>(string path, JsonSerializerOptions options)
	{
		using var stream = OpenBounded(path, FileOptions.SequentialScan);
		return JsonSerializer.Deserialize<T>(stream, options);
	}

	public static async Task<T?> ReadAsync<T>(
		string path,
		JsonSerializerOptions options,
		CancellationToken cancellationToken)
	{
		await using var stream = OpenBounded(
			path,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		return await JsonSerializer.DeserializeAsync<T>(
			stream,
			options,
			cancellationToken).ConfigureAwait(false);
	}

	internal static Stream CreateBounded(Stream stream) =>
		new MaximumLengthReadStream(
			stream,
			DesktopProtocol.MaximumMessageBytes,
			static () => new IOException("Desktop request exceeds the protocol limit."));

	private static Stream OpenBounded(string path, FileOptions options) =>
		CreateBounded(new FileStream(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 4 * 1024,
			options));
}
