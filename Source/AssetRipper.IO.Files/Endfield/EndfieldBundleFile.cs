using AssetRipper.IO.Files.ResourceFiles;
using AssetRipper.IO.Files.Streams.Smart;
using K4os.Compression.LZ4;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace AssetRipper.IO.Files.Endfield;

/// <summary>
/// An Endfield "VFSFile": a UnityFS bundle whose header, blocks-info and directory fields are scrambled (XOR / rotate /
/// split-and-mix, big-endian), whose blocks-info is encrypted (<see cref="VfsCipher.DecryptSparse"/>) and LZ4-compressed,
/// and whose data blocks are either stored (type 0) or encrypted + <see cref="Lz4Inv"/>-compressed (type 5).
/// Node names are XOR-obfuscated. Decoded nodes become <see cref="ResourceFile"/>s, like any bundle.
/// </summary>
public sealed class EndfieldBundleFile : FileContainer
{
	public const int MagicLength = 8;

	public EndfieldBundleHeader Header { get; private set; }
	public IReadOnlyList<EndfieldStorageBlock> Blocks { get; private set; } = [];
	public IReadOnlyList<EndfieldNode> Nodes { get; private set; } = [];

	/// <summary>Set once any Endfield bundle has been decoded; enables type tree transcoding of its modified engine classes.</summary>
	public static bool AnyLoaded { get; private set; }

	/// <summary>True when the first 8 bytes at the stream position are an Endfield bundle signature.</summary>
	public static bool IsEndfieldBundle(SmartStream stream)
	{
		long position = stream.Position;
		Span<byte> head = stackalloc byte[MagicLength];
		int read = stream.Read(head);
		stream.Position = position;
		return read == MagicLength && IsSignature(head);
	}

	public static bool IsSignature(ReadOnlySpan<byte> head)
	{
		unchecked
		{
			uint a = BinaryPrimitives.ReadUInt32BigEndian(head);
			uint b = BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
			uint x = a ^ 0x4A92F0CD;
			uint c1 = unchecked(4 * x) & 0xFFFF0000;
			uint c2 = BitOperations.RotateRight(x, 14);
			return b == (c1 ^ c2 ^ 0xD8B1E637);
		}
	}

	public override void Read(SmartStream stream)
	{
		long basePosition = stream.Position;
		VfsCipher cipher = VfsCipher.FromKeys(EndfieldKeys.Load());
		(EndfieldBundleHeader header, List<EndfieldStorageBlock> blocks, List<EndfieldNode> nodes, long dataOffset) = ReadMetadata(stream, cipher, NameFixed);
		Header = header;
		Blocks = blocks;
		Nodes = nodes;
		AnyLoaded = true;

		stream.Position = basePosition + dataOffset;
		byte[] data = DecodeBlocks(stream, cipher);

		foreach (EndfieldNode node in Nodes)
		{
			if (node.Offset < 0 || node.Size < 0 || node.Offset + node.Size > data.Length)
			{
				AddFailedFile(new FailedFile { Name = node.Path, FilePath = FilePath, StackTrace = $"Node {node.Path} [{node.Offset}, +{node.Size}] outside {data.Length} decoded bytes." });
				continue;
			}
			SmartStream entry = SmartStream.CreateMemory(data, (int)node.Offset, (int)node.Size, false);
			AddResourceFile(new ResourceFile(entry, FilePath, node.Path));
		}
	}

	public override void Write(Stream stream) => throw new NotSupportedException("Writing Endfield bundles is not supported.");

	/// <summary>Reads only the directory (node names, offsets, sizes) of the bundle at the stream position; data blocks are not decoded.</summary>
	public static IReadOnlyList<EndfieldNode> ReadNodeList(SmartStream stream, EndfieldKeys keys)
	{
		return ReadMetadata(stream, VfsCipher.FromKeys(keys), "bundle").Nodes;
	}

