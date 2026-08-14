namespace DevProjex.Infrastructure.Persistence;

internal static class JsonStorePersistence
{
    // A future backup is authoritative even when the primary is missing, corrupt or older.
    // Treating the complete file set as read-only prevents recovery from downgrading both copies.
    public static bool ContainsFutureDocument(
        JsonStoreFileSet fileSet,
        int currentSchemaVersion,
        int? currentDefaultsRevision = null) =>
        IsFutureDocument(fileSet.PrimaryPath, currentSchemaVersion, currentDefaultsRevision) ||
        IsFutureDocument(fileSet.BackupPath, currentSchemaVersion, currentDefaultsRevision);

    public static bool TryReadNormalized<TDocument>(
        string path,
        JsonSerializerOptions serializerOptions,
        Func<TDocument> createDefault,
        Func<TDocument, TDocument> normalize,
        out TDocument document,
        out bool requiresRewrite)
    {
        document = createDefault();
        requiresRewrite = false;

        if (!File.Exists(path))
            return false;

        try
        {
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
			maximumPayloadBytes: long.MaxValue);

	public static bool TryWriteAtomic<TDocument>(
		JsonStoreFileSet fileSet,
		TDocument document,
		JsonSerializerOptions serializerOptions,
		long maximumPayloadBytes)
		=> TryWriteAtomic(fileSet, document, serializerOptions, flushToDisk: false, maximumPayloadBytes);

	public static bool TryWriteAtomicDurable<TDocument>(
		JsonStoreFileSet fileSet,
		TDocument document,
		JsonSerializerOptions serializerOptions)
		=> TryWriteAtomic(
			fileSet,
			document,
			serializerOptions,
			flushToDisk: true,
			maximumPayloadBytes: long.MaxValue);

	public static bool TryWriteAtomicDurable<TDocument>(
		JsonStoreFileSet fileSet,
		TDocument document,
		JsonSerializerOptions serializerOptions,
		long maximumPayloadBytes)
		=> TryWriteAtomic(fileSet, document, serializerOptions, flushToDisk: true, maximumPayloadBytes);

	private static bool TryWriteAtomic<TDocument>(
		JsonStoreFileSet fileSet,
		TDocument document,
		JsonSerializerOptions serializerOptions,
		bool flushToDisk,
		long maximumPayloadBytes)
    {
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

            try
            {
                // Replace keeps the primary update atomic on supported platforms
                // and gives us a rollback snapshot for free when the file already exists.
                if (File.Exists(fileSet.PrimaryPath))
                    File.Replace(tempPath, fileSet.PrimaryPath, fileSet.BackupPath);
                else
                    File.Move(tempPath, fileSet.PrimaryPath);
            }
            catch
            {
                File.Move(tempPath, fileSet.PrimaryPath, overwrite: true);
            }

            TryMirrorPrimaryToBackup(fileSet);
            return true;
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

    private static void TryMirrorPrimaryToBackup(JsonStoreFileSet fileSet)
    {
        try
        {
            // The backup must mirror the final committed primary snapshot.
            // This keeps recovery deterministic across multiple processes.
            if (File.Exists(fileSet.PrimaryPath))
                File.Copy(fileSet.PrimaryPath, fileSet.BackupPath, overwrite: true);
        }
        catch
        {
            // Best effort only. The primary file remains authoritative.
        }
    }

    private static bool IsFutureDocument(
        string path,
        int currentSchemaVersion,
        int? currentDefaultsRevision)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
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
}
