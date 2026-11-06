namespace AssetRipper.IO.Files.Endfield;

/// <summary>
/// The merged Endfield virtual file system: <c>Persistent/VFS</c> (hot-update) over <c>StreamingAssets/VFS</c> (install).
/// A group (folder named like its .blc) present in Persistent uses the Persistent catalog; each of its chunks is resolved
/// to Persistent first, then StreamingAssets (a hot-update catalog can reference chunks that only the install ships).
/// </summary>
public sealed class EndfieldVfs
{
	public required string StreamingRoot { get; init; }
	public string? PersistentRoot { get; init; }
	/// <summary>Active catalog per group id.</summary>
	public required IReadOnlyDictionary<string, BlcCatalog> Catalogs { get; init; }
	/// <summary>StreamingAssets catalogs that a Persistent catalog replaced.</summary>
	public required IReadOnlyDictionary<string, BlcCatalog> ShadowedCatalogs { get; init; }
	/// <summary>Virtual files by normalized name (lower-case, '/'). A name listed by several groups keeps every entry.</summary>
	public required IReadOnlyDictionary<string, BlcVirtualFile[]> Files { get; init; }

	public IEnumerable<BlcChunk> Chunks => Catalogs.Values.SelectMany(c => c.Chunks);

	public static string NormalizeName(string name) => name.Replace('\\', '/').ToLowerInvariant();

	/// <summary>Accepts the game folder, <c>Endfield_Data</c>, <c>StreamingAssets</c> or a <c>VFS</c> folder.</summary>
	public static EndfieldVfs Open(string path, EndfieldKeys keys, bool usePersistent = true)
	{
		(string streaming, string? persistent) = FindRoots(path);
		if (!usePersistent)
		{
			persistent = null;
		}
		Dictionary<string, BlcCatalog> sa = LoadCatalogs(streaming, keys);
		Dictionary<string, BlcCatalog> pe = persistent is null ? new() : LoadCatalogs(persistent, keys);

		Dictionary<string, BlcCatalog> active = new(sa, StringComparer.OrdinalIgnoreCase);
		Dictionary<string, BlcCatalog> shadowed = new(StringComparer.OrdinalIgnoreCase);
		foreach ((string id, BlcCatalog catalog) in pe)
		{
			if (active.Remove(id, out BlcCatalog? old))
			{
				shadowed[id] = old;
			}
			active[id] = catalog;
		}
		foreach (BlcCatalog catalog in active.Values)
		{
			foreach (BlcChunk chunk in catalog.Chunks)
			{
				chunk.PhysicalPath = ResolveChunk(chunk, persistent, streaming);
			}
		}
		Dictionary<string, List<BlcVirtualFile>> files = new();
		foreach (BlcCatalog catalog in active.Values.OrderBy(c => c.GroupId, StringComparer.Ordinal))
		{
			foreach (BlcVirtualFile file in catalog.Files)
			{
				string key = NormalizeName(file.Name);
				if (!files.TryGetValue(key, out List<BlcVirtualFile>? list))
				{
					files[key] = list = new(1);
				}
				list.Add(file);
			}
		}
		return new EndfieldVfs
		{
			StreamingRoot = streaming,
			PersistentRoot = persistent,
			Catalogs = active,
			ShadowedCatalogs = shadowed,
			Files = files.ToDictionary(p => p.Key, p => p.Value.ToArray()),
		};
	}

	public BlcVirtualFile? Find(string name)
	{
		return Files.TryGetValue(NormalizeName(name), out BlcVirtualFile[]? found) ? found[^1] : null;
	}

	private static string? ResolveChunk(BlcChunk chunk, string? persistent, string streaming)
	{
		string file = chunk.Md5Name + ".chk";
		foreach (string? root in (ReadOnlySpan<string?>)[persistent, streaming])
		{
			if (root is null)
			{
				continue;
			}
			string candidate = System.IO.Path.Combine(root, chunk.Catalog.GroupId, file);
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}
		return null;
	}

	private static Dictionary<string, BlcCatalog> LoadCatalogs(string vfsRoot, EndfieldKeys keys)
	{
		string[] paths = Directory.GetFiles(vfsRoot, "*.blc", SearchOption.AllDirectories);
		BlcCatalog[] catalogs = new BlcCatalog[paths.Length];
		Parallel.For(0, paths.Length, i => catalogs[i] = BlcCatalog.Read(paths[i], keys));
		Dictionary<string, BlcCatalog> result = new(StringComparer.OrdinalIgnoreCase);
		foreach (BlcCatalog catalog in catalogs)
		{
			if (!result.TryAdd(catalog.GroupId, catalog))
			{
				throw new InvalidDataException($"Two catalogs for group {catalog.GroupId} under {vfsRoot}");
			}
		}
		return result;
	}

	private static (string Streaming, string? Persistent) FindRoots(string path)
	{
		string full = System.IO.Path.GetFullPath(path).TrimEnd('\\', '/');
		string? data = null;
		for (DirectoryInfo? d = new(full); d is not null && data is null; d = d.Parent)
		{
			if (Directory.Exists(System.IO.Path.Combine(d.FullName, "StreamingAssets", "VFS")))
			{
				data = d.FullName;
			}
			else
			{
				string nested = System.IO.Path.Combine(d.FullName, "Endfield_Data");
				if (Directory.Exists(System.IO.Path.Combine(nested, "StreamingAssets", "VFS")))
				{
					data = nested;
				}
			}
		}
		if (data is null)
		{
			throw new DirectoryNotFoundException($"No Endfield_Data/StreamingAssets/VFS at or above {path}");
		}
		string persistent = System.IO.Path.Combine(data, "Persistent", "VFS");
		return (System.IO.Path.Combine(data, "StreamingAssets", "VFS"), Directory.Exists(persistent) ? persistent : null);
	}
}
