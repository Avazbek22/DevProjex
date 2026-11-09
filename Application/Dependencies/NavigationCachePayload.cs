using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace DevProjex.Application.Dependencies;

internal static class NavigationCachePayload
{
	private const int MaximumValues = (int)(DependencyFactsEngine.MaximumNavigationCacheBytes / sizeof(int));

	public static byte[] Encode(IReadOnlyList<NavigationDeclaration> declarations, CancellationToken cancellationToken)
	{
		var identifiers = new Dictionary<string, int>(StringComparer.Ordinal);
		var strings = new List<string>();
		int Intern(string value)
		{
			if (identifiers.TryGetValue(value, out var identifier)) return identifier;
			identifier = strings.Count;
			identifiers.Add(value, identifier);
			strings.Add(value);
			return identifier;
		}
		foreach (var declaration in declarations)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Intern(declaration.Name);
			if (declaration.Owner is not null) Intern(declaration.Owner);
			Intern(declaration.ContentFingerprint);
		}
		using var output = new MemoryStream();
		using (var compressed = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
		using (var writer = new BinaryWriter(compressed, Encoding.UTF8, leaveOpen: true))
		{
			writer.Write(strings.Count);
			foreach (var value in strings)
			{
				cancellationToken.ThrowIfCancellationRequested();
				writer.Write(value.Length);
				// This process-local payload preserves UTF-16 code units, including unpaired surrogates.
				writer.Write(MemoryMarshal.AsBytes(value.AsSpan()));
			}
			writer.Write(declarations.Count);
			foreach (var declaration in declarations)
			{
				cancellationToken.ThrowIfCancellationRequested();
				writer.Write(identifiers[declaration.Name]);
				writer.Write((int)declaration.Kind);
				writer.Write(declaration.Owner is null ? -1 : identifiers[declaration.Owner]);
				writer.Write(declaration.StartLine);
				writer.Write(declaration.EndLine);
				writer.Write(identifiers[declaration.ContentFingerprint]);
				writer.Write(declaration.StartIndex);
				writer.Write(declaration.EndIndex);
			}
		}
		cancellationToken.ThrowIfCancellationRequested();
		return output.ToArray();
	}

	public static IReadOnlyList<NavigationDeclaration> Decode(byte[] payload, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		using var input = new MemoryStream(payload, writable: false);
		using var compressed = new DeflateStream(input, CompressionMode.Decompress);
		using var reader = new BinaryReader(compressed, Encoding.UTF8, leaveOpen: true);
		var strings = new string[ReadCount(reader)];
		long characters = 0;
		for (var index = 0; index < strings.Length; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var length = ReadCount(reader);
			characters += length;
			if (characters > DependencyFactsEngine.MaximumNavigationCacheBytes / sizeof(char)) throw new InvalidDataException();
			var value = new char[length];
			compressed.ReadExactly(MemoryMarshal.AsBytes(value.AsSpan()));
			strings[index] = new string(value);
		}
		string ReadString()
		{
			var index = reader.ReadInt32();
			return (uint)index < (uint)strings.Length ? strings[index] : throw new InvalidDataException();
		}
		var declarations = new NavigationDeclaration[ReadCount(reader)];
		for (var index = 0; index < declarations.Length; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var name = ReadString();
			var kind = (NavigationSymbolKind)reader.ReadInt32();
			var ownerIndex = reader.ReadInt32();
			var owner = ownerIndex == -1 ? null : (uint)ownerIndex < (uint)strings.Length ? strings[ownerIndex] : throw new InvalidDataException();
			var start = reader.ReadInt32();
			var end = reader.ReadInt32();
			var fingerprint = ReadString();
			declarations[index] = new NavigationDeclaration(name, kind, owner, start, end, fingerprint)
			{
				StartIndex = reader.ReadInt32(),
				EndIndex = reader.ReadInt32()
			};
		}
		cancellationToken.ThrowIfCancellationRequested();
		if (compressed.ReadByte() != -1) throw new InvalidDataException();
		return Array.AsReadOnly(declarations);
	}

	private static int ReadCount(BinaryReader reader)
	{
		var count = reader.ReadInt32();
		return (uint)count <= MaximumValues ? count : throw new InvalidDataException();
	}
}
