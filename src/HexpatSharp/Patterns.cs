using System.Buffers.Binary;

namespace HexpatSharp;

public enum Endian { Little, Big }

/// <summary>A run of bytes patterns are placed over: the file itself (section 0) or a buffer a pattern made.</summary>
public sealed class Section(int id, string name, byte[] data)
{
    public int Id { get; } = id;
    public string Name { get; } = name;
    public byte[] Data { get; set; } = data;

    public bool InRange(ulong offset, ulong size) => offset <= (ulong)Data.LongLength && size <= (ulong)Data.LongLength - offset;

    public UInt128 ReadUnsigned(ulong offset, int size, Endian endian)
    {
        if (!InRange(offset, (ulong)size)) throw new HexpatException($"reading {size} bytes at 0x{offset:X} is past the end of the data");
        UInt128 value = 0;
        if (endian == Endian.Little)
            for (int i = size - 1; i >= 0; i--) value = (value << 8) | Data[(int)offset + i];
        else
            for (int i = 0; i < size; i++) value = (value << 8) | Data[(int)offset + i];
        return value;
    }
}

public enum ScalarKind { Unsigned, Signed, Float, Bool, Char, Char16 }

public abstract class Pattern
{
    public string Name = "";
    /// <summary>The name shown instead of <see cref="Name"/> (<c>[[name]]</c>), or null.</summary>
    public string? DisplayName;
    /// <summary>The name of the type as written, e.g. "u32", "Header", "RelPtr".</summary>
    public string TypeName = "";
    public ulong Offset;
    public ulong Size;
    public Endian Endian;
    public Section Source = null!;
    public Pattern? Parent;

    public bool Hidden;
    public bool Inline;
    public string? Transform;
    public string? Format;
    public string? Comment;
    public string? Color;
    public List<Attribute> Attributes = [];
    public Dictionary<string, Value> AttributeValues = new(StringComparer.Ordinal);

    public virtual IReadOnlyList<Pattern> Children => [];

    public ulong End => Offset + Size;
}

public sealed class ScalarPattern : Pattern
{
    public ScalarKind Kind;

    public UInt128 ReadRaw() => Source.ReadUnsigned(Offset, (int)Size, Endian);
}

public sealed class PaddingPattern : Pattern;

public sealed class EnumPattern : Pattern
{
    public EnumDef Def = null!;
    public ScalarPattern Underlying = null!;
}

public sealed class EnumDef
{
    public string Name = "";
    public List<(string Name, Int128 Low, Int128 High)> Entries = [];

    public string? NameOf(Int128 value)
    {
        foreach (var (name, low, high) in Entries)
            if (value >= low && value <= high) return name;
        return null;
    }
}

public sealed class StructPattern : Pattern
{
    public bool IsUnion;
    /// <summary>True while the body is still being evaluated, so its size is what has been placed so far.</summary>
    public bool Building;
    public List<Pattern> Members = [];
    /// <summary>Variables made in the body (<c>u32 x = 5;</c>), which can be read as members but are not part of the data.</summary>
    public Dictionary<string, Value> Locals = new(StringComparer.Ordinal);
    private Dictionary<string, Pattern>? _byName;

    public override IReadOnlyList<Pattern> Children => Members;

    public void Add(Pattern member)
    {
        Members.Add(member);
        if (_byName is not null) _byName[member.Name] = member;
    }

    public Pattern? Find(string name)
    {
        if (_byName is null)
        {
            _byName = new Dictionary<string, Pattern>(StringComparer.Ordinal);
            foreach (Pattern m in Members) _byName[m.Name] = m;
        }
        return _byName.GetValueOrDefault(name);
    }
}

public sealed class BitfieldFieldPattern : Pattern
{
    public int BitOffset;
    public int BitSize;
    /// <summary>The field as the type it was declared with (an enum or bool), if it had one.</summary>
    public Pattern? Typed;
    public ScalarKind Kind;
    public bool IsPadding;
    public BitfieldPattern Owner = null!;
    public EnumDef? Enum;

    public UInt128 ReadBits()
    {
        UInt128 raw = Source.ReadUnsigned(Owner.Offset, (int)Owner.Size, Owner.Endian);
        int total = (int)Owner.Size * 8;
        int shift = Owner.MostSignificantFirst ? total - BitOffset - BitSize : BitOffset;
        UInt128 mask = BitSize >= 128 ? UInt128.MaxValue : ((UInt128)1 << BitSize) - 1;
        return (raw >> shift) & mask;
    }
}

public sealed class BitfieldPattern : Pattern
{
    public List<BitfieldFieldPattern> Fields = [];
    public Dictionary<string, BitfieldFieldPattern> ByName = new(StringComparer.Ordinal);
    public bool MostSignificantFirst;

    public override IReadOnlyList<Pattern> Children => Fields;
}

public sealed class ArrayPattern : Pattern
{
    /// <summary>The element type's name, e.g. "u32" or "Entry".</summary>
    public string ElementTypeName = "";
    /// <summary>For arrays of plain numbers or characters: one template, no per-element patterns. For the rest, null.</summary>
    public ScalarKind? ScalarElement;
    public ulong ElementSize;
    public ulong Count;
    public List<Pattern> Elements = [];

    public bool IsScalar => ScalarElement is not null;

    public override IReadOnlyList<Pattern> Children => IsScalar ? [] : Elements;

    public ScalarPattern ScalarAt(ulong index) => new()
    {
        Name = $"[{index}]",
        TypeName = ElementTypeName,
        Kind = ScalarElement!.Value,
        Offset = Offset + index * ElementSize,
        Size = ElementSize,
        Endian = Endian,
        Source = Source,
        Parent = Parent,
    };
}

public sealed class PointerPattern : Pattern
{
    public ScalarPattern Pointer = null!;
    public ulong Address;
    public Pattern? Pointee;

    public override IReadOnlyList<Pattern> Children => Pointee is null ? [] : [Pointee];
}
