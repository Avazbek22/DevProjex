#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace DevProjex.ReleaseValidation;

public sealed class BundlePayloadEntry
{
    public string Path { get; init; } = string.Empty;
    public long Offset { get; init; }
    public long Size { get; init; }
    public long CompressedSize { get; init; }
    public byte FileType { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public string[] ManagedResources { get; init; } = Array.Empty<string>();
    public bool IsManagedAssembly { get; init; }
}

public sealed class BundlePayloadInspection
{
    public uint MajorVersion { get; init; }
    public uint MinorVersion { get; init; }
    public BundlePayloadEntry[] Files { get; init; } = Array.Empty<BundlePayloadEntry>();
}

public static class ReleasePayloadInspector
{
    private static readonly byte[] BundleSignature =
    {
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
        0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
        0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
    };

    public static BundlePayloadInspection InspectBundle(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return InspectBundle(stream, path);
    }

    public static BundlePayloadInspection InspectBundle(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return InspectBundle(stream, "in-memory artifact");
    }

    private static BundlePayloadInspection InspectBundle(Stream stream, string displayPath)
    {
        var signatureOffset = FindSignature(stream);
        if (signatureOffset < sizeof(long))
        {
            throw new InvalidDataException($"'{displayPath}' is not a .NET single-file bundle.");
        }

        stream.Position = signatureOffset - sizeof(long);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var headerOffset = reader.ReadInt64();
        if (headerOffset <= 0 || headerOffset >= stream.Length)
        {
            throw new InvalidDataException($"'{displayPath}' has an invalid .NET bundle header offset '{headerOffset}'.");
        }

        stream.Position = headerOffset;
        var majorVersion = reader.ReadUInt32();
        var minorVersion = reader.ReadUInt32();
        var fileCount = reader.ReadInt32();
        if (majorVersion is < 1 or > 6 || minorVersion != 0 || fileCount < 0 || fileCount > 100_000)
        {
            throw new InvalidDataException($"'{displayPath}' has an unsupported .NET bundle manifest {majorVersion}.{minorVersion} with {fileCount} files.");
        }

        _ = reader.ReadString();
        if (majorVersion >= 2)
        {
            _ = reader.ReadInt64();
            _ = reader.ReadInt64();
            _ = reader.ReadInt64();
            _ = reader.ReadInt64();
            _ = reader.ReadUInt64();
        }

        var manifestEntries = new List<(long Offset, long Size, long CompressedSize, byte FileType, string Path)>(fileCount);
        for (var index = 0; index < fileCount; index++)
        {
            var offset = reader.ReadInt64();
            var size = reader.ReadInt64();
            var compressedSize = majorVersion >= 6 ? reader.ReadInt64() : 0;
            var fileType = reader.ReadByte();
            var relativePath = reader.ReadString().Replace('\\', '/');
            var storedSize = compressedSize == 0 ? size : compressedSize;
            if (offset < 0 || size < 0 || compressedSize < 0 || offset > stream.Length - storedSize)
            {
                throw new InvalidDataException($"'{displayPath}' has invalid bounds for bundle entry '{relativePath}'.");
            }

            manifestEntries.Add((offset, size, compressedSize, fileType, relativePath));
        }

        var entries = new List<BundlePayloadEntry>(fileCount);
        foreach (var entry in manifestEntries)
        {
            if (entry.CompressedSize != 0)
            {
                throw new InvalidDataException($"'{displayPath}' uses compression for bundle entry '{entry.Path}'.");
            }

            var hash = ComputeHash(stream, entry.Offset, entry.Size);
            var resources = entry.FileType == 1
                ? ReadManagedResources(stream, entry.Offset, entry.Size)
                : null;
            entries.Add(new BundlePayloadEntry
            {
                Path = entry.Path,
                Offset = entry.Offset,
                Size = entry.Size,
                CompressedSize = entry.CompressedSize,
                FileType = entry.FileType,
                Sha256 = hash,
                IsManagedAssembly = resources is not null,
                ManagedResources = resources ?? Array.Empty<string>()
            });
        }

        return new BundlePayloadInspection
        {
            MajorVersion = majorVersion,
            MinorVersion = minorVersion,
            Files = entries.ToArray()
        };
    }

    public static string[]? TryReadManagedResources(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadManagedResources(stream, 0, stream.Length);
    }

