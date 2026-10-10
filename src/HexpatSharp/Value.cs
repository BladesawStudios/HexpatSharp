namespace HexpatSharp;

public enum ValueKind { Void, Integer, Float, Boolean, Char, String, Pattern, Section, List }

/// <summary>A value of the pattern language: numbers (128-bit), floats, booleans, characters, strings, patterns already placed, and so on.</summary>
public sealed class Value
{
    public static readonly Value Void = new(ValueKind.Void);

    public ValueKind Kind { get; }
    public Int128 Integer { get; }
    public bool Unsigned { get; }
    public double Float { get; }
    public string Text { get; } = "";
    public Pattern? Pattern { get; }
    public int SectionId { get; }
    public List<Value>? Items { get; }

    private Value(ValueKind kind) => Kind = kind;

    private Value(double value) : this(ValueKind.Float) => Float = value;

    private Value(ValueKind kind, Int128 integer, bool unsigned) : this(kind)
    {
        Integer = integer;
        Unsigned = unsigned;
    }

    public static Value FromInt(Int128 value, bool unsigned = false) => new(ValueKind.Integer, value, unsigned);

    public static Value FromUnsigned(UInt128 value) => new(ValueKind.Integer, (Int128)value, true);

    public static Value FromBool(bool value) => new(ValueKind.Boolean, value ? 1 : 0, true);

    public static Value FromChar(int code) => new(ValueKind.Char, code, true);

    public static Value FromFloat(double value) => new(value);

    public static Value FromString(string text) => new(ValueKind.String, text);

    public static Value FromPattern(Pattern pattern) => new(ValueKind.Pattern, pattern);

    public static Value FromSection(int id) => new(ValueKind.Section, id);

    public static Value FromList(List<Value> items) => new(ValueKind.List, items);

    private Value(ValueKind kind, string text) : this(kind) => Text = text;

    private Value(ValueKind kind, Pattern pattern) : this(kind) => Pattern = pattern;

    private Value(ValueKind kind, int sectionId) : this(kind) => SectionId = sectionId;

    private Value(ValueKind kind, List<Value> items) : this(kind) => Items = items;

    public bool IsNumeric => Kind is ValueKind.Integer or ValueKind.Float or ValueKind.Boolean or ValueKind.Char;

    public override string ToString() => Kind switch
    {
        ValueKind.Integer => Unsigned ? ((UInt128)Integer).ToString() : Integer.ToString(),
        ValueKind.Boolean => Integer != 0 ? "true" : "false",
        ValueKind.Char => ((char)(int)Integer).ToString(),
        ValueKind.Float => Float.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        ValueKind.String => Text,
        ValueKind.Pattern => Pattern?.TypeName ?? "pattern",
        ValueKind.Section => $"section {SectionId}",
        ValueKind.List => "[" + string.Join(", ", Items!.Select(v => v.ToString())) + "]",
        _ => "void",
    };
}