	/// <summary>Header, storage blocks, directory, and the data offset relative to the bundle start.</summary>
	private static (EndfieldBundleHeader Header, List<EndfieldStorageBlock> Blocks, List<EndfieldNode> Nodes, long DataOffset) ReadMetadata(SmartStream stream, VfsCipher cipher, string name)
	{
		long basePosition = stream.Position;
		byte[] head = ReadExactly(stream, EndfieldBundleHeader.Length);
		if (!IsSignature(head))
		{
			throw new InvalidDataException($"Not an Endfield bundle: {name}");
		}
		EndfieldBundleHeader header = EndfieldBundleHeader.Parse(head);

		long fixedOffset = header.EncFlags >= 7 ? 48 : 40;
		long blocksInfoOffset = header.BlocksInfoAtTheEnd ? (long)(uint)header.Size - header.CompressedBlocksInfoSize : fixedOffset;
		stream.Position = basePosition + blocksInfoOffset;
		byte[] blocksInfo = ReadExactly(stream, checked((int)header.CompressedBlocksInfoSize));
		if ((header.Flags & 0x3F) != 0)
		{
			cipher.DecryptSparse(blocksInfo);
			byte[] uncompressed = new byte[header.UncompressedBlocksInfoSize];
			int written = LZ4Codec.Decode(blocksInfo, uncompressed);
			if (written != uncompressed.Length)
			{
				throw new InvalidDataException($"Blocks-info LZ4 wrote {written} of {uncompressed.Length} bytes: {name}");
			}
			blocksInfo = uncompressed;
		}
		int cursor = 0;
		List<EndfieldStorageBlock> blocks = ReadBlocks(blocksInfo, ref cursor);
		List<EndfieldNode> nodes = ReadNodes(blocksInfo, ref cursor);

		long dataOffset = fixedOffset;
		if (!header.BlocksInfoAtTheEnd)
		{
			long infoSize = header.CompressedBlocksInfoSize;
			if (header.BlockInfoNeedPaddingAtStart)
			{
				infoSize = (infoSize + 15) & ~15L;
			}
			dataOffset += infoSize;
		}
		return (header, blocks, nodes, dataOffset);
	}

	private byte[] DecodeBlocks(SmartStream stream, VfsCipher cipher)
	{
		long total = Blocks.Sum(b => (long)b.UncompressedSize);
		if (total > Array.MaxLength)
		{
			throw new NotSupportedException($"Bundle {NameFixed} decodes to {total} bytes (over 2 GB).");
		}
		byte[] data = new byte[total];
		int position = 0;
		byte[] scratch = [];
		foreach (EndfieldStorageBlock block in Blocks)
		{
			Span<byte> target = data.AsSpan(position, checked((int)block.UncompressedSize));
			switch (block.Flags)
			{
				case 0:
					stream.ReadExactly(target);
					break;
				case 5:
					{
						int size = checked((int)block.CompressedSize);
						if (scratch.Length < size)
						{
							scratch = new byte[size];
						}
						Span<byte> compressed = scratch.AsSpan(0, size);
						stream.ReadExactly(compressed);
						cipher.DecryptSparse(compressed);
						int written = Lz4Inv.Decompress(compressed, target);
						if (written != target.Length)
						{
							throw new InvalidDataException($"LZ4Inv wrote {written} of {target.Length} bytes: {NameFixed}");
						}
					}
					break;
				default:
					throw new NotSupportedException($"Endfield block type {block.Flags} in {NameFixed}");
			}
			position += target.Length;
		}
		return data;
	}

	private static List<EndfieldStorageBlock> ReadBlocks(ReadOnlySpan<byte> info, ref int p)
	{
		unchecked
		{
			uint count = DecodeCount(info, ref p, 0x8A7BF723, 0x91CE0A4F);
			List<EndfieldStorageBlock> blocks = new((int)Math.Min(count, 4096));
			for (uint i = 0; i < count; i++)
			{
				ushort a = U16(info, ref p), b = U16(info, ref p), c = U16(info, ref p);
				ushort enc = (ushort)(U16(info, ref p) ^ 0x9CD6);
				ushort d = U16(info, ref p);
				byte e0 = (byte)enc, e1 = (byte)(enc >> 8);
				ushort flags = (ushort)(((e0 ^ e1) << 8) | e0);
				flags = (ushort)(c ^ RotateLeft16(flags, 14) ^ 0x523F);
				blocks.Add(new EndfieldStorageBlock(
					UncompressedSize: Mix32(a, c),
					CompressedSize: Mix32(b, d),
					Flags: flags));
			}
			return blocks;
		}
	}

	private static List<EndfieldNode> ReadNodes(ReadOnlySpan<byte> info, ref int p)
	{
		unchecked
		{
			uint count = DecodeCount(info, ref p, 0x5DE50A6B, 0xE4C1D9F2);
			List<EndfieldNode> nodes = new((int)Math.Min(count, 4096));
			Span<byte> name = stackalloc byte[64];
			for (uint i = 0; i < count; i++)
			{
				uint a = U32(info, ref p) ^ 0x8E06A9F8, b = U32(info, ref p), c = U32(info, ref p), d = U32(info, ref p);
				int length = 0;
				while (p < info.Length && length < 64)
				{
					byte bt = info[p++];
					if (bt == 0)
					{
						break;
					}
					name[length] = (byte)(bt ^ (length ^ 0x97));
					length++;
				}
				uint e = U32(info, ref p);
				ushort a0 = (ushort)a, a1 = (ushort)(a >> 16);
				uint flags = BitOperations.RotateRight(((uint)(a1 ^ a0) << 16) | a0, 18) ^ 0xF13927C4 ^ b;
				nodes.Add(new EndfieldNode(
					Path: Encoding.ASCII.GetString(name[..length]),
					Offset: (long)Mix64Left(c, d),
					Size: (long)Mix64Left(e, b),
					Flags: flags));
			}
			return nodes;
		}
	}

