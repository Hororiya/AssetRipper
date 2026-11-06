using AssetRipper.Assets.Metadata;
using AssetRipper.Import.Structure.Assembly.TypeTrees;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace AssetRipper.Import.AssetCreation;

/// <summary>
/// Rewrites an engine object serialized with a modified type tree (e.g. Arknights: Endfield's custom Unity) into the stock
/// release layout of its Unity version (from the TPK), so the source-generated readers can read it.
/// <list type="bullet">
/// <item>Fields are matched by name, recursively (structs, arrays, maps, strings, typeless data).</item>
/// <item>Numeric fields are converted to the stock width/kind (e.g. UInt8 -> UInt16).</item>
/// <item>Stock fields absent from the file are written as zero / empty.</item>
/// <item>File fields absent from the stock tree are dropped from the layout and kept in <see cref="Extras"/>.</item>
/// </list>
/// Only types whose layout differs from the stock tree are touched; identical ones keep the normal read path.
/// </summary>
public static class TypeTreeTranscoder
{
	/// <summary>Off by default (stock AssetRipper behaviour). Turned on automatically once an Endfield bundle is decoded.</summary>
	public static bool Enabled { get; set; }

	internal static bool IsActive => Enabled || IO.Files.Endfield.EndfieldBundleFile.AnyLoaded;

	/// <summary>Fields only present in the game's type tree, per object: dotted path -> value (long, ulong, double, bool, byte[], object[], Dictionary).</summary>
	public static ConcurrentDictionary<(string Collection, long PathID), IReadOnlyDictionary<string, object?>> Extras { get; } = new();

	/// <summary>Statistics: transcoded objects per class id, and failures per class id.</summary>
	public static ConcurrentDictionary<int, int> TranscodedCount { get; } = new();
	public static ConcurrentDictionary<int, int> FailedCount { get; } = new();

	/// <summary>
	/// Top-level fields the game's custom serializer omits although its type tree declares them: the field is absent when
	/// the already-read top-level <c>Control</c> field is non-zero.
	/// Endfield: a collision-only Mesh (<c>m_CollisionMeshOnly</c>) has no <c>m_CompressedMesh</c> (3 of 3 such meshes in a
	/// 600-bundle sample fit byte-exactly only without it; 45 of 45 other meshes have it).
	/// </summary>
	private static readonly (int ClassID, string Field, string Control)[] ConditionalFields =
	[
		((int)ClassIDType.Mesh, "m_CompressedMesh", "m_CollisionMeshOnly"),
	];

	private sealed class Plan(TypeTreeNodeStruct file, TypeTreeNodeStruct stock)
	{
		public TypeTreeNodeStruct File { get; } = file;
		public TypeTreeNodeStruct Stock { get; } = stock;
	}

	private sealed class NoPlan { }

	private static readonly ConditionalWeakTable<SerializedType, object> plans = new();
	private static readonly object noPlan = new NoPlan();

	/// <summary>
	/// Returns true and the stock-layout bytes when <paramref name="type"/>'s tree differs from the stock tree and the
	/// object was transcoded; false to keep the original bytes.
	/// </summary>
	public static bool TryTranscode(AssetInfo assetInfo, ReadOnlySpan<byte> data, SerializedType? type, UnityVersion version, out byte[] result)
	{
		result = [];
		if (type is null || !IsActive)
		{
			return false;
		}
		object entry = plans.GetValue(type, t => MakePlan(t, assetInfo.ClassID, version) ?? noPlan);
		if (entry is not Plan plan)
		{
			return false;
		}
		bool big = assetInfo.Collection.EndianType == EndianType.BigEndian;
		try
		{
			Reader reader = new(data, big);
			Value value = ReadRoot(plan.File, ref reader, assetInfo.ClassID);
			if (reader.Position != data.Length)
			{
				throw new InvalidDataException($"type tree read {reader.Position} of {data.Length} bytes");
			}
			Writer writer = new(big, data.Length + 64);
			Dictionary<string, object?> extras = new();
			Write(plan.Stock, value, ref writer, "", extras);
			result = writer.ToArray();
			if (extras.Count > 0)
			{
				Extras[(assetInfo.Collection.Name, assetInfo.PathID)] = extras;
			}
			TranscodedCount.AddOrUpdate(assetInfo.ClassID, 1, (_, n) => n + 1);
			return true;
		}
		catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
		{
			FailedCount.AddOrUpdate(assetInfo.ClassID, 1, (_, n) => n + 1);
			DumpFailure(assetInfo, data, plan.File);
			Logging.Logger.Warning(Logging.LogCategory.Import, $"Type tree transcoding failed for {(ClassIDType)assetInfo.ClassID} {assetInfo.PathID} in {assetInfo.Collection.Name}: {ex.Message}");
			return false;
		}
	}

