namespace AssetRipper.IO.Files.Endfield;

/// <summary>
/// Endfield VFS bundle cipher: an AES-128 encryption (custom S-box, standard key schedule) driving a keystream.
/// Block n's keystream is E(IV_n); IV_0 is the key file's IV and IV_{n+1}[i] = S[rotl3(K_n[i] ^ 31i ^ x_i)], with x_i the
/// i-th byte (mod 8) of the 64-bit mixing key. Bundle data uses a sparse variant that only touches 256 bytes.
/// </summary>
public sealed class VfsCipher
{
	private readonly byte[] sbox;
	private readonly byte[] iv;
	private readonly ulong xorKey;
	private readonly byte[] roundKeys = new byte[11 * 16];

	private static readonly byte[] RoundConstants = [0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0x1B, 0x36];

	public VfsCipher(byte[] sbox, byte[] key, byte[] iv, ulong xorKey)
	{
		if (sbox.Length != 256 || key.Length != 16 || iv.Length != 16)
		{
			throw new ArgumentException("VFS cipher needs a 256-byte S-box, a 16-byte key and a 16-byte IV.");
		}
		this.sbox = sbox;
		this.iv = iv;
		this.xorKey = xorKey;
		ExpandKey(key);
	}

	private static VfsCipher? cached;

	public static VfsCipher FromKeys(EndfieldKeys keys)
	{
		if (cached is not null)
		{
			return cached;
		}
		if (!keys.HasBundleKeys)
		{
			throw new InvalidOperationException($"{EndfieldKeys.FileName} has no VfsAes* entries; Endfield bundles cannot be decrypted.");
		}
		return cached = new VfsCipher(keys.VfsAesSBox!, keys.VfsAesKey!, keys.VfsAesIV!, keys.VfsAesXorKey);
	}

	/// <summary>Keystream-XOR <paramref name="data"/> in place, starting at block 0.</summary>
	public void Decrypt(Span<byte> data)
	{
		unchecked
		{
			Span<byte> state = stackalloc byte[16];
			Span<byte> next = stackalloc byte[16];
			iv.CopyTo(state);
			for (int offset = 0; offset < data.Length; offset += 16)
			{
				EncryptBlock(state);
				int count = Math.Min(16, data.Length - offset);
				for (int i = 0; i < count; i++)
				{
					data[offset + i] ^= state[i];
				}
				for (int i = 0; i < 16; i++)
				{
					byte t = (byte)(state[i] ^ (byte)(31 * i) ^ (byte)(xorKey >> ((8 * i) & 0x38)));
					next[i] = sbox[(byte)((t << 3) | (t >> 5))];
				}
				next.CopyTo(state);
			}
		}
	}

	/// <summary>
	/// The sparse scheme used for bundle blocks-info and data blocks: buffers up to 256 bytes are decrypted whole;
	/// longer ones gather <c>step = 256 / (len / 16)</c> bytes (at least 1) from the start of each of the first 256
	/// 16-byte blocks into one 256-byte buffer, decrypt it, and scatter it back.
	/// </summary>
	public void DecryptSparse(Span<byte> buffer)
	{
		if (buffer.Length <= 256)
		{
			Decrypt(buffer);
			return;
		}
		int blocks = buffer.Length / 16;
		int step = blocks > 256 ? 1 : 256 / blocks;
		int used = Math.Min(blocks, 256);
		Span<byte> gathered = stackalloc byte[256];
		gathered.Clear();
		for (int i = 0; i < used; i++)
		{
			buffer.Slice(i * 16, step).CopyTo(gathered.Slice(i * step, step));
		}
		Decrypt(gathered);
		for (int i = 0; i < used; i++)
		{
			gathered.Slice(i * step, step).CopyTo(buffer.Slice(i * 16, step));
		}
	}

	private void ExpandKey(ReadOnlySpan<byte> key)
	{
		unchecked
		{
			key.CopyTo(roundKeys);
			Span<byte> word = stackalloc byte[4];
			for (int w = 4; w < 44; w++)
			{
				roundKeys.AsSpan((w - 1) * 4, 4).CopyTo(word);
				if (w % 4 == 0)
				{
					byte first = word[0];
					word[0] = (byte)(sbox[word[1]] ^ RoundConstants[w / 4 - 1]);
					word[1] = sbox[word[2]];
					word[2] = sbox[word[3]];
					word[3] = sbox[first];
				}
				for (int k = 0; k < 4; k++)
				{
					roundKeys[w * 4 + k] = (byte)(word[k] ^ roundKeys[(w - 4) * 4 + k]);
				}
			}
		}
	}

	private void EncryptBlock(Span<byte> s)
	{
		AddRoundKey(s, 0);
		for (int round = 1; round < 10; round++)
		{
			SubBytes(s);
			ShiftRows(s);
			MixColumns(s);
			AddRoundKey(s, round);
		}
		SubBytes(s);
		ShiftRows(s);
		AddRoundKey(s, 10);
	}

	private void AddRoundKey(Span<byte> s, int round)
	{
		ReadOnlySpan<byte> k = roundKeys.AsSpan(round * 16, 16);
		for (int i = 0; i < 16; i++)
		{
			s[i] ^= k[i];
		}
	}

	private void SubBytes(Span<byte> s)
	{
		for (int i = 0; i < 16; i++)
		{
			s[i] = sbox[s[i]];
		}
	}

	// State is column-major (byte 4c + r is row r of column c), as in FIPS-197.
	private static void ShiftRows(Span<byte> s)
	{
		(s[1], s[5], s[9], s[13]) = (s[5], s[9], s[13], s[1]);
		(s[2], s[6], s[10], s[14]) = (s[10], s[14], s[2], s[6]);
		(s[3], s[7], s[11], s[15]) = (s[15], s[3], s[7], s[11]);
	}

	private static byte XTime(byte a) => unchecked((byte)((a << 1) ^ ((a & 0x80) != 0 ? 0x1B : 0)));

	private static void MixColumns(Span<byte> s)
	{
		unchecked
		{
			for (int c = 0; c < 16; c += 4)
			{
				byte a0 = s[c], a1 = s[c + 1], a2 = s[c + 2], a3 = s[c + 3];
				byte t = (byte)(a0 ^ a1 ^ a2 ^ a3);
				s[c] ^= (byte)(t ^ XTime((byte)(a0 ^ a1)));
				s[c + 1] ^= (byte)(t ^ XTime((byte)(a1 ^ a2)));
				s[c + 2] ^= (byte)(t ^ XTime((byte)(a2 ^ a3)));
				s[c + 3] ^= (byte)(t ^ XTime((byte)(a3 ^ a0)));
			}
		}
	}
}
