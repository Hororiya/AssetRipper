using AssetRipper.Assets;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files.Endfield;
using AssetRipper.Numerics;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.ChannelInfo;
using AssetRipper.SourceGenerated.Subclasses.VertexData;
using System.Buffers.Binary;
using System.Numerics;

namespace AssetRipper.Processing.Endfield;

/// <summary>
/// Rewrites Arknights: Endfield vertex data into stock Unity vertex data, so every exporter reads it correctly.
/// Endfield stores a hint in the high nibble of <c>ChannelInfo.Dimension</c> (stock Unity keeps it zero):
/// <list type="bullet">
/// <item>Normal channel, Float32 x1, hint 3: one 32-bit packed frame. Bits 0-9 and 10-19 are a signed 10-bit
/// octahedral normal (scale 1/511); bits 20-29 and 31 are presumably the tangent frame (not decoded yet, so tangents are
/// left out and exporters recompute them).</item>
/// <item>Blend weight / index channels: the hint is <c>m_BonesPerVertex</c> (0 = 4). With 1, the mesh has indices but
/// no weight channel; the single weight is 1.</item>
/// </list>
/// Other hinted channels (UV2 SNorm8x4 hint 2) decode correctly by format and are kept as they are.
/// </summary>
public sealed class EndfieldMeshProcessor : IAssetProcessor
{
	private const int NormalChannel = 1, WeightChannel = 12, IndexChannel = 13;

	public static int Rewritten { get; private set; }
	public static int Failed { get; private set; }

