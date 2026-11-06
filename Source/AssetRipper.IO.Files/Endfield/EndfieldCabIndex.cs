using AssetRipper.IO.Files.Streams.Smart;
using System.Collections.Concurrent;
using System.Text;

namespace AssetRipper.IO.Files.Endfield;

/// <summary>
/// Serialized-file name (e.g. <c>cab-0feb57dc8b047487b6214c5f99fbecd2</c>, lower case) -> the virtual bundle holding it.
/// Built by reading only bundle directories (no data blocks), one pass per chunk in offset order, chunks in parallel;
/// cached as TSV keyed by the active catalogs' hashes, so it is rebuilt automatically after a game update.
/// </summary>
public sealed class EndfieldCabIndex
{
	public IReadOnlyDictionary<string, BlcVirtualFile> Cabs { get; }
	public int BundlesScanned { get; }
	public int BundlesFailed { get; }

	private EndfieldCabIndex(IReadOnlyDictionary<string, BlcVirtualFile> cabs, int scanned, int failed)
	{
		Cabs = cabs;
		BundlesScanned = scanned;
		BundlesFailed = failed;
	}

	public BlcVirtualFile? Find(string serializedFileName) =>
		Cabs.TryGetValue(serializedFileName.ToLowerInvariant(), out BlcVirtualFile? file) ? file : null;

	/// <summary>Loads <paramref name="cachePath"/> when it matches the current catalogs, otherwise scans and rewrites it.</summary>
	public static EndfieldCabIndex LoadOrBuild(EndfieldVfs vfs, EndfieldKeys keys, string? cachePath, Action<string>? log = null)
	{
		string stamp = Stamp(vfs);
		if (cachePath is not null && File.Exists(cachePath))
		{
			using StreamReader reader = new(cachePath, Encoding.UTF8);
			if (reader.ReadLine() == stamp)
			{
				Dictionary<string, BlcVirtualFile> cabs = new();
				string? line;
				while ((line = reader.ReadLine()) is not null)
				{
					int tab = line.IndexOf('\t');
					if (tab > 0 && vfs.Find(line[(tab + 1)..]) is { } file)
					{
						cabs[line[..tab]] = file;
					}
				}
				log?.Invoke($"cab index: {cabs.Count} CABs from cache {cachePath}");
				return new EndfieldCabIndex(cabs, 0, 0);
			}
		}
		EndfieldCabIndex index = Build(vfs, keys, log);
		if (cachePath is not null)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachePath))!);
			using StreamWriter writer = new(cachePath, false, new UTF8Encoding(false));
			writer.WriteLine(stamp);
			foreach ((string cab, BlcVirtualFile file) in index.Cabs.OrderBy(p => p.Key, StringComparer.Ordinal))
			{
				writer.Write(cab);
				writer.Write('\t');
				writer.WriteLine(file.Name);
			}
		}
		return index;
	}

	public static EndfieldCabIndex Build(EndfieldVfs vfs, EndfieldKeys keys, Action<string>? log = null)
	{
		ConcurrentDictionary<string, BlcVirtualFile> cabs = new();
		int scanned = 0, failed = 0;
		IEnumerable<IGrouping<string, BlcVirtualFile>> chunks = vfs.Catalogs.Values
			.SelectMany(c => c.Files)
			.Where(f => f.Name.EndsWith(".ab", StringComparison.OrdinalIgnoreCase) && !f.Encrypted && f.Chunk.PhysicalPath is not null)
			.GroupBy(f => f.Chunk.PhysicalPath!);
		System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
		Parallel.ForEach(chunks, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) }, chunk =>
		{
			using SmartStream source = SmartStream.OpenRead(chunk.Key, LocalFileSystem.Instance);
			foreach (BlcVirtualFile file in chunk.OrderBy(f => f.Offset))
			{
				try
				{
					using SmartStream bundle = source.CreatePartial(file.Offset, file.Length);
					foreach (EndfieldNode node in EndfieldBundleFile.ReadNodeList(bundle, keys))
					{
						if (!node.Path.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) && !node.Path.EndsWith(".resource", StringComparison.OrdinalIgnoreCase))
						{
							cabs[node.Path.ToLowerInvariant()] = file;
						}
					}
					Interlocked.Increment(ref scanned);
				}
				catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or NotSupportedException or OverflowException)
				{
					Interlocked.Increment(ref failed);
				}
			}
		});
		log?.Invoke($"cab index: scanned {scanned} bundles ({failed} failed) -> {cabs.Count} CABs in {sw.Elapsed.TotalSeconds:F1} s");
		return new EndfieldCabIndex(cabs, scanned, failed);
	}

	private static string Stamp(EndfieldVfs vfs) =>
		"#endfield-cab-index v1 " + string.Join(",", vfs.Catalogs.Values.OrderBy(c => c.GroupId, StringComparer.Ordinal).Select(c => $"{c.GroupId}:{c.Hash}"));
}
