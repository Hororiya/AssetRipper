using AssetRipper.Assets;
using AssetRipper.Export.Modules.Models;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Extensions;
using SharpGLTF.Scenes;

namespace AssetRipper.Tools.EndfieldImport;

/// <summary>--glb &lt;root GameObject name&gt;: exports that hierarchy through GlbLevelBuilder (with skinned meshes).</summary>
internal static class Glb
{
	public static void Export(IUnityObjectBase[] assets, string rootName, string outDir)
	{
		IGameObject[] roots = assets.OfType<IGameObject>().Where(g => g.Name == rootName && g.GetTransform().Father_C4P is null).ToArray();
		if (roots.Length == 0)
		{
			Console.WriteLine($"glb: no root GameObject named '{rootName}'");
			return;
		}
		SceneBuilder scene = GlbLevelBuilder.Build([roots[0]], false);
		Directory.CreateDirectory(outDir);
		string path = Path.Combine(outDir, rootName + ".glb");
		using FileStream stream = File.Create(path);
		if (GlbWriter.TryWrite(scene, stream, out string? error))
		{
			Console.WriteLine($"glb: {path} ({stream.Length} bytes)");
		}
		else
		{
			Console.WriteLine($"glb: FAILED {error}");
		}
	}
}

internal static class SkinDiag
{
	public static void Print(IUnityObjectBase[] assets)
	{
		foreach (var r in assets.OfType<AssetRipper.SourceGenerated.Classes.ClassID_137.ISkinnedMeshRenderer>())
		{
			var mesh = r.MeshP;
			string meshInfo = "mesh=null";
			if (mesh is not null)
			{
				bool ok = MeshData.TryMakeFromMesh(mesh, out MeshData md);
				int bonesResolved = r.BonesP.Count(b => b is not null);
				meshInfo = $"mesh={mesh.Name} isSet={mesh.IsSet()} meshData={ok} verts={(ok ? md.Vertices.Length : -1)} skin={(ok ? md.Skin?.Length.ToString() ?? "null" : "-")} bindposes={mesh.BindPose.Count} bones={r.Bones.Count} resolved={bonesResolved} vdBytes={mesh.VertexData.Data.Length} channels={string.Join(",", mesh.VertexData.Channels.Select(c => c.Dimension))}";
			}
			Console.WriteLine($"  skindiag go={r.GameObject_C2P?.Name} {meshInfo}");
		}
	}
}

/// <summary>Ground truth for skinned exports: Unity's skinning in the prefab pose (world = sum w * boneWorld * bindPose * v), as bounding boxes.</summary>
internal static class SkinTruth
{
	public static void Print(IUnityObjectBase[] assets, string rootName)
	{
		var rendererSet = assets.OfType<AssetRipper.SourceGenerated.Classes.ClassID_137.ISkinnedMeshRenderer>()
			.Where(r => r.GameObject_C2P is { } go && go.GetRoot().Name == rootName);
		foreach (var r in rendererSet)
		{
			if (r.MeshP is not { } mesh || !MeshData.TryMakeFromMesh(mesh, out MeshData md) || md.Skin is null)
			{
				continue;
			}
			System.Numerics.Matrix4x4[] skinMats = r.BonesP.Select((b, i) =>
				mesh.BindPose[i].CastToStruct() * (b is null ? System.Numerics.Matrix4x4.Identity : WorldMatrix(b))).ToArray();
			System.Numerics.Vector3 min = new(float.MaxValue), max = new(float.MinValue);
			for (int v = 0; v < md.Vertices.Length; v++)
			{
				var s = md.Skin[v];
				System.Numerics.Vector3 p = System.Numerics.Vector3.Zero;
				for (int k = 0; k < 4; k++)
				{
					float w = s.Weights[k];
					if (w > 0)
					{
						p += w * System.Numerics.Vector3.Transform(md.Vertices[v], skinMats[s.Indices[k]]);
					}
				}
				min = System.Numerics.Vector3.Min(min, p);
				max = System.Numerics.Vector3.Max(max, p);
			}
			// glTF / Blender: X mirrored (Unity left-handed); report in Unity axes
			Console.WriteLine($"  skintruth {r.GameObject_C2P!.Name} min=({min.X:F4},{min.Y:F4},{min.Z:F4}) max=({max.X:F4},{max.Y:F4},{max.Z:F4})");
		}
	}

	private static System.Numerics.Matrix4x4 WorldMatrix(AssetRipper.SourceGenerated.Classes.ClassID_4.ITransform t)
	{
		System.Numerics.Matrix4x4 m = System.Numerics.Matrix4x4.Identity;
		for (var cur = t; cur is not null; cur = cur.Father_C4P)
		{
			m *= System.Numerics.Matrix4x4.CreateScale(cur.LocalScale_C4.CastToStruct())
				* System.Numerics.Matrix4x4.CreateFromQuaternion(cur.LocalRotation_C4.CastToStruct())
				* System.Numerics.Matrix4x4.CreateTranslation(cur.LocalPosition_C4.CastToStruct());
		}
		return m;
	}
}
