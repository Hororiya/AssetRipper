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

	private static EndfieldKeys? cached;

	public static EndfieldKeys Load() => cached ??= LoadFrom(Locate());

	public static EndfieldKeys LoadFrom(string path)
	{
		using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
		byte[] key = Convert.FromHexString(doc.RootElement.GetProperty("BlcChaCha20Key").GetString() ?? "");
		if (key.Length != 32)
		{
			throw new InvalidDataException($"BlcChaCha20Key in {path} must be 32 bytes.");
		}
		return new EndfieldKeys { BlcChaCha20Key = key };
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