	/// <summary>Debug aid: with ENDFIELD_TRANSCODE_DUMP=&lt;dir&gt;, failing objects are written there with their file type tree.</summary>
	private static void DumpFailure(AssetInfo assetInfo, ReadOnlySpan<byte> data, TypeTreeNodeStruct tree)
	{
		string? dir = Environment.GetEnvironmentVariable("ENDFIELD_TRANSCODE_DUMP");
		if (string.IsNullOrEmpty(dir))
		{
			return;
		}
		Directory.CreateDirectory(dir);
		string stem = System.IO.Path.Combine(dir, $"{assetInfo.Collection.Name}_{assetInfo.PathID}_{assetInfo.ClassID}");
		File.WriteAllBytes(stem + ".bin", data.ToArray());
		List<string> lines = new();
		void Walk(TypeTreeNodeStruct n, int depth)
		{
			lines.Add($"{new string(' ', depth * 2)}{n.TypeName} {n.Name} align={n.AlignBytes}");
			foreach (TypeTreeNodeStruct c in n.SubNodes)
			{
				Walk(c, depth + 1);
			}
		}
		Walk(tree, 0);
		File.WriteAllLines(stem + ".tree.txt", lines);
	}

	private static Plan? MakePlan(SerializedType type, int classID, UnityVersion version)
	{
		if (type.OldType.Nodes.Count == 0 || !TypeTreeNodeStruct.TryMakeFromTypeTree(type.OldType, out TypeTreeNodeStruct file))
		{
			return null;
		}
		if (!TypeTreeNodeStruct.TryMakeFromTpk((ClassIDType)classID, version, out TypeTreeNodeStruct stock, out _))
		{
			return null;
		}
		return SameLayout(file, stock) ? null : new Plan(file, stock);
	}

	/// <summary>Layout equality: type names, field names, alignment and structure (ignores versions and other meta flags).</summary>
	public static bool SameLayout(TypeTreeNodeStruct a, TypeTreeNodeStruct b)
	{
		if (a.TypeName != b.TypeName || a.Name != b.Name || a.AlignBytes != b.AlignBytes || a.SubNodes.Count != b.SubNodes.Count)
		{
			return false;
		}
		for (int i = 0; i < a.SubNodes.Count; i++)
		{
			if (!SameLayout(a.SubNodes[i], b.SubNodes[i]))
			{
				return false;
			}
		}
		return true;
	}

	#region values

	private abstract class Value;

	/// <summary>A primitive, stored as its raw little-endian bit pattern.</summary>
	private sealed class Leaf(string type, ulong bits) : Value
	{
		public string Type { get; } = type;
		public ulong Bits { get; } = bits;
	}

	/// <summary>An array of primitives, kept as raw bytes (fast path for vertex data, index buffers, strings).</summary>
	private sealed class Raw(string elementType, int count, byte[] bytes) : Value
	{
		public string ElementType { get; } = elementType;
		public int Count { get; } = count;
		public byte[] Bytes { get; } = bytes;
	}

	private sealed class Arr(Value[] items) : Value
	{
		public Value[] Items { get; } = items;
	}

