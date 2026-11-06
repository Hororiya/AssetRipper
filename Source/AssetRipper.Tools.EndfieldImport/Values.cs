using AssetRipper.Assets;
using AssetRipper.Import.AssetCreation;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_137;
using AssetRipper.SourceGenerated.Classes.ClassID_142;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Classes.ClassID_95;
using AssetRipper.SourceGenerated.Subclasses.Matrix4x4f;
using System.Security.Cryptography;

namespace AssetRipper.Tools.EndfieldImport;

/// <summary>
/// One comparable line per object of the checked classes; the AnimeStudio2 baseline prints the same format.
/// "<cab> <pathID> <class> <fields>"; hashes are SHA256 prefixes, float sums use 4 decimals.
/// </summary>
internal static class Values
{
	private static string Sha(ReadOnlySpan<byte> b) => Convert.ToHexString(SHA256.HashData(b))[..16];

	private static string ShaU32(IEnumerable<uint> values)
	{
		List<byte> bytes = new();
		foreach (uint v in values) { bytes.AddRange(BitConverter.GetBytes(v)); }
		return Sha(bytes.ToArray());
	}

	private static float MatrixSum(Matrix4x4f m) =>
		m.E00 + m.E01 + m.E02 + m.E03 + m.E10 + m.E11 + m.E12 + m.E13 + m.E20 + m.E21 + m.E22 + m.E23 + m.E30 + m.E31 + m.E32 + m.E33;

	public static string? Describe(IUnityObjectBase a)
	{
		string head = $"{a.Collection.Name.ToLowerInvariant()}\t{a.PathID}\t{a.ClassID}";
		switch (a)
		{
			case IMesh m:
				{
					uint[] indices = m.IndexFormat == 0
						? Enumerable.Range(0, m.IndexBuffer.Length / 2).Select(i => (uint)BitConverter.ToUInt16(m.IndexBuffer, i * 2)).ToArray()
						: Enumerable.Range(0, m.IndexBuffer.Length / 4).Select(i => BitConverter.ToUInt32(m.IndexBuffer, i * 4)).ToArray();
					string channels = m.VertexData is null ? "" : string.Join(",", m.VertexData.Channels.Select(c => $"{c.Stream}:{c.Offset}:{c.Format}:{c.Dimension}"));
					string subs = string.Join(",", m.SubMeshes.Select(s => $"{s.FirstByte}:{s.IndexCount}:{s.VertexCount}"));
					double bp = m.BindPose.Sum(x => (double)MatrixSum(x));
					return $"{head}\t{m.Name}|vc={m.VertexData?.VertexCount}|data={Sha(m.VertexData?.Data ?? [])}|ch={channels}|sub={subs}|ib={indices.Length}:{ShaU32(indices)}|bp={m.BindPose.Count}:{bp:F4}|bnh={m.BoneNameHashes.Count}:{ShaU32(m.BoneNameHashes)}|sd={m.StreamData?.Path}:{m.StreamData?.Offset_UInt64}:{m.StreamData?.Size}";
				}
			case ISkinnedMeshRenderer r:
				return $"{head}\tmesh={r.Mesh.FileID}:{r.Mesh.PathID}|bones={r.Bones.Count}:{Sha(r.Bones.SelectMany(b => BitConverter.GetBytes(b.PathID)).ToArray())}|root={r.RootBone.PathID}";
			case IAnimator an:
				return $"{head}\tavatar={an.Avatar.FileID}:{an.Avatar.PathID}";
			case IGameObject g:
				{
					long art = TypeTreeTranscoder.Extras.TryGetValue((a.Collection.Name, a.PathID), out var ex) && ex.TryGetValue("m_ArtTag", out object? v) && v is not null ? System.Convert.ToInt64(v) : 0;
					return $"{head}\t{g.Name}|tag16={g.Tag | (art << 8)}";
				}
			case IAssetBundle b:
				return $"{head}\t{b.Name}|container={b.Container.Count}";
			default:
				return null;
		}
	}
}
