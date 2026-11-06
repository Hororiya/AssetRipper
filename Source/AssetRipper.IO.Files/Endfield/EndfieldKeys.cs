using System.Text.Json;

namespace AssetRipper.IO.Files.Endfield;

/// <summary>
/// Key material for Arknights: Endfield VFS files. Keys are never compiled in: they come from a local JSON file
/// (<c>endfield.keys.json</c>, git-ignored) found via the <c>ENDFIELD_KEYS</c> environment variable, the current
/// directory, or any parent of the application directory.
/// </summary>
public sealed class EndfieldKeys
{
	public const string FileName = "endfield.keys.json";
	public const string EnvironmentVariable = "ENDFIELD_KEYS";

	/// <summary>32-byte ChaCha20 key for .blc catalogs and encrypted virtual files.</summary>
	public required byte[] BlcChaCha20Key { get; init; }

	/// <summary>256-byte substitution box of the VFS bundle cipher (AES-128 structure with a custom S-box).</summary>
	public byte[]? VfsAesSBox { get; init; }
	/// <summary>16-byte key of the VFS bundle cipher.</summary>
	public byte[]? VfsAesKey { get; init; }
	/// <summary>16-byte initial vector of the VFS bundle cipher.</summary>
	public byte[]? VfsAesIV { get; init; }
	/// <summary>64-bit value mixed into each next-IV derivation of the VFS bundle cipher.</summary>
	public ulong VfsAesXorKey { get; init; }

	public bool HasBundleKeys => VfsAesSBox is not null && VfsAesKey is not null && VfsAesIV is not null;

	private static EndfieldKeys? cached;

	public static EndfieldKeys Load() => cached ??= LoadFrom(Locate());

	public static EndfieldKeys LoadFrom(string path)
	{
		using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
		JsonElement root = doc.RootElement;
		return new EndfieldKeys
		{
			BlcChaCha20Key = Hex(root, "BlcChaCha20Key", 32, path)!,
			VfsAesSBox = Hex(root, "VfsAesSBox", 256, path, optional: true),
			VfsAesKey = Hex(root, "VfsAesKey", 16, path, optional: true),
			VfsAesIV = Hex(root, "VfsAesIV", 16, path, optional: true),
			VfsAesXorKey = root.TryGetProperty("VfsAesXorKey", out JsonElement x)
				? ulong.Parse(x.GetString() ?? "0", System.Globalization.NumberStyles.HexNumber)
				: 0,
		};
	}

	private static byte[]? Hex(JsonElement root, string name, int length, string path, bool optional = false)
	{
		if (!root.TryGetProperty(name, out JsonElement value))
		{
			return optional ? null : throw new InvalidDataException($"{name} missing in {path}.");
		}
		byte[] bytes = Convert.FromHexString(value.GetString() ?? "");
		return bytes.Length == length ? bytes : throw new InvalidDataException($"{name} in {path} must be {length} bytes.");
	}

	public static string Locate()
	{
		string? env = Environment.GetEnvironmentVariable(EnvironmentVariable);
		if (!string.IsNullOrEmpty(env))
		{
			return File.Exists(env) ? env : throw new FileNotFoundException($"{EnvironmentVariable} points to a missing file.", env);
		}
		string local = Path.Combine(Directory.GetCurrentDirectory(), FileName);
		if (File.Exists(local))
		{
			return local;
		}
		for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
		{
			string candidate = Path.Combine(dir.FullName, FileName);
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}
		throw new FileNotFoundException($"No {FileName} found (set {EnvironmentVariable}, or place it in the current directory or above the application).");
	}
}