	private sealed class Obj(TypeTreeNodeStruct node, Value[] children) : Value
	{
		public TypeTreeNodeStruct Node { get; } = node;
		public Value[] Children { get; } = children;

		public Value? Find(string name)
		{
			for (int i = 0; i < Node.SubNodes.Count; i++)
			{
				if (Node.SubNodes[i].Name == name)
				{
					return Children[i];
				}
			}
			return null;
		}
	}

	private static int PrimitiveSize(string type) => type switch
	{
		"bool" or "char" or "SInt8" or "UInt8" => 1,
		"SInt16" or "UInt16" or "short" or "unsigned short" => 2,
		"SInt32" or "UInt32" or "int" or "unsigned int" or "float" or "Type*" => 4,
		"SInt64" or "UInt64" or "long long" or "unsigned long long" or "double" or "FileSize" => 8,
		_ => throw new InvalidDataException($"unknown primitive type '{type}'"),
	};

	private static bool IsFloat(string type) => type is "float" or "double";

	private static bool IsSigned(string type) => type is "SInt8" or "SInt16" or "short" or "SInt32" or "int" or "SInt64" or "long long";

	#endregion

	#region read

	/// <summary>Reads the root struct, applying <see cref="ConditionalFields"/>; absent fields stay null (written as defaults).</summary>
	private static Value ReadRoot(TypeTreeNodeStruct root, ref Reader r, int classID)
	{
		Value[] children = new Value[root.SubNodes.Count];
		for (int i = 0; i < children.Length; i++)
		{
			TypeTreeNodeStruct child = root.SubNodes[i];
			if (IsOmitted(root, children, i, classID))
			{
				children[i] = null!;
				continue;
			}
			int start = r.Position;
			try
			{
				children[i] = Read(child, ref r);
			}
			catch (InvalidDataException ex)
			{
				throw new InvalidDataException($"{ex.Message} <- {child.TypeName} {child.Name} @{start}", ex.InnerException);
			}
		}
		if (root.AlignBytes)
		{
			r.Align();
		}
		return new Obj(root, children);
	}

	private static bool IsOmitted(TypeTreeNodeStruct root, Value[] read, int index, int classID)
	{
		string name = root.SubNodes[index].Name;
		foreach ((int id, string field, string control) in ConditionalFields)
		{
			if (id != classID || field != name)
			{
				continue;
			}
			for (int j = 0; j < index; j++)
			{
				if (root.SubNodes[j].Name == control && read[j] is Leaf leaf)
				{
					return leaf.Bits != 0;
				}
			}
		}
		return false;
	}

	private static Value Read(TypeTreeNodeStruct node, ref Reader r)
	{
		Value value;
		if (node.SubNodes.Count == 0)
		{
			value = new Leaf(node.TypeName, r.ReadBits(PrimitiveSize(node.TypeName)));
		}
		else if (node.IsArray)
		{
			int count = r.ReadInt32();
			if (count < 0)
			{
				throw new InvalidDataException($"negative array size {count} at {r.Position} ({node.Name})");
			}
			TypeTreeNodeStruct element = node.SubNodes[1];
			if (element.SubNodes.Count == 0)
			{
				int size = PrimitiveSize(element.TypeName);
				value = new Raw(element.TypeName, count, r.ReadBytes(checked(count * size)));
			}
			else
			{
				Value[] items = new Value[count];
				for (int i = 0; i < count; i++)
				{
					items[i] = Read(element, ref r);
				}
				value = new Arr(items);
			}
		}
		else
		{
			Value[] children = new Value[node.SubNodes.Count];
			for (int i = 0; i < children.Length; i++)
			{
				int start = r.Position;
				try
				{
					children[i] = Read(node.SubNodes[i], ref r);
				}
				catch (InvalidDataException ex)
				{
					throw new InvalidDataException($"{ex.Message} <- {node.SubNodes[i].TypeName} {node.SubNodes[i].Name} @{start}", ex.InnerException);
				}
			}
			value = new Obj(node, children);
		}
		if (node.AlignBytes)
		{
			r.Align();
		}
		return value;
	}

