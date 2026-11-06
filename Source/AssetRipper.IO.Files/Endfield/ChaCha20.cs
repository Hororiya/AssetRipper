using System.Buffers.Binary;
using System.Numerics;

namespace AssetRipper.IO.Files.Endfield;

/// <summary>RFC 8439 ChaCha20 keystream XOR (20 rounds, 32-bit counter, 96-bit nonce).</summary>
/// <remarks>The repository builds with overflow checking, so every modular addition is explicitly unchecked.</remarks>
public static class ChaCha20
{
	private const uint Sigma0 = 0x61707865, Sigma1 = 0x3320646e, Sigma2 = 0x79622d32, Sigma3 = 0x6b206574;

	public static void Xor(Span<byte> data, ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, uint initialCounter)
	{
		if (key.Length != 32 || nonce.Length != 12)
		{
			throw new ArgumentException("ChaCha20 requires a 32-byte key and a 12-byte nonce.");
		}
		Span<uint> state = stackalloc uint[16];
		state[0] = Sigma0; state[1] = Sigma1; state[2] = Sigma2; state[3] = Sigma3;
		for (int i = 0; i < 8; i++)
		{
			state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(i * 4, 4));
		}
		for (int i = 0; i < 3; i++)
		{
			state[13 + i] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(i * 4, 4));
		}
		Span<uint> working = stackalloc uint[16];
		Span<byte> block = stackalloc byte[64];
		uint counter = initialCounter;
		while (!data.IsEmpty)
		{
			state[12] = counter;
			counter = unchecked(counter + 1);
			state.CopyTo(working);
			for (int round = 0; round < 10; round++)
			{
				QuarterRound(working, 0, 4, 8, 12);
				QuarterRound(working, 1, 5, 9, 13);
				QuarterRound(working, 2, 6, 10, 14);
				QuarterRound(working, 3, 7, 11, 15);
				QuarterRound(working, 0, 5, 10, 15);
				QuarterRound(working, 1, 6, 11, 12);
				QuarterRound(working, 2, 7, 8, 13);
				QuarterRound(working, 3, 4, 9, 14);
			}
			for (int i = 0; i < 16; i++)
			{
				BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(i * 4, 4), unchecked(working[i] + state[i]));
			}
			int count = Math.Min(data.Length, 64);
			for (int i = 0; i < count; i++)
			{
				data[i] ^= block[i];
			}
			data = data[count..];
		}
	}

	private static void QuarterRound(Span<uint> s, int a, int b, int c, int d)
	{
		unchecked
		{
			s[a] += s[b]; s[d] = BitOperations.RotateLeft(s[d] ^ s[a], 16);
			s[c] += s[d]; s[b] = BitOperations.RotateLeft(s[b] ^ s[c], 12);
			s[a] += s[b]; s[d] = BitOperations.RotateLeft(s[d] ^ s[a], 8);
			s[c] += s[d]; s[b] = BitOperations.RotateLeft(s[b] ^ s[c], 7);
		}
	}
}
