namespace AssetRipper.IO.Files.Endfield;

/// <summary>
/// LZ4 block variant used by Endfield bundle data blocks: the token's literal and match nibbles are interleaved
/// (literal = bits 0,1,4,5; match = bits 2,3,6,7) and the match offset is big-endian. Everything else is LZ4.
/// </summary>
public static class Lz4Inv
{
	/// <returns>Number of bytes written to <paramref name="dst"/>.</returns>
	public static int Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
	{
		int s = 0, d = 0;
		while (s < src.Length)
		{
			int token = src[s++];
			int literal = (token & 0b11) | ((token >> 2) & 0b1100);
			int match = ((token >> 2) & 0b11) | ((token >> 4) & 0b1100);

			literal = ExtendLength(literal, src, ref s);
			if (literal > src.Length - s || literal > dst.Length - d)
			{
				throw new InvalidDataException($"LZ4Inv literal run overflows (src {s}+{literal}/{src.Length}, dst {d}+{literal}/{dst.Length}).");
			}
			src.Slice(s, literal).CopyTo(dst.Slice(d));
			s += literal;
			d += literal;
			if (s >= src.Length || d >= dst.Length)
			{
				break;
			}

			if (src.Length - s < 2)
			{
				throw new InvalidDataException("LZ4Inv truncated match offset.");
			}
			int back = (src[s] << 8) | src[s + 1];
			s += 2;
			match = ExtendLength(match, src, ref s) + 4;
			int from = d - back;
			if (back == 0 || from < 0 || match > dst.Length - d)
			{
				throw new InvalidDataException($"LZ4Inv bad match (offset {back}, length {match}) at output {d}.");
			}
			if (match <= back)
			{
				dst.Slice(from, match).CopyTo(dst.Slice(d));
				d += match;
			}
			else
			{
				for (int i = 0; i < match; i++)
				{
					dst[d++] = dst[from++];
				}
			}
		}
		return d;
	}

	private static int ExtendLength(int length, ReadOnlySpan<byte> src, ref int s)
	{
		if (length != 0xF)
		{
			return length;
		}
		byte b;
		do
		{
			if (s >= src.Length)
			{
				throw new InvalidDataException("LZ4Inv truncated length.");
			}
			b = src[s++];
			length += b;
		}
		while (b == 0xFF);
		return length;
	}
}