	#endregion

	#region write

	private static void Write(TypeTreeNodeStruct node, Value? value, ref Writer w, string path, Dictionary<string, object?> extras)
	{
		if (node.SubNodes.Count == 0)
		{
			w.WriteBits(value is Leaf leaf ? Convert(leaf, node.TypeName) : 0, PrimitiveSize(node.TypeName));
		}
		else if (node.IsArray)
		{
			TypeTreeNodeStruct element = node.SubNodes[1];
			switch (value)
			{
				case Raw raw when element.SubNodes.Count == 0:
					w.WriteInt32(raw.Count);
					if (raw.ElementType == element.TypeName || PrimitiveSize(raw.ElementType) == PrimitiveSize(element.TypeName) && IsFloat(raw.ElementType) == IsFloat(element.TypeName))
					{
						w.WriteBytes(raw.Bytes);
					}
					else
					{
						int fromSize = PrimitiveSize(raw.ElementType), toSize = PrimitiveSize(element.TypeName);
						Reader rr = new(raw.Bytes, w.BigEndian);
						for (int i = 0; i < raw.Count; i++)
						{
							w.WriteBits(Convert(new Leaf(raw.ElementType, rr.ReadBits(fromSize)), element.TypeName), toSize);
						}
					}
					break;
				case Arr arr when element.SubNodes.Count > 0:
					w.WriteInt32(arr.Items.Length);
					for (int i = 0; i < arr.Items.Length; i++)
					{
						Write(element, arr.Items[i], ref w, $"{path}[{i}]", extras);
					}
					break;
				default:
					if (value is not null)
					{
						extras[path] = ToClr(value);
					}
					w.WriteInt32(0);
					break;
			}
		}
		else
		{
			Obj? obj = value as Obj;
			if (value is not null && obj is null)
			{
				extras[path] = ToClr(value);
			}
			for (int i = 0; i < node.SubNodes.Count; i++)
			{
				TypeTreeNodeStruct child = node.SubNodes[i];
				Write(child, obj?.Find(child.Name), ref w, path.Length == 0 ? child.Name : $"{path}.{child.Name}", extras);
			}
			if (obj is not null)
			{
				for (int i = 0; i < obj.Node.SubNodes.Count; i++)
				{
					string name = obj.Node.SubNodes[i].Name;
					if (!HasChild(node, name))
					{
						extras[path.Length == 0 ? name : $"{path}.{name}"] = ToClr(obj.Children[i]);
					}
				}
			}
		}
		if (node.AlignBytes)
		{
			w.Align();
		}
	}

