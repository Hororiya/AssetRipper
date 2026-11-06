using AssetRipper.Assets;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure;
using AssetRipper.IO.Files;

namespace AssetRipper.Tools.EndfieldImport;

// Import harness: loads files/folders through GameStructure (Endfield .ab files decode via EndfieldBundleScheme) and reports
// per-class object counts, unreadable objects, and the first import errors.
//   import <file|dir>... [--errors N]
internal static class Program
{
	private sealed class CaptureLogger : ILogger
	{
		public readonly List<string> Errors = new();
		public int Warnings;
		public readonly Dictionary<string, int> WarningKinds = new();
		public void Log(LogType type, LogCategory category, string message)
		{
			if (type == LogType.Error)
			{
				lock (Errors) { Errors.Add(message); }
			}
			else if (type == LogType.Warning)
			{
				Interlocked.Increment(ref Warnings);
				string kind = System.Text.RegularExpressions.Regex.Replace(message.Split('\n')[0], @"[0-9a-f]{16,}|-?\d+|'[^']*'", "#");
				kind = kind.Length > 140 ? kind[..140] : kind;
				lock (WarningKinds) { WarningKinds[kind] = WarningKinds.GetValueOrDefault(kind) + 1; }
				if (message.StartsWith("Type tree transcoding failed", StringComparison.Ordinal))
				{
					lock (Errors) { Errors.Insert(0, "[warning] " + message); }
				}
			}
		}
		public void BlankLine(int numLines) { }
	}

	private static int Main(string[] args)
	{
		int maxErrors = 10;
		string? valuesPath = null;
		List<string> paths = new();
		for (int i = 0; i < args.Length; i++)
		{
			if (args[i] == "--errors") { maxErrors = int.Parse(args[++i]); }
			else if (args[i] == "--values") { valuesPath = args[++i]; }
			else { paths.Add(args[i]); }
		}
		CaptureLogger log = new();
		Logger.Add(log);
		System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
		using GameStructure structure = GameStructure.Load(paths, LocalFileSystem.Instance, new CoreConfiguration());
		long ms = sw.ElapsedMilliseconds;
		IUnityObjectBase[] assets = structure.FileCollection.FetchAssets().ToArray();
		Console.WriteLine($"loaded {assets.Length} objects from {structure.FileCollection.FetchAssetCollections().Count()} collections in {ms} ms; failed files {structure.FileCollection.FailedFiles.Count}; errors {log.Errors.Count}; warnings {log.Warnings}");
		foreach (var g in assets.GroupBy(a => a.ClassID).OrderBy(g => g.Key))
		{
			int unreadable = g.Count(a => a is UnreadableObject);
			int unknown = g.Count(a => a is UnknownObject);
			string name = (g.FirstOrDefault(a => a is not UnreadableObject and not UnknownObject) ?? g.First()).ClassName;
			Console.WriteLine($"  class {g.Key,5} {name,-28} {g.Count(),5} objects{(unreadable > 0 ? $", {unreadable} UNREADABLE" : "")}{(unknown > 0 ? $", {unknown} unknown" : "")}");
		}
		Console.WriteLine("transcoded: " + string.Join(", ", TypeTreeTranscoder.TranscodedCount.OrderBy(p => p.Key).Select(p => $"{p.Key}x{p.Value}"))
			+ " | failed: " + string.Join(", ", TypeTreeTranscoder.FailedCount.OrderBy(p => p.Key).Select(p => $"{p.Key}x{p.Value}"))
			+ $" | objects with extras: {TypeTreeTranscoder.Extras.Count}");
		var meshFlags = assets.Where(a => a.ClassID == 43 && a is not UnreadableObject)
			.Select(a => TypeTreeTranscoder.Extras.TryGetValue((a.Collection.Name, a.PathID), out var ex)
				? string.Join("/", new[] { "m_CollisionMeshOnly", "m_CollisionMeshBaked", "m_CollisionMeshConvex" }.Select(k => ex.TryGetValue(k, out object? v) ? v?.ToString() : "-"))
				: "no extras")
			.GroupBy(s => s).Select(g => $"{g.Key} x{g.Count()}");
		Console.WriteLine("readable meshes by CollisionMeshOnly/Baked/Convex: " + string.Join(", ", meshFlags));
		if (valuesPath is not null)
		{
			File.WriteAllLines(valuesPath, assets.Select(Values.Describe).Where(s => s is not null).Order(StringComparer.Ordinal)!);
			Console.WriteLine($"values -> {valuesPath}");
		}
		foreach ((string kind, int n) in log.WarningKinds.OrderByDescending(p => p.Value).Take(8))
		{
			Console.WriteLine($"  warning x{n}: {kind}");
		}
		foreach (string e in log.Errors.Take(maxErrors))
		{
			Console.WriteLine("  error: " + (e.Length > 300 ? e[..300] + "..." : e));
		}
		return assets.Any(a => a is UnreadableObject) ? 1 : 0;
	}
}