	/// <summary>Count field: little-endian read, XOR, byte swap, then the 16-bit split-mix and a rotate/XOR.</summary>
	private static uint DecodeCount(ReadOnlySpan<byte> info, ref int p, uint xorIn, uint xorOut)
	{
		unchecked
		{
			uint raw = BinaryPrimitives.ReverseEndianness(BinaryPrimitives.ReadUInt32LittleEndian(Take(info, ref p, 4)) ^ xorIn);
			ushort low = (ushort)raw, high = (ushort)(raw >> 16);
			return BitOperations.RotateRight(((uint)(ushort)(low ^ high) << 16) | low, 18) ^ xorOut;
		}
	}

	/// <summary>Header / blocks 32-bit field from two 16-bit halves.</summary>
	internal static uint Mix32(ushort first, ushort second)
	{
		unchecked
		{
			uint v = ((uint)(ushort)(first ^ second ^ 0xA121) << 16) | second;
			return BitOperations.RotateRight(v, 18) ^ 0xF74324EE;
		}
	}

	/// <summary>Directory 64-bit field (<paramref name="low"/> is kept, the high half is mixed with it).</summary>
	private static ulong Mix64Left(uint low, uint other)
	{
		unchecked
		{
			ulong v = ((ulong)(other ^ low ^ 0xDAD76848) << 32) | low;
			return BitOperations.RotateLeft(v, 14) ^ 0xA4F1A11747816520UL;
		}
	}

	private static ushort RotateLeft16(ushort v, int n) => unchecked((ushort)((v << n) | (v >> (16 - n))));

	private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> s, ref int p, int n)
	{
		if (p + n > s.Length)
		{
			throw new InvalidDataException($"Blocks-info truncated at {p} (+{n} of {s.Length}).");
		}
		ReadOnlySpan<byte> r = s.Slice(p, n);
		p += n;
		return r;
	}

	private static ushort U16(ReadOnlySpan<byte> s, ref int p) => BinaryPrimitives.ReadUInt16BigEndian(Take(s, ref p, 2));
	private static uint U32(ReadOnlySpan<byte> s, ref int p) => BinaryPrimitives.ReadUInt32BigEndian(Take(s, ref p, 4));

	private static byte[] ReadExactly(SmartStream stream, int count)
	{
		byte[] buffer = new byte[count];
		stream.ReadExactly(buffer);
		return buffer;
	}
}

public readonly record struct EndfieldStorageBlock(uint UncompressedSize, uint CompressedSize, ushort Flags);

public readonly record struct EndfieldNode(string Path, long Offset, long Size, uint Flags);

/// <summary>Descrambled header fields. Raw layout after the 8-byte signature (big-endian):
/// u16 cbi2, u32 flags2, u32 enc, u32 size2, u32 flags1, u16 ubi1, u32 ?, u16 ubi2, u32 size1, u16 cbi1, u8 ?.</summary>
public readonly record struct EndfieldBundleHeader(uint CompressedBlocksInfoSize, uint UncompressedBlocksInfoSize, ulong Size, uint Flags, uint EncFlags)
{
	public const int Length = 8 + 33;

	public bool BlocksInfoAtTheEnd => (Flags & 0x80) != 0;
	public bool BlockInfoNeedPaddingAtStart => (Flags & 0x200) != 0;

	public static EndfieldBundleHeader Parse(ReadOnlySpan<byte> h)
	{
		unchecked
		{
			ReadOnlySpan<byte> r = h[8..];
			ushort cbi2 = BinaryPrimitives.ReadUInt16BigEndian(r);
			uint flags2 = BinaryPrimitives.ReadUInt32BigEndian(r[2..]);
			uint enc = BinaryPrimitives.ReadUInt32BigEndian(r[6..]);
			uint size2 = BinaryPrimitives.ReadUInt32BigEndian(r[10..]);
			uint flags1 = BinaryPrimitives.ReadUInt32BigEndian(r[14..]);
			ushort ubi1 = BinaryPrimitives.ReadUInt16BigEndian(r[18..]);
			// r[20..24]: unknown
			ushort ubi2 = BinaryPrimitives.ReadUInt16BigEndian(r[24..]);
			uint size1 = BinaryPrimitives.ReadUInt32BigEndian(r[26..]);
			ushort cbi1 = BinaryPrimitives.ReadUInt16BigEndian(r[30..]);
			// r[32]: unknown
			ulong size = BitOperations.RotateRight(((ulong)(size1 ^ size2 ^ 0xDAD76848) << 32) | size2, 18) ^ 0xA4F1A11747816520UL;
			return new EndfieldBundleHeader(
				CompressedBlocksInfoSize: EndfieldBundleFile.Mix32(cbi1, cbi2),
				UncompressedBlocksInfoSize: EndfieldBundleFile.Mix32(ubi1, ubi2),
				Size: size,
				Flags: flags1 ^ flags2 ^ 0xA7F49310,
				EncFlags: enc ^ flags2);
		}
	}
}
