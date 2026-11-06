using AssetRipper.IO.Files.Endfield;
using System.Diagnostics;

namespace AssetRipper.Tools.EndfieldVfs;

// Endfield VFS catalog tool.
//   catalog <game dir> [--no-persistent] [--tsv <out.tsv>]   merged catalog statistics (+ one row per virtual file)
//   find <game dir> <name|substring>...                      resolve virtual files (exact name, else substring)
internal static class Program
{
	private static int Main(string[] args)
	{
		if (args.Length < 2)
		{
			Console.Error.WriteLine("usage: catalog <game dir> [--no-persistent] [--tsv out.tsv] | find <game dir> <name>...");
			return 2;
		}
		EndfieldKeys keys = EndfieldKeys.Load();
		if (args[0] == "blcdump")
		{
			return BlcDump(args.Skip(1).ToArray(), keys);
		}
		bool usePersistent = !args.Contains("--no-persistent");
		Stopwatch sw = Stopwatch.StartNew();
		AssetRipper.IO.Files.Endfield.EndfieldVfs vfs = AssetRipper.IO.Files.Endfield.EndfieldVfs.Open(args[1], keys, usePersistent);
		long ms = sw.ElapsedMilliseconds;
		return args[0] switch
		{
			"catalog" => Catalog(vfs, ms, Option(args, "--tsv")),
			"find" => Find(vfs, args.Skip(2)),
			"extract" => Extract(vfs, keys, Positional(args), Option(args, "--out")),
			"cabindex" => CabIndexCommand(vfs, keys, Option(args, "--index")),
			"deps" => Deps(vfs, keys, Positional(args), Option(args, "--index"), Option(args, "--out"), Option(args, "--list")),
			"dump" => Dump(vfs, keys, Positional(args), Option(args, "--out")),
			_ => 2,
		};
	}

	// virtual names, or AnimeStudio load lists (*.json with Source + Offset), resolved to virtual files
	private static List<BlcVirtualFile> ResolveTargets(AssetRipper.IO.Files.Endfield.EndfieldVfs vfs, string[] inputs)
	{
		Dictionary<(string, long), BlcVirtualFile> byLocation = new();
		foreach (BlcVirtualFile f in vfs.Catalogs.Values.SelectMany(c => c.Files))
		{
			byLocation[(f.Chunk.Md5Name, f.Offset)] = f;
		}
		List<BlcVirtualFile> targets = new();
		foreach (string input in inputs)
		{
			if (input.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
			{
				using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(input));
				foreach (System.Text.Json.JsonElement item in doc.RootElement.EnumerateArray())
				{
					string chunk = Path.GetFileNameWithoutExtension(item.GetProperty("Source").GetString()!).ToUpperInvariant();
					long offset = item.GetProperty("Offset").GetInt64();
					if (byLocation.TryGetValue((chunk, offset), out BlcVirtualFile? f))
					{
						targets.Add(f);
					}
					else
					{
						Console.WriteLine($"{chunk}+{offset}: no virtual file starts here");
					}
				}
			}
			else
			{
				targets.Add(vfs.Find(input) ?? throw new FileNotFoundException($"No virtual file '{input}'."));
			}
		}
		return targets.Distinct().ToList();
	}