	private static bool HasChild(TypeTreeNodeStruct node, string name)
	{
		foreach (TypeTreeNodeStruct child in node.SubNodes)
		{
			if (child.Name == name)
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>Converts a primitive to the bit pattern of <paramref name="toType"/>.</summary>
	private static ulong Convert(Leaf leaf, string toType)
	{
		if (leaf.Type == toType)
		{
			return leaf.Bits;
		}
		int fromSize = PrimitiveSize(leaf.Type);
		unchecked
		{
			if (IsFloat(leaf.Type) || IsFloat(toType))
			{
				double d = leaf.Type switch
				{
					"float" => BitConverter.Int32BitsToSingle((int)leaf.Bits),
					"double" => BitConverter.Int64BitsToDouble((long)leaf.Bits),
					_ => IsSigned(leaf.Type) ? SignExtend(leaf.Bits, fromSize) : (double)leaf.Bits,
				};
				return toType switch
				{
					"float" => (uint)BitConverter.SingleToInt32Bits((float)d),
					"double" => (ulong)BitConverter.DoubleToInt64Bits(d),
					_ => (ulong)(long)d,
				};
			}
			ulong v = IsSigned(leaf.Type) ? (ulong)SignExtend(leaf.Bits, fromSize) : leaf.Bits;
			int toSize = PrimitiveSize(toType);
			return toSize == 8 ? v : v & ((1UL << (toSize * 8)) - 1);
		}
	}

	private static long SignExtend(ulong bits, int size)
	{
		int shift = 64 - size * 8;
		return unchecked((long)(bits << shift) >> shift);
	}

	private static object? ToClr(Value value) => value switch
	{
		Leaf l when IsFloat(l.Type) => l.Type == "float" ? BitConverter.Int32BitsToSingle(unchecked((int)l.Bits)) : BitConverter.Int64BitsToDouble(unchecked((long)l.Bits)),
		Leaf l when l.Type == "bool" => l.Bits != 0,
		Leaf l when IsSigned(l.Type) => SignExtend(l.Bits, PrimitiveSize(l.Type)),
		Leaf l => l.Bits,
		Raw r => r.Bytes,
		Arr a => a.Items.Select(ToClr).ToArray(),
		Obj o => Enumerable.Range(0, o.Children.Length).ToDictionary(i => o.Node.SubNodes[i].Name, i => ToClr(o.Children[i])),
		_ => null,
	};

	#endregion

	#region io

	private ref struct Reader(ReadOnlySpan<byte> data, bool bigEndian)
	{
		private readonly ReadOnlySpan<byte> data = data;
		public int Position;

		private ReadOnlySpan<byte> Take(int n)
		{
			if (n < 0 || Position + n > data.Length)
			{
				throw new InvalidDataException($"read past end ({Position}+{n} of {data.Length})");
			}
			ReadOnlySpan<byte> s = data.Slice(Position, n);
			Position += n;
			return s;
		}

		public ulong ReadBits(int size)
		{
			ReadOnlySpan<byte> s = Take(size);
			return size switch
			{
				1 => s[0],
				2 => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s),
				4 => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s),
				_ => bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(s) : BinaryPrimitives.ReadUInt64LittleEndian(s),
			};
		}

		public int ReadInt32() => unchecked((int)ReadBits(4));

		public byte[] ReadBytes(int n) => Take(n).ToArray();

		public void Align()
		{
			int pad = (4 - (Position & 3)) & 3;
			Take(pad);
		}
	}

	private struct Writer(bool bigEndian, int capacity)
	{
		private byte[] buffer = new byte[Math.Max(capacity, 16)];
		private int length;
		public readonly bool BigEndian => bigEndian;

		private Span<byte> Grow(int n)
		{
			if (length + n > buffer.Length)
			{
				Array.Resize(ref buffer, Math.Max(buffer.Length * 2, length + n));
			}
			Span<byte> s = buffer.AsSpan(length, n);
			length += n;
			return s;
		}

		public void WriteBits(ulong bits, int size)
		{
			Span<byte> s = Grow(size);
			unchecked
			{
				switch (size)
				{
					case 1: s[0] = (byte)bits; break;
					case 2: if (bigEndian) { BinaryPrimitives.WriteUInt16BigEndian(s, (ushort)bits); } else { BinaryPrimitives.WriteUInt16LittleEndian(s, (ushort)bits); } break;
					case 4: if (bigEndian) { BinaryPrimitives.WriteUInt32BigEndian(s, (uint)bits); } else { BinaryPrimitives.WriteUInt32LittleEndian(s, (uint)bits); } break;
					default: if (bigEndian) { BinaryPrimitives.WriteUInt64BigEndian(s, bits); } else { BinaryPrimitives.WriteUInt64LittleEndian(s, bits); } break;
				}
			}
		}

		public void WriteInt32(int value) => WriteBits(unchecked((uint)value), 4);

		public void WriteBytes(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Grow(bytes.Length));

		public void Align() => Grow((4 - (length & 3)) & 3).Clear();

		public readonly byte[] ToArray() => buffer.AsSpan(0, length).ToArray();
	}

	#endregion
}
