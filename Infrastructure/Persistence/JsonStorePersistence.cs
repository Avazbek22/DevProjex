namespace DevProjex.Infrastructure.Persistence;

internal static class JsonStorePersistence
{
    internal const long SmallDocumentMaximumBytes = 8 * 1024 * 1024;
    private static readonly Encoding StrictUtf16LittleEndian = new UnicodeEncoding(
        bigEndian: false,
        byteOrderMark: true,
        throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16BigEndian = new UnicodeEncoding(
        bigEndian: true,
        byteOrderMark: true,
        throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf32LittleEndian = new UTF32Encoding(
        bigEndian: false,
        byteOrderMark: true,
        throwOnInvalidCharacters: true);
    private static readonly Encoding StrictUtf32BigEndian = new UTF32Encoding(
        bigEndian: true,
        byteOrderMark: true,
        throwOnInvalidCharacters: true);

    // A future backup is authoritative even when the primary is missing, corrupt or older.
    // Treating the complete file set as read-only prevents recovery from downgrading both copies.
    public static bool ContainsFutureDocument(
        JsonStoreFileSet fileSet,
        int currentSchemaVersion,
        int? currentDefaultsRevision = null,
        long maximumDocumentBytes = long.MaxValue) =>
        IsFutureDocument(fileSet.PrimaryPath, currentSchemaVersion, currentDefaultsRevision, maximumDocumentBytes) ||
        IsFutureDocument(fileSet.BackupPath, currentSchemaVersion, currentDefaultsRevision, maximumDocumentBytes);

    public static bool TryReadNormalized<TDocument>(
        string path,
        JsonSerializerOptions serializerOptions,
        Func<TDocument> createDefault,
        Func<TDocument, TDocument> normalize,
        out TDocument document,
        out bool requiresRewrite,
        long maximumDocumentBytes = long.MaxValue)
    {
        document = createDefault();
        requiresRewrite = false;

        if (!File.Exists(path))
            return false;

        try
        {
            if (!IsDocumentWithinSizeLimit(path, maximumDocumentBytes))
                return false;

            var json = File.ReadAllText(path);
            var deserialized = JsonSerializer.Deserialize<TDocument>(json, serializerOptions);
            if (deserialized is null)
                return false;

            // Normalize only documents that were parsed successfully.
            // Invalid payloads stay untouched so a backup or a human can recover them.
            var originalSnapshot = JsonSerializer.Serialize(deserialized, serializerOptions);
            var normalized = normalize(deserialized);
            var normalizedSnapshot = JsonSerializer.Serialize(normalized, serializerOptions);
            requiresRewrite = !string.Equals(originalSnapshot, normalizedSnapshot, StringComparison.Ordinal);
            document = normalized;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryWriteAtomic<TDocument>(
        JsonStoreFileSet fileSet,
        TDocument document,
        JsonSerializerOptions serializerOptions)
		=> TryWriteAtomic(
			fileSet,
			document,
			serializerOptions,
			flushToDisk: false,
			maximumPayloadBytes: long.MaxValue,
			requireBackup: false,
			JsonStoreWriteOperations.Default);

	public static bool TryWriteAtomic<TDocument>(
		JsonStoreFileSet fileSet,
		TDocument document,
		JsonSerializerOptions serializerOptions,
		long maximumPayloadBytes)
		=> TryWriteAtomic(
			fileSet,
			document,
			serializerOptions,
			flushToDisk: false,
			maximumPayloadBytes,
			requireBackup: false,
			JsonStoreWriteOperations.Default);

	public static bool TryWriteAtomicDurable<TDocument>(
		JsonStoreFileSet fileSet,
		TDocument document,
		JsonSerializerOptions serializerOptions)
		=> TryWriteAtomic(
			fileSet,
			document,
			serializerOptions,
			flushToDisk: true,
			maximumPayloadBytes: long.MaxValue,
			requireBackup: true,
			JsonStoreWriteOperations.Default);

	public static bool TryWriteAtomicDurable<TDocument>(
		JsonStoreFileSet fileSet,
		TDocument document,
		JsonSerializerOptions serializerOptions,
		long maximumPayloadBytes)
		=> TryWriteAtomic(
			fileSet,
			document,
			serializerOptions,
			flushToDisk: true,
			maximumPayloadBytes,
			requireBackup: true,
			JsonStoreWriteOperations.Default);

	internal static bool TryWriteAtomicDurable<TDocument>(
		JsonStoreFileSet fileSet,
		TDocument document,
		JsonSerializerOptions serializerOptions,
		JsonStoreWriteOperations writeOperations) =>
		TryWriteAtomic(
			fileSet,
			document,
			serializerOptions,
			flushToDisk: true,
			maximumPayloadBytes: long.MaxValue,
			requireBackup: true,
			writeOperations);

	private static bool TryWriteAtomic<TDocument>(
		JsonStoreFileSet fileSet,
		TDocument document,
		JsonSerializerOptions serializerOptions,
		bool flushToDisk,
		long maximumPayloadBytes,
		bool requireBackup,
		JsonStoreWriteOperations writeOperations)
    {
		ArgumentNullException.ThrowIfNull(writeOperations);
		string? tempPath = null;
        try
        {
            if (string.IsNullOrWhiteSpace(fileSet.DirectoryPath))
                return false;

            Directory.CreateDirectory(fileSet.DirectoryPath);

			var payload = JsonSerializer.SerializeToUtf8Bytes(document, serializerOptions);
			if (payload.LongLength > maximumPayloadBytes)
				return false;
			tempPath = Path.Combine(fileSet.DirectoryPath, $"{fileSet.FileName}.{Guid.NewGuid():N}.tmp");
			using (var stream = new FileStream(
				       tempPath,
				       FileMode.CreateNew,
				       FileAccess.Write,
				       FileShare.None,
				       bufferSize: 16 * 1024,
				       flushToDisk ? FileOptions.WriteThrough : FileOptions.None))
			{
				stream.Write(payload);
				stream.Flush(flushToDisk);
			}

            if (File.Exists(fileSet.PrimaryPath))
            {
				try
				{
					// Replace keeps the primary update atomic and creates the rollback snapshot.
					writeOperations.Replace(tempPath, fileSet.PrimaryPath, fileSet.BackupPath);
				}
				catch (NotSupportedException)
				{
					File.Move(tempPath, fileSet.PrimaryPath, overwrite: true);
				}
            }
			else
            {
				File.Move(tempPath, fileSet.PrimaryPath);
            }

			var backupMirrored = TryMirrorPrimaryToBackup(fileSet, writeOperations);
			return backupMirrored || !requireBackup;
        }
        catch
        {
            return false;
        }
		finally
		{
			if (tempPath is not null)
			{
				try
				{
					File.Delete(tempPath);
				}
				catch
				{
					// A failed atomic write must not mask its original persistence result.
				}
			}
		}
    }

	private static bool TryMirrorPrimaryToBackup(
		JsonStoreFileSet fileSet,
		JsonStoreWriteOperations writeOperations)
    {
        try
        {
            // The backup must mirror the final committed primary snapshot.
            // This keeps recovery deterministic across multiple processes.
            if (File.Exists(fileSet.PrimaryPath))
				writeOperations.Copy(fileSet.PrimaryPath, fileSet.BackupPath, overwrite: true);
			return true;
        }
        catch
        {
			return false;
        }
    }

    internal static bool IsDocumentWithinSizeLimit(string path, long maximumDocumentBytes)
    {
        if (maximumDocumentBytes < 0)
            return false;

        try
        {
            return new FileInfo(path).Length <= maximumDocumentBytes;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFutureDocument(
        string path,
        int currentSchemaVersion,
        int? currentDefaultsRevision,
        long maximumDocumentBytes)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            // An unbounded document cannot be classified safely and must not be downgraded.
            if (!IsDocumentWithinSizeLimit(path, maximumDocumentBytes))
                return true;

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = ParseDocument(stream);
            var root = document.RootElement;
            var schemaVersion = root.TryGetProperty("schemaVersion", out var schemaElement) &&
                                schemaElement.TryGetInt32(out var schema)
                ? schema
                : 0;
            if (schemaVersion > currentSchemaVersion)
                return true;

            return currentDefaultsRevision is { } currentRevision &&
                   root.TryGetProperty("defaultsRevision", out var revisionElement) &&
                   revisionElement.TryGetInt32(out var revision) &&
                   revision > currentRevision;
        }
        catch
        {
            return false;
        }
    }

    private static JsonDocument ParseDocument(FileStream stream)
    {
        var encoding = DetectUnicodeEncoding(stream);
        if (encoding is null)
            return JsonDocument.Parse(stream);

        using var reader = new StreamReader(
            stream,
            encoding,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        return JsonDocument.Parse(reader.ReadToEnd());
    }

    private static Encoding? DetectUnicodeEncoding(FileStream stream)
    {
        Span<byte> prefix = stackalloc byte[4];
        var bytesRead = stream.Read(prefix);
        stream.Position = 0;
        if (bytesRead >= 4)
        {
            if (prefix[0] == 0xFF && prefix[1] == 0xFE && prefix[2] == 0x00 && prefix[3] == 0x00)
                return StrictUtf32LittleEndian;
            if (prefix[0] == 0x00 && prefix[1] == 0x00 && prefix[2] == 0xFE && prefix[3] == 0xFF)
                return StrictUtf32BigEndian;
        }
        if (bytesRead >= 2)
        {
            if (prefix[0] == 0xFF && prefix[1] == 0xFE)
                return StrictUtf16LittleEndian;
            if (prefix[0] == 0xFE && prefix[1] == 0xFF)
                return StrictUtf16BigEndian;
        }
        return null;
    }
}

internal sealed class JsonStoreWriteOperations(
	Action<string, string, string> replace,
	Action<string, string, bool> copy)
{
	internal static JsonStoreWriteOperations Default { get; } = new(
		static (source, destination, backup) => File.Replace(source, destination, backup),
		static (source, destination, overwrite) => File.Copy(source, destination, overwrite));

	internal void Replace(string source, string destination, string backup) =>
		replace(source, destination, backup);

	internal void Copy(string source, string destination, bool overwrite) =>
		copy(source, destination, overwrite);
}