    public static void MutateBundleEntry(string path, string relativePath)
    {
        var inspection = InspectBundle(path);
        var entry = inspection.Files.SingleOrDefault(candidate =>
            string.Equals(candidate.Path, relativePath, StringComparison.Ordinal));
        if (entry is null || entry.Size == 0)
        {
            throw new InvalidDataException($"Bundle entry '{relativePath}' was not found or is empty in '{path}'.");
        }

        using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var mutationOffset = entry.Offset + (entry.Size / 2);
        stream.Position = mutationOffset;
        var value = stream.ReadByte();
        stream.Position = mutationOffset;
        stream.WriteByte((byte)(value ^ 0x01));
    }

    public static string MutateBundleResource(string path, string assemblyPath, string resourceName)
    {
        var inspection = InspectBundle(path);
        var entry = inspection.Files.SingleOrDefault(candidate =>
            string.Equals(candidate.Path, assemblyPath, StringComparison.Ordinal));
        if (entry is null || !entry.IsManagedAssembly)
        {
            throw new InvalidDataException($"Managed bundle entry '{assemblyPath}' was not found in '{path}'.");
        }

        using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        return MutateUtf8Name(stream, entry.Offset, entry.Size, resourceName);
    }

    public static string MutateManagedResource(string path, string resourceName)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        return MutateUtf8Name(stream, 0, stream.Length, resourceName);
    }

    private static long FindSignature(Stream stream)
    {
        var buffer = new byte[64 * 1024 + BundleSignature.Length - 1];
        var carry = 0;
        long absoluteOffset = 0;
        while (true)
        {
            var read = stream.Read(buffer, carry, buffer.Length - carry);
            if (read == 0)
            {
                return -1;
            }

            var available = carry + read;
            var index = IndexOf(buffer, available, BundleSignature);
            if (index >= 0)
            {
                return absoluteOffset - carry + index;
            }

            carry = Math.Min(BundleSignature.Length - 1, available);
            Buffer.BlockCopy(buffer, available - carry, buffer, 0, carry);
            absoluteOffset += read;
        }
    }

    private static int IndexOf(byte[] buffer, int length, byte[] needle)
    {
        for (var index = 0; index <= length - needle.Length; index++)
        {
            var found = true;
            for (var needleIndex = 0; needleIndex < needle.Length; needleIndex++)
            {
                if (buffer[index + needleIndex] == needle[needleIndex])
                {
                    continue;
                }

                found = false;
                break;
            }

            if (found)
            {
                return index;
            }
        }

        return -1;
    }

    private static string ComputeHash(Stream stream, long offset, long size)
    {
        stream.Position = offset;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        var remaining = size;
        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string[]? ReadManagedResources(Stream stream, long offset, long size)
    {
        if (size <= 0 || size > int.MaxValue)
        {
            return null;
        }

        stream.Position = offset;
        var bytes = new byte[(int)size];
        stream.ReadExactly(bytes);
        try
        {
            using var memory = new MemoryStream(bytes, writable: false);
            using var peReader = new PEReader(memory, PEStreamOptions.LeaveOpen);
            if (!peReader.HasMetadata)
            {
                return null;
            }

            var metadata = peReader.GetMetadataReader();
            return metadata.ManifestResources
                .Select(handle => metadata.GetManifestResource(handle))
                .Where(resource => resource.Implementation.IsNil)
                .Select(resource => metadata.GetString(resource.Name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    private static string MutateUtf8Name(Stream stream, long offset, long size, string name)
    {
        var needle = Encoding.UTF8.GetBytes(name);
        if (needle.Length == 0 || size > int.MaxValue)
        {
            throw new InvalidDataException($"Resource name '{name}' cannot be mutated.");
        }

        stream.Position = offset;
        var bytes = new byte[(int)size];
        stream.ReadExactly(bytes);
        var index = IndexOf(bytes, bytes.Length, needle);
        if (index < 0)
        {
            throw new InvalidDataException($"Resource name '{name}' was not found.");
        }

        var changedName = name.ToCharArray();
        var changedIndex = changedName.Length - 1;
        changedName[changedIndex] = changedName[changedIndex] == 'X' ? 'Y' : 'X';
        var replacement = Encoding.UTF8.GetBytes(changedName);
        if (replacement.Length != needle.Length)
        {
            throw new InvalidDataException($"Resource name '{name}' cannot be mutated without changing its byte length.");
        }

        stream.Position = offset + index;
        stream.Write(replacement, 0, replacement.Length);
        return new string(changedName);
    }
}
