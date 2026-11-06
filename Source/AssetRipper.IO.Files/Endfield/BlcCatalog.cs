using System.Buffers.Binary;
using System.Text;

namespace AssetRipper.IO.Files.Endfield;

/// <summary>
/// One decrypted <c>.blc</c> catalog: a VFS group made of <c>.chk</c> chunks, each holding virtual files.
/// Layout: int32 outer version (3), 12-byte ChaCha20 nonce, then the encrypted body (counter 1):
/// int32 version (3|4|5), [v4+: int32 schema], u16-string name, 8-byte hash, int32 file count, int64 chunks length,
/// u8 block type, int32 chunk count, chunks..., 14-byte trailer. v4 adds int32 flags to chunk and file records,
/// v5 one more int32 after each flags.
/// </summary>
public sealed class BlcCatalog
{
	public const int HeaderLength = 12;
	public const int TrailerLength = 14;

	public required string Path { get; init; }
	/// <summary>Group folder name (e.g. <c>7064D8E2</c>), equal to the .blc file name.</summary>
	public required string GroupId { get; init; }
	public required int Version { get; init; }
	public int? SchemaTag { get; init; }
	public required string Name { get; init; }
	public required string Hash { get; init; }
	public required int DeclaredFileCount { get; init; }
	public required long DeclaredChunksLength { get; init; }
	public required byte BlockType { get; init; }
	public List<BlcChunk> Chunks { get; } = [];

	public IEnumerable<BlcVirtualFile> Files => Chunks.SelectMany(c => c.Files);

	public static BlcCatalog Read(string path, EndfieldKeys keys)
	{
		byte[] data = File.ReadAllBytes(path);
		return Parse(data, path, keys);
	}

	public static BlcCatalog Parse(byte[] data, string path, EndfieldKeys keys)
	{
		if (data.Length <= HeaderLength)
		{
			throw new InvalidDataException($"BLC shorter than its {HeaderLength}-byte header: {path}");
		}
		int outer = BinaryPrimitives.ReadInt32LittleEndian(data);
		if (outer != 3)
		{
			throw new InvalidDataException($"Unsupported BLC outer version {outer}: {path}");
		}
		ChaCha20.Xor(data.AsSpan(HeaderLength), keys.BlcChaCha20Key, data.AsSpan(0, HeaderLength), 1);

		SpanCursor r = new(data, HeaderLength, path);
		int version = r.Int32();
		if (version is not (3 or 4 or 5))
		{
			throw new InvalidDataException($"Unsupported BLC inner version {version} (wrong key?): {path}");
		}
		BlcCatalog catalog = new()
		{
			Path = path,
			GroupId = System.IO.Path.GetFileNameWithoutExtension(path),
			Version = version,
			SchemaTag = version >= 4 ? r.Int32() : null,
			Name = r.Utf8(),
			Hash = r.Hex(8),
			DeclaredFileCount = r.Int32(),
			DeclaredChunksLength = r.Int64(),
			BlockType = r.UInt8(),
		};
		int chunkCount = r.Count();
		for (int c = 0; c < chunkCount; c++)
		{
			BlcChunk chunk = new()
			{
				Catalog = catalog,
				Index = c,
				Md5Name = r.Hex(16),
				ContentMd5 = r.Hex(16),
				Length = r.Int64(),
				BlockType = r.UInt8(),
				Flags = version >= 4 ? r.Int32() : 0,
				Extra = version >= 5 ? r.Int32() : 0,
			};
			int fileCount = r.Count();
			for (int f = 0; f < fileCount; f++)
			{
				string name = r.Utf8();
				string nameHash = r.Hex(8);
				string chunkMd5 = r.Hex(16);
				string dataMd5 = r.Hex(16);
				long offset = r.Int64();
				long length = r.Int64();
				byte blockType = r.UInt8();
				bool encrypted = r.UInt8() != 0;
				long? ivSeed = encrypted ? r.Int64() : null;
				int flags = version >= 4 ? r.Int32() : 0;
				int extra = version >= 5 ? r.Int32() : 0;
				chunk.Files.Add(new BlcVirtualFile
				{
					Chunk = chunk,
					Index = f,
					Name = name,
					NameHash = nameHash,
					ChunkMd5Name = chunkMd5,
					DataMd5 = dataMd5,
					Offset = offset,
					Length = length,
					BlockType = blockType,
					Encrypted = encrypted,
					IvSeed = ivSeed,
					Flags = flags,
					Extra = extra,
				});
			}
			catalog.Chunks.Add(chunk);
		}
		int trailer = data.Length - r.Position;
		if (trailer != TrailerLength)
		{
			throw new InvalidDataException($"Unexpected BLC trailer length {trailer} (expected {TrailerLength}): {path}");
		}
		return catalog;
	}

	private ref struct SpanCursor(byte[] data, int position, string path)
	{
		private readonly ReadOnlySpan<byte> data = data;
		public int Position = position;

		private ReadOnlySpan<byte> Take(int n)
		{
			if (n < 0 || Position + n > data.Length)
			{
				throw new EndOfStreamException($"BLC truncated at {Position} (+{n}): {path}");
			}
			ReadOnlySpan<byte> s = data.Slice(Position, n);
			Position += n;
			return s;
		}

		public byte UInt8() => Take(1)[0];
		public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
		public long Int64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
		public string Utf8() => Encoding.UTF8.GetString(Take(BinaryPrimitives.ReadUInt16LittleEndian(Take(2))));
		public string Hex(int n) => Convert.ToHexString(Take(n));

		public int Count()
		{
			int n = Int32();
			return n >= 0 ? n : throw new InvalidDataException($"Negative count {n} at {Position - 4}: {path}");
		}
	}
}

public sealed class BlcChunk
{
	public required BlcCatalog Catalog { get; init; }
	public required int Index { get; init; }
	public required string Md5Name { get; init; }
	public required string ContentMd5 { get; init; }
	public required long Length { get; init; }
	public required byte BlockType { get; init; }
	public required int Flags { get; init; }
	/// <summary>v5: an int32 after <see cref="Flags"/> (meaning unknown; chunk and file records share the value).</summary>
	public int Extra { get; init; }
	/// <summary>Resolved <c>.chk</c> path (set by <see cref="EndfieldVfs"/>); null when the chunk exists in no root.</summary>
	public string? PhysicalPath { get; set; }
	public List<BlcVirtualFile> Files { get; } = [];
}

public sealed class BlcVirtualFile
{
	public required BlcChunk Chunk { get; init; }
	public required int Index { get; init; }
	public required string Name { get; init; }
	public required string NameHash { get; init; }
	public required string ChunkMd5Name { get; init; }
	public required string DataMd5 { get; init; }
	public required long Offset { get; init; }
	public required long Length { get; init; }
	public required byte BlockType { get; init; }
	public required bool Encrypted { get; init; }
	public long? IvSeed { get; init; }
	public int Flags { get; init; }
	/// <summary>v5: an int32 after <see cref="Flags"/> (meaning unknown).</summary>
	public int Extra { get; init; }

	public override string ToString() => $"{Name} @{Chunk.Md5Name}+{Offset} ({Length} B)";
}
