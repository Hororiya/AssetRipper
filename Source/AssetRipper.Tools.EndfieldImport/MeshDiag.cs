using AssetRipper.Assets;
using AssetRipper.Import.AssetCreation;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Extensions;

namespace AssetRipper.Tools.EndfieldImport;

/// <summary>Skin-channel flag diagnostics: flag nibbles vs m_BonesPerVertex vs the decoded weights.</summary>
internal static class MeshDiag
{
	public static void Print(IEnumerable<IUnityObjectBase> assets)
	{
		foreach (IMesh mesh in assets.OfType<IMesh>())
		{
			if (mesh.VertexData is null || mesh.VertexData.Channels.Count < 14)
			{
				continue;
			}
			int fw = mesh.VertexData.Channels[12].Dimension >> 4, fi = mesh.VertexData.Channels[13].Dimension >> 4;
			bool hasW = mesh.VertexData.Channels[12].Dimension != 0, hasI = mesh.VertexData.Channels[13].Dimension != 0;
			if (!hasI)
			{
				continue;
			}
			object? bpv = TypeTreeTranscoder.Extras.TryGetValue((mesh.Collection.Name, mesh.PathID), out var ex) && ex.TryGetValue("m_BonesPerVertex", out object? v) ? v : "-";
			string skinStats = "no skin";
			if (MeshData.TryMakeFromMesh(mesh, out MeshData md) && md.Skin is { Length: > 0 } skin)
			{
				int maxUsed = skin.Max(s => (s.Weight0 > 0 ? 1 : 0) + (s.Weight1 > 0 ? 1 : 0) + (s.Weight2 > 0 ? 1 : 0) + (s.Weight3 > 0 ? 1 : 0));
				double minSum = skin.Min(s => (double)(s.Weight0 + s.Weight1 + s.Weight2 + s.Weight3));
				double maxSum = skin.Max(s => (double)(s.Weight0 + s.Weight1 + s.Weight2 + s.Weight3));
				int maxIdxUsed = skin.Max(s => s.Index1 != 0 || s.Index2 != 0 || s.Index3 != 0 ? (s.Index3 != 0 ? 4 : s.Index2 != 0 ? 3 : 2) : 1);
				var w = skin[skin.Length / 2];
				skinStats = $"maxNonzeroWeights={maxUsed} weightSum=[{minSum:F3},{maxSum:F3}] maxIndexSlotsUsed={maxIdxUsed} mid=({w.Weight0:F3},{w.Weight1:F3},{w.Weight2:F3},{w.Weight3:F3}|{w.Index0},{w.Index1},{w.Index2},{w.Index3})";
			}
			Console.WriteLine($"  meshdiag {mesh.Name,-40} v={mesh.VertexData.VertexCount,6} weightFlag={(hasW ? fw.ToString() : "none")} indexFlag={fi} bonesPerVertex={bpv} {skinStats}");
		}
	}
}