	// extract <game dir> <virtual name | load-list.json>... --out dir: raw virtual-file bytes (decrypted if ChaCha-encrypted)
	private static int Extract(AssetRipper.IO.Files.Endfield.EndfieldVfs vfs, EndfieldKeys keys, string[] inputs, string? outDir)
	{
		if (outDir is null)
		{
			Console.Error.WriteLine("extract needs --out <dir>");
			return 2;
		}
		foreach (BlcVirtualFile file in ResolveTargets(vfs, inputs))
		{
			string path = Path.Combine(outDir, file.Name.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			using AssetRipper.IO.Files.Streams.Smart.SmartStream s = AssetRipper.IO.Files.Endfield.EndfieldVfs.OpenStream(file, keys);
			using FileStream o = File.Create(path);
			s.CopyTo(o);
			Console.WriteLine($"{file.Name}	{o.Length}");
		}
		return 0;
	}

	// cabindex <game dir> [--index cache.tsv]: CAB name -> bundle, from bundle directories only
	private static int CabIndexCommand(AssetRipper.IO.Files.Endfield.EndfieldVfs vfs, EndfieldKeys keys, string? cache)
	{
		EndfieldCabIndex index = EndfieldCabIndex.LoadOrBuild(vfs, keys, cache, Console.WriteLine);
		Console.WriteLine($"{index.Cabs.Count} CABs in {index.Cabs.Values.Distinct().Count()} bundles");
		return 0;
	}

	// deps <game dir> <virtual name | load-list.json>... [--index cache.tsv] [--list out.txt] [--out dir]
	// Dependency closure: decode each bundle, follow its serialized files' external references through the CAB index.
	// --list writes the closure's virtual names; --out extracts the raw bundles.
	private static int Deps(AssetRipper.IO.Files.Endfield.EndfieldVfs vfs, EndfieldKeys keys, string[] inputs, string? cache, string? outDir, string? listPath)
	{
		EndfieldCabIndex index = EndfieldCabIndex.LoadOrBuild(vfs, keys, cache, Console.WriteLine);
		Queue<BlcVirtualFile> queue = new(ResolveTargets(vfs, inputs));
		HashSet<BlcVirtualFile> closure = new(queue);
		SortedSet<string> missing = new(StringComparer.Ordinal);
		int builtin = 0;
		while (queue.Count > 0)
		{
			BlcVirtualFile file = queue.Dequeue();
			if (AssetRipper.IO.Files.Endfield.EndfieldVfs.Load(file, keys) is not AssetRipper.IO.Files.FileContainer bundle)
			{
				continue;
			}
			bundle.ReadContents();
			foreach (AssetRipper.IO.Files.SerializedFiles.SerializedFile serialized in bundle.SerializedFiles)
			{
				foreach (AssetRipper.IO.Files.SerializedFiles.Parser.FileIdentifier dependency in serialized.Dependencies)
				{
					string name = Path.GetFileName(dependency.PathNameOrigin ?? dependency.PathName).ToLowerInvariant();
					if (name is "unity default resources" or "unity_builtin_extra" or "unity builtin extra" || name.StartsWith("library/"))
					{
						builtin++;
						continue;
					}
					if (index.Find(name) is { } next)
					{
						if (closure.Add(next))
						{
							queue.Enqueue(next);
						}
					}
					else
					{
						missing.Add(name);
					}
				}
			}
		}
		Console.WriteLine($"closure: {closure.Count} bundles ({inputs.Length} inputs), {missing.Count} unresolved CAB names, {builtin} builtin references");
		foreach (string m in missing.Take(20))
		{
			Console.WriteLine($"  unresolved {m}");
		}
		if (listPath is not null)
		{
			File.WriteAllLines(listPath, closure.Select(f => f.Name).Order(StringComparer.Ordinal));
		}
		if (outDir is not null)
		{
			foreach (BlcVirtualFile file in closure)
			{
				string path = Path.Combine(outDir, file.Name.Replace('/', Path.DirectorySeparatorChar));
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				using AssetRipper.IO.Files.Streams.Smart.SmartStream s = AssetRipper.IO.Files.Endfield.EndfieldVfs.OpenStream(file, keys);
				using FileStream o = File.Create(path);
				s.CopyTo(o);
			}
			Console.WriteLine($"extracted {closure.Count} bundles to {outDir}");
		}
		return 0;
	}

	// dump <game dir> <virtual name | AnimeStudio load-list.json>... [--out dir]
	// Decodes each bundle; per node prints "<chunk>+<offset>  <node>  <size>  <sha256>  <parse result>".
	private static int Dump(AssetRipper.IO.Files.Endfield.EndfieldVfs vfs, EndfieldKeys keys, string[] inputs, string? outDir)
	{
		List<BlcVirtualFile> targets = ResolveTargets(vfs, inputs);
		int failures = 0;
		Stopwatch sw = Stopwatch.StartNew();
		foreach (BlcVirtualFile file in targets.Distinct())
		{
			AssetRipper.IO.Files.FileBase loaded;
			try
			{
				loaded = AssetRipper.IO.Files.Endfield.EndfieldVfs.Load(file, keys);
			}
			catch (Exception ex)
			{
				failures++;
				Console.WriteLine($"{file.Chunk.Md5Name}+{file.Offset}\t{file.Name}\tFAILED {ex.GetType().Name}: {ex.Message}");
				continue;
			}
			if (loaded is not EndfieldBundleFile bundle)
			{
				Console.WriteLine($"{file.Chunk.Md5Name}+{file.Offset}\t{file.Name}\tnot an Endfield bundle ({loaded.GetType().Name})");
				continue;
			}
			foreach (AssetRipper.IO.Files.FailedFile failed in bundle.FailedFiles)
			{
				failures++;
				Console.WriteLine($"{file.Chunk.Md5Name}+{file.Offset}\t{failed.Name}\tFAILED {failed.StackTrace}");
			}
			foreach (AssetRipper.IO.Files.ResourceFiles.ResourceFile node in bundle.ResourceFiles.ToArray())
			{
				byte[] bytes = node.ToByteArray();
				string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
				string parsed;
				try
				{
					AssetRipper.IO.Files.FileBase inner = AssetRipper.IO.Files.SchemeReader.ReadFile(bytes, file.Name, node.Name);
					parsed = inner is AssetRipper.IO.Files.SerializedFiles.SerializedFile sf
						? $"SerializedFile {sf.Version} format {sf.Generation} objects {sf.Objects.Length} types {sf.Types.Length} typetree {sf.HasTypeTree}"
						: inner.GetType().Name;
				}
				catch (Exception ex)
				{
					failures++;
					parsed = $"PARSE FAILED {ex.GetType().Name}: {ex.Message}";
				}
				Console.WriteLine($"{file.Chunk.Md5Name}+{file.Offset}\t{node.Name}\t{bytes.Length}\t{sha}\t{parsed}");
				if (outDir is not null)
				{
					Directory.CreateDirectory(outDir);
					File.WriteAllBytes(Path.Combine(outDir, node.Name), bytes);
				}
			}
		}
		Console.Error.WriteLine($"dumped {targets.Distinct().Count()} bundles in {sw.ElapsedMilliseconds} ms, {failures} failures");
		return failures == 0 ? 0 : 1;
	}

	// blcdump <.blc|dir> [out dir]: version survey; with an out dir, also writes each decrypted body (<group>.v<N>.bin)
	private static int BlcDump(string[] args, EndfieldKeys keys)
	{
		string[] paths = Directory.Exists(args[0]) ? Directory.GetFiles(args[0], "*.blc", SearchOption.AllDirectories) : [args[0]];
		foreach (string path in paths.Order(StringComparer.OrdinalIgnoreCase))
		{
			byte[] data = File.ReadAllBytes(path);
			int outer = BitConverter.ToInt32(data, 0);
			ChaCha20.Xor(data.AsSpan(BlcCatalog.HeaderLength), keys.BlcChaCha20Key, data.AsSpan(0, BlcCatalog.HeaderLength), 1);
			int inner = BitConverter.ToInt32(data, BlcCatalog.HeaderLength);
			Console.WriteLine($"{path}: outer {outer} inner {inner} size {data.Length}");
			if (args.Length > 1)
			{
				Directory.CreateDirectory(args[1]);
				string root = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path))!) ?? "";
				File.WriteAllBytes(Path.Combine(args[1], $"{root}_{Path.GetFileNameWithoutExtension(path)}.v{inner}.bin"), data[BlcCatalog.HeaderLength..]);
			}
		}
		return 0;
	}

	// arguments after the game dir, without "--flag" and the values of --out / --tsv
	private static string[] Positional(string[] args)
	{
		List<string> result = new();
		for (int i = 2; i < args.Length; i++)
		{
			if (args[i] is "--out" or "--tsv" or "--index" or "--list")
			{
				i++;
			}
			else if (!args[i].StartsWith("--"))
			{
				result.Add(args[i]);
			}
		}
		return result.ToArray();
	}

	private static string? Option(string[] args, string name)
	{
		int i = Array.IndexOf(args, name);
		return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
	}

	private static int Catalog(AssetRipper.IO.Files.Endfield.EndfieldVfs vfs, long ms, string? tsv)
	{
		BlcCatalog[] active = vfs.Catalogs.Values.OrderBy(c => c.GroupId, StringComparer.Ordinal).ToArray();
		int fromPersistent = vfs.PersistentRoot is null ? 0 : active.Count(c => c.Path.StartsWith(vfs.PersistentRoot, StringComparison.OrdinalIgnoreCase));
		BlcChunk[] chunks = vfs.Chunks.ToArray();
		int files = active.Sum(c => c.Files.Count());
		Console.WriteLine($"roots: streaming={vfs.StreamingRoot} persistent={vfs.PersistentRoot ?? "(off)"}");
		Console.WriteLine($"opened in {ms} ms: groups={active.Length} (persistent {fromPersistent}, shadowed {vfs.ShadowedCatalogs.Count}) chunks={chunks.Length} files={files} uniqueNames={vfs.Files.Count} multiGroupNames={vfs.Files.Count(p => p.Value.Length > 1)}");
		int declaredMismatch = active.Count(c => c.DeclaredFileCount != c.Files.Count());
		Console.WriteLine($"declared file count mismatches: {declaredMismatch}");
		BlcChunk[] missing = chunks.Where(c => c.PhysicalPath is null).ToArray();
		int chunksInPersistent = vfs.PersistentRoot is null ? 0 : chunks.Count(c => c.PhysicalPath?.StartsWith(vfs.PersistentRoot, StringComparison.OrdinalIgnoreCase) == true);
		Console.WriteLine($"chunks: in persistent {chunksInPersistent}, in streaming {chunks.Length - chunksInPersistent - missing.Length}, not installed {missing.Length} ({missing.Sum(c => c.Files.Count)} files; optional packs such as voice/lip-sync languages)");
		foreach (BlcChunk c in missing.Take(10))
		{
			Console.WriteLine($"  not installed {c.Catalog.GroupId} '{c.Catalog.Name}'/{c.Md5Name}.chk ({c.Files.Count} files, {c.Length} B, e.g. {c.Files[0].Name})");
		}
		// Is a Persistent catalog a full replacement or a delta of the StreamingAssets one?
		foreach ((string id, BlcCatalog old) in vfs.ShadowedCatalogs.OrderBy(p => p.Key, StringComparer.Ordinal))
		{
			BlcCatalog now = vfs.Catalogs[id];
			Dictionary<string, BlcVirtualFile> a = old.Files.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
			Dictionary<string, BlcVirtualFile> b = now.Files.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
			int onlyOld = a.Keys.Count(k => !b.ContainsKey(k));
			int onlyNew = b.Keys.Count(k => !a.ContainsKey(k));
			int changed = a.Count(p => b.TryGetValue(p.Key, out BlcVirtualFile? n) && n.DataMd5 != p.Value.DataMd5);
			Console.WriteLine($"  group {id} '{now.Name}': streaming {a.Count} -> persistent {b.Count}; only-streaming {onlyOld}, only-persistent {onlyNew}, changed {changed}");
		}
		if (tsv is not null)
		{
			using StreamWriter w = new(tsv);
			w.WriteLine("name\tgroup\tchunk\toffset\tlength\tblockType\tencrypted\tdataMd5\tphysical");
			foreach (BlcCatalog c in active)
			{
				foreach (BlcVirtualFile f in c.Files)
				{
					w.WriteLine($"{f.Name}\t{c.GroupId}\t{f.Chunk.Md5Name}\t{f.Offset}\t{f.Length}\t{f.BlockType}\t{(f.Encrypted ? 1 : 0)}\t{f.DataMd5}\t{f.Chunk.PhysicalPath}");
				}
			}
			Console.WriteLine($"tsv -> {tsv}");
		}
		return 0;
	}

	private static int Find(AssetRipper.IO.Files.Endfield.EndfieldVfs vfs, IEnumerable<string> names)
	{
		int notFound = 0;
		foreach (string name in names)
		{
			BlcVirtualFile[] hits = vfs.Files.TryGetValue(AssetRipper.IO.Files.Endfield.EndfieldVfs.NormalizeName(name), out BlcVirtualFile[]? exact)
				? exact
				: vfs.Files.Where(p => p.Key.Contains(name, StringComparison.OrdinalIgnoreCase)).SelectMany(p => p.Value).Take(50).ToArray();
			if (hits.Length == 0)
			{
				notFound++;
				Console.WriteLine($"{name}: not found");
			}
			foreach (BlcVirtualFile f in hits)
			{
				Console.WriteLine($"{name}: {f.Name} | {f.Chunk.Catalog.GroupId} {f.Chunk.PhysicalPath ?? "(missing chunk)"} +{f.Offset} len {f.Length} block {f.BlockType} enc {f.Encrypted}");
			}
		}
		return notFound == 0 ? 0 : 1;
	}
}