	public void Process(GameData gameData)
	{
		if (!EndfieldBundleFile.AnyLoaded)
		{
			return;
		}
		Logger.Info(LogCategory.Processing, "Rewriting Endfield vertex data");
		foreach (IUnityObjectBase asset in gameData.GameBundle.FetchAssets())
		{
			if (asset is IMesh mesh && HasHints(mesh.VertexData))
			{
				try
				{
					Rewrite(mesh);
					Rewritten++;
				}
				catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException)
				{
					Failed++;
					Logger.Warning(LogCategory.Processing, $"Endfield vertex data of mesh '{mesh.Name}' ({mesh.PathID}) left as is: {ex.Message}");
				}
			}
		}
	}

	private static bool HasHints(IVertexData? vertexData)
	{
		return vertexData is not null && vertexData.Channels.Any(c => c.Dimension >> 4 != 0);
	}

	private static void Rewrite(IMesh mesh)
	{
		VertexDataBlob blob = VertexDataBlob.Create(mesh);
		if (blob.VertexCount == 0 || blob.Data.Length == 0)
		{
			return;
		}
		if (!blob.ReadData(out Vector3[]? vertices, out Vector3[]? normals, out Vector4[]? tangents, out ColorFloat[]? colors,
			out Vector2[]? uv0, out Vector2[]? uv1, out Vector2[]? uv2, out Vector2[]? uv3, out Vector2[]? uv4, out Vector2[]? uv5,
			out Vector2[]? uv6, out Vector2[]? uv7, out BoneWeight4[]? skin))
		{
			throw new InvalidDataException("vertex data could not be read");
		}

		ChannelInfo normal = blob.Channels[NormalChannel];
		if (normal.Dimension >> 4 == 3)
		{
			if ((normal.Dimension & 0xF) != 1 || normal.Format != 0)
			{
				throw new InvalidDataException($"unexpected packed normal channel (format {normal.Format}, dimension 0x{normal.Dimension:X2})");
			}
			normals = DecodePackedNormals(blob, normal);
		}

		if (blob.Channels.Count > IndexChannel
			&& blob.Channels[IndexChannel].Dimension >> 4 == 1 && (blob.Channels[WeightChannel].Dimension & 0xF) == 0)
		{
			// One bone per vertex and no weight channel: stock decoding yields no skin at all, so read the indices here.
			skin = ReadSingleBoneSkin(blob, blob.Channels[IndexChannel]);
		}

		MeshData data = new(vertices, normals, tangents, colors, uv0, uv1, uv2, uv3, uv4, uv5, uv6, uv7, skin, [], [], []);
		VertexDataBlob stock = VertexDataBlob.Create(data, blob.Version, blob.EndianType);

		IVertexData target = mesh.VertexData;
		target.VertexCount = (uint)stock.VertexCount;
		target.Channels.Clear();
		foreach (ChannelInfo source in stock.Channels)
		{
			ChannelInfo channel = target.Channels.AddNew();
			channel.Stream = source.Stream;
			channel.Offset = source.Offset;
			channel.Format = source.Format;
			channel.Dimension = source.Dimension;
		}
		target.Data = stock.Data;
		mesh.StreamData?.ClearValues();
	}

	/// <summary>Index channel with hint 1: the first index component of each vertex, weight 1.</summary>
	private static BoneWeight4[] ReadSingleBoneSkin(VertexDataBlob blob, ChannelInfo channel)
	{
		int size = channel.Format switch
		{
			6 or 7 => 1, // UInt8 / SInt8
			8 or 9 => 2, // UInt16 / SInt16
			10 or 11 => 4, // UInt32 / SInt32
			_ => throw new InvalidDataException($"unexpected bone index format {channel.Format}"),
		};
		BoneWeight4[] skin = new BoneWeight4[blob.VertexCount];
		int stride = blob.GetStreamStride(channel.Stream);
		int start = blob.GetStreamOffset(channel.Stream) + channel.Offset;
		bool big = blob.EndianType == IO.Endian.EndianType.BigEndian;
		for (int v = 0; v < skin.Length; v++)
		{
			ReadOnlySpan<byte> raw = blob.Data.AsSpan(start + stride * v, size);
			int index = size switch
			{
				1 => raw[0],
				2 => big ? BinaryPrimitives.ReadUInt16BigEndian(raw) : BinaryPrimitives.ReadUInt16LittleEndian(raw),
				_ => (int)(big ? BinaryPrimitives.ReadUInt32BigEndian(raw) : BinaryPrimitives.ReadUInt32LittleEndian(raw)),
			};
			skin[v] = new BoneWeight4(1f, 0f, 0f, 0f, index, 0, 0, 0);
		}
		return skin;
	}

	/// <summary>Decodes the 10:10 octahedral normal in each packed 32-bit value (bits 0-9 = x, 10-19 = y, signed, scale 1/511).</summary>
	public static Vector3[] DecodePackedNormals(VertexDataBlob blob, ChannelInfo channel)
	{
		Vector3[] result = new Vector3[blob.VertexCount];
		int stride = blob.GetStreamStride(channel.Stream);
		int start = blob.GetStreamOffset(channel.Stream) + channel.Offset;
		bool big = blob.EndianType == IO.Endian.EndianType.BigEndian;
		for (int v = 0; v < result.Length; v++)
		{
			ReadOnlySpan<byte> raw = blob.Data.AsSpan(start + stride * v, 4);
			uint packed = big ? BinaryPrimitives.ReadUInt32BigEndian(raw) : BinaryPrimitives.ReadUInt32LittleEndian(raw);
			result[v] = DecodeOctahedral(packed);
		}
		return result;
	}

	public static Vector3 DecodeOctahedral(uint packed)
	{
		int ix = unchecked((int)(packed << 22)) >> 22;
		int iy = unchecked((int)(packed << 12)) >> 22;
		float x = ix / 511f, y = iy / 511f;
		float z = 1f - MathF.Abs(x) - MathF.Abs(y);
		if (z < 0f)
		{
			float fx = (1f - MathF.Abs(y)) * (ix >= 0 ? 1f : -1f);
			float fy = (1f - MathF.Abs(x)) * (iy >= 0 ? 1f : -1f);
			x = fx;
			y = fy;
		}
		return Vector3.Normalize(new Vector3(x, y, z));
	}
}
